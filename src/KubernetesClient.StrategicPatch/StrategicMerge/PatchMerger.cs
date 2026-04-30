using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;

namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Merges two strategic-merge patch DOMs into a single combined patch. Used by three-way merge to
/// combine the deletions sub-diff (original→modified, IgnoreChangesAndAdditions) with the delta
/// sub-diff (current→modified, IgnoreDeletions). The two inputs are well-formed patches by
/// construction; this is a structural deep-union, not a re-diff.
/// </summary>
internal static class PatchMerger
{
    /// <summary>
    /// Returns a fresh patch that contains every key from both inputs. For overlapping keys:
    /// <list type="bullet">
    ///   <item>Both objects → recurse.</item>
    ///   <item>Both arrays → concatenate (entries from <paramref name="left"/> first, then <paramref name="right"/>).</item>
    ///   <item>Otherwise → prefer <paramref name="right"/> (the delta side wins, matching Go's
    ///         <c>mergeMap(deletionsMap, deltaMap)</c> semantics where the patch is applied <i>onto</i>
    ///         the deletions).</item>
    /// </list>
    /// Inputs are not mutated.
    /// </summary>
    public static JsonObject Merge(JsonObject? left, JsonObject? right)
    {
        var result = new JsonObject();
        if (left is not null)
        {
            foreach (var (key, value) in left)
            {
                result[key] = JsonNodeCloning.CloneOrNull(value);
            }
        }
        if (right is null)
        {
            return result;
        }

        foreach (var (key, value) in right)
        {
            if (!result.TryGetPropertyValue(key, out var existing))
            {
                result[key] = JsonNodeCloning.CloneOrNull(value);
                continue;
            }

            switch (existing, value)
            {
                case (JsonObject le, JsonObject re):
                    result[key] = Merge(le, re);
                    break;
                case (JsonArray la, JsonArray ra):
                    result[key] = ConcatArrays(la, ra);
                    break;
                default:
                    result[key] = JsonNodeCloning.CloneOrNull(value);
                    break;
            }
        }
        return result;
    }

    private static JsonArray ConcatArrays(JsonArray left, JsonArray right)
    {
        var arr = new JsonArray();
        foreach (var item in left)
        {
            arr.Add(JsonNodeCloning.CloneOrNull(item));
        }
        foreach (var item in right)
        {
            arr.Add(JsonNodeCloning.CloneOrNull(item));
        }
        return arr;
    }
}
