using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Tests.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Stage 7 corpus: typed-object boundary. Drives the diff/apply engines from
/// <c>KubernetesClient</c> models so callers get the same surface they already use everywhere
/// else in the SDK. Covers the four canonical scenarios called out in Plan-v2 — Deployment scale,
/// ConfigMap data-key delete, Service port reorder, Pod container env update — plus three-way
/// merge and the apply round-trip.
/// </summary>
[TestClass]
public sealed class StrategicPatchExtensionsTests
{
    private static StrategicPatchOptions WithSchema() => new()
    {
        SchemaProvider = TestSchemas.DeploymentSchemaProvider(),
    };

    // ---- Deployment scale ---------------------------------------------------------------------

    [TestMethod]
    public void DeploymentScale_OneFieldChange_ProducesMinimalPatch()
    {
        var original = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "default" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };
        var modified = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "default" },
            Spec = new V1DeploymentSpec { Replicas = 5 },
        };

        var result = original.CreateStrategicPatch(modified);

        Assert.IsFalse(result.IsEmpty);
        Assert.AreEqual(new GroupVersionKind("apps", "v1", "Deployment"), result.Gvk);
        Assert.AreEqual(V1Patch.PatchType.StrategicMergePatch, result.Patch.Type);

        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        Assert.IsTrue(JsonNodeEquality.DeepEquals(
            (JsonObject)JsonNode.Parse("""{"spec":{"replicas":5}}""")!, patch),
            $"Unexpected patch: {result.Patch.Content}");
        Assert.AreEqual(result.PayloadBytes, ((string)result.Patch.Content).Length);
    }

    [TestMethod]
    public void DeploymentScale_NoChange_IsEmptyResult_NotNullPatch()
    {
        var doc = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };
        var result = doc.CreateStrategicPatch(doc);

        Assert.IsTrue(result.IsEmpty);
        Assert.AreEqual("{}", (string)result.Patch.Content);
        Assert.AreEqual(2, result.PayloadBytes);
        Assert.AreEqual(V1Patch.PatchType.StrategicMergePatch, result.Patch.Type);
    }

    // ---- ConfigMap data-key delete ------------------------------------------------------------

    [TestMethod]
    public void ConfigMap_DataKeyDelete_EmitsNullMarker()
    {
        var original = new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = new V1ObjectMeta { Name = "settings" },
            Data = new Dictionary<string, string>
            {
                ["FOO"] = "1",
                ["BAR"] = "2",
                ["BAZ"] = "3",
            },
        };
        var modified = new V1ConfigMap
        {
            ApiVersion = "v1",
            Kind = "ConfigMap",
            Metadata = new V1ObjectMeta { Name = "settings" },
            Data = new Dictionary<string, string>
            {
                ["FOO"] = "1",
                ["BAR"] = "2",
            },
        };

        var result = original.CreateStrategicPatch(modified);
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        Assert.IsTrue(JsonNodeEquality.DeepEquals(
            (JsonObject)JsonNode.Parse("""{"data":{"BAZ":null}}""")!, patch),
            $"Unexpected patch: {result.Patch.Content}");
    }

    // ---- Service port reorder ----------------------------------------------------------------

    [TestMethod]
    public void Service_PortReorder_AtomicListReplace_WhenSchemaForcedAbsent()
    {
        // Stage 11 made the typed boundary auto-default to EmbeddedSchemaProvider.Shared, which
        // recognises Service.spec.ports as a keyed merge by 'port'. To exercise the historic
        // RFC 7396 atomic-replace fallback path, the caller must explicitly opt out by passing
        // an empty InMemorySchemaProvider. Reorder → whole-list replace under that override.
        var original = new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1ServiceSpec
            {
                Ports = new List<V1ServicePort>
                {
                    new() { Port = 80, Name = "http" },
                    new() { Port = 443, Name = "https" },
                },
            },
        };
        var modified = new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1ServiceSpec
            {
                Ports = new List<V1ServicePort>
                {
                    new() { Port = 443, Name = "https" },
                    new() { Port = 80, Name = "http" },
                },
            },
        };

        var result = original.CreateStrategicPatch(modified, new StrategicPatchOptions
        {
            SchemaProvider = new KubernetesClient.StrategicPatch.Schema.InMemorySchemaProvider(
                new Dictionary<GroupVersionKind, KubernetesClient.StrategicPatch.Schema.SchemaNode>()),
        });
        Assert.IsFalse(result.IsEmpty);

        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        var ports = (JsonArray)patch["spec"]!["ports"]!;
        Assert.HasCount(2, ports);
        Assert.AreEqual(443, (int)ports[0]!["port"]!);
    }

    // ---- Pod container env update ------------------------------------------------------------

    [TestMethod]
    public void Pod_ContainerEnvUpdate_DiffsByMergeKey()
    {
        var original = new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta { Name = "web" },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "web",
                        Image = "nginx",
                        Env = new List<V1EnvVar>
                        {
                            new() { Name = "DEBUG", Value = "false" },
                            new() { Name = "REGION", Value = "us-east-1" },
                        },
                    },
                },
            },
        };
        var modified = new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta { Name = "web" },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "web",
                        Image = "nginx",
                        Env = new List<V1EnvVar>
                        {
                            new() { Name = "DEBUG", Value = "true" },
                            new() { Name = "REGION", Value = "us-east-1" },
                        },
                    },
                },
            },
        };

        // Pod GVK isn't in our test deployment-only schema → list dispatches via RFC 7396 atomic.
        // To exercise the keyed-merge path, build a minimal Pod schema by aliasing the Deployment
        // container schema at the Pod root.
        var podSchema = new InMemoryPodSchema();
        var result = original.CreateStrategicPatch(modified, new StrategicPatchOptions { SchemaProvider = podSchema });
        Assert.IsFalse(result.IsEmpty);

        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        // The patch should target spec.containers and recurse into the env list of "web".
        var containers = patch["spec"]!["containers"] as JsonArray;
        Assert.IsNotNull(containers);
        Assert.HasCount(1, containers);
        Assert.AreEqual("web", (string)containers[0]!["name"]!);
        var env = containers[0]!["env"] as JsonArray;
        Assert.IsNotNull(env);
        // Only DEBUG changed; the merge-key carry should preserve "name".
        Assert.IsTrue(env.Any(e => (string?)e!["name"] == "DEBUG"));
    }

    // ---- Three-way merge through the typed boundary ------------------------------------------

    [TestMethod]
    public void ThreeWay_OnDeployment_PreservesServerAddedAnnotation()
    {
        var original = MakeDeploy(replicas: 3, annotations: new Dictionary<string, string> { ["caller-owned"] = "1" });
        var current = MakeDeploy(replicas: 3, annotations: new Dictionary<string, string>
        {
            ["caller-owned"] = "1",
            ["server-owned"] = "injected",
        });
        var modified = MakeDeploy(replicas: 5, annotations: new Dictionary<string, string> { ["caller-owned"] = "1" });

        var result = original.CreateThreeWayStrategicPatch(modified, current);
        Assert.IsFalse(result.IsEmpty);

        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        // Caller's only intent was to scale; server-injected annotation must survive (no delete).
        Assert.IsTrue(JsonNodeEquality.DeepEquals(
            (JsonObject)JsonNode.Parse("""{"spec":{"replicas":5}}""")!, patch),
            $"Unexpected patch: {result.Patch.Content}");
    }

    [TestMethod]
    public void ThreeWay_OnDeployment_ConflictThrowsByDefault()
    {
        var original = MakeDeploy(replicas: 3);
        var current = MakeDeploy(replicas: 4);
        var modified = MakeDeploy(replicas: 5);

        Assert.ThrowsExactly<StrategicMergePatchConflictException>(
            () => original.CreateThreeWayStrategicPatch(modified, current));
    }

    [TestMethod]
    public void ThreeWay_OnDeployment_OverwriteConflicts_TakesCallerSide()
    {
        var original = MakeDeploy(replicas: 3);
        var current = MakeDeploy(replicas: 4);
        var modified = MakeDeploy(replicas: 5);

        var result = original.CreateThreeWayStrategicPatch(modified, current,
            new StrategicPatchOptions { OverwriteConflicts = true });
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        Assert.AreEqual(5, (int)patch["spec"]!["replicas"]!);
    }

    // ---- Apply round-trip ---------------------------------------------------------------------

    [TestMethod]
    public void Apply_DeploymentReplicasChange_RoundTripsThroughTypedBoundary()
    {
        var original = MakeDeploy(replicas: 3);
        var modified = MakeDeploy(replicas: 5);

        var result = original.CreateStrategicPatch(modified);
        var applied = original.ApplyStrategicPatch(result);

        Assert.AreEqual(5, applied.Spec.Replicas);
        Assert.AreEqual("api", applied.Metadata.Name);
    }

    [TestMethod]
    public void Apply_EmptyResult_ReturnsCloneOfOriginal()
    {
        var doc = MakeDeploy(replicas: 3);
        var result = doc.CreateStrategicPatch(doc);
        Assert.IsTrue(result.IsEmpty);

        var applied = doc.ApplyStrategicPatch(result);
        Assert.AreEqual(doc.Spec.Replicas, applied.Spec.Replicas);
        Assert.AreNotSame(doc, applied);
    }

    // ---- GVK fall-back -------------------------------------------------------------------

    [TestMethod]
    public void GvkResolution_FallsBackToKubernetesEntityAttribute_WhenInputsLackMetadata()
    {
        // Both inputs are missing apiVersion/kind on the wire; the [KubernetesEntity] attribute
        // on V1Deployment supplies the GVK so the result is still well-formed.
        var original = new V1Deployment { Spec = new V1DeploymentSpec { Replicas = 3 } };
        var modified = new V1Deployment { Spec = new V1DeploymentSpec { Replicas = 4 } };

        var result = original.CreateStrategicPatch(modified);
        Assert.AreEqual(new GroupVersionKind("apps", "v1", "Deployment"), result.Gvk);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static V1Deployment MakeDeploy(int replicas, IDictionary<string, string>? annotations = null) =>
        new()
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = "api",
                NamespaceProperty = "default",
                Annotations = annotations,
            },
            Spec = new V1DeploymentSpec { Replicas = replicas },
        };

    private sealed class InMemoryPodSchema : KubernetesClient.StrategicPatch.Schema.ISchemaProvider
    {
        private readonly KubernetesClient.StrategicPatch.Schema.ISchemaProvider _backing
            = TestSchemas.DeploymentSchemaProvider();

        public KubernetesClient.StrategicPatch.Schema.SchemaNode? GetRootSchema(GroupVersionKind gvk)
        {
            // Crude alias: when we see a Pod, return the deployment's pod-template-spec subtree
            // (which has the apps/v1 Deployment schema's `.spec.template` object) so the diff has
            // the keyed-merge metadata for `containers` and `env`.
            if (gvk.Kind == "Pod")
            {
                var dep = _backing.GetRootSchema(new GroupVersionKind("apps", "v1", "Deployment"));
                return dep?.Resolve(JsonPointer.Parse("/spec/template"));
            }
            return _backing.GetRootSchema(gvk);
        }
    }
}
