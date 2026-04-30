using System.Linq;
using KubernetesClient.StrategicPatch;
using KubernetesClient.StrategicPatch.Generated;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.SourceGenerators.Tests;

/// <summary>
/// Headline test for the generator: instantiate the emitted
/// <see cref="GeneratedStrategicPatchSchemaProvider"/> and verify that for every GVK in the
/// embedded snapshot, the generated tree is structurally identical to the wire-format-loaded
/// tree. If this passes, deployment projects can swap <see cref="EmbeddedSchemaProvider"/> for
/// <see cref="GeneratedStrategicPatchSchemaProvider"/> without behavioural change.
/// </summary>
[TestClass]
public sealed class GeneratedProviderRoundTripTests
{
    [TestMethod]
    public void Generated_HasSameGvkSetAsEmbedded()
    {
        var embeddedKeys = Diagnostics.SchemaProviderDebug.EveryEmbeddedGvk()
            .OrderBy(g => g.ToString(), System.StringComparer.Ordinal)
            .ToArray();
        var generatedKeys = embeddedKeys
            .Where(g => GeneratedStrategicPatchSchemaProvider.Instance.GetRootSchema(g) is not null)
            .ToArray();
        CollectionAssert.AreEqual(embeddedKeys, generatedKeys);
    }

    [TestMethod]
    public void Generated_TreesStructurallyMatchEmbedded()
    {
        var failures = new System.Collections.Generic.List<string>();
        foreach (var gvk in Diagnostics.SchemaProviderDebug.EveryEmbeddedGvk())
        {
            var fromEmbedded = EmbeddedSchemaProvider.Shared.GetRootSchema(gvk);
            var fromGenerated = GeneratedStrategicPatchSchemaProvider.Instance.GetRootSchema(gvk);
            if (!SchemaNode.StructuralEquals(fromEmbedded, fromGenerated))
            {
                failures.Add(gvk.ToString());
                if (failures.Count >= 3) break;
            }
        }
        Assert.IsEmpty(failures,
            $"{failures.Count} GVK(s) diverged between embedded and generated providers (showing first 3): "
            + string.Join(", ", failures));
    }

    [TestMethod]
    public void Generated_Manifest_HasMatchingGvkCount()
    {
        var manifest = GeneratedStrategicPatchSchemaProvider.Instance.Manifest;
        Assert.AreEqual(EmbeddedSchemaProvider.Shared.Count, manifest.GvkCount);
        Assert.AreEqual(1, manifest.SchemaWireFormatVersion);
        Assert.AreEqual(64, manifest.SnapshotContentHash.Length); // SHA-256 hex
    }

    [TestMethod]
    public void Generated_NameIsGenerated()
    {
        Assert.AreEqual("Generated", GeneratedStrategicPatchSchemaProvider.Instance.Name);
    }

    [TestMethod]
    public void Generated_Singleton_IsStableAcrossCalls()
    {
        Assert.AreSame(
            GeneratedStrategicPatchSchemaProvider.Instance,
            GeneratedStrategicPatchSchemaProvider.Instance);
    }

    [TestMethod]
    public void Generated_ResolvesDeepKeyedListMetadata()
    {
        // Spot-check a deep merge-key path: Deployment → spec → template → spec → containers
        // (mergeKey=name, strategy=Merge|listType=Map). If the generator's emitted code
        // round-trips correctly, this resolution should match what the EmbeddedSchemaProvider
        // returns — already covered by Generated_TreesStructurallyMatchEmbedded, but pinned
        // separately here so a regression doesn't drown in the bulk match.
        var node = ((ISchemaProvider)GeneratedStrategicPatchSchemaProvider.Instance)
            .Resolve(new GroupVersionKind("apps", "v1", "Deployment"),
                     JsonPointer.Parse("/spec/template/spec/containers"));
        Assert.IsNotNull(node);
        Assert.AreEqual(SchemaNodeKind.List, node!.Kind);
        Assert.AreEqual("name", node.PatchMergeKey);
        Assert.IsTrue(node.Strategy.HasFlag(PatchStrategy.Merge));
        Assert.AreEqual(ListType.Map, node.ListType);
    }
}
