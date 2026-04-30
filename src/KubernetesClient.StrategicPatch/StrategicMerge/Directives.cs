namespace KubernetesClient.StrategicPatch.StrategicMerge;

/// <summary>
/// Strategic merge patch directive markers. Wire-compatible with the Go reference
/// (<c>k8s.io/apimachinery/pkg/util/strategicpatch</c>). Names are constants because
/// they appear inside generated JSON keys and values; varying them would break
/// server-side merge interpretation.
/// </summary>
internal static class Directives
{
    /// <summary>Object-element directive marker key. Values are <see cref="Delete"/>, <see cref="Replace"/>, <see cref="Merge"/>.</summary>
    public const string Marker = "$patch";

    /// <summary>"$deleteFromPrimitiveList" — prefix for parallel delete lists on scalar/set merging lists.</summary>
    public const string DeleteFromPrimitiveListPrefix = "$deleteFromPrimitiveList";

    /// <summary>"$setElementOrder" — prefix for the parallel ordering list on merging lists.</summary>
    public const string SetElementOrderPrefix = "$setElementOrder";

    /// <summary>"$retainKeys" — directive listing the surviving keys for a retainKeys-strategy map.</summary>
    public const string RetainKeys = "$retainKeys";

    /// <summary>Marker payload values for <see cref="Marker"/>.</summary>
    public const string Delete = "delete";
    public const string Replace = "replace";
    public const string Merge = "merge";

    /// <summary>Builds "$deleteFromPrimitiveList/&lt;field&gt;".</summary>
    public static string DeleteFromPrimitiveListKey(string field) => $"{DeleteFromPrimitiveListPrefix}/{field}";

    /// <summary>Builds "$setElementOrder/&lt;field&gt;".</summary>
    public static string SetElementOrderKey(string field) => $"{SetElementOrderPrefix}/{field}";
}
