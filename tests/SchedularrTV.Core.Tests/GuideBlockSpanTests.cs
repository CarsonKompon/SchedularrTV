using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GuideBlockSpanTests
{
    [Fact]
    public void Follow_guide_uses_archive_labels_and_date_not_the_configured_block()
    {
        DateTimeOffset start = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var schedule = new ChannelSchedule
        {
            ChannelId = "follow", ChannelName = "Follow", Start = start, End = start.AddHours(3), ArchiveFollowEnabled = true,
            Entries = [new() { Start = start, Duration = TimeSpan.FromHours(3), Kind = ScheduleEntryKind.Program, BlockName = "Usual block" }],
            ArchiveDays = [new(start, start.AddHours(2), new(2002, 9, 15),
                [new(start, start.AddMinutes(30), ["Toonami '00", "Special"]),
                 new(start.AddMinutes(30), start.AddHours(1), ["Toonami '00", "Special"]),
                 new(start.AddHours(1), start.AddHours(2), [])])]
        };
        var blocks = GuideBlockSpan.FromSchedule(schedule);
        Assert.Equal(3, blocks.Count);
        Assert.Equal("Toonami '00 / Special", blocks[0].Name);
        Assert.Equal(start.AddHours(1), blocks[0].End);
        Assert.Equal("No archive block", blocks[1].Name);
        Assert.Null(blocks[2].Name);
        Assert.DoesNotContain(blocks, block => block.Name == "Usual block");
        Assert.Equal(new GuideBlockSpan("Following 2002-09-15", start, start.AddHours(2)), Assert.Single(GuideBlockSpan.ArchiveDates(schedule)));
        Assert.Equal("Usual block", Assert.Single(GuideBlockSpan.FromSchedule(schedule with { ArchiveFollowEnabled = false, ArchiveDays = [] })).Name);
    }

    [Fact]
    public void Carried_archive_ranges_are_clipped_and_date_changes_stay_separate()
    {
        DateTimeOffset start = new(2026, 9, 15, 23, 0, 0, TimeSpan.Zero);
        var day = new ArchiveGuideDay(start, start.AddHours(1), new(2002, 9, 15), [new(start, start.AddHours(1), ["Night"])]);
        var carried = ArchiveGuideDay.Clip([day], start.AddMinutes(30), start.AddMinutes(45));
        Assert.Equal(start.AddMinutes(30), carried[0].Blocks[0].Start);
        Assert.Equal(start.AddMinutes(45), carried[0].Blocks[0].End);
        var schedule = new ChannelSchedule
        {
            ChannelId = "follow", ChannelName = "Follow", Start = start.AddMinutes(30), End = start.AddHours(2), Entries = [],
            ArchiveDays = [.. carried, day with { Start = start.AddMinutes(45) },
                new(start.AddHours(1), start.AddHours(2), new(2004, 9, 16), [])]
        };
        var dates = GuideBlockSpan.ArchiveDates(schedule);
        Assert.Equal(2, dates.Count);
        Assert.Equal(start.AddHours(1), dates[0].End);
        Assert.Equal("Following 2004-09-16", dates[1].Name);
    }

    [Fact]
    public void Block_bands_include_breaks_but_stop_at_normal_time_gaps_and_other_blocks()
    {
        DateTimeOffset start = new(2026, 9, 15, 23, 0, 0, TimeSpan.Zero);
        ScheduleEntry Entry(int minute, int duration, string? block, ScheduleEntryKind kind = ScheduleEntryKind.Program) =>
            new() { Start = start.AddMinutes(minute), Duration = TimeSpan.FromMinutes(duration), BlockName = block, Kind = kind };
        var spans = GuideBlockSpan.FromEntries([
            Entry(90, 30, "Night"), Entry(0, 20, "Evening"), Entry(20, 10, "Evening", ScheduleEntryKind.Commercial),
            Entry(30, 30, null), Entry(60, 10, "Night"), Entry(70, 10, "Night", ScheduleEntryKind.Padding), Entry(120, 30, "Evening")]);
        Assert.Equal(5, spans.Count);
        Assert.Equal(new GuideBlockSpan("Evening", start, start.AddMinutes(30)), spans[0]);
        Assert.Null(spans[1].Name);
        Assert.Equal(start.AddMinutes(80), spans[2].End);
        Assert.Equal(start.AddMinutes(90), spans[3].Start);
        Assert.Equal("Evening", spans[4].Name);
    }
}