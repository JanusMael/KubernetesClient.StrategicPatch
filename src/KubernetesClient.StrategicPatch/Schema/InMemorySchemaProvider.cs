using System.Collections.Frozen;

namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Schema provider backed by an in-memory dictionary of GVK to root <see cref="SchemaNode"/>.
/// Useful for tests and for callers that build schemas at runtime (e.g. dynamic CRD discovery).
/// </summary>
public sealed class InMemorySchemaProvider : ISchemaProvider
{
    private readonly FrozenDictionary<GroupVersionKind, SchemaNode> _roots;

    public InMemorySchemaProvider(IReadOnlyDictionary<GroupVersionKind, SchemaNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots.ToFrozenDictionary();
    }

    public SchemaNode? GetRootSchema(GroupVersionKind gvk) =>
        _roots.TryGetValue(gvk, out var root) ? root : null;

    /// <inheritdoc />
    public string Name => "InMemory";
}
