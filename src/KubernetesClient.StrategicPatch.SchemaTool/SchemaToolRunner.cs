using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.Schema;

namespace KubernetesClient.StrategicPatch.SchemaTool;

/// <summary>
/// Programmatic entry point for the SchemaTool — usable both by the console <c>Main</c>
/// and by tests that want to drive a complete end-to-end run without spawning a process.
/// </summary>
public static class SchemaToolRunner
{
    /// <summary>
    /// Reads each <paramref name="inputPaths"/> as an OpenAPI v3 document, walks every schema that
    /// declares <c>x-kubernetes-group-version-kind</c>, and writes a minified schemas.json blob
    /// to <paramref name="outputPath"/>.
    /// </summary>
    /// <returns>Number of GVKs written.</returns>
    public static int Run(string outputPath, IEnumerable<string> inputPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(inputPaths);

        var index = LoadIndex(inputPaths);
        var roots = BuildRoots(index);

        var bytes = SchemaWireFormat.Serialize(roots);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(outputPath, bytes);
        return roots.Count;
    }

    /// <summary>
    /// Builds the in-memory GVK→SchemaNode map from a pre-built component index. Useful for
    /// tests that want to skip file I/O entirely.
    /// </summary>
    public static IReadOnlyDictionary<GroupVersionKind, SchemaNode> BuildRoots(
        IReadOnlyDictionary<string, JsonObject> index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var walker = new OpenApiSchemaWalker(index);
        var roots = new Dictionary<GroupVersionKind, SchemaNode>();
        foreach (var (name, schema) in index)
        {
            if (schema["x-kubernetes-group-version-kind"] is not JsonArray gvkList)
            {
                continue;
            }
            foreach (var gvkNode in gvkList)
            {
                if (gvkNode is not JsonObject gvkObj)
                {
                    continue;
                }
                roots[ReadGvk(gvkObj)] = walker.Build(name);
            }
        }
        return roots;
    }

    private static Dictionary<string, JsonObject> LoadIndex(IEnumerable<string> inputPaths)
    {
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var path in inputPaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"OpenAPI input not found: {path}", path);
            }
            using var fs = File.OpenRead(path);
            var doc = JsonNode.Parse(fs) as JsonObject
                ?? throw new InvalidDataException($"OpenAPI v3 root must be a JSON object: {path}");
            if (doc["components"] is JsonObject components &&
                components["schemas"] is JsonObject schemas)
            {
                foreach (var (name, schema) in schemas)
                {
                    if (schema is JsonObject obj)
                    {
                        index[name] = obj;
                    }
                }
            }
        }
        return index;
    }

    private static GroupVersionKind ReadGvk(JsonObject obj)
    {
        var group = (string?)obj["group"] ?? string.Empty;
        var version = (string?)obj["version"]
            ?? throw new InvalidDataException("x-kubernetes-group-version-kind missing 'version'.");
        var kind = (string?)obj["kind"]
            ?? throw new InvalidDataException("x-kubernetes-group-version-kind missing 'kind'.");
        return new GroupVersionKind(group, version, kind);
    }
}
