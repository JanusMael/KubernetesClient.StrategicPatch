using System.Diagnostics;
using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Stage 8 corpus: OTel + structured logging. Verifies the activity surface
/// (<c>smp.compute_two_way</c> / <c>smp.compute_three_way</c> / <c>smp.apply</c>) and the tag
/// shape exposed to listeners (<c>smp.gvk</c>, <c>smp.empty</c>, <c>smp.patch.bytes</c>,
/// <c>smp.schema_miss_count</c>), plus the per-call <c>Information</c> summary on the
/// caller-supplied <see cref="ILogger"/>.
/// </summary>
[TestClass]
public sealed class ObservabilityTests
{
    [TestMethod]
    public void TwoWay_Activity_HasGvkAndBytesAndEmptyTags()
    {
        var captured = CaptureActivities("smp.compute_two_way");
        try
        {
            var original = new V1Deployment
            {
                ApiVersion = "apps/v1", Kind = "Deployment",
                Metadata = new V1ObjectMeta { Name = "api" },
                Spec = new V1DeploymentSpec { Replicas = 3 },
            };
            var modified = new V1Deployment
            {
                ApiVersion = "apps/v1", Kind = "Deployment",
                Metadata = new V1ObjectMeta { Name = "api" },
                Spec = new V1DeploymentSpec { Replicas = 5 },
            };
            _ = original.CreateStrategicPatch(modified, new StrategicPatchOptions
            {
                SchemaProvider = StrategicMerge.TestSchemas.DeploymentSchemaProvider(),
            });
        }
        finally { captured.Listener.Dispose(); }

        Assert.HasCount(1, captured.Activities);
        var act = captured.Activities[0];
        Assert.AreEqual("smp.compute_two_way", act.OperationName);
        Assert.AreEqual("apps/v1/Deployment", act.GetTagItem("smp.gvk"));
        Assert.IsFalse((bool)act.GetTagItem("smp.empty")!);
        Assert.IsNotNull(act.GetTagItem("smp.patch.bytes"));
        // Schema is fully populated for this path → no misses.
        Assert.AreEqual(0, act.GetTagItem(SchemaMissTracking.CountTag));
    }

    [TestMethod]
    public void TwoWay_Activity_NoDiff_TagsEmptyTrueAndBytes2()
    {
        var captured = CaptureActivities("smp.compute_two_way");
        try
        {
            var doc = new V1Deployment
            {
                ApiVersion = "apps/v1", Kind = "Deployment",
                Metadata = new V1ObjectMeta { Name = "api" },
                Spec = new V1DeploymentSpec { Replicas = 3 },
            };
            _ = doc.CreateStrategicPatch(doc);
        }
        finally { captured.Listener.Dispose(); }

        var act = captured.Activities.Single();
        Assert.IsTrue((bool)act.GetTagItem("smp.empty")!);
        Assert.AreEqual(2, act.GetTagItem("smp.patch.bytes"));
    }

    [TestMethod]
    public void TwoWay_Activity_SchemaMiss_EmitsEventAndIncrementsCount()
    {
        // No SchemaProvider → every recursive level into a non-leaf object is a schema miss.
        var captured = CaptureActivities("smp.compute_two_way");
        try
        {
            var original = (JsonObject)JsonNode.Parse(
                """{"apiVersion":"v1","kind":"Pod","spec":{"a":1,"b":{"c":2}}}""")!;
            var modified = (JsonObject)JsonNode.Parse(
                """{"apiVersion":"v1","kind":"Pod","spec":{"a":2,"b":{"c":3}}}""")!;
            _ = TwoWayMerge.CreateTwoWayMergePatch(original, modified, new StrategicPatchOptions());
        }
        finally { captured.Listener.Dispose(); }

        var act = captured.Activities.Single();
        var missCount = (int?)act.GetTagItem(SchemaMissTracking.CountTag);
        Assert.IsNotNull(missCount);
        Assert.IsGreaterThanOrEqualTo(1, missCount.Value);

        var missEvents = act.Events.Count(e => e.Name == "smp.schema_miss");
        Assert.AreEqual(missCount.Value, missEvents);
    }

    [TestMethod]
    public void ThreeWay_Activity_HasGvkAndBytesAndEmptyTags()
    {
        var captured = CaptureActivities("smp.compute_three_way");
        try
        {
            var doc = new V1Deployment
            {
                ApiVersion = "apps/v1", Kind = "Deployment",
                Metadata = new V1ObjectMeta { Name = "api" },
                Spec = new V1DeploymentSpec { Replicas = 3 },
            };
            var modified = new V1Deployment
            {
                ApiVersion = "apps/v1", Kind = "Deployment",
                Metadata = new V1ObjectMeta { Name = "api" },
                Spec = new V1DeploymentSpec { Replicas = 5 },
            };
            _ = doc.CreateThreeWayStrategicPatch(modified, doc);
        }
        finally { captured.Listener.Dispose(); }

        var act = captured.Activities.Single();
        Assert.AreEqual("smp.compute_three_way", act.OperationName);
        Assert.AreEqual("apps/v1/Deployment", act.GetTagItem("smp.gvk"));
        Assert.IsNotNull(act.GetTagItem("smp.patch.bytes"));
    }

    [TestMethod]
    public void Apply_Activity_HasGvkAndBytesTags()
    {
        var captured = CaptureActivities("smp.apply");
        try
        {
            var original = (JsonObject)JsonNode.Parse(
                """{"apiVersion":"v1","kind":"Pod","spec":{"a":1}}""")!;
            var patch = (JsonObject)JsonNode.Parse("""{"spec":{"a":2}}""")!;
            _ = PatchApply.StrategicMergePatch(original, patch);
        }
        finally { captured.Listener.Dispose(); }

        var act = captured.Activities.Single();
        Assert.AreEqual("smp.apply", act.OperationName);
        Assert.AreEqual("v1/Pod", act.GetTagItem("smp.gvk"));
        Assert.IsNotNull(act.GetTagItem("smp.patch.bytes"));
    }

    [TestMethod]
    public void Logger_Information_FiresWithGvkAndBytes()
    {
        var logger = new ListLogger();
        var doc = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };
        var modified = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 5 },
        };

        _ = doc.CreateStrategicPatch(modified, new StrategicPatchOptions { Logger = logger });

        var info = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Information);
        Assert.IsNotNull(info);
        StringAssert.Contains(info.Message, "smp.compute_two_way");
        StringAssert.Contains(info.Message, "apps/v1/Deployment");
        StringAssert.Contains(info.Message, "empty=false");
    }

    [TestMethod]
    public void Logger_Debug_FiresOnAddAndDeletePaths()
    {
        var logger = new ListLogger();
        var original = (JsonObject)JsonNode.Parse("""{"a":1,"b":2}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a":1,"c":3}""")!;
        _ = TwoWayMerge.CreateTwoWayMergePatch(original, modified, new StrategicPatchOptions { Logger = logger });

        var debugMessages = logger.Entries.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message).ToList();
        Assert.IsTrue(debugMessages.Any(m => m.StartsWith("smp.add", StringComparison.Ordinal)),
            $"No smp.add log: {string.Join(" | ", debugMessages)}");
        Assert.IsTrue(debugMessages.Any(m => m.StartsWith("smp.delete", StringComparison.Ordinal)),
            $"No smp.delete log: {string.Join(" | ", debugMessages)}");
    }

    [TestMethod]
    public void ActivitySource_NameMatchesConstant()
    {
        Assert.AreEqual(StrategicPatchActivity.Name, StrategicPatchActivity.Source.Name);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private sealed record CapturedActivities(ActivityListener Listener, List<Activity> Activities);

    private static CapturedActivities CaptureActivities(string expectedOperationName)
    {
        var list = new List<Activity>();
        // Tag every test-driven call so the listener can ignore activities started by
        // parallel tests sharing the same ActivitySource.
        var correlation = Guid.NewGuid().ToString("N");
        TestActivityCorrelation.Value = correlation;
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == StrategicPatchActivity.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                // Stamp the correlation onto the activity at start so it survives until stop.
                if (TestActivityCorrelation.Value is { } id)
                {
                    activity.SetTag("test.correlation", id);
                }
            },
            ActivityStopped = activity =>
            {
                if (activity.OperationName != expectedOperationName)
                {
                    return;
                }
                if (activity.GetTagItem("test.correlation") as string != correlation)
                {
                    return;
                }
                lock (list) { list.Add(activity); }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return new CapturedActivities(listener, list);
    }

    /// <summary>
    /// Per-async-flow correlation token used to pin captured activities to their owning test.
    /// Survives across awaits via <see cref="AsyncLocal{T}"/>; the listener is invoked on the
    /// same flow as the test method.
    /// </summary>
    private static class TestActivityCorrelation
    {
        private static readonly System.Threading.AsyncLocal<string?> Token = new();

        public static string? Value
        {
            get => Token.Value;
            set => Token.Value = value;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
