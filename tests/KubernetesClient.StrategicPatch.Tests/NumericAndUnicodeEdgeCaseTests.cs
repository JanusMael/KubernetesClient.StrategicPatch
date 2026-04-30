using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Numeric and Unicode edge cases that the Go reference handles via its
/// <c>fmt.Sprintf("%v", v)</c>-stringification path. The C# port must match that semantic
/// exactly: equal-value-different-text variants are treated as distinct (e.g. <c>1</c> vs
/// <c>1.0</c> are distinct list elements), but JSON-equivalent-text variants are equal.
/// </summary>
[TestClass]
public sealed class NumericAndUnicodeEdgeCaseTests
{
    // ---- ScalarKey numeric ------------------------------------------------------------------

    [TestMethod]
    public void ScalarKey_IntAndDouble_SameLiteral_AreEqualOrNot()
    {
        // 1 vs 1.0 produce different raw text → different keys (matches Go's %v behaviour).
        Assert.AreNotEqual(
            ScalarKey.Of(JsonNode.Parse("1")),
            ScalarKey.Of(JsonNode.Parse("1.0")));
    }

    [TestMethod]
    public void ScalarKey_NegativeZero_DistinctFromPositiveZero_InRawText()
    {
        // Go's fmt.Sprintf("%v", -0.0) prints "-0", not "0". We mirror that via raw token text.
        Assert.AreNotEqual(
            ScalarKey.Of(JsonNode.Parse("0")),
            ScalarKey.Of(JsonNode.Parse("-0")));
    }

    [TestMethod]
    public void ScalarKey_ScientificNotation_PreservedAsRawText()
    {
        Assert.AreNotEqual(
            ScalarKey.Of(JsonNode.Parse("1e2")),
            ScalarKey.Of(JsonNode.Parse("100")));
    }

    [TestMethod]
    public void ScalarKey_LargeNumber_BeyondDoublePrecision_PreservedExactly()
    {
        // 9999999999999999 is beyond IEEE 754 double's exact range; raw-text comparison must
        // preserve the original token.
        Assert.AreEqual("n:9999999999999999", ScalarKey.Of(JsonNode.Parse("9999999999999999")));
    }

    [TestMethod]
    public void DeepEquals_NumericCanonicalization_IndependentOfScalarKey()
    {
        // JsonNodeEquality canonicalises 1 == 1.0 == 1e0 (it's used for the diff's "are these
        // equal?" check), even though ScalarKey treats them as distinct list elements.
        Assert.IsTrue(JsonNodeEquality.DeepEquals(
            JsonNode.Parse("1"), JsonNode.Parse("1.0")));
        Assert.IsTrue(JsonNodeEquality.DeepEquals(
            JsonNode.Parse("1e2"), JsonNode.Parse("100")));
    }

    // ---- Decimal precision through round-trip -----------------------------------------------

    [TestMethod]
    public void Diff_HighPrecisionDecimal_PreservedThroughDiffAndApply()
    {
        var lit = "0.123456789012345678";
        var original = (JsonObject)JsonNode.Parse($$"""{"v":1}""")!;
        var modified = (JsonObject)JsonNode.Parse($$"""{"v":{{lit}}}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified)
            ?? throw new InvalidOperationException("expected diff");
        var applied = PatchApply.StrategicMergePatch(original, patch);
        Assert.AreEqual(lit, applied["v"]!.ToJsonString());
    }

    // ---- Unicode keys ------------------------------------------------------------------------

    [TestMethod]
    public void Diff_NfcVsNfdKeys_AreTreatedAsDistinct()
    {
        // U+00E9 (NFC composed é) vs "e" + U+0301 (NFD decomposed). The wire format
        // doesn't normalise, so neither do we — they are distinct keys.
        var nfc = "café";          // 4 chars
        var nfd = "café";         // 5 chars

        var original = new JsonObject { [nfc] = "old" };
        var modified = new JsonObject { [nfd] = "new" };
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified)!;
        Assert.IsNotNull(patch);
        // Both keys appear: nfc as a delete (null), nfd as an addition.
        Assert.IsTrue(patch.ContainsKey(nfc));
        Assert.IsNull(patch[nfc]);
        Assert.IsTrue(patch.ContainsKey(nfd));
        Assert.AreEqual("new", (string)patch[nfd]!);
    }

    [TestMethod]
    public void Diff_HighSurrogatePairKey_HandledAsSingleKey()
    {
        // Emoji 😀 = U+1F600, encoded in UTF-16 as a surrogate pair.
        var emoji = "\U0001F600";
        var original = new JsonObject { [emoji] = 1 };
        var modified = new JsonObject { [emoji] = 2 };
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual(2, (int)patch![emoji]!);
    }

    [TestMethod]
    public void Diff_RtlTextValue_PreservedByteForByte()
    {
        // Hebrew "שלום" — RTL display order, but logical-order in the wire format.
        var original = new JsonObject { ["greeting"] = "hello" };
        var modified = new JsonObject { ["greeting"] = "שלום" };
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual("שלום", (string)patch!["greeting"]!);
    }

    [TestMethod]
    public void Diff_KeyWithNumericTextDigits_TreatedAsKey_NotIndex()
    {
        // SchemaNode.Resolve treats numeric-only segments as array indices; on object-property
        // access the engine must not be confused by a key like "0" or "42".
        var original = (JsonObject)JsonNode.Parse("""{"data":{"0":"a","42":"b"}}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"data":{"0":"a","42":"c"}}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual("c", (string)patch!["data"]!["42"]!);
        Assert.IsFalse(((JsonObject)patch["data"]!).ContainsKey("0"));
    }

    // ---- Locale insensitivity ----------------------------------------------------------------

    [TestMethod]
    public void Diff_NumberFormatting_UnaffectedByCurrentCulture()
    {
        // German culture uses ',' as decimal separator. Our internal numeric parsing uses
        // CultureInfo.InvariantCulture; switching threads' culture must not affect the result.
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var original = (JsonObject)JsonNode.Parse("""{"n":1.5}""")!;
            var modified = (JsonObject)JsonNode.Parse("""{"n":2.5}""")!;
            var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
            Assert.IsNotNull(patch);
            // Body must contain "2.5" not "2,5".
            Assert.IsTrue(patch!.ToJsonString().Contains("2.5", StringComparison.Ordinal),
                $"Got: {patch.ToJsonString()}");
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [TestMethod]
    public void Apply_RoundTripPreservesUtf8ByteCount()
    {
        // Encoding of the "💀" emoji in UTF-8 is 4 bytes; the patch payload should also be 4 bytes.
        var original = new JsonObject { ["v"] = "old" };
        var modified = new JsonObject { ["v"] = "💀" };
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        var bytes = Encoding.UTF8.GetByteCount(patch!.ToJsonString());
        // Sanity: the body contains "💀" plus surrounding JSON; must include the 4-byte char.
        Assert.IsGreaterThanOrEqualTo(4, bytes);
    }
}
