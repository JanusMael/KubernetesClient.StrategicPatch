using System.Diagnostics;

namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Identity record emitted by the source generator alongside its
/// <see cref="ISchemaProvider"/>. Lets a consumer answer "did the generator run? when?
/// against what schema snapshot?" at runtime without spelunking through <c>obj/</c>.
/// </summary>
/// <param name="GeneratedAtUtc">Wall-clock time the generator emitted the provider.</param>
/// <param name="SchemaWireFormatVersion">Mirrors <c>SchemaWireFormat.CurrentVersion</c> at generation time. Bumps require regeneration.</param>
/// <param name="GvkCount">Number of GVKs in the generator's emitted schema set.</param>
/// <param name="LibraryVersion">Informational version of <c>KubernetesClient.StrategicPatch</c> the generator was compiled against.</param>
/// <param name="SnapshotContentHash">SHA-256 of the schemas.json bytes the generator consumed (lower-case hex). Lets a CI step assert "this generator output corresponds exactly to this snapshot."</param>
public sealed record GenerationManifest(
    DateTimeOffset GeneratedAtUtc,
    int SchemaWireFormatVersion,
    int GvkCount,
    string LibraryVersion,
    string SnapshotContentHash)
{
    private int _emitted;

    /// <summary>
    /// Fires the <c>smp.generator.manifest</c> event on the library's <see cref="ActivitySource"/>
    /// exactly once per process, even if called from many threads. Generator-emitted providers
    /// should call this in their static constructor so OTel listeners observing the activity
    /// source pick up the manifest without consumer cooperation.
    /// </summary>
    public void EmitOnce()
    {
        if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0)
        {
            return;
        }
        var tags = new ActivityTagsCollection
        {
            ["smp.generator.generated_at"] = GeneratedAtUtc.ToString("O"),
            ["smp.generator.wire_format_version"] = SchemaWireFormatVersion,
            ["smp.generator.gvk_count"] = GvkCount,
            ["smp.generator.library_version"] = LibraryVersion,
            ["smp.generator.snapshot_hash"] = SnapshotContentHash,
        };
        // The activity source has no Activity context for "happened once at startup" — we use
        // an event on a synthetic short-lived Activity so listeners that only subscribe to
        // ActivityStopped still see the data.
        using var activity = StrategicPatchActivity.Source.StartActivity(
            "smp.generator.manifest", ActivityKind.Internal);
        if (activity is null)
        {
            return; // No listeners; silent no-op.
        }
        activity.AddEvent(new ActivityEvent("smp.generator.manifest", tags: tags));
    }
}

/// <summary>
/// Marker interface implemented by source-generator-emitted schema providers. The diff and
/// apply engines tag their activities with the manifest's snapshot hash so traces can
/// correlate runtime patches to a specific generator output.
/// </summary>
public interface IManifestedSchemaProvider : ISchemaProvider
{
    /// <summary>The generator's emit identity. Stable for the lifetime of the assembly.</summary>
    GenerationManifest Manifest { get; }
}
