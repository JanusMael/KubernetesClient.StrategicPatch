using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using k8s.Models;

namespace KubernetesClient.StrategicPatch.Schema;

/// <summary>
/// Resolves a CLR type to its <see cref="GroupVersionKind"/> via the
/// <see cref="KubernetesEntityAttribute"/> the KubernetesClient ships on every model type.
/// </summary>
public static class KubernetesEntityResolver
{
    private static readonly ConcurrentDictionary<Type, GroupVersionKind?> Cache = new();

    /// <summary>
    /// Returns the GVK for <paramref name="type"/>, or <c>null</c> if the type is not annotated.
    /// </summary>
    public static GroupVersionKind? TryGetGvk(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, ResolveUncached);
    }

    /// <summary>Strict variant; throws when the attribute is absent.</summary>
    public static GroupVersionKind GetGvk(Type type)
    {
        var gvk = TryGetGvk(type);
        if (gvk is null)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' is not annotated with [KubernetesEntity].");
        }
        return gvk.Value;
    }

    [SuppressMessage("Reliability", "CA1031:Do not catch general exception types",
        Justification = "Reflection failures are downgraded to a null lookup; callers fall back to RFC 7396.")]
    private static GroupVersionKind? ResolveUncached(Type type)
    {
        try
        {
            var attr = (KubernetesEntityAttribute?)Attribute.GetCustomAttribute(
                type, typeof(KubernetesEntityAttribute));
            if (attr is null)
            {
                return null;
            }
            return new GroupVersionKind(attr.Group ?? string.Empty, attr.ApiVersion, attr.Kind);
        }
        catch
        {
            return null;
        }
    }
}
