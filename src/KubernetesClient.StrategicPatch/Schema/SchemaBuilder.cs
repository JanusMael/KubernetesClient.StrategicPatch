using System.Collections.Frozen;

namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Fluent factory for <see cref="SchemaNode"/> trees. Optimised for two callers:
/// <list type="bullet">
///   <item>Tests authoring small in-memory schemas inline.</item>
///   <item>The forthcoming Roslyn source generator emitting compile-time schema constants.
///         Source generators are easier to read and easier to evolve when the emitted code
///         calls a small, stable API surface than when they hand-roll <see cref="FrozenDictionary"/>
///         construction inline.</item>
/// </list>
/// All factory methods are pure: they return new immutable nodes without mutating caller state.
/// The <see cref="ObjectNode"/> overloads come in two flavours — a <see cref="Action"/>-based
/// builder for hand-authored trees, and a dictionary-taking variant for codegen where the
/// schema's properties are known up front.
/// </summary>
/// <remarks>
/// <para><b>Source-generator contract.</b> The generator may rely on these signatures being
/// stable across minor versions. Any breaking change to <see cref="SchemaBuilder"/> or
/// <see cref="SchemaNode"/> bumps the wire-format version constant in
/// <c>Internal/SchemaWireFormat.cs</c> so consumers regenerate.</para>
/// <para><b>Thread safety.</b> Every returned <see cref="SchemaNode"/> is immutable; callers can
/// share instances across threads without coordination.</para>
/// </remarks>
public static class SchemaBuilder
{
    /// <summary>A primitive leaf — string, number, bool, or null.</summary>
    public static SchemaNode Primitive(string jsonName = "") =>
        new() { JsonName = jsonName, Kind = SchemaNodeKind.Primitive };

    /// <summary>A free-form object whose values share a single value-schema (OpenAPI <c>additionalProperties</c>).</summary>
    public static SchemaNode Map(SchemaNode valueSchema, string jsonName = "")
    {
        ArgumentNullException.ThrowIfNull(valueSchema);
        return new SchemaNode
        {
            JsonName = jsonName,
            Kind = SchemaNodeKind.Map,
            Items = valueSchema,
        };
    }

    /// <summary>An object built from a property-name → child-schema dictionary.</summary>
    public static SchemaNode ObjectNode(
        string jsonName,
        IReadOnlyDictionary<string, SchemaNode> properties,
        PatchStrategy strategy = PatchStrategy.None)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return new SchemaNode
        {
            JsonName = jsonName,
            Kind = SchemaNodeKind.Object,
            Properties = properties is FrozenDictionary<string, SchemaNode> frozen
                ? frozen
                : properties.ToFrozenDictionary(StringComparer.Ordinal),
            Strategy = strategy,
        };
    }

    /// <summary>An object built via a fluent builder action — ergonomic for tests.</summary>
    public static SchemaNode ObjectNode(
        string jsonName,
        Action<ObjectSchemaBuilder> configure,
        PatchStrategy strategy = PatchStrategy.None)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var b = new ObjectSchemaBuilder();
        configure(b);
        return ObjectNode(jsonName, b.Build(), strategy);
    }

    /// <summary>A list with the given element schema and optional merge metadata.</summary>
    public static SchemaNode ListNode(
        string jsonName,
        SchemaNode itemSchema,
        PatchStrategy strategy = PatchStrategy.None,
        string? patchMergeKey = null,
        ListType listType = ListType.Unspecified)
    {
        ArgumentNullException.ThrowIfNull(itemSchema);
        return new SchemaNode
        {
            JsonName = jsonName,
            Kind = SchemaNodeKind.List,
            Items = itemSchema,
            Strategy = strategy,
            PatchMergeKey = patchMergeKey,
            ListType = listType,
        };
    }

    /// <summary>
    /// Builder for the property dictionary of an <see cref="SchemaNodeKind.Object"/> node.
    /// </summary>
    public sealed class ObjectSchemaBuilder
    {
        private readonly Dictionary<string, SchemaNode> _properties = new(StringComparer.Ordinal);

        /// <summary>Adds a property with a pre-built schema.</summary>
        public ObjectSchemaBuilder Property(string jsonName, SchemaNode schema)
        {
            ArgumentException.ThrowIfNullOrEmpty(jsonName);
            ArgumentNullException.ThrowIfNull(schema);
            _properties[jsonName] = schema;
            return this;
        }

        /// <summary>Adds a primitive property.</summary>
        public ObjectSchemaBuilder Primitive(string jsonName) =>
            Property(jsonName, SchemaBuilder.Primitive(jsonName));

        /// <summary>Adds a map property (free-form object with shared value-schema).</summary>
        public ObjectSchemaBuilder Map(string jsonName, SchemaNode valueSchema) =>
            Property(jsonName, SchemaBuilder.Map(valueSchema, jsonName));

        /// <summary>Adds an object property built via a nested builder.</summary>
        public ObjectSchemaBuilder ObjectProperty(
            string jsonName, Action<ObjectSchemaBuilder> configure, PatchStrategy strategy = PatchStrategy.None) =>
            Property(jsonName, ObjectNode(jsonName, configure, strategy));

        /// <summary>Adds a list property.</summary>
        public ObjectSchemaBuilder ListProperty(
            string jsonName,
            SchemaNode itemSchema,
            PatchStrategy strategy = PatchStrategy.None,
            string? patchMergeKey = null,
            ListType listType = ListType.Unspecified) =>
            Property(jsonName, ListNode(jsonName, itemSchema, strategy, patchMergeKey, listType));

        internal IReadOnlyDictionary<string, SchemaNode> Build() => _properties;
    }
}
