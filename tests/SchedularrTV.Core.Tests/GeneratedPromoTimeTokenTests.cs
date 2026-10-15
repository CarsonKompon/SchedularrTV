using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoTimeTokenTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Both_affixes_expand_the_selected_range_in_channel_time()
    {
        var template = GeneratedPromoTemplates.UpNext();
        template.Layers[3].UpNext = new GeneratedUpNextOptions
        {
            Offset = 1, ShowsAhead = 2,
            Prefix = "{START_TIME}–{end_time}\n",
            Suffix = " on {channel} ({start_time}–{END_TIME})"
        };
        UpNextListing[] listings =
        [
            new("Skipped", Noon),
            new("First", Noon.AddHours(1)),
            new("Last", Noon.AddHours(2), Runtime: TimeSpan.FromMinutes(20)) { End = Noon.AddHours(2.5) }
        ];
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", listings, zone, Noon)!;
        Assert.Equal(new[] { "4:00–5:30", "First", "Last on Nick (4:00–5:30)" }, promo.LayerLines[3]);
    }

    [Theory]
    [InlineData(30, "12:30")]
    [InlineData(0, "")]
    public void End_uses_runtime_when_available_and_is_otherwise_blank(int minutes, string expected)
    {
        var template = GeneratedPromoTemplates.UpNext();
        template.Layers[3].UpNext = new GeneratedUpNextOptions { Prefix = "{start_time}: ", Suffix = " until {end_time}" };
        UpNextListing[] listings = [new("Show", Noon, Runtime: minutes > 0 ? TimeSpan.FromMinutes(minutes) : null)];
        var promo = GeneratedPromoResolver.Resolve(template, "Nick", listings, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal(new[] { $"12:00: Show until {expected}" }, promo.LayerLines[3]);
        template.Layers[3].UpNext!.Offset = 1;
        Assert.Empty(GeneratedPromoResolver.Resolve(template, "Nick", listings, TimeZoneInfo.Utc, Noon)!.LayerLines[3]);
    }

    [Fact]
    public void Scheduled_end_includes_split_segments_and_stacked_episodes_but_not_trailing_ads()
    {
        var show = TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(20));
        var schedule = new ChannelSchedule
        {
            ChannelId = "test", ChannelName = "Test", Start = Noon, End = Noon.AddHours(1),
            Entries =
            [
                new() { Start = Noon, Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program, Item = show, SegmentNumber = 1 },
                new() { Start = Noon.AddMinutes(10), Duration = TimeSpan.FromMinutes(5), Kind = ScheduleEntryKind.Commercial },
                new() { Start = Noon.AddMinutes(15), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program, Item = show, SegmentNumber = 2 },
                new() { Start = Noon.AddMinutes(25), Duration = TimeSpan.FromMinutes(20), Kind = ScheduleEntryKind.Program,
                    Item = TestMedia.Episode("Show", 1, 2, TimeSpan.FromMinutes(20)), ContinuesSlot = true },
                new() { Start = Noon.AddMinutes(45), Duration = TimeSpan.FromMinutes(5), Kind = ScheduleEntryKind.Commercial },
                new() { Start = Noon.AddMinutes(50), Duration = TimeSpan.FromMinutes(10), Kind = ScheduleEntryKind.Program,
                    Item = TestMedia.Episode("Other", 1, 1, TimeSpan.FromMinutes(10)) }
            ]
        };
        var listing = Assert.Single(UpNextPromo.Listings(schedule, Noon, 1));
        Assert.Equal(Noon.AddMinutes(45), listing.End);
    }
}
