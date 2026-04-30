using k8s.Models;

namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Outcome of a strategic merge patch computation.
/// </summary>
/// <param name="Patch">A <see cref="V1Patch"/> ready to hand to <c>KubernetesClient</c>'s
/// <c>PatchNamespacedXxxAsync</c> APIs. When <see cref="IsEmpty"/> is <c>true</c> the body is the
/// JSON literal <c>{}</c>; the caller should typically <i>skip</i> the API call to avoid
/// "ghost" requests.</param>
/// <param name="IsEmpty"><c>true</c> when the patch carries no diff. Callers should branch on
/// this before invoking the API server — sending an empty SMP still costs a round-trip and a
/// retry-budget hit if the server is rate-limited.</param>
/// <param name="PayloadBytes">UTF-8 byte count of the rendered patch body. Useful for OTel
/// tags and step-summary reporting.</param>
/// <param name="Gvk">GVK identifying the resource the patch targets.</param>
public sealed record StrategicPatchResult(
    V1Patch Patch,
    bool IsEmpty,
    int PayloadBytes,
    GroupVersionKind Gvk);
