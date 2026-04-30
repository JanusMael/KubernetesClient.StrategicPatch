namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Thrown when strategic merge patch generation or application fails for a documented reason
/// (e.g. mismatched <c>apiVersion</c>/<c>kind</c>, malformed input, schema-required field missing).
/// </summary>
public class StrategicMergePatchException : Exception
{
    public StrategicMergePatchException(string message) : base(message) { }
    public StrategicMergePatchException(string message, Exception innerException) : base(message, innerException) { }

    public StrategicMergePatchException(string message, JsonPointer path) : base(message)
    {
        Path = path;
    }

    /// <summary>The JSON pointer at which the failure was detected, or root if global.</summary>
    public JsonPointer Path { get; }

    /// <summary>API group of the document being patched, when known.</summary>
    public string? Group { get; init; }

    /// <summary>API version of the document being patched, when known.</summary>
    public string? Version { get; init; }

    /// <summary>Resource kind being patched, when known.</summary>
    public string? Kind { get; init; }
}
