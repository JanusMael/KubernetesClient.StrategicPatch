using Microsoft.CodeAnalysis;

namespace KubernetesClient.StrategicPatch.SourceGenerators;

/// <summary>
/// Roslyn-side mapping of the runtime library's <c>SmpDiagnostics</c> catalog. The generator
/// project deliberately doesn't reference the runtime lib (that targets net10.0 and we target
/// netstandard2.0), so the catalog is duplicated here as <see cref="DiagnosticDescriptor"/>
/// instances. Drift between the two is caught by tests pinning IDs and message-format prefixes.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "KubernetesClient.StrategicPatch";

    /// <summary>SMP001 — Info, summarises the embedded schema set on every generator run.</summary>
    public static readonly DiagnosticDescriptor EmbeddedSchemasReady = new(
        id: "SMP001",
        title: "Embedded strategic-merge schemas baked",
        messageFormat: "Embedded {0} GVK schemas (wire format v{1}, source hash {2})",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>SMP004 — Error, schemas.json wire version mismatch.</summary>
    public static readonly DiagnosticDescriptor WireFormatVersionMismatch = new(
        id: "SMP004",
        title: "Schema wire format version mismatch",
        messageFormat: "{0}; this build of KubernetesClient.StrategicPatch expects v{1}. "
            + "Re-run scripts/regen-schemas.sh after updating the library.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SMP005 — Error, catch-all for unexpected generator failures.</summary>
    public static readonly DiagnosticDescriptor GeneratorThrew = new(
        id: "SMP005",
        title: "Strategic-merge-patch source generator threw",
        messageFormat: "The strategic-merge-patch source generator threw {0}: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
