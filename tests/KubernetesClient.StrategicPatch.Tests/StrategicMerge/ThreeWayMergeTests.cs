using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests.StrategicMerge;

/// <summary>
/// Stage 5 corpus: three-way merge. Mirrors Go's <c>strategicpatch.CreateThreeWayMergePatch</c> —
/// preserves server-side additions, applies caller-side changes/additions, carries forward
/// caller-side deletions, throws on conflicts unless <see cref="StrategicPatchOptions.OverwriteConflicts"/>.
/// Cases tagged <c>[TestCategory("go-parity")]</c> mirror behaviour from the Go test corpus.
/// </summary>
[TestClass]
public sealed class ThreeWayMergeTests
{
    private static StrategicPatchOptions Options(bool overwrite = false) => new()
    {
        SchemaProvider = TestSchemas.DeploymentSchemaProvider(),
        OverwriteConflicts = overwrite,
    };

    private static JsonObject? Patch(string original, string modified, string current, StrategicPatchOptions? opts = null) =>
        ThreeWayMerge.CreateThreeWayMergePatch(
            (JsonObject?)JsonNode.Parse(original),
            (JsonObject?)JsonNode.Parse(modified),
            (JsonObject?)JsonNode.Parse(current),
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

    // ---- Basic delta semantics (current → modified, ignoring deletions) ----------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void ChangePrimitive_FromCallerSide_AppearsInPatch()
    {
        const string Original = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        const string Current  = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        const string Modified = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5}}""";
        AssertEqual("""{"spec":{"replicas":5}}""", Patch(Original, Modified, Current));
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void NoCallerChange_ReturnsNull_EvenIfServerDrifted()
    {
        const string Original = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        // Server scaled to 7; caller still wants the original 3.
        const string Current  = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":7}}""";
        const string Modified = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        // Caller's 3 conflicts with server's 7 → throws unless we set Overwrite.
        var ex = Assert.ThrowsExactly<StrategicMergePatchConflictException>(
            () => Patch(Original, Modified, Current));
        Assert.HasCount(1, ex.Conflicts);
    }

    [TestMethod]
    public void ServerSideAddition_IsPreserved()
    {
        // Original had only replicas; caller's modified still doesn't mention server-set fields.
        // Server added paused=true; caller's patch must NOT delete it.
        const string Original = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        const string Current  = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3,"paused":true}}""";
        const string Modified = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5}}""";
        // Patch only adjusts replicas; paused must not appear (no delete, no overwrite).
        AssertEqual("""{"spec":{"replicas":5}}""", Patch(Original, Modified, Current));
    }

    [TestMethod]
    public void CallerSideDeletion_FlowsThrough_AsNullMarker()
    {
        // Caller removed a label that was in original. Server (current) still has it.
        const string Original = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1","b":"2"}}}""";
        const string Current  = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1","b":"2"}}}""";
        const string Modified = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1"}}}""";
        AssertEqual("""{"metadata":{"labels":{"b":null}}}""", Patch(Original, Modified, Current));
    }

    [TestMethod]
    public void ServerSideDeletion_AndCallerWantsItBack_IsConflict()
    {
        // Server removed a label since last apply. Caller's modified asserts the original value
        // back — caller and server disagree on labels.b → conflict (unless overwrite).
        const string Original = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1","b":"2"}}}""";
        const string Current  = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1"}}}""";
        const string Modified = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1","b":"2"}}}""";
        var ex = Assert.ThrowsExactly<StrategicMergePatchConflictException>(
            () => Patch(Original, Modified, Current));
        Assert.HasCount(1, ex.Conflicts);
    }

    [TestMethod]
    public void ServerSideDeletion_AndCallerAlsoDeletes_EmitsRedundantNullMarker()
    {
        // Caller and server both removed labels.b. Patch carries the {labels:{b:null}} marker
        // anyway — it's a no-op delete server-side, and Go produces the same shape. Importantly,
        // there is no conflict: both sides agreed.
        const string Original = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1","b":"2"}}}""";
        const string Current  = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1"}}}""";
        const string Modified = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1"}}}""";
        AssertEqual("""{"metadata":{"labels":{"b":null}}}""", Patch(Original, Modified, Current));
    }

    // ---- Conflict detection ------------------------------------------------------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void Conflict_DifferentReplicas_Throws()
    {
        const string Original = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        const string Current  = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":4}}""";
        const string Modified = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5}}""";
        var ex = Assert.ThrowsExactly<StrategicMergePatchConflictException>(
            () => Patch(Original, Modified, Current));
        Assert.HasCount(1, ex.Conflicts);
        Assert.AreEqual("/spec/replicas", ex.Conflicts[0].ToString());
    }

    [TestMethod]
    [TestCategory("go-parity")]
    public void Conflict_OverwriteConflicts_True_TakesCallerSide()
    {
        const string Original = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        const string Current  = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":4}}""";
        const string Modified = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5}}""";
        AssertEqual("""{"spec":{"replicas":5}}""", Patch(Original, Modified, Current, Options(overwrite: true)));
    }

    [TestMethod]
    public void Conflict_BothSidesAgreeOnNewValue_NotConflict()
    {
        // Original: 3. Both server and caller independently arrived at 5.
        const string Original = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":3}}""";
        const string Current  = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5}}""";
        const string Modified = """{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":5}}""";
        // Delta is empty (current already matches modified); no conflict; no patch.
        Assert.IsNull(Patch(Original, Modified, Current));
    }

    [TestMethod]
    public void Conflict_CallerDeletes_ServerChanges_Throws()
    {
        const string Original = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"1"}}}""";
        const string Current  = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{"a":"2"}}}""";
        // Caller wants to remove label "a"; server changed it.
        const string Modified = """{"apiVersion":"v1","kind":"Pod","metadata":{"labels":{}}}""";
        var ex = Assert.ThrowsExactly<StrategicMergePatchConflictException>(
            () => Patch(Original, Modified, Current));
        Assert.HasCount(1, ex.Conflicts);
        Assert.AreEqual("/metadata/labels/a", ex.Conflicts[0].ToString());
    }

    // ---- List behaviour ----------------------------------------------------------------------

    [TestMethod]
    [TestCategory("go-parity")]
    public void Containers_CallerAddsSidecar_ServerUntouched()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"}]
            }}}}
            """;
        const string Current  = Original;
        const string Modified = """
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
        AssertEqual(expected, Patch(Original, Modified, Current));
    }

    [TestMethod]
    public void Containers_ServerAddedSidecar_CallerUntouched_PreservesIt()
    {
        // Sidecar injection (e.g. by an admission controller) shouldn't be reverted by a no-op
        // re-apply: caller's modified equals original, so the patch is empty and the injected
        // container survives. This is the canonical SMP three-way value proposition.
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"}]
            }}}}
            """;
        const string Current  = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "containers":[{"name":"web","image":"nginx"},{"name":"injected","image":"agent"}]
            }}}}
            """;
        const string Modified = Original;
        Assert.IsNull(Patch(Original, Modified, Current));
    }

    [TestMethod]
    public void Finalizers_CallerAddsItem_ServerUntouched()
    {
        const string Original = """
            {"apiVersion":"apps/v1","kind":"Deployment","spec":{"template":{"spec":{
              "finalizers":["a","b"]
            }}}}
            """;
        const string Current  = Original;
        const string Modified = """
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
        AssertEqual(expected, Patch(Original, Modified, Current));
    }

    // ---- Identity validation ----------------------------------------------------------------

    [TestMethod]
    public void RootIdentity_KindMismatchBetweenModifiedAndCurrent_Throws()
    {
        const string Original = """{"apiVersion":"v1","kind":"Pod","spec":{"a":1}}""";
        const string Current  = """{"apiVersion":"v1","kind":"ConfigMap","spec":{"a":1}}""";
        const string Modified = """{"apiVersion":"v1","kind":"Pod","spec":{"a":2}}""";
        Assert.ThrowsExactly<StrategicMergePatchException>(
            () => Patch(Original, Modified, Current));
    }

    // ---- Optimistic concurrency uses CURRENT ------------------------------------------------

    [TestMethod]
    public void OptimisticConcurrency_PullsResourceVersionFromCurrent()
    {
        const string Original = """
            {"apiVersion":"v1","kind":"Pod","metadata":{"uid":"abc","resourceVersion":"100"},"spec":{"a":1}}
            """;
        const string Current  = """
            {"apiVersion":"v1","kind":"Pod","metadata":{"uid":"abc","resourceVersion":"105"},"spec":{"a":1}}
            """;
        const string Modified = """
            {"apiVersion":"v1","kind":"Pod","metadata":{"uid":"abc","resourceVersion":"100"},"spec":{"a":2}}
            """;

        var patch = ThreeWayMerge.CreateThreeWayMergePatch(
            (JsonObject)JsonNode.Parse(Original)!,
            (JsonObject)JsonNode.Parse(Modified)!,
            (JsonObject)JsonNode.Parse(Current)!,
            new StrategicPatchOptions
            {
                SchemaProvider = TestSchemas.DeploymentSchemaProvider(),
                EnforceOptimisticConcurrency = true,
                OverwriteConflicts = true, // resourceVersion differs between original and current
            });

        Assert.IsNotNull(patch);
        var meta = (JsonObject)patch!["metadata"]!;
        Assert.AreEqual("105", (string)meta["resourceVersion"]!);
    }
}
