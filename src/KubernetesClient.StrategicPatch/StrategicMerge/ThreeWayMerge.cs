using System.Diagnostics;
using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Strategic-merge three-way patch generator. Produces a patch that:
/// <list type="bullet">
///   <item>preserves additions the server made between <c>original</c> (last-applied) and <c>current</c> (live);</item>
///   <item>applies caller-side additions and changes from <c>modified</c>;</item>
///   <item>carries forward deletions the caller made between <c>original</c> and <c>modified</c>.</item>
/// </list>
/// Mirrors Go's <c>strategicpatch.CreateThreeWayMergePatch</c>: composes a delta (current→modified
/// without deletions) and a deletions map (original→modified, deletions only), unions them, and
/// detects conflicts against a changed-map (original→current) unless
/// <see cref="StrategicPatchOptions.OverwriteConflicts"/> is set.
/// </summary>
internal static class ThreeWayMerge
{
    /// <summary>
    /// Computes a three-way merge patch. Returns <c>null</c> when there's no diff to apply.
    /// </summary>
    /// <exception cref="StrategicMergePatchConflictException">
    /// Thrown when caller-side and server-side changes disagree and
    /// <see cref="StrategicPatchOptions.OverwriteConflicts"/> is <c>false</c>.
    /// </exception>
    public static JsonObject? CreateThreeWayMergePatch(
        JsonObject? original,
        JsonObject? modified,
        JsonObject? current,
        StrategicPatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= StrategicPatchOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRootIdentity(original, modified, current);

        var gvk = ResolveGvk(modified) ?? ResolveGvk(current) ?? ResolveGvk(original);
        SchemaNode? rootSchema = null;
        if (options.SchemaProvider is not null && gvk is not null)
        {
            rootSchema = options.SchemaProvider.GetRootSchema(gvk.Value);
        }

        using var activity = StrategicPatchActivity.Source.StartActivity(
            "smp.compute_three_way", ActivityKind.Internal);
        if (gvk is not null)
        {
            activity?.SetTag("smp.gvk", gvk.Value.ToString());
        }
        TwoWayMerge.TagSchemaSource(activity, options.SchemaProvider);
        SchemaMissTracking.Initialise(activity);

        // delta: changes/additions between current and modified, NO deletions.
        var delta = TwoWayMerge.ComputeDiffWithFlags(
            current, modified, rootSchema, options, DiffFlags.DeltaOnly, activity, cancellationToken);

        // deletions: ONLY deletions between original and modified.
        var deletions = TwoWayMerge.ComputeDiffWithFlags(
            original, modified, rootSchema, options, DiffFlags.DeletionsOnly, activity, cancellationToken);

        var patch = PatchMerger.Merge(deletions, delta);

        if (!options.OverwriteConflicts)
        {
            // changed: what the server (current) drifted to since original was last applied.
            var changed = TwoWayMerge.ComputeDiffWithFlags(
                original, current, rootSchema, options, DiffFlags.Default, activity, cancellationToken);
            var conflicts = ConflictDetector.FindConflicts(patch, changed);
            if (conflicts.Count > 0)
            {
                activity?.SetTag("smp.conflicts", conflicts.Count);
                throw new StrategicMergePatchConflictException(
                    $"Three-way merge has {conflicts.Count} conflicting field(s); "
                        + "set OverwriteConflicts=true to force caller-side values.",
                    conflicts);
            }
        }

        // Optimistic concurrency only matters when there's an actual change to apply; injecting it
        // on a no-op patch produces a non-empty body that defeats IsEmpty-based ghost-call skipping.
        if (options.EnforceOptimisticConcurrency && patch.Count > 0)
        {
            // Pull from `current` (live state) — that's what the server will compare against.
            InjectOptimisticConcurrency(patch, current ?? original);
        }

        if (patch.Count == 0)
        {
            activity?.SetTag("smp.empty", true);
            activity?.SetTag("smp.patch.bytes", 2);
            options.Logger?.LogInformation(
                "smp.compute_three_way gvk={Gvk} empty=true bytes=2 schema_miss={Misses}",
                gvk?.ToString() ?? "<unknown>",
                activity?.GetTagItem(SchemaMissTracking.CountTag) ?? 0);
            return null;
        }

        activity?.SetTag("smp.empty", false);
        var bytes = patch.ToJsonString().Length;
        activity?.SetTag("smp.patch.bytes", bytes);
        options.Logger?.LogInformation(
            "smp.compute_three_way gvk={Gvk} empty=false bytes={Bytes} schema_miss={Misses}",
            gvk?.ToString() ?? "<unknown>", bytes,
            activity?.GetTagItem(SchemaMissTracking.CountTag) ?? 0);
        return patch;
    }

    private static void ValidateRootIdentity(JsonObject? original, JsonObject? modified, JsonObject? current)
    {
        // Pairwise checks; sparse-on-one-side is tolerated (matches two-way behaviour).
        AssertSame(original, modified, "apiVersion");
        AssertSame(original, modified, "kind");
        AssertSame(modified, current, "apiVersion");
        AssertSame(modified, current, "kind");
    }

    private static void AssertSame(JsonObject? a, JsonObject? b, string field)
    {
        if (a is null || b is null)
        {
            return;
        }
        var l = a.TryGetPropertyValue(field, out var lv) ? lv?.GetValue<string>() : null;
        var r = b.TryGetPropertyValue(field, out var rv) ? rv?.GetValue<string>() : null;
        if (l is null || r is null)
        {
            return;
        }
        if (!string.Equals(l, r, StringComparison.Ordinal))
        {
            throw new StrategicMergePatchException(
                $"Three-way merge inputs disagree on '{field}': '{l}' vs '{r}'.", JsonPointer.Root);
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

    private static void InjectOptimisticConcurrency(JsonObject patch, JsonObject? sourceDoc)
    {
        if (sourceDoc?["metadata"] is not JsonObject sourceMeta)
        {
            return;
        }
        if (patch["metadata"] is not JsonObject meta)
        {
            meta = new JsonObject();
            patch["metadata"] = meta;
        }
        CopyIfPresent(sourceMeta, meta, "uid");
        CopyIfPresent(sourceMeta, meta, "resourceVersion");
    }

    private static void CopyIfPresent(JsonObject src, JsonObject dst, string key)
    {
        if (!src.TryGetPropertyValue(key, out var value) || value is null)
        {
            return;
        }
        dst[key] = Internal.JsonNodeCloning.CloneOrNull(value);
    }
}
