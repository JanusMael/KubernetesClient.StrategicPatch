using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests.StrategicMerge;

/// <summary>
/// Stage 6 corpus: server-side strategic-merge patch application. The headline gate is the
/// round-trip property: <c>Apply(original, CreateTwoWay(original, modified)) == modified</c>
/// for every (original, modified) pair drawn from the Stage 3 + Stage 4 corpora. Specific
/// directive tests pin behaviour for <c>$patch</c>, <c>$retainKeys</c>,
/// <c>$setElementOrder</c>, and <c>$deleteFromPrimitiveList</c>.
/// </summary>
[TestClass]
public sealed class PatchApplyTests
{
    private static StrategicPatchOptions Options() => new()
    {
        SchemaProvider = TestSchemas.DeploymentSchemaProvider(),
    };

    private static JsonObject Apply(JsonObject? original, JsonObject patch, StrategicPatchOptions? opts = null) =>
        PatchApply.StrategicMergePatch(original, patch, opts ?? Options());

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ---- Round-trip property over the Stage 3 corpus ----------------------------------------

    [TestMethod]
    [TestCategory("round-trip")]
    [DataRow("identical-empty",     "{}",                              "{}")]
    [DataRow("identical-flat",      """{"a":1}""",                     """{"a":1}""")]
    [DataRow("identical-nested",    """{"spec":{"replicas":3}}""",     """{"spec":{"replicas":3}}""")]
    [DataRow("add-flat",            """{"a":1}""",                     """{"a":1,"b":2}""")]
    [DataRow("add-nested-key",      """{"spec":{"a":1}}""",            """{"spec":{"a":1,"b":2}}""")]
    [DataRow("change-primitive",    """{"replicas":1}""",              """{"replicas":2}""")]
    [DataRow("change-string",       """{"name":"old"}""",              """{"name":"new"}""")]
    [DataRow("change-bool",         """{"flag":false}""",              """{"flag":true}""")]
    [DataRow("change-null-to-val",  """{"x":null}""",                  """{"x":1}""")]
    // Note: change-val-to-null is intentionally excluded — see ExplicitNull_InModified_BehavesAsDelete.
    // SMP cannot distinguish "field is null" from "field is absent" in a patch's wire format, and
    // the same limitation applies to the Go reference and to RFC 7396.
    [DataRow("delete-by-missing",   """{"a":1,"b":2}""",               """{"a":1}""")]
    [DataRow("delete-nested-key",   """{"spec":{"a":1,"b":2}}""",      """{"spec":{"a":1}}""")]
    [DataRow("type-change-obj-prim","""{"x":{"a":1}}""",                """{"x":1}""")]
    [DataRow("type-change-prim-obj","""{"x":1}""",                     """{"x":{"a":1}}""")]
    [DataRow("type-change-obj-arr", """{"x":{"a":1}}""",                """{"x":[1,2]}""")]
    [DataRow("nested-deep-change",  """{"a":{"b":{"c":1}}}""",         """{"a":{"b":{"c":2}}}""")]
    [DataRow("array-replace-when-diff",   """{"l":[1,2,3]}""",         """{"l":[1,2,4]}""")]
    [DataRow("array-length-change",       """{"l":[1,2]}""",            """{"l":[1,2,3]}""")]
    [DataRow("siblings-mixed",      """{"a":1,"b":2,"c":3}""",         """{"a":1,"b":99,"d":4}""")]
    [DataRow("empty-orig-add-all", """{}""",                            """{"a":1,"b":{"c":2}}""")]
    [DataRow("empty-mod-delete-all","""{"a":1,"b":2}""",                """{}""")]
    [DataRow("string-key-with-slash","""{"a/b":1}""",                  """{"a/b":2}""")]
    [DataRow("unicode-key",          """{"日本":1}""",                   """{"日本":2}""")]
    [DataRow("nested-add-only",      """{"a":{}}""",                    """{"a":{"b":1}}""")]
    [DataRow("nested-delete-only",   """{"a":{"b":1}}""",               """{"a":{}}""")]
    public void RoundTrip_NoSchema(string name, string originalJson, string modifiedJson)
    {
        AssertRoundTrip(name, originalJson, modifiedJson, opts: new StrategicPatchOptions());
    }

    // ---- Round-trip property over the Stage 4 list-bearing corpus ---------------------------

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Containers_ChangeImage()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx:1.0"}]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx:2.0"}]
            }}}}
            """;
        AssertRoundTrip("containers-change", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Containers_AddSidecar()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"}]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        AssertRoundTrip("containers-add", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Containers_RemoveOne()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"}]
            }}}}
            """;
        AssertRoundTrip("containers-remove", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Containers_Reorder()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"sidecar","image":"envoy"},{"name":"web","image":"nginx"}]
            }}}}
            """;
        AssertRoundTrip("containers-reorder", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Containers_NestedPortsChangeAddRemove()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx",
                "ports":[
                  {"containerPort":80,"protocol":"TCP","name":"http"},
                  {"containerPort":443,"protocol":"TCP","name":"https"}
                ]
              }]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx",
                "ports":[
                  {"containerPort":80,"protocol":"TCP","name":"http-renamed"},
                  {"containerPort":8080,"protocol":"TCP","name":"alt"}
                ]
              }]
            }}}}
            """;
        AssertRoundTrip("nested-ports", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Finalizers_AddDelete()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b","c"]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","c","d"]
            }}}}
            """;
        AssertRoundTrip("finalizers", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_Tolerations_AtomicReplace()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "tolerations":[{"key":"a","operator":"Equal"},{"key":"b","operator":"Exists"}]
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "tolerations":[{"key":"c","operator":"Exists"}]
            }}}}
            """;
        AssertRoundTrip("tolerations", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_NodeSelector_MapField()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "nodeSelector":{"zone":"a","tier":"web"}
            }}}}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "nodeSelector":{"zone":"b"}
            }}}}
            """;
        AssertRoundTrip("nodeSelector", Original, Modified);
    }

    [TestMethod]
    [TestCategory("round-trip")]
    public void RoundTrip_RetainKeys_DeploymentStrategy()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{
              "strategy":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":"25%","maxUnavailable":"25%"}}
            }}
            """;
        const string Modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{
              "strategy":{"type":"Recreate"}
            }}
            """;
        AssertRoundTrip("retainKeys", Original, Modified);
    }

    // ---- Targeted directive apply behaviour --------------------------------------------------

    [TestMethod]
    public void Apply_DollarPatchReplace_OverwritesSubtree()
    {
        var original = Parse("""{"spec":{"a":1,"b":2}}""");
        var patch    = Parse("""{"spec":{"$patch":"replace","c":3}}""");
        var result   = Apply(original, patch, new StrategicPatchOptions());
        Assert.IsTrue(JsonNodeEquality.DeepEquals(Parse("""{"spec":{"c":3}}"""), result),
            $"got {result.ToJsonString()}");
    }

    [TestMethod]
    public void Apply_DollarPatchDelete_ClearsSubtree()
    {
        var original = Parse("""{"spec":{"a":1,"b":2}}""");
        var patch    = Parse("""{"spec":{"$patch":"delete"}}""");
        var result   = Apply(original, patch, new StrategicPatchOptions());
        Assert.IsTrue(JsonNodeEquality.DeepEquals(Parse("""{"spec":{}}"""), result),
            $"got {result.ToJsonString()}");
    }

    [TestMethod]
    public void Apply_DeleteFromPrimitiveList_RemovesNamedScalars()
    {
        var original = Parse("""
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b","c","d"]
            }}}}
            """);
        var patch = Parse("""
            {"spec":{"template":{"spec":{
              "$deleteFromPrimitiveList/finalizers":["b","d"]
            }}}}
            """);
        var result = Apply(original, patch);
        var finalizers = (JsonArray)result["spec"]!["template"]!["spec"]!["finalizers"]!;
        var values = finalizers.Select(n => (string)n!).ToArray();
        CollectionAssert.AreEqual(new[] { "a", "c" }, values);
    }

    [TestMethod]
    public void Apply_PatchDeleteElement_RemovesByMergeKey()
    {
        var original = Parse("""
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """);
        var patch = Parse("""
            {"spec":{"template":{"spec":{
              "containers":[{"$patch":"delete","name":"sidecar"}]
            }}}}
            """);
        var result = Apply(original, patch);
        var containers = (JsonArray)result["spec"]!["template"]!["spec"]!["containers"]!;
        Assert.HasCount(1, containers);
        Assert.AreEqual("web", (string)containers[0]!["name"]!);
    }

    [TestMethod]
    public void Apply_RetainKeys_DropsKeysNotListed()
    {
        var original = Parse("""{"spec":{"strategy":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":"25%"}}}}""");
        var patch = Parse("""
            {"spec":{"strategy":{
              "$retainKeys":["type"],
              "rollingUpdate":null,
              "type":"Recreate"
            }}}
            """);
        var result = Apply(original, patch);
        var strategy = (JsonObject)result["spec"]!["strategy"]!;
        Assert.AreEqual("Recreate", (string)strategy["type"]!);
        Assert.IsFalse(strategy.ContainsKey("rollingUpdate"));
    }

    [TestMethod]
    public void Apply_NullPatchValue_DeletesKey()
    {
        var original = Parse("""{"a":1,"b":2}""");
        var patch    = Parse("""{"b":null}""");
        var result   = Apply(original, patch, new StrategicPatchOptions());
        Assert.IsTrue(JsonNodeEquality.DeepEquals(Parse("""{"a":1}"""), result));
    }

    [TestMethod]
    public void ExplicitNull_InModified_BehavesAsDelete()
    {
        // Documented SMP semantic: {x: null} in a patch is a delete marker, so round-tripping
        // a `modified` that asserts {x: null} drops the key entirely. Same as the Go reference
        // and RFC 7396; pinned here so a regression on this collapse would fail loudly.
        var original = Parse("""{"x":1}""");
        var modified = Parse("""{"x":null}""");
        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified, new StrategicPatchOptions());
        Assert.IsNotNull(patch);
        Assert.IsTrue(patch!.ContainsKey("x"));
        Assert.IsNull(patch["x"]);

        var applied = Apply(original, patch, new StrategicPatchOptions());
        Assert.IsFalse(applied.ContainsKey("x"));
    }

    [TestMethod]
    public void Apply_OriginalNull_ReturnsClonedPatchWithoutDirectives()
    {
        var patch  = Parse("""{"a":1,"b":{"c":2}}""");
        var result = Apply(null, patch, new StrategicPatchOptions());
        Assert.IsTrue(JsonNodeEquality.DeepEquals(Parse("""{"a":1,"b":{"c":2}}"""), result));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static void AssertRoundTrip(string name, string originalJson, string modifiedJson, StrategicPatchOptions? opts = null)
    {
        opts ??= Options();
        var original = (JsonObject?)JsonNode.Parse(originalJson);
        var modified = (JsonObject?)JsonNode.Parse(modifiedJson);

        var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified, opts);
        if (patch is null)
        {
            // No diff → applying nothing should also yield modified == original.
            Assert.IsTrue(JsonNodeEquality.DeepEquals(original, modified),
                $"[{name}] CreateTwoWayMergePatch returned null but original != modified");
            return;
        }

        var applied = PatchApply.StrategicMergePatch(original, patch, opts);
        Assert.IsTrue(JsonNodeEquality.DeepEquals(modified, applied),
            $"[{name}] round-trip mismatch:{Environment.NewLine}"
            + $"  original: {original?.ToJsonString()}{Environment.NewLine}"
            + $"  modified: {modified?.ToJsonString()}{Environment.NewLine}"
            + $"  patch:    {patch.ToJsonString()}{Environment.NewLine}"
            + $"  applied:  {applied.ToJsonString()}");
    }
}
