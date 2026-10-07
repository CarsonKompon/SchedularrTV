using System.Text;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.LiveCapture;
using SchedularrTV.Plugins;
using SchedularrTV.Providers.Jellyfin;
using SchedularrTV.Radio;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class RadioTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "radio-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private RadioSettings Settings() =>
        new(new FakeContext(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5252" }).Build(), _folder));

    // --- Lyrics ----------------------------------------------------------------------------------

    [Fact]
    public void Timed_lyrics_are_read_in_order_with_repeated_lines_at_each_time()
    {
        IReadOnlyList<LyricLine> lines = Lyrics.Parse("""
            [ar:Someone]
            [00:12.50]First line
            [00:20.1][01:05.00]Chorus
            [00:15]Second line
            """);

        Assert.Equal([12500L, 15000L, 20100L, 65000L], lines.Select(line => line.At!.Value));
        Assert.Equal(["First line", "Second line", "Chorus", "Chorus"], lines.Select(line => line.Text));
    }

    [Fact]
    public void Plain_lyrics_keep_their_lines_untimed()
    {
        IReadOnlyList<LyricLine> lines = Lyrics.Parse("\n\nOne\n\nTwo\n\n");

        Assert.Equal(["One", "", "Two"], lines.Select(line => line.Text));
        Assert.All(lines, line => Assert.Null(line.At));
        Assert.Empty(Lyrics.Parse("   "));
    }

    // --- Internet radio --------------------------------------------------------------------------

    [Fact]
    public void Stream_titles_are_read_and_split_into_artist_and_title()
    {
        Assert.Equal("Daft Punk - One More Time", StreamTitle.FromMetadata("StreamTitle='Daft Punk - One More Time';StreamUrl='';"));
        Assert.Null(StreamTitle.FromMetadata("StreamUrl='x';"));
        Assert.Equal(("Daft Punk", "One More Time - Radio Edit"), StreamTitle.Split("Daft Punk - One More Time - Radio Edit"));
        Assert.Equal((string.Empty, "Station ID"), StreamTitle.Split("Station ID"));
    }

    [Fact]
    public void Icy_metadata_is_taken_out_of_the_audio_even_across_reads()
    {
        // Every 4 bytes of audio, a metadata block: a length byte (in 16s) and the text, zero-padded.
        byte[] meta = Encoding.UTF8.GetBytes("StreamTitle='A - B';");
        byte[] block = new byte[32];
        meta.CopyTo(block, 0);
        byte[] stream = [1, 2, 3, 4, 2, .. block, 5, 6, 7, 8, 0, 9, 10];

        var reader = new IcyReader(4);
        List<IcyChunk> chunks = [];
        foreach (byte[] piece in new[] { stream[..3], stream[3..20], stream[20..] })
        {
            chunks.AddRange(reader.Read(piece));
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, chunks.Where(chunk => chunk.Audio is not null).SelectMany(chunk => chunk.Audio!).ToArray());
        Assert.Equal(["A - B"], chunks.Where(chunk => chunk.Title is not null).Select(chunk => chunk.Title!));
    }

    [Fact]
    public void Without_an_interval_everything_is_audio()
    {
        IcyChunk only = Assert.Single(new IcyReader(0).Read([1, 2, 3]));
        Assert.Equal(new byte[] { 1, 2, 3 }, only.Audio);
    }

    [Fact]
    public void Station_playlists_give_their_first_stream()
    {
        Assert.Equal("http://radio.test/live.mp3", StreamUrls.FromPlaylist("[playlist]\nNumberOfEntries=2\nFile1=http://radio.test/live.mp3\nTitle1=Live\nFile2=http://radio.test/b.mp3"));
        Assert.Equal("https://radio.test/stream", StreamUrls.FromPlaylist("#EXTM3U\n#EXTINF:-1,Radio\nhttps://radio.test/stream\n"));
        Assert.Equal("http://radio.test/folder/live.aac", StreamUrls.FromPlaylist("live.aac", new Uri("http://radio.test/folder/list.m3u")));
        Assert.Null(StreamUrls.FromPlaylist("#EXTM3U\n"));
        Assert.True(StreamUrls.IsHls("https://radio.test/live/master.m3u8?token=1"));
        Assert.False(StreamUrls.IsHls("https://radio.test/live.mp3"));
    }

    // --- Songs -----------------------------------------------------------------------------------

    [Fact]
    public void Library_songs_show_their_album_cover_through_the_plugin()
    {
        var track = new JellyfinTrack("t1", "Song", ["A", "B"], "Album", "al1", "A", TimeSpan.FromMinutes(3), 1999, ["Pop"]);

        SongInfo song = RadioSound.Library(track, [new LyricLine("Hi", 1000)]);

        Assert.Equal("A, B", song.Artist);
        Assert.Equal("/plugins/radio/art/al1", song.Art);
        Assert.Equal(180_000, song.DurationMs);
        Assert.True(song.Synced);
    }

    [Fact]
    public void Each_song_is_read_at_its_own_speed_with_the_key_in_a_header_and_stamped_on_the_clock()
    {
        List<string> arguments = RadioSound.SongArguments("http://jellyfin.test/Audio/t1/stream?static=true", "Authorization: MediaBrowser Token=\"k\"",
            TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(42.5));

        Assert.Contains("-re", arguments);
        Assert.Equal("Authorization: MediaBrowser Token=\"k\"\r\n", arguments[arguments.IndexOf("-headers") + 1]);
        Assert.True(arguments.IndexOf("-headers") < arguments.IndexOf("-i"));
        Assert.DoesNotContain(arguments, argument => argument.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("201", arguments[arguments.IndexOf("-t") + 1]);
        Assert.Equal("42.5", arguments[arguments.IndexOf("-output_ts_offset") + 1]);
        Assert.Equal("pipe:1", arguments[^1]);
    }

    // --- Stream ----------------------------------------------------------------------------------

    [Fact]
    public void A_station_streams_its_page_with_its_sound_and_the_visualizer_drawn_from_it()
    {
        var station = new RadioStation { Id = "late", Name = "Late Night", Visualizer = RadioVisualizer.Bars, AccentColor = "#00ff88" };

        LiveStreamSpec spec = Settings().Spec(station);
        IReadOnlyList<string> arguments = LiveStream.Arguments(spec, null);
        string graph = arguments[arguments.ToList().IndexOf("-filter_complex") + 1];

        Assert.Equal("http://localhost:5252/plugins/radio/view/late", spec.PageUrl);
        Assert.Equal(LiveAudio.Stream, spec.Audio);
        Assert.Equal("http://localhost:5252/plugins/radio/sound/late.ts", spec.AudioStreamUrl);
        Assert.Contains("asplit=2[a][drawn]", graph, StringComparison.Ordinal);
        Assert.Contains("showfreqs=s=1280x130", graph, StringComparison.Ordinal);
        Assert.Contains("colors=0x00ff88", graph, StringComparison.Ordinal);
        Assert.Contains("overlay=0:590", graph, StringComparison.Ordinal);
        Assert.EndsWith("[a][drawn]", graph.Split(';').Single(part => part.Contains("asplit", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_visualizer_the_sound_is_only_heard()
    {
        var station = new RadioStation { Id = "late", Visualizer = RadioVisualizer.None };

        LiveStreamSpec spec = Settings().Spec(station);
        List<string> arguments = [.. LiveStream.Arguments(spec, null)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        Assert.Null(spec.Visualizer);
        Assert.DoesNotContain("asplit", graph, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RadioVisualizer.Wave, "showwaves=")]
    [InlineData(RadioVisualizer.Spectrum, "showcqt=")]
    [InlineData(RadioVisualizer.Scope, "avectorscope=")]
    public void Each_visualizer_has_its_filter(RadioVisualizer visualizer, string filter)
    {
        FrameRect rect = RadioLayout.VisualizerRect(RadioPreset.NowPlaying, visualizer);
        Assert.StartsWith(filter, RadioLayout.Filter(visualizer, rect, "#ff5c8a"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bigger_visualizer_grows_up_from_its_strip_and_stays_on_the_frame()
    {
        FrameRect normal = RadioLayout.VisualizerRect(RadioPreset.NowPlaying, RadioVisualizer.Bars);
        FrameRect big = RadioLayout.VisualizerRect(RadioPreset.NowPlaying, RadioVisualizer.Bars, 1.5);
        FrameRect wide = RadioLayout.VisualizerRect(RadioPreset.Minimal, RadioVisualizer.Bars, 1.5);
        FrameRect meter = RadioLayout.VisualizerRect(RadioPreset.RetroFm, RadioVisualizer.Bars, 0.5);

        Assert.Equal(normal.Y + normal.Height, big.Y + big.Height);
        Assert.Equal(196, big.Height);
        Assert.All(new[] { big, wide, meter }, rect =>
        {
            Assert.InRange(rect.X, 0, 1280 - rect.Width);
            Assert.InRange(rect.Y, 0, 720 - rect.Height);
            Assert.Equal(0, rect.Width % 2);
            Assert.Equal(0, rect.Height % 2);
        });
        // Retro FM's meter shrinks about its middle.
        Assert.InRange(meter.Y + meter.Height / 2, 300 + 220 / 2 - 2, 300 + 220 / 2 + 2);
    }

    [Fact]
    public void Intensity_turns_up_only_the_sound_the_visualizer_is_drawn_from()
    {
        FrameRect rect = RadioLayout.VisualizerRect(RadioPreset.NowPlaying, RadioVisualizer.Bars);

        Assert.StartsWith("showfreqs=", RadioLayout.Filter(RadioVisualizer.Bars, rect, "#ff5c8a"), StringComparison.Ordinal);
        Assert.StartsWith("volume=2,showfreqs=", RadioLayout.Filter(RadioVisualizer.Bars, rect, "#ff5c8a", intensity: 2), StringComparison.Ordinal);
        Assert.Contains("bar_g=4", RadioLayout.Filter(RadioVisualizer.Spectrum, rect, "#ff5c8a", intensity: 2), StringComparison.Ordinal);
        Assert.Contains("colors=0x00ff00", RadioLayout.Filter(RadioVisualizer.Bars, rect, "#00ff00"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_scope_is_square_and_centred_in_the_presets_strip()
    {
        FrameRect strip = RadioLayout.VisualizerRect(RadioPreset.NowPlaying, RadioVisualizer.Bars);
        FrameRect scope = RadioLayout.VisualizerRect(RadioPreset.NowPlaying, RadioVisualizer.Scope);

        Assert.Equal(scope.Width, scope.Height);
        Assert.Equal(strip.Y, scope.Y);
        Assert.InRange(scope.X + scope.Width / 2, strip.X + strip.Width / 2 - 2, strip.X + strip.Width / 2 + 2);
    }

    [Fact]
    public void Saving_keeps_only_real_colours_and_tidies_the_music_choice()
    {
        var store = new RadioStore(_folder);
        store.Save([new RadioStation
        {
            Name = "Late Night", AccentColor = "red;}</style>", Genres = ["Jazz", " jazz ", ""], LyricsOffset = 99, Message = "  "
        }]);

        RadioStation saved = Assert.Single(new RadioStore(_folder).Stations);
        Assert.Equal("late-night", saved.Id);
        Assert.Equal("#ff5c8a", saved.AccentColor);
        Assert.Equal(["Jazz"], saved.Genres);
        Assert.Equal(30, saved.LyricsOffset);
        Assert.Null(saved.Message);
    }

    [Fact]
    public void The_page_carries_its_settings_without_letting_them_end_the_script()
    {
        var station = new RadioStation { Id = "late", Name = "</script><script>alert(1)</script>", Preset = RadioPreset.Vinyl };

        string html = RadioPage.Html(RadioPageConfig.For(station, preview: false));

        Assert.DoesNotContain("__CONFIG__", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script><script>alert", html, StringComparison.Ordinal);
        Assert.Contains("\"preset\":\"Vinyl\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_station_is_a_show_with_an_episode_per_length()
    {
        MediaItem item = RadioLibrary.Item(new RadioStation { Id = "late", Name = "Late Night" }, 30, "http://localhost:5252/plugins/radio/live/late.ts");

        Assert.Equal(("radio:late:30", "Late Night", "Music (30 min)"), (item.Id, item.SeriesName, item.Title));
        Assert.Equal("true", item.Attributes["ersatztv.live"]);
    }

    private sealed class FakeContext(IConfiguration app, string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "radio", Name = "Radio channels" };
        public string? PluginDirectory => null;
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => new ConfigurationBuilder().Build();
        public IConfiguration AppConfiguration => app;
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/radio/";
    }
}
