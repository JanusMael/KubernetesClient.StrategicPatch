using System.Collections.Frozen;
using System.Reflection;
using KubernetesClient.StrategicPatch.Internal;

namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Schema provider backed by a <c>schemas.json</c> file shipped as an embedded resource in
/// this assembly. The payload is parsed once on first use.
/// </summary>
public sealed class EmbeddedSchemaProvider : ISchemaProvider
{
    private const string DefaultResourceName = "KubernetesClient.StrategicPatch.schemas.json";

    private static readonly Lazy<FrozenDictionary<GroupVersionKind, SchemaNode>> DefaultRoots =
        new(static () => LoadFrom(typeof(EmbeddedSchemaProvider).Assembly, DefaultResourceName));

    private readonly Lazy<FrozenDictionary<GroupVersionKind, SchemaNode>> _roots;

    /// <summary>
    /// Process-wide singleton instance reading the schemas.json embedded into this library.
    /// Allocation-free for repeat use; thread-safe by construction (the underlying
    /// <see cref="Lazy{T}"/> is initialised exactly once across all callers). Prefer this over
    /// constructing a new <see cref="EmbeddedSchemaProvider"/> per call.
    /// </summary>
    public static EmbeddedSchemaProvider Shared { get; } = new();

    /// <summary>Reads the schemas.json embedded into this library.</summary>
    public EmbeddedSchemaProvider()
    {
        _roots = DefaultRoots;
    }

    /// <summary>Reads schemas.json from a custom assembly/resource.</summary>
    public EmbeddedSchemaProvider(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        _roots = new Lazy<FrozenDictionary<GroupVersionKind, SchemaNode>>(
            () => LoadFrom(assembly, resourceName));
    }

    public SchemaNode? GetRootSchema(GroupVersionKind gvk) =>
        _roots.Value.TryGetValue(gvk, out var root) ? root : null;

    /// <inheritdoc />
    public string Name => "Embedded";

    /// <summary>Number of GVKs available. Forces lazy load.</summary>
    public int Count => _roots.Value.Count;

    private static FrozenDictionary<GroupVersionKind, SchemaNode> LoadFrom(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Empty fallback so consumers can still construct the provider before the SchemaTool
            // has produced an artifact (e.g. during early Stage 1 development). Resolve will
            // simply return null for every GVK.
            return FrozenDictionary<GroupVersionKind, SchemaNode>.Empty;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return SchemaWireFormat.Deserialize(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }
}
