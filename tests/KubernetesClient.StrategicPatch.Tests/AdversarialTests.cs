using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Adversarial inputs — payloads designed to break the diff/apply engines if they assumed
/// well-formed K8s structure. These don't appear in the Go corpus; they're our additional
/// hardening surface.
/// </summary>
[TestClass]
public sealed class AdversarialTests
{
    [TestMethod]
    public void Diff_VeryWideObject_ScalesToManyKeys()
    {
        var original = new JsonObject();
        var modified = new JsonObject();
        for (var i = 0; i < 5_000; i++)
        {
            original[$"k{i}"] = i;
            modified[$"k{i}"] = i + 1;
        }

        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.HasCount(5_000, patch);
    }

    [TestMethod]
    public void Diff_KeyWithEmbeddedDirectiveMarker_TreatedAsDataNotDirective()
    {
        // A user property literally named "$patch" must not be misinterpreted as a directive.
        var original = (JsonObject)JsonNode.Parse("""{"data":{"$patch":"someUserValue"}}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"data":{"$patch":"someUserValue2"}}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        // The $patch literal survives in the diff value, not interpreted as delete/replace.
        var inner = (JsonObject)patch!["data"]!;
        Assert.AreEqual("someUserValue2", (string)inner["$patch"]!);
    }

    [TestMethod]
    public void Diff_PropertyNamesWithJsonPointerSpecialChars_RoundTrip()
    {
        // Keys containing '/' and '~' must round-trip cleanly through JsonPointer escaping
        // when used in error / diagnostic messages.
        var original = (JsonObject)JsonNode.Parse("""{"a/b":1,"c~d":2,"~/~/":3}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a/b":99,"c~d":2,"~/~/":3,"extra":4}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual(99, (int)patch!["a/b"]!);
        Assert.AreEqual(4, (int)patch["extra"]!);
    }

    [TestMethod]
    public void Diff_VeryLongStringValue_HandledWithoutAllocationStorm()
    {
        var huge = new string('x', 200_000);
        var original = new JsonObject { ["s"] = huge };
        var modified = new JsonObject { ["s"] = huge + "y" };
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual(200_001, ((string)patch!["s"]!).Length);
    }

    [TestMethod]
    public void Diff_EmptyKey_PreservedAsLegitimateField()
    {
        // RFC 8259 allows zero-length keys. We don't reject them.
        var original = (JsonObject)JsonNode.Parse("""{"":1}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"":2}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual(2, (int)patch![""]!);
    }

    [TestMethod]
    public void Apply_PatchWithEmbeddedSetElementOrderForUnknownField_NoOps()
    {
        // The patch references a $setElementOrder/foo where `foo` doesn't exist in original.
        // Must not crash; the directive simply has no list to reorder.
        var original = (JsonObject)JsonNode.Parse("""{"data":{"k":"v"}}""")!;
        var patch = (JsonObject)JsonNode.Parse(
            """{"data":{"$setElementOrder/missing":[],"k":"v2"}}""")!;
        var result = PatchApply.StrategicMergePatch(original, patch);
        Assert.AreEqual("v2", (string)result["data"]!["k"]!);
        Assert.IsFalse(((JsonObject)result["data"]!).ContainsKey("missing"));
    }

    [TestMethod]
    public void Apply_PatchWithUnknownDollarPatchValue_Throws()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1}""")!;
        var patch = (JsonObject)JsonNode.Parse("""{"$patch":"definitelyNotADirective"}""")!;
        Assert.ThrowsExactly<StrategicMergePatchException>(
            () => PatchApply.StrategicMergePatch(original, patch));
    }

    [TestMethod]
    public void Diff_TypeAlternation_ObjectThenPrimitiveThenArray_DoesNotCrash()
    {
        var original = (JsonObject)JsonNode.Parse("""{"x":{"a":1}}""")!;
        var middle1 = (JsonObject)JsonNode.Parse("""{"x":42}""")!;
        var middle2 = (JsonObject)JsonNode.Parse("""{"x":[1,2,3]}""")!;
        var middle3 = (JsonObject)JsonNode.Parse("""{"x":"hello"}""")!;

        // Each transition is a wholesale-replace (kind mismatch). Just exercise the full chain.
        Assert.IsNotNull(TwoWayMerge.CreateTwoWayMergePatch(original, middle1));
        Assert.IsNotNull(TwoWayMerge.CreateTwoWayMergePatch(middle1, middle2));
        Assert.IsNotNull(TwoWayMerge.CreateTwoWayMergePatch(middle2, middle3));
        Assert.IsNotNull(TwoWayMerge.CreateTwoWayMergePatch(middle3, original));
    }
}
