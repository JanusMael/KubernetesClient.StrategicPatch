namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Resolves Kubernetes strategic-merge schema metadata for a given resource and JSON path.
/// Implementations may source schemas from embedded OpenAPI snapshots, source-generated
/// providers, live API server discovery, or hand-built fixtures for tests.
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Implementations must be safe for concurrent
/// <see cref="GetRootSchema"/> / <see cref="Resolve"/> calls. The diff/apply engines call
/// resolution methods from multiple threads when processing concurrent diffs that share a
/// provider instance.</para>
/// </remarks>
public interface ISchemaProvider
{
    /// <summary>
    /// Returns the schema rooted at the given resource, or <c>null</c> if the resource is unknown.
    /// </summary>
    SchemaNode? GetRootSchema(GroupVersionKind gvk);

    /// <summary>
    /// Walks from the resource root down to <paramref name="path"/>. Returns <c>null</c> if any
    /// segment leaves the known schema (custom fields, unknown CRD, schema-miss fallback territory).
    /// </summary>
    SchemaNode? Resolve(GroupVersionKind gvk, JsonPointer path)
        => GetRootSchema(gvk)?.Resolve(path);

    /// <summary>
    /// Short, observable identity of the provider. Surfaces on activity tags so traces can
    /// distinguish "served by Embedded" from "served by Generated" from "served by Composite[...]".
    /// Default implementation returns the simple type name; <see cref="CompositeSchemaProvider"/>
    /// composes its constituents' names.
    /// </summary>
    string Name => GetType().Name;
}
