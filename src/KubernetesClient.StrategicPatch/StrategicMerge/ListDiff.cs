using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Computes the strategic-merge two-way diff for a JSON array. Dispatches on the schema's
/// patch-strategy and inferred list element type, emitting:
/// <list type="bullet">
///   <item><b>Atomic</b> (no merge strategy or schema): wholesale replace when not deep-equal.</item>
///   <item><b>Primitive merge / set</b>: surviving values plus a parallel
///         <c>$deleteFromPrimitiveList/&lt;field&gt;</c> for removals, with optional
///         <c>$setElementOrder/&lt;field&gt;</c> when content or order changed.</item>
///   <item><b>Object merge by merge-key</b>: per-element recursion keyed on the merge-key value,
///         deletions appended as <c>{$patch: delete, &lt;mergeKey&gt;: value}</c> elements,
///         plus <c>$setElementOrder/&lt;field&gt;</c> when content or order changed.</item>
/// </list>
/// Mirrors <c>diffLists</c> / <c>diffListsOfMaps</c> / <c>diffListsOfScalars</c> from the Go reference.
/// </summary>
internal static class ListDiff
{
    /// <summary>
    /// Diffs the array <paramref name="originalList"/> against <paramref name="modifiedList"/>,
    /// writing results into <paramref name="patch"/> at the appropriate keys. Recurses into
    /// <see cref="TwoWayMerge.DiffObjectInternal"/> for nested per-element object diffs.
    /// </summary>
    public static void Diff(
        string fieldName,
        JsonArray originalList,
        JsonArray modifiedList,
        SchemaNode? schema,
        JsonObject patch,
        JsonPointer parentPath,
        StrategicPatchOptions options,
        Activity? activity,
        DiffFlags flags = default,
        int depth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // List-of-lists is unsupported by SMP — match Go's mergepatch.ErrNoListOfLists.
        EnsureNoListOfLists(fieldName, originalList, modifiedList, parentPath);

        var strategy = schema?.Strategy ?? PatchStrategy.None;

        // Without merge strategy, atomically replace the whole list when contents differ.
        if (!strategy.HasFlag(PatchStrategy.Merge))
        {
            // Atomic replace counts as "changes and additions" — skipping it under
            // IgnoreChangesAndAdditions matches Go's diffOptions semantics.
            if (!flags.IgnoreChangesAndAdditions && !JsonNodeEquality.DeepEquals(originalList, modifiedList))
            {
                patch[fieldName] = JsonNodeCloning.CloneOrNull(modifiedList);
            }
            return;
        }

        // Heuristic: look at element shape to decide between map-of-objects and scalar/set.
        // Items schema, if present, is authoritative; otherwise sniff from elements.
        var elementsAreObjects = AreElementsObjects(originalList, modifiedList, schema);

        if (elementsAreObjects)
        {
            DiffListsOfMaps(fieldName, originalList, modifiedList, schema, patch, parentPath, options, activity, flags, depth, cancellationToken);
        }
        else
        {
            DiffListsOfScalars(fieldName, originalList, modifiedList, schema, patch, options, flags);
        }
    }

    /// <summary>
    /// Walks both lists once and throws <see cref="StrategicMergePatchException"/> if any element
    /// is itself a list. Mirrors Go's <c>mergepatch.ErrNoListOfLists</c>; the SMP wire format has
    /// no encoding for nested lists and the API server rejects them.
    /// </summary>
    private static void EnsureNoListOfLists(string fieldName, JsonArray a, JsonArray b, JsonPointer parentPath)
    {
        foreach (var arr in new[] { a, b })
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonArray)
                {
                    throw new StrategicMergePatchException(
                        $"Field '{fieldName}' is a list of lists, which strategic merge patch does not support.",
                        parentPath.Append(fieldName).Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }
    }

    private static bool AreElementsObjects(JsonArray a, JsonArray b, SchemaNode? schema)
    {
        if (schema?.Items?.Kind == SchemaNodeKind.Object || schema?.Items?.Kind == SchemaNodeKind.Map)
        {
            return true;
        }
        if (schema?.Items?.Kind == SchemaNodeKind.Primitive)
        {
            return false;
        }
        // Sniff: any element being a JsonObject indicates a map-of-objects list.
        foreach (var arr in new[] { a, b })
        {
            foreach (var item in arr)
            {
                if (item is JsonObject)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // ---- Scalar / set lists ------------------------------------------------------------------

    /// <summary>
    /// Mirrors Go's <c>diffListsOfScalars</c>. Sorts both sides by stringified value, walks them in
    /// lockstep, partitions into <c>add</c> and <c>delete</c> sets. Emits surviving additions as the
    /// patch list (in modified's original order) and removals via the parallel
    /// <c>$deleteFromPrimitiveList/&lt;field&gt;</c>.
    /// </summary>
    private static void DiffListsOfScalars(
        string fieldName,
        JsonArray original,
        JsonArray modified,
        SchemaNode? schema,
        JsonObject patch,
        StrategicPatchOptions options,
        DiffFlags flags)
    {
        var (additions, deletions) = PartitionScalars(original, modified);
        if (flags.IgnoreChangesAndAdditions)
        {
            additions.Clear();
        }
        if (flags.IgnoreDeletions)
        {
            deletions.Clear();
        }

        var addList = additions.Count == 0 ? null : ReorderToMatch(additions, modified);
        if (addList is not null && addList.Count > 0)
        {
            patch[fieldName] = addList;
        }

        if (deletions.Count > 0)
        {
            var del = new JsonArray();
            foreach (var d in deletions)
            {
                del.Add(JsonNodeCloning.CloneOrNull(d));
            }
            patch[Directives.DeleteFromPrimitiveListKey(fieldName)] = del;
        }

        // setElementOrder fires when this diff actually emitted content; pure-no-op subsequence
        // changes under IgnoreChangesAndAdditions or IgnoreDeletions don't warrant the directive.
        var contentDiffers = additions.Count > 0 || deletions.Count > 0
            || (!flags.IgnoreChangesAndAdditions && !JsonNodeEquality.DeepEquals(original, modified));
        if (contentDiffers)
        {
            var order = new JsonArray();
            foreach (var item in modified)
            {
                order.Add(JsonNodeCloning.CloneOrNull(item));
            }
            patch[Directives.SetElementOrderKey(fieldName)] = order;
        }
    }

    private static (List<JsonNode?> Additions, List<JsonNode?> Deletions) PartitionScalars(
        JsonArray original, JsonArray modified)
    {
        var origKeys = new HashSet<string>(StringComparer.Ordinal);
        var origByKey = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in original)
        {
            var key = Internal.ScalarKey.Of(item);
            origKeys.Add(key);
            origByKey[key] = item;
        }

        var modifiedKeys = new HashSet<string>(StringComparer.Ordinal);
        var additions = new List<JsonNode?>();
        foreach (var item in modified)
        {
            var key = Internal.ScalarKey.Of(item);
            modifiedKeys.Add(key);
            if (!origKeys.Contains(key))
            {
                additions.Add(item);
            }
        }

        var deletions = new List<JsonNode?>();
        foreach (var key in origKeys)
        {
            if (!modifiedKeys.Contains(key))
            {
                deletions.Add(origByKey[key]);
            }
        }
        return (additions, deletions);
    }

    // ScalarKey moved to Internal/ScalarKey.cs — single source of truth shared with PatchApply.

    private static JsonArray ReorderToMatch(List<JsonNode?> survivors, JsonArray modifiedOrder)
    {
        var keyset = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var s in survivors)
        {
            keyset[Internal.ScalarKey.Of(s)] = s;
        }

        var arr = new JsonArray();
        foreach (var item in modifiedOrder)
        {
            var k = Internal.ScalarKey.Of(item);
            if (keyset.Remove(k, out var match))
            {
                arr.Add(JsonNodeCloning.CloneOrNull(match));
            }
        }
        // Anything still in keyset would be unexpected; append for safety.
        foreach (var leftover in keyset.Values)
        {
            arr.Add(JsonNodeCloning.CloneOrNull(leftover));
        }
        return arr;
    }

    // ---- Map (objects keyed by merge-key) lists ----------------------------------------------

    /// <summary>
    /// Mirrors Go's <c>diffListsOfMaps</c>. Per-element recursion via merge-key value, deletions as
    /// <c>{$patch: delete, &lt;mergeKey&gt;: value}</c> elements appended at the end.
    /// </summary>
    private static void DiffListsOfMaps(
        string fieldName,
        JsonArray original,
        JsonArray modified,
        SchemaNode? schema,
        JsonObject patch,
        JsonPointer parentPath,
        StrategicPatchOptions options,
        Activity? activity,
        DiffFlags flags,
        int depth,
        CancellationToken cancellationToken)
    {
        var mergeKey = schema?.PatchMergeKey;
        if (string.IsNullOrEmpty(mergeKey))
        {
            // Schema says merge but no merge key — fall back to atomic replace and warn.
            options.Logger?.LogWarning(
                "smp.list-merge-without-mergekey path={Path} field={Field}",
                parentPath, fieldName);
            if (!JsonNodeEquality.DeepEquals(original, modified))
            {
                patch[fieldName] = JsonNodeCloning.CloneOrNull(modified);
            }
            return;
        }

        // Index original by merge-key value (stringified).
        var origByKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var origOrder = new List<string>(original.Count);
        foreach (var node in original)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(mergeKey, out var keyValue) || keyValue is null)
            {
                throw new StrategicMergePatchException(
                    $"Item in original list '{fieldName}' is missing merge key '{mergeKey}'.",
                    parentPath.Append(fieldName));
            }
            var keyString = MergeKeyString(keyValue);
            origByKey[keyString] = obj;
            origOrder.Add(keyString);
        }

        // Walk modified in caller-supplied order. Recurse for matches; full-element add for new keys.
        var modifiedKeys = new HashSet<string>(StringComparer.Ordinal);
        var modifiedOrder = new List<string>(modified.Count);
        var patchEntries = new List<JsonNode>();
        foreach (var node in modified)
        {
            if (node is not JsonObject modObj || !modObj.TryGetPropertyValue(mergeKey, out var modKeyValue) || modKeyValue is null)
            {
                throw new StrategicMergePatchException(
                    $"Item in modified list '{fieldName}' is missing merge key '{mergeKey}'.",
                    parentPath.Append(fieldName));
            }
            var keyString = MergeKeyString(modKeyValue);
            modifiedKeys.Add(keyString);
            modifiedOrder.Add(keyString);

            if (origByKey.TryGetValue(keyString, out var origObj))
            {
                var subPath = parentPath.Append(fieldName).Append(keyString);
                var subPatch = TwoWayMerge.DiffObjectInternal(
                    origObj, modObj, schema?.Items, subPath, options, activity, flags, depth + 1, cancellationToken);
                if (subPatch.Count > 0)
                {
                    // Always carry the merge-key into the patch object so the applier can locate it.
                    if (!subPatch.ContainsKey(mergeKey))
                    {
                        subPatch[mergeKey] = JsonNodeCloning.CloneOrNull(modKeyValue);
                    }
                    patchEntries.Add(subPatch);
                }
            }
            else if (!flags.IgnoreChangesAndAdditions)
            {
                // New element — full clone.
                patchEntries.Add((JsonNode)modObj.DeepClone());
            }
        }

        // Deletions: items in original whose merge-key isn't in modified — emit as $patch:delete elements.
        var deletions = new List<JsonNode>();
        if (!flags.IgnoreDeletions)
        {
            foreach (var key in origOrder)
            {
                if (!modifiedKeys.Contains(key))
                {
                    var origObj = origByKey[key];
                    var deleter = new JsonObject
                    {
                        [Directives.Marker] = Directives.Delete,
                        [mergeKey] = JsonNodeCloning.CloneOrNull(origObj[mergeKey]),
                    };
                    deletions.Add(deleter);
                }
            }
        }

        var contentChanges = patchEntries.Count > 0 || deletions.Count > 0;
        var orderChanged = !OrderMatches(origOrder, modifiedOrder, modifiedKeys);

        // Mirror Go's diffLists gate for setElementOrder:
        //   (!IgnoreChangesAndAdditions && (contentChanges || orderChanged))
        //   || (!IgnoreDeletions && contentChanges)
        // Under DeletionsOnly we only emit when we actually produced deletions; the
        // original-vs-modified order delta is already captured by the deltaMap.
        var emitSetOrder = (!flags.IgnoreChangesAndAdditions && (contentChanges || orderChanged))
                        || (!flags.IgnoreDeletions && contentChanges);

        // Always emit content array if we produced patch entries or deletions, regardless of
        // setElementOrder gating.
        if (contentChanges)
        {
            var arr = new JsonArray();
            foreach (var e in patchEntries)
            {
                arr.Add(e);
            }
            foreach (var d in deletions)
            {
                arr.Add(d);
            }
            patch[fieldName] = arr;
        }

        if (emitSetOrder)
        {
            var orderArr = new JsonArray();
            foreach (var node in modified)
            {
                if (node is JsonObject modObj && modObj.TryGetPropertyValue(mergeKey, out var v))
                {
                    var entry = new JsonObject
                    {
                        [mergeKey] = JsonNodeCloning.CloneOrNull(v),
                    };
                    orderArr.Add(entry);
                }
            }
            patch[Directives.SetElementOrderKey(fieldName)] = orderArr;
        }
    }

    /// <summary>
    /// Returns true iff the surviving merge-key sequence in <paramref name="modifiedOrder"/>
    /// (i.e. the keys that exist in both original and modified) is equal to the corresponding
    /// surviving sequence in <paramref name="origOrder"/>.
    /// </summary>
    private static bool OrderMatches(List<string> origOrder, List<string> modifiedOrder, HashSet<string> modifiedKeys)
    {
        var origSurvivors = new List<string>(origOrder.Count);
        foreach (var k in origOrder)
        {
            if (modifiedKeys.Contains(k))
            {
                origSurvivors.Add(k);
            }
        }
        var modSurvivors = new List<string>(modifiedOrder.Count);
        foreach (var k in modifiedOrder)
        {
            modSurvivors.Add(k);
        }
        if (origSurvivors.Count != modSurvivors.Count)
        {
            return false;
        }
        for (var i = 0; i < origSurvivors.Count; i++)
        {
            if (!string.Equals(origSurvivors[i], modSurvivors[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string MergeKeyString(JsonNode keyValue)
    {
        if (keyValue is JsonValue v && v.TryGetValue<JsonElement>(out var el))
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? string.Empty,
                _ => el.GetRawText(),
            };
        }
        return keyValue.ToJsonString();
    }
}
