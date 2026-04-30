using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;

namespace KubernetesClient.StrategicPatch.Tests.Internal;

[TestClass]
public sealed class JsonNodeEqualityTests
{
    [TestMethod]
    public void BothNull_AreEqual()
    {
        Assert.IsTrue(JsonNodeEquality.DeepEquals(null, null));
    }

    [TestMethod]
    public void NullVsNonNull_NotEqual()
    {
        Assert.IsFalse(JsonNodeEquality.DeepEquals(null, JsonNode.Parse("1")));
        Assert.IsFalse(JsonNodeEquality.DeepEquals(JsonNode.Parse("1"), null));
    }

    [TestMethod]
    public void SameInstance_ShortCircuitsToTrue()
    {
        var node = JsonNode.Parse("{}");
        Assert.IsTrue(JsonNodeEquality.DeepEquals(node, node));
    }

    [TestMethod]
    [DataRow("\"a\"", "\"a\"", true)]
    [DataRow("\"a\"", "\"b\"", false)]
    [DataRow("\"A\"", "\"a\"", false)]
    [DataRow("true", "true", true)]
    [DataRow("true", "false", false)]
    [DataRow("null", "null", true)]
    [DataRow("null", "0", false)]
    public void Primitives(string left, string right, bool expected)
    {
        var l = JsonNode.Parse(left);
        var r = JsonNode.Parse(right);
        Assert.AreEqual(expected, JsonNodeEquality.DeepEquals(l, r));
    }

    [TestMethod]
    [DataRow("1", "1", true)]
    [DataRow("1", "1.0", true)]                // canonicalized
    [DataRow("1.0", "1.00", true)]
    [DataRow("1", "2", false)]
    [DataRow("1e2", "100", true)]              // exponent vs literal
    [DataRow("0", "-0", true)]                 // signed zero
    [DataRow("9999999999999999", "9999999999999999", true)]  // beyond double precision; raw-text wins
    public void Numbers_AreCanonicalized(string left, string right, bool expected)
    {
        var l = JsonNode.Parse(left);
        var r = JsonNode.Parse(right);
        Assert.AreEqual(expected, JsonNodeEquality.DeepEquals(l, r));
    }

    [TestMethod]
    public void KindMismatch_NotEqual()
    {
        Assert.IsFalse(JsonNodeEquality.DeepEquals(JsonNode.Parse("\"1\""), JsonNode.Parse("1")));
        Assert.IsFalse(JsonNodeEquality.DeepEquals(JsonNode.Parse("[]"), JsonNode.Parse("{}")));
        Assert.IsFalse(JsonNodeEquality.DeepEquals(JsonNode.Parse("{}"), JsonNode.Parse("\"\"")));
    }

    [TestMethod]
    public void Objects_AreOrderIndependent()
    {
        var a = JsonNode.Parse("""{"a":1,"b":2,"c":3}""");
        var b = JsonNode.Parse("""{"c":3,"a":1,"b":2}""");
        Assert.IsTrue(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void Objects_DifferentKeySet_NotEqual()
    {
        var a = JsonNode.Parse("""{"a":1,"b":2}""");
        var b = JsonNode.Parse("""{"a":1,"c":2}""");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void Objects_DifferentSize_NotEqual()
    {
        var a = JsonNode.Parse("""{"a":1}""");
        var b = JsonNode.Parse("""{"a":1,"b":2}""");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void Objects_NullValueVsMissingKey_NotEqual()
    {
        // SMP cares about this distinction: null means "delete this key", absence means "untouched".
        var a = JsonNode.Parse("""{"a":null}""");
        var b = JsonNode.Parse("""{}""");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void Arrays_AreOrderDependent()
    {
        var a = JsonNode.Parse("[1,2,3]");
        var b = JsonNode.Parse("[3,2,1]");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void Arrays_SameOrder_AreEqual()
    {
        var a = JsonNode.Parse("[1,2,3]");
        var b = JsonNode.Parse("[1,2,3]");
        Assert.IsTrue(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void Arrays_DifferentLengths_NotEqual()
    {
        var a = JsonNode.Parse("[1,2,3]");
        var b = JsonNode.Parse("[1,2,3,4]");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void NestedStructures_RecurseDeeply()
    {
        var a = JsonNode.Parse("""
            {"spec":{"replicas":3,"template":{"spec":{"containers":[{"name":"web","image":"nginx"}]}}}}
            """);
        var b = JsonNode.Parse("""
            {"spec":{"template":{"spec":{"containers":[{"image":"nginx","name":"web"}]}},"replicas":3}}
            """);
        Assert.IsTrue(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void NestedStructures_LeafDifference_PropagatesUp()
    {
        var a = JsonNode.Parse("""{"spec":{"replicas":3}}""");
        var b = JsonNode.Parse("""{"spec":{"replicas":4}}""");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(a, b));
    }

    [TestMethod]
    public void DetachedClone_IsEqualToOriginal()
    {
        var node = JsonNode.Parse("""{"a":[1,{"b":"c"}]}""")!;
        var clone = node.DeepClone();
        Assert.IsTrue(JsonNodeEquality.DeepEquals(node, clone));
    }

    [TestMethod]
    public void Numbers_ConstructedViaJsonValueCreate_FallBackToTextPath()
    {
        // JsonValue.Create wraps the CLR value directly, so TryGetValue<JsonElement> returns false.
        // This exercises the non-JsonElement code path in NumbersEqual.
        JsonNode left = JsonValue.Create(42)!;
        JsonNode right = JsonValue.Create(42.0)!;
        Assert.IsTrue(JsonNodeEquality.DeepEquals(left, right));

        JsonNode otherLeft = JsonValue.Create(1)!;
        JsonNode otherRight = JsonValue.Create(2)!;
        Assert.IsFalse(JsonNodeEquality.DeepEquals(otherLeft, otherRight));
    }

    [TestMethod]
    public void Numbers_OverflowingDecimal_DistinctText_FallBackToDouble_Equal()
    {
        // Both literals overflow decimal but parse to +Infinity in double.
        // Distinct raw text forces the path past the raw-equality short-circuit.
        var l = JsonNode.Parse("1e500");
        var r = JsonNode.Parse("2e500");
        Assert.IsTrue(JsonNodeEquality.DeepEquals(l, r));
    }

    [TestMethod]
    public void Numbers_OneOverflowOneFinite_FallBackToDouble_NotEqual()
    {
        // "1" parses as decimal; "1e500" overflows. Falls to double path: 1.0 vs Infinity.
        var l = JsonNode.Parse("1");
        var r = JsonNode.Parse("1e500");
        Assert.IsFalse(JsonNodeEquality.DeepEquals(l, r));
    }
}
