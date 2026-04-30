using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.Schema;

/// <summary>
/// Tests for <see cref="SchemaBuilder"/> — the source-generator-friendly fluent API. Verifies
/// the structural shape of nodes produced by the factory methods and the nested object-builder.
/// </summary>
[TestClass]
public sealed class SchemaBuilderTests
{
    [TestMethod]
    public void Primitive_HasCorrectKindAndDefaults()
    {
        var node = SchemaBuilder.Primitive("name");
        Assert.AreEqual("name", node.JsonName);
        Assert.AreEqual(SchemaNodeKind.Primitive, node.Kind);
        Assert.AreEqual(PatchStrategy.None, node.Strategy);
        Assert.AreEqual(ListType.Unspecified, node.ListType);
        Assert.IsNull(node.Items);
        Assert.IsEmpty(node.Properties);
    }

    [TestMethod]
    public void Map_CarriesValueSchemaAsItems()
    {
        var value = SchemaBuilder.Primitive();
        var node = SchemaBuilder.Map(value, "labels");
        Assert.AreEqual(SchemaNodeKind.Map, node.Kind);
        Assert.AreEqual("labels", node.JsonName);
        Assert.AreSame(value, node.Items);
    }

    [TestMethod]
    public void Object_DictionaryOverload_FrozenAndOrdinal()
    {
        var props = new Dictionary<string, SchemaNode>
        {
            ["name"] = SchemaBuilder.Primitive(),
            ["replicas"] = SchemaBuilder.Primitive(),
        };
        var node = SchemaBuilder.ObjectNode("spec", props);
        Assert.AreEqual(SchemaNodeKind.Object, node.Kind);
        Assert.HasCount(2, node.Properties);
        Assert.IsTrue(node.Properties.ContainsKey("name"));
        Assert.IsTrue(node.Properties.ContainsKey("replicas"));
    }

    [TestMethod]
    public void Object_FluentBuilder_BuildsTreeAsExpected()
    {
        var node = SchemaBuilder.ObjectNode("deployment", b => b
            .Primitive("apiVersion")
            .Primitive("kind")
            .ObjectProperty("metadata", m => m
                .Primitive("name")
                .Map("labels", SchemaBuilder.Primitive()))
            .ObjectProperty("spec", s => s
                .Primitive("replicas")
                .ListProperty("containers",
                    SchemaBuilder.ObjectNode("container", c => c.Primitive("name").Primitive("image")),
                    strategy: PatchStrategy.Merge,
                    patchMergeKey: "name",
                    listType: ListType.Map)));

        Assert.AreEqual(SchemaNodeKind.Object, node.Kind);
        var spec = node.Properties["spec"];
        var containers = spec.Properties["containers"];
        Assert.AreEqual(SchemaNodeKind.List, containers.Kind);
        Assert.AreEqual("name", containers.PatchMergeKey);
        Assert.AreEqual(PatchStrategy.Merge, containers.Strategy);
        Assert.AreEqual(ListType.Map, containers.ListType);
        Assert.AreEqual(SchemaNodeKind.Object, containers.Items!.Kind);
        Assert.IsTrue(containers.Items.Properties.ContainsKey("name"));
    }

    [TestMethod]
    public void List_PatchMetadataPropagated()
    {
        var item = SchemaBuilder.Primitive();
        var list = SchemaBuilder.ListNode("finalizers", item,
            strategy: PatchStrategy.Merge, patchMergeKey: null, listType: ListType.Set);
        Assert.AreEqual(SchemaNodeKind.List, list.Kind);
        Assert.AreEqual(PatchStrategy.Merge, list.Strategy);
        Assert.AreEqual(ListType.Set, list.ListType);
        Assert.IsNull(list.PatchMergeKey);
    }

    [TestMethod]
    public void Builder_RejectsNullArguments()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SchemaBuilder.Map(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => SchemaBuilder.ObjectNode("x", (IReadOnlyDictionary<string, SchemaNode>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => SchemaBuilder.ObjectNode("x", (Action<SchemaBuilder.ObjectSchemaBuilder>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => SchemaBuilder.ListNode("x", null!));
    }

    [TestMethod]
    public void ObjectSchemaBuilder_RejectsEmptyKey()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => SchemaBuilder.ObjectNode("x", b => b.Primitive("")));
    }

    [TestMethod]
    public void Builder_ProducedSchema_DrivesDiffEngineCorrectly()
    {
        // End-to-end: build a Deployment schema via the builder and verify the diff engine
        // honours the merge metadata.
        var schema = SchemaBuilder.ObjectNode("deployment", root => root
            .Primitive("apiVersion").Primitive("kind")
            .ObjectProperty("spec", spec => spec
                .ListProperty("containers",
                    SchemaBuilder.ObjectNode("container", c => c.Primitive("name").Primitive("image")),
                    strategy: PatchStrategy.Merge, patchMergeKey: "name", listType: ListType.Map)));

        var provider = new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode>
        {
            [new("apps", "v1", "Deployment")] = schema,
        });

        var original = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"containers":[{"name":"web","image":"nginx:1"}]}}""")!;
        var modified = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"containers":[{"name":"web","image":"nginx:2"}]}}""")!;

        var patch = KubernetesClient.StrategicPatch.StrategicMerge.TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { SchemaProvider = provider });
        Assert.IsNotNull(patch);
        // Builder-driven schema correctly enables keyed merge → emits $setElementOrder/containers.
        Assert.IsTrue(patch!["spec"]!.AsObject().ContainsKey("$setElementOrder/containers"));
    }
}
