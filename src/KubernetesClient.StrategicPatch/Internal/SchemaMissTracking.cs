using System.Diagnostics;

namespace KubernetesClient.StrategicPatch.Internal;

/// <summary>
/// Records schema-miss events on the active <see cref="Activity"/> and increments the
/// <c>smp.schema_miss_count</c> tag in lockstep so the count is observable at activity-stop
/// time without listeners having to enumerate events. Mirroring this on the activity (rather
/// than a thread-local) keeps the API of <see cref="StrategicMerge.TwoWayMerge"/> and friends
/// non-invasive — the same single nullable Activity reference is already plumbed everywhere.
/// </summary>
internal static class SchemaMissTracking
{
    public const string CountTag = "smp.schema_miss_count";

    /// <summary>
    /// Adds an <c>smp.schema_miss</c> event with the offending path, and bumps the count tag.
    /// </summary>
    public static void RecordMiss(Activity? activity, JsonPointer path)
    {
        if (activity is null)
        {
            return;
        }
        activity.AddEvent(new ActivityEvent("smp.schema_miss",
            tags: new ActivityTagsCollection { ["smp.path"] = path.ToString() }));
        var current = activity.GetTagItem(CountTag) as int? ?? 0;
        activity.SetTag(CountTag, current + 1);
    }

    /// <summary>
    /// Initialises the count tag to <c>0</c> so listeners always see it on stopped activities.
    /// </summary>
    public static void Initialise(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }
        activity.SetTag(CountTag, 0);
    }
}
