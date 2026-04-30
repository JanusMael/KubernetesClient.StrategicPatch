using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;

namespace KubernetesClient.StrategicPatch.Tests.Internal;

[TestClass]
public sealed class JsonNodeCloningTests
{
    [TestMethod]
    public void Null_RoundTrip_ReturnsNull()
    {
        Assert.IsNull(JsonNodeCloning.CloneOrNull(null));
    }

    [TestMethod]
    public void Clone_IsDeep_AndDetached()
    {
        var src = (JsonObject)JsonNode.Parse("""{"spec":{"replicas":3,"labels":{"app":"web"}}}""")!;
        var clone = (JsonObject)JsonNodeCloning.CloneOrNull(src)!;

        // Mutate the clone — original must not change.
        clone["spec"]!["replicas"] = 99;
        ((JsonObject)clone["spec"]!["labels"]!)["app"] = "api";

        Assert.AreEqual(3, (int)src["spec"]!["replicas"]!);
        Assert.AreEqual("web", (string)src["spec"]!["labels"]!["app"]!);
        Assert.AreEqual(99, (int)clone["spec"]!["replicas"]!);
        Assert.AreEqual("api", (string)clone["spec"]!["labels"]!["app"]!);
    }

    [TestMethod]
    public void Clone_HasNoParent()
    {
        var parent = new JsonObject { ["child"] = JsonNode.Parse("""{"a":1}""") };
        var detached = JsonNodeCloning.CloneOrNull(parent["child"]!);
        Assert.IsNotNull(detached);
        Assert.IsNull(detached!.Parent);
    }
}
