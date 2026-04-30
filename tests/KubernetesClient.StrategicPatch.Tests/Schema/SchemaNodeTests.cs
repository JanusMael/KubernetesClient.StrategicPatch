using System.Collections.Frozen;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.Schema;

[TestClass]
public sealed class SchemaNodeTests
{
    [TestMethod]
    public void Resolve_RootPointer_ReturnsSelf()
    {
        var root = new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Object };
        Assert.AreSame(root, root.Resolve(JsonPointer.Root));
    }

    [TestMethod]
    public void Resolve_WalksObjectProperties()
    {
        var leaf = new SchemaNode { JsonName = "replicas", Kind = SchemaNodeKind.Primitive };
        var spec = new SchemaNode
        {
            JsonName = "spec",
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode> { ["replicas"] = leaf }.ToFrozenDictionary(),
        };
        var root = new SchemaNode
        {
            JsonName = string.Empty,
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode> { ["spec"] = spec }.ToFrozenDictionary(),
        };

        var found = root.Resolve(JsonPointer.Parse("/spec/replicas"));
        Assert.AreSame(leaf, found);
    }

    [TestMethod]
    public void Resolve_UnknownProperty_ReturnsNull()
    {
        var root = new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Object };
        Assert.IsNull(root.Resolve(JsonPointer.Parse("/missing")));
    }

    [TestMethod]
    public void Resolve_SkipsArrayIndexSegments_AndDescendsIntoListItems()
    {
        var itemPort = new SchemaNode { JsonName = "containerPort", Kind = SchemaNodeKind.Primitive };
        var itemSchema = new SchemaNode
        {
            JsonName = string.Empty,
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode> { ["containerPort"] = itemPort }.ToFrozenDictionary(),
        };
        var ports = new SchemaNode
        {
            JsonName = "ports",
            Kind = SchemaNodeKind.List,
            Items = itemSchema,
            ListType = ListType.Map,
            PatchMergeKey = "containerPort",
            Strategy = PatchStrategy.Merge,
        };
        var root = new SchemaNode
        {
            JsonName = string.Empty,
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode> { ["ports"] = ports }.ToFrozenDictionary(),
        };

        var hit = root.Resolve(JsonPointer.Parse("/ports/0/containerPort"));
        Assert.AreSame(itemPort, hit);
    }

    [TestMethod]
    public void Resolve_DescendsIntoMapValueSchema_ForAnySegment()
    {
        var valueSchema = new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Primitive };
        var labels = new SchemaNode
        {
            JsonName = "labels",
            Kind = SchemaNodeKind.Map,
            Items = valueSchema,
        };
        var root = new SchemaNode
        {
            JsonName = string.Empty,
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode> { ["labels"] = labels }.ToFrozenDictionary(),
        };

        Assert.AreSame(valueSchema, root.Resolve(JsonPointer.Parse("/labels/anything")));
        Assert.AreSame(valueSchema, root.Resolve(JsonPointer.Parse("/labels/app.kubernetes.io~1name")));
    }
}
