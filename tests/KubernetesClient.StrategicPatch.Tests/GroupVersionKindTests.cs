namespace KubernetesClient.StrategicPatch.Tests;

[TestClass]
public sealed class GroupVersionKindTests
{
    [TestMethod]
    public void Parse_CoreApiVersion_HasEmptyGroup()
    {
        var gvk = GroupVersionKind.Parse("v1", "Pod");
        Assert.AreEqual(string.Empty, gvk.Group);
        Assert.AreEqual("v1", gvk.Version);
        Assert.AreEqual("Pod", gvk.Kind);
    }

    [TestMethod]
    public void Parse_GroupedApiVersion_SplitsOnSlash()
    {
        var gvk = GroupVersionKind.Parse("apps/v1", "Deployment");
        Assert.AreEqual("apps", gvk.Group);
        Assert.AreEqual("v1", gvk.Version);
        Assert.AreEqual("Deployment", gvk.Kind);
    }

    [TestMethod]
    [DataRow("v1", "Pod", "v1")]
    [DataRow("apps/v1", "Deployment", "apps/v1")]
    public void ApiVersion_RoundTripsThroughParse(string apiVersion, string kind, string expectedApiVersion)
    {
        var gvk = GroupVersionKind.Parse(apiVersion, kind);
        Assert.AreEqual(expectedApiVersion, gvk.ApiVersion);
    }

    [TestMethod]
    public void Parse_NullOrEmpty_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GroupVersionKind.Parse(string.Empty, "Pod"));
        Assert.ThrowsExactly<ArgumentException>(() => GroupVersionKind.Parse("v1", string.Empty));
    }

    [TestMethod]
    public void RecordEquality_IsValueBased()
    {
        var a = new GroupVersionKind("apps", "v1", "Deployment");
        var b = new GroupVersionKind("apps", "v1", "Deployment");
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }
}
