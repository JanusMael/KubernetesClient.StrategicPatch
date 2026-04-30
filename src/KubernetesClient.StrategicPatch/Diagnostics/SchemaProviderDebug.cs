using System.Diagnostics;
using System.Text;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Diagnostics;

/// <summary>
/// Debug-only schema-provider introspection. The methods here are gated by
/// <see cref="ConditionalAttribute"/>(<c>"DEBUG"</c>) so call sites compile to
/// <i>nothing</i> in Release builds — zero allocation, zero runtime overhead. Useful when
/// iterating on the source generator: dump what the embedded / generated provider considers
/// to be its schema set, side-by-side, and eyeball the difference.
/// </summary>
public static class SchemaProviderDebug
{
    /// <summary>
    /// Writes a human-readable tree of every GVK the supplied provider's <see cref="ISchemaProvider.GetRootSchema"/>
    /// returns a non-null root for. The probe set is provided by the caller; pass
    /// <see cref="EveryEmbeddedGvk"/> to enumerate the snapshot baked into the library, or hand-roll
    /// a list to focus on the GVKs your generator just emitted.
    /// </summary>
    [Conditional("DEBUG")]
    public static void DumpTo(
        ISchemaProvider provider,
        IEnumerable<GroupVersionKind> gvks,
        TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(gvks);
        ArgumentNullException.ThrowIfNull(writer);

        var sorted = gvks.OrderBy(g => g.ToString(), StringComparer.Ordinal).ToList();
        writer.WriteLine($"# {provider.Name} — probing {sorted.Count} GVK(s)");
        if (provider is IManifestedSchemaProvider manifested)
        {
            var m = manifested.Manifest;
            writer.WriteLine($"# manifest: generated={m.GeneratedAtUtc:O} v{m.SchemaWireFormatVersion} gvk_count={m.GvkCount} hash={m.SnapshotContentHash}");
        }

        var rendered = 0;
        foreach (var gvk in sorted)
        {
            var root = provider.GetRootSchema(gvk);
            if (root is null)
            {
                writer.WriteLine($"=== {gvk} ===   (not found)");
                continue;
            }
            writer.WriteLine($"=== {gvk} ===");
            RenderNode(root, writer, depth: 1);
            rendered++;
        }
        writer.WriteLine($"# rendered {rendered}/{sorted.Count} GVK(s)");
    }

    /// <summary>
    /// Convenience overload that probes every GVK the embedded snapshot covers. Use to dump
    /// either <see cref="EmbeddedSchemaProvider.Shared"/> directly or any generator-emitted
    /// provider whose intended coverage matches the embedded set.
    /// </summary>
    [Conditional("DEBUG")]
    public static void DumpEmbedded(ISchemaProvider provider, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(writer);
        DumpTo(provider, EveryEmbeddedGvk(), writer);
    }

    /// <summary>
    /// Returns every GVK the library's embedded <c>schemas.json</c> snapshot covers. Useful as
    /// the probe set when comparing generated vs embedded providers.
    /// </summary>
    public static IReadOnlyCollection<GroupVersionKind> EveryEmbeddedGvk()
    {
        var asm = typeof(EmbeddedSchemaProvider).Assembly;
        using var stream = asm.GetManifestResourceStream("KubernetesClient.StrategicPatch.schemas.json");
        if (stream is null)
        {
            return Array.Empty<GroupVersionKind>();
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var dict = Internal.SchemaWireFormat.Deserialize(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        return dict.Keys.ToArray();
    }

    private static void RenderNode(SchemaNode node, TextWriter writer, int depth)
    {
        var sb = new StringBuilder();
        sb.Append(' ', depth * 2);
        var label = string.IsNullOrEmpty(node.JsonName) ? "(root)" : node.JsonName;
        sb.Append(label).Append(": ").Append(node.Kind);
        if (node.PatchMergeKey is not null)
        {
            sb.Append(" mergeKey=").Append(node.PatchMergeKey);
        }
        if (node.Strategy != PatchStrategy.None)
        {
            sb.Append(" strategy=").Append(node.Strategy);
        }
        if (node.ListType != ListType.Unspecified)
        {
            sb.Append(" listType=").Append(node.ListType);
        }
        writer.WriteLine(sb.ToString());

        if (node.Properties.Count > 0)
        {
            foreach (var (key, child) in node.Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                _ = key;
                RenderNode(child, writer, depth + 1);
            }
        }
        if (node.Items is not null)
        {
            writer.WriteLine(new string(' ', depth * 2 + 2) + "[items]:");
            RenderNode(node.Items, writer, depth + 2);
        }
    }
}
