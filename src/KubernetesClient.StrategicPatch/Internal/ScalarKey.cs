using System.Text.Json;
using System.Text.Json.Nodes;

namespace KubernetesClient.StrategicPatch.Internal;

/// <summary>
/// Canonical string representation for primitive <see cref="JsonNode"/> values used as set
/// membership keys (primitive merge / set lists) and merge-key lookup keys (object lists).
/// </summary>
/// <remarks>
/// <para>The key carries a one-character discriminator (<c>s</c> for string, <c>n</c> for number,
/// <c>b</c> for bool, <c>x</c> for null) so values of different JSON kinds with the same textual
/// form (string <c>"42"</c> vs number <c>42</c>) compare unequal — matching Go's behaviour and
/// preventing surprising merges.</para>
/// <para>Critically, the discriminator is applied <i>regardless</i> of whether the node was
/// constructed by <see cref="JsonNode.Parse(string,System.Text.Json.Nodes.JsonNodeOptions?,System.Text.Json.JsonDocumentOptions)"/>
/// (JsonElement-backed) or <see cref="JsonValue.Create{T}(T,System.Text.Json.Nodes.JsonNodeOptions?)"/>
/// (CLR-backed). An earlier implementation prefixed only the JsonElement path, which made
/// <c>JsonValue.Create("42")</c> and <c>JsonNode.Parse("\"42\"")</c> compare unequal as set
/// members. This helper unifies the two.</para>
/// <para>Numbers retain their raw textual form so equal-value-different-text variants
/// (<c>1</c> vs <c>1.0</c>) are treated as distinct set elements; that mirrors the Go reference's
/// <c>fmt.Sprintf("%v", v)</c>-style stringification, where the JSON token text is what gets
/// compared. Number canonicalization is the responsibility of the caller (<see cref="JsonNodeEquality"/>
/// for value equality, <c>ScalarKey</c> for set-membership identity).</para>
/// </remarks>
internal static class ScalarKey
{
    /// <summary>The sentinel returned for <c>null</c> nodes — distinguishable from any concrete value.</summary>
    public const string Null = "x:null";

    /// <summary>Canonical string for any primitive <see cref="JsonNode"/>.</summary>
    public static string Of(JsonNode? node)
    {
        if (node is null)
        {
            return Null;
        }
        if (node is JsonValue value)
        {
            // Prefer the JsonElement-backed path: it carries the original JSON token text.
            if (value.TryGetValue<JsonElement>(out var element))
            {
                return Encode(element.ValueKind, element);
            }

            // CLR-backed values (JsonValue.Create<T>(...)). Probe the CLR shape directly so the
            // resulting key is identical to the JsonElement path for the same logical value.
            if (value.TryGetValue<bool>(out var b))
            {
                return b ? "b:1" : "b:0";
            }
            if (value.TryGetValue<string>(out var s))
            {
                return "s:" + s;
            }
            // Numeric: serialise via ToJsonString to get the canonical JSON token text.
            return "n:" + value.ToJsonString();
        }
        // Should not happen for primitive lists; produce a stable but obviously-non-primitive key.
        return "?:" + node.ToJsonString();
    }

    private static string Encode(JsonValueKind kind, JsonElement element) => kind switch
    {
        JsonValueKind.String => "s:" + element.GetString(),
        JsonValueKind.Number => "n:" + element.GetRawText(),
        JsonValueKind.True => "b:1",
        JsonValueKind.False => "b:0",
        JsonValueKind.Null => Null,
        _ => "?:" + element.GetRawText(),
    };
}
