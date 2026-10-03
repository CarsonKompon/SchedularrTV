using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class TitleRotationTests
{
    private static readonly DateTimeOffset Day = new(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PlaybackOrder.Chronological)]
    [InlineData(PlaybackOrder.Shuffle)]
    [InlineData(PlaybackOrder.Random)]
    public void Unequal_titles_keep_weighted_turns_and_resume_independently(PlaybackOrder order)
    {
        var items = Library().Where(item => item.SeriesName != "Mid Show" &&
            (item.SeriesName != "Old Show" || item.EpisodeNumber <= 2)).ToArray();
        var rules = new Dictionary<string, TitleRotation.TitleRule> { ["Old Show"] = new(null, 2) };
        MediaSequence Create(PlaybackState? state) => MediaSequence.CreateRotation("rotation", items,
            TitleOrder.Shuffle, order, rules, [], "seed", state);
        var sequence = Create(null);
        List<MediaItem> played = [];
        for (int index = 0; index < 30; index++)
        {
            Assert.True(sequence.TryTake(null, out var item));
            played.Add(item);
        }
        Assert.Equal(20, played.Count(item => item.SeriesName == "Old Show"));
        Assert.Equal(10, played.Count(item => item.SeriesName == "New Show"));
        if (order != PlaybackOrder.Random)
        {
            foreach (var cycle in played.Where(item => item.SeriesName == "Old Show").Chunk(2))
                Assert.Equal(2, cycle.Select(item => item.Id).Distinct().Count());
        }
        var state = System.Text.Json.JsonSerializer.Deserialize<PlaybackState>(
            System.Text.Json.JsonSerializer.Serialize(sequence.ToState()))!;
        var resumed = Create(state.Clone());
        Assert.Equal(sequence.PreviewOrder(10).Select(item => item.Id), resumed.PreviewOrder(10).Select(item => item.Id));
        for (int index = 0; index < 50; index++)
        {
            Assert.True(sequence.TryTake(null, out var expected));
            Assert.True(resumed.TryTake(null, out var actual));
            Assert.Equal(expected.Id, actual.Id);
        }
    }

    [Fact]
    public void Legacy_rotation_state_migrates_each_titles_episode_position()
    {
        var items = Library();
        var rules = new Dictionary<string, TitleRotation.TitleRule>();
        string[] titles = ["Old Show", "Mid Show", "New Show"];
        var legacy = MediaSequence.CreatePreordered("rotation", TitleRotation.Build(items,
            TitleOrder.Ordered, PlaybackOrder.Chronological, rules, titles, "seed"), null);
        for (int index = 0; index < 8; index++) Assert.True(legacy.TryTake(null, out _));
        var migrated = MediaSequence.CreateRotation("rotation", items, TitleOrder.Ordered,
            PlaybackOrder.Chronological, rules, titles, "seed", legacy.ToState());
        foreach (int episode in new[] { 4, 4, 3 })
        {
            Assert.True(migrated.TryTake(null, out var item));
            Assert.Equal(episode, item.EpisodeNumber);
        }
    }

    [Fact]
    public void Consuming_a_preview_advances_the_same_title_turn_as_taking_it()
    {
        var rules = new Dictionary<string, TitleRotation.TitleRule> { ["Old Show"] = new(null, 3) };
        MediaSequence Create() => MediaSequence.CreateRotation("rotation", Library(), TitleOrder.Ordered,
            PlaybackOrder.Chronological, rules, [], "seed", null);
        var direct = Create();
        var previewed = Create();
        for (int index = 0; index < 20; index++)
        {
            Assert.True(direct.TryTake(null, out var expected));
            var actual = Assert.Single(previewed.PreviewOrder(1));
            Assert.Equal(expected.Id, actual.Id);
            previewed.Consume(actual.Id);
        }
    }

    [Fact]
    public void Adding_episodes_preserves_each_title_position_and_stacking_preserves_title_turns()
    {
        var items = Library();
        var rules = new Dictionary<string, TitleRotation.TitleRule>();
        string[] titles = ["Old Show", "Mid Show", "New Show"];
        var sequence = MediaSequence.CreateRotation("rotation", items, TitleOrder.Ordered,
            PlaybackOrder.Chronological, rules, titles, "seed", null);
        Assert.True(sequence.TryTake(null, out var first));
        Assert.True(sequence.TryTakeSameTitle("Old Show", _ => true, out var extra));
        Assert.Equal(2, extra.EpisodeNumber);
        items.Add(Episode("Old Show", 1, 13, Day));
        var resumed = MediaSequence.CreateRotation("rotation", items, TitleOrder.Ordered,
            PlaybackOrder.Chronological, rules, titles, "seed", sequence.ToState());
        Assert.True(resumed.TryTake(null, out var next));
        Assert.Equal("Mid Show", next.SeriesName);
        Assert.True(resumed.TryTake(null, out _));
        Assert.True(resumed.TryTake(null, out var old));
        Assert.Equal("Old Show", old.SeriesName);
        Assert.Equal(3, old.EpisodeNumber);
    }

    private static List<MediaItem> Library()
    {
        List<MediaItem> items = [];

        // Deliberately staggered air dates: "Old Show" would monopolise a flat chronological pool.
        for (int e = 1; e <= 12; e++)
        {
            items.Add(Episode("Old Show", 1, e, new DateTimeOffset(1990, 1, e, 0, 0, 0, TimeSpan.Zero)));
            items.Add(Episode("Mid Show", 1, e, new DateTimeOffset(2000, 1, e, 0, 0, 0, TimeSpan.Zero)));
            items.Add(Episode("New Show", 1, e, new DateTimeOffset(2020, 1, e, 0, 0, 0, TimeSpan.Zero)));
        }

        return items;
    }

    private static MediaItem Episode(string series, int season, int episode, DateTimeOffset aired) => new()
    {
        Id = $"{series}-{season}-{episode}",
        Title = $"{series} {season}x{episode}",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(22),
        SeriesName = series,
        SeasonNumber = season,
        EpisodeNumber = episode,
        PremiereDate = aired
    };

    private static async Task<ChannelSchedule> BuildAsync(ContentSelector rotation)
    {
        ChannelConfig channel = new()
        {
            Id = "cn",
            Name = "Cartoon Network",
            Number = 1,
            TimeZone = "UTC",
            BaseRotation = rotation
        };

        SchedulerConfig config = new()
        {
            Settings = new SchedulerSettings { TimeZone = "UTC" },
            Channels = [channel]
        };

        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore());
        return await engine.BuildAsync(config, channel, Day, Day + TimeSpan.FromHours(6));
    }

    private static List<string> Titles(ChannelSchedule schedule) =>
        [.. schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program)
            .Select(e => e.Item!.SeriesName!)];

    [Fact]
    public async Task A_flat_chronological_pool_plays_only_the_oldest_show()
    {
        ChannelSchedule schedule = await BuildAsync(new ContentSelector
        {
            Query = "kind == 'Episode'",
            Order = PlaybackOrder.Chronological
        });

        // This is the behaviour that made the channel feel broken; it is kept as a baseline.
        Assert.Single(Titles(schedule).Take(8).Distinct());
    }

    [Fact]
    public async Task Rotating_titles_keeps_every_show_on_air()
    {
        ChannelSchedule schedule = await BuildAsync(new ContentSelector
        {
            Query = "kind == 'Episode'",
            Order = PlaybackOrder.Chronological,
            TitleOrder = TitleOrder.Ordered
        });

        List<string> titles = Titles(schedule);

        Assert.Equal(3, titles.Distinct().Count());

        // Round-robin: no title twice in a row while others still have episodes.
        for (int i = 1; i < titles.Count; i++)
        {
            Assert.NotEqual(titles[i - 1], titles[i]);
        }
    }

    [Fact]
    public async Task Each_title_still_advances_in_its_own_order()
    {
        ChannelSchedule schedule = await BuildAsync(new ContentSelector
        {
            Query = "kind == 'Episode'",
            Order = PlaybackOrder.Chronological,
            TitleOrder = TitleOrder.Ordered
        });

        List<int> oldShow = [.. schedule.Entries
            .Where(e => e.Kind == ScheduleEntryKind.Program && e.Item!.SeriesName == "Old Show")
            .Select(e => e.Item!.EpisodeNumber!.Value)];

        Assert.NotEmpty(oldShow);
        Assert.Equal(oldShow.OrderBy(n => n), oldShow);
    }

    [Fact]
    public void Ordered_titles_follow_the_collection_order()
    {
        (Dictionary<string, TitleRotation.TitleRule> rules, List<string> order) = TitleRotation.RulesFrom(
        [
            new ContentInclude { Show = "New Show" },
            new ContentInclude { Show = "Old Show" },
            new ContentInclude { Show = "Mid Show" }
        ]);

        List<MediaItem> laid = TitleRotation.Build(
            Library(),
            TitleOrder.Ordered,
            PlaybackOrder.Chronological,
            rules,
            order,
            "seed");

        Assert.Equal(["New Show", "Old Show", "Mid Show"], laid.Take(3).Select(i => i.SeriesName));
    }

    [Fact]
    public void A_title_can_override_the_collection_order()
    {
        (Dictionary<string, TitleRotation.TitleRule> rules, List<string> order) = TitleRotation.RulesFrom(
        [
            new ContentInclude { Show = "Old Show" },
            new ContentInclude { Show = "Mid Show", Order = PlaybackOrder.Shuffle }
        ]);

        List<MediaItem> laid = TitleRotation.Build(
            Library(),
            TitleOrder.Ordered,
            PlaybackOrder.Chronological,
            rules,
            order,
            "seed");

        List<int> old = [.. laid.Where(i => i.SeriesName == "Old Show").Select(i => i.EpisodeNumber!.Value)];
        List<int> mid = [.. laid.Where(i => i.SeriesName == "Mid Show").Select(i => i.EpisodeNumber!.Value)];

        Assert.Equal(old.OrderBy(n => n), old);
        Assert.NotEqual(mid.OrderBy(n => n), mid);
    }

    [Fact]
    public void Weight_gives_a_title_more_turns()
    {
        (Dictionary<string, TitleRotation.TitleRule> rules, List<string> order) = TitleRotation.RulesFrom(
        [
            new ContentInclude { Show = "Old Show", Weight = 3 },
            new ContentInclude { Show = "Mid Show" },
            new ContentInclude { Show = "New Show" }
        ]);

        List<MediaItem> laid = TitleRotation.Build(
            Library(),
            TitleOrder.Ordered,
            PlaybackOrder.Chronological,
            rules,
            order,
            "seed");

        // One full round is 3 + 1 + 1 turns, spread through the round rather than played in a block.
        Assert.Equal(
            ["Old Show", "Mid Show", "Old Show", "New Show", "Old Show"],
            laid.Take(5).Select(i => i.SeriesName));
    }

    [Fact]
    public void A_weighted_title_does_not_play_its_whole_turn_back_to_back()
    {
        (Dictionary<string, TitleRotation.TitleRule> rules, List<string> order) = TitleRotation.RulesFrom(
        [
            new ContentInclude { Show = "Old Show", Weight = 3 },
            new ContentInclude { Show = "Mid Show" },
            new ContentInclude { Show = "New Show" }
        ]);

        string?[] played = [.. TitleRotation
            .Build(Library(), TitleOrder.Ordered, PlaybackOrder.Chronological, rules, order, "seed")
            .Select(i => i.SeriesName)];

        int longest = 1, run = 1;
        for (int i = 1; i < played.Length; i++)
        {
            run = played[i] == played[i - 1] ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        // Weight 3 against two other titles should never need three in a row while they still have
        // episodes; the tail, once the lighter titles are exhausted, is unavoidable.
        int beforeExhaustion = played.Length - 12;
        string?[] head = [.. played.Take(beforeExhaustion)];

        int headLongest = 1, headRun = 1;
        for (int i = 1; i < head.Length; i++)
        {
            headRun = head[i] == head[i - 1] ? headRun + 1 : 1;
            headLongest = Math.Max(headLongest, headRun);
        }

        Assert.True(headLongest < 3, $"expected no title to play 3 in a row while others remain, saw {headLongest}");
    }

    [Fact]
    public void Shuffled_titles_are_stable_for_a_given_seed()
    {
        List<string> Run() =>
        [
            .. TitleRotation.Build(
                    Library(),
                    TitleOrder.Shuffle,
                    PlaybackOrder.Chronological,
                    new Dictionary<string, TitleRotation.TitleRule>(StringComparer.OrdinalIgnoreCase),
                    [],
                    "seed")
                .Take(3)
                .Select(i => i.SeriesName!)
        ];

        Assert.Equal(Run(), Run());
        Assert.Equal(3, Run().Distinct().Count());
    }

    [Fact]
    public void A_single_title_is_left_alone()
    {
        List<MediaItem> one = [.. Library().Where(i => i.SeriesName == "Old Show")];

        List<MediaItem> laid = TitleRotation.Build(
            one,
            TitleOrder.Shuffle,
            PlaybackOrder.Chronological,
            new Dictionary<string, TitleRotation.TitleRule>(StringComparer.OrdinalIgnoreCase),
            [],
            "seed");

        Assert.Equal(one.Select(i => i.EpisodeNumber).OrderBy(n => n), laid.Select(i => i.EpisodeNumber));
    }
}
