using System.Diagnostics;
using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Server-side strategic-merge patch application. Given an <c>original</c> document and a patch
/// produced by <see cref="TwoWayMerge"/> or <see cref="ThreeWayMerge"/>, produces the merged
/// result. Mirrors Go's <c>strategicpatch.StrategicMergePatch</c> /
/// <c>StrategicMergeMapPatchUsingLookupPatchMeta</c> with <c>MergeParallelList=true</c>.
/// </summary>
internal static class PatchApply
{
    /// <summary>
    /// Applies <paramref name="patch"/> onto a deep clone of <paramref name="original"/> and
    /// returns the merged document. Both inputs are unmodified by the call.
    /// </summary>
    public static JsonObject StrategicMergePatch(
        JsonObject? original,
        JsonObject patch,
        StrategicPatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        options ??= StrategicPatchOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var gvk = ResolveGvk(original) ?? ResolveGvk(patch);
        SchemaNode? rootSchema = null;
        if (options.SchemaProvider is not null && gvk is not null)
        {
            rootSchema = options.SchemaProvider.GetRootSchema(gvk.Value);
        }

        using var activity = StrategicPatchActivity.Source.StartActivity(
            "smp.apply", ActivityKind.Internal);
        if (gvk is not null)
        {
            activity?.SetTag("smp.gvk", gvk.Value.ToString());
        }
        TwoWayMerge.TagSchemaSource(activity, options.SchemaProvider);
        activity?.SetTag("smp.patch.bytes", patch.ToJsonString().Length);

        var working = original is null
            ? new JsonObject()
            : (JsonObject)original.DeepClone();
        var patchClone = (JsonObject)patch.DeepClone();
        var result = MergeMap(working, patchClone, rootSchema, options, depth: 0, cancellationToken);
        options.Logger?.LogInformation(
            "smp.apply gvk={Gvk} bytes={Bytes}",
            gvk?.ToString() ?? "<unknown>",
            patch.ToJsonString().Length);
        return result;
    }

    /// <summary>
    /// Recursively applies <paramref name="patch"/> onto <paramref name="original"/>. Mutates
    /// <paramref name="original"/> and returns it (caller is responsible for cloning if needed).
    /// </summary>
    private static JsonObject MergeMap(
        JsonObject original,
        JsonObject patch,
        SchemaNode? schema,
        StrategicPatchOptions options,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > options.MaxDepth)
        {
            throw new StrategicMergePatchException(
                $"Apply recursion depth {depth} exceeded MaxDepth ({options.MaxDepth}).", JsonPointer.Root);
        }
        // Object-level $patch directive (delete/replace/merge).
        if (patch.TryGetPropertyValue(Directives.Marker, out var directive) && directive is not null)
        {
            var marker = directive.GetValue<string>();
            switch (marker)
            {
                case Directives.Delete:
                    original.Clear();
                    return original;
                case Directives.Replace:
                    original.Clear();
                    foreach (var (k, v) in patch)
                    {
                        if (k == Directives.Marker)
                        {
                            continue;
                        }
                        original[k] = JsonNodeCloning.CloneOrNull(v);
                    }
                    return original;
                case Directives.Merge:
                    // Default behavior — drop the marker and proceed.
                    patch.Remove(Directives.Marker);
                    break;
                default:
                    throw new StrategicMergePatchException(
                        $"Unknown $patch directive value '{marker}'.", JsonPointer.Root);
            }
        }

        // Pull out parallel-list directives so we can apply them after the main merge.
        // These keys are stripped from `patch` so the main loop doesn't process them as fields.
        var deleteFromPrimitive = ExtractParallelLists(patch, Directives.DeleteFromPrimitiveListPrefix);
        var setElementOrder = ExtractParallelLists(patch, Directives.SetElementOrderPrefix);
        ApplyRetainKeys(original, patch);

        foreach (var key in patch.Select(kv => kv.Key).ToArray())
        {
            var patchV = patch[key];
            var originalV = original.TryGetPropertyValue(key, out var existing) ? existing : null;

            // null in the patch = delete the key from original.
            if (patchV is null)
            {
                original.Remove(key);
                continue;
            }

            var childSchema = schema?.Properties.TryGetValue(key, out var childNode) == true ? childNode : null;

            if (originalV is null)
            {
                // New field: take patch value as-is, after stripping any directives that linger inside.
                original[key] = StripDirectives(patchV);
                continue;
            }

            if (originalV.GetValueKind() != patchV.GetValueKind())
            {
                // Type changed — patch wins.
                original[key] = StripDirectives(patchV);
                continue;
            }

            switch (originalV, patchV)
            {
                case (JsonObject originalObj, JsonObject patchObj):
                    {
                        original[key] = MergeMap(originalObj, patchObj, childSchema, options, depth + 1, cancellationToken);
                        break;
                    }
                case (JsonArray originalArr, JsonArray patchArr):
                    {
                        deleteFromPrimitive.TryGetValue(key, out var deleteList);
                        setElementOrder.TryGetValue(key, out var orderList);
                        original[key] = MergeSlice(
                            originalArr, patchArr, childSchema, deleteList, orderList, options, depth + 1, cancellationToken);
                        break;
                    }
                default:
                    {
                        original[key] = JsonNodeCloning.CloneOrNull(patchV);
                        break;
                    }
            }
        }

        // Apply $deleteFromPrimitiveList for fields that didn't have a corresponding patch list
        // entry (the deletes alone — primitive removal without addition).
        foreach (var (field, deleteList) in deleteFromPrimitive)
        {
            if (!patch.ContainsKey(field) && original[field] is JsonArray originalArr)
            {
                original[field] = ApplyPrimitiveDeleteList(originalArr, deleteList);
            }
        }

        // Apply $setElementOrder for fields where order changed but no add/delete patch entry was emitted.
        foreach (var (field, orderList) in setElementOrder)
        {
            if (!patch.ContainsKey(field) && original[field] is JsonArray originalArr)
            {
                var fieldSchema = schema?.Properties.TryGetValue(field, out var f) == true ? f : null;
                original[field] = ReorderToMatch(originalArr, orderList, fieldSchema);
            }
        }

        return original;
    }

    /// <summary>
    /// Applies a strategic-merge slice patch onto <paramref name="original"/>.
    /// </summary>
    private static JsonArray MergeSlice(
        JsonArray original,
        JsonArray patch,
        SchemaNode? schema,
        JsonArray? deleteList,
        JsonArray? orderList,
        StrategicPatchOptions options,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // List-of-lists is unsupported on apply just as it is on diff.
        EnsureNoListOfLists(original);
        EnsureNoListOfLists(patch);

        var strategy = schema?.Strategy ?? PatchStrategy.None;
        if (!strategy.HasFlag(PatchStrategy.Merge))
        {
            // Atomic replace: clone the patch wholesale.
            return (JsonArray)patch.DeepClone();
        }

        var mergeKey = schema?.PatchMergeKey;

        // Heuristic for primitive vs object lists.
        var elementsAreObjects = ContainsObject(original) || ContainsObject(patch);

        if (elementsAreObjects)
        {
            return MergeListOfMaps(original, patch, schema, mergeKey, orderList, options, depth, cancellationToken);
        }

        return MergeListOfScalars(original, patch, deleteList, orderList);
    }

    private static void EnsureNoListOfLists(JsonArray arr)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonArray)
            {
                throw new StrategicMergePatchException(
                    "List of lists is not supported by strategic merge patch.", JsonPointer.Root);
            }
        }
    }

    private static JsonArray MergeListOfScalars(JsonArray original, JsonArray patch, JsonArray? deleteList, JsonArray? orderList)
    {
        var result = new List<JsonNode?>(original.Count + patch.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in original)
        {
            var k = Internal.ScalarKey.Of(item);
            if (seen.Add(k))
            {
                result.Add(JsonNodeCloning.CloneOrNull(item));
            }
        }
        foreach (var item in patch)
        {
            var k = Internal.ScalarKey.Of(item);
            if (seen.Add(k))
            {
                result.Add(JsonNodeCloning.CloneOrNull(item));
            }
        }

        if (deleteList is not null && deleteList.Count > 0)
        {
            var toDelete = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in deleteList)
            {
                toDelete.Add(Internal.ScalarKey.Of(d));
            }
            result.RemoveAll(node => toDelete.Contains(Internal.ScalarKey.Of(node)));
        }

        if (orderList is not null && orderList.Count > 0)
        {
            result.Sort(new ScalarOrderComparer(orderList));
        }

        var arr = new JsonArray();
        foreach (var item in result)
        {
            arr.Add(item);
        }
        return arr;
    }

    private static JsonArray MergeListOfMaps(
        JsonArray original,
        JsonArray patch,
        SchemaNode? schema,
        string? mergeKey,
        JsonArray? orderList,
        StrategicPatchOptions options,
        int depth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(mergeKey))
        {
            // Defensive: schema says merge but no key — atomic replace.
            return (JsonArray)patch.DeepClone();
        }

        // Index original by merge-key value.
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var node in original)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(mergeKey, out var key) || key is null)
            {
                continue;
            }
            var k = MergeKeyString(key);
            byKey[k] = (JsonObject)obj.DeepClone();
            order.Add(k);
        }

        // Apply patch entries in order.
        foreach (var patchNode in patch)
        {
            if (patchNode is not JsonObject patchObj)
            {
                continue;
            }

            // Element-level $patch:delete?
            if (patchObj.TryGetPropertyValue(Directives.Marker, out var marker) &&
                marker?.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                marker.GetValue<string>() == Directives.Delete)
            {
                if (patchObj.TryGetPropertyValue(mergeKey, out var keyValue) && keyValue is not null)
                {
                    var k = MergeKeyString(keyValue);
                    byKey.Remove(k);
                    order.RemoveAll(s => string.Equals(s, k, StringComparison.Ordinal));
                }
                continue;
            }

            if (!patchObj.TryGetPropertyValue(mergeKey, out var pkv) || pkv is null)
            {
                throw new StrategicMergePatchException(
                    $"Patch list element missing merge key '{mergeKey}'.", JsonPointer.Root);
            }
            var pk = MergeKeyString(pkv);

            if (byKey.TryGetValue(pk, out var existing))
            {
                byKey[pk] = MergeMap(existing, (JsonObject)patchObj.DeepClone(), schema?.Items, options, depth + 1, cancellationToken);
            }
            else
            {
                byKey[pk] = (JsonObject)patchObj.DeepClone();
                order.Add(pk);
            }
        }

        // Apply ordering: prefer setElementOrder when supplied.
        IReadOnlyList<string> finalOrder;
        if (orderList is not null && orderList.Count > 0)
        {
            var ordered = new List<string>(orderList.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var orderItem in orderList)
            {
                if (orderItem is not JsonObject orderObj ||
                    !orderObj.TryGetPropertyValue(mergeKey, out var ov) || ov is null)
                {
                    continue;
                }
                var k = MergeKeyString(ov);
                if (byKey.ContainsKey(k) && seen.Add(k))
                {
                    ordered.Add(k);
                }
            }
            // Append any extras not mentioned in setElementOrder (Go behaviour: extras keep relative position from `order`).
            foreach (var k in order)
            {
                if (byKey.ContainsKey(k) && seen.Add(k))
                {
                    ordered.Add(k);
                }
            }
            finalOrder = ordered;
        }
        else
        {
            finalOrder = order.Where(byKey.ContainsKey).ToArray();
        }

        var result = new JsonArray();
        foreach (var k in finalOrder)
        {
            result.Add(byKey[k]);
        }
        return result;
    }

    /// <summary>
    /// Strips embedded directives from a subtree before adopting it as a fresh field value.
    /// </summary>
    private static JsonNode? StripDirectives(JsonNode? node)
    {
        var clone = JsonNodeCloning.CloneOrNull(node);
        if (clone is JsonObject obj)
        {
            StripDirectivesInPlace(obj);
        }
        else if (clone is JsonArray arr)
        {
            for (var i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is JsonObject elementObj &&
                    elementObj.TryGetPropertyValue(Directives.Marker, out var m) &&
                    m?.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                    m.GetValue<string>() == Directives.Delete)
                {
                    arr.RemoveAt(i);
                }
                else if (arr[i] is JsonObject inner)
                {
                    StripDirectivesInPlace(inner);
                }
            }
        }
        return clone;
    }

    private static void StripDirectivesInPlace(JsonObject obj)
    {
        var toRemove = new List<string>();
        foreach (var (k, _) in obj)
        {
            if (k.Length > 0 && k[0] == '$')
            {
                toRemove.Add(k);
            }
        }
        foreach (var k in toRemove)
        {
            obj.Remove(k);
        }
        foreach (var (_, v) in obj)
        {
            if (v is JsonObject child)
            {
                StripDirectivesInPlace(child);
            }
        }
    }

    /// <summary>
    /// Pulls all keys with the given parallel-list prefix out of <paramref name="patch"/>,
    /// returning a map of (field name → directive value).
    /// </summary>
    private static Dictionary<string, JsonArray> ExtractParallelLists(JsonObject patch, string prefix)
    {
        var result = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var keysToRemove = new List<string>();
        foreach (var (key, value) in patch)
        {
            if (!key.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                continue;
            }
            keysToRemove.Add(key);
            if (value is JsonArray arr)
            {
                var field = key[(prefix.Length + 1)..];
                result[field] = arr;
            }
        }
        foreach (var k in keysToRemove)
        {
            patch.Remove(k);
        }
        return result;
    }

    private static void ApplyRetainKeys(JsonObject original, JsonObject patch)
    {
        if (!patch.TryGetPropertyValue(Directives.RetainKeys, out var retainNode) ||
            retainNode is not JsonArray retainArr)
        {
            return;
        }
        patch.Remove(Directives.RetainKeys);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in retainArr)
        {
            if (item is null)
            {
                continue;
            }
            allowed.Add(item.GetValue<string>());
        }

        // Drop original keys not in the retainKeys list.
        var toRemove = new List<string>();
        foreach (var (k, _) in original)
        {
            if (!allowed.Contains(k))
            {
                toRemove.Add(k);
            }
        }
        foreach (var k in toRemove)
        {
            original.Remove(k);
        }
    }

    private static JsonArray ApplyPrimitiveDeleteList(JsonArray originalArr, JsonArray deleteList)
    {
        var toDelete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in deleteList)
        {
            toDelete.Add(Internal.ScalarKey.Of(d));
        }
        var result = new JsonArray();
        foreach (var item in originalArr)
        {
            if (!toDelete.Contains(Internal.ScalarKey.Of(item)))
            {
                result.Add(JsonNodeCloning.CloneOrNull(item));
            }
        }
        return result;
    }

    private static JsonArray ReorderToMatch(JsonArray originalArr, JsonArray orderList, SchemaNode? fieldSchema)
    {
        var mergeKey = fieldSchema?.PatchMergeKey;
        var elementsAreObjects = mergeKey is not null;

        if (elementsAreObjects)
        {
            var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in originalArr)
            {
                if (item is JsonObject obj && obj.TryGetPropertyValue(mergeKey!, out var v) && v is not null)
                {
                    byKey[MergeKeyString(v)] = (JsonObject)obj.DeepClone();
                }
            }
            var result = new JsonArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var orderItem in orderList)
            {
                if (orderItem is JsonObject orderObj &&
                    orderObj.TryGetPropertyValue(mergeKey!, out var ov) && ov is not null)
                {
                    var k = MergeKeyString(ov);
                    if (byKey.Remove(k, out var elem) && seen.Add(k))
                    {
                        result.Add(elem);
                    }
                }
            }
            foreach (var leftover in byKey.Values)
            {
                result.Add(leftover);
            }
            return result;
        }

        // Primitive: sort by orderList rank.
        var copy = new List<JsonNode?>(originalArr.Count);
        foreach (var item in originalArr)
        {
            copy.Add(JsonNodeCloning.CloneOrNull(item));
        }
        copy.Sort(new ScalarOrderComparer(orderList));
        var arr = new JsonArray();
        foreach (var item in copy)
        {
            arr.Add(item);
        }
        return arr;
    }

    private sealed class ScalarOrderComparer : IComparer<JsonNode?>
    {
        private readonly Dictionary<string, int> _rank;

        public ScalarOrderComparer(JsonArray orderList)
        {
            _rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < orderList.Count; i++)
            {
                var k = Internal.ScalarKey.Of(orderList[i]);
                _rank.TryAdd(k, i);
            }
        }

        public int Compare(JsonNode? x, JsonNode? y)
        {
            var xk = Internal.ScalarKey.Of(x);
            var yk = Internal.ScalarKey.Of(y);
            var xi = _rank.GetValueOrDefault(xk, int.MaxValue);
            var yi = _rank.GetValueOrDefault(yk, int.MaxValue);
            return xi.CompareTo(yi);
        }
    }

    private static bool ContainsObject(JsonArray arr)
    {
        foreach (var item in arr)
        {
            if (item is JsonObject)
            {
                return true;
            }
        }
        return false;
    }

    // ScalarKey moved to Internal/ScalarKey.cs — single source of truth shared with ListDiff.

    /// <summary>
    /// Stringified merge-key value used as a dictionary lookup. Discriminator-free because the
    /// merge-key field is schema-typed at this point (we know it's a string or a number, never
    /// both), so collision between `"42"` (string) and `42` (number) cannot happen here.
    /// </summary>
    private static string MergeKeyString(JsonNode key)
    {
        if (key is JsonValue v && v.TryGetValue<System.Text.Json.JsonElement>(out var el))
        {
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => el.GetString() ?? string.Empty,
                _ => el.GetRawText(),
            };
        }
        return key.ToJsonString();
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
}
