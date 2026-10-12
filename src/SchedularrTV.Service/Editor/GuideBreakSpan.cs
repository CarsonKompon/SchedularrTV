using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

/// <summary>Continuous break content, drawn over a programme slot at close zoom.</summary>
public sealed record GuideBreakSpan(DateTimeOffset Start, DateTimeOffset End, int Clips)
{
    public static IReadOnlyList<GuideBreakSpan> FromSchedule(ChannelSchedule schedule)
    {
        List<GuideBreakSpan> spans = [];
        foreach (ScheduleEntry entry in schedule.Entries.OrderBy(entry => entry.Start))
        {
            if (entry.Kind is ScheduleEntryKind.Program or ScheduleEntryKind.Interstitial || entry.Duration <= TimeSpan.Zero) continue;
            if (spans.Count > 0 && spans[^1].End == entry.Start)
                spans[^1] = spans[^1] with { End = entry.End, Clips = spans[^1].Clips + 1 };
            else
                spans.Add(new(entry.Start, entry.End, 1));
        }
        return spans;
    }
}
