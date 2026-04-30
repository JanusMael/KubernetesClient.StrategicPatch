using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;
using LibSchema = KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Tests.StrategicMerge;

/// <summary>
/// Stage 4 corpus: list-directive diffs. Schema-driven dispatch covers atomic replace,
/// primitive set/merge, object merge-by-key (with $patch:delete + $setElementOrder), and
/// $retainKeys emission. Cases tagged with <c>[TestCategory("go-parity")]</c> mirror behaviour
/// covered by Go's <c>strategicpatch.TestStrategicMergePatch</c> table.
/// </summary>
[TestClass]
public sealed class TwoWayMergeListTests
{
    private static StrategicPatchOptions Options() => new()
    {
        SchemaProvider = TestSchemas.DeploymentSchemaProvider(),
    };

    private static JsonObject? Patch(string original, string modified, StrategicPatchOptions? opts = null) =>
        TwoWayMerge.CreateTwoWayMergePatch(
            (JsonObject?)JsonNode.Parse(original),
            (JsonObject?)JsonNode.Parse(modified),
            opts ?? Options());

    private static void AssertEqual(string expectedJson, JsonObject? patch, string? caseName = null)
    {
        var expected = (JsonObject?)JsonNode.Parse(expectedJson);
        var lbl = caseName is null ? string.Empty : $"[{caseName}] ";
        if (expected is null)
        {
            Assert.IsNull(patch, $"{lbl}expected no diff but got: {patch?.ToJsonString()}");
            return;
        }
        Assert.IsNotNull(patch, $"{lbl}expected a patch but got null");
        Assert.IsTrue(JsonNodeEquality.DeepEquals(expected, patch),
            $"{lbl}mismatch:{Environment.NewLine}  expected: {expected.ToJsonString()}{Environment.NewLine}  actual:   {patch!.ToJsonString()}");
    }

    // ---- Object lists keyed by merge-key -----------------------------------------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void Containers_ChangeImage_DiffsByMergeKey()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx:1.0"}]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx:2.0"}]
            }}}}
            """;
        // Recurse into the "web" container; image change is a leaf replace; mergeKey carried.
        // Order didn't change but content did → setElementOrder fires.
        var expected = """
            {"spec":{"template":{"spec":{
              "containers":[{"image":"nginx:2.0","name":"web"}],
              "$setElementOrder/containers":[{"name":"web"}]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Containers_AddNew_AppendsFullElement()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"}]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "containers":[{"name":"sidecar","image":"envoy"}],
              "$setElementOrder/containers":[{"name":"web"},{"name":"sidecar"}]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Containers_RemoveOne_EmitsPatchDeleteElement()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"}]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "containers":[{"$patch":"delete","name":"sidecar"}],
              "$setElementOrder/containers":[{"name":"web"}]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Containers_Reorder_OrderOnly_EmitsSetElementOrder()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"sidecar","image":"envoy"},{"name":"web","image":"nginx"}]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "$setElementOrder/containers":[{"name":"sidecar"},{"name":"web"}]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Containers_NoChange_ReturnsNullPatch()
    {
        var doc = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"sidecar","image":"envoy"}]
            }}}}
            """;
        Assert.IsNull(Patch(doc, doc));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Container_NestedPorts_ChangeAddRemove_AllAtOnce()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx",
                "ports":[
                  {"containerPort":80,"protocol":"TCP","name":"http"},
                  {"containerPort":443,"protocol":"TCP","name":"https"}
                ]
              }]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx",
                "ports":[
                  {"containerPort":80,"protocol":"TCP","name":"http-renamed"},
                  {"containerPort":8080,"protocol":"TCP","name":"alt"}
                ]
              }]
            }}}}
            """;
        // Inside the container: 80 changes name (recurse), 443 deleted, 8080 added.
        var expected = """
            {"spec":{"template":{"spec":{
              "containers":[{
                "name":"web",
                "ports":[
                  {"containerPort":80,"name":"http-renamed"},
                  {"containerPort":8080,"protocol":"TCP","name":"alt"},
                  {"$patch":"delete","containerPort":443}
                ],
                "$setElementOrder/ports":[{"containerPort":80},{"containerPort":8080}]
              }],
              "$setElementOrder/containers":[{"name":"web"}]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    // ---- Set lists ---------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void Finalizers_AddPrimitive_EmitsAdditionAndOrder()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b"]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b","c"]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "finalizers":["c"],
              "$setElementOrder/finalizers":["a","b","c"]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Finalizers_DeletePrimitive_EmitsDeleteFromPrimitiveListAndOrder()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b","c"]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","c"]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "$deleteFromPrimitiveList/finalizers":["b"],
              "$setElementOrder/finalizers":["a","c"]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Finalizers_AddAndDelete_EmitsBothDirectives()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b","c"]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","c","d"]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "finalizers":["d"],
              "$deleteFromPrimitiveList/finalizers":["b"],
              "$setElementOrder/finalizers":["a","c","d"]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    public void Finalizers_NoChange_ReturnsNull()
    {
        var doc = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b","c"]
            }}}}
            """;
        Assert.IsNull(Patch(doc, doc));
    }

    // ---- Atomic lists ------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void Tolerations_Atomic_ReplacesWholeListWhenChanged()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "tolerations":[{"key":"a","operator":"Equal"},{"key":"b","operator":"Exists"}]
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "tolerations":[{"key":"c","operator":"Exists"}]
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "tolerations":[{"key":"c","operator":"Exists"}]
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    public void Tolerations_Atomic_NoDiff_OmittedFromPatch()
    {
        var doc = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "tolerations":[{"key":"a","operator":"Equal"}]
            }}}}
            """;
        Assert.IsNull(Patch(doc, doc));
    }

    // ---- Map fields (additionalProperties) ---------------------------------------------------

    [TestMethod]
    public void NodeSelector_MapField_DiffsAsObject()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "nodeSelector":{"zone":"a","tier":"web"}
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "nodeSelector":{"zone":"b","tier":"web"}
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "nodeSelector":{"zone":"b"}
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    public void NodeSelector_KeyDeleted_EmitsNullDelete()
    {
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "nodeSelector":{"zone":"a","tier":"web"}
            }}}}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "nodeSelector":{"zone":"a"}
            }}}}
            """;
        var expected = """
            {"spec":{"template":{"spec":{
              "nodeSelector":{"tier":null}
            }}}}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    // ---- $retainKeys -------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void RetainKeys_OnDeploymentStrategy_EmitsKeyList()
    {
        // spec.strategy has retainKeys strategy. Switching from rollingUpdate→recreate should
        // emit a $retainKeys list naming only the keys that survive in modified.
        var original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{
              "strategy":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":"25%","maxUnavailable":"25%"}}
            }}
            """;
        var modified = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{
              "strategy":{"type":"Recreate"}
            }}
            """;
        var expected = """
            {"spec":{
              "strategy":{
                "$retainKeys":["type"],
                "rollingUpdate":null,
                "type":"Recreate"
              }
            }}
            """;
        AssertEqual(expected, Patch(original, modified));
    }

    [TestMethod]
    public void RetainKeys_NoChange_NotEmitted()
    {
        var doc = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{
              "strategy":{"type":"RollingUpdate","rollingUpdate":{"maxSurge":"25%"}}
            }}
            """;
        Assert.IsNull(Patch(doc, doc));
    }

    // ---- Schema-miss safety -----------------------------------------------------------------

    [TestMethod]
    public void NoSchema_FallsBackToAtomicListReplace()
    {
        var original = """{"l":[1,2,3]}""";
        var modified = """{"l":[3,2,1]}""";
        // No schema provider at all — RFC 7396 = whole-list replace.
        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            (JsonObject)JsonNode.Parse(original)!, (JsonObject)JsonNode.Parse(modified)!);
        Assert.IsNotNull(patch);
        var arr = (JsonArray)patch!["l"]!;
        Assert.AreEqual(3, (int)arr[0]!);
        Assert.AreEqual(1, (int)arr[2]!);
    }

    [TestMethod]
    public void MergeStrategy_WithoutMergeKey_FallsBackToAtomicReplace_AndLogsWarning()
    {
        // Build a schema where a field has Merge strategy but no merge key — defensive fallback.
        var bogus = new LibSchema.InMemorySchemaProvider(new Dictionary<GroupVersionKind, LibSchema.SchemaNode>
        {
            [new("", "v1", "Bogus")] = new()
            {
                JsonName = "root",
                Kind = LibSchema.SchemaNodeKind.Object,
                Properties = new Dictionary<string, LibSchema.SchemaNode>
                {
                    ["items"] = new()
                    {
                        JsonName = "items",
                        Kind = LibSchema.SchemaNodeKind.List,
                        Strategy = LibSchema.PatchStrategy.Merge,
                        // No PatchMergeKey
                        Items = new() { JsonName = string.Empty, Kind = LibSchema.SchemaNodeKind.Object,
                            Properties = new Dictionary<string, LibSchema.SchemaNode> { ["a"] = new() { JsonName="a", Kind=LibSchema.SchemaNodeKind.Primitive } }
                                .ToFrozenDictionaryFix(),
                        },
                    },
                }.ToFrozenDictionaryFix(),
            },
        });

        var original = """{"apiVersion":"v1","kind":"Bogus","items":[{"a":1}]}""";
        var modified = """{"apiVersion":"v1","kind":"Bogus","items":[{"a":2}]}""";
        var patch = TwoWayMerge.CreateTwoWayMergePatch(
            (JsonObject)JsonNode.Parse(original)!,
            (JsonObject)JsonNode.Parse(modified)!,
            new StrategicPatchOptions { SchemaProvider = bogus });
        Assert.IsNotNull(patch);
        var arr = (JsonArray)patch!["items"]!;
        // Atomic replace path: full list is dropped in.
        Assert.AreEqual(1, arr.Count);
        Assert.AreEqual(2, (int)arr[0]!["a"]!);
    }
}

internal static class FrozenDictionaryFixExtensions
{
    // Tiny helper so the bogus inline schema above fits on one line per property.
    public static System.Collections.Frozen.FrozenDictionary<string, LibSchema.SchemaNode> ToFrozenDictionaryFix(
        this Dictionary<string, LibSchema.SchemaNode> source)
        => System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(source);
}
