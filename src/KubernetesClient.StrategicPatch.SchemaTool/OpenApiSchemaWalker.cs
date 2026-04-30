using System.Text.Json;
using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.SchemaTool;

/// <summary>
/// Walks an OpenAPI v3 component schema and builds the corresponding <see cref="SchemaNode"/> tree.
/// Resolves <c>$ref</c> pointers via the supplied component index and guards against cycles with a
/// <see cref="HashSet{T}"/> of visited component names.
/// </summary>
public sealed class OpenApiSchemaWalker
{
    private const string RefPrefix = "#/components/schemas/";

    private readonly IReadOnlyDictionary<string, JsonObject> _index;

    public OpenApiSchemaWalker(IReadOnlyDictionary<string, JsonObject> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <summary>
    /// Builds the tree rooted at the named component. Unknown component names yield an opaque
    /// <see cref="SchemaNodeKind.Object"/> so callers always get a usable root.
    /// </summary>
    public SchemaNode Build(string componentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentName);
        if (!_index.TryGetValue(componentName, out var schema))
        {
            return new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Object };
        }
        var visited = new HashSet<string>(StringComparer.Ordinal) { componentName };
        return BuildFromSchema(schema, jsonName: string.Empty, visited);
    }

    private SchemaNode BuildFromSchema(JsonObject schema, string jsonName, HashSet<string> visited)
    {
        var (mergeKey, strategy, listType) = ReadAnnotations(schema);

        // Kubernetes' generated OpenAPI v3 wraps the $ref inside `allOf` so it can co-locate
        // `default` / `description` alongside the reference (since per OpenAPI 3.0 a $ref's
        // siblings are ignored). Unwrap the single-element allOf into its contained $ref so
        // the rest of this walker can stay simple.
        if (schema["allOf"] is JsonArray allOf && allOf.Count == 1
            && allOf[0] is JsonObject inner && inner["$ref"] is JsonValue innerRef
            && innerRef.GetValueKind() == JsonValueKind.String
            && schema["$ref"] is null)
        {
            schema = inner;
        }

        if (schema["$ref"] is JsonValue refValue && refValue.GetValueKind() == JsonValueKind.String)
        {
            var refName = StripRefPrefix(refValue.GetValue<string>());
            if (!visited.Add(refName))
            {
                // Cycle: opaque object placeholder, but keep property-level annotations.
                return new SchemaNode
                {
                    JsonName = jsonName,
                    Kind = SchemaNodeKind.Object,
                    PatchMergeKey = mergeKey,
                    Strategy = strategy,
                    ListType = listType,
                };
            }
            try
            {
                if (!_index.TryGetValue(refName, out var referent))
                {
                    return new SchemaNode { JsonName = jsonName, Kind = SchemaNodeKind.Object };
                }
                var built = BuildFromSchema(referent, jsonName, visited);
                if (mergeKey is null && strategy == PatchStrategy.None && listType == ListType.Unspecified)
                {
                    return built;
                }
                return built with
                {
                    PatchMergeKey = mergeKey ?? built.PatchMergeKey,
                    Strategy = strategy != PatchStrategy.None ? strategy : built.Strategy,
                    ListType = listType != ListType.Unspecified ? listType : built.ListType,
                };
            }
            finally
            {
                visited.Remove(refName);
            }
        }

        var type = (string?)schema["type"];
        if (type == "array" || schema["items"] is not null)
        {
            var itemSchema = schema["items"] as JsonObject;
            var items = itemSchema is null
                ? new SchemaNode { JsonName = string.Empty, Kind = SchemaNodeKind.Object }
                : BuildFromSchema(itemSchema, string.Empty, visited);
            return new SchemaNode
            {
                JsonName = jsonName,
                Kind = SchemaNodeKind.List,
                Items = items,
                PatchMergeKey = mergeKey,
                Strategy = strategy,
                ListType = listType == ListType.Unspecified
                    ? (strategy.HasFlag(PatchStrategy.Merge) ? ListType.Map : ListType.Unspecified)
                    : listType,
            };
        }

        if (type == "object" || schema["properties"] is not null || schema["additionalProperties"] is not null)
        {
            if (schema["additionalProperties"] is JsonObject additional)
            {
                var values = BuildFromSchema(additional, string.Empty, visited);
                return new SchemaNode
                {
                    JsonName = jsonName,
                    Kind = SchemaNodeKind.Map,
                    Items = values,
                    PatchMergeKey = mergeKey,
                    Strategy = strategy,
                    ListType = listType,
                };
            }

            var props = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
            if (schema["properties"] is JsonObject properties)
            {
                foreach (var (childName, childSchema) in properties)
                {
                    if (childSchema is JsonObject childObj)
                    {
                        props[childName] = BuildFromSchema(childObj, childName, visited);
                    }
                }
            }
            return new SchemaNode
            {
                JsonName = jsonName,
                Kind = SchemaNodeKind.Object,
                Properties = props,
                PatchMergeKey = mergeKey,
                Strategy = strategy,
                ListType = listType,
            };
        }

        return new SchemaNode
        {
            JsonName = jsonName,
            Kind = SchemaNodeKind.Primitive,
            PatchMergeKey = mergeKey,
            Strategy = strategy,
            ListType = listType,
        };
    }

    private static (string? MergeKey, PatchStrategy Strategy, ListType ListType) ReadAnnotations(JsonObject schema)
    {
        var mergeKey = (string?)schema["x-kubernetes-patch-merge-key"];
        var strategyRaw = (string?)schema["x-kubernetes-patch-strategy"];
        var listTypeRaw = (string?)schema["x-kubernetes-list-type"];

        var strategy = PatchStrategy.None;
        if (!string.IsNullOrEmpty(strategyRaw))
        {
            foreach (var part in strategyRaw.Split(','))
            {
                strategy |= part.Trim() switch
                {
                    "replace" => PatchStrategy.Replace,
                    "merge" => PatchStrategy.Merge,
                    "retainKeys" => PatchStrategy.RetainKeys,
                    _ => PatchStrategy.None,
                };
            }
        }

        var listType = listTypeRaw switch
        {
            "atomic" => ListType.Atomic,
            "set" => ListType.Set,
            "map" => ListType.Map,
            _ => ListType.Unspecified,
        };

        return (mergeKey, strategy, listType);
    }

    private static string StripRefPrefix(string r) =>
        r.StartsWith(RefPrefix, StringComparison.Ordinal) ? r[RefPrefix.Length..] : r;
}
