namespace KubernetesClient.StrategicPatch.Diagnostics;

/// <summary>
/// Pre-allocated diagnostic catalog for the strategic-merge-patch source generator. The runtime
/// library deliberately does <i>not</i> reference <c>Microsoft.CodeAnalysis</c>; the generator
/// project takes these <see cref="SmpDescriptor"/> records and constructs
/// <c>Microsoft.CodeAnalysis.DiagnosticDescriptor</c> instances at compile time.
/// </summary>
/// <remarks>
/// <para>IDs are stable across library versions: callers can <c>nowarn:SMP002</c> in their
/// build configuration without that suppression silently re-targeting a different diagnostic.</para>
/// <para>Severity strings use the same names as
/// <c>Microsoft.CodeAnalysis.DiagnosticSeverity</c> (<c>Hidden</c>, <c>Info</c>, <c>Warning</c>,
/// <c>Error</c>) so the generator code can do a one-line mapping.</para>
/// </remarks>
public static class SmpDiagnostics
{
    /// <summary>Diagnostic category — mirrors the <see cref="StrategicPatchActivity.Name"/>.</summary>
    public const string Category = StrategicPatchActivity.Name;

    /// <summary>SMP001 — Info. Reported once per generator run, summarising the embedded schema set.</summary>
    public static readonly SmpDescriptor EmbeddedSchemasReady = new(
        Id: "SMP001",
        Title: "Embedded strategic-merge schemas baked",
        MessageFormat: "Embedded {0} GVK schemas (wire format v{1}, source hash {2})",
        Severity: "Info");

    /// <summary>SMP002 — Warning. Type referenced by patch APIs but missing the K8s entity attribute.</summary>
    public static readonly SmpDescriptor MissingKubernetesEntityAttribute = new(
        Id: "SMP002",
        Title: "Type lacks [KubernetesEntity]",
        MessageFormat: "'{0}' is referenced by KubernetesClient.StrategicPatch but is not annotated with [KubernetesEntity]; "
            + "no schema can be resolved for it. The diff will fall through to RFC 7396 semantics.",
        Severity: "Warning");

    /// <summary>SMP003 — Warning. Schema not found for a GVK the generator was asked to bake.</summary>
    public static readonly SmpDescriptor SchemaNotFoundForGvk = new(
        Id: "SMP003",
        Title: "No strategic-merge schema available for GVK",
        MessageFormat: "No schema available for '{0}'. Resolve calls for this GVK will fall through to RFC 7396 semantics. "
            + "Add the GVK's OpenAPI v3 spec under reference/kubernetes/openapi-spec/v3/ and re-run scripts/regen-schemas.sh.",
        Severity: "Warning");

    /// <summary>SMP004 — Error. The schemas.json snapshot the generator consumed does not match the library's wire format version.</summary>
    public static readonly SmpDescriptor WireFormatVersionMismatch = new(
        Id: "SMP004",
        Title: "Schema wire format version mismatch",
        MessageFormat: "schemas.json declares wire format v{0} but the referenced KubernetesClient.StrategicPatch library expects v{1}. "
            + "Re-run scripts/regen-schemas.sh against the current library version.",
        Severity: "Error");

    /// <summary>SMP005 — Error. Catch-all for unexpected generator failures, with the underlying exception type and message.</summary>
    public static readonly SmpDescriptor GeneratorThrew = new(
        Id: "SMP005",
        Title: "Strategic-merge-patch source generator threw",
        MessageFormat: "The strategic-merge-patch source generator threw {0}: {1}",
        Severity: "Error");

    /// <summary>Returns every descriptor in the catalog. Useful for IDE 'show all diagnostics' UIs and for generator analyzer registration.</summary>
    public static IReadOnlyList<SmpDescriptor> All { get; } =
    [
        EmbeddedSchemasReady,
        MissingKubernetesEntityAttribute,
        SchemaNotFoundForGvk,
        WireFormatVersionMismatch,
        GeneratorThrew,
    ];

    /// <summary>Formats a descriptor's <see cref="SmpDescriptor.MessageFormat"/> against the supplied arguments, using <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.</summary>
    public static string Format(SmpDescriptor descriptor, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, descriptor.MessageFormat, args);
    }
}

/// <summary>
/// Plain-data record describing a generator diagnostic. Kept POCO so the runtime library does
/// not pull in <c>Microsoft.CodeAnalysis</c>.
/// </summary>
public sealed record SmpDescriptor(string Id, string Title, string MessageFormat, string Severity);
