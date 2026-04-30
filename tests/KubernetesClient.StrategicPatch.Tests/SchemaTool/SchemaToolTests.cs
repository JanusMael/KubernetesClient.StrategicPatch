using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;
using KubernetesClient.StrategicPatch.SchemaTool;

namespace KubernetesClient.StrategicPatch.Tests.SchemaTool;

[TestClass]
public sealed class SchemaToolTests
{
    private static readonly GroupVersionKind DeploymentGvk = new("apps", "v1", "Deployment");
    private static readonly GroupVersionKind TreeNodeGvk = new("demo", "v1", "TreeNode");

    [TestMethod]
    public void Walker_ResolvesDeploymentContainersToMergeKeyName()
    {
        var roots = BuildRootsFromInline(SchemaToolFixtures.DeploymentSlice);
        Assert.IsTrue(roots.TryGetValue(DeploymentGvk, out var deployment));

        var containers = deployment!.Resolve(JsonPointer.Parse("/spec/template/spec/containers"));
        Assert.IsNotNull(containers);
        Assert.AreEqual(SchemaNodeKind.List, containers!.Kind);
        Assert.AreEqual("name", containers.PatchMergeKey);
        Assert.IsTrue(containers.Strategy.HasFlag(PatchStrategy.Merge));
        Assert.AreEqual(ListType.Map, containers.ListType);
        Assert.IsNotNull(containers.Items);
        Assert.AreEqual(SchemaNodeKind.Object, containers.Items!.Kind);
        Assert.IsTrue(containers.Items.Properties.ContainsKey("name"));
    }

    [TestMethod]
    public void Walker_ResolvesNestedListPort_ToContainerPortMergeKey()
    {
        var roots = BuildRootsFromInline(SchemaToolFixtures.DeploymentSlice);
        var ports = roots[DeploymentGvk]
            .Resolve(JsonPointer.Parse("/spec/template/spec/containers/0/ports"));
        Assert.IsNotNull(ports);
        Assert.AreEqual(SchemaNodeKind.List, ports!.Kind);
        Assert.AreEqual("containerPort", ports.PatchMergeKey);
        Assert.AreEqual(ListType.Map, ports.ListType);
    }

    [TestMethod]
    public void Walker_ResolvesPrimitiveSetList_FinalizersAsSet()
    {
        var roots = BuildRootsFromInline(SchemaToolFixtures.DeploymentSlice);
        var finalizers = roots[DeploymentGvk]
            .Resolve(JsonPointer.Parse("/spec/template/spec/finalizers"));
        Assert.IsNotNull(finalizers);
        Assert.AreEqual(SchemaNodeKind.List, finalizers!.Kind);
        Assert.AreEqual(ListType.Set, finalizers.ListType);
        Assert.IsNull(finalizers.PatchMergeKey);
    }

    [TestMethod]
    public void Walker_ResolvesMapField_ObjectMetaLabels()
    {
        var roots = BuildRootsFromInline(SchemaToolFixtures.DeploymentSlice);
        var labels = roots[DeploymentGvk].Resolve(JsonPointer.Parse("/metadata/labels"));
        Assert.IsNotNull(labels);
        Assert.AreEqual(SchemaNodeKind.Map, labels!.Kind);
        Assert.IsNotNull(labels.Items);
        Assert.AreEqual(SchemaNodeKind.Primitive, labels.Items!.Kind);
    }

    [TestMethod]
    public void Walker_HandlesSelfReferentialSchema_WithoutStackOverflow()
    {
        var roots = BuildRootsFromInline(SchemaToolFixtures.CyclicTree);
        var tree = roots[TreeNodeGvk];
        Assert.AreEqual(SchemaNodeKind.Object, tree.Kind);
        Assert.IsTrue(tree.Properties.ContainsKey("child"));
        // First descent should still expose child as Object; the second-level recursion is the
        // one that gets short-circuited by the cycle guard.
        var child = tree.Properties["child"];
        Assert.AreEqual(SchemaNodeKind.Object, child.Kind);
    }

    [TestMethod]
    public void Runner_FileIO_RoundTripsThroughSchemasJson()
    {
        var inputDir = Directory.CreateTempSubdirectory("smp-fixture-");
        try
        {
            var inputPath = Path.Combine(inputDir.FullName, "deployment-slice.json");
            File.WriteAllText(inputPath, SchemaToolFixtures.DeploymentSlice);

            var outputPath = Path.Combine(inputDir.FullName, "schemas.json");
            var count = SchemaToolRunner.Run(outputPath, [inputPath]);
            Assert.AreEqual(1, count);
            Assert.IsTrue(File.Exists(outputPath));

            var bytes = File.ReadAllBytes(outputPath);
            var dict = SchemaWireFormat.Deserialize(bytes);

            Assert.IsTrue(dict.ContainsKey(DeploymentGvk));
            var containers = dict[DeploymentGvk]
                .Resolve(JsonPointer.Parse("/spec/template/spec/containers"));
            Assert.IsNotNull(containers);
            Assert.AreEqual("name", containers!.PatchMergeKey);
        }
        finally
        {
            inputDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Runner_MissingInput_Throws()
    {
        Assert.ThrowsExactly<FileNotFoundException>(
            () => SchemaToolRunner.Run("ignored.json", ["does-not-exist.json"]));
    }

    private static IReadOnlyDictionary<GroupVersionKind, SchemaNode> BuildRootsFromInline(string fixture)
    {
        var doc = (JsonObject)JsonNode.Parse(fixture)!;
        var components = (JsonObject)doc["components"]!;
        var schemas = (JsonObject)components["schemas"]!;
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (name, node) in schemas)
        {
            if (node is JsonObject obj)
            {
                index[name] = obj;
            }
        }
        return SchemaToolRunner.BuildRoots(index);
    }
}
