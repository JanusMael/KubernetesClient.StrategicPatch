using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace KubernetesClient.StrategicPatch.SourceGenerators;

/// <summary>
/// Roslyn incremental generator that emits a compile-time-baked
/// <c>GeneratedStrategicPatchSchemaProvider</c> for the consuming compilation. The provider
/// covers the same Kubernetes built-in GVK set as the runtime library's embedded snapshot but
/// materialised as static C# data — no runtime JSON parse, AOT-friendly, single allocation
/// for the dispatch dictionary.
/// </summary>
/// <remarks>
/// <para>The generator embeds the runtime library's <c>schemas.json</c> as its own resource at
/// build time (linked via the .csproj). Re-baking the snapshot via
/// <c>scripts/regen-schemas.sh</c> updates both inputs in lockstep.</para>
/// <para>Diagnostics are emitted from the catalog in <c>SmpDiagnostics</c> (runtime lib).</para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StrategicMergePatchGenerator : IIncrementalGenerator
{
    private const string EmbeddedResourceName =
        "KubernetesClient.StrategicPatch.SourceGenerators.schemas.json";

    /// <summary>The descriptor IDs in <see cref="DiagnosticDescriptors"/> mirror the runtime
    /// catalog's <c>SmpDiagnostics.SMPxxx</c> identifiers.</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The generator's behaviour does not depend on consumer syntax — the schema set is fully
        // determined by the embedded resource at compile time. We trigger off CompilationProvider
        // (always available) so the SourceProductionContext gives us ReportDiagnostic, which the
        // post-initialisation context lacks.
        context.RegisterSourceOutput(context.CompilationProvider, static (ctx, _) => EmitProvider(ctx));
    }

    private static void EmitProvider(SourceProductionContext context)
    {
        try
        {
            var bytes = ReadEmbeddedSchemas();
            if (bytes is null || bytes.Length == 0)
            {
                // Snapshot hasn't been baked yet (clean clone before first regen). Emit an
                // empty provider so consumers compile cleanly; runtime resolution returns null
                // and falls through to RFC 7396.
                context.AddSource(
                    "GeneratedStrategicPatchSchemaProvider.g.cs",
                    SourceWriter.EmitEmpty(GeneratorVersion));
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.EmbeddedSchemasReady, Location.None,
                    0, WireFormat.CurrentVersion, "(empty)"));
                return;
            }

            var doc = WireFormat.Read(bytes);
            var source = SourceWriter.Emit(bytes, doc, GeneratorVersion);
            context.AddSource("GeneratedStrategicPatchSchemaProvider.g.cs", source);
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.EmbeddedSchemasReady, Location.None,
                doc.Schemas?.Count ?? 0, doc.Version, ShortHashFor(bytes)));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("wire version"))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.WireFormatVersionMismatch, Location.None,
                ex.Message, WireFormat.CurrentVersion));
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.GeneratorThrew, Location.None,
                ex.GetType().Name, ex.Message));
        }
    }

    private static byte[]? ReadEmbeddedSchemas()
    {
        var asm = typeof(StrategicMergePatchGenerator).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ShortHashFor(byte[] bytes)
    {
        // 8-character prefix of the snapshot's SHA-256 — enough for diagnostic readability,
        // not security. The full hash lands on the GenerationManifest via SourceWriter.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        return BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static readonly string GeneratorVersion =
        typeof(StrategicMergePatchGenerator).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StrategicMergePatchGenerator).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
