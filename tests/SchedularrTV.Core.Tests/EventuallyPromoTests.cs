using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>
/// Trailers for a show airing later in the week, filed under the day and time it airs and mixed in
/// among the ads rather than given a position of their own.
/// </summary>
public class EventuallyPromoTests
{
    private static readonly DateTimeOffset Monday = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

    private static List<MediaItem> Library()
    {
        List<MediaItem> items = [];

        for (int e = 1; e <= 20; e++)
        {
            items.Add(TestMedia.Episode("Weekday Filler", 1, e, TimeSpan.FromMinutes(22), collection: "Weekdays"));
            items.Add(TestMedia.Episode("Super Chunk", 1, e, TimeSpan.FromMinutes(22), collection: "Matinee"));
        }

        foreach (int seconds in (int[])[10, 15, 20, 30, 45, 60])
        {
            for (int i = 0; i < 6; i++)
            {
                items.Add(TestMedia.Filler($"ad-{seconds}-{i}", TimeSpan.FromSeconds(seconds), "Commercials", "Cartoon Network", "Any Time"));
            }
        }

        return items;
    }

    /// <summary>The channel runs a Saturday block at 4pm that Super Chunk is the only candidate for.</summary>
    private static ChannelConfig Channel(double chance = 1.0) => new()
    {
        Id = "cn",
        Name = "Cartoon Network",
        Number = 1,
        TimeZone = "UTC",
        CommercialBaseTags = ["Cartoon Network"],
        AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = new ContentSelector { Query = "collection:'Weekdays'", Order = PlaybackOrder.Chronological },
        Promos = new PromoConfig
        {
            BaseQuery = "tag:'Promo'",
            Eventually = new EventuallyConfig { Chance = chance }
        },
        Blocks =
        [
            new BlockConfig
            {
                Name = "Saturday Matinee",
                Time = TimeSpan.FromHours(16),
                EndTime = TimeSpan.FromHours(18),
                Days = [DayOfWeek.Saturday],
                Shows = [new ContentSelector { Query = "collection:'Matinee'", Order = PlaybackOrder.Chronological }]
            }
        ]
    };

    private static SchedulerConfig Config(ChannelConfig channel) => new()
    {
        Settings = new SchedulerSettings
        {
            TimeZone = "UTC",
            Interstitials = new InterstitialsConfig
            {
                Commercials = new CommercialsConfig
                {
                    BaseQuery = "tag:\"Commercials\"",
                    Tolerance = TimeSpan.FromMilliseconds(500),
                    Granularity = TimeSpan.FromMilliseconds(250),
                    MaxRepeatsPerBreak = 4
                }
            }
        },
        Channels = [channel]
    };

    private static async Task<ChannelSchedule> BuildAsync(ChannelConfig channel, params MediaItem[] promos)
    {
        List<MediaItem> items = [.. Library(), .. promos];
        var engine = new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore());

        return await engine.BuildAsync(Config(channel), channel, Monday, Monday + TimeSpan.FromHours(6));
    }

    private static List<ScheduleEntry> Trailers(ChannelSchedule schedule) =>
        [.. schedule.Entries.Where(e => e.Kind == ScheduleEntryKind.EventuallyPromo)];

    [Fact]
    public async Task A_trailer_plays_when_the_show_really_airs_on_that_day_at_that_time()
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "400pm", "Cartoon Network"));

        Assert.NotEmpty(Trailers(schedule));
        Assert.All(Trailers(schedule), e => Assert.Equal("eventually-superchunk", e.Item!.Id));
    }

    [Fact]
    public async Task A_trailer_claiming_a_slot_the_channel_does_not_run_never_plays()
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            // The Saturday block opens at 4pm, not 9am, so this promo is advertising nothing.
            TestMedia.PromoOn("eventually-wrong-time", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "900am", "Cartoon Network"),
            TestMedia.PromoOn("eventually-wrong-day", TimeSpan.FromSeconds(15), "Super Chunk", "tuesday", "400pm", "Cartoon Network"),
            TestMedia.PromoOn("eventually-wrong-show", TimeSpan.FromSeconds(15), "Nothing Here", "saturday", "400pm", "Cartoon Network"));

        Assert.Empty(Trailers(schedule));
    }

    [Fact]
    public async Task A_trailer_with_no_day_or_time_plays_for_a_show_in_the_base_rotation()
    {
        // Unanchored rotation time cannot promise an hour, so an undated trailer is all it can carry.
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            TestMedia.PromoOn("eventually-filler", TimeSpan.FromSeconds(15), "Weekday Filler", day: null, clock: null, "Cartoon Network"));

        Assert.NotEmpty(Trailers(schedule));
    }

    [Fact]
    public async Task Odds_of_zero_keep_trailers_off_the_channel_entirely()
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(chance: 0),
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "400pm", "Cartoon Network"));

        Assert.Empty(Trailers(schedule));
    }

    [Fact]
    public async Task A_trailer_lands_among_the_ads_rather_than_at_the_edge_of_the_break()
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "400pm", "Cartoon Network"));

        List<ScheduleEntry> entries = [.. schedule.Entries];
        List<int> positions = [.. Trailers(schedule).Select(e => entries.IndexOf(e))];

        Assert.NotEmpty(positions);

        // At least one has an advert on both sides; a bookend would only ever have one.
        Assert.Contains(positions, i =>
            entries[i - 1].Kind == ScheduleEntryKind.Commercial
            && entries[i + 1].Kind == ScheduleEntryKind.Commercial);
    }

    [Fact]
    public async Task Mixing_a_trailer_in_leaves_the_timeline_gap_free()
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "400pm", "Cartoon Network"));

        Assert.NotEmpty(Trailers(schedule));

        DateTimeOffset cursor = schedule.Start;
        foreach (ScheduleEntry entry in schedule.Entries)
        {
            Assert.Equal(cursor, entry.Start);
            cursor = entry.End;
        }
    }

    [Fact]
    public async Task A_trailer_takes_break_time_rather_than_pushing_the_next_programme_late()
    {
        ChannelConfig channel = Channel();

        ChannelSchedule without = await BuildAsync(channel);
        ChannelSchedule with = await BuildAsync(
            channel,
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "400pm", "Cartoon Network"));

        Assert.NotEmpty(Trailers(with));

        Assert.Equal(
            without.Entries.Where(e => e.Kind == ScheduleEntryKind.Program).Select(e => e.Start),
            with.Entries.Where(e => e.Kind == ScheduleEntryKind.Program).Select(e => e.Start));
    }

    [Fact]
    public async Task The_trailer_records_the_show_it_advertises()
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", "400pm", "Cartoon Network"));

        Assert.All(Trailers(schedule), e => Assert.Equal("Super Chunk", e.PromoTargetId));
    }

    [Theory]
    [InlineData("4pm")]
    [InlineData("400pm")]
    public async Task Either_spelling_of_the_hour_matches_the_slot(string clock)
    {
        ChannelSchedule schedule = await BuildAsync(
            Channel(),
            TestMedia.PromoOn("eventually-superchunk", TimeSpan.FromSeconds(15), "Super Chunk", "saturday", clock, "Cartoon Network"));

        Assert.NotEmpty(Trailers(schedule));
    }
}
