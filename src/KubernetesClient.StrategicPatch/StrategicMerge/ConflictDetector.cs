using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;

namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Walks two strategic-merge patch DOMs and reports paths where they disagree.
/// Used by three-way merge to detect when server-side changes (encoded in <c>changedMap</c>)
/// conflict with caller-side changes (encoded in <c>patchMap</c>). Mirrors Go's
/// <c>MergingMapsHaveConflicts</c>.
/// </summary>
internal static class ConflictDetector
{
    /// <summary>
    /// Returns every JSON pointer at which <paramref name="patch"/> and <paramref name="changed"/>
    /// disagree. A path agrees when (a) only one side touches it, or (b) both sides set it to the
    /// same value. Empty list = no conflicts.
    /// </summary>
    public static IReadOnlyList<JsonPointer> FindConflicts(JsonObject patch, JsonObject changed)
    {
        var conflicts = new List<JsonPointer>();
        WalkObjects(patch, changed, JsonPointer.Root, conflicts);
        return conflicts;
    }

    private static void WalkObjects(
        JsonObject patch, JsonObject changed, JsonPointer path, List<JsonPointer> conflicts)
    {
        foreach (var (key, patchValue) in patch)
        {
            // Directive keys live alongside payload at the object level; their conflicts must be
            // reasoned about with the SMP-aware logic in Stage 6's apply layer. For now we skip
            // them — caller-vs-server conflicts on raw values are the load-bearing case.
            if (IsDirectiveKey(key))
            {
                continue;
            }

            if (!changed.TryGetPropertyValue(key, out var changedValue))
            {
                continue;
            }

            WalkValues(patchValue, changedValue, path.Append(key), conflicts);
        }
    }

    private static void WalkValues(
        JsonNode? patchValue, JsonNode? changedValue, JsonPointer path, List<JsonPointer> conflicts)
    {
        if (patchValue is null && changedValue is null)
        {
            return;
        }
        if (patchValue is null || changedValue is null)
        {
            // One side wants to delete (null), the other set a value. That's a conflict.
            conflicts.Add(path);
            return;
        }

        if (patchValue.GetValueKind() != changedValue.GetValueKind())
        {
            conflicts.Add(path);
            return;
        }

        switch (patchValue, changedValue)
        {
            case (JsonObject po, JsonObject co):
                WalkObjects(po, co, path, conflicts);
                break;
            case (JsonArray pa, JsonArray ca):
                // Lists with directives have nuanced merge semantics; defer detailed list-conflict
                // analysis to Stage 6's apply layer. For Stage 5 we only flag the wholesale-replace
                // disagreement when both sides assert different list contents.
                if (!JsonNodeEquality.DeepEquals(pa, ca))
                {
                    conflicts.Add(path);
                }
                break;
            default:
                if (!JsonNodeEquality.DeepEquals(patchValue, changedValue))
                {
                    conflicts.Add(path);
                }
                break;
        }
    }

    private static bool IsDirectiveKey(string key) =>
        key.Length > 0 && key[0] == '$';
}
