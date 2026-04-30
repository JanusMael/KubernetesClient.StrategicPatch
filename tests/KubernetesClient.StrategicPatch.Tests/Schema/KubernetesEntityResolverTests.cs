using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.Schema;

[TestClass]
public sealed class KubernetesEntityResolverTests
{
    [TestMethod]
    public void TryGetGvk_CoreResource_HasEmptyGroup()
    {
        var gvk = KubernetesEntityResolver.TryGetGvk(typeof(k8s.Models.V1Pod));
        Assert.IsNotNull(gvk);
        Assert.AreEqual(string.Empty, gvk.Value.Group);
        Assert.AreEqual("v1", gvk.Value.Version);
        Assert.AreEqual("Pod", gvk.Value.Kind);
    }

    [TestMethod]
    public void TryGetGvk_GroupedResource_HasGroup()
    {
        var gvk = KubernetesEntityResolver.TryGetGvk(typeof(k8s.Models.V1Deployment));
        Assert.IsNotNull(gvk);
        Assert.AreEqual("apps", gvk.Value.Group);
        Assert.AreEqual("v1", gvk.Value.Version);
        Assert.AreEqual("Deployment", gvk.Value.Kind);
    }

    [TestMethod]
    public void TryGetGvk_UnannotatedType_ReturnsNull()
    {
        Assert.IsNull(KubernetesEntityResolver.TryGetGvk(typeof(string)));
    }

    [TestMethod]
    public void GetGvk_Strict_ThrowsOnUnannotated()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => KubernetesEntityResolver.GetGvk(typeof(string)));
    }

    [TestMethod]
    public void TryGetGvk_IsCached()
    {
        var first = KubernetesEntityResolver.TryGetGvk(typeof(k8s.Models.V1ConfigMap));
        var second = KubernetesEntityResolver.TryGetGvk(typeof(k8s.Models.V1ConfigMap));
        Assert.AreEqual(first, second);
    }
}
