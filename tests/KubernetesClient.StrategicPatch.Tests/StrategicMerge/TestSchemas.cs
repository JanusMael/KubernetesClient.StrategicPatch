using System.Collections.Frozen;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.StrategicMerge;

/// <summary>
/// Hand-built schema fixtures for Stage 4 tests. Mirrors the shape of the real K8s OpenAPI for
/// apps/v1 Deployment but only carries the patch metadata we test against — no need to vendor
/// the whole spec to drive the diff engine here.
/// </summary>
internal static class TestSchemas
{
    public static readonly GroupVersionKind DeploymentGvk = new("apps", "v1", "Deployment");

    private static SchemaNode Primitive() =>
        new() { JsonName = string.Empty, Kind = SchemaNodeKind.Primitive };

    private static SchemaNode Map(SchemaNode value) =>
        new() { JsonName = string.Empty, Kind = SchemaNodeKind.Map, Items = value };

    private static SchemaNode Obj(string name, IReadOnlyDictionary<string, SchemaNode> props,
        PatchStrategy strategy = PatchStrategy.None) => new()
    {
        JsonName = name,
        Kind = SchemaNodeKind.Object,
        Properties = props.ToFrozenDictionary(),
        Strategy = strategy,
    };

    private static SchemaNode List(string name, SchemaNode items,
        PatchStrategy strategy = PatchStrategy.None,
        string? mergeKey = null,
        ListType listType = ListType.Unspecified) => new()
    {
        JsonName = name,
        Kind = SchemaNodeKind.List,
        Items = items,
        Strategy = strategy,
        PatchMergeKey = mergeKey,
        ListType = listType,
    };

    /// <summary>
    /// Returns a minimal apps/v1 Deployment schema that exercises every Stage 4 list-strategy code
    /// path (atomic, primitive merge as set, primitive set, object merge by key, retainKeys).
    /// </summary>
    public static ISchemaProvider DeploymentSchemaProvider()
    {
        var containerPort = Obj("containerPort-item", new Dictionary<string, SchemaNode>
        {
            ["containerPort"] = Primitive(),
            ["protocol"] = Primitive(),
            ["name"] = Primitive(),
        });

        var envVar = Obj("env-item", new Dictionary<string, SchemaNode>
        {
            ["name"] = Primitive(),
            ["value"] = Primitive(),
        });

        var container = Obj("container-item", new Dictionary<string, SchemaNode>
        {
            ["name"] = Primitive(),
            ["image"] = Primitive(),
            ["ports"] = List("ports", containerPort,
                strategy: PatchStrategy.Merge, mergeKey: "containerPort", listType: ListType.Map),
            ["env"] = List("env", envVar,
                strategy: PatchStrategy.Merge, mergeKey: "name", listType: ListType.Map),
        });

        var localObjectRef = Obj("local-object-ref", new Dictionary<string, SchemaNode>
        {
            ["name"] = Primitive(),
        });

        var podSpec = Obj("podspec", new Dictionary<string, SchemaNode>
        {
            ["containers"] = List("containers", container,
                strategy: PatchStrategy.Merge, mergeKey: "name", listType: ListType.Map),
            ["imagePullSecrets"] = List("imagePullSecrets", localObjectRef,
                strategy: PatchStrategy.Merge, mergeKey: "name", listType: ListType.Map),
            ["finalizers"] = List("finalizers", Primitive(),
                strategy: PatchStrategy.Merge, listType: ListType.Set),
            ["tolerations"] = List("tolerations", Obj("toleration-item",
                new Dictionary<string, SchemaNode>
                {
                    ["key"] = Primitive(),
                    ["operator"] = Primitive(),
                }), strategy: PatchStrategy.None, listType: ListType.Atomic),
            ["nodeSelector"] = Map(Primitive()),
        });

        var objectMeta = Obj("metadata", new Dictionary<string, SchemaNode>
        {
            ["name"] = Primitive(),
            ["namespace"] = Primitive(),
            ["uid"] = Primitive(),
            ["resourceVersion"] = Primitive(),
            ["labels"] = Map(Primitive()),
            ["annotations"] = Map(Primitive()),
        });

        var podTemplateSpec = Obj("template", new Dictionary<string, SchemaNode>
        {
            ["metadata"] = objectMeta,
            ["spec"] = podSpec,
        });

        // A compact retainKeys-strategy block under spec.strategy.rollingUpdate (mirrors the real
        // DeploymentStrategy where only one of {rollingUpdate, recreate} is present at a time).
        var deploymentStrategy = Obj("deploymentStrategy", new Dictionary<string, SchemaNode>
        {
            ["type"] = Primitive(),
            ["rollingUpdate"] = Obj("rollingUpdate", new Dictionary<string, SchemaNode>
            {
                ["maxSurge"] = Primitive(),
                ["maxUnavailable"] = Primitive(),
            }),
        }, strategy: PatchStrategy.RetainKeys);

        var deploymentSpec = Obj("deploymentSpec", new Dictionary<string, SchemaNode>
        {
            ["replicas"] = Primitive(),
            ["template"] = podTemplateSpec,
            ["strategy"] = deploymentStrategy,
        });

        var deployment = Obj("deployment", new Dictionary<string, SchemaNode>
        {
            ["apiVersion"] = Primitive(),
            ["kind"] = Primitive(),
            ["metadata"] = objectMeta,
            ["spec"] = deploymentSpec,
        });

        return new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode>
        {
            [DeploymentGvk] = deployment,
        });
    }
}
