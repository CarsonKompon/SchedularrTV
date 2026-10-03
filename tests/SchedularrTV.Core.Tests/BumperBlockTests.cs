using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class BumperBlockTests
{
    private static readonly DateTimeOffset Day = new(2026, 4, 13, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void Season_restrictions_control_selection_and_reserved_time(int? season, bool eligible)
    {
        MediaItem episode = Episode(season, 1);
        var bumper = new ResolvedBumper(new BumperBlock { Seasons = [1, 2] },
            [new ResolvedClip(Clip("theme", 20), []), new ResolvedClip(Clip("pinned", 20), ["S03E01"])]);

        Assert.Equal(eligible, bumper.AppliesTo(episode));
        Assert.Equal(eligible ? TimeSpan.FromSeconds(20) : TimeSpan.Zero, bumper.MinimumFor(episode));
        Assert.Equal(eligible ? "theme" : null, bumper.Choose(episode, TimeSpan.FromMinutes(1), "seed")?.Id);
    }

    [Fact]
    public void Unrestricted_blocks_keep_unknown_seasons_and_specials_can_be_selected_explicitly()
    {
        var bumper = Bumper(BumperSelection.Random, new ResolvedClip(Clip("theme", 20), []));
        Assert.NotNull(bumper.Choose(null, TimeSpan.FromMinutes(1), "seed"));
        bumper.Block.Seasons = [0];
        Assert.NotNull(bumper.Choose(Episode(0, 1), TimeSpan.FromMinutes(1), "seed"));
        Assert.Null(bumper.Choose(null, TimeSpan.FromMinutes(1), "seed"));
        bumper.Block.SkipWhenFileContains = ["s00e01"];
        Assert.Null(bumper.Choose(Episode(0, 1), TimeSpan.FromMinutes(1), "seed"));
    }

    private static MediaItem Clip(string id, int seconds) => new()
    {
        Id = id,
        Title = id,
        Kind = MediaKind.Unknown,
        Duration = TimeSpan.FromSeconds(seconds),
        Path = $"/media/tv/shorty/Extras/{id}.avi"
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Season_specific_bookends_use_the_first_and_last_episode_of_each_slot(bool shared, bool stack)
    {
        MediaItem earlyIntro = Clip("early-intro", 20);
        MediaItem lateIntro = Clip("late-intro", 20);
        MediaItem earlyOutro = Clip("early-outro", 20);
        MediaItem lateOutro = Clip("late-outro", 20);
        List<BumperBlock> bumpers =
        [
            new() { Name = "Early intro", Seasons = [1, 2], Clips = [new() { Path = earlyIntro.Path! }] },
            new() { Name = "Late intro", Seasons = [3], Clips = [new() { Path = lateIntro.Path! }] },
            new() { Name = "Early outro", Position = BumperPosition.Outro, Seasons = [1, 2], Clips = [new() { Path = earlyOutro.Path! }] },
            new() { Name = "Late outro", Position = BumperPosition.Outro, Seasons = [3], Clips = [new() { Path = lateOutro.Path! }] }
        ];
        ChannelConfig channel = Channel(new ContentInclude { Show = "Shorty", Bumpers = shared ? [] : bumpers });
        channel.StackToFill = stack;
        var config = new SchedulerConfig { Channels = [channel] };
        if (shared) config.Titles["Shorty"] = new TitleDefaults { Bumpers = bumpers };
        var engine = new SchedulingEngine(new FakeMediaLibrary([
            earlyIntro, lateIntro, earlyOutro, lateOutro, Episode(2, 1), Episode(3, 1)]), new InMemoryPlaybackStateStore());

        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Day, Day.AddMinutes(stack ? 30 : 60));
        var played = schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program).Select(entry => entry.Item!.Id).ToArray();
        Assert.Equal(stack
            ? ["early-intro", "s2e1", "s3e1", "late-outro"]
            : new[] { "early-intro", "s2e1", "early-outro", "late-intro", "s3e1", "late-outro" }, played);
        Assert.Equal(Day.AddMinutes(stack ? 30 : 60), schedule.End);
    }

    [Fact]
    public void Season_restrictions_round_trip_for_shared_and_collection_blocks()
    {
        var restricted = new BumperBlock { Name = "Early", Seasons = [2, 1, 2], Clips = [new() { Path = "intro.mp4" }] };
        var config = new SchedulerConfig
        {
            Titles = { ["Shorty"] = new TitleDefaults { Bumpers = [restricted, new BumperBlock { Name = "All" }] } },
            GlobalCollections = { ["Shows"] = new ContentSelector { Include = [new ContentInclude { Show = "Shorty", Bumpers = [restricted] }] } }
        };
        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        Assert.Equal([1, 2], parsed.Titles["Shorty"].Bumpers[0].Seasons);
        Assert.Empty(parsed.Titles["Shorty"].Bumpers[1].Seasons);
        Assert.Equal([1, 2], parsed.GlobalCollections["Shows"].Include[0].Bumpers[0].Seasons);
    }

    private static MediaItem Episode(int? season, int episode, int minutes = 11) => new()
    {
        Id = $"s{season}e{episode}",
        Title = $"Shorty S{season}E{episode}",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(minutes),
        SeriesId = "Shorty",
        SeriesName = "Shorty",
        SeasonNumber = season,
        EpisodeNumber = episode,
        Path = $"/media/tv/shorty/s{season:D2}e{episode:D2}.mkv"
    };

    private static ResolvedBumper Bumper(BumperSelection selection, params ResolvedClip[] clips) =>
        new(new BumperBlock { Name = "Test", Selection = selection }, clips);

    [Fact]
    public void Longest_that_fits_prefers_the_long_version_when_there_is_room()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.LongestThatFits,
            new ResolvedClip(Clip("short", 15), []),
            new ResolvedClip(Clip("long", 45), []));

        Assert.Equal("long", block.Choose(Episode(1, 1), TimeSpan.FromSeconds(60), "seed")?.Id);
    }

    [Fact]
    public void Longest_that_fits_falls_back_when_the_long_version_would_not_fit()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.LongestThatFits,
            new ResolvedClip(Clip("short", 15), []),
            new ResolvedClip(Clip("long", 45), []));

        Assert.Equal("short", block.Choose(Episode(1, 1), TimeSpan.FromSeconds(20), "seed")?.Id);
    }

    [Fact]
    public void Nothing_plays_when_even_the_shortest_clip_is_too_long()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.LongestThatFits,
            new ResolvedClip(Clip("short", 15), []));

        Assert.Null(block.Choose(Episode(1, 1), TimeSpan.FromSeconds(5), "seed"));
    }

    [Fact]
    public void A_pinned_clip_is_forced_for_its_own_episode()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.Random,
            new ResolvedClip(Clip("general-a", 10), []),
            new ResolvedClip(Clip("general-b", 10), []),
            new ResolvedClip(Clip("finale", 10), ["S01E03"]));

        Assert.Equal("finale", block.Choose(Episode(1, 3), TimeSpan.FromMinutes(1), "seed")?.Id);
    }

    [Fact]
    public void A_pinned_clip_never_plays_with_any_other_episode()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.Random,
            new ResolvedClip(Clip("general", 10), []),
            new ResolvedClip(Clip("finale", 10), ["S01E03"]));

        for (int episode = 1; episode <= 8; episode++)
        {
            if (episode == 3)
            {
                continue;
            }

            Assert.Equal("general", block.Choose(Episode(1, episode), TimeSpan.FromMinutes(1), "seed")?.Id);
        }
    }

    [Fact]
    public void Random_is_stable_for_one_airing_but_varies_between_them()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.Random,
            new ResolvedClip(Clip("a", 10), []),
            new ResolvedClip(Clip("b", 10), []),
            new ResolvedClip(Clip("c", 10), []));

        MediaItem episode = Episode(1, 1);

        Assert.Equal(
            block.Choose(episode, TimeSpan.FromMinutes(1), "slot-1")?.Id,
            block.Choose(episode, TimeSpan.FromMinutes(1), "slot-1")?.Id);

        HashSet<string> seen = [];
        for (int i = 0; i < 40; i++)
        {
            seen.Add(block.Choose(episode, TimeSpan.FromMinutes(1), $"slot-{i}")!.Id);
        }

        Assert.True(seen.Count > 1, "a random block should not always return the same clip");
    }

    [Fact]
    public void Sizing_uses_the_shortest_clip_so_a_long_one_spends_break_time()
    {
        ResolvedBumper block = Bumper(
            BumperSelection.LongestThatFits,
            new ResolvedClip(Clip("short", 15), []),
            new ResolvedClip(Clip("long", 45), []));

        Assert.Equal(TimeSpan.FromSeconds(15), block.MinimumFor(Episode(1, 1)));
    }

    [Fact]
    public async Task Blocks_play_in_the_order_they_are_listed_around_the_episode()
    {
        MediaItem theme = Clip("theme", 20);
        MediaItem gag = Clip("gag", 10);
        MediaItem endCard = Clip("endcard", 8);
        MediaItem credits = Clip("credits", 12);

        List<MediaItem> library = [theme, gag, endCard, credits, Episode(1, 1), Episode(1, 2)];

        ChannelConfig channel = new()
        {
            Id = "kids",
            Name = "Kids",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(30),
            BaseRotation = new ContentSelector
            {
                Order = PlaybackOrder.SeasonEpisode,
                Include = [new ContentInclude
                {
                    Show = "Shorty",
                    Bumpers =
                    [
                        new BumperBlock { Name = "Theme", Position = BumperPosition.Intro, Clips = [new BumperClip { Path = theme.Path! }] },
                        new BumperBlock { Name = "Gag", Position = BumperPosition.Intro, Clips = [new BumperClip { Path = gag.Path! }] },
                        new BumperBlock { Name = "End card", Position = BumperPosition.Outro, Clips = [new BumperClip { Path = endCard.Path! }] },
                        new BumperBlock { Name = "Credits", Position = BumperPosition.Outro, Clips = [new BumperClip { Path = credits.Path! }] }
                    ]
                }]
            }
        };

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Channels = [channel]
        };

        SchedulingEngine engine = new(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromMinutes(30));

        List<string> played = [.. schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program && e.Start < Day.AddMinutes(30))
            .OrderBy(e => e.Start)
            .Select(e => e.Item!.Id)];

        Assert.Equal(["theme", "gag", "s1e1", "endcard", "credits"], played);
    }

    [Fact]
    public async Task A_show_level_block_applies_without_the_collection_naming_it()
    {
        MediaItem theme = Clip("theme", 20);
        List<MediaItem> library = [theme, Episode(1, 1), Episode(1, 2)];

        ChannelConfig channel = Channel(new ContentInclude { Show = "Shorty" });

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Titles =
            {
                ["Shorty"] = new TitleDefaults
                {
                    Bumpers = [new BumperBlock
                    {
                        Name = "Theme",
                        Position = BumperPosition.Intro,
                        Clips = [new BumperClip { Path = theme.Path! }]
                    }]
                }
            },
            Channels = [channel]
        };

        SchedulingEngine engine = new(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromMinutes(30));

        ScheduleEntry first = schedule.Entries.First(e => e.Kind == ScheduleEntryKind.Program);

        Assert.True(first.IsSlotIntro);
        Assert.Equal("theme", first.Item!.Id);
    }

    [Fact]
    public async Task A_collection_block_replaces_the_show_level_one_rather_than_adding_to_it()
    {
        MediaItem showTheme = Clip("show-theme", 20);
        MediaItem collectionTheme = Clip("collection-theme", 20);

        List<MediaItem> library = [showTheme, collectionTheme, Episode(1, 1), Episode(1, 2)];

        ChannelConfig channel = Channel(new ContentInclude
        {
            Show = "Shorty",
            Bumpers = [new BumperBlock
            {
                Name = "Themed",
                Position = BumperPosition.Intro,
                Clips = [new BumperClip { Path = collectionTheme.Path! }]
            }]
        });

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Titles =
            {
                ["Shorty"] = new TitleDefaults
                {
                    Bumpers = [new BumperBlock
                    {
                        Name = "Theme",
                        Position = BumperPosition.Intro,
                        Clips = [new BumperClip { Path = showTheme.Path! }]
                    }]
                }
            },
            Channels = [channel]
        };

        SchedulingEngine engine = new(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromMinutes(30));

        List<ScheduleEntry> intros = [.. schedule.Entries.Where(e => e.IsSlotIntro)];

        Assert.Single(intros);
        Assert.Equal("collection-theme", intros[0].Item!.Id);
    }

    private static ChannelConfig Channel(ContentInclude include) => new()
    {
        Id = "kids",
        Name = "Kids",
        Number = 1,
        TimeZone = "UTC",
        AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = new ContentSelector
        {
            Order = PlaybackOrder.SeasonEpisode,
            Include = [include]
        }
    };

    [Fact]
    public void A_block_is_skipped_when_the_episode_file_is_blacklisted()
    {
        var block = new BumperBlock
        {
            Name = "Theme",
            Selection = BumperSelection.Random,
            SkipWhenFileContains = ["(with Intro)"]
        };

        ResolvedBumper bumper = new(block, [new ResolvedClip(Clip("theme", 20), [])]);

        MediaItem baked = new()
        {
            Id = "s3e2",
            Title = "Awesomeness",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(11),
            SeriesName = "Shorty",
            SeasonNumber = 3,
            EpisodeNumber = 2,
            Path = @"M:\TV\Shorty\S03E02 Awesomeness (with Intro).mkv"
        };

        Assert.False(bumper.AppliesTo(baked));
        Assert.Null(bumper.Choose(baked, TimeSpan.FromMinutes(1), "seed"));
        Assert.Equal(TimeSpan.Zero, bumper.MinimumFor(baked));
    }

    [Fact]
    public void A_block_still_plays_for_episodes_that_are_not_blacklisted()
    {
        var block = new BumperBlock
        {
            Name = "Theme",
            Selection = BumperSelection.Random,
            SkipWhenFileContains = ["(with Intro)"]
        };

        ResolvedBumper bumper = new(block, [new ResolvedClip(Clip("theme", 20), [])]);

        Assert.True(bumper.AppliesTo(Episode(3, 1)));
        Assert.Equal("theme", bumper.Choose(Episode(3, 1), TimeSpan.FromMinutes(1), "seed")?.Id);
    }

    [Theory]
    [InlineData(@"M:\TV\Shorty\S03E02 Awesomeness (WITH INTRO).mkv", false)]
    [InlineData(@"M:\TV\Shorty\S03E02 Awesomeness (with intro).mkv", false)]
    [InlineData(@"M:\TV\Shorty (with Intro)\S03E02 Awesomeness.mkv", true)]
    public void The_blacklist_matches_the_file_name_case_insensitively(string path, bool applies)
    {
        var block = new BumperBlock { Name = "Theme", SkipWhenFileContains = ["(with Intro)"] };
        ResolvedBumper bumper = new(block, [new ResolvedClip(Clip("theme", 20), [])]);

        MediaItem episode = new()
        {
            Id = "e",
            Title = "e",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(11),
            SeriesName = "Shorty",
            SeasonNumber = 3,
            EpisodeNumber = 2,
            Path = path
        };

        Assert.Equal(applies, bumper.AppliesTo(episode));
    }

    [Fact]
    public async Task A_blacklisted_episode_airs_without_the_block()
    {
        MediaItem theme = Clip("theme", 20);

        MediaItem plain = Episode(1, 1);
        MediaItem baked = new()
        {
            Id = "s1e2",
            Title = "Shorty S1E2",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(11),
            SeriesId = "Shorty",
            SeriesName = "Shorty",
            SeasonNumber = 1,
            EpisodeNumber = 2,
            Path = @"M:\TV\Shorty\S01E02 Awesomeness (with Intro).mkv"
        };

        ChannelConfig channel = Channel(new ContentInclude
        {
            Show = "Shorty",
            Bumpers = [new BumperBlock
            {
                Name = "Theme",
                Position = BumperPosition.Intro,
                SkipWhenFileContains = ["(with Intro)"],
                Clips = [new BumperClip { Path = theme.Path! }]
            }]
        });

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Channels = [channel]
        };

        SchedulingEngine engine = new(new FakeMediaLibrary([theme, plain, baked]), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromHours(1));

        List<ScheduleEntry> ordered = [.. schedule.Entries.OrderBy(e => e.Start)];

        Assert.All(
            ordered.Where(e => e.Item?.Id == "s1e2"),
            episode =>
            {
                ScheduleEntry? before = ordered.LastOrDefault(e => e.End <= episode.Start);
                Assert.False(before?.IsSlotIntro, "the baked-in episode must not be given a second theme");
            });

        // The episode without the marker still opens with it, so the block is not simply broken.
        ScheduleEntry first = ordered.First(e => e.Item?.Id == "s1e1");
        Assert.True(
            ordered.LastOrDefault(e => e.End <= first.Start)?.IsSlotIntro == true,
            "the ordinary episode should still get its theme");
    }
}
