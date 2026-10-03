using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.LiveCapture;
using SchedularrTV.Plugins;
using SchedularrTV.TvGuide;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class TvGuideTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tv-guide-" + Guid.NewGuid().ToString("N"));

    public TvGuideTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly DateTimeOffset Eight = new(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_video_window_is_a_16_by_9_box_on_the_left_of_the_top_panel_and_only_when_something_plays_in_it()
    {
        var guide = new GuideStation { Top = TopPanel.Channel, TopPercent = 45 };

        FrameRect window = guide.Window!;

        Assert.Equal(324, guide.TopHeight);
        Assert.Equal((22, 22, 280), (window.X, window.Y, window.Height));
        Assert.Equal(window.Height * 16 / 9 / 2 * 2, window.Width);
        Assert.Equal(0, window.Width % 2);
        Assert.Null(new GuideStation { Top = TopPanel.Info }.Window);
        Assert.Null(new GuideStation { Top = TopPanel.Ads, TopPercent = 10 }.Window);
    }

    [Fact]
    public void Saving_tidies_values_and_keeps_only_real_colours()
    {
        var store = new GuideStore(_folder);
        store.Save([new GuideStation
        {
            Name = "Prevue", ScrollSpeed = 9000, TopPercent = 99, Background = "red;}</style>", AccentColor = "#ffd400", HiddenChannels = [9, 9, 1]
        }]);

        GuideStation saved = Assert.Single(new GuideStore(_folder).Guides);
        Assert.Equal("prevue", saved.Id);
        Assert.Equal(300, saved.ScrollSpeed);
        Assert.Equal(70, saved.TopPercent);
        Assert.Null(saved.Background);
        Assert.Equal("#ffd400", saved.AccentColor);
        Assert.Equal([1, 9], saved.HiddenChannels);
    }

    [Fact]
    public void The_page_carries_its_settings_without_letting_them_end_the_script()
    {
        var guide = new GuideStation { Id = "guide", Name = "</script><script>alert(1)</script>", Theme = GuideTheme.TvGuide };

        string html = GuidePage.Html(GuidePageConfig.For(guide, preview: false));

        Assert.DoesNotContain("__CONFIG__", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script><script>alert", html, StringComparison.Ordinal);
        Assert.Contains("\"theme\":\"TvGuide\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Listings_are_one_row_per_channel_with_programmes_titled_by_show()
    {
        ScheduleEntry Programme(int minute, int length, string series, string episode) => new()
        {
            Start = Eight.AddMinutes(minute), Duration = TimeSpan.FromMinutes(length), Kind = ScheduleEntryKind.Program,
            Item = TestMedia.Episode(series, 1, minute + 1, TimeSpan.FromMinutes(length), title: episode)
        };

        var schedule = new ChannelSchedule
        {
            ChannelId = "toons", ChannelName = "Toons", ChannelNumber = 5, ChannelLogo = "logos/toons.png",
            Start = Eight, End = Eight.AddHours(4),
            Entries = [Programme(0, 30, "SpongeBob", "Help Wanted"), Programme(30, 30, "Rugrats", "Tommy's First Birthday"), Programme(240, 30, "Late", "Too late")]
        };

        GuideRow row = Assert.Single(GuideData.Rows([schedule], Eight.AddMinutes(10), TimeSpan.FromHours(2)));

        Assert.Equal((5, "Toons", "/logos/toons.png"), (row.Number, row.Name, row.Logo));
        Assert.Equal(["SpongeBob", "Rugrats"], row.Listings.Select(listing => listing.Title));
        Assert.Equal("Help Wanted", row.Listings[0].Subtitle);
    }

    [Fact]
    public void A_guide_streams_its_page_with_the_chosen_channel_in_the_window_and_that_channels_sound()
    {
        IConfiguration app = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ErsatzTv:Lineup:Port"] = "8409", ["Server:Port"] = "5252" })
            .Build();
        var settings = new GuideSettings(new FakeContext(app, _folder));
        var guide = new GuideStation { Id = "guide", Name = "Guide", Top = TopPanel.Channel, WindowChannel = 3, Sound = GuideSound.Channel };

        LiveStreamSpec spec = settings.Spec(guide);
        IReadOnlyList<string> arguments = LiveStream.Arguments(spec, null);

        Assert.Equal("http://localhost:5252/plugins/tv-guide/view/guide", spec.PageUrl);
        Assert.Equal("http://localhost:8409/channel/3.m3u8", spec.WindowStreamUrl);
        Assert.Equal(LiveAudio.Stream, spec.Audio);
        Assert.Equal(spec.WindowStreamUrl, spec.AudioStreamUrl);

        // One input for the channel, used for both its picture and its sound.
        Assert.Single(arguments, argument => argument == spec.WindowStreamUrl);
        string graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains($"overlay={guide.Window!.X}:{guide.Window.Y}", graph, StringComparison.Ordinal);
        Assert.Contains("[1:a]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_window_or_music_the_stream_is_the_page_and_silence()
    {
        var spec = new LiveStreamSpec { Id = "g", Name = "G", PageUrl = "http://localhost/page", Ffmpeg = "ffmpeg", Audio = LiveAudio.Music };

        IReadOnlyList<string> arguments = LiveStream.Arguments(spec, playlist: null);

        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("overlay", StringComparison.Ordinal));
        Assert.Equal("pipe:1", arguments[^1]);
    }

    [Fact]
    public void Each_guide_is_a_show_with_an_episode_per_length_pointing_at_its_live_stream()
    {
        MediaItem item = GuideLibrary.Item(new GuideStation { Id = "guide", Name = "Program Guide" }, 30, "http://localhost:5252/plugins/tv-guide/live/guide.ts");

        Assert.Equal(("tv-guide:guide:30", "Program Guide", "Listings (30 min)"), (item.Id, item.SeriesName, item.Title));
        Assert.Equal("true", item.Attributes["ersatztv.live"]);
        Assert.Contains("mpegts", item.Attributes["ersatztv.probe_hint"], StringComparison.Ordinal);
    }

    private sealed class FakeContext(IConfiguration app, string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "tv-guide", Name = "TV Guide channel" };
        public string? PluginDirectory => null;
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => new ConfigurationBuilder().Build();
        public IConfiguration AppConfiguration => app;
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/tv-guide/";
    }
}
