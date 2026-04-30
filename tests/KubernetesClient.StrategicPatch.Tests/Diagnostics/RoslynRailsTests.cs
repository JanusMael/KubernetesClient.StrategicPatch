using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Diagnostics;
using KubernetesClient.StrategicPatch.Schema;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests.Diagnostics;

/// <summary>
/// Tests for the iteration-loop rails put in place ahead of Stage 13: the diagnostic catalog,
/// the generation-manifest contract, the schema-provider debug dumper, and the activity tagging
/// that lets traces correlate runtime diffs back to the provider that served them.
/// </summary>
[TestClass]
public sealed class RoslynRailsTests
{
    // ---- SmpDiagnostics ---------------------------------------------------------------------

    [TestMethod]
    public void Diagnostics_AllDescriptors_HaveStableIdsAndCategories()
    {
        // Stable IDs let consumers `nowarn:SMP002` without that suppression silently re-targeting.
        Assert.AreEqual("SMP001", SmpDiagnostics.EmbeddedSchemasReady.Id);
        Assert.AreEqual("SMP002", SmpDiagnostics.MissingKubernetesEntityAttribute.Id);
        Assert.AreEqual("SMP003", SmpDiagnostics.SchemaNotFoundForGvk.Id);
        Assert.AreEqual("SMP004", SmpDiagnostics.WireFormatVersionMismatch.Id);
        Assert.AreEqual("SMP005", SmpDiagnostics.GeneratorThrew.Id);

        foreach (var d in SmpDiagnostics.All)
        {
            Assert.IsTrue(d.Id.StartsWith("SMP", StringComparison.Ordinal));
            Assert.IsFalse(string.IsNullOrEmpty(d.Title));
            Assert.IsFalse(string.IsNullOrEmpty(d.MessageFormat));
            Assert.IsTrue(
                d.Severity is "Hidden" or "Info" or "Warning" or "Error",
                $"Unrecognised severity '{d.Severity}' on {d.Id}");
        }
    }

    [TestMethod]
    public void Diagnostics_All_ContainsEveryDescriptor_NoDuplicates()
    {
        var ids = SmpDiagnostics.All.Select(d => d.Id).ToArray();
        var unique = new HashSet<string>(ids);
        Assert.HasCount(ids.Length, unique);
        Assert.HasCount(5, unique);
    }

    [TestMethod]
    public void Diagnostics_Format_PopulatesPlaceholders()
    {
        var msg = SmpDiagnostics.Format(SmpDiagnostics.EmbeddedSchemasReady, 78, 1, "abc123");
        StringAssert.Contains(msg, "78");
        StringAssert.Contains(msg, "v1");
        StringAssert.Contains(msg, "abc123");
    }

    [TestMethod]
    public void Diagnostics_Format_NullDescriptor_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SmpDiagnostics.Format(null!));
    }

    // ---- GenerationManifest -----------------------------------------------------------------

    [TestMethod]
    public void GenerationManifest_RecordEqualityIsValueBased()
    {
        var a = new GenerationManifest(DateTimeOffset.Parse("2026-04-30T00:00:00Z"), 1, 78, "0.1.0", "abc");
        var b = new GenerationManifest(DateTimeOffset.Parse("2026-04-30T00:00:00Z"), 1, 78, "0.1.0", "abc");
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void GenerationManifest_EmitOnce_IsIdempotent()
    {
        var manifest = new GenerationManifest(DateTimeOffset.UtcNow, 1, 1, "0.1.0", "deadbeef");

        // Per-flow correlation so this listener doesn't capture events fired by parallel tests
        // (the no-listener test, for instance, will trigger StartActivity → activity-stop here
        // because our listener is registered globally).
        var captured = new List<Activity>();
        var correlation = Guid.NewGuid().ToString("N");
        TestActivityCorrelation.Value = correlation;
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == StrategicPatchActivity.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a =>
            {
                if (TestActivityCorrelation.Value is { } id)
                {
                    a.SetTag("test.correlation", id);
                }
            },
            ActivityStopped = a =>
            {
                if (a.OperationName == "smp.generator.manifest"
                    && (a.GetTagItem("test.correlation") as string) == correlation)
                {
                    lock (captured) captured.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // Repeated calls must fire the event exactly once.
        manifest.EmitOnce();
        manifest.EmitOnce();
        manifest.EmitOnce();

        Assert.HasCount(1, captured);
        var evt = captured[0].Events.Single(e => e.Name == "smp.generator.manifest");
        Assert.AreEqual("deadbeef", evt.Tags.Single(t => t.Key == "smp.generator.snapshot_hash").Value);
    }

    [TestMethod]
    public void GenerationManifest_EmitOnce_NoListener_NoOp()
    {
        // No exception when no one's listening; matches the "Activity is null" defensive path.
        var manifest = new GenerationManifest(DateTimeOffset.UtcNow, 1, 0, "0.0.0", "xx");
        manifest.EmitOnce();
    }

    // ---- IManifestedSchemaProvider activity tagging -----------------------------------------

    [TestMethod]
    public void Activity_Tags_SchemaSourceWithProviderName()
    {
        var captured = CaptureSingleTwoWayActivity(() =>
        {
            var orig = NewDeploy(3);
            var mod = NewDeploy(5);
            _ = orig.CreateStrategicPatch(mod, new StrategicPatchOptions
            {
                SchemaProvider = EmbeddedSchemaProvider.Shared,
            });
        });
        Assert.AreEqual("Embedded", captured.GetTagItem("smp.schema_source"));
    }

    [TestMethod]
    public void Activity_Tags_CompositeProviderName()
    {
        var composite = new CompositeSchemaProvider(
            EmbeddedSchemaProvider.Shared,
            new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode>()));
        var captured = CaptureSingleTwoWayActivity(() =>
        {
            var orig = NewDeploy(3);
            var mod = NewDeploy(5);
            _ = orig.CreateStrategicPatch(mod, new StrategicPatchOptions { SchemaProvider = composite });
        });
        var name = captured.GetTagItem("smp.schema_source") as string;
        Assert.IsNotNull(name);
        StringAssert.Contains(name!, "Composite[");
        StringAssert.Contains(name!, "Embedded");
        StringAssert.Contains(name!, "InMemory");
    }

    [TestMethod]
    public void Activity_Tags_ManifestHash_WhenProviderIsManifested()
    {
        var manifested = new ManifestedFakeProvider();
        var captured = CaptureSingleTwoWayActivity(() =>
        {
            var orig = NewDeploy(3);
            var mod = NewDeploy(5);
            _ = orig.CreateStrategicPatch(mod, new StrategicPatchOptions { SchemaProvider = manifested });
        });
        Assert.AreEqual("ManifestedFakeProvider", captured.GetTagItem("smp.schema_source"));
        Assert.AreEqual("fake-hash-1234", captured.GetTagItem("smp.generator.manifest_hash"));
        Assert.AreEqual(0, captured.GetTagItem("smp.generator.gvk_count"));
    }

    // ---- SchemaProviderDebug.DumpTo (Conditional("DEBUG")) ----------------------------------

#if DEBUG
    [TestMethod]
    public void SchemaProviderDebug_DumpTo_RendersTreeForRequestedGvks()
    {
        if (EmbeddedSchemaProvider.Shared.Count == 0)
        {
            Assert.Inconclusive("schemas.json is empty — run scripts/regen-schemas.sh first.");
        }

        var sw = new StringWriter();
        SchemaProviderDebug.DumpTo(
            EmbeddedSchemaProvider.Shared,
            new[]
            {
                new GroupVersionKind("apps", "v1", "Deployment"),
                new GroupVersionKind(string.Empty, "v1", "ConfigMap"),
            },
            sw);
        var output = sw.ToString();

        StringAssert.Contains(output, "Embedded");
        StringAssert.Contains(output, "apps/v1/Deployment");
        StringAssert.Contains(output, "v1/ConfigMap");
        StringAssert.Contains(output, "spec");
        StringAssert.Contains(output, "containers");
        StringAssert.Contains(output, "rendered 2/2");
    }

    [TestMethod]
    public void SchemaProviderDebug_DumpTo_RendersManifestWhenAvailable()
    {
        var sw = new StringWriter();
        SchemaProviderDebug.DumpTo(
            new ManifestedFakeProvider(),
            Array.Empty<GroupVersionKind>(),
            sw);
        StringAssert.Contains(sw.ToString(), "fake-hash-1234");
        StringAssert.Contains(sw.ToString(), "manifest:");
    }

    [TestMethod]
    public void SchemaProviderDebug_DumpTo_NotFoundGvk_RendersExplicitly()
    {
        var sw = new StringWriter();
        SchemaProviderDebug.DumpTo(
            new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode>()),
            new[] { new GroupVersionKind("none", "v1", "Nothing") },
            sw);
        StringAssert.Contains(sw.ToString(), "(not found)");
    }

    [TestMethod]
    public void SchemaProviderDebug_EveryEmbeddedGvk_ReturnsTheSnapshotKeys()
    {
        var keys = SchemaProviderDebug.EveryEmbeddedGvk();
        Assert.HasCount(EmbeddedSchemaProvider.Shared.Count, keys);
    }
#else
    [TestMethod]
    public void SchemaProviderDebug_DumpTo_NoOpInRelease()
    {
        // [Conditional("DEBUG")] means the call evaporates in Release: even passing an
        // already-disposed writer must not throw.
        var disposed = new StringWriter();
        disposed.Dispose();
        SchemaProviderDebug.DumpTo(
            EmbeddedSchemaProvider.Shared,
            new[] { new GroupVersionKind("apps", "v1", "Deployment") },
            disposed);
        // Reaching this line is the assertion: no exception.
    }
#endif

    // ---- helpers ----------------------------------------------------------------------------

    private static V1Deployment NewDeploy(int replicas) => new()
    {
        ApiVersion = "apps/v1", Kind = "Deployment",
        Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "default" },
        Spec = new V1DeploymentSpec { Replicas = replicas },
    };

    private static Activity CaptureSingleTwoWayActivity(Action act)
    {
        var captured = new List<Activity>();
        var correlation = Guid.NewGuid().ToString("N");
        TestActivityCorrelation.Value = correlation;
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == StrategicPatchActivity.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a =>
            {
                if (TestActivityCorrelation.Value is { } id)
                {
                    a.SetTag("test.correlation", id);
                }
            },
            ActivityStopped = a =>
            {
                if (a.OperationName == "smp.compute_two_way"
                    && (a.GetTagItem("test.correlation") as string) == correlation)
                {
                    lock (captured) captured.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        act();

        Assert.HasCount(1, captured);
        return captured[0];
    }

    private static class TestActivityCorrelation
    {
        private static readonly System.Threading.AsyncLocal<string?> Token = new();
        public static string? Value
        {
            get => Token.Value;
            set => Token.Value = value;
        }
    }

    private sealed class ManifestedFakeProvider : IManifestedSchemaProvider
    {
        public GenerationManifest Manifest { get; } =
            new(DateTimeOffset.Parse("2026-04-30T00:00:00Z"), 1, 0, "0.1.0", "fake-hash-1234");
        public SchemaNode? GetRootSchema(GroupVersionKind gvk) => null;
        public string Name => nameof(ManifestedFakeProvider);
    }
}
