using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

public sealed record GuideBlockSpan(string? Name, DateTimeOffset Start, DateTimeOffset End)
{
    public static IReadOnlyList<GuideBlockSpan> FromSchedule(ChannelSchedule schedule)
    {
        if (!schedule.ArchiveFollowEnabled && schedule.ArchiveDays.Count == 0) return FromEntries(schedule.Entries);
        var days = ArchiveGuideDay.Clip(schedule.ArchiveDays, schedule.Start, schedule.End);
        var boundaries = days.SelectMany(day => day.Blocks.SelectMany(block => new[] { block.Start, block.End })
                .Concat([day.Start, day.End])).Concat([schedule.Start, schedule.End]).Distinct().Order().ToArray();
        List<GuideBlockSpan> spans = [];
        for (int index = 0; index < boundaries.Length - 1; index++)
        {
            DateTimeOffset start = boundaries[index];
            DateTimeOffset end = boundaries[index + 1];
            var day = days.FirstOrDefault(day => day.Start <= start && day.End > start);
            var block = day?.Blocks.FirstOrDefault(block => block.Start <= start && block.End > start);
            string[] labels = block?.Labels.Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            string? name = day is null ? null : labels.Length == 0 ? "No archive block" : string.Join(" / ", labels);
            Append(spans, new(name, start, end));
        }
        return spans;
    }

    public static IReadOnlyList<GuideBlockSpan> ArchiveDates(ChannelSchedule schedule)
    {
        List<GuideBlockSpan> spans = [];
        foreach (var day in ArchiveGuideDay.Clip(schedule.ArchiveDays, schedule.Start, schedule.End).OrderBy(day => day.Start))
            Append(spans, new($"Following {day.Date:yyyy-MM-dd}", day.Start, day.End));
        return spans;
    }

    private static void Append(List<GuideBlockSpan> spans, GuideBlockSpan span)
    {
        if (spans.Count > 0 && spans[^1].End == span.Start && spans[^1].Name == span.Name)
            spans[^1] = spans[^1] with { End = span.End };
        else
            spans.Add(span);
    }

    public static IReadOnlyList<GuideBlockSpan> FromEntries(IReadOnlyList<ScheduleEntry> entries)
    {
        List<GuideBlockSpan> spans = [];
        foreach (ScheduleEntry entry in entries.OrderBy(entry => entry.Start))
        {
            if (entry.Duration <= TimeSpan.Zero) continue;
            string? name = string.IsNullOrWhiteSpace(entry.BlockName) ? null : entry.BlockName;
            if (spans.Count > 0 && spans[^1].End == entry.Start && spans[^1].Name == name)
            {
                spans[^1] = spans[^1] with { End = entry.End };
            }
            else
            {
                spans.Add(new(name, entry.Start, entry.End));
            }
        }
        return spans;
    }
}