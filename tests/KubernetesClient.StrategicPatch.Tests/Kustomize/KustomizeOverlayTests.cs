using System.Text;
using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Kustomize;

namespace KubernetesClient.StrategicPatch.Tests.Kustomize;

/// <summary>
/// Stage 12 corpus: Kustomize overlay loader. The deployment projects pipe
/// <c>kustomize build</c> output into the diff engine; this fixture verifies the
/// loader handles every shape Kustomize emits, indexes documents correctly, and feeds the
/// strategic-merge engine end-to-end.
/// </summary>
[TestClass]
public sealed class KustomizeOverlayTests
{
    private const string SimpleSingleDoc = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: api
          namespace: default
        spec:
          replicas: 5
        """;

    private const string MultiDoc = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: settings
          namespace: default
        data:
          FOO: "1"
          BAR: "2"
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: api
          namespace: default
        spec:
          replicas: 3
        ---
        apiVersion: v1
        kind: Service
        metadata:
          name: api
          namespace: default
        spec:
          ports:
            - port: 80
              name: http
        """;

    private const string ContainsLeadingSeparatorAndComments = """
        # this is a kustomize-build banner comment
        ---
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: only
        data:
          k: v
        """;

    private const string MixedNamespaces = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: api
          namespace: prod
        spec:
          replicas: 5
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: api
          namespace: staging
        spec:
          replicas: 2
        """;

    // ---- LoadAll basics ----------------------------------------------------------------------

    [TestMethod]
    public void LoadAll_SingleDocument_ParsesIntoOneJsonObject()
    {
        var docs = KustomizeOverlay.LoadAll(SimpleSingleDoc);
        Assert.HasCount(1, docs);
        Assert.AreEqual("apps/v1", (string)docs[0]["apiVersion"]!);
        Assert.AreEqual("Deployment", (string)docs[0]["kind"]!);
        Assert.AreEqual(5, (int)docs[0]["spec"]!["replicas"]!);
    }

    [TestMethod]
    public void LoadAll_MultiDoc_ReturnsAllInOrder()
    {
        var docs = KustomizeOverlay.LoadAll(MultiDoc);
        Assert.HasCount(3, docs);
        Assert.AreEqual("ConfigMap", (string)docs[0]["kind"]!);
        Assert.AreEqual("Deployment", (string)docs[1]["kind"]!);
        Assert.AreEqual("Service", (string)docs[2]["kind"]!);
    }

    [TestMethod]
    public void LoadAll_LeadingSeparatorAndComments_StillParses()
    {
        var docs = KustomizeOverlay.LoadAll(ContainsLeadingSeparatorAndComments);
        Assert.HasCount(1, docs);
        Assert.AreEqual("only", (string)docs[0]["metadata"]!["name"]!);
    }

    [TestMethod]
    public void LoadAll_EmptyInput_ReturnsEmptyList()
    {
        Assert.IsEmpty(KustomizeOverlay.LoadAll(string.Empty));
        Assert.IsEmpty(KustomizeOverlay.LoadAll("   \n   \n"));
    }

    [TestMethod]
    public void LoadAll_OnlySeparators_ReturnsEmptyList()
    {
        Assert.IsEmpty(KustomizeOverlay.LoadAll("---\n---\n---\n"));
    }

    [TestMethod]
    public void LoadAll_AcceptsStreamAndTextReader()
    {
        var bytes = Encoding.UTF8.GetBytes(MultiDoc);
        using (var stream = new MemoryStream(bytes))
        {
            Assert.HasCount(3, KustomizeOverlay.LoadAll(stream));
        }
        using (var reader = new StringReader(MultiDoc))
        {
            Assert.HasCount(3, KustomizeOverlay.LoadAll(reader));
        }
    }

    [TestMethod]
    public void LoadAll_SkipsNonObjectDocuments()
    {
        const string Yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: x
            data:
              k: v
            ---
            - just
            - a
            - list
            ---
            42
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        Assert.HasCount(1, docs);
        Assert.AreEqual("ConfigMap", (string)docs[0]["kind"]!);
    }

    [TestMethod]
    public void LoadAll_NumericValuesPreservedAsNumbers()
    {
        var docs = KustomizeOverlay.LoadAll(SimpleSingleDoc);
        // YAML's "5" without quotes is a number; the JSON-compatible serializer must preserve it.
        var replicas = docs[0]["spec"]!["replicas"]!;
        Assert.AreEqual(System.Text.Json.JsonValueKind.Number, replicas.GetValueKind());
    }

    [TestMethod]
    public void LoadAll_QuotedStringsStayStrings()
    {
        var docs = KustomizeOverlay.LoadAll(MultiDoc);
        var fooValue = docs[0]["data"]!["FOO"]!;
        Assert.AreEqual(System.Text.Json.JsonValueKind.String, fooValue.GetValueKind());
        Assert.AreEqual("1", (string)fooValue!);
    }

    [TestMethod]
    public void LoadAll_NullArguments_Throw()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => KustomizeOverlay.LoadAll((string)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => KustomizeOverlay.LoadAll((Stream)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => KustomizeOverlay.LoadAll((TextReader)null!));
    }

    // ---- TryGetKey ---------------------------------------------------------------------------

    [TestMethod]
    public void TryGetKey_FullDocument_ReturnsPopulatedKey()
    {
        var docs = KustomizeOverlay.LoadAll(SimpleSingleDoc);
        Assert.IsTrue(KustomizeOverlay.TryGetKey(docs[0], out var key));
        Assert.AreEqual(new GroupVersionKind("apps", "v1", "Deployment"), key.Gvk);
        Assert.AreEqual("default", key.Namespace);
        Assert.AreEqual("api", key.Name);
    }

    [TestMethod]
    public void TryGetKey_DocumentMissingApiVersion_ReturnsFalse()
    {
        var doc = (JsonObject)JsonNode.Parse("""{"kind":"Pod","metadata":{"name":"x"}}""")!;
        Assert.IsFalse(KustomizeOverlay.TryGetKey(doc, out _));
    }

    [TestMethod]
    public void TryGetKey_DocumentMissingMetadataName_ReturnsFalse()
    {
        var doc = (JsonObject)JsonNode.Parse("""{"apiVersion":"v1","kind":"Pod","metadata":{}}""")!;
        Assert.IsFalse(KustomizeOverlay.TryGetKey(doc, out _));
    }

    [TestMethod]
    public void TryGetKey_NamespaceAbsent_ResolvesToNull()
    {
        var doc = (JsonObject)JsonNode.Parse(
            """{"apiVersion":"v1","kind":"Namespace","metadata":{"name":"prod"}}""")!;
        Assert.IsTrue(KustomizeOverlay.TryGetKey(doc, out var key));
        Assert.IsNull(key.Namespace);
    }

    // ---- Index -------------------------------------------------------------------------------

    [TestMethod]
    public void Index_BuildsKeyedLookup()
    {
        var docs = KustomizeOverlay.LoadAll(MultiDoc);
        var idx = KustomizeOverlay.Index(docs);
        Assert.HasCount(3, idx);
        Assert.IsTrue(idx.ContainsKey(new KustomizeOverlay.DocumentKey(
            new GroupVersionKind("apps", "v1", "Deployment"), "default", "api")));
        Assert.IsTrue(idx.ContainsKey(new KustomizeOverlay.DocumentKey(
            new GroupVersionKind("", "v1", "ConfigMap"), "default", "settings")));
    }

    [TestMethod]
    public void Index_DistinguishesNamespaces()
    {
        var docs = KustomizeOverlay.LoadAll(MixedNamespaces);
        var idx = KustomizeOverlay.Index(docs);
        Assert.HasCount(2, idx);
        var prod = new KustomizeOverlay.DocumentKey(new GroupVersionKind("apps", "v1", "Deployment"), "prod", "api");
        var staging = new KustomizeOverlay.DocumentKey(new GroupVersionKind("apps", "v1", "Deployment"), "staging", "api");
        Assert.AreEqual(5, (int)idx[prod]["spec"]!["replicas"]!);
        Assert.AreEqual(2, (int)idx[staging]["spec"]!["replicas"]!);
    }

    [TestMethod]
    public void Index_DuplicateKey_ThrowsTyped()
    {
        const string Dup = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
              namespace: default
            data: {}
            ---
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
              namespace: default
            data: {}
            """;
        var docs = KustomizeOverlay.LoadAll(Dup);
        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(() => KustomizeOverlay.Index(docs));
        StringAssert.Contains(ex.Message, "Duplicate");
    }

    [TestMethod]
    public void Index_SkipsDocumentsLackingIdentity()
    {
        const string Yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: x
            data: {}
            ---
            data: {}
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        Assert.HasCount(2, docs);
        Assert.HasCount(1, KustomizeOverlay.Index(docs));
    }

    // ---- Find --------------------------------------------------------------------------------

    [TestMethod]
    public void Find_ByGvkAndName_ReturnsMatchingDocument()
    {
        var docs = KustomizeOverlay.LoadAll(MultiDoc);
        var hit = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "api", "default");
        Assert.IsNotNull(hit);
        Assert.AreEqual(3, (int)hit!["spec"]!["replicas"]!);
    }

    [TestMethod]
    public void Find_NamespaceUnspecified_MatchesEither()
    {
        var docs = KustomizeOverlay.LoadAll(MixedNamespaces);
        // Just first match wins when caller doesn't specify namespace.
        var hit = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "api");
        Assert.IsNotNull(hit);
    }

    [TestMethod]
    public void Find_NamespaceSpecified_RequiresExactMatch()
    {
        var docs = KustomizeOverlay.LoadAll(MixedNamespaces);
        var prod = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "api", "prod");
        var staging = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "api", "staging");
        var devNotPresent = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "api", "dev");
        Assert.IsNotNull(prod);
        Assert.IsNotNull(staging);
        Assert.IsNull(devNotPresent);
        Assert.AreEqual(5, (int)prod!["spec"]!["replicas"]!);
        Assert.AreEqual(2, (int)staging!["spec"]!["replicas"]!);
    }

    [TestMethod]
    public void Find_NotFound_ReturnsNull()
    {
        var docs = KustomizeOverlay.LoadAll(MultiDoc);
        var hit = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "nonexistent");
        Assert.IsNull(hit);
    }

    // ---- End-to-end with the diff engine -----------------------------------------------------

    [TestMethod]
    public void EndToEnd_OverlayAsModified_DiffAgainstTypedOriginal_UsesEmbeddedSchema()
    {
        // Caller has typed live state (replicas=3) and a Kustomize overlay rendering the
        // desired state (replicas=3 in MultiDoc) — the patch must therefore be null because
        // there is no drift. This pins the typical "no-op apply" flow that deployment projects
        // run on every reconcile.
        var live = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "default" },
            Spec = new V1DeploymentSpec { Replicas = 3 },
        };

        var docs = KustomizeOverlay.LoadAll(MultiDoc);
        var overlayDeployment = KustomizeOverlay.Find(docs,
            new GroupVersionKind("apps", "v1", "Deployment"), "api", "default");
        Assert.IsNotNull(overlayDeployment);
        Assert.AreEqual(3, (int)overlayDeployment!["spec"]!["replicas"]!);

        var liveJson = (JsonObject)JsonNode.Parse(k8s.KubernetesJson.Serialize(live))!;
        var patch = KubernetesClient.StrategicPatch.StrategicMerge.TwoWayMerge.CreateTwoWayMergePatch(
            liveJson, overlayDeployment,
            new StrategicPatchOptions { SchemaProvider = KubernetesClient.StrategicPatch.Schema.EmbeddedSchemaProvider.Shared });

        Assert.IsNull(patch, $"Expected no-op diff but got: {patch?.ToJsonString()}");
    }

    [TestMethod]
    public void EndToEnd_TwoOverlays_DiffByKey()
    {
        // Diff a "base" kustomize overlay against an environment-specific one. Both come from
        // multi-doc YAML; we use Find to extract the matching pair, then diff.
        const string BaseOverlay = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: api
              namespace: default
            spec:
              replicas: 3
              template:
                spec:
                  containers:
                    - name: web
                      image: nginx:1.0
            """;
        const string ProdOverlay = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: api
              namespace: default
            spec:
              replicas: 5
              template:
                spec:
                  containers:
                    - name: web
                      image: nginx:2.0
            """;

        var baseDeployment = KustomizeOverlay.Find(KustomizeOverlay.LoadAll(BaseOverlay),
            new GroupVersionKind("apps", "v1", "Deployment"), "api");
        var prodDeployment = KustomizeOverlay.Find(KustomizeOverlay.LoadAll(ProdOverlay),
            new GroupVersionKind("apps", "v1", "Deployment"), "api");

        Assert.IsNotNull(baseDeployment);
        Assert.IsNotNull(prodDeployment);

        var patch = KubernetesClient.StrategicPatch.StrategicMerge.TwoWayMerge.CreateTwoWayMergePatch(baseDeployment, prodDeployment,
            new StrategicPatchOptions { SchemaProvider = KubernetesClient.StrategicPatch.Schema.EmbeddedSchemaProvider.Shared });
        Assert.IsNotNull(patch);
        Assert.AreEqual(5, (int)patch!["spec"]!["replicas"]!);
        // Schema-driven path: container image change emits a keyed merge with $setElementOrder.
        var podSpec = (JsonObject)patch["spec"]!["template"]!["spec"]!;
        Assert.IsTrue(podSpec.ContainsKey("$setElementOrder/containers"),
            $"Got: {patch.ToJsonString()}");
    }
}
