namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Thrown by three-way merge when a field the caller wants to update has also been mutated by the
/// server since the last apply, and the two changes disagree. Pass
/// <see cref="StrategicPatchOptions.OverwriteConflicts"/> = <c>true</c> to suppress this and let the
/// caller-side change win unconditionally (mirrors Go's <c>overwrite</c> parameter).
/// </summary>
public sealed class StrategicMergePatchConflictException : StrategicMergePatchException
{
    public StrategicMergePatchConflictException(
        string message,
        IReadOnlyList<JsonPointer> conflicts) : base(message)
    {
        Conflicts = conflicts;
    }

    /// <summary>
    /// Paths within the resource where caller-side and server-side changes disagree.
    /// </summary>
    public IReadOnlyList<JsonPointer> Conflicts { get; }
}
