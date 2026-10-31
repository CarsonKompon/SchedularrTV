using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>
/// Ads should go all the way round their pool before any comes back: across midnight, across rebuilds, when the pool
/// changes, and between blocks whose ad breaks share folders.
/// </summary>
public sealed class CommercialVarietyTests
{
    private const string Shared = @"D:\Media\Commercials\Any Channel\Ads";

    private static MediaItem Ad(string folder, int index) => new()
    {
        Id = $@"{folder}\ad-{index:000}.mp4",
        Title = $"ad-{index:000}",
        Kind = MediaKind.Filler,
        Duration = TimeSpan.FromSeconds(30),
        Path = $@"{folder}\ad-{index:000}.mp4"
    };

    private static MediaItem Item(string id, double seconds = 30) => new()
    {
        Id = id,
        Title = id,
        Kind = MediaKind.Filler,
        Duration = TimeSpan.FromSeconds(seconds)
    };

    // ---- The shuffle itself ----

    [Fact]
    public void A_shuffle_carries_on_its_cycle_when_its_pool_changes()
    {
        MediaItem[] before = [.. Enumerable.Range(0, 10).Select(index => Item($"a{index}"))];
        MediaSequence sequence = MediaSequence.Create("pool", PlaybackOrder.Shuffle, before, null);
        List<string> played = [];
        for (int take = 0; take < 5; take++)
        {
            Assert.True(sequence.TryTake(null, out MediaItem item));
            played.Add(item.Id);
        }

        // One clip leaves the folder and two arrive.
        MediaItem[] after = [.. before.Where(item => item.Id != "a9"), Item("n0"), Item("n1")];
        MediaSequence resumed = MediaSequence.Create("pool", PlaybackOrder.Shuffle, after, sequence.ToState());

        HashSet<string> expected = [.. after.Select(item => item.Id).Except(played)];
        HashSet<string> rest = [];
        for (int take = 0; take < expected.Count; take++)
        {
            Assert.True(resumed.TryTake(null, out MediaItem item));
            rest.Add(item.Id);
        }

        // What's left of the cycle, plus the newcomers: nothing that already played, nothing twice.
        Assert.Equal(expected, rest);
    }

    [Fact]
    public void Recycling_a_played_item_to_fill_a_gap_does_not_put_it_back_in_the_cycle()
    {
        MediaItem[] pool = [Item("short", 5), Item("long-1", 60), Item("long-2", 60), Item("long-3", 60)];
        MediaSequence sequence = MediaSequence.Create("gap", PlaybackOrder.Shuffle, pool, null);
        Assert.True(sequence.TryTake(item => item.Duration.TotalSeconds <= 10, out MediaItem first));
        Assert.Equal("short", first.Id);

        Assert.Equal("short", Assert.Single(sequence.PreviewForGap(10, item => item.Duration.TotalSeconds <= 10)).Id);
        sequence.Consume("short");

        PlaybackState state = sequence.ToState();
        Assert.DoesNotContain("short", state.Bag);
        Assert.Equal(new[] { "short" }, state.Played);
    }

    [Fact]
    public void A_random_pool_previews_in_a_random_order_that_moves_on_once_something_is_taken()
    {
        MediaItem[] pool = [.. Enumerable.Range(0, 100).Select(index => Item($"ad-{index:000}"))];
        MediaSequence sequence = MediaSequence.Create("random", PlaybackOrder.Random, pool, null);

        string[] preview = [.. sequence.PreviewForGap(10, _ => true).Select(item => item.Id)];
        Assert.NotEqual(pool.Take(10).Select(item => item.Id), preview);
        Assert.Equal(preview, sequence.PreviewForGap(10, _ => true).Select(item => item.Id));

        sequence.Consume(preview[0]);
        Assert.NotEqual(preview, sequence.PreviewForGap(10, _ => true).Select(item => item.Id));
    }

    [Fact]
    public void A_seasonal_pool_keeps_one_identity_from_day_to_day()
    {
        SchedulerConfig config = new();
        config.AdBreaks["Ads"] = new AdBreakConfig { Seasonal = true, Folders = [new AdBreakFolder { Path = Shared, Role = AdFolderRole.Ads }] };
        ChannelConfig channel = new() { Id = "ch", Name = "Channel", AdBreaks = ["Ads"] };
        CommercialsConfig commercials = SchedulingEngine.ResolveInterstitials(config, channel, null).Commercials!;
        TemporalTagOptions options = new();

        DateTimeOffset monday = new(2026, 7, 13, 23, 0, 0, TimeSpan.Zero);
        Assert.NotEqual(
            InterstitialQueryBuilder.BuildCommercials(commercials, monday, options).ToQueryString(),
            InterstitialQueryBuilder.BuildCommercials(commercials, monday.AddDays(1), options).ToQueryString());
        Assert.Equal(InterstitialQueryBuilder.CommercialsIdentity(commercials), InterstitialQueryBuilder.CommercialsIdentity(commercials));
    }

    // ---- Whole builds ----

    private static (SchedulerConfig Config, ChannelConfig Channel) Setup(params (string Name, string Folder)[] adBreaks)
    {
        SchedulerConfig config = new();
        config.Settings.TimeZone = "UTC";
        config.Settings.AdBreaksMigrated = true;
        foreach ((string name, string folder) in adBreaks)
        {
            config.AdBreaks[name] = new AdBreakConfig { Seasonal = true, Folders = [new AdBreakFolder { Path = folder, Role = AdFolderRole.Ads }] };
        }

        // A one-minute episode on a five-minute grid: every break is four minutes, eight ads.
        ChannelConfig channel = new()
        {
            Id = "ch",
            Name = "Channel",
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(5),
            BaseRotation = new ContentSelector { Query = "kind:'Episode'" },
            AdBreaks = [adBreaks[0].Name]
        };
        return (config, channel);
    }

    private static List<ScheduleEntry> Ads(ChannelSchedule schedule) =>
        [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Commercial).OrderBy(entry => entry.Start)];

    [Fact]
    public async Task Ads_go_round_the_whole_pool_across_midnight_and_rebuilds_before_any_comes_back()
    {
        (SchedulerConfig config, ChannelConfig channel) = Setup(("Ads", Shared));
        MediaItem[] ads = [.. Enumerable.Range(0, 100).Select(index => Ad(Shared, index))];
        var library = new FakeMediaLibrary([.. ads, TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(1))]);
        var state = new InMemoryPlaybackStateStore();
        var engine = new SchedulingEngine(library, state);

        // The first build runs over midnight (the seasonal query changes with the date); the second tops it up.
        DateTimeOffset start = new(2026, 7, 13, 23, 0, 0, TimeSpan.Zero);
        ChannelSchedule first = await engine.BuildAsync(config, channel, start, start.AddMinutes(90));
        DateTimeOffset resume = first.Entries.Max(entry => entry.End);
        ChannelSchedule second = await engine.BuildAsync(config, channel, resume, resume.AddMinutes(60));

        List<string> played = [.. Ads(first).Concat(Ads(second)).Select(entry => entry.Item!.Id)];
        Assert.True(played.Count >= 200, $"expected at least 200 ads, got {played.Count}");
        Assert.Equal(100, played.Take(100).Distinct().Count());
        Assert.Equal(100, played.Skip(100).Take(100).Distinct().Count());
    }

    [Fact]
    public async Task A_block_passes_over_ads_that_another_block_aired_lately()
    {
        const string Morning = @"D:\Media\Commercials\Morning\Ads";
        const string Late = @"D:\Media\Commercials\Late\Ads";
        (SchedulerConfig config, ChannelConfig channel) = Setup(("Shared", Shared), ("Morning", Morning), ("Late", Late));
        ContentSelector episodes = new() { Query = "kind:'Episode'" };
        channel.Blocks =
        [
            new BlockConfig { Name = "Morning", Time = TimeSpan.Zero, EndTime = TimeSpan.FromMinutes(30), Recurrence = Recurrence.Daily,
                Shows = [episodes], AdBreaks = ["Shared", "Morning"] },
            new BlockConfig { Name = "Late", Time = TimeSpan.FromMinutes(30), EndTime = TimeSpan.FromMinutes(60), Recurrence = Recurrence.Daily,
                Shows = [episodes], AdBreaks = ["Shared", "Late"] }
        ];

        MediaItem[] ads = [.. Enumerable.Range(0, 60).Select(index => Ad(Shared, index))];
        var engine = new SchedulingEngine(
            new FakeMediaLibrary([.. ads, TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(1))]), new InMemoryPlaybackStateStore());
        DateTimeOffset start = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, start, start.AddHours(1));

        List<ScheduleEntry> all = Ads(schedule);
        HashSet<string> morning = [.. all.Where(entry => entry.BlockName == "Morning").Select(entry => entry.Item!.Id)];
        List<string> late = [.. all.Where(entry => entry.BlockName == "Late").Select(entry => entry.Item!.Id)];

        // 48 of the 60 aired in the morning block; the late block (its own shuffle) plays the other 12 before any of those.
        Assert.Equal(48, morning.Count);
        Assert.Equal(48, late.Count);
        Assert.All(late.Take(12), id => Assert.DoesNotContain(id, morning));
        Assert.Equal(12, late.Take(12).Distinct().Count());
    }
}
