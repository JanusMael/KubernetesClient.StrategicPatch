using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.Internal;

/// <summary>
/// Serialization for the embedded schemas.json artifact produced by the SchemaTool and consumed
/// by <see cref="EmbeddedSchemaProvider"/>. Uses single-letter property names for compactness;
/// default values are omitted to shrink the embedded payload.
/// </summary>
internal static class SchemaWireFormat
{
    /// <summary>Current wire format version. Bumped on breaking shape changes.</summary>
    public const int CurrentVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(IReadOnlyDictionary<GroupVersionKind, SchemaNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var doc = new WireDoc
        {
            Version = CurrentVersion,
            Schemas = new Dictionary<string, WireNode>(roots.Count, StringComparer.Ordinal),
        };
        foreach (var (gvk, node) in roots)
        {
            doc.Schemas[FormatGvkKey(gvk)] = ToWire(node);
        }
        return JsonSerializer.SerializeToUtf8Bytes(doc, JsonOptions);
    }

    public static FrozenDictionary<GroupVersionKind, SchemaNode> Deserialize(ReadOnlySpan<byte> utf8)
    {
        WireDoc? doc;
        try
        {
            doc = JsonSerializer.Deserialize<WireDoc>(utf8, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StrategicMergePatchException(
                $"schemas.json is not well-formed JSON: {ex.Message}", JsonPointer.Root);
        }
        if (doc is null)
        {
            throw new StrategicMergePatchException(
                "schemas.json deserialized to null.", JsonPointer.Root);
        }
        if (doc.Version != CurrentVersion)
        {
            throw new StrategicMergePatchException(
                $"Unsupported schemas.json wire version {doc.Version}; this build of "
                + $"KubernetesClient.StrategicPatch expects v{CurrentVersion}. "
                + "Re-run scripts/regen-schemas.sh after updating the library.",
                JsonPointer.Root);
        }
        var dict = new Dictionary<GroupVersionKind, SchemaNode>(doc.Schemas?.Count ?? 0);
        if (doc.Schemas is not null)
        {
            foreach (var (key, node) in doc.Schemas)
            {
                dict[ParseGvkKey(key)] = FromWire(node, jsonName: string.Empty);
            }
        }
        return dict.ToFrozenDictionary();
    }

    private static string FormatGvkKey(GroupVersionKind gvk) =>
        gvk.Group.Length == 0 ? $"{gvk.Version}/{gvk.Kind}" : $"{gvk.Group}/{gvk.Version}/{gvk.Kind}";

    private static GroupVersionKind ParseGvkKey(string key)
    {
        var parts = key.Split('/');
        return parts.Length switch
        {
            2 => new GroupVersionKind(string.Empty, parts[0], parts[1]),
            3 => new GroupVersionKind(parts[0], parts[1], parts[2]),
            _ => throw new FormatException($"Invalid GVK key '{key}'."),
        };
    }

    private static WireNode ToWire(SchemaNode node)
    {
        var wire = new WireNode { K = EncodeKind(node.Kind) };
        if (node.PatchMergeKey is not null)
        {
            wire.Mk = node.PatchMergeKey;
        }
        if (node.Strategy != PatchStrategy.None)
        {
            wire.Ps = (int)node.Strategy;
        }
        if (node.ListType != ListType.Unspecified)
        {
            wire.Lt = EncodeListType(node.ListType);
        }
        if (node.Properties.Count > 0)
        {
            var props = new Dictionary<string, WireNode>(node.Properties.Count, StringComparer.Ordinal);
            foreach (var (childName, childNode) in node.Properties)
            {
                props[childName] = ToWire(childNode);
            }
            wire.P = props;
        }
        if (node.Items is not null)
        {
            wire.I = ToWire(node.Items);
        }
        return wire;
    }

    private static SchemaNode FromWire(WireNode wire, string jsonName)
    {
        var properties = wire.P is { Count: > 0 }
            ? wire.P.ToDictionary(
                kvp => kvp.Key,
                kvp => FromWire(kvp.Value, kvp.Key),
                StringComparer.Ordinal)
                .ToFrozenDictionary()
            : (IReadOnlyDictionary<string, SchemaNode>)FrozenDictionary<string, SchemaNode>.Empty;

        return new SchemaNode
        {
            JsonName = jsonName,
            Kind = DecodeKind(wire.K),
            PatchMergeKey = wire.Mk,
            Strategy = wire.Ps is { } ps ? (PatchStrategy)ps : PatchStrategy.None,
            ListType = wire.Lt is { } lt ? DecodeListType(lt) : ListType.Unspecified,
            Properties = properties,
            Items = wire.I is null ? null : FromWire(wire.I, jsonName: string.Empty),
        };
    }

    private static string EncodeKind(SchemaNodeKind kind) => kind switch
    {
        SchemaNodeKind.Object => "O",
        SchemaNodeKind.Map => "M",
        SchemaNodeKind.List => "L",
        SchemaNodeKind.Primitive => "P",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static SchemaNodeKind DecodeKind(string? code) => code switch
    {
        "O" => SchemaNodeKind.Object,
        "M" => SchemaNodeKind.Map,
        "L" => SchemaNodeKind.List,
        "P" => SchemaNodeKind.Primitive,
        _ => throw new InvalidDataException($"Unknown schema kind code '{code}'."),
    };

    private static string EncodeListType(ListType lt) => lt switch
    {
        ListType.Atomic => "a",
        ListType.Set => "s",
        ListType.Map => "m",
        _ => throw new ArgumentOutOfRangeException(nameof(lt), lt, null),
    };

    private static ListType DecodeListType(string code) => code switch
    {
        "a" => ListType.Atomic,
        "s" => ListType.Set,
        "m" => ListType.Map,
        _ => throw new InvalidDataException($"Unknown list-type code '{code}'."),
    };

    private sealed class WireDoc
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("s")] public Dictionary<string, WireNode>? Schemas { get; set; }
    }

    private sealed class WireNode
    {
        [JsonPropertyName("k")] public string? K { get; set; }
        [JsonPropertyName("mk")] public string? Mk { get; set; }
        [JsonPropertyName("ps")] public int? Ps { get; set; }
        [JsonPropertyName("lt")] public string? Lt { get; set; }
        [JsonPropertyName("p")] public Dictionary<string, WireNode>? P { get; set; }
        [JsonPropertyName("i")] public WireNode? I { get; set; }
    }
}
