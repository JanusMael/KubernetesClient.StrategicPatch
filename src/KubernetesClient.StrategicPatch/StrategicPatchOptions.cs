using KubernetesClient.StrategicPatch.Schema;
using Microsoft.Extensions.Logging;

namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Configuration for strategic merge patch generation. Defaults match Go's
/// <c>strategicpatch.CreateTwoWayMergePatch</c> behavior with no options set.
/// </summary>
/// <remarks>
/// <para><b>Thread safety.</b> <see cref="StrategicPatchOptions"/> is an immutable
/// <see langword="record"/>; instances are safe to share across threads. The
/// <see cref="SchemaProvider"/> property is read-only here, but its underlying
/// implementation must be thread-safe for concurrent <see cref="Schema.ISchemaProvider.GetRootSchema"/>
/// calls — the providers shipped in this library are.</para>
/// </remarks>
public sealed record StrategicPatchOptions
{
    /// <summary>
    /// When <c>true</c>, treat null/missing values in <c>modified</c> as "leave alone": no delete
    /// markers are emitted for keys present in <c>original</c> but absent or explicitly null in
    /// <c>modified</c>. Use this when the caller passes a sparse object (e.g. only the fields they
    /// want to update) and other fields should be preserved on the server.
    /// </summary>
    public bool IgnoreNullValuesInModified { get; init; }

    /// <summary>
    /// When <c>true</c>, the generated patch carries <c>metadata.uid</c> and
    /// <c>metadata.resourceVersion</c> copied from <c>original</c>. The server then rejects the
    /// patch with HTTP 409 if the resource has drifted since <c>original</c> was read.
    /// </summary>
    public bool EnforceOptimisticConcurrency { get; init; }

    /// <summary>
    /// Three-way merge only. When <c>true</c>, server-side changes that disagree with caller-side
    /// changes are silently overwritten by the caller. When <c>false</c> (the default),
    /// disagreements throw <see cref="StrategicMergePatchConflictException"/>. Mirrors the
    /// <c>overwrite</c> parameter of Go's <c>strategicpatch.CreateThreeWayMergePatch</c>.
    /// </summary>
    public bool OverwriteConflicts { get; init; }

    /// <summary>
    /// Optional logger; structured events are emitted at <c>Debug</c> for path-by-path decisions
    /// and <c>Information</c> for the final patch summary. <c>null</c> by default.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Schema source. When <c>null</c> (or when the provider returns <c>null</c> for a path), the
    /// engine falls back to RFC 7396 JSON Merge Patch semantics for that subtree.
    /// </summary>
    public ISchemaProvider? SchemaProvider { get; init; }

    /// <summary>
    /// Maximum recursion depth permitted when walking objects, lists, or patch DOMs. Adversarial
    /// inputs (deeply nested CRDs, malicious payloads) are bounded to prevent stack overflow.
    /// Matches <see cref="System.Text.Json.JsonSerializerOptions.MaxDepth"/>'s default of 256.
    /// </summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>The default options instance — all fields at their defaults.</summary>
    public static StrategicPatchOptions Default { get; } = new();
}
