namespace KubernetesClient.StrategicPatch.Tests;

[TestClass]
public sealed class JsonPointerTests
{
    [TestMethod]
    public void Root_HasNoSegments()
    {
        Assert.AreEqual(0, JsonPointer.Root.Count);
        Assert.IsTrue(JsonPointer.Root.IsRoot);
        Assert.AreEqual(string.Empty, JsonPointer.Root.ToString());
    }

    [TestMethod]
    public void Parse_EmptyString_IsRoot()
    {
        var p = JsonPointer.Parse(string.Empty);
        Assert.IsTrue(p.IsRoot);
    }

    [TestMethod]
    public void Parse_WithoutLeadingSlash_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() => JsonPointer.Parse("foo"));
    }

    [TestMethod]
    [DataRow("/spec/template/spec/containers", new[] { "spec", "template", "spec", "containers" })]
    [DataRow("/a", new[] { "a" })]
    [DataRow("/", new[] { "" })]
    public void Parse_SimpleSegments(string input, string[] expected)
    {
        var p = JsonPointer.Parse(input);
        CollectionAssert.AreEqual(expected, p.ToArray());
    }

    [TestMethod]
    public void Parse_DecodesEscapes()
    {
        // ~1 is '/', ~0 is '~'.
        var p = JsonPointer.Parse("/foo~1bar/x~0y");
        CollectionAssert.AreEqual(new[] { "foo/bar", "x~y" }, p.ToArray());
    }

    [TestMethod]
    public void ToString_RoundTrips_WithEscaping()
    {
        var p = JsonPointer.FromSegments(["a/b", "c~d"]);
        var s = p.ToString();
        Assert.AreEqual("/a~1b/c~0d", s);
        var parsed = JsonPointer.Parse(s);
        Assert.AreEqual(p, parsed);
    }

    [TestMethod]
    public void Append_ProducesNewPointer_WithoutMutating()
    {
        var p = JsonPointer.FromSegments(["spec"]);
        var p2 = p.Append("containers");
        Assert.AreEqual(1, p.Count);
        Assert.AreEqual(2, p2.Count);
        Assert.AreEqual("containers", p2[1]);
    }

    [TestMethod]
    public void Equality_IgnoresSegmentArrayIdentity()
    {
        var a = JsonPointer.FromSegments(["x", "y"]);
        var b = JsonPointer.FromSegments(["x", "y"]);
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.IsTrue(a == b);
    }

    [TestMethod]
    public void Equality_IsCaseSensitive()
    {
        var a = JsonPointer.FromSegments(["spec"]);
        var b = JsonPointer.FromSegments(["Spec"]);
        Assert.AreNotEqual(a, b);
    }
}
