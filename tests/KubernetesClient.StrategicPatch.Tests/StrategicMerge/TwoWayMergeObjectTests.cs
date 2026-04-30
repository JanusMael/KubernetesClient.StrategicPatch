using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests.StrategicMerge;

/// <summary>
/// Stage 3 corpus: object-and-primitive diffs only. Lists are tested under atomic-replace semantics
/// only (Stage 4 introduces directive-aware list handling). Cases tagged with
/// <c>[TestCategory("go-parity")]</c> mirror behavior covered by Go's
/// <c>strategicpatch.TestStrategicMergePatch</c>; the rest are C#-side coverage of
/// <see cref="TwoWayMerge"/>'s control flow (options, throw paths, schema-miss).
/// </summary>
[TestClass]
public sealed class TwoWayMergeObjectTests
{
    [TestMethod]
    [TestCategory("go-parity")]
    [DataRow("identical-empty",     "{}",                              "{}",                              null)]
    [DataRow("identical-flat",      """{"a":1}""",                     """{"a":1}""",                     null)]
    [DataRow("identical-nested",    """{"spec":{"replicas":3}}""",     """{"spec":{"replicas":3}}""",     null)]
    [DataRow("add-flat",            """{"a":1}""",                     """{"a":1,"b":2}""",               """{"b":2}""")]
    [DataRow("add-nested-key",      """{"spec":{"a":1}}""",            """{"spec":{"a":1,"b":2}}""",      """{"spec":{"b":2}}""")]
    [DataRow("add-nested-object",   """{}""",                          """{"spec":{"a":1}}""",            """{"spec":{"a":1}}""")]
    [DataRow("change-primitive",    """{"replicas":1}""",              """{"replicas":2}""",              """{"replicas":2}""")]
    [DataRow("change-string",       """{"name":"old"}""",              """{"name":"new"}""",              """{"name":"new"}""")]
    [DataRow("change-bool",         """{"flag":false}""",              """{"flag":true}""",               """{"flag":true}""")]
    [DataRow("change-null-to-val",  """{"x":null}""",                  """{"x":1}""",                     """{"x":1}""")]
    [DataRow("change-val-to-null",  """{"x":1}""",                     """{"x":null}""",                  """{"x":null}""")]
    [DataRow("delete-by-missing",   """{"a":1,"b":2}""",               """{"a":1}""",                     """{"b":null}""")]
    [DataRow("delete-nested-key",   """{"spec":{"a":1,"b":2}}""",      """{"spec":{"a":1}}""",            """{"spec":{"b":null}}""")]
    [DataRow("type-change-obj-prim","""{"x":{"a":1}}""",                """{"x":1}""",                     """{"x":1}""")]
    [DataRow("type-change-prim-obj","""{"x":1}""",                     """{"x":{"a":1}}""",                """{"x":{"a":1}}""")]
    [DataRow("type-change-obj-arr", """{"x":{"a":1}}""",                """{"x":[1,2]}""",                  """{"x":[1,2]}""")]
    [DataRow("nested-no-diff",      """{"spec":{"a":1,"b":{"c":2}}}""", """{"spec":{"a":1,"b":{"c":2}}}""", null)]
    [DataRow("nested-deep-change",  """{"a":{"b":{"c":1}}}""",         """{"a":{"b":{"c":2}}}""",         """{"a":{"b":{"c":2}}}""")]
    [DataRow("array-replace-when-diff",   """{"l":[1,2,3]}""",         """{"l":[1,2,4]}""",               """{"l":[1,2,4]}""")]
    [DataRow("array-no-diff-omitted",     """{"l":[1,2,3]}""",         """{"l":[1,2,3]}""",                null)]
    [DataRow("array-length-change",       """{"l":[1,2]}""",            """{"l":[1,2,3]}""",                """{"l":[1,2,3]}""")]
    [DataRow("number-canonicalization",   """{"n":1}""",                """{"n":1.0}""",                    null)]
    [DataRow("siblings-mixed",      """{"a":1,"b":2,"c":3}""",         """{"a":1,"b":99,"d":4}""",        """{"b":99,"c":null,"d":4}""")]
    [DataRow("empty-orig-add-all", """{}""",                            """{"a":1,"b":{"c":2}}""",         """{"a":1,"b":{"c":2}}""")]
    [DataRow("empty-mod-delete-all","""{"a":1,"b":2}""",                """{}""",                          """{"a":null,"b":null}""")]
    [DataRow("retain-key-name-not-changed", """{"name":"a","value":"b"}""", """{"name":"a","value":"c"}""", """{"value":"c"}""")]
    [DataRow("string-key-with-slash","""{"a/b":1}""",                  """{"a/b":2}""",                    """{"a/b":2}""")]
    [DataRow("unicode-key",          """{"日本":1}""",                   """{"日本":2}""",                    """{"日本":2}""")]
    [DataRow("nested-add-only",      """{"a":{}}""",                    """{"a":{"b":1}}""",               """{"a":{"b":1}}""")]
    [DataRow("nested-delete-only",   """{"a":{"b":1}}""",               """{"a":{}}""",                    """{"a":{"b":null}}""")]
    [DataRow("apiVersion-kind-untouched", """{"apiVersion":"v1","kind":"Pod","data":1}""",
                                          """{"apiVersion":"v1","kind":"Pod","data":2}""",
                                          """{"data":2}""")]
    public void Diff_TableDriven(string name, string originalJson, string modifiedJson, string? expectedJson)
    {
        _ = name; // surfaces in test names via DataRow; intentionally unused otherwise
        var original = (JsonObject?)JsonNode.Parse(originalJson);
        var modified = (JsonObject?)JsonNode.Parse(modifiedJson);
        var expected = expectedJson is null ? null : (JsonObject?)JsonNode.Parse(expectedJson);

        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        if (expected is null)
        {
            Assert.IsNull(patch, $"[{name}] expected no diff but got: {patch?.ToJsonString()}");
            return;
        }
        Assert.IsNotNull(patch, $"[{name}] expected a patch but got null");
        Assert.IsTrue(JsonNodeEquality.DeepEquals(expected, patch),
            $"[{name}] mismatch:{Environment.NewLine}  expected: {expected.ToJsonString()}{Environment.NewLine}  actual:   {patch!.ToJsonString()}");
    }

    [TestMethod]
    public void ApiVersion_Mismatch_Throws()
    {
        var original = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod"}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"apiVersion":"apps/v1","kind":"Pod"}""")!;
        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => TwoWayMerge.CreateTwoWayMergePatch(original, modified));
        StringAssert.Contains(ex.Message, "apiVersion");
    }

    [TestMethod]
    public void Kind_Mismatch_Throws()
    {
        var original = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod"}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"ConfigMap"}""")!;
        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => TwoWayMerge.CreateTwoWayMergePatch(original, modified));
        StringAssert.Contains(ex.Message, "kind");
    }

    [TestMethod]
    public void RootIdentity_SparseSide_AllowsMissingFields()
    {
        // modified has only kind, no apiVersion — matches a sparse caller payload.
        var original = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","spec":{"a":1}}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"kind":"Pod","spec":{"a":2}}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
    }

    [TestMethod]
    public void IgnoreNullValuesInModified_SuppressesMissingKeyDeletes()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1,"b":2,"c":{"d":3}}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a":1}""")!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { IgnoreNullValuesInModified = true });

        // No deletes — original-side b and c.d are left alone.
        Assert.IsNull(patch);
    }

    [TestMethod]
    public void IgnoreNullValuesInModified_SuppressesExplicitNullsInModified()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1,"b":2}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a":1,"b":null}""")!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { IgnoreNullValuesInModified = true });

        // b's explicit null is suppressed; nothing else changed → no patch.
        Assert.IsNull(patch);
    }

    [TestMethod]
    public void IgnoreNullValuesInModified_StillEmitsRealChanges()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1,"b":2}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a":1,"b":99}""")!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { IgnoreNullValuesInModified = true });

        Assert.IsNotNull(patch);
        Assert.AreEqual(99, (int)patch!["b"]!);
    }

    [TestMethod]
    public void OptimisticConcurrency_InjectsUidAndResourceVersion()
    {
        var original = (JsonObject)JsonNode.Parse("""
            {"apiVersion":"v1","kind":"Pod","metadata":{"uid":"abc","resourceVersion":"42","name":"x"},"spec":{"a":1}}
            """)!;
        var modified = (JsonObject)JsonNode.Parse("""
            {"apiVersion":"v1","kind":"Pod","metadata":{"name":"x"},"spec":{"a":2}}
            """)!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { EnforceOptimisticConcurrency = true });

        Assert.IsNotNull(patch);
        var meta = (JsonObject)patch!["metadata"]!;
        Assert.AreEqual("abc", (string)meta["uid"]!);
        Assert.AreEqual("42", (string)meta["resourceVersion"]!);
    }

    [TestMethod]
    public void OptimisticConcurrency_OmittedWhenOriginalLacksUid()
    {
        var original = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","spec":{"a":1}}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","spec":{"a":2}}""")!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { EnforceOptimisticConcurrency = true });

        Assert.IsNotNull(patch);
        Assert.IsFalse(patch!.ContainsKey("metadata"));
    }

    [TestMethod]
    public void OptimisticConcurrency_DoesNotOverwriteExistingPatchMetadata()
    {
        var original = (JsonObject)JsonNode.Parse("""
            {"apiVersion":"v1","kind":"Pod","metadata":{"uid":"abc","resourceVersion":"42","name":"old"}}
            """)!;
        var modified = (JsonObject)JsonNode.Parse("""
            {"apiVersion":"v1","kind":"Pod","metadata":{"uid":"abc","resourceVersion":"42","name":"new"}}
            """)!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            original, modified, new StrategicPatchOptions { EnforceOptimisticConcurrency = true });

        Assert.IsNotNull(patch);
        var meta = (JsonObject)patch!["metadata"]!;
        Assert.AreEqual("new", (string)meta["name"]!);
        Assert.AreEqual("abc", (string)meta["uid"]!);
        Assert.AreEqual("42", (string)meta["resourceVersion"]!);
    }

    [TestMethod]
    public void NoSchemaProvider_FallsBackToRfc7396()
    {
        // Without a schema provider every subtree is RFC 7396 (JSON Merge Patch). This is the
        // graceful default — the engine still produces correct output, just without strategic
        // list directives.
        var original = (JsonObject)JsonNode.Parse("""{"a":1,"l":[1,2,3]}""")!;
        var modified = (JsonObject)JsonNode.Parse("""{"a":1,"l":[1,2,4]}""")!;

        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
        Assert.IsNotNull(patch);
        // Whole-list replace under RFC 7396.
        var arr = (JsonArray)patch!["l"]!;
        Assert.AreEqual(3, arr.Count);
        Assert.AreEqual(4, (int)arr[2]!);
    }

    [TestMethod]
    public void BothSidesNull_ReturnsNullPatch()
    {
        Assert.IsNull(TwoWayMerge.CreateTwoWayMergePatch(null, null));
    }

    [TestMethod]
    public void OriginalNull_AddsAllFromModified()
    {
        var modified = (JsonObject)JsonNode.Parse("""{"a":1,"b":{"c":2}}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(null, modified);
        Assert.IsNotNull(patch);
        Assert.AreEqual(1, (int)patch!["a"]!);
        Assert.AreEqual(2, (int)patch!["b"]!["c"]!);
    }

    [TestMethod]
    public void ModifiedNull_DeletesAllFromOriginal()
    {
        var original = (JsonObject)JsonNode.Parse("""{"a":1,"b":2}""")!;
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, null);
        Assert.IsNotNull(patch);
        Assert.IsNull(patch!["a"]);
        Assert.IsNull(patch!["b"]);
        Assert.IsTrue(patch!.ContainsKey("a"));
        Assert.IsTrue(patch!.ContainsKey("b"));
    }
}
