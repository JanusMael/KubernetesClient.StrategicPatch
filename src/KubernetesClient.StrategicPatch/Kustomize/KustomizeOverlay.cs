using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KubernetesClient.StrategicPatch.Kustomize;

/// <summary>
/// Loads multi-document YAML produced by <c>kustomize build</c> (or any other source) into
/// <see cref="JsonObject"/> instances ready for the strategic-merge engine, plus convenience
/// methods for locating a specific document by GVK + name (+ namespace).
/// </summary>
/// <remarks>
/// <para><b>Why a separate helper.</b> The strategic-merge engine operates on
/// <see cref="JsonObject"/>; deployment projects typically feed it Kustomize-rendered overlays
/// rather than typed K8s models for the modified side of a diff. This helper bridges the
/// YAML→<see cref="JsonObject"/> gap with the same semantic the rest of <c>KubernetesClient</c>
/// uses (numeric promotion, no implicit type coercion beyond what the YAML→JSON model dictates).</para>
/// <para><b>Thread safety.</b> All methods are pure: they accept input, return a new collection,
/// and never share mutable state. Safe to call concurrently with different inputs.</para>
/// <para>The conversion uses <c>YamlDotNet</c>'s JSON-compatible serializer to round-trip each
/// document through canonical JSON, then parses with <c>System.Text.Json</c> so the resulting
/// <see cref="JsonObject"/> matches what <c>JsonNode.Parse</c> would produce given the same JSON
/// payload. That matters: the diff/apply engines compare scalars via raw JSON token text, and the
/// caller side (<see cref="StrategicPatchExtensions"/>) uses <see cref="System.Text.Json"/>.</para>
/// </remarks>
public static class KustomizeOverlay
{
    /// <summary>Identity tuple for a Kubernetes document — what an overlay calls "Deployment named api in default".</summary>
    /// <param name="Gvk">apiVersion + kind.</param>
    /// <param name="Namespace">Cluster-scoped resources have <c>null</c>; everything else has the namespace from <c>metadata.namespace</c> (or empty string when absent).</param>
    /// <param name="Name">Required <c>metadata.name</c>.</param>
    public readonly record struct DocumentKey(GroupVersionKind Gvk, string? Namespace, string Name);

    /// <summary>Loads every document from a YAML string. Empty documents (e.g. a leading <c>---</c>) are skipped.</summary>
    public static IReadOnlyList<JsonObject> LoadAll(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        using var reader = new StringReader(yaml);
        return LoadAll(reader);
    }

    /// <summary>Loads every document from a UTF-8 stream.</summary>
    public static IReadOnlyList<JsonObject> LoadAll(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, leaveOpen: true);
        return LoadAll(reader);
    }

    /// <summary>Loads every document from a <see cref="TextReader"/>. Caller owns the reader.</summary>
    public static IReadOnlyList<JsonObject> LoadAll(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // WithAttemptingUnquotedStringTypeDeserialization forces type inference on unquoted
        // scalars (so `replicas: 5` deserializes as int, not string "5"). Without it the
        // JSON-compatible serializer emits everything as quoted strings, breaking the
        // diff engine which compares numeric kinds via ScalarKey.
        var deserializer = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        var jsonSerializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();

        var docs = new List<JsonObject>();
        var parser = new Parser(reader);
        if (!parser.MoveNext())
        {
            return docs;
        }

        // Walk the StreamStart → DocumentStart → ... → DocumentEnd → StreamEnd state machine.
        // We rely on YamlDotNet's deserializer to consume one document's worth of events at a time.
        if (parser.Current is not StreamStart)
        {
            return docs;
        }
        parser.MoveNext();

        while (parser.Current is not StreamEnd && parser.Current is not null)
        {
            if (parser.Current is not DocumentStart)
            {
                parser.MoveNext();
                continue;
            }
            // Deserialize consumes DocumentStart..DocumentEnd inclusive.
            var raw = deserializer.Deserialize(parser);
            if (raw is null)
            {
                continue; // empty document (e.g. `---\n---`)
            }

            var jsonText = jsonSerializer.Serialize(raw);
            var node = JsonNode.Parse(jsonText);
            if (node is JsonObject obj)
            {
                docs.Add(obj);
            }
            // Non-object documents (a bare scalar, a top-level list) are silently skipped — they
            // can't be SMP inputs anyway, and Kustomize never emits them at the top level.
        }
        return docs;
    }

    /// <summary>
    /// Builds a lookup keyed by GVK + namespace + name. Documents without identifying metadata
    /// (no <c>apiVersion</c>/<c>kind</c>, or no <c>metadata.name</c>) are silently skipped — they
    /// can't be addressed by an SMP caller anyway. Conflicts (same key encountered twice) throw
    /// <see cref="StrategicMergePatchException"/> with the conflicting key in the message.
    /// </summary>
    public static IReadOnlyDictionary<DocumentKey, JsonObject> Index(IEnumerable<JsonObject> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var index = new Dictionary<DocumentKey, JsonObject>();
        foreach (var doc in documents)
        {
            if (TryGetKey(doc, out var key))
            {
                if (!index.TryAdd(key, doc))
                {
                    throw new StrategicMergePatchException(
                        $"Duplicate document key in overlay: {key}.", JsonPointer.Root);
                }
            }
        }
        return index;
    }

    /// <summary>
    /// Linear search for a single document matching the supplied identity. Returns <c>null</c>
    /// when no match exists. For repeat lookups against the same overlay prefer <see cref="Index"/>.
    /// </summary>
    public static JsonObject? Find(
        IEnumerable<JsonObject> documents,
        GroupVersionKind gvk,
        string name,
        string? @namespace = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var doc in documents)
        {
            if (!TryGetKey(doc, out var key))
            {
                continue;
            }
            if (key.Gvk != gvk || key.Name != name)
            {
                continue;
            }
            // Namespace match: when caller specifies, exact compare; when unspecified, accept either.
            if (@namespace is null || string.Equals(key.Namespace, @namespace, StringComparison.Ordinal))
            {
                return doc;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the document's identity. Returns <c>false</c> if the document lacks
    /// <c>apiVersion</c>/<c>kind</c>/<c>metadata.name</c>.
    /// </summary>
    public static bool TryGetKey(JsonObject document, out DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(document);
        var apiVersion = document["apiVersion"]?.GetValue<string>();
        var kind = document["kind"]?.GetValue<string>();
        var name = document["metadata"]?["name"]?.GetValue<string>();
        if (apiVersion is null || kind is null || name is null)
        {
            key = default;
            return false;
        }
        var ns = document["metadata"]?["namespace"]?.GetValue<string>();
        key = new DocumentKey(GroupVersionKind.Parse(apiVersion, kind), ns, name);
        return true;
    }
}
