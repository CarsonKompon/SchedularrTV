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

    private GuideSettings Settings()
    {
        IConfiguration app = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ErsatzTv:Lineup:Port"] = "8409", ["Server:Port"] = "5252" })
            .Build();
        return new GuideSettings(new FakeContext(app, _folder));
    }

    [Fact]
    public void Commercials_play_from_the_window_feed_and_can_be_heard()
    {
        var guide = new GuideStation { Id = "guide", Top = TopPanel.Ads, Sound = GuideSound.Window };

        LiveStreamSpec spec = Settings().Spec(guide);

        Assert.Equal("http://localhost:5252/plugins/tv-guide/feed/guide.ts", spec.WindowStreamUrl);
        Assert.Equal(guide.Window, spec.Window);
        Assert.Equal(LiveAudio.Stream, spec.Audio);
        Assert.Equal(spec.WindowStreamUrl, spec.AudioStreamUrl);
    }

    [Fact]
    public void Commercials_under_music_keep_the_music()
    {
        LiveStreamSpec spec = Settings().Spec(new GuideStation { Id = "guide", Top = TopPanel.Ads, Sound = GuideSound.Music, MusicFolder = "D:/Music" });

        Assert.Equal("http://localhost:5252/plugins/tv-guide/feed/guide.ts", spec.WindowStreamUrl);
        Assert.Equal(LiveAudio.Music, spec.Audio);
        Assert.Null(spec.AudioStreamUrl);
    }

    [Fact]
    public void Cycling_channels_play_from_the_feed_and_the_window_sound_follows_them()
    {
        var guide = new GuideStation { Id = "guide", Top = TopPanel.Channel, CycleChannels = true, WindowChannel = 3, Sound = GuideSound.Channel };

        LiveStreamSpec spec = Settings().Spec(guide);

        Assert.Equal("http://localhost:5252/plugins/tv-guide/feed/guide.ts", spec.WindowStreamUrl);
        Assert.Equal(spec.WindowStreamUrl, spec.AudioStreamUrl);

        guide.SoundChannel = 7;
        Assert.Equal("http://localhost:8409/channel/7.m3u8", Settings().Spec(guide).AudioStreamUrl);
    }

    [Fact]
    public void The_top_panels_sound_without_a_window_is_music()
    {
        LiveStreamSpec spec = Settings().Spec(new GuideStation { Id = "guide", Top = TopPanel.Info, Sound = GuideSound.Window });

        Assert.Null(spec.WindowStreamUrl);
        Assert.Equal(LiveAudio.Music, spec.Audio);
    }

    [Fact]
    public void Cycling_picks_from_the_chosen_channels_that_are_published_else_every_listed_one()
    {
        var guide = new GuideStation { HiddenChannels = [1] };
        Assert.Equal([2, 3], WindowFeed.Candidates(guide, [3, 1, 2]));

        guide.CycleChannelNumbers = [3, 9];
        Assert.Equal([3], WindowFeed.Candidates(guide, [3, 1, 2]));
    }

    [Fact]
    public void Cycling_never_picks_the_same_channel_twice_in_a_row_when_there_is_another()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            Assert.NotEqual(2, WindowFeed.Pick([2, 5], avoid: 2));
        }

        Assert.Equal(4, WindowFeed.Pick([4], avoid: 4));
    }

    [Fact]
    public void Shuffled_commercials_dont_start_with_the_last_one_played()
    {
        GuideAd[] ads = [new("A", "a.mp4", TimeSpan.FromSeconds(30)), new("B", "b.mp4", TimeSpan.FromSeconds(30))];

        for (int attempt = 0; attempt < 50; attempt++)
        {
            IReadOnlyList<GuideAd> shuffled = WindowFeed.Shuffle(ads, last: "a.mp4");
            Assert.Equal(2, shuffled.Count);
            Assert.Equal("b.mp4", shuffled[0].File);
        }
    }

    [Fact]
    public void Each_feed_segment_is_normalised_and_stamped_on_the_running_clock()
    {
        IReadOnlyList<string> ad = SequenceFeed.Arguments(new FeedSegment("ad.mp4", TimeSpan.FromSeconds(30), Realtime: true), 496, 280, TimeSpan.FromSeconds(95.5));
        IReadOnlyList<string> silent = SequenceFeed.Arguments(new FeedSegment("quiet.mp4", TimeSpan.FromSeconds(15), Realtime: true, HasAudio: false), 496, 280, TimeSpan.Zero);
        IReadOnlyList<string> channel = SequenceFeed.Arguments(new FeedSegment("http://localhost:8409/channel/3.m3u8", TimeSpan.FromMinutes(5), Realtime: false), 496, 280, TimeSpan.Zero);

        Assert.Equal("-re", ad[4]);
        Assert.Equal("95.5", ad[ad.ToList().IndexOf("-output_ts_offset") + 1]);
        Assert.Equal("30", ad[ad.ToList().IndexOf("-t") + 1]);
        Assert.Contains(ad, argument => argument.StartsWith("scale=496:280", StringComparison.Ordinal));
        Assert.Equal("0:a:0", ad[ad.ToList().IndexOf("0:v:0") + 2]);

        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", silent);
        Assert.Equal("1:a:0", silent[silent.ToList().IndexOf("0:v:0") + 2]);

        Assert.DoesNotContain("-re", channel);
        Assert.Equal("300", channel[channel.ToList().IndexOf("-t") + 1]);
        Assert.Equal("pipe:1", channel[^1]);
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
