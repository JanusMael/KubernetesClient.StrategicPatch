using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubernetesClient.StrategicPatch.SourceGenerators;

/// <summary>
/// Compile-time-only POCO copy of the runtime library's <c>SchemaWireFormat</c>. The generator
/// targets <c>netstandard2.0</c> (compiler host's runtime), so it can't reference the
/// <c>net10.0</c> runtime lib's wire types directly. Single source of truth for the format
/// stays in <c>SchemaWireFormat.cs</c>; this file is the read-only mirror. Both files MUST
/// agree on:
/// <list type="bullet">
///   <item>The version constant (currently 1).</item>
///   <item>The single-letter field names (<c>v</c>, <c>s</c>, <c>k</c>, <c>p</c>, <c>i</c>, <c>mk</c>, <c>ps</c>, <c>lt</c>).</item>
///   <item>The single-character kind/list-type codes.</item>
/// </list>
/// Drift is caught by <c>RoundTrip</c> snapshot tests; a wire-version bump in the runtime lib
/// requires a matching bump here.
/// </summary>
internal static class WireFormat
{
    public const int CurrentVersion = 1;

    public sealed class WireDoc
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("s")] public Dictionary<string, WireNode>? Schemas { get; set; }
    }

    public sealed class WireNode
    {
        /// <summary>Single-character kind code: O=Object, M=Map, L=List, P=Primitive.</summary>
        [JsonPropertyName("k")] public string? K { get; set; }
        /// <summary>x-kubernetes-patch-merge-key (omitted when null).</summary>
        [JsonPropertyName("mk")] public string? Mk { get; set; }
        /// <summary>PatchStrategy bitmask (omitted when None).</summary>
        [JsonPropertyName("ps")] public int? Ps { get; set; }
        /// <summary>Single-character list-type code: a=Atomic, s=Set, m=Map (omitted when Unspecified).</summary>
        [JsonPropertyName("lt")] public string? Lt { get; set; }
        /// <summary>Object children — keyed by JSON property name.</summary>
        [JsonPropertyName("p")] public Dictionary<string, WireNode>? P { get; set; }
        /// <summary>List items / Map values.</summary>
        [JsonPropertyName("i")] public WireNode? I { get; set; }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
    };

    /// <summary>
    /// Parses the schemas.json bytes into the generator's wire DTOs. Throws
    /// <see cref="System.InvalidOperationException"/> on version mismatch (the generator
    /// catches that and surfaces a Roslyn diagnostic <c>SMP004</c>).
    /// </summary>
    public static WireDoc Read(byte[] bytes)
    {
        var doc = JsonSerializer.Deserialize<WireDoc>(bytes, JsonOptions)
            ?? throw new System.InvalidOperationException("schemas.json deserialised to null.");
        if (doc.Version != CurrentVersion)
        {
            throw new System.InvalidOperationException(
                $"Unsupported schemas.json wire version {doc.Version}; generator expects v{CurrentVersion}.");
        }
        return doc;
    }
}
