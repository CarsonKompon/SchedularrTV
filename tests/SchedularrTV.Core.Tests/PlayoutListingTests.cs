using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class PlayoutListingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 12:00 a show in three parts with two mid-roll breaks, a break between programmes, then 12:30 a whole show.
    /// </summary>
    private static List<ScheduleEntry> HalfHour()
    {
        MediaItem first = TestMedia.Episode("Top Cat", 1, 2, TimeSpan.FromMinutes(22), title: "The Maharajah of Pookajee");
        MediaItem second = TestMedia.Episode("Jonny Quest", 1, 1, TimeSpan.FromMinutes(25), title: "Mystery of the Lizard Men");

        List<ScheduleEntry> entries = [];
        DateTimeOffset at = Noon;

        void Add(ScheduleEntryKind kind, MediaItem item, TimeSpan length, int part = 0, int parts = 0)
        {
            entries.Add(new ScheduleEntry { Start = at, Duration = length, Kind = kind, Item = item, SegmentNumber = part, SegmentCount = parts });
            at += length;
        }

        MediaItem ad = TestMedia.Filler("ad", TimeSpan.FromSeconds(30), "Commercials");
        MediaItem upNext = TestMedia.Filler("up next", TimeSpan.FromSeconds(10), "Promos");

        Add(ScheduleEntryKind.Program, first, TimeSpan.FromMinutes(8), 1, 3);
        Add(ScheduleEntryKind.Commercial, ad, TimeSpan.FromSeconds(30));
        Add(ScheduleEntryKind.Commercial, ad, TimeSpan.FromSeconds(30));
        Add(ScheduleEntryKind.Program, first, TimeSpan.FromMinutes(7), 2, 3);
        Add(ScheduleEntryKind.Commercial, ad, TimeSpan.FromSeconds(30));
        Add(ScheduleEntryKind.Program, first, TimeSpan.FromMinutes(7), 3, 3);
        Add(ScheduleEntryKind.Commercial, ad, TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(20));
        Add(ScheduleEntryKind.UpNextPromo, upNext, TimeSpan.FromSeconds(10));
        Add(ScheduleEntryKind.Program, second, TimeSpan.FromMinutes(25));

        return entries;
    }

    [Fact]
    public void Without_breaks_each_airing_is_one_row_with_its_parts_and_mid_roll_time()
    {
        IReadOnlyList<PlayoutListingRow> rows = PlayoutListing.Build(HalfHour(), Noon, Noon.AddHours(2), showBreaks: false, TimeZoneInfo.Utc);

        Assert.IsType<PlayoutListingDay>(rows[0]);
        PlayoutListingProgramme[] programmes = [.. rows.OfType<PlayoutListingProgramme>()];
        Assert.Equal(2, programmes.Length);
        Assert.Empty(rows.OfType<PlayoutListingBreak>());

        PlayoutListingProgramme split = programmes[0];
        Assert.Equal(Noon, split.Start);
        Assert.Equal(3, split.Parts);
        Assert.True(split.IsSplit);
        Assert.Equal(2, split.Breaks);
        Assert.Equal(TimeSpan.FromSeconds(90), split.BreakTime);
        Assert.Equal(TimeSpan.FromMinutes(22), split.Runtime);

        // The break after it is between programmes, so it isn't counted against either.
        PlayoutListingProgramme whole = programmes[1];
        Assert.Equal(Noon.AddMinutes(30), whole.Start);
        Assert.False(whole.IsSplit);
        Assert.Equal(TimeSpan.Zero, whole.BreakTime);
    }

    [Fact]
    public void With_breaks_each_part_is_listed_and_the_clips_between_are_grouped()
    {
        IReadOnlyList<PlayoutListingRow> rows = PlayoutListing.Build(HalfHour(), Noon, Noon.AddHours(2), showBreaks: true, TimeZoneInfo.Utc);

        PlayoutListingProgramme[] parts = [.. rows.OfType<PlayoutListingProgramme>()];
        Assert.Equal(new[] { 1, 2, 3, 1 }, parts.Select(p => p.Part).ToArray());

        PlayoutListingBreak[] breaks = [.. rows.OfType<PlayoutListingBreak>()];
        Assert.Equal(3, breaks.Length);
        Assert.True(breaks[0].MidRoll);
        Assert.Equal(2, breaks[0].Clips.Count);
        Assert.True(breaks[1].MidRoll);
        Assert.False(breaks[2].MidRoll);
        Assert.Equal(ScheduleEntryKind.UpNextPromo, breaks[2].Clips[^1].Kind);
        Assert.Equal(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(30), breaks[2].Duration);
    }

    [Fact]
    public void Only_what_is_still_to_air_in_the_window_is_listed()
    {
        IReadOnlyList<PlayoutListingRow> rows = PlayoutListing.Build(HalfHour(), Noon.AddMinutes(29), Noon.AddMinutes(40), showBreaks: false, TimeZoneInfo.Utc);

        // The split show ended before the window; the break running at 12:29 isn't a programme.
        PlayoutListingProgramme only = Assert.Single(rows.OfType<PlayoutListingProgramme>());
        Assert.Equal("Jonny Quest", PlayoutListing.Describe(only.Entry).Title);
    }

    [Fact]
    public void A_new_day_starts_a_new_heading()
    {
        MediaItem late = TestMedia.Episode("Late Show", 1, 1, TimeSpan.FromHours(1));
        DateTimeOffset evening = new(2026, 10, 12, 23, 30, 0, TimeSpan.Zero);
        ScheduleEntry[] entries =
        [
            new() { Start = evening, Duration = TimeSpan.FromMinutes(30), Kind = ScheduleEntryKind.Program, Item = late },
            new() { Start = evening.AddMinutes(30), Duration = TimeSpan.FromMinutes(30), Kind = ScheduleEntryKind.Program, Item = late }
        ];

        PlayoutListingDay[] days = [.. PlayoutListing.Build(entries, evening, evening.AddHours(2), false, TimeZoneInfo.Utc).OfType<PlayoutListingDay>()];

        Assert.Equal(new[] { new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 13) }, days.Select(d => d.Date).ToArray());
    }

    [Fact]
    public void An_episode_is_named_by_its_show_with_the_episode_beneath()
    {
        var entry = new ScheduleEntry
        {
            Start = Noon,
            Duration = TimeSpan.FromMinutes(22),
            Kind = ScheduleEntryKind.Program,
            Item = TestMedia.Episode("Top Cat", 1, 2, TimeSpan.FromMinutes(22), title: "The Maharajah of Pookajee")
        };

        Assert.Equal(("Top Cat", "S01E02 · The Maharajah of Pookajee"), PlayoutListing.Describe(entry));
    }
}
