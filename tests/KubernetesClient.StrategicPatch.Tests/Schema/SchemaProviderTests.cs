using System.Collections.Frozen;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.Schema;

[TestClass]
public sealed class SchemaProviderTests
{
    private static readonly GroupVersionKind DeploymentGvk = new("apps", "v1", "Deployment");

    [TestMethod]
    public void InMemoryProvider_ResolvesKnownGvk()
    {
        var root = new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Object };
        var provider = new InMemorySchemaProvider(
            new Dictionary<GroupVersionKind, SchemaNode> { [DeploymentGvk] = root });

        Assert.AreSame(root, provider.GetRootSchema(DeploymentGvk));
        Assert.IsNull(provider.GetRootSchema(new GroupVersionKind("apps", "v1", "DaemonSet")));
    }

    [TestMethod]
    public void Composite_PrefersFirstNonNull()
    {
        var first = new SchemaNode { JsonName = "first", Kind = SchemaNodeKind.Object };
        var second = new SchemaNode { JsonName = "second", Kind = SchemaNodeKind.Object };
        var provider = new CompositeSchemaProvider(
            new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode> { [DeploymentGvk] = first }),
            new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode> { [DeploymentGvk] = second }));

        Assert.AreSame(first, provider.GetRootSchema(DeploymentGvk));
    }

    [TestMethod]
    public void Composite_FallsThrough_ToLaterProvider()
    {
        var late = new SchemaNode { JsonName = "late", Kind = SchemaNodeKind.Object };
        var provider = new CompositeSchemaProvider(
            new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode>()),
            new InMemorySchemaProvider(new Dictionary<GroupVersionKind, SchemaNode> { [DeploymentGvk] = late }));

        Assert.AreSame(late, provider.GetRootSchema(DeploymentGvk));
    }

    [TestMethod]
    public void Composite_RejectsEmptyAndNull()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new CompositeSchemaProvider());
        Assert.ThrowsExactly<ArgumentNullException>(() => new CompositeSchemaProvider((ISchemaProvider[])null!));
        Assert.ThrowsExactly<ArgumentException>(() => new CompositeSchemaProvider(null!, null!));
    }

    [TestMethod]
    public void EmbeddedProvider_MissingResource_ReturnsEmpty()
    {
        var provider = new EmbeddedSchemaProvider(typeof(SchemaProviderTests).Assembly, "does.not.exist");
        Assert.IsNull(provider.GetRootSchema(DeploymentGvk));
        Assert.AreEqual(0, provider.Count);
    }

    [TestMethod]
    public void WireFormat_RoundTripsAllFields()
    {
        var item = new SchemaNode
        {
            JsonName = string.Empty,
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode>
            {
                ["name"] = new() { JsonName = "name", Kind = SchemaNodeKind.Primitive },
            }.ToFrozenDictionary(),
        };
        var containers = new SchemaNode
        {
            JsonName = "containers",
            Kind = SchemaNodeKind.List,
            Items = item,
            ListType = ListType.Map,
            PatchMergeKey = "name",
            Strategy = PatchStrategy.Merge | PatchStrategy.RetainKeys,
        };
        var root = new SchemaNode
        {
            JsonName = string.Empty,
            Kind = SchemaNodeKind.Object,
            Properties = new Dictionary<string, SchemaNode> { ["containers"] = containers }.ToFrozenDictionary(),
        };

        var bytes = SchemaWireFormat.Serialize(
            new Dictionary<GroupVersionKind, SchemaNode> { [DeploymentGvk] = root });
        var dict = SchemaWireFormat.Deserialize(bytes);

        Assert.IsTrue(dict.TryGetValue(DeploymentGvk, out var rt));
        var containersRt = rt!.Properties["containers"];
        Assert.AreEqual(SchemaNodeKind.List, containersRt.Kind);
        Assert.AreEqual(ListType.Map, containersRt.ListType);
        Assert.AreEqual("name", containersRt.PatchMergeKey);
        Assert.AreEqual(PatchStrategy.Merge | PatchStrategy.RetainKeys, containersRt.Strategy);
        Assert.IsNotNull(containersRt.Items);
        Assert.IsTrue(containersRt.Items!.Properties.ContainsKey("name"));
    }

    [TestMethod]
    public void WireFormat_OmitsDefaults_KeepingPayloadCompact()
    {
        var minimal = new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Primitive };
        var bytes = SchemaWireFormat.Serialize(
            new Dictionary<GroupVersionKind, SchemaNode> { [new("", "v1", "Foo")] = minimal });
        var s = System.Text.Encoding.UTF8.GetString(bytes);
        // Defaults (no merge key, no list type, no properties) should be absent.
        Assert.IsFalse(s.Contains("\"mk\"", StringComparison.Ordinal));
        Assert.IsFalse(s.Contains("\"lt\"", StringComparison.Ordinal));
        Assert.IsFalse(s.Contains("\"ps\"", StringComparison.Ordinal));
        Assert.IsFalse(s.Contains("\"p\"", StringComparison.Ordinal));
        Assert.IsFalse(s.Contains("\"i\"", StringComparison.Ordinal));
    }
}
