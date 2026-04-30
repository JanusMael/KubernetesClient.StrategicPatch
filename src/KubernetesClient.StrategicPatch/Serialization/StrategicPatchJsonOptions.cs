using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubernetesClient.StrategicPatch.Serialization;

/// <summary>
/// JSON serializer options for the strategic-merge engine. Geared at <see cref="System.Text.Json.Nodes.JsonNode"/>-level
/// serialization (the DOM the diff engine produces); typed Kubernetes objects continue to round-trip through
/// <see cref="k8s.KubernetesJson"/> at the API boundary, which keeps caller-facing parity with the rest of
/// <c>KubernetesClient</c> intact.
/// </summary>
/// <remarks>
/// <para>The original plan called for cloning <c>KubernetesJson.JsonSerializerOptions</c>, but that field is
/// not part of the public API of <c>KubernetesClient</c>. Reaching it via reflection would couple this library
/// to private layout. Building options from scratch is cleaner because the patch DOM only contains JSON
/// primitives, objects, and arrays — no K8s-specific value types — so the K8s-specific converters
/// (<c>IntOrStringConverter</c>, <c>ResourceQuantityJsonConverter</c>, etc.) are not needed at this layer.</para>
/// <para>Overrides applied:
/// <list type="bullet">
///   <item><see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> = <see cref="JsonIgnoreCondition.Never"/>
///         so caller-supplied <c>null</c> properties survive into the patch DOM (under SMP semantics they
///         denote "delete this map key").</item>
///   <item><see cref="JsonSerializerOptions.MaxDepth"/> = 256, comfortably above realistic Kubernetes payloads
///         while still bounding pathological cycles.</item>
///   <item>UTC-only DateTime / DateTimeOffset converters; non-UTC values throw at the boundary.</item>
/// </list>
/// </para>
/// </remarks>
public static class StrategicPatchJsonOptions
{
    private static readonly Lazy<JsonSerializerOptions> LazyDefault = new(BuildSealed, isThreadSafe: true);

    /// <summary>The library-wide default options instance. Read-only and shared.</summary>
    public static JsonSerializerOptions Default => LazyDefault.Value;

    /// <summary>
    /// Returns a fresh, mutable clone with the same overrides applied. Callers that need to register
    /// additional converters (custom CRD types, test stubs) should use this.
    /// </summary>
    public static JsonSerializerOptions CreateClone() => Build();

    private static JsonSerializerOptions BuildSealed()
    {
        var options = Build();
        // Populate the default reflection-based resolver so the options can be marked read-only
        // without forcing every consumer to ship a source-gen JsonSerializerContext.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            MaxDepth = 256,
            PropertyNamingPolicy = null,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new StrictUtcDateTimeConverter());
        options.Converters.Add(new StrictUtcDateTimeOffsetConverter());
        return options;
    }
}
