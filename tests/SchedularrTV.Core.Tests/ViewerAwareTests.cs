using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Stats;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class ViewerAwareTests
{
    private static readonly DateTimeOffset Day = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedPreference(Dictionary<string, double> factors) : IViewerPreference
    {
        public IReadOnlyDictionary<string, double>? TitleFactors(ChannelConfig channel) => factors;
    }

    private static readonly Dictionary<string, double> Favourite = new(StringComparer.OrdinalIgnoreCase) { ["Loved"] = 2.5, ["Skipped"] = 0.4 };

    // --- The planner ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_blocks_favourite_comes_round_more_often_and_a_skipped_show_less()
    {
        (int loved, int skipped) even = Count(await BuildBlockAsync(enabled: true, preference: null));
        (int loved, int skipped) aware = Count(await BuildBlockAsync(enabled: true, preference: new FixedPreference(Favourite)));

        Assert.True(Math.Abs(even.loved - even.skipped) <= 1);
        Assert.True(aware.loved > aware.skipped * 3, $"loved {aware.loved}, skipped {aware.skipped}");
        Assert.True(aware.skipped > 0, "a skipped show is rested, never dropped");
    }

    [Fact]
    public async Task Nothing_changes_while_the_channel_has_it_switched_off()
    {
        (int loved, int skipped) off = Count(await BuildBlockAsync(enabled: false, preference: new FixedPreference(Favourite)));

        Assert.True(Math.Abs(off.loved - off.skipped) <= 1);
    }

    [Fact]
    public async Task A_title_rotation_gives_favourites_more_turns()
    {
        ChannelConfig channel = Channel(enabled: true);
        channel.BaseRotation = new ContentSelector { Query = "kind == 'Episode'", Order = PlaybackOrder.Chronological, TitleOrder = TitleOrder.Ordered };
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore()) { ViewerPreference = new FixedPreference(Favourite) };

        ChannelSchedule schedule = await engine.BuildAsync(Config(channel), channel, Day, Day + TimeSpan.FromHours(12));
        (int loved, int skipped) = Count(schedule);

        Assert.True(loved > skipped * 3, $"loved {loved}, skipped {skipped}");
        Assert.True(skipped > 0);
    }

    [Fact]
    public void Weights_scale_every_title_and_never_fall_below_one()
    {
        Assert.Equal(ViewerWeights.Scale, ViewerWeights.Weight(1, 1));
        Assert.Equal(10, ViewerWeights.Weight(1, 2.5));
        Assert.Equal(1, ViewerWeights.Weight(1, 0.01));
        Assert.False(ViewerWeights.Moves(Favourite, ["Someone else"]));
        Assert.True(ViewerWeights.Moves(Favourite, ["skipped"]));
    }

    // --- Working out the factors ---------------------------------------------------------------------------------

    [Fact]
    public void Factors_follow_watched_share_and_stay_within_the_strength()
    {
        (double boost, double rest) = ViewerPreferences.Bounds(ViewerAwareStrength.Balanced);

        Assert.Equal(1, ViewerPreferences.Factor(0.3, 0.3, 600, boost, rest), 3);
        Assert.True(ViewerPreferences.Factor(0.6, 0.3, 600, boost, rest) > 1.5);
        Assert.True(ViewerPreferences.Factor(0.6, 0.3, 30, boost, rest) < ViewerPreferences.Factor(0.6, 0.3, 600, boost, rest),
            "a show seen briefly is trusted less");
        Assert.Equal(rest, ViewerPreferences.Factor(0, 0.3, 100_000, boost, rest), 3);
        Assert.Equal(boost, ViewerPreferences.Factor(1, 0.05, 100_000, boost, rest), 3);
    }

    [Fact]
    public void Nothing_moves_until_every_threshold_is_met()
    {
        long now = 28_000_000;
        long start = now - 10 * 24 * 60;
        List<AiringRow> airings = [];
        List<ViewerMinute> viewers = [];
        for (int day = 0; day < 10; day++)
        {
            long at = start + day * 24 * 60;
            airings.Add(new("ch", at * 60, "program", "Loved", null, $"l{day}", 120, 100, 100, 1));
            airings.Add(new("ch", (at + 120) * 60, "program", "Skipped", null, $"s{day}", 120, 0, 0, 0));
            for (long minute = at; minute < at + 100; minute++)
            {
                viewers.Add(new ViewerMinute(minute, "carson", "Jellyfin"));
            }
        }

        var settings = new ViewerAwareConfig { Enabled = true, MinHistoryDays = 7, MinAverageDailyViewers = 1, MinWatchedHours = 10 };
        ViewerAwareStatus met = ViewerPreferences.Evaluate(settings, airings, viewers, start, now);

        Assert.True(met.Active);
        Assert.True(met.Factors["Loved"] > 1);
        Assert.True(met.Factors["Skipped"] < 1);
        Assert.Equal("Loved", met.Shows[0].Show);

        settings.MinAverageDailyViewers = 20;
        ViewerAwareStatus waiting = ViewerPreferences.Evaluate(settings, airings, viewers, start, now);

        Assert.False(waiting.Active);
        Assert.Empty(waiting.Factors);
        Assert.False(waiting.Checks.Single(check => check.Label == "Average daily viewers").Met);
        Assert.Equal(2, waiting.Shows.Count);
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private static async Task<ChannelSchedule> BuildBlockAsync(bool enabled, IViewerPreference? preference)
    {
        ChannelConfig channel = Channel(enabled);
        channel.Blocks =
        [
            new BlockConfig
            {
                Name = "Day",
                Time = TimeSpan.Zero,
                EndTime = TimeSpan.FromHours(12),
                Shows =
                [
                    new ContentSelector { Include = [new ContentInclude { Show = "Loved" }], Order = PlaybackOrder.Chronological },
                    new ContentSelector { Include = [new ContentInclude { Show = "Skipped" }], Order = PlaybackOrder.Chronological }
                ]
            }
        ];

        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore()) { ViewerPreference = preference };
        return await engine.BuildAsync(Config(channel), channel, Day, Day + TimeSpan.FromHours(12));
    }

    private static ChannelConfig Channel(bool enabled) => new()
    {
        Id = "ch",
        Name = "Channel",
        Number = 1,
        TimeZone = "UTC",
        ViewerAware = new ViewerAwareConfig { Enabled = enabled }
    };

    private static SchedulerConfig Config(ChannelConfig channel) => new()
    {
        Settings = new SchedulerSettings { TimeZone = "UTC" },
        Channels = [channel]
    };

    private static List<MediaItem> Library() =>
    [
        .. Enumerable.Range(1, 40).Select(episode => TestMedia.Episode("Loved", 1, episode, TimeSpan.FromMinutes(22))),
        .. Enumerable.Range(1, 40).Select(episode => TestMedia.Episode("Skipped", 1, episode, TimeSpan.FromMinutes(22)))
    ];

    private static (int Loved, int Skipped) Count(ChannelSchedule schedule)
    {
        List<string?> shows = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program).Select(entry => entry.Item?.SeriesName)];
        return (shows.Count(show => show == "Loved"), shows.Count(show => show == "Skipped"));
    }
}
