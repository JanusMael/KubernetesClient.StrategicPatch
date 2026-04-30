using System.Text.Json.Nodes;

namespace KubernetesClient.StrategicPatch.Internal;

/// <summary>
/// Null-safe wrapper around <see cref="JsonNode.DeepClone"/>. Produces a copy that is no longer
/// parented to the source tree, which the diff and apply engines need before mutating subtrees.
/// </summary>
internal static class JsonNodeCloning
{
    public static JsonNode? CloneOrNull(JsonNode? source) => source?.DeepClone();
}
