namespace KubernetesClient.StrategicPatch.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void Smoke_Test_Builds()
    {
        // Stage 0 checkpoint: solution compiles and the test runner is wired up.
        var asm = typeof(SmokeTests).Assembly;
        Assert.AreEqual("KubernetesClient.StrategicPatch.Tests", asm.GetName().Name);
    }

    [TestMethod]
    public void KubernetesClient_Reference_Resolves()
    {
        // Verifies the KubernetesClient package is restorable on net10.0.
        var attr = new k8s.Models.KubernetesEntityAttribute();
        Assert.IsNotNull(attr);
    }
}
