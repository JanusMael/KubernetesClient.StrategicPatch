using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;
using KubernetesClient.StrategicPatch.Tests.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Concurrency stress tests. The library's contract is "safe to call concurrently with different
/// inputs"; these tests run hundreds of parallel calls against shared schemas, shared options,
/// and the global ActivitySource to confirm no shared state corrupts.
/// </summary>
[TestClass]
public sealed class ConcurrencyStressTests
{
    private const int Parallelism = 64;
    private const int IterationsPerThread = 25;

    [TestMethod]
    public void TwoWayMerge_ConcurrentCalls_AllProduceTheSamePatch()
    {
        // Single shared (read-only) input pair; many threads diff it concurrently. Engines must
        // never mutate inputs, so every result must be identical.
        var original = (JsonObject)JsonNode.Parse(
            """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3,"template":{"spec":{"containers":[{"name":"web","image":"nginx:1"}]}}}}""")!;
        var modified = (JsonObject)JsonNode.Parse(
            """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5,"template":{"spec":{"containers":[{"name":"web","image":"nginx:2"}]}}}}""")!;

        var options = new StrategicPatchOptions { SchemaProvider = TestSchemas.DeploymentSchemaProvider() };
        var canonical = TwoWayMerge.CreateTwoWayMergePatch(original, modified, options)
            ?? throw new InvalidOperationException("expected a patch");
        var canonicalJson = canonical.ToJsonString();

        var bag = new ConcurrentBag<string>();
        Parallel.For(0, Parallelism, _ =>
        {
            for (var i = 0; i < IterationsPerThread; i++)
            {
                var result = TwoWayMerge.CreateTwoWayMergePatch(original, modified, options);
                bag.Add(result!.ToJsonString());
            }
        });

        Assert.HasCount(Parallelism * IterationsPerThread, bag);
        Assert.IsTrue(bag.All(s => s == canonicalJson),
            $"Some concurrent diffs disagreed with canonical:{Environment.NewLine}  expected: {canonicalJson}{Environment.NewLine}  saw: {bag.GroupBy(x => x).Select(g => g.Key).First(s => s != canonicalJson)}");
    }

    [TestMethod]
    public void Apply_ConcurrentCalls_AllProduceTheSameResult()
    {
        var original = (JsonObject)JsonNode.Parse(
            """{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"x"},"data":{"FOO":"1","BAR":"2","BAZ":"3"}}""")!;
        var patch = (JsonObject)JsonNode.Parse(
            """{"data":{"BAZ":null,"NEW":"4"}}""")!;

        var canonical = PatchApply.StrategicMergePatch(original, patch).ToJsonString();
        var bag = new ConcurrentBag<string>();
        Parallel.For(0, Parallelism, _ =>
        {
            for (var i = 0; i < IterationsPerThread; i++)
            {
                bag.Add(PatchApply.StrategicMergePatch(original, patch).ToJsonString());
            }
        });

        Assert.HasCount(Parallelism * IterationsPerThread, bag);
        Assert.IsTrue(bag.All(s => s == canonical));
    }

    [TestMethod]
    public void TypedExtensions_ConcurrentCalls_NoSharedStateCorruption()
    {
        var original = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };

        // Each thread builds its own modified with a thread-local replicas value, ensuring the
        // result is uniquely identifiable. We assert the patch contains the right replicas count
        // for each call — a shared-state bug in serialisation or schema-resolution caches would
        // mix them up.
        var bag = new ConcurrentBag<(int desired, int observed)>();
        Parallel.For(1, Parallelism + 1, threadIndex =>
        {
            for (var i = 0; i < IterationsPerThread; i++)
            {
                var desired = threadIndex * 1000 + i;
                var modified = new V1Deployment
                {
                    ApiVersion = "apps/v1", Kind = "Deployment",
                    Metadata = new V1ObjectMeta { Name = "api" },
                    Spec = new V1DeploymentSpec { Replicas = desired },
                };
                var result = original.CreateStrategicPatch(modified);
                var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
                var observed = (int)patch["spec"]!["replicas"]!;
                bag.Add((desired, observed));
            }
        });

        Assert.HasCount(Parallelism * IterationsPerThread, bag);
        Assert.IsTrue(bag.All(pair => pair.desired == pair.observed),
            $"Mismatch: {bag.First(p => p.desired != p.observed)}");
    }

    private static readonly System.Threading.AsyncLocal<string?> ActivityCorrelation = new();

    [TestMethod]
    public void ActivityListener_UnderConcurrentLoad_CorrelationStaysIsolated()
    {
        // Each task gets its own listener with a unique correlation. The listener stamps the
        // tag from the creating flow's AsyncLocal value (not the listener's own — that races
        // when multiple listeners' ActivityStarted callbacks fire on the same activity).
        // Filter on stop matches only this listener's own activities.
        var faults = new ConcurrentBag<string>();

        Parallel.For(0, Parallelism, taskIndex =>
        {
            var captured = new List<Activity>();
            var correlation = Guid.NewGuid().ToString("N");
            ActivityCorrelation.Value = correlation;

            using var listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == StrategicPatchActivity.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (ActivityCorrelation.Value is { } id)
                    {
                        activity.SetTag("test.correlation", id);
                    }
                },
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem("test.correlation") as string == correlation)
                    {
                        lock (captured) captured.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(listener);

            for (var i = 0; i < IterationsPerThread; i++)
            {
                var orig = (JsonObject)JsonNode.Parse(
                    "{\"apiVersion\":\"v1\",\"kind\":\"Pod\",\"spec\":{\"replicas\":" + taskIndex + "}}")!;
                var mod = (JsonObject)JsonNode.Parse(
                    "{\"apiVersion\":\"v1\",\"kind\":\"Pod\",\"spec\":{\"replicas\":" + (taskIndex + 1) + "}}")!;
                _ = TwoWayMerge.CreateTwoWayMergePatch(orig, mod);
            }

            // Each call produces exactly one smp.compute_two_way activity.
            if (captured.Count != IterationsPerThread)
            {
                faults.Add($"task {taskIndex}: expected {IterationsPerThread} captures, got {captured.Count}");
            }
        });

        Assert.IsTrue(faults.IsEmpty,
            "Activity correlation isolation broke under load: " + string.Join("; ", faults));
    }

    [TestMethod]
    public void SchemaProviderCache_ConcurrentResolution_NoExceptions()
    {
        // KubernetesEntityResolver.Cache is ConcurrentDictionary-backed; hammer it from many
        // threads simultaneously and assert no race exceptions.
        Parallel.For(0, Parallelism, threadIndex =>
        {
            for (var i = 0; i < IterationsPerThread; i++)
            {
                _ = KubernetesClient.StrategicPatch.Schema.KubernetesEntityResolver.TryGetGvk(typeof(V1Deployment));
                _ = KubernetesClient.StrategicPatch.Schema.KubernetesEntityResolver.TryGetGvk(typeof(V1Pod));
                _ = KubernetesClient.StrategicPatch.Schema.KubernetesEntityResolver.TryGetGvk(typeof(V1ConfigMap));
                _ = KubernetesClient.StrategicPatch.Schema.KubernetesEntityResolver.TryGetGvk(typeof(V1Service));
            }
        });
    }
}
