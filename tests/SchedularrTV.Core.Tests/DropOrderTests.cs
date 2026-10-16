using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class DropOrderTests
{
    // A Monday.
    private static readonly DateTimeOffset Monday = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

    private static DropContext At(DateTimeOffset now) => new(TimeZoneInfo.Utc, now);

    private static List<MediaItem> Show(string name, int episodes) =>
        [.. Enumerable.Range(1, episodes).Select(e => TestMedia.Episode(name, 1, e, TimeSpan.FromMinutes(22)))];

    private static DropConfig Mondays(int count = 1, TimeSpan? airTime = null) =>
        new() { Days = [DayOfWeek.Monday], Count = count, Start = "2026-01-12", Time = airTime };

    [Fact]
    public void Drops_are_on_fridays_unless_days_or_an_interval_are_set()
    {
        Assert.Equal(new[] { DayOfWeek.Friday }, new DropConfig().DropDays);
        Assert.Empty(new DropConfig { Every = TimeSpan.FromDays(3) }.DropDays);
        Assert.Equal(new[] { DayOfWeek.Monday }, Mondays().DropDays);

        DropPlan plan = DropPlan.Create(new DropConfig { Start = "2026-01-12" }, At(Monday), null);
        Assert.Equal(0, plan.ReleasedAt(Monday.AddDays(3)));
        Assert.Equal(1, plan.ReleasedAt(Monday.AddDays(4)));
    }

    [Fact]
    public void Without_an_air_time_new_episodes_take_the_shows_normal_turns()
    {
        List<MediaItem> items = [.. Show("Drop", 5), .. Show("Other", 30)];
        Dictionary<string, TitleRotation.TitleRule> rules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Drop"] = new(PlaybackOrder.Drop, 1, Mondays())
        };
        MediaSequence sequence = MediaSequence.CreateMixed("k", items, PlaybackOrder.Shuffle, rules, null, At(Monday), null);
        sequence.At = Monday.AddHours(1);

        // Whenever the show first comes up, it's with the new episode; nothing jumps the queue for it.
        MediaItem? firstDrop = null;
        bool firstWasPremiere = false;
        for (int i = 0; i < 200 && firstDrop is null; i++)
        {
            Assert.True(sequence.TryTake(null, out MediaItem item));
            if (item.SeriesName == "Drop")
            {
                firstDrop = item;
                firstWasPremiere = sequence.LastTakeWasPremiere;
            }
        }

        Assert.NotNull(firstDrop);
        Assert.Equal("Drop-s01e01", firstDrop!.Id);
        Assert.True(firstWasPremiere);
    }

    [Fact]
    public void Weekly_drops_count_each_monday_from_the_start()
    {
        DropPlan plan = DropPlan.Create(Mondays(), At(Monday), null);

        Assert.Equal(0, plan.ReleasedAt(Monday.AddSeconds(-1)));
        Assert.Equal(1, plan.ReleasedAt(Monday));
        Assert.Equal(1, plan.ReleasedAt(Monday.AddDays(6)));
        Assert.Equal(2, plan.ReleasedAt(Monday.AddDays(7)));
        Assert.Equal(4, plan.ReleasedAt(Monday.AddDays(21).AddHours(5)));
        Assert.Equal(Monday.AddDays(7), plan.NextDropAfter(Monday.AddHours(1)));
    }

    [Fact]
    public void Interval_drops_and_episodes_already_out_add_up()
    {
        DropConfig config = new() { Every = TimeSpan.FromDays(3), Count = 2, AlreadyOut = 3, Start = "2026-01-12" };
        DropPlan plan = DropPlan.Create(config, At(Monday), null);

        Assert.Equal(3, plan.ReleasedAt(Monday.AddDays(-1)));
        Assert.Equal(5, plan.ReleasedAt(Monday));
        Assert.Equal(5, plan.ReleasedAt(Monday.AddDays(2)));
        Assert.Equal(7, plan.ReleasedAt(Monday.AddDays(3)));
    }

    [Fact]
    public void Without_a_start_the_latest_drop_counts_so_one_is_out_straight_away()
    {
        DateTimeOffset friday = Monday.AddDays(4).AddHours(15);
        DropPlan plan = DropPlan.Create(new DropConfig { Days = [DayOfWeek.Monday], Time = TimeSpan.FromHours(20) }, At(friday), null);

        Assert.Equal(Monday.AddHours(20), plan.Origin);
        Assert.Equal(1, plan.ReleasedAt(friday));
    }

    [Fact]
    public void A_dropped_episode_airs_new_once_then_only_repeats_until_the_next_drop()
    {
        MediaSequence sequence = MediaSequence.CreateDrop("k", Show("Drop", 5), Mondays(), At(Monday), null);

        sequence.At = Monday.AddHours(10);
        Assert.True(sequence.TryTake(null, out MediaItem first));
        Assert.Equal("Drop-s01e01", first.Id);
        Assert.True(sequence.LastTakeWasPremiere);

        sequence.At = Monday.AddHours(12);
        Assert.True(sequence.TryTake(null, out MediaItem repeat));
        Assert.Equal("Drop-s01e01", repeat.Id);
        Assert.False(sequence.LastTakeWasPremiere);

        sequence.At = Monday.AddDays(7).AddMinutes(1);
        Assert.True(sequence.TryTake(null, out MediaItem second));
        Assert.Equal("Drop-s01e02", second.Id);
        Assert.True(sequence.LastTakeWasPremiere);
    }

    [Fact]
    public void Several_episodes_can_drop_at_once()
    {
        MediaSequence sequence = MediaSequence.CreateDrop("k", Show("Drop", 5), Mondays(count: 2), At(Monday), null);
        sequence.At = Monday;

        Assert.True(sequence.TryTake(null, out MediaItem first));
        Assert.True(sequence.LastTakeWasPremiere);
        Assert.True(sequence.TryTake(null, out MediaItem second));
        Assert.True(sequence.LastTakeWasPremiere);
        Assert.Equal(new[] { "Drop-s01e01", "Drop-s01e02" }, new[] { first.Id, second.Id });

        Assert.True(sequence.TryTake(null, out _));
        Assert.False(sequence.LastTakeWasPremiere);
    }

    [Fact]
    public void Nothing_airs_before_the_first_drop()
    {
        DropConfig later = new() { Days = [DayOfWeek.Monday], Start = "2026-02-02" };
        MediaSequence sequence = MediaSequence.CreateDrop("k", Show("Drop", 5), later, At(Monday), null);
        sequence.At = Monday.AddDays(3);

        Assert.False(sequence.TryTake(null, out _));
    }

    [Fact]
    public void The_place_in_the_drops_survives_a_save()
    {
        List<MediaItem> pool = Show("Drop", 5);
        MediaSequence sequence = MediaSequence.CreateDrop("k", pool, Mondays(), At(Monday), null);
        sequence.At = Monday.AddDays(7);
        Assert.True(sequence.TryTake(null, out _));
        Assert.True(sequence.TryTake(null, out _));

        PlaybackState state = sequence.ToState();
        Assert.Equal(2, state.Premiered);

        MediaSequence resumed = MediaSequence.CreateDrop("k", pool, Mondays(), At(Monday.AddDays(8)), state.Clone());
        resumed.At = Monday.AddDays(8);
        Assert.True(resumed.TryTake(null, out MediaItem item));
        Assert.False(resumed.LastTakeWasPremiere);
        Assert.Contains(item.Id, new[] { "Drop-s01e01", "Drop-s01e02" });
    }

    [Fact]
    public void A_show_with_an_air_time_puts_its_fresh_drop_first()
    {
        List<MediaItem> items = [.. Show("Drop", 5), .. Show("Other", 30)];
        Dictionary<string, TitleRotation.TitleRule> rules = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Drop"] = new(PlaybackOrder.Drop, 1, Mondays(airTime: TimeSpan.Zero))
        };
        MediaSequence sequence = MediaSequence.CreateMixed("k", items, PlaybackOrder.Shuffle, rules, null, At(Monday), null);

        sequence.At = Monday.AddHours(1);
        Assert.True(sequence.TryTake(null, out MediaItem first));
        Assert.Equal("Drop-s01e01", first.Id);
        Assert.True(sequence.LastTakeWasPremiere);

        // The rest of the day only ever repeats what has dropped.
        for (int i = 0; i < 40; i++)
        {
            Assert.True(sequence.TryTake(null, out MediaItem item));
            Assert.False(sequence.LastTakeWasPremiere);
            if (item.SeriesName == "Drop")
            {
                Assert.Equal("Drop-s01e01", item.Id);
            }
        }
    }

    [Fact]
    public async Task The_schedule_marks_the_new_episode_and_repeats_it_afterwards()
    {
        ChannelConfig channel = new()
        {
            Id = "drops",
            Name = "Drops",
            Number = 1,
            TimeZone = "UTC",
            BaseRotation = new ContentSelector
            {
                Order = PlaybackOrder.Shuffle,
                Include =
                [
                    new ContentInclude { Show = "Drop", Order = PlaybackOrder.Drop, Drop = Mondays(airTime: TimeSpan.Zero) },
                    new ContentInclude { Show = "Other" }
                ]
            }
        };
        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Channels = [channel]
        };

        var engine = new SchedulingEngine(new FakeMediaLibrary([.. Show("Drop", 5), .. Show("Other", 30)]), new InMemoryPlaybackStateStore());
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, Monday, Monday + TimeSpan.FromHours(12));

        List<ScheduleEntry> programmes = [.. schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Program)];
        Assert.True(programmes[0].IsPremiere);
        Assert.Equal("Drop-s01e01", programmes[0].Item!.Id);
        Assert.Single(programmes.Where(entry => entry.IsPremiere).Select(entry => entry.Item!.Id).Distinct());
        Assert.All(programmes.Where(entry => entry.Item!.SeriesName == "Drop"), entry => Assert.Equal("Drop-s01e01", entry.Item!.Id));
    }

    [Fact]
    public void Drop_settings_read_from_yaml()
    {
        const string yaml = """
            global_collections:
              c:
                query: "*"
                order: drop
                drop:
                  days: [monday, thursday]
                  time: "20:00"
                  count: 2
                  release_order: air_date
                  repeats: random
            channels:
              - id: a
                name: A
                base_rotation:
                  collection: c
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(yaml);
        ContentSelector collection = config.GlobalCollections["c"];
        Assert.Equal(PlaybackOrder.Drop, collection.Order);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Thursday }, collection.Drop!.Days);
        Assert.Equal(TimeSpan.FromHours(20), collection.Drop.Time);
        Assert.Equal(2, collection.Drop.Count);
        Assert.Equal(DropReleaseOrder.AirDate, collection.Drop.ReleaseOrder);
        Assert.Equal(PlaybackOrder.Random, collection.Drop.Repeats);
        Assert.True(SchedulerConfigLoader.Validate(config).IsValid);
    }

    [Fact]
    public void A_drop_of_no_episodes_is_reported()
    {
        const string yaml = """
            global_collections:
              c:
                query: "*"
                order: drop
                drop:
                  count: 0
                  start: "next monday"
            channels:
              - id: a
                name: A
                base_rotation:
                  collection: c
            """;

        ConfigValidationResult result = SchedulerConfigLoader.Validate(SchedulerConfigLoader.Parse(yaml));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("count", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("start", StringComparison.Ordinal));
    }
}
