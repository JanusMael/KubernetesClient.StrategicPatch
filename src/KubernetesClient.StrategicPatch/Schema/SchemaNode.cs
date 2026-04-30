using System.Collections.Frozen;

namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>Top-level kind of a schema node.</summary>
public enum SchemaNodeKind
{
    Primitive,
    Object,
    /// <summary>A JSON object whose values share a single schema (OpenAPI <c>additionalProperties</c>).</summary>
    Map,
    List,
}

/// <summary>
/// Strategic merge patch strategies declared via <c>x-kubernetes-patch-strategy</c>.
/// Multiple values may be combined (e.g. <c>merge,retainKeys</c>).
/// </summary>
[Flags]
public enum PatchStrategy
{
    None = 0,
    Replace = 1 << 0,
    Merge = 1 << 1,
    RetainKeys = 1 << 2,
}

/// <summary>
/// List merge semantics declared via <c>x-kubernetes-list-type</c>.
/// </summary>
public enum ListType
{
    /// <summary>No declared list type. Treated as <see cref="Atomic"/> by SMP.</summary>
    Unspecified,
    /// <summary>The list is replaced wholesale on patch.</summary>
    Atomic,
    /// <summary>The list is a de-duplicating set of primitives.</summary>
    Set,
    /// <summary>The list is keyed by one or more primitive merge keys.</summary>
    Map,
}

/// <summary>
/// A node in the strategic-merge schema tree resolved from Kubernetes OpenAPI v3.
/// Carries the patch metadata the diff engine needs at every recursion step.
/// </summary>
public sealed record SchemaNode
{
    /// <summary>JSON property name as it appears in serialized payloads.</summary>
    public required string JsonName { get; init; }

    /// <summary>What kind of value lives here.</summary>
    public required SchemaNodeKind Kind { get; init; }

    /// <summary>
    /// Merge key for keyed object lists, taken from <c>x-kubernetes-patch-merge-key</c>.
    /// May be a single field ("name") or a comma-separated compound key ("port,protocol").
    /// </summary>
    public string? PatchMergeKey { get; init; }

    /// <summary>Patch strategy. Defaults to <see cref="PatchStrategy.None"/> (replace semantics).</summary>
    public PatchStrategy Strategy { get; init; } = PatchStrategy.None;

    /// <summary>List merge semantics; only meaningful when <see cref="Kind"/> is <see cref="SchemaNodeKind.List"/>.</summary>
    public ListType ListType { get; init; } = ListType.Unspecified;

    /// <summary>Child schemas keyed by JSON property name. Empty for non-objects.</summary>
    public IReadOnlyDictionary<string, SchemaNode> Properties { get; init; } =
        FrozenDictionary<string, SchemaNode>.Empty;

    /// <summary>
    /// Schema for list elements (when <see cref="Kind"/> is <see cref="SchemaNodeKind.List"/>) or for
    /// map values (when <see cref="Kind"/> is <see cref="SchemaNodeKind.Map"/>). <c>null</c> otherwise.
    /// </summary>
    public SchemaNode? Items { get; init; }

    /// <summary>
    /// Walks the tree by JSON-property segments, skipping numeric (array index) segments.
    /// Returns <c>null</c> if any non-numeric segment is missing from the schema.
    /// </summary>
    public SchemaNode? Resolve(JsonPointer path)
    {
        var node = this;
        foreach (var segment in path)
        {
            if (IsArrayIndex(segment))
            {
                if (node!.Kind != SchemaNodeKind.List || node.Items is null)
                {
                    return null;
                }
                node = node.Items;
                continue;
            }

            switch (node!.Kind)
            {
                case SchemaNodeKind.Object:
                    if (!node.Properties.TryGetValue(segment, out var child))
                    {
                        return null;
                    }
                    node = child;
                    break;
                case SchemaNodeKind.Map:
                    // Every key in a map shares the same value schema.
                    if (node.Items is null)
                    {
                        return null;
                    }
                    node = node.Items;
                    break;
                default:
                    return null;
            }
        }
        return node;
    }

    private static bool IsArrayIndex(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }
        foreach (var c in segment)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Deep structural equality between two schema trees. The auto-implemented record
    /// <see cref="Equals(object?)"/> falls back to reference equality on
    /// <see cref="Properties"/> (the field is <see cref="IReadOnlyDictionary{TKey,TValue}"/>,
    /// which doesn't override <c>Equals</c>), so this static helper is required for any
    /// "are these two trees the same shape?" comparison — including the source-generator
    /// round-trip check that proves an emitted tree matches the wire-format snapshot.
    /// </summary>
    public static bool StructuralEquals(SchemaNode? left, SchemaNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null)
        {
            return false;
        }
        if (left.JsonName != right.JsonName
            || left.Kind != right.Kind
            || left.PatchMergeKey != right.PatchMergeKey
            || left.Strategy != right.Strategy
            || left.ListType != right.ListType)
        {
            return false;
        }
        if (!StructuralEquals(left.Items, right.Items))
        {
            return false;
        }
        if (left.Properties.Count != right.Properties.Count)
        {
            return false;
        }
        foreach (var (key, leftChild) in left.Properties)
        {
            if (!right.Properties.TryGetValue(key, out var rightChild)
                || !StructuralEquals(leftChild, rightChild))
            {
                return false;
            }
        }
        return true;
    }
}
