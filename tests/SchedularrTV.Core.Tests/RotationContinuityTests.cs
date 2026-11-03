using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>
/// Shows keep their place when what a channel plays is edited: adding or removing a show mustn't send every other show
/// back to its first episode.
/// </summary>
public sealed class RotationContinuityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static List<MediaItem> Library() =>
        [.. new[] { "Alpha", "Bravo", "Charlie" }.SelectMany(show => Enumerable.Range(1, 30)
            .Select(episode => TestMedia.Episode(show, 1, episode, TimeSpan.FromMinutes(22), "Kids")))];

    private static ContentInclude Show(string name) => new() { Show = name };

    private static ChannelConfig Channel(ContentSelector rotation) => new()
    {
        Id = "kids",
        Name = "Kids",
        Number = 1,
        TimeZone = "UTC",
        AutoTemporalTags = false,
        PadToNearest = TimeSpan.FromMinutes(30),
        BaseRotation = rotation
    };

    private static SchedulerConfig Config(ChannelConfig channel, params ContentInclude[] kids)
    {
        SchedulerConfig config = new() { Settings = new SchedulerSettings { TimeZone = "UTC" } };
        config.Settings.Promos.Enabled = false;
        config.GlobalCollections["Kids"] = new ContentSelector
        {
            Include = [.. kids], Order = PlaybackOrder.Chronological, TitleOrder = TitleOrder.Shuffle
        };
        config.Channels.Add(channel);
        return config;
    }

    /// <summary>The episodes of <paramref name="show"/> the window plays, in order.</summary>
    private static async Task<List<int>> EpisodesAsync(SchedulingEngine engine, SchedulerConfig config, string show, DateTimeOffset from, TimeSpan length)
    {
        ChannelSchedule schedule = await engine.BuildAsync(config, config.Channels[0], from, from + length);
        return [.. schedule.Entries
            .Where(entry => entry.Kind == ScheduleEntryKind.Program && entry.Item?.SeriesName == show)
            .Select(entry => entry.Item!.EpisodeNumber ?? 0)];
    }

    [Fact]
    public async Task Adding_a_show_to_a_collection_keeps_the_other_shows_places()
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore());
        ChannelConfig channel = Channel(new ContentSelector { Collection = "Kids" });
        SchedulerConfig before = Config(channel, Show("Alpha"), Show("Bravo"));

        List<int> first = await EpisodesAsync(engine, before, "Alpha", Start, TimeSpan.FromHours(6));
        Assert.NotEmpty(first);

        // Charlie joins the collection; Alpha carries on where it was.
        before.GlobalCollections["Kids"].Include.Add(Show("Charlie"));
        List<int> after = await EpisodesAsync(engine, before, "Alpha", Start.AddHours(6), TimeSpan.FromHours(6));

        Assert.NotEmpty(after);
        Assert.Equal(first.Max() + 1, after[0]);
    }

    [Fact]
    public async Task Editing_a_selectors_own_shows_carries_their_places_over()
    {
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), new InMemoryPlaybackStateStore());
        // The rotation names its shows itself, so its key does change when they're edited.
        var rotation = new ContentSelector { Include = [Show("Alpha"), Show("Bravo")], Order = PlaybackOrder.Chronological, TitleOrder = TitleOrder.Shuffle };
        SchedulerConfig config = Config(Channel(rotation));

        List<int> first = await EpisodesAsync(engine, config, "Bravo", Start, TimeSpan.FromHours(6));
        Assert.NotEmpty(first);

        rotation.Include.Add(Show("Charlie"));
        List<int> after = await EpisodesAsync(engine, config, "Bravo", Start.AddHours(6), TimeSpan.FromHours(6));

        Assert.NotEmpty(after);
        Assert.Equal(first.Max() + 1, after[0]);
    }

    [Fact]
    public async Task An_unrelated_rotation_on_the_channel_is_not_taken_for_a_new_one()
    {
        var store = new InMemoryPlaybackStateStore();
        var engine = new SchedulingEngine(new FakeMediaLibrary(Library()), store);
        SchedulerConfig alpha = Config(Channel(new ContentSelector { Include = [Show("Alpha")], Order = PlaybackOrder.Chronological, TitleOrder = TitleOrder.Shuffle }));
        await EpisodesAsync(engine, alpha, "Alpha", Start, TimeSpan.FromHours(6));

        // A rotation of other shows starts each from its first episode.
        SchedulerConfig others = Config(Channel(new ContentSelector { Include = [Show("Bravo"), Show("Charlie")], Order = PlaybackOrder.Chronological, TitleOrder = TitleOrder.Shuffle }));
        List<int> bravo = await EpisodesAsync(engine, others, "Bravo", Start.AddHours(6), TimeSpan.FromHours(6));
        Assert.Equal(1, bravo[0]);
    }
}
