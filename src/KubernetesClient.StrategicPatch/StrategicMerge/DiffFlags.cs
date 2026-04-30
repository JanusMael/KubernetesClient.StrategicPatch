namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Internal-only flags that control how <see cref="TwoWayMerge"/> walks a pair of objects.
/// Three-way merge composes its result from two specialised diffs that each set one of these.
/// Mirrors Go's <c>strategicpatch.DiffOptions</c>.
/// </summary>
internal readonly record struct DiffFlags(
    bool IgnoreDeletions = false,
    bool IgnoreChangesAndAdditions = false)
{
    public static DiffFlags Default => default;
    public static DiffFlags DeltaOnly => new(IgnoreDeletions: true);
    public static DiffFlags DeletionsOnly => new(IgnoreChangesAndAdditions: true);
}
