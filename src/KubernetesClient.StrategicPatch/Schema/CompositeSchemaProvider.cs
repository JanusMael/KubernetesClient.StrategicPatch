namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Chains schema providers; the first to return a non-null root schema wins.
/// Use this to layer custom CRD providers in front of the embedded snapshot.
/// </summary>
public sealed class CompositeSchemaProvider : ISchemaProvider
{
    private readonly IReadOnlyList<ISchemaProvider> _providers;

    public CompositeSchemaProvider(params ISchemaProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Length == 0)
        {
            throw new ArgumentException("At least one provider is required.", nameof(providers));
        }
        foreach (var p in providers)
        {
            if (p is null)
            {
                throw new ArgumentException("Providers must not be null.", nameof(providers));
            }
        }
        _providers = providers;
    }

    public SchemaNode? GetRootSchema(GroupVersionKind gvk)
    {
        foreach (var provider in _providers)
        {
            var root = provider.GetRootSchema(gvk);
            if (root is not null)
            {
                return root;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public string Name => $"Composite[{string.Join(",", _providers.Select(p => p.Name))}]";
}
