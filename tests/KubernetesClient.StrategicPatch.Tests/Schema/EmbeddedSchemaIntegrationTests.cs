using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.Schema;

/// <summary>
/// Stage 11 corpus: integration tests for the baked Kubernetes built-in schemas. These exercise
/// the real <c>schemas.json</c> snapshot that ships in the assembly, not a hand-built fixture —
/// they fail loudly if the schema-baking pipeline (<c>scripts/regen-schemas.sh</c>) drifts or
/// produces incorrect <c>x-kubernetes-patch-merge-key</c> / <c>x-kubernetes-patch-strategy</c>
/// metadata. Skipped quietly when the embedded resource is empty (i.e. before the first bake).
/// </summary>
[TestClass]
public sealed class EmbeddedSchemaIntegrationTests
{
    [TestMethod]
    public void Embedded_HasContent_OrTestsAreSkipped()
    {
        // Self-pinning: this assertion fires once the snapshot is baked, and forms the
        // implicit guard for every other test in the class.
        Assert.IsGreaterThan(0, EmbeddedSchemaProvider.Shared.Count,
            "EmbeddedSchemaProvider.Shared.Count is 0 — schemas.json was not baked. "
            + "Run scripts/regen-schemas.sh.");
    }

    [TestMethod]
    public void Embedded_ResolvesDeploymentContainersToMergeKey()
    {
        SkipIfEmpty();
        var node = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/spec/template/spec/containers"));
        Assert.IsNotNull(node);
        Assert.AreEqual(SchemaNodeKind.List, node!.Kind);
        Assert.AreEqual("name", node.PatchMergeKey);
        Assert.IsTrue(node.Strategy.HasFlag(PatchStrategy.Merge));
    }

    [TestMethod]
    public void Embedded_ResolvesContainerPortsToContainerPortMergeKey()
    {
        SkipIfEmpty();
        var node = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/spec/template/spec/containers/0/ports"));
        Assert.IsNotNull(node);
        Assert.AreEqual(SchemaNodeKind.List, node!.Kind);
        Assert.AreEqual("containerPort", node.PatchMergeKey);
    }

    [TestMethod]
    public void Embedded_ResolvesContainerEnvToNameMergeKey()
    {
        SkipIfEmpty();
        var node = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/spec/template/spec/containers/0/env"));
        Assert.IsNotNull(node);
        Assert.AreEqual(SchemaNodeKind.List, node!.Kind);
        Assert.AreEqual("name", node.PatchMergeKey);
    }

    [TestMethod]
    public void Embedded_ResolvesServicePortsToPortMergeKey()
    {
        SkipIfEmpty();
        var node = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind(string.Empty, "v1", "Service"),
                     JsonPointer.Parse("/spec/ports"));
        Assert.IsNotNull(node);
        Assert.AreEqual(SchemaNodeKind.List, node!.Kind);
        Assert.AreEqual("port", node.PatchMergeKey);
    }

    [TestMethod]
    public void Embedded_ResolvesObjectMetaLabelsAsMap()
    {
        SkipIfEmpty();
        var node = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/metadata/labels"));
        Assert.IsNotNull(node);
        Assert.AreEqual(SchemaNodeKind.Map, node!.Kind);
    }

    [TestMethod]
    public void Embedded_CoversCanonicalBuiltins()
    {
        SkipIfEmpty();
        // Pin the canonical resource set — if the bake script fails to produce one of these,
        // the deployment projects will silently fall through to RFC 7396 for that kind.
        var canonical = new[]
        {
            new GroupVersionKind(string.Empty, "v1", "Pod"),
            new GroupVersionKind(string.Empty, "v1", "Service"),
            new GroupVersionKind(string.Empty, "v1", "ConfigMap"),
            new GroupVersionKind(string.Empty, "v1", "Secret"),
            new GroupVersionKind(string.Empty, "v1", "ServiceAccount"),
            new GroupVersionKind(string.Empty, "v1", "Namespace"),
            new GroupVersionKind(string.Empty, "v1", "PersistentVolume"),
            new GroupVersionKind(string.Empty, "v1", "PersistentVolumeClaim"),
            new GroupVersionKind("apps", "v1", "Deployment"),
            new GroupVersionKind("apps", "v1", "StatefulSet"),
            new GroupVersionKind("apps", "v1", "DaemonSet"),
            new GroupVersionKind("apps", "v1", "ReplicaSet"),
            new GroupVersionKind("batch", "v1", "Job"),
            new GroupVersionKind("batch", "v1", "CronJob"),
            new GroupVersionKind("networking.k8s.io", "v1", "Ingress"),
            new GroupVersionKind("networking.k8s.io", "v1", "NetworkPolicy"),
            new GroupVersionKind("rbac.authorization.k8s.io", "v1", "Role"),
            new GroupVersionKind("rbac.authorization.k8s.io", "v1", "ClusterRoleBinding"),
            new GroupVersionKind("policy", "v1", "PodDisruptionBudget"),
            new GroupVersionKind("autoscaling", "v2", "HorizontalPodAutoscaler"),
        };
        var missing = canonical.Where(g => EmbeddedSchemaProvider.Shared.GetRootSchema(g) is null).ToArray();
        Assert.IsEmpty(missing,
            "Missing GVKs in the embedded snapshot: " + string.Join(", ", missing.Select(g => g.ToString())));
    }

    // ---- Auto-default in StrategicPatchExtensions --------------------------------------------

    [TestMethod]
    public void Extensions_AutoDefaultEmbeddedProvider_DrivesStrategicMerge()
    {
        SkipIfEmpty();
        // No SchemaProvider passed → CreateStrategicPatch should default to
        // EmbeddedSchemaProvider.Shared and produce a strategic-merge patch (with directives),
        // not an RFC 7396 atomic-replace patch.
        var original = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new() { Name = "web", Image = "nginx:1" },
                        },
                    },
                },
            },
        };
        var modified = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new() { Name = "web", Image = "nginx:2" },
                        },
                    },
                },
            },
        };

        var result = original.CreateStrategicPatch(modified);
        Assert.IsFalse(result.IsEmpty);
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        // Schema-driven path: containers list emits $setElementOrder and a per-element merge,
        // not a wholesale list replace. RFC 7396 fallback would emit a full containers array.
        var podSpec = (JsonObject)patch["spec"]!["template"]!["spec"]!;
        Assert.IsTrue(podSpec.ContainsKey("$setElementOrder/containers"),
            $"Expected schema-driven $setElementOrder; got: {patch.ToJsonString()}");
    }

    [TestMethod]
    public void Extensions_ExplicitProviderOverridesDefault()
    {
        SkipIfEmpty();
        // Caller passes an empty InMemorySchemaProvider → no schema for Deployment → falls back
        // to RFC 7396 for the containers list (no $setElementOrder).
        var emptyProvider = new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode>());
        var original = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new() { Name = "web", Image = "nginx:1" },
                        },
                    },
                },
            },
        };
        var modified = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec
            {
                Replicas = 3,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new() { Name = "web", Image = "nginx:2" },
                        },
                    },
                },
            },
        };

        var result = original.CreateStrategicPatch(modified,
            new StrategicPatchOptions { SchemaProvider = emptyProvider });
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        var podSpec = (JsonObject)patch["spec"]!["template"]!["spec"]!;
        Assert.IsFalse(podSpec.ContainsKey("$setElementOrder/containers"),
            $"Expected RFC 7396 fallback path (no $setElementOrder); got: {patch.ToJsonString()}");
    }

    // ---- Real-spec round-trip ----------------------------------------------------------------

    [TestMethod]
    public void RoundTrip_DeploymentScale_AgainstBakedSchema()
    {
        SkipIfEmpty();
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

        var result = original.CreateStrategicPatch(modified);
        var applied = original.ApplyStrategicPatch(result);
        Assert.AreEqual(5, applied.Spec.Replicas);
    }

    [TestMethod]
    public void RoundTrip_ServicePortReorder_AgainstBakedSchema()
    {
        SkipIfEmpty();
        // Service.spec.ports is keyed by 'port' under SMP. Reordering ports should produce a
        // $setElementOrder directive, not a wholesale list replace.
        var original = new V1Service
        {
            ApiVersion = "v1", Kind = "Service",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1ServiceSpec
            {
                Ports = new List<V1ServicePort>
                {
                    new() { Port = 80, Name = "http", Protocol = "TCP" },
                    new() { Port = 443, Name = "https", Protocol = "TCP" },
                },
            },
        };
        var modified = new V1Service
        {
            ApiVersion = "v1", Kind = "Service",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1ServiceSpec
            {
                Ports = new List<V1ServicePort>
                {
                    new() { Port = 443, Name = "https", Protocol = "TCP" },
                    new() { Port = 80, Name = "http", Protocol = "TCP" },
                },
            },
        };

        var result = original.CreateStrategicPatch(modified);
        Assert.IsFalse(result.IsEmpty);
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        // Now that the baked schema knows port is the merge key, reorder-only diffs surface as
        // $setElementOrder/ports rather than a full list replace.
        Assert.IsTrue(((JsonObject)patch["spec"]!).ContainsKey("$setElementOrder/ports"),
            $"Got: {patch.ToJsonString()}");
    }

    private static void SkipIfEmpty()
    {
        if (EmbeddedSchemaProvider.Shared.Count == 0)
        {
            Assert.Inconclusive("schemas.json is empty — run scripts/regen-schemas.sh first.");
        }
    }
}
