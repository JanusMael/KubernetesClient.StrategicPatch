using System.Diagnostics;
using System.Reflection;

namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Library-wide <see cref="ActivitySource"/> used by the diff and apply engines.
/// Listeners can subscribe to <c>"KubernetesClient.StrategicPatch"</c> to receive
/// <c>smp.compute_two_way</c>, <c>smp.compute_three_way</c>, <c>smp.apply</c> spans
/// plus per-subtree events such as <c>smp.schema_miss</c>.
/// </summary>
public static class StrategicPatchActivity
{
    /// <summary>The <see cref="ActivitySource"/> name. Used for OTel listeners.</summary>
    public const string Name = "KubernetesClient.StrategicPatch";

    /// <summary>The shared <see cref="ActivitySource"/> instance.</summary>
    public static readonly ActivitySource Source = new(
        Name,
        version: typeof(StrategicPatchActivity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(StrategicPatchActivity).Assembly.GetName().Version?.ToString()
            ?? "unknown");
}
