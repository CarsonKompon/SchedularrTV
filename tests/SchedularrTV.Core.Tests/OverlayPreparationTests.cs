using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Images;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Service.Images;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class OverlayPreparationTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overlay-preparation-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.Delete(_folder);
    private static ScheduleEntry Entry(string title, DateTimeOffset start, double minutes = 20) => new()
    {
        Start = start, Duration = TimeSpan.FromMinutes(minutes), Kind = ScheduleEntryKind.Program,
        Item = TestMedia.Episode(title, 1, 1, TimeSpan.FromMinutes(minutes))
    };

    [Fact]
    public void Defaults_are_watched_only_and_one_hour_and_settings_are_read_live()
    {
        Dictionary<string, string?> values = [];
        var settings = new OverlayPreparationSettings(key => values.GetValueOrDefault(key));
        var viewers = new Viewers { Watching = false };
        ScheduleEntry entry = Entry("Show", Noon.AddMinutes(30));
        Assert.True(settings.WatchedOnly);
        Assert.Equal(TimeSpan.FromHours(1), settings.LeadTime);
        Assert.False(settings.MayPrepare(7, entry, new(null), Noon, viewers));
        viewers.Watching = true;
        Assert.True(settings.MayPrepare(7, entry, new(null), Noon, viewers));
        Assert.Equal(7, viewers.Channel);
        Assert.False(settings.MayPrepare(7, Entry("Later", Noon.AddMinutes(61)), new(null), Noon, viewers));
        values["LeadTimeMinutes"] = "120";
        Assert.True(settings.MayPrepare(7, Entry("Later", Noon.AddMinutes(61)), new(null), Noon, viewers));
        values["WatchedOnly"] = "false";
        viewers.Watching = false;
        Assert.True(settings.MayPrepare(7, entry, new(null), Noon, viewers));
        values["LeadTimeMinutes"] = "0";
        Assert.True(settings.MayPrepare(7, Entry("Tomorrow", Noon.AddDays(1)), new(null), Noon, viewers));
        Assert.False(settings.MayPrepare(7, Entry("Ended", Noon.AddHours(-1)), new(null), Noon, viewers));
        values["LeadTimeMinutes"] = "NaN";
        Assert.Equal(TimeSpan.FromHours(1), settings.LeadTime);
    }

    [Fact]
    public void Unknown_viewer_status_matches_generated_promos_and_allows_preparation()
    {
        var settings = new OverlayPreparationSettings(_ => null);
        Assert.True(settings.MayPrepare(1, Entry("Now", Noon), new(null), Noon, null));
        Assert.True(settings.MayPrepare(1, Entry("Now", Noon), new(null), Noon, new Viewers()));
    }

    [Fact]
    public void Lead_time_uses_the_overlay_appearance_including_late_slots_and_currently_visible_slots()
    {
        var settings = new OverlayPreparationSettings(_ => null);
        var viewers = new Viewers { Watching = true };
        var entry = Entry("Long movie", Noon, 180);
        var overlay = new Overlay { When = OverlayWhen.Start, OffsetSeconds = 90 * 60, DurationSeconds = 10, FadeSeconds = 0 };
        OverlayShowing showing = OverlayTiming.For(overlay, entry)!;
        Assert.False(settings.MayPrepare(7, entry, showing, Noon, viewers));
        Assert.True(settings.MayPrepare(7, entry, showing, Noon.AddMinutes(30), viewers));
        Assert.True(settings.MayPrepare(7, entry, showing, Noon.AddMinutes(90).AddSeconds(5), viewers));
        Assert.False(settings.MayPrepare(7, entry, showing, Noon.AddMinutes(91), viewers));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeating_content_and_wall_clock_appearances_use_the_next_cycle(bool wall)
    {
        var entry = Entry("Long movie", Noon, 180);
        var showing = new OverlayShowing(new(wall, 30 * 60 * 1000, 0, 0, 10_000));
        Assert.Equal(Noon.AddMinutes(30), OverlayPreparationSettings.NextAppearance(entry, showing, Noon.AddMinutes(1)));
        Assert.Equal(Noon.AddSeconds(5), OverlayPreparationSettings.NextAppearance(entry, showing, Noon.AddSeconds(5)));
    }

    [Fact]
    public async Task Three_variable_queries_wait_for_a_viewer_and_then_prepare_only_the_near_overlay()
    {
        var search = new Search();
        var images = new AutomaticImages([search], null, Path.Combine(_folder, "images"));
        var viewers = new Viewers { Watching = false };
        var settings = new OverlayPreparationSettings(_ => null);
        var overlay = new Overlay
        {
            VariableImages =
            [
                new() { Name = "current", Context = GeneratedImageContext.CurrentlyWatching, Automatic = new() { Query = "{show}" } },
                new() { Name = "next", Context = GeneratedImageContext.UpNext, Automatic = new() { Query = "{show}" } },
                new() { Name = "later", Context = GeneratedImageContext.UpNext, Offset = 1, Automatic = new() { Query = "{show}" } }
            ],
            Layers = [.. new[] { "current", "next", "later" }.Select(name => new OverlayLayer { Kind = OverlayLayerKinds.Image, VariableImage = name })]
        };
        var channel = new ChannelConfig { Id = "channel", Name = "Channel", Number = 7,
            Plugins = new() { [OverlayResolver.PluginId] = new OverlaysSection { Overlays = [overlay] } } };
        ScheduleEntry[] entries = [Entry("A", Noon.AddMinutes(10)), Entry("B", Noon.AddHours(2)), Entry("C", Noon.AddHours(3)), Entry("D", Noon.AddHours(4))];
        var context = new PlayoutContext(new() { Channels = [channel] }, channel,
            new ChannelSchedule { ChannelId = channel.Id, ChannelName = channel.Name, ChannelNumber = 7, Start = Noon, End = Noon.AddHours(5), Entries = entries });
        var renderer = new OverlayRenderer(Path.Combine(_folder, "renders"));
        var processor = new OverlayProcessor(renderer, NullLogger<OverlayProcessor>.Instance, automaticImages: images,
            preparation: settings, viewers: viewers, timeProvider: new Clock(Noon));
        await processor.ProcessAsync(context, CancellationToken.None);
        Assert.Empty(search.Queries);
        Assert.False(Directory.Exists(renderer.Folder));
        viewers.Watching = true;
        await processor.ProcessAsync(context, CancellationToken.None);
        await search.Three.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(new[] { "A", "B", "C" }.Select(query => images.GetAsync(new(query, 1, false, null))));
        Assert.Equal(["A", "B", "C"], search.Queries.Order()); // B/C are shown by the near overlay, despite airing later themselves.
        Assert.DoesNotContain("D", search.Queries);
        Assert.NotEmpty(Directory.GetFiles(renderer.Folder, "*.png"));
        viewers.Watching = false;
        ChannelSchedule reused = await processor.ProcessAsync(context, CancellationToken.None);
        Assert.NotNull(reused.Entries[0].Annotations); // Cached graphics remain available while not watched.
    }

    [Fact]
    public void Monitor_republishes_after_tuning_in_or_entering_the_lead_window()
    {
        var settings = new OverlayPreparationSettings(_ => null);
        var clock = new Clock(Noon);
        var viewers = new Viewers { Watching = false };
        var republisher = new Republisher();
        using var services = new ServiceCollection().AddSingleton<IChannelViewers>(viewers).AddSingleton<IScheduleRepublisher>(republisher).BuildServiceProvider();
        using var monitor = new OverlayPreparationMonitor(settings, services, clock);
        monitor.Track(7, [new(Entry("Soon", Noon.AddMinutes(30)), new(null))]);
        monitor.Check();
        Assert.Equal(0, republisher.Calls);
        viewers.Watching = true;
        monitor.Check();
        Assert.Equal(1, republisher.Calls);
        monitor.Track(7, [new(Entry("Later", Noon.AddMinutes(90)), new(null))]);
        monitor.Check();
        Assert.Equal(1, republisher.Calls);
        clock.Now = Noon.AddMinutes(31);
        monitor.Check();
        Assert.Equal(2, republisher.Calls);
        monitor.Track(7, []);
        monitor.Check();
        Assert.Equal(2, republisher.Calls);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Viewers : IChannelViewers
    {
        public bool? Watching;
        public int Channel;
        public bool? IsWatched(int channelNumber) { Channel = channelNumber; return Watching; }
    }
    private sealed class Republisher : IScheduleRepublisher
    {
        public int Calls;
        public void RequestRepublish() => Calls++;
    }
    private sealed class Search : IImageSearchProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public ConcurrentBag<string> Queries { get; } = [];
        public TaskCompletionSource Three { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ImageSearchResult>> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
        {
            Queries.Add(request.Query);
            if (Queries.Count == 3) Three.TrySetResult();
            return Task.FromResult<IReadOnlyList<ImageSearchResult>>([]);
        }
    }
}
