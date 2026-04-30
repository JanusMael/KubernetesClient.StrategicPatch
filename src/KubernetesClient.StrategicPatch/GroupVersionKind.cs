namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Identifies a Kubernetes resource by its API group, version, and kind.
/// </summary>
/// <param name="Group">API group; empty string for the core group ("v1").</param>
/// <param name="Version">API version, e.g. "v1", "v1beta1".</param>
/// <param name="Kind">Resource kind, e.g. "Pod", "Deployment".</param>
public readonly record struct GroupVersionKind(string Group, string Version, string Kind)
{
    /// <summary>
    /// Parses an "apiVersion" string of the form "group/version" or "version" (core group).
    /// </summary>
    public static GroupVersionKind Parse(string apiVersion, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiVersion);
        ArgumentException.ThrowIfNullOrEmpty(kind);

        var slash = apiVersion.IndexOf('/');
        return slash < 0
            ? new GroupVersionKind(string.Empty, apiVersion, kind)
            : new GroupVersionKind(apiVersion[..slash], apiVersion[(slash + 1)..], kind);
    }

    /// <summary>
    /// Returns the apiVersion form: "group/version" or "version" for the core group.
    /// </summary>
    public string ApiVersion => Group.Length == 0 ? Version : $"{Group}/{Version}";

    public override string ToString() => $"{ApiVersion}/{Kind}";
}
