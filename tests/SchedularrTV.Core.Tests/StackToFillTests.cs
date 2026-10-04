using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class StackToFillTests
{
    private static readonly DateTimeOffset Day = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Eleven-minute episodes: two make a half-hour slot, one leaves nineteen minutes of adverts.</summary>
    private static List<MediaItem> Shorts(int minutes = 11, int count = 40)
    {
        List<MediaItem> items = [];

        for (int e = 1; e <= count; e++)
        {
            items.Add(TestMedia.Episode("Shorty", 1, e, TimeSpan.FromMinutes(minutes), collection: "Kids"));
        }

        for (int i = 0; i < 40; i++)
        {
            items.Add(TestMedia.Filler($"ad-{i}", TimeSpan.FromSeconds(30), "Commercials", "Any Time"));
        }

        return items;
    }

    private static async Task<ChannelSchedule> BuildAsync(bool stack, int minutes = 11)
    {
        ChannelConfig channel = new()
        {
            Id = "kids",
            Name = "Kids",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(30),
            StackToFill = stack,
            BaseRotation = new ContentSelector { Query = "collection:'Kids'", Order = PlaybackOrder.SeasonEpisode }
        };

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings
            {
                TimeZone = "UTC",
                Interstitials = new InterstitialsConfig
                {
                    Commercials = new CommercialsConfig { BaseQuery = "tag:'Commercials'" }
                }
            },
            Channels = [channel]
        };

        SchedulingEngine engine = new(new FakeMediaLibrary(Shorts(minutes)), new InMemoryPlaybackStateStore());
        return await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromHours(2));
    }

    private static List<(DateTimeOffset Start, string Id)> Programmes(ChannelSchedule schedule) =>
        [.. schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program)
            .Select(e => (e.Start, e.Item!.Id))
            .Distinct()];

    [Fact]
    public async Task Without_stacking_a_short_episode_leaves_the_rest_of_the_slot_to_adverts()
    {
        ChannelSchedule schedule = await BuildAsync(stack: false);

        List<(DateTimeOffset Start, string Id)> programmes = Programmes(schedule);

        // One episode per half-hour slot.
        Assert.Equal(Day, programmes[0].Start);
        Assert.Equal(Day.AddMinutes(30), programmes[1].Start);
    }

    [Fact]
    public async Task Stacking_puts_a_second_episode_in_the_same_slot()
    {
        ChannelSchedule schedule = await BuildAsync(stack: true);

        List<(DateTimeOffset Start, string Id)> programmes = Programmes(schedule);

        // Two eleven-minute episodes now share the half hour, so the second starts before :30.
        Assert.True(
            programmes[1].Start < Day.AddMinutes(30),
            $"expected a second episode inside the first slot, got {programmes[1].Start:HH:mm}");
    }

    [Fact]
    public async Task Stacking_only_ever_adds_episodes_of_the_same_title()
    {
        ChannelSchedule schedule = await BuildAsync(stack: true);

        IEnumerable<string?> titles = schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program)
            .Select(e => e.Item!.SeriesName);

        Assert.All(titles, t => Assert.Equal("Shorty", t));
    }

    [Fact]
    public async Task Stacking_does_not_skip_other_titles_turns_in_a_rotation()
    {
        string[] titles = ["Shorty", "Second", "Third", "Fourth"];
        var channel = new ChannelConfig
        {
            Id = "rotation", Name = "Rotation", PadToNearest = TimeSpan.FromMinutes(30), StackToFill = true,
            BaseRotation = new()
            {
                Include = titles.Select(title => new ContentInclude { Show = title }).ToList(),
                Order = PlaybackOrder.SeasonEpisode, TitleOrder = TitleOrder.Ordered
            }
        };
        var items = titles.SelectMany(title => Enumerable.Range(1, title == "Shorty" ? 4 : 40)
            .Select(number => TestMedia.Episode(title, 1, number, TimeSpan.FromMinutes(7)))).ToArray();
        var schedule = await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, Day, Day.AddHours(4));
        var firstInSlot = Enumerable.Range(0, 8).Select(slot => schedule.Entries.First(entry =>
            entry.Kind == ScheduleEntryKind.Program && entry.Start >= Day.AddMinutes(slot * 30)).Item!.SeriesName);
        Assert.Equal(titles.Concat(titles), firstInSlot);
        Assert.Equal(32, schedule.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Program));
    }

    [Fact]
    public async Task Stacking_keeps_the_grid_so_slots_still_start_on_the_boundary()
    {
        ChannelSchedule schedule = await BuildAsync(stack: true);

        List<DateTimeOffset> starts = [.. Programmes(schedule).Select(p => p.Start)];

        // Whatever fills a slot, the next slot still opens on the half hour.
        Assert.Contains(starts, s => s == Day.AddMinutes(30));
        Assert.Contains(starts, s => s == Day.AddMinutes(60));
    }

    [Fact]
    public async Task Stacking_never_runs_past_the_slot()
    {
        ChannelSchedule schedule = await BuildAsync(stack: true, minutes: 7);

        DateTimeOffset cursor = schedule.Start;
        foreach (ScheduleEntry entry in schedule.Entries.OrderBy(e => e.Start))
        {
            Assert.Equal(cursor, entry.Start);
            cursor = entry.End;
        }

        Assert.True(cursor <= schedule.End);
    }

    [Fact]
    public async Task Seven_minute_episodes_stack_further_than_eleven_minute_ones()
    {
        int Slots(ChannelSchedule s) => Programmes(s).Count(p => p.Start < Day.AddMinutes(30));

        Assert.True(Slots(await BuildAsync(stack: true, minutes: 7)) > Slots(await BuildAsync(stack: true, minutes: 11)));
    }

    [Theory]
    [InlineData(MidRollMode.Auto)]
    [InlineData(MidRollMode.Chapters)]
    public async Task Stacked_episode_boundaries_are_chapters_and_leave_room_for_real_breaks(MidRollMode mode)
    {
        var channel = new ChannelConfig
        {
            Id = "shorts", Name = "Shorts", PadToNearest = TimeSpan.FromMinutes(30), StackToFill = true,
            BaseRotation = new() { Query = "collection:'Kids'", Order = PlaybackOrder.SeasonEpisode },
            MidRoll = new() { Mode = mode, TargetSegment = TimeSpan.FromMinutes(15), MaxBreakLength = TimeSpan.FromMinutes(4) },
            Interstitials = new() { Commercials = new() { Query = "tag:'Commercials'" }, Filler = new() { Fallback = false } }
        };
        var schedule = await new SchedulingEngine(new FakeMediaLibrary(Shorts(7)), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, Day, Day.AddHours(1));
        for (int slot = 0; slot < 2; slot++)
        {
            var start = Day.AddMinutes(slot * 30);
            var entries = schedule.Entries.Where(entry => entry.Start >= start && entry.Start < start.AddMinutes(30)).ToArray();
            var programmes = entries.Where(entry => entry.Kind == ScheduleEntryKind.Program).ToArray();
            Assert.Equal(3, programmes.Length);
            Assert.Equal([start, start.AddMinutes(7), start.AddMinutes(18)], programmes.Select(entry => entry.Start));
            Assert.All(programmes, entry => Assert.Equal(TimeSpan.FromMinutes(7), entry.Duration));
            Assert.Equal(TimeSpan.FromMinutes(4), TimeSpan.FromTicks(entries.Where(entry => entry.Kind == ScheduleEntryKind.Commercial
                && entry.Start >= start.AddMinutes(14) && entry.End <= start.AddMinutes(18)).Sum(entry => entry.Duration.Ticks)));
            Assert.True(programmes[1].ContinuesSlot);
            Assert.True(programmes[2].ContinuesSlot);
            Assert.Equal(start.AddMinutes(30), entries[^1].End);
        }
        Assert.Equal(TimeSpan.Zero, schedule.TotalPadding);
    }

    [Fact]
    public async Task Two_eleven_and_a_half_minute_episodes_share_a_half_hour_with_breaks()
    {
        // 23 minutes of programme leaves 7 for a full four-minute mid-roll and the end break; reserving a full break at
        // the end as well used to turn the second episode away, leaving one episode and 18 minutes of adverts.
        List<MediaItem> media = [.. Enumerable.Range(1, 10).Select(e => TestMedia.Episode("Hungry", 1, e, TimeSpan.FromMinutes(11.5), collection: "Kids")),
            .. Enumerable.Range(0, 60).Select(i => TestMedia.Filler($"ad-{i}", TimeSpan.FromSeconds(30), "Commercials", "Any Time"))];
        var channel = new ChannelConfig
        {
            Id = "adults", Name = "Adults", PadToNearest = TimeSpan.FromMinutes(30), StackToFill = true,
            BaseRotation = new() { Query = "collection:'Kids'", Order = PlaybackOrder.SeasonEpisode },
            MidRoll = new() { Mode = MidRollMode.Auto, TargetSegment = TimeSpan.FromMinutes(14), MaxBreakLength = TimeSpan.FromMinutes(4), MinBreak = TimeSpan.FromSeconds(30) },
            Interstitials = new() { Commercials = new() { Query = "tag:'Commercials'" }, Filler = new() { Fallback = false } }
        };
        var schedule = await new SchedulingEngine(new FakeMediaLibrary(media), new InMemoryPlaybackStateStore())
            .BuildAsync(new SchedulerConfig(), channel, Day, Day.AddHours(1));

        var first = schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program && entry.Start < Day.AddMinutes(30)).ToArray();
        Assert.Equal(2, first.Length);
        Assert.Equal(TimeSpan.FromMinutes(23), TimeSpan.FromTicks(first.Sum(entry => entry.Duration.Ticks)));
    }

    [Fact]
    public async Task An_episode_is_never_stacked_twice_in_the_same_slot()
    {
        // Only three episodes for a grid that could hold five: the pool wraps mid-slot.
        ChannelConfig channel = new()
        {
            Id = "kids",
            Name = "Kids",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(30),
            StackToFill = true,
            BaseRotation = new ContentSelector { Query = "collection:'Kids'", Order = PlaybackOrder.SeasonEpisode }
        };

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Channels = [channel]
        };

        List<MediaItem> library =
        [
            TestMedia.Episode("Shorty", 1, 1, TimeSpan.FromMinutes(5), collection: "Kids"),
            TestMedia.Episode("Shorty", 1, 2, TimeSpan.FromMinutes(5), collection: "Kids"),
            TestMedia.Episode("Shorty", 1, 3, TimeSpan.FromMinutes(5), collection: "Kids")
        ];

        SchedulingEngine engine = new(new FakeMediaLibrary(library), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromMinutes(30));

        // Counted as runs, since mid-roll legitimately splits one episode across several entries.
        List<string> runs = [];
        string? previous = null;

        foreach (ScheduleEntry entry in schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program && e.Start < Day.AddMinutes(30))
            .OrderBy(e => e.Start))
        {
            if (entry.Item!.Id != previous)
            {
                runs.Add(entry.Item.Id);
                previous = entry.Item.Id;
            }
        }

        Assert.Equal(runs.Count, runs.Distinct().Count());
    }

    [Fact]
    public async Task An_intro_plays_once_at_the_top_of_the_slot()
    {
        MediaItem intro = new()
        {
            Id = "intro",
            Title = "Intro",
            Kind = MediaKind.Unknown,
            Duration = TimeSpan.FromSeconds(45),
            Path = "/media/tv/shorty/Extras/Intro.avi"
        };

        List<MediaItem> library = [intro, .. Shorts()];

        ChannelConfig channel = new()
        {
            Id = "kids",
            Name = "Kids",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(30),
            StackToFill = true,
            BaseRotation = new ContentSelector
            {
                Order = PlaybackOrder.SeasonEpisode,
                Include = [new ContentInclude
                {
                    Show = "Shorty",
                    Bumpers = [Block("Opening", BumperPosition.Intro, intro.Path!)]
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

        List<ScheduleEntry> slot = [.. schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program && e.Start < Day.AddMinutes(30))
            .OrderBy(e => e.Start)];

        Assert.Equal(Day, slot[0].Start);
        Assert.True(slot[0].IsSlotIntro, "the slot should open with the intro");
        Assert.Equal(intro.Id, slot[0].Item!.Id);

        // Once per slot, not before every stacked episode.
        Assert.Single(slot, e => e.IsSlotIntro);
        Assert.True(slot.Count > 1, "the intro should be followed by the programme");
    }

    [Fact]
    public async Task An_outro_closes_the_slot_after_the_last_episode()
    {
        MediaItem intro = new()
        {
            Id = "intro",
            Title = "Intro",
            Kind = MediaKind.Unknown,
            Duration = TimeSpan.FromSeconds(45),
            Path = "/media/tv/shorty/Extras/Intro.avi"
        };

        MediaItem outro = new()
        {
            Id = "outro",
            Title = "Outro",
            Kind = MediaKind.Unknown,
            Duration = TimeSpan.FromSeconds(30),
            Path = "/media/tv/shorty/Extras/Outro.avi"
        };

        List<MediaItem> library = [intro, outro, .. Shorts()];

        ChannelConfig channel = new()
        {
            Id = "kids",
            Name = "Kids",
            Number = 1,
            TimeZone = "UTC",
            AutoTemporalTags = false,
            PadToNearest = TimeSpan.FromMinutes(30),
            StackToFill = true,
            BaseRotation = new ContentSelector
            {
                Order = PlaybackOrder.SeasonEpisode,
                Include = [new ContentInclude
                {
                    Show = "Shorty",
                    Bumpers =
                    [
                        Block("Opening", BumperPosition.Intro, intro.Path!),
                        Block("Closing", BumperPosition.Outro, outro.Path!)
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

        List<ScheduleEntry> slot = [.. schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program && e.Start < Day.AddMinutes(30))
            .OrderBy(e => e.Start)];

        Assert.True(slot[0].IsSlotIntro, "the slot should open with the intro");

        ScheduleEntry last = slot[^1];
        Assert.True(last.IsSlotOutro, "the slot should close with the outro");
        Assert.Equal(outro.Id, last.Item!.Id);

        // Once per slot, and after the episodes rather than between them.
        Assert.Single(slot, e => e.IsSlotOutro);
        Assert.All(slot.Where(e => !e.IsSlotOutro), e => Assert.True(e.Start < last.Start));

        // Nothing is allowed between the closing episode and its end card.
        ScheduleEntry previous = schedule.Entries
            .Where(e => e.End <= last.Start)
            .OrderBy(e => e.End)
            .Last();

        Assert.Equal(ScheduleEntryKind.Program, previous.Kind);
        Assert.Equal(previous.End, last.Start);

        // Stacking must not spend the time the end card needs.
        Assert.True(
            slot.Count(e => !e.IsSlotIntro && !e.IsSlotOutro) > 1,
            "the slot should have stacked more than one episode");
    }

    private static BumperBlock Block(string name, BumperPosition position, params string[] paths) => new()
    {
        Name = name,
        Position = position,
        Clips = [.. paths.Select(p => new BumperClip { Path = p })]
    };
}
