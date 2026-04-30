using System.Diagnostics;
using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Generates a strategic-merge two-way patch from <c>original</c> to <c>modified</c>.
/// Stage 3 covers objects, primitives, and the RFC 7396 fallback. Lists are atomically
/// replaced; Stage 4 swaps in the directive-aware list diff.
/// </summary>
internal static class TwoWayMerge
{
    /// <summary>
    /// Computes a patch <c>P</c> such that <c>Apply(original, P) == modified</c>. Returns
    /// <c>null</c> when there is no diff. Throws <see cref="StrategicMergePatchException"/>
    /// when root <c>apiVersion</c>/<c>kind</c> disagree between the two documents.
    /// </summary>
    public static JsonObject? CreateTwoWayMergePatch(
        JsonObject? original,
        JsonObject? modified,
        StrategicPatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= StrategicPatchOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRootIdentity(original, modified);

        var gvk = ResolveGvk(modified) ?? ResolveGvk(original);
        SchemaNode? rootSchema = null;
        if (options.SchemaProvider is not null && gvk is not null)
        {
            rootSchema = options.SchemaProvider.GetRootSchema(gvk.Value);
        }

        using var activity = StrategicPatchActivity.Source.StartActivity(
            "smp.compute_two_way", ActivityKind.Internal);
        if (gvk is not null)
        {
            activity?.SetTag("smp.gvk", gvk.Value.ToString());
        }
        TagSchemaSource(activity, options.SchemaProvider);
        SchemaMissTracking.Initialise(activity);

        var patch = DiffObject(original, modified, rootSchema, JsonPointer.Root, options, activity, DiffFlags.Default, depth: 0, cancellationToken);

        // Optimistic concurrency only matters when there's an actual change to apply; injecting it
        // on a no-op patch produces a non-empty body that defeats IsEmpty-based ghost-call skipping.
        if (options.EnforceOptimisticConcurrency && patch.Count > 0)
        {
            InjectOptimisticConcurrency(patch, original);
        }

        if (patch.Count == 0)
        {
            activity?.SetTag("smp.empty", true);
            activity?.SetTag("smp.patch.bytes", 2);
            options.Logger?.LogInformation(
                "smp.compute_two_way gvk={Gvk} empty=true bytes=2 schema_miss={Misses}",
                gvk?.ToString() ?? "<unknown>", activity?.GetTagItem(SchemaMissTracking.CountTag) ?? 0);
            return null;
        }

        activity?.SetTag("smp.empty", false);
        var bytes = patch.ToJsonString().Length;
        activity?.SetTag("smp.patch.bytes", bytes);
        options.Logger?.LogInformation(
            "smp.compute_two_way gvk={Gvk} empty=false bytes={Bytes} schema_miss={Misses}",
            gvk?.ToString() ?? "<unknown>", bytes,
            activity?.GetTagItem(SchemaMissTracking.CountTag) ?? 0);
        return patch;
    }

    /// <summary>
    /// Recurses a pair of objects. Strategy is governed by <paramref name="schema"/>; when null,
    /// behaviour collapses to RFC 7396 (JSON Merge Patch) for the subtree.
    /// </summary>
    /// <remarks>Internal entry point for <see cref="ListDiff"/>'s per-element recursion.</remarks>
    internal static JsonObject DiffObjectInternal(
        JsonObject? original,
        JsonObject? modified,
        SchemaNode? schema,
        JsonPointer path,
        StrategicPatchOptions options,
        Activity? activity,
        DiffFlags flags = default,
        int depth = 0,
        CancellationToken cancellationToken = default)
        => DiffObject(original, modified, schema, path, options, activity, flags, depth, cancellationToken);

    /// <summary>
    /// Three-way merge entry point for the deltaMap (current → modified, IgnoreDeletions) and the
    /// deletionsMap (original → modified, IgnoreChangesAndAdditions) sub-diffs.
    /// </summary>
    internal static JsonObject ComputeDiffWithFlags(
        JsonObject? original,
        JsonObject? modified,
        SchemaNode? schema,
        StrategicPatchOptions options,
        DiffFlags flags,
        Activity? activity = null,
        CancellationToken cancellationToken = default)
    {
        return DiffObject(original, modified, schema, JsonPointer.Root, options, activity, flags, depth: 0, cancellationToken);
    }

    private static JsonObject DiffObject(
        JsonObject? original,
        JsonObject? modified,
        SchemaNode? schema,
        JsonPointer path,
        StrategicPatchOptions options,
        Activity? activity,
        DiffFlags flags = default,
        int depth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > options.MaxDepth)
        {
            throw new StrategicMergePatchException(
                $"Recursion depth {depth} exceeded MaxDepth ({options.MaxDepth}).", path);
        }
        var patch = new JsonObject();
        original ??= new JsonObject();
        modified ??= new JsonObject();

        if (schema is null)
        {
            Internal.SchemaMissTracking.RecordMiss(activity, path);
        }

        // Track keys that survived in modified — used for $retainKeys when the schema asks for it.
        var emitRetainKeys = schema?.Strategy.HasFlag(PatchStrategy.RetainKeys) ?? false;
        var retainKeys = emitRetainKeys ? new SortedSet<string>(StringComparer.Ordinal) : null;

        // Pass 1: properties present in modified. We always walk this loop, even under
        // IgnoreChangesAndAdditions: nested deletions can hide deep inside a key that survives at
        // the parent level. Only the *leaf emit* is gated by the flag.
        foreach (var (key, modifiedValue) in modified)
        {
            if (retainKeys is not null && modifiedValue is not null)
            {
                retainKeys.Add(key);
            }

            var childPath = path.Append(key);
            var childSchema = schema?.Properties.TryGetValue(key, out var childNode) == true ? childNode : null;
            var originalValue = original.TryGetPropertyValue(key, out var ov) ? ov : null;

            if (modifiedValue is null)
            {
                if (!flags.IgnoreDeletions)
                {
                    HandleNullInModified(key, originalValue, patch, options);
                }
                continue;
            }

            if (originalValue is null)
            {
                // Key added — copy through (gated by IgnoreChangesAndAdditions).
                if (!flags.IgnoreChangesAndAdditions)
                {
                    patch[key] = JsonNodeCloning.CloneOrNull(modifiedValue);
                    options.Logger?.LogDebug("smp.add path={Path}", childPath);
                }
                continue;
            }

            DiffExisting(key, originalValue, modifiedValue, childSchema, path, patch, options, activity, flags, depth, cancellationToken);
        }

        // Pass 2: deletions — keys present in original but absent in modified.
        var hadDeletes = false;
        if (!options.IgnoreNullValuesInModified && !flags.IgnoreDeletions)
        {
            foreach (var (key, originalValue) in original)
            {
                if (!modified.ContainsKey(key))
                {
                    if (originalValue is null)
                    {
                        // Already null on both sides — no diff, no delete needed.
                        continue;
                    }
                    patch[key] = null;
                    hadDeletes = true;
                    options.Logger?.LogDebug("smp.delete path={Path}", path.Append(key));
                }
            }
        }

        // $retainKeys is emitted only if the patch is non-empty OR the original had additional keys
        // not in modified (i.e. the applier has cleanup to do). Mirrors Go's diffMaps tail.
        if (retainKeys is not null && retainKeys.Count > 0 && (patch.Count > 0 || hadDeletes))
        {
            var arr = new JsonArray();
            foreach (var k in retainKeys)
            {
                arr.Add(k);
            }
            patch[Directives.RetainKeys] = arr;
        }

        return patch;
    }

    /// <summary>
    /// Handles the <c>"key": null</c> case in modified. Under default options this is a delete
    /// (drop the key), recorded as <c>{key: null}</c> in the patch. Under
    /// <see cref="StrategicPatchOptions.IgnoreNullValuesInModified"/> it is suppressed entirely.
    /// </summary>
    private static void HandleNullInModified(
        string key,
        JsonNode? originalValue,
        JsonObject patch,
        StrategicPatchOptions options)
    {
        if (options.IgnoreNullValuesInModified)
        {
            return;
        }
        // If the key wasn't in original, an explicit null in modified still counts as a desired
        // null value on the server side — emit it. (Matches Go: replacePatchFieldIfNotEqual treats
        // nil != non-existent.)
        if (originalValue is null)
        {
            // Both sides null/absent — nothing to do.
            return;
        }
        patch[key] = null;
    }

    /// <summary>
    /// Both keys present on both sides; dispatch on type alignment.
    /// </summary>
    private static void DiffExisting(
        string key,
        JsonNode originalValue,
        JsonNode modifiedValue,
        SchemaNode? childSchema,
        JsonPointer parentPath,
        JsonObject patch,
        StrategicPatchOptions options,
        Activity? activity,
        DiffFlags flags,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Type mismatch (object vs primitive, etc.) → wholesale replace.
        if (originalValue.GetValueKind() != modifiedValue.GetValueKind())
        {
            // A type mismatch reads as both an addition and a deletion; honour the flags.
            if (!flags.IgnoreChangesAndAdditions)
            {
                patch[key] = JsonNodeCloning.CloneOrNull(modifiedValue);
            }
            return;
        }

        switch (originalValue, modifiedValue)
        {
            case (JsonObject originalObj, JsonObject modifiedObj):
                {
                    var subPatch = DiffObject(originalObj, modifiedObj, childSchema, parentPath.Append(key), options, activity, flags, depth + 1, cancellationToken);
                    if (subPatch.Count > 0)
                    {
                        patch[key] = subPatch;
                    }
                    return;
                }

            case (JsonArray originalArr, JsonArray modifiedArr):
                {
                    ListDiff.Diff(key, originalArr, modifiedArr, childSchema, patch, parentPath, options, activity, flags, depth + 1, cancellationToken);
                    return;
                }

            default:
                {
                    if (!JsonNodeEquality.DeepEquals(originalValue, modifiedValue) && !flags.IgnoreChangesAndAdditions)
                    {
                        patch[key] = JsonNodeCloning.CloneOrNull(modifiedValue);
                    }
                    return;
                }
        }
    }

    /// <summary>
    /// Tags the activity with the provider's <see cref="ISchemaProvider.Name"/>, plus
    /// generator-emitted manifest fields when the provider implements
    /// <see cref="IManifestedSchemaProvider"/>. Lets traces correlate to the specific
    /// generator output that served the request.
    /// </summary>
    internal static void TagSchemaSource(Activity? activity, ISchemaProvider? provider)
    {
        if (activity is null || provider is null)
        {
            return;
        }
        activity.SetTag("smp.schema_source", provider.Name);
        if (provider is IManifestedSchemaProvider manifested)
        {
            var m = manifested.Manifest;
            activity.SetTag("smp.generator.manifest_hash", m.SnapshotContentHash);
            activity.SetTag("smp.generator.gvk_count", m.GvkCount);
        }
    }

    private static void ValidateRootIdentity(JsonObject? original, JsonObject? modified)
    {
        if (original is null || modified is null)
        {
            return;
        }
        AssertSameField(original, modified, "apiVersion");
        AssertSameField(original, modified, "kind");
    }

    private static void AssertSameField(JsonObject original, JsonObject modified, string field)
    {
        var l = original.TryGetPropertyValue(field, out var lv) ? lv?.GetValue<string>() : null;
        var r = modified.TryGetPropertyValue(field, out var rv) ? rv?.GetValue<string>() : null;
        if (l is null || r is null)
        {
            return; // Sparse on one side — allow.
        }
        if (!string.Equals(l, r, StringComparison.Ordinal))
        {
            throw new StrategicMergePatchException(
                $"Root '{field}' mismatch: original='{l}' vs modified='{r}'.", JsonPointer.Root)
            {
                Kind = field == "kind" ? l : null,
            };
        }
    }

    private static GroupVersionKind? ResolveGvk(JsonObject? doc)
    {
        if (doc is null)
        {
            return null;
        }
        var apiVersion = doc.TryGetPropertyValue("apiVersion", out var av) ? av?.GetValue<string>() : null;
        var kind = doc.TryGetPropertyValue("kind", out var kv) ? kv?.GetValue<string>() : null;
        if (apiVersion is null || kind is null)
        {
            return null;
        }
        return GroupVersionKind.Parse(apiVersion, kind);
    }

    /// <summary>
    /// Copies <c>metadata.uid</c> and <c>metadata.resourceVersion</c> from <paramref name="original"/>
    /// into the patch root. Server-side this turns the patch into an optimistic-concurrency check
    /// that fails with HTTP 409 if the resource has drifted.
    /// </summary>
    private static void InjectOptimisticConcurrency(JsonObject patch, JsonObject? original)
    {
        if (original?["metadata"] is not JsonObject originalMeta)
        {
            return;
        }
        var meta = patch["metadata"] as JsonObject;
        if (meta is null)
        {
            meta = new JsonObject();
            patch["metadata"] = meta;
        }
        CopyIfPresent(originalMeta, meta, "uid");
        CopyIfPresent(originalMeta, meta, "resourceVersion");
    }

    private static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
    {
        if (!src.TryGetPropertyValue(key, out var value) || value is null)
        {
            return;
        }
        // Always overwrite, even if the diff produced a delete marker (null) for this key —
        // optimistic concurrency is a precondition, not a normal diff.
        dst[key] = JsonNodeCloning.CloneOrNull(value);
    }
}
