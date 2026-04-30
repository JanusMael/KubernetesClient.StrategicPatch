using System.Text;
using System.Text.Json.Nodes;
using k8s;
using k8s.Models;
using KubernetesClient.StrategicPatch.Schema;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Extension methods bridging typed Kubernetes models to the strategic-merge engine.
/// Inputs round-trip through <see cref="KubernetesJson"/> so caller-facing serialization stays
/// identical to the rest of <c>KubernetesClient</c> — including <c>IntOrString</c>,
/// <c>ResourceQuantity</c>, and the K8s-canonical timestamp shape.
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> Every method is safe to call concurrently with different inputs.
/// The diff and apply engines never mutate caller-supplied objects, but callers <i>must not</i>
/// mutate <paramref name="original"/>/<paramref name="modified"/>/<paramref name="current"/> on
/// another thread while a call is in flight — <see cref="System.Text.Json.Nodes.JsonNode"/> is
/// not thread-safe per its own contract, and the K8s model classes are mutable too.</para>
/// </remarks>
public static class StrategicPatchExtensions
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Generates a two-way strategic-merge patch from <paramref name="original"/> to
    /// <paramref name="modified"/>. Returns a <see cref="StrategicPatchResult"/> whose
    /// <see cref="StrategicPatchResult.IsEmpty"/> flag tells the caller whether to skip the
    /// API call. Throws <see cref="StrategicMergePatchException"/> on root-identity mismatch.
    /// </summary>
    public static StrategicPatchResult CreateStrategicPatch<T>(
        this T original,
        T modified,
        StrategicPatchOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : IKubernetesObject
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);

        options = WithDefaultSchemaProvider(options);
        var originalDom = ToDom(original);
        var modifiedDom = ToDom(modified);
        var gvk = ResolveGvk<T>(originalDom, modifiedDom);

        var patch = TwoWayMerge.CreateTwoWayMergePatch(originalDom, modifiedDom, options, cancellationToken);
        return Wrap(patch, gvk);
    }

    /// <summary>
    /// Generates a three-way strategic-merge patch (last-applied → desired vs. live).
    /// Throws <see cref="StrategicMergePatchConflictException"/> when caller-side and
    /// server-side changes disagree, unless
    /// <see cref="StrategicPatchOptions.OverwriteConflicts"/> is set.
    /// </summary>
    public static StrategicPatchResult CreateThreeWayStrategicPatch<T>(
        this T original,
        T modified,
        T current,
        StrategicPatchOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : IKubernetesObject
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ArgumentNullException.ThrowIfNull(current);

        options = WithDefaultSchemaProvider(options);
        var originalDom = ToDom(original);
        var modifiedDom = ToDom(modified);
        var currentDom = ToDom(current);
        var gvk = ResolveGvk<T>(originalDom, modifiedDom, currentDom);

        var patch = ThreeWayMerge.CreateThreeWayMergePatch(originalDom, modifiedDom, currentDom, options, cancellationToken);
        return Wrap(patch, gvk);
    }

    /// <summary>
    /// Server-side application of a strategic merge patch onto a typed object. Returns the
    /// merged document deserialised back into <typeparamref name="T"/>. Useful for tests and
    /// for client-side simulation of what the API server will produce.
    /// </summary>
    public static T ApplyStrategicPatch<T>(
        this T original,
        StrategicPatchResult patch,
        StrategicPatchOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : IKubernetesObject
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.IsEmpty)
        {
            // No-op: the original is already the desired state per SMP semantics.
            return Clone(original);
        }

        options = WithDefaultSchemaProvider(options);
        var originalDom = ToDom(original);
        var patchDom = ParsePatchBody(patch.Patch);
        var merged = PatchApply.StrategicMergePatch(originalDom, patchDom, options, cancellationToken);
        return FromDom<T>(merged);
    }

    /// <summary>
    /// If the caller did not supply a <see cref="ISchemaProvider"/>, default to the
    /// process-wide <see cref="EmbeddedSchemaProvider.Shared"/>. The library ships a baked
    /// snapshot of Kubernetes built-ins; consumers get strategic-merge semantics
    /// (list-merge by name, $setElementOrder, $deleteFromPrimitiveList, $retainKeys) without
    /// having to construct or register a provider themselves.
    /// </summary>
    /// <remarks>
    /// Callers who explicitly want RFC 7396 fallback semantics for everything (e.g. for tests
    /// that mirror the Go reference's behaviour without schema metadata) can pass an explicit
    /// <see cref="StrategicPatchOptions"/> with <see cref="StrategicPatchOptions.SchemaProvider"/>
    /// set to a <see cref="InMemorySchemaProvider"/> with no entries.
    /// </remarks>
    private static StrategicPatchOptions WithDefaultSchemaProvider(StrategicPatchOptions? options)
    {
        if (options is null)
        {
            return new StrategicPatchOptions { SchemaProvider = EmbeddedSchemaProvider.Shared };
        }
        if (options.SchemaProvider is not null)
        {
            return options;
        }
        return options with { SchemaProvider = EmbeddedSchemaProvider.Shared };
    }

    /// <summary>
    /// Reads a <see cref="V1Patch"/> body into a <see cref="JsonObject"/>. <see cref="V1Patch.Content"/>
    /// is typed as <see cref="object"/>; in practice it is a JSON string for SMP, but a future or
    /// custom caller could supply <see cref="byte"/>[] or <see cref="ReadOnlyMemory{Byte}"/>. We
    /// branch on the runtime type rather than blindly casting to <see cref="string"/>.
    /// </summary>
    private static JsonObject ParsePatchBody(V1Patch patch)
    {
        switch (patch.Content)
        {
            case string s:
                return ParseOrThrow(s);
            case byte[] bytes:
                return ParseOrThrow(System.Text.Encoding.UTF8.GetString(bytes));
            case ReadOnlyMemory<byte> mem:
                return ParseOrThrow(System.Text.Encoding.UTF8.GetString(mem.Span));
            case JsonObject obj:
                return (JsonObject)obj.DeepClone();
            case null:
                throw new StrategicMergePatchException("V1Patch.Content is null.", JsonPointer.Root);
            default:
                throw new StrategicMergePatchException(
                    $"Unsupported V1Patch.Content type '{patch.Content.GetType().FullName}'. " +
                    "Expected string, byte[], ReadOnlyMemory<byte>, or JsonObject.",
                    JsonPointer.Root);
        }

        static JsonObject ParseOrThrow(string body)
        {
            var node = JsonNode.Parse(body)
                ?? throw new StrategicMergePatchException("V1Patch body parsed to null.", JsonPointer.Root);
            if (node is not JsonObject obj)
            {
                throw new StrategicMergePatchException(
                    $"V1Patch body must be a JSON object; got {node.GetValueKind()}.", JsonPointer.Root);
            }
            return obj;
        }
    }

    // ---- Internals ---------------------------------------------------------------------------

    private static JsonObject ToDom<T>(T obj) where T : IKubernetesObject
    {
        var json = KubernetesJson.Serialize(obj);
        return (JsonObject)JsonNode.Parse(json)!;
    }

    private static T FromDom<T>(JsonObject dom) where T : IKubernetesObject =>
        KubernetesJson.Deserialize<T>(dom.ToJsonString())!;

    private static T Clone<T>(T obj) where T : IKubernetesObject =>
        KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(obj))!;

    private static GroupVersionKind ResolveGvk<T>(params JsonObject?[] candidates) where T : IKubernetesObject
    {
        foreach (var doc in candidates)
        {
            if (doc is null)
            {
                continue;
            }
            var apiVersion = doc.TryGetPropertyValue("apiVersion", out var av) ? av?.GetValue<string>() : null;
            var kind = doc.TryGetPropertyValue("kind", out var kv) ? kv?.GetValue<string>() : null;
            if (apiVersion is not null && kind is not null)
            {
                return GroupVersionKind.Parse(apiVersion, kind);
            }
        }
        // Fall back to the type-level [KubernetesEntity] attribute when documents lack metadata.
        var attrGvk = KubernetesEntityResolver.TryGetGvk(typeof(T));
        if (attrGvk is not null)
        {
            return attrGvk.Value;
        }
        throw new StrategicMergePatchException(
            $"Could not resolve GVK for {typeof(T).Name}: no apiVersion/kind on inputs and no [KubernetesEntity] attribute.",
            JsonPointer.Root);
    }

    private static StrategicPatchResult Wrap(JsonObject? patch, GroupVersionKind gvk)
    {
        if (patch is null)
        {
            return new StrategicPatchResult(
                Patch: new V1Patch(body: "{}", type: V1Patch.PatchType.StrategicMergePatch),
                IsEmpty: true,
                PayloadBytes: 2,
                Gvk: gvk);
        }
        var body = patch.ToJsonString();
        return new StrategicPatchResult(
            Patch: new V1Patch(body: body, type: V1Patch.PatchType.StrategicMergePatch),
            IsEmpty: false,
            PayloadBytes: Utf8NoBom.GetByteCount(body),
            Gvk: gvk);
    }
}
