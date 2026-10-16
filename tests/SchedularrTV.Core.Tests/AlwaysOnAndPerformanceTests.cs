using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Service;
using SchedularrTV.Service.Editor;
using SchedularrTV.Service.Performance;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class AlwaysOnAndPerformanceTests
{
    [Fact]
    public void A_channel_is_set_to_play_around_the_clock_in_the_yaml_and_left_out_when_it_isnt()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse("""
            channels:
            - id: news
              name: News
              number: 3
              always_on: true
              always_on_counts_as_watched: false
            - id: movies
              name: Movies
              number: 4
            """);

        Assert.True(config.Channels[0].AlwaysOn);
        Assert.False(config.Channels[0].AlwaysOnCountsAsWatched);
        Assert.Null(config.Channels[1].AlwaysOn);

        string yaml = SchedulerConfigLoader.Serialize(config);
        Assert.Contains("always_on: true", yaml, StringComparison.Ordinal);
        Assert.Single(yaml.Split('\n'), line => line.Contains("always_on:", StringComparison.Ordinal));
    }

    [Fact]
    public void Always_on_channels_count_as_watched_unless_set_otherwise()
    {
        var config = new SchedulerConfig
        {
            Channels =
            [
                new() { Id = "a", Name = "A", Number = 1, AlwaysOn = true },
                new() { Id = "b", Name = "B", Number = 2, AlwaysOn = true, AlwaysOnCountsAsWatched = false },
                new() { Id = "c", Name = "C", Number = 3, AlwaysOn = false },
                new() { Id = "d", Name = "D", Number = 4 }
            ]
        };

        IReadOnlyDictionary<int, bool> wanted = AlwaysOnKeeper.Wanted(config);

        Assert.Equal([1, 2], wanted.Keys.Order());
        Assert.True(wanted[1]);
        Assert.False(wanted[2]);
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nhttp://127.0.0.1:8409/session/5/live.m3u8\n", "http://127.0.0.1:8409/session/5/live.m3u8")]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n/session/5/live.m3u8?x=1\n", "http://127.0.0.1:8409/session/5/live.m3u8?x=1")]
    [InlineData("#EXTM3U\n#EXTINF:6,\nlive001.ts\n", null)]
    public void The_session_playlist_is_found_in_the_channel_playlist(string playlist, string? expected)
    {
        Uri? next = AlwaysOnKeeper.NextPlaylist(playlist, new Uri("http://127.0.0.1:8409/channel/5.m3u8"));
        Assert.Equal(expected, next?.ToString());
    }

    [Fact]
    public void An_always_on_channel_is_watched_or_not_by_its_setting_rather_than_its_heartbeat()
    {
        string root = Path.Combine(Path.GetTempPath(), $"etv-always-on-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "hls", "5"));
            File.WriteAllText(Path.Combine(root, "lineup.json"), """{ "output": { "folder": "hls" } }""");
            File.WriteAllText(Path.Combine(root, "hls", "5", ".heartbeat"), "");

            var alwaysOn = new AlwaysOnChannels();
            var monitor = new ErsatzTvViewerMonitor(
                Microsoft.Extensions.Options.Options.Create(new ErsatzTvOptions { ConfigRoot = root }),
                TimeProvider.System, NullLogger<ErsatzTvViewerMonitor>.Instance, alwaysOn);

            Assert.True(monitor.IsWatched(5));

            alwaysOn.Set([KeyValuePair.Create(5, false), KeyValuePair.Create(6, true)]);
            Assert.False(monitor.IsWatched(5));
            Assert.True(monitor.IsWatched(6));
            Assert.False(monitor.IsWatched(7));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_proc_stat_line_gives_the_name_and_parent_even_with_spaces_and_brackets_in_the_name()
    {
        Assert.Equal(("ffmpeg", 42), ProcessTable.ParseStat("1234 (ffmpeg) S 42 1234 1234 0 -1"));
        Assert.Equal(("Web Content (x)", 7), ProcessTable.ParseStat("99 (Web Content (x)) R 7 99 99 0 -1"));
        Assert.Null(ProcessTable.ParseStat("garbage"));
    }

    [Fact]
    public void Descendants_are_found_through_every_level_and_a_reused_parent_id_is_ignored()
    {
        ProcessEntry[] table =
        [
            new(1, 0, "init"),
            new(10, 1, "schedularrtv"),
            new(11, 10, "ersatztv"),
            new(12, 11, "ersatztv-channel"),
            new(13, 12, "ffmpeg"),
            new(20, 10, "chrome"),
            new(21, 20, "chrome"),
            // Claims 10 as its parent but started before it: 10 is a reused id.
            new(30, 10, "stale"),
            new(40, 1, "unrelated")
        ];
        DateTime origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? Started(int pid) => pid == 30 ? origin : origin.AddMinutes(pid);

        Assert.Equal([10, 11, 12, 13, 20, 21], ProcessTable.Descendants(table, [10], Started).Select(entry => entry.Pid).Order());
        Assert.Equal([11, 12, 13], ProcessTable.Descendants(table, [11], Started).Select(entry => entry.Pid).Order());
        Assert.Empty(ProcessTable.Descendants(table, [999], Started));
    }

    [Theory]
    [InlineData("C:\\Program Files\\ErsatzTV\\ErsatzTV.exe", "ersatztv")]
    [InlineData("ffmpeg", "ffmpeg")]
    [InlineData("/usr/bin/chromium-browser", "chromium-browser")]
    public void Program_names_are_compared_without_folder_extension_or_case(string name, string expected) =>
        Assert.Equal(expected, ProcessTable.Normalize(name));

    [Fact]
    public void System_cpu_and_memory_are_read_from_proc()
    {
        // user nice system idle iowait…: idle is idle + iowait, the total everything.
        Assert.Equal((500UL, 1700UL), SystemUsage.ParseProcStat("cpu  1000 0 200 400 100 0 0 0 0 0"));
        Assert.Null(SystemUsage.ParseProcStat("cpu0 1 2 3 4"));

        (long? used, long? total) = SystemUsage.ParseMeminfo(["MemTotal:       16000000 kB", "MemFree: 1 kB", "MemAvailable:    6000000 kB"]);
        Assert.Equal(16000000L * 1024, total);
        Assert.Equal(10000000L * 1024, used);
    }

    [Fact]
    public void Nvidia_smi_output_is_read_with_and_without_the_encoder_columns()
    {
        GpuUsage full = Assert.Single(GpuReader.Parse("NVIDIA GeForce RTX 3070, 12, 45, 3, 1024, 8192\n", basic: false));
        Assert.Equal("NVIDIA GeForce RTX 3070", full.Name);
        Assert.Equal(12, full.Utilization);
        Assert.Equal(45, full.Encoder);
        Assert.Equal(3, full.Decoder);
        Assert.Equal(1024L * 1024 * 1024, full.MemoryUsedBytes);

        GpuUsage basic = Assert.Single(GpuReader.Parse("Tesla T4, [N/A], 10, 15360\n", basic: true));
        Assert.Null(basic.Utilization);
        Assert.Null(basic.Encoder);
        Assert.Equal(15360L * 1024 * 1024, basic.MemoryTotalBytes);
    }

    [Theory]
    [InlineData("""{ "normalization": { "video": { "accel": null } } }""", true)]
    [InlineData("""{ "normalization": { "video": { } } }""", true)]
    [InlineData("""{ "normalization": { "video": { "accel": "" } } }""", true)]
    [InlineData("""{ "normalization": { "video": { "accel": "nvenc" } } }""", false)]
    [InlineData("not json", false)]
    public void A_channel_without_hardware_acceleration_is_noticed(string json, bool software) =>
        Assert.Equal(software, ResourceMonitor.IsSoftwareEncoded(json));

    [Fact]
    public void A_channel_overrides_the_app_wide_defaults_field_by_field()
    {
        var global = new ErsatzTvEncodingOptions { Hardware = "CUDA", Height = "720", BitrateKbps = "" };

        EffectiveEncoding inherited = ErsatzTvEncoding.Defaults(global);
        Assert.Equal(new EffectiveEncoding("cuda", 720, ErsatzTvEncoding.DefaultBitrateKbps, null, null), inherited);

        EffectiveEncoding own = ErsatzTvEncoding.Resolve(global, new ChannelEncodingConfig { Hardware = "none", BitrateKbps = 3000 });
        Assert.Equal(new EffectiveEncoding("none", 720, 3000, null, null), own);

        // Nothing set anywhere: the defaults, so every channel is always encoded as SchedularrTV says.
        Assert.Equal(new EffectiveEncoding(ErsatzTvEncoding.DefaultHardware, ErsatzTvEncoding.DefaultHeight, ErsatzTvEncoding.DefaultBitrateKbps, null, null),
            ErsatzTvEncoding.Defaults(new ErsatzTvEncodingOptions()));

        // An unknown choice (a typo in the YAML) is ignored rather than handed to ErsatzTV.
        Assert.Equal("cuda", ErsatzTvEncoding.Resolve(global, new ChannelEncodingConfig { Hardware = "nvidia" }).Hardware);
    }

    [Fact]
    public void The_encoding_overlay_is_versioned_and_sets_everything()
    {
        string overlay = ErsatzTvEncoding.Overlay(new EffectiveEncoding("vaapi", 1080, 6000, "/dev/dri/renderD129", "ihd"));
        using var document = System.Text.Json.JsonDocument.Parse(overlay);
        System.Text.Json.JsonElement root = document.RootElement;
        System.Text.Json.JsonElement video = root.GetProperty("normalization").GetProperty("video");

        Assert.Equal(ErsatzTvEncoding.OverlayVersion, root.GetProperty("version").GetString());
        Assert.Equal("vaapi", video.GetProperty("accel").GetString());
        Assert.Equal("transcode", video.GetProperty("mode").GetString());
        Assert.Equal("scale_and_pad", video.GetProperty("scaling_mode").GetString());
        Assert.Equal(1920, video.GetProperty("width").GetInt32());
        Assert.Equal(1080, video.GetProperty("height").GetInt32());
        Assert.Equal(12000, video.GetProperty("buffer_kbps").GetInt32());
        Assert.Equal("ihd", video.GetProperty("vaapi_driver").GetString());

        // The processor is chosen by removing the acceleration channel.json may have, which a null in an overlay does.
        using var software = System.Text.Json.JsonDocument.Parse(ErsatzTvEncoding.Overlay(new EffectiveEncoding("none", 720, 3000, null, null)));
        System.Text.Json.JsonElement softwareVideo = software.RootElement.GetProperty("normalization").GetProperty("video");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, softwareVideo.GetProperty("accel").ValueKind);
        Assert.Equal(1280, softwareVideo.GetProperty("width").GetInt32());
        Assert.False(softwareVideo.TryGetProperty("vaapi_device", out _));

        Assert.NotEqual(ErsatzTvEncoding.FileName(overlay), ErsatzTvEncoding.FileName(software.RootElement.GetRawText()));
    }

    [Fact]
    public void A_channels_own_encoding_reaches_the_lineup_through_its_schedule()
    {
        var config = new SchedulerConfig
        {
            Channels =
            [
                new() { Id = "own", Name = "Own", Number = 1, Encoding = new ChannelEncodingConfig { Hardware = "qsv", Height = 540 } },
                new() { Id = "inherits", Name = "Inherits", Number = 2 }
            ]
        };
        ChannelSchedule Schedule(string id, int number) => new()
        {
            ChannelId = id, ChannelName = id, ChannelNumber = number,
            Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch.AddHours(1), Entries = []
        };

        IReadOnlyList<ChannelSchedule> annotated = ScheduleCoordinator.WithEncoding(config, [Schedule("own", 1), Schedule("inherits", 2)]);

        var global = new ErsatzTvEncodingOptions { Hardware = "cuda", Height = "1080", BitrateKbps = "6000" };
        Assert.Equal(new EffectiveEncoding("qsv", 540, 6000, null, null), ErsatzTvEncoding.Resolve(global, annotated[0]));
        Assert.Equal(new EffectiveEncoding("cuda", 1080, 6000, null, null), ErsatzTvEncoding.Resolve(global, annotated[1]));
        Assert.Null(annotated[1].Annotations);
    }

    [Fact]
    public void A_channel_made_from_one_source_keeps_its_picture_size_and_the_default_encoder()
    {
        var global = new ErsatzTvEncodingOptions { Hardware = "cuda", Height = "1080", BitrateKbps = "6000" };
        ChannelSchedule Schedule(Dictionary<string, string>? annotations) => new()
        {
            ChannelId = "iptv-a-1", ChannelName = "News", ChannelNumber = 1001,
            Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch.AddHours(1), Entries = [], Annotations = annotations
        };

        EffectiveEncoding source = ErsatzTvEncoding.Resolve(global, Schedule(new() { [ErsatzTvEncoding.PictureAnnotation] = "720x577" }));
        Assert.Equal(new EffectiveEncoding("cuda", 576, 6000, null, null, 720), source);
        Assert.Equal(720, source.Width);

        // A size the channel chooses itself still wins over its source's.
        string own = ErsatzTvEncoding.ToAnnotation(new ChannelEncodingConfig { Height = 480 })!;
        EffectiveEncoding chosen = ErsatzTvEncoding.Resolve(global,
            Schedule(new() { [ErsatzTvEncoding.PictureAnnotation] = "1280x720", [ErsatzTvEncoding.Annotation] = own }));
        Assert.Equal(480, chosen.Height);
        Assert.Equal(854, chosen.Width);

        Assert.Null(ErsatzTvEncoding.ParsePicture("garbage"));
        Assert.Equal(1080, ErsatzTvEncoding.Resolve(global, Schedule(new() { [ErsatzTvEncoding.PictureAnnotation] = "10x10" })).Height);
    }

    [Theory]
    [InlineData("  Stream #0:0: Video: h264 (High) ([27][0][0][0] / 0x001B), yuv420p(tv, bt709, progressive), 1280x720 [SAR 1:1 DAR 16:9], q=2-31, 50 fps", "1280x720")]
    [InlineData("  Stream #0:1: Video: mpeg2video (Main), yuv420p(tv, top first), 720x576 [SAR 16:15 DAR 4:3], 25 fps", "720x576")]
    [InlineData("  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp", null)]
    public void The_relays_picture_size_is_read_from_ffmpegs_stream_line(string line, string? size) =>
        Assert.Equal(size, SchedularrTV.Iptv.PictureSizes.Parse(line));

    [Fact]
    public void Only_the_relays_output_stream_gives_the_size_not_the_variants_an_input_lists()
    {
        var watcher = new SchedularrTV.Iptv.RelayHub.OutputSizeWatcher();
        Assert.Null(watcher.Read("Input #0, hls, from 'http://provider.test/master.m3u8':"));
        Assert.Null(watcher.Read("  Stream #0:0: Video: h264 (Main), yuv420p, 640x360, 25 fps"));
        Assert.Null(watcher.Read("  Stream #0:2: Video: h264 (High), yuv420p, 1920x1080, 25 fps"));
        Assert.Null(watcher.Read("Output #0, mpegts, to 'pipe:1':"));
        Assert.Equal("1920x1080", watcher.Read("  Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, q=2-31, 25 fps"));
        Assert.Null(watcher.Read("  Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, q=2-31, 25 fps"));
    }

    [Fact]
    public void A_relayed_size_is_remembered_across_restarts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"iptv-pictures-{Guid.NewGuid():N}.json");
        try
        {
            new SchedularrTV.Iptv.PictureSizes(path).Note("acme/1001", "1280x720");
            Assert.Equal("1280x720", new SchedularrTV.Iptv.PictureSizes(path).For("acme/1001"));
            Assert.Null(new SchedularrTV.Iptv.PictureSizes(path).For("acme/1002"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_streamer_channel_says_the_size_its_picture_is_made_at()
    {
        var encoding = new SchedularrTV.Core.Platform.LiveEncodingSettings { Height = 540 };
        Assert.Equal("960x540", SchedularrTV.Streamers.StreamerChannels.Picture(encoding));
        Assert.Equal(ErsatzTvEncoding.PictureAnnotation, SchedularrTV.Streamers.StreamerChannels.PictureAnnotation);
        Assert.Equal(ErsatzTvEncoding.PictureAnnotation, SchedularrTV.Iptv.IptvChannels.PictureAnnotation);
    }

    [Fact]
    public void A_channels_encoding_is_kept_in_the_yaml_only_when_set()
    {
        SchedulerConfig config = SchedulerConfigLoader.Parse("""
            channels:
            - id: news
              name: News
              number: 3
              encoding:
                hardware: cuda
                height: 720
                width: 960
                scaling_mode: crop
            """);

        Assert.Equal("cuda", config.Channels[0].Encoding?.Hardware);
        Assert.Equal(720, config.Channels[0].Encoding?.Height);
        Assert.Equal(960, config.Channels[0].Encoding?.Width);
        Assert.Equal("crop", config.Channels[0].Encoding?.ScalingMode);

        string yaml = SchedulerConfigLoader.Serialize(config);
        Assert.Contains("hardware: cuda", yaml, StringComparison.Ordinal);
        Assert.Contains("width: 960", yaml, StringComparison.Ordinal);
        Assert.Contains("scaling_mode: crop", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("is_empty", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("bitrate_kbps", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_dimensions_and_sizing_inherit_and_normalize_for_encoders()
    {
        var global = new ErsatzTvEncodingOptions { Width = "1440", Height = "1080", ScalingMode = "crop" };
        EffectiveEncoding inherited = ErsatzTvEncoding.Defaults(global);
        Assert.Equal(1440, inherited.Width);
        Assert.Equal("crop", inherited.ScalingMode);

        EffectiveEncoding own = ErsatzTvEncoding.Resolve(global, new ChannelEncodingConfig
        {
            Width = 641, Height = 481, ScalingMode = "scale_and_pad"
        });
        Assert.Equal(640, own.Width);
        Assert.Equal(480, own.Height);
        Assert.Equal("scale_and_pad", own.ScalingMode);
        Assert.Equal(inherited, ErsatzTvEncoding.Resolve(global, new ChannelEncodingConfig
        {
            Width = -1, Height = 99999, ScalingMode = "invalid"
        }));
        Assert.False(new ChannelEncodingConfig { Width = 640 }.IsEmpty);
        Assert.False(new ChannelEncodingConfig { ScalingMode = "crop" }.IsEmpty);
        Assert.NotEqual(ErsatzTvEncoding.FileName(ErsatzTvEncoding.Overlay(own)),
            ErsatzTvEncoding.FileName(ErsatzTvEncoding.Overlay(own with { ScalingMode = "crop" })));
    }

    [Theory]
    [InlineData(480, "4:3", 640)]
    [InlineData(720, "4:3", 960)]
    [InlineData(480, "16:9", 854)]
    [InlineData(1080, "1:1", 1080)]
    [InlineData(720, "9:16", 406)]
    public void Resolution_presets_keep_the_selected_shape(int height, string aspect, int width)
    {
        var global = new ErsatzTvEncodingOptions { Height = "1080", AspectRatio = aspect };
        EffectiveEncoding result = ErsatzTvEncoding.Resolve(global, new ChannelEncodingConfig { Height = height });
        Assert.Equal(width, result.Width);
        Assert.Equal(800, ErsatzTvEncoding.Resolve(global, new ChannelEncodingConfig { Height = height, Width = 800 }).Width);
    }

    [Theory]
    [InlineData("source", "source")]
    [InlineData("24000/1001", "24000/1001")]
    [InlineData("48", "48")]
    [InlineData("240", "240")]
    [InlineData("241", null)]
    [InlineData("29.97", null)]
    [InlineData("30/0", null)]
    [InlineData("0", null)]
    [InlineData("1/2", null)]
    [InlineData("30/1/1", null)]
    public void Custom_frame_rates_are_validated(string input, string? expected) =>
        Assert.Equal(expected, ErsatzTvEncoding.NormalizeFrameRate(input));

    [Fact]
    public void Source_frame_rate_removes_fixed_defaults_and_round_trips_through_the_schedule()
    {
        var config = SchedulerConfigLoader.Parse("""
            channels:
            - id: news
              name: News
              number: 3
              encoding:
                height: 480
                aspect_ratio: '4:3'
                frame_rate: source
            """);
        config = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        var global = new ErsatzTvEncodingOptions { FrameRate = "30000/1001" };
        var schedule = new ChannelSchedule
        {
            ChannelId = "news", ChannelName = "News", ChannelNumber = 3,
            Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch.AddHours(1), Entries = [],
            Annotations = new Dictionary<string, string> { [ErsatzTvEncoding.PictureAnnotation] = "1920x1080" }
        };
        EffectiveEncoding resolved = ErsatzTvEncoding.Resolve(global, ScheduleCoordinator.WithEncoding(config, [schedule])[0]);
        Assert.Equal(640, resolved.Width);
        Assert.Equal(480, resolved.Height);
        Assert.Null(resolved.FrameRate);
        Assert.True(ErsatzTvEncoding.HasOverrides(ScheduleCoordinator.WithEncoding(config, [schedule])[0]));
        using var source = System.Text.Json.JsonDocument.Parse(ErsatzTvEncoding.Overlay(resolved));
        Assert.Equal(System.Text.Json.JsonValueKind.Null,
            source.RootElement.GetProperty("normalization").GetProperty("video").GetProperty("frame_rate").ValueKind);
        Assert.Null(ErsatzTvEncoding.Defaults(new()).FrameRate);
        EffectiveEncoding fixedRate = ErsatzTvEncoding.Defaults(global);
        Assert.Equal("30000/1001", fixedRate.FrameRate);
        Assert.NotEqual(ErsatzTvEncoding.FileName(ErsatzTvEncoding.Overlay(resolved)),
            ErsatzTvEncoding.FileName(ErsatzTvEncoding.Overlay(resolved with { FrameRate = fixedRate.FrameRate })));
    }

    [Fact]
    public void The_system_file_dialogs_are_asked_for_the_right_files()
    {
        string start = Path.GetTempPath();
        var songs = new FileDialogRequest("Add songs", [".mp3", ".wav"], start, Multiple: true) { FilterName = "Audio" };

        string script = NativeFileDialog.AppleScript(songs);
        Assert.Contains("choose file with prompt \"Add songs\" of type {\"mp3\", \"wav\"}", script, StringComparison.Ordinal);
        Assert.Contains("with multiple selections allowed", script, StringComparison.Ordinal);
        Assert.Contains("choose folder", NativeFileDialog.AppleScript(new FileDialogRequest("Choose a folder", [], null, Folder: true)), StringComparison.Ordinal);

        IReadOnlyList<string> zenity = NativeFileDialog.ZenityArguments(songs);
        Assert.Contains("--multiple", zenity);
        Assert.Contains("--file-filter=Audio | *.mp3 *.MP3 *.wav *.WAV", zenity);
        Assert.Contains("--directory", NativeFileDialog.ZenityArguments(new FileDialogRequest("Choose a folder", [], null, Folder: true)));

        IReadOnlyList<string> kdialog = NativeFileDialog.KdialogArguments(songs);
        Assert.Contains("--getopenfilename", kdialog);
        Assert.Contains("--separate-output", kdialog);

        Assert.Equal(["/music/a.mp3", "/music/b b.mp3", "/media/folder"], NativeFileDialog.SplitPaths("/music/a.mp3\n/music/b b.mp3\r\n/media/folder/\n\n"));
    }

    [Theory]
    [InlineData("ffmpeg", "Transcoder (ffmpeg)")]
    [InlineData("ersatztv-channe", "Channel worker")]
    [InlineData("ersatztv-next", "ErsatzTV")]
    [InlineData("msedge", "Browser")]
    [InlineData("something", "something")]
    public void Processes_are_described_by_their_part_in_the_setup(string name, string role) =>
        Assert.Equal(role, ResourceMonitor.Role(name));
}
