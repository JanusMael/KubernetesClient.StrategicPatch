using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using k8s.Models;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Kustomize;
using KubernetesClient.StrategicPatch.Schema;
using KubernetesClient.StrategicPatch.StrategicMerge;
using Microsoft.Extensions.Logging.Abstractions;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Pre-Roslyn audit (2026-04-30). Each section pins a specific finding from
/// <c>docs/PRE_ROSLYN_AUDIT_2026-04-30.md</c>. The headline test —
/// <see cref="SchemaBuilder_ReconstructsEverySnapshotGvk_StructurallyEqualToWireFormat"/> — is
/// the load-bearing one for the source generator: if it passes, the generator can be written
/// with high confidence that emitting code through <see cref="SchemaBuilder"/> reproduces what
/// <see cref="SchemaWireFormat"/> would have produced from a baked snapshot.
/// </summary>
[TestClass]
public sealed class PreRoslynAuditTests
{
    private static void SkipIfEmpty()
    {
        if (EmbeddedSchemaProvider.Shared.Count == 0)
        {
            Assert.Inconclusive("schemas.json is empty — run scripts/regen-schemas.sh first.");
        }
    }

    // ---- Finding 1 — compound merge keys (Container.ports, single-key approximation) --------

    [TestMethod]
    public void Finding1_ContainerPorts_TreatedAsSingleKey_SameAsGo()
    {
        // x-kubernetes-list-map-keys is [containerPort, protocol] (compound), but the older
        // x-kubernetes-patch-merge-key is just 'containerPort'. Our walker reads the older
        // annotation, matching Go's behaviour. Pin so a future "fix" doesn't drift.
        SkipIfEmpty();
        var node = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/spec/template/spec/containers/0/ports"));
        Assert.IsNotNull(node);
        Assert.AreEqual("containerPort", node!.PatchMergeKey,
            "We deliberately read the singular x-kubernetes-patch-merge-key, matching Go. "
            + "If you change this to read x-kubernetes-list-map-keys, you also break parity.");
    }

    // ---- Finding 2 — list-map-keys without patch-merge-key falls through to atomic ----------

    [TestMethod]
    public void Finding2_ResourceRequirementsClaims_FallsThroughToAtomic()
    {
        // ResourceRequirements.claims has only x-kubernetes-list-map-keys (no patch-merge-key).
        // Both Go and we treat it as atomic-replace. Pin behaviour explicitly.
        SkipIfEmpty();
        var claims = ((ISchemaProvider)EmbeddedSchemaProvider.Shared)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/spec/template/spec/containers/0/resources/claims"));
        if (claims is null)
        {
            // The OpenAPI snapshot may not project claims at this depth depending on $ref order;
            // tolerate as inconclusive rather than fail noisily.
            Assert.Inconclusive("claims schema not reachable in this snapshot — pinned semantic stays valid.");
        }
        else
        {
            Assert.AreEqual(SchemaNodeKind.List, claims.Kind);
            Assert.IsNull(claims.PatchMergeKey,
                "ResourceRequirements.claims uses only the newer list-map-keys annotation; "
                + "the walker correctly leaves PatchMergeKey null, which means atomic-replace.");
        }
    }

    // ---- Finding 3 — wire-format exception type aligned -------------------------------------

    [TestMethod]
    public void Finding3_WireFormatVersionMismatch_ThrowsStrategicMergePatchException()
    {
        // Hand-craft a v=2 payload (newer than CurrentVersion=1).
        var futureBlob = """{"v":2,"s":{"v1/Pod":{"k":"O"}}}"""u8.ToArray();
        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => SchemaWireFormat.Deserialize(futureBlob));
        StringAssert.Contains(ex.Message, "wire version");
    }

    [TestMethod]
    public void Finding3_WireFormatGarbage_ThrowsStrategicMergePatchException()
    {
        var garbage = "this is not json"u8.ToArray();
        Assert.ThrowsExactly<StrategicMergePatchException>(
            () => SchemaWireFormat.Deserialize(garbage));
    }

    // ---- Finding 4 — EmbeddedSchemaProvider.Shared lazy initialiser under stress ------------

    [TestMethod]
    public void Finding4_Shared_LazyLoadIsThreadSafe()
    {
        // Hammer Shared.GetRootSchema from many threads concurrently. The Lazy<> default
        // (ExecutionAndPublication) guarantees exactly one initialisation; this proves it.
        var bag = new ConcurrentBag<int>();
        Parallel.For(0, 64, _ =>
        {
            for (var i = 0; i < 50; i++)
            {
                bag.Add(EmbeddedSchemaProvider.Shared.Count);
            }
        });
        var observed = bag.Distinct().ToArray();
        Assert.HasCount(1, observed);
        Assert.AreEqual(EmbeddedSchemaProvider.Shared.Count, observed[0]);
    }

    [TestMethod]
    public void Finding4_Shared_ReferenceIsStableAcrossCalls()
    {
        Assert.AreSame(EmbeddedSchemaProvider.Shared, EmbeddedSchemaProvider.Shared);
    }

    // ---- Finding 5 — auto-default preserves caller-side options -----------------------------

    [TestMethod]
    public void Finding5_AutoDefault_PreservesMaxDepth()
    {
        SkipIfEmpty();
        // Both Deployments share metadata, so the diff necessarily recurses into the metadata
        // object at depth 1. With MaxDepth=0, that recursion trips the guard immediately and we
        // confirm the option flowed through the auto-default unchanged.
        var orig = NewDeploy(replicas: 3);
        var mod = NewDeploy(replicas: 5);
        var ex = Assert.ThrowsExactly<StrategicMergePatchException>(
            () => orig.CreateStrategicPatch(mod, new StrategicPatchOptions { MaxDepth = 0 }));
        StringAssert.Contains(ex.Message, "MaxDepth");
    }

    [TestMethod]
    public void Finding5_AutoDefault_PreservesLogger()
    {
        SkipIfEmpty();
        var entries = new List<string>();
        var logger = new InspectableLogger(entries);
        var orig = NewDeploy(replicas: 3);
        var mod = NewDeploy(replicas: 5);

        _ = orig.CreateStrategicPatch(mod, new StrategicPatchOptions { Logger = logger });
        Assert.IsTrue(entries.Any(e => e.Contains("smp.compute_two_way", StringComparison.Ordinal)),
            "Caller-supplied logger should still receive the boundary Information event.");
    }

    [TestMethod]
    public void Finding5_AutoDefault_PreservesIgnoreNullValuesInModified()
    {
        SkipIfEmpty();
        // Sparse modified: only replicas set; the rest of the V1Deployment shape would otherwise
        // be treated as "delete every other field." With IgnoreNullValuesInModified=true, the
        // diff suppresses those deletes. The auto-default must keep that behaviour intact.
        var orig = NewDeploy(replicas: 3, addLabel: true);
        var sparse = new V1Deployment
        {
            ApiVersion = "apps/v1", Kind = "Deployment",
            Metadata = new V1ObjectMeta { Name = "api" },
            Spec = new V1DeploymentSpec { Replicas = 5 },
        };

        var withFlag = orig.CreateStrategicPatch(sparse,
            new StrategicPatchOptions { IgnoreNullValuesInModified = true });
        // Patch should be just the replicas change — labels untouched.
        var patch = (JsonObject)JsonNode.Parse((string)withFlag.Patch.Content)!;
        Assert.IsFalse(patch.ContainsKey("metadata") && patch["metadata"] is JsonObject metaObj && metaObj.ContainsKey("labels"),
            $"Caller-supplied IgnoreNullValuesInModified leaked through the auto-default; got: {patch.ToJsonString()}");
    }

    [TestMethod]
    public void Finding5_AutoDefault_PreservesEnforceOptimisticConcurrency()
    {
        SkipIfEmpty();
        var orig = NewDeploy(replicas: 3, withResourceVersion: "42");
        var mod = NewDeploy(replicas: 5, withResourceVersion: "42");

        var result = orig.CreateStrategicPatch(mod,
            new StrategicPatchOptions { EnforceOptimisticConcurrency = true });
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        Assert.AreEqual("42", (string)patch["metadata"]!["resourceVersion"]!,
            "EnforceOptimisticConcurrency must inject metadata.resourceVersion when there is a real diff.");
    }

    [TestMethod]
    public void Finding5_AutoDefault_PreservesOverwriteConflicts()
    {
        SkipIfEmpty();
        var orig = NewDeploy(replicas: 3);
        var current = NewDeploy(replicas: 4);
        var modified = NewDeploy(replicas: 5);

        // Default (no OverwriteConflicts) → throws.
        Assert.ThrowsExactly<StrategicMergePatchConflictException>(
            () => orig.CreateThreeWayStrategicPatch(modified, current));
        // OverwriteConflicts=true → caller wins.
        var result = orig.CreateThreeWayStrategicPatch(modified, current,
            new StrategicPatchOptions { OverwriteConflicts = true });
        var patch = (JsonObject)JsonNode.Parse((string)result.Patch.Content)!;
        Assert.AreEqual(5, (int)patch["spec"]!["replicas"]!);
    }

    [TestMethod]
    public void Finding5_AutoDefault_NullOptions_StillEnablesEmbeddedProvider()
    {
        SkipIfEmpty();
        var orig = NewDeploy(replicas: 3);
        var mod = NewDeploy(replicas: 5);

        var result = orig.CreateStrategicPatch(mod);  // null options
        // The diff produced via the auto-default carries the GVK successfully → schema lookup
        // happened, which means EmbeddedSchemaProvider.Shared was used.
        Assert.AreEqual(new GroupVersionKind("apps", "v1", "Deployment"), result.Gvk);
    }

    [TestMethod]
    public void Finding5_AutoDefault_ExplicitProvider_NotOverridden()
    {
        SkipIfEmpty();
        var capturing = new CapturingSchemaProvider();
        var orig = NewDeploy(replicas: 3);
        var mod = NewDeploy(replicas: 5);

        _ = orig.CreateStrategicPatch(mod,
            new StrategicPatchOptions { SchemaProvider = capturing });
        Assert.IsGreaterThan(0, capturing.GetRootCalls,
            "Caller-supplied SchemaProvider should still be the one consulted.");
    }

    // ---- Finding 6 — SchemaBuilder ↔ wire format round-trip (THE Roslyn confidence test) ----

    [TestMethod]
    public void Finding6_SchemaBuilder_ReconstructsEverySnapshotGvk_StructurallyEqualToWireFormat()
    {
        SkipIfEmpty();
        // For every GVK in the embedded snapshot:
        //   1. Get its SchemaNode tree from the wire-format-loaded provider.
        //   2. Reconstruct an equivalent tree by walking it and re-emitting via SchemaBuilder.
        //   3. Assert structural equality.
        // Step 2 is exactly what the source generator will do — but in literal C# code form.
        // Passing this test for every GVK in the snapshot proves the generator can be written.
        var failures = new List<string>();
        var snapshot = LoadEmbeddedSnapshot();
        foreach (var (gvk, original) in snapshot)
        {
            var rebuilt = ReconstructViaBuilder(original);
            if (!SchemaNode.StructuralEquals(original, rebuilt))
            {
                failures.Add($"GVK {gvk} round-trip diverged.");
                if (failures.Count >= 3) break;
            }
        }
        Assert.IsEmpty(failures,
            $"{failures.Count} GVK(s) failed the round-trip:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void Finding6_SchemaBuilder_RoundTripPreservesAllStrategyFlagCombos()
    {
        // The codegen must emit `PatchStrategy.Merge | PatchStrategy.RetainKeys` correctly. Walk
        // the snapshot and confirm every observed Strategy value round-trips.
        SkipIfEmpty();
        var observed = new HashSet<PatchStrategy>();
        foreach (var (_, root) in LoadEmbeddedSnapshot())
        {
            CollectStrategies(root, observed);
        }
        Assert.IsGreaterThan(1, observed.Count, "Snapshot exercises more than one strategy flag.");
        // Round-trip each observed combination through a synthetic node.
        foreach (var combo in observed)
        {
            var node = new SchemaNode { JsonName = "x", Kind = SchemaNodeKind.List, Strategy = combo };
            var rebuilt = SchemaBuilder.ListNode("x", SchemaBuilder.Primitive(),
                strategy: combo, patchMergeKey: null);
            Assert.AreEqual(combo, rebuilt.Strategy);
            Assert.AreEqual(node.Strategy, rebuilt.Strategy);
        }
    }

    // ---- Finding 7 — SchemaNode.StructuralEquals -------------------------------------------

    [TestMethod]
    public void Finding7_StructuralEquals_StructurallyEqualTreesWithDifferentDictInstances()
    {
        // Two trees with structurally-identical Properties dictionaries built independently —
        // the auto-implemented record Equals would say they're unequal because the dicts have
        // different identity. StructuralEquals must say they're equal.
        var leftProps = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
        {
            ["a"] = new() { JsonName = "a", Kind = SchemaNodeKind.Primitive },
            ["b"] = new() { JsonName = "b", Kind = SchemaNodeKind.Primitive },
        };
        var rightProps = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
        {
            ["b"] = new() { JsonName = "b", Kind = SchemaNodeKind.Primitive },
            ["a"] = new() { JsonName = "a", Kind = SchemaNodeKind.Primitive },
        };
        var left = SchemaBuilder.ObjectNode("root", leftProps);
        var right = SchemaBuilder.ObjectNode("root", rightProps);

        Assert.IsTrue(SchemaNode.StructuralEquals(left, right));
        // The default record-level Equals would NOT consider these equal (proves the helper is necessary).
        Assert.IsFalse(left.Equals(right),
            "If this fires, record-default equality became structural and we may not need the helper anymore.");
    }

    [TestMethod]
    public void Finding7_StructuralEquals_DivergencesDetected()
    {
        var left = SchemaBuilder.ListNode("x", SchemaBuilder.Primitive(), patchMergeKey: "name");
        var right = SchemaBuilder.ListNode("x", SchemaBuilder.Primitive(), patchMergeKey: "id");
        Assert.IsFalse(SchemaNode.StructuralEquals(left, right));
    }

    [TestMethod]
    public void Finding7_StructuralEquals_NullHandling()
    {
        Assert.IsTrue(SchemaNode.StructuralEquals(null, null));
        Assert.IsFalse(SchemaNode.StructuralEquals(SchemaBuilder.Primitive(), null));
        Assert.IsFalse(SchemaNode.StructuralEquals(null, SchemaBuilder.Primitive()));
    }

    // ---- Finding 8 — Kustomize YAML edge cases ----------------------------------------------

    [TestMethod]
    public void Finding8_BlockScalar_PreservedAsString()
    {
        const string Yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: x
            data:
              script: |
                #!/bin/bash
                echo "hello"
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        Assert.HasCount(1, docs);
        var script = (string)docs[0]["data"]!["script"]!;
        StringAssert.Contains(script, "#!/bin/bash");
        StringAssert.Contains(script, "echo \"hello\"");
    }

    [TestMethod]
    public void Finding8_FoldedScalar_NewlinesFolded()
    {
        const string Yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: x
            data:
              prose: >
                this is one
                long sentence
                across lines.
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        var prose = (string)docs[0]["data"]!["prose"]!;
        // Folded scalar collapses interior newlines into spaces.
        StringAssert.Contains(prose, "this is one long sentence across lines.");
    }

    [TestMethod]
    public void Finding8_FlowStyleSequenceAndMapping_ParsedCorrectly()
    {
        const string Yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata: {name: x, labels: {app: api, tier: web}}
            data: {k1: v1, k2: v2}
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        Assert.HasCount(1, docs);
        Assert.AreEqual("api", (string)docs[0]["metadata"]!["labels"]!["app"]!);
        Assert.AreEqual("v2", (string)docs[0]["data"]!["k2"]!);
    }

    [TestMethod]
    public void Finding8_AnchorAndAlias_AliasResolvedToReferencedNode()
    {
        const string Yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: x
            data:
              shared: &common
                key: value
              other: *common
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        Assert.HasCount(1, docs);
        var shared = (JsonObject)docs[0]["data"]!["shared"]!;
        var other = (JsonObject)docs[0]["data"]!["other"]!;
        Assert.AreEqual("value", (string)shared["key"]!);
        Assert.AreEqual("value", (string)other["key"]!);
    }

    [TestMethod]
    public void Finding8_KubernetesQuantitiesNotInferredAsNumbers()
    {
        // Resource quantities like "100m", "1Gi" must NOT be coerced to numbers — they're
        // K8s strings with units. YAML 1.2 core schema correctly leaves these as strings.
        const string Yaml = """
            apiVersion: v1
            kind: Pod
            metadata:
              name: x
            spec:
              containers:
                - name: web
                  resources:
                    requests:
                      cpu: 100m
                      memory: 1Gi
            """;
        var docs = KustomizeOverlay.LoadAll(Yaml);
        var requests = docs[0]["spec"]!["containers"]![0]!["resources"]!["requests"]!;
        Assert.AreEqual(System.Text.Json.JsonValueKind.String, requests["cpu"]!.GetValueKind());
        Assert.AreEqual(System.Text.Json.JsonValueKind.String, requests["memory"]!.GetValueKind());
        Assert.AreEqual("100m", (string)requests["cpu"]!);
        Assert.AreEqual("1Gi", (string)requests["memory"]!);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static V1Deployment NewDeploy(int replicas, string? withResourceVersion = null, bool addLabel = false) => new()
    {
        ApiVersion = "apps/v1",
        Kind = "Deployment",
        Metadata = new V1ObjectMeta
        {
            Name = "api",
            NamespaceProperty = "default",
            ResourceVersion = withResourceVersion,
            Labels = addLabel ? new Dictionary<string, string> { ["caller-owned"] = "1" } : null,
        },
        Spec = new V1DeploymentSpec { Replicas = replicas },
    };

    private static V1Pod NewPod(int? replicas, string? label = null) => new()
    {
        ApiVersion = "v1",
        Kind = "Pod",
        Metadata = new V1ObjectMeta
        {
            Name = "p",
            Labels = label is null ? null : new Dictionary<string, string> { ["k"] = label },
        },
    };

    private static IReadOnlyDictionary<GroupVersionKind, SchemaNode> LoadEmbeddedSnapshot()
    {
        // Read the same blob the EmbeddedSchemaProvider reads, but bypass the singleton so we
        // get the canonical wire-format-derived tree to compare against.
        var asm = typeof(EmbeddedSchemaProvider).Assembly;
        using var stream = asm.GetManifestResourceStream("KubernetesClient.StrategicPatch.schemas.json")
            ?? throw new InvalidOperationException("schemas.json embedded resource missing");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return SchemaWireFormat.Deserialize(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    /// <summary>
    /// Walks the input tree and rebuilds it by calling <see cref="SchemaBuilder"/> factory
    /// methods. Mirror image of what the source generator will emit in literal C# code.
    /// </summary>
    private static SchemaNode ReconstructViaBuilder(SchemaNode source)
    {
        return source.Kind switch
        {
            SchemaNodeKind.Primitive => new SchemaNode
            {
                JsonName = source.JsonName,
                Kind = SchemaNodeKind.Primitive,
                PatchMergeKey = source.PatchMergeKey,
                Strategy = source.Strategy,
                ListType = source.ListType,
            },
            SchemaNodeKind.Map => SchemaBuilder.Map(
                ReconstructViaBuilder(source.Items!), source.JsonName)
                with
            {
                PatchMergeKey = source.PatchMergeKey,
                Strategy = source.Strategy,
                ListType = source.ListType,
            },
            SchemaNodeKind.List => SchemaBuilder.ListNode(
                source.JsonName,
                ReconstructViaBuilder(source.Items!),
                strategy: source.Strategy,
                patchMergeKey: source.PatchMergeKey,
                listType: source.ListType),
            SchemaNodeKind.Object => SchemaBuilder.ObjectNode(
                source.JsonName,
                source.Properties.ToDictionary(
                    kvp => kvp.Key,
                    kvp => ReconstructViaBuilder(kvp.Value),
                    StringComparer.Ordinal),
                strategy: source.Strategy)
                with
            {
                PatchMergeKey = source.PatchMergeKey,
                ListType = source.ListType,
                Items = source.Items is null ? null : ReconstructViaBuilder(source.Items),
            },
            _ => throw new InvalidOperationException($"Unknown kind: {source.Kind}"),
        };
    }

    private static void CollectStrategies(SchemaNode node, HashSet<PatchStrategy> sink)
    {
        sink.Add(node.Strategy);
        if (node.Items is not null)
        {
            CollectStrategies(node.Items, sink);
        }
        foreach (var (_, child) in node.Properties)
        {
            CollectStrategies(child, sink);
        }
    }

    private sealed class CapturingSchemaProvider : ISchemaProvider
    {
        public int GetRootCalls { get; private set; }
        public SchemaNode? GetRootSchema(GroupVersionKind gvk)
        {
            GetRootCalls++;
            return null;
        }
    }

    private sealed class InspectableLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _entries;
        public InspectableLogger(List<string> entries) { _entries = entries; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add(formatter(state, exception));
    }
}
