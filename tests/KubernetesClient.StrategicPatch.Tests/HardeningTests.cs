using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Regression tests for the 2026-04-29 hardening pass. Each test pins a specific finding
/// from <c>docs/CODE_REVIEW_2026-04-29.md</c> so future contributors see the failure mode
/// the bug fix prevents.
/// </summary>
[TestClass]
public sealed class HardeningTests
{
    // ---- ScalarKey mixed construction --------------------------------------------------------

    [TestMethod]
    public void ScalarKey_StringValueAndStringNode_MatchRegardlessOfBacking()
    {
        // JsonElement-backed
        var parsed = JsonNode.Parse("\"hello\"")!;
        // CLR-backed
        JsonNode created = JsonValue.Create("hello")!;

        Assert.AreEqual(ScalarKey.Of(parsed), ScalarKey.Of(created));
    }

    [TestMethod]
    public void ScalarKey_NumberValueAndNumberNode_MatchRegardlessOfBacking()
    {
        var parsed = JsonNode.Parse("42")!;
        JsonNode created = JsonValue.Create(42)!;

        Assert.AreEqual(ScalarKey.Of(parsed), ScalarKey.Of(created));
    }

    [TestMethod]
    public void ScalarKey_BoolValueAndBoolNode_MatchRegardlessOfBacking()
    {
        var parsedTrue = JsonNode.Parse("true")!;
        JsonNode createdTrue = JsonValue.Create(true)!;
        var parsedFalse = JsonNode.Parse("false")!;
        JsonNode createdFalse = JsonValue.Create(false)!;

        Assert.AreEqual(ScalarKey.Of(parsedTrue), ScalarKey.Of(createdTrue));
        Assert.AreEqual(ScalarKey.Of(parsedFalse), ScalarKey.Of(createdFalse));
        Assert.AreNotEqual(ScalarKey.Of(parsedTrue), ScalarKey.Of(parsedFalse));
    }

    [TestMethod]
    public void ScalarKey_StringNumber42AndIntegerNumber42_AreDifferent()
    {
        // The bug we're guarding against: silent collapse of "42" (string) and 42 (number)
        // because both end up as the literal substring "42" in some path.
        Assert.AreNotEqual(
            ScalarKey.Of(JsonNode.Parse("\"42\"")),
            ScalarKey.Of(JsonNode.Parse("42")));
    }

    [TestMethod]
    public void ScalarKey_NullAndAnyValue_AreDifferent()
    {
        Assert.AreNotEqual(ScalarKey.Of(null), ScalarKey.Of(JsonNode.Parse("0")));
        Assert.AreNotEqual(ScalarKey.Of(null), ScalarKey.Of(JsonNode.Parse("\"\"")));
        Assert.AreNotEqual(ScalarKey.Of(null), ScalarKey.Of(JsonNode.Parse("false")));
    }

    [TestMethod]
    public void SetMerge_MixedConstructionInputs_ProduceConsistentDiff()
    {
        // Caller builds modified via JsonValue.Create, the fixture / cluster reads its document
        // via JsonNode.Parse. ScalarKey unification means the diff sees them as equal.
        var original = new JsonObject
        {
            ["finalizers"] = new JsonArray(JsonNode.Parse("\"a\""), JsonNode.Parse("\"b\"")),
        };
        var modified = new JsonObject
        {
            ["finalizers"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b")),
        };
        // No schema provider → atomic-replace fallback; even so DeepEquals must compare equal.
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNull(patch, $"Expected no diff but got: {patch?.ToJsonString()}");
    }

    // ---- List-of-lists -----------------------------------------------------------------------

    [TestMethod]
    public void Diff_ListOfLists_ThrowsWithPathInformation()
    {
        var original = (JsonObject)JsonNode.Parse("""{"matrix":[[1,2],[3,4]]}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"matrix":[[1,2],[5,6]]}""")!;

        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => TwoWayMerge.CreateTwoWayMergePatch(original, modified));
        StringAssert.Contains(ex.Message, "list of lists");
    }

    [TestMethod]
    public void Apply_ListOfLists_Throws()
    {
        var original = (JsonObject)JsonNode.Parse("""{"matrix":[[1,2]]}""")!;
        var patch = (JsonObject)JsonNode.Parse("""{"matrix":[[3,4]]}""")!;

        Assert.ThrowsExactly<StrategicMergePatchException>(
            () => PatchApply.StrategicMergePatch(original, patch));
    }

    // ---- V1Patch.Content cast safety ---------------------------------------------------------

    [TestMethod]
    public void Apply_PatchWithByteArrayContent_RoundTrips()
    {
        var original = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };
        var modified = original.DeepClone();
        modified.Spec.Replicas = 5;

        var result = original.CreateStrategicPatch(modified);
        // Substitute byte[] content for the string content.
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes((string)result.Patch.Content);
        var byteResult = new StrategicPatchResult(
            new V1Patch(body: bodyBytes, type: V1Patch.PatchType.StrategicMergePatch),
            IsEmpty: false, PayloadBytes: bodyBytes.Length, Gvk: result.Gvk);

        var applied = original.ApplyStrategicPatch(byteResult);
        Assert.AreEqual(5, applied.Spec.Replicas);
    }

    [TestMethod]
    public void Apply_PatchWithUnsupportedContentType_ThrowsTyped()
    {
        var original = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };
        // V1Patch with a CLR int as content — neither string, nor byte[], nor JsonObject.
        var bogus = new V1Patch(body: 12345, type: V1Patch.PatchType.StrategicMergePatch);
        var result = new StrategicPatchResult(bogus, IsEmpty: false, PayloadBytes: 5,
            Gvk: new GroupVersionKind("apps", "v1", "Deployment"));

        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => original.ApplyStrategicPatch(result));
        StringAssert.Contains(ex.Message, "Unsupported");
    }

    // ---- MaxDepth recursion guard ------------------------------------------------------------

    [TestMethod]
    public void Diff_PathologicallyDeepObject_ThrowsWithPathInformation()
    {
        // Build a 600-deep nested object on both sides; under default MaxDepth=256 we throw.
        var original = BuildNested(depth: 600, leafValue: 1);
        var modified = BuildNested(depth: 600, leafValue: 2);

        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => TwoWayMerge.CreateTwoWayMergePatch(original, modified));
        StringAssert.Contains(ex.Message, "MaxDepth");
    }

    [TestMethod]
    public void Diff_PathologicallyDeepObject_AllowsCustomMaxDepth()
    {
        var original = BuildNested(depth: 600, leafValue: 1);
        var modified = BuildNested(depth: 600, leafValue: 2);

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { MaxDepth = 1024 });
        Assert.IsNotNull(patch);
    }

    private static JsonObject BuildNested(int depth, int leafValue)
    {
        var root = new JsonObject();
        var current = root;
        for (var i = 0; i < depth; i++)
        {
            var next = new JsonObject();
            current["nested"] = next;
            current = next;
        }
        current["leaf"] = leafValue;
        return root;
    }

    // ---- CancellationToken plumbing ----------------------------------------------------------

    [TestMethod]
    public void Diff_PreCancelledToken_ThrowsImmediately()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a":2}""")!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => TwoWayMerge.CreateTwoWayMergePatch(original, modified, cancellationToken: cts.Token));
    }

    [TestMethod]
    public void Apply_PreCancelledToken_ThrowsImmediately()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1}""")!;
        var patch = (JsonObject)JsonNode.Parse("""{"a":2}""")!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => PatchApply.StrategicMergePatch(original, patch, cancellationToken: cts.Token));
    }

    [TestMethod]
    public void ThreeWay_PreCancelledToken_ThrowsImmediately()
    {
        var doc = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","spec":{"a":1}}""")!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => ThreeWayMerge.CreateThreeWayMergePatch(doc, doc, doc, cancellationToken: cts.Token));
    }

    [TestMethod]
    public void ExtensionMethods_AcceptCancellationToken()
    {
        var orig = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "x" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };
        var mod = orig.DeepClone();
        mod.Spec.Replicas = 4;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => orig.CreateStrategicPatch(mod, cancellationToken: cts.Token));
        Assert.ThrowsExactly<OperationCanceledException>(
            () => orig.CreateThreeWayStrategicPatch(mod, orig, cancellationToken: cts.Token));
    }
}

internal static class V1DeploymentExtensions
{
    public static V1Deployment DeepClone(this V1Deployment source) =>
        k8s.KubernetesJson.Deserialize<V1Deployment>(k8s.KubernetesJson.Serialize(source))!;
}
