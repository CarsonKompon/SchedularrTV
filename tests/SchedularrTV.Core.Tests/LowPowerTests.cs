using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SchedularrTV.AutoChapters;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Platform;
using SchedularrTV.LiveCapture;
using SchedularrTV.Providers.ErsatzTv;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>The hardware profile, the encoding settings it picks defaults for, and what they turn into.</summary>
public class LowPowerTests
{
    private static Func<string, string?> Settings(params (string Key, string Value)[] values)
    {
        Dictionary<string, string> map = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out string? value) ? value : null;
    }

    [Fact]
    public void A_raspberry_pi_is_low_power_and_a_desktop_is_not()
    {
        var pi = new HardwareInfo("Raspberry Pi 5 Model B Rev 1.0", true, 5, 4, 8L << 30, Architecture.Arm64, false);
        var desktop = new HardwareInfo(null, false, null, 16, 32L << 30, Architecture.X64, false);
        var tiny = new HardwareInfo(null, false, null, 2, 16L << 30, Architecture.X64, false);

        Assert.True(pi.LooksLowPower);
        Assert.False(desktop.LooksLowPower);
        Assert.True(tiny.LooksLowPower);
        Assert.Contains("Raspberry Pi 5", pi.Summary, StringComparison.Ordinal);
        Assert.Contains("4 cores", pi.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Software_encodes_keep_the_x264_arguments_they_always_had()
    {
        var choice = new VideoEncoderChoice { Preset = "veryfast" };

        Assert.Equal(["-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-b:v", "3500k", "-maxrate", "3500k", "-bufsize", "7000k"],
            choice.VideoArguments(3500));
        Assert.Equal(["-c:v", "libx264", "-preset", "medium", "-tune", "animation", "-crf", "22"],
            (choice with { Preset = "medium" }).VideoArguments(8000, crf: 22, profileHigh: false, tune: "animation"));
    }

    [Fact]
    public void Hardware_encoders_use_a_bitrate_and_drop_what_they_dont_take()
    {
        var v4l2 = new VideoEncoderChoice { Encoder = H264Encoder.Find("h264_v4l2m2m"), Preset = "ultrafast", Threads = 2 };
        IReadOnlyList<string> arguments = v4l2.VideoArguments(6000, crf: 20, tune: "animation");

        Assert.Equal(["-c:v", "h264_v4l2m2m", "-b:v", "6000k"], arguments);
        Assert.Equal("p1", new VideoEncoderChoice { Encoder = H264Encoder.Find("h264_nvenc"), Preset = "ultrafast" }.VideoArguments(3000)[3]);
        Assert.Same(H264Encoder.Software, H264Encoder.Find("no-such-encoder"));
    }

    [Fact]
    public void Live_settings_fill_blanks_with_defaults_and_ignore_values_they_cant_use()
    {
        LiveEncodingSettings standard = LiveEncodingSettings.From(Settings());
        Assert.Equal(LiveEncodingSettings.Defaults.Height, standard.Height);

        LiveEncodingSettings chosen = LiveEncodingSettings.From(Settings(
            ("LiveEncoding:Preset", "SuperFast"), ("LiveEncoding:Height", "480"), ("LiveEncoding:FrameRate", "24"),
            ("LiveEncoding:BitrateKbps", "1800"), ("LiveEncoding:Threads", "3")));
        Assert.Equal("superfast", chosen.Video.Preset);
        Assert.Equal((854, 480, 24, 1800, 3), (chosen.Width, chosen.Height, chosen.FrameRate, chosen.BitrateKbps, chosen.Video.Threads));

        LiveEncodingSettings odd = LiveEncodingSettings.From(Settings(("LiveEncoding:Height", "777"), ("LiveEncoding:Preset", "warp")));
        Assert.Equal(LiveEncodingSettings.Defaults.Height, odd.Height);
        Assert.Equal(LiveEncodingSettings.Defaults.Video.Preset, odd.Video.Preset);
    }

    [Fact]
    public void A_smaller_live_stream_is_laid_out_at_720p_and_scaled_once_at_the_end()
    {
        var encoding = new LiveEncodingSettings { Video = new VideoEncoderChoice { Preset = "ultrafast" }, Height = 540, FrameRate = 25, BitrateKbps = 2500 };
        var spec = new LiveStreamSpec { Id = "x", Name = "X", PageUrl = "http://localhost/", Ffmpeg = "ffmpeg", Encoding = encoding };
        List<string> arguments = [.. LiveStream.Arguments(spec, null)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        Assert.Contains("scale=1280:720", graph, StringComparison.Ordinal);
        Assert.Contains("fps=25", graph, StringComparison.Ordinal);
        Assert.Contains("scale=960:540:flags=bilinear,format=yuv420p[v]", graph, StringComparison.Ordinal);
        Assert.Equal("ultrafast", arguments[arguments.IndexOf("-preset") + 1]);
        Assert.Equal("2500k", arguments[arguments.IndexOf("-b:v") + 1]);
        Assert.Equal("50", arguments[arguments.IndexOf("-g") + 1]);

        using JsonDocument hint = JsonDocument.Parse(LiveStream.ProbeHintFor(encoding));
        JsonElement video = hint.RootElement.GetProperty("video")[0];
        Assert.Equal(960, video.GetProperty("width").GetInt32());
        Assert.Equal("25", video.GetProperty("frame_rate").GetString());
        Assert.Equal("high", video.GetProperty("profile").GetString());
    }

    [Fact]
    public void A_full_size_live_stream_isnt_scaled_again()
    {
        var spec = new LiveStreamSpec
        {
            Id = "x", Name = "X", PageUrl = "http://localhost/", Ffmpeg = "ffmpeg",
            Encoding = new LiveEncodingSettings { Height = 720, FrameRate = 30 }
        };
        List<string> arguments = [.. LiveStream.Arguments(spec, null)];
        string graph = arguments[arguments.IndexOf("-filter_complex") + 1];

        Assert.DoesNotContain("flags=bilinear", graph, StringComparison.Ordinal);
        Assert.EndsWith("format=yuv420p[v]", graph.Split(';')[^2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_low_power_channel_config_is_current_and_lighter()
    {
        JsonObject config = (JsonObject)JsonNode.Parse(LineupPublisher.LowPowerChannelConfig)!;

        Assert.Null(ChannelConfigSchema.Upgrade(config));
        Assert.Equal(720, (int)config["normalization"]!["video"]!["height"]!);
        Assert.False((bool)config["normalization"]!["audio"]!["normalize_loudness"]!);
    }

    [Fact]
    public void Black_detection_can_decode_in_hardware_with_fewer_threads()
    {
        DetectionOptions options = DetectionOptions.Default with { Threads = 2, HardwareDecode = "auto" };
        List<string> arguments = [.. BlackDetector.Arguments("file.mkv", options)];

        Assert.True(arguments.IndexOf("-hwaccel") < arguments.IndexOf("-i"));
        Assert.Equal("auto", arguments[arguments.IndexOf("-hwaccel") + 1]);
        Assert.Equal("2", arguments[arguments.IndexOf("-threads") + 1]);
        Assert.DoesNotContain("-hwaccel", BlackDetector.Arguments("file.mkv", options.InSoftware()));
        Assert.DoesNotContain("-threads", BlackDetector.Arguments("file.mkv", DetectionOptions.Default));
    }

    [Fact]
    public void Black_detection_can_keep_frames_on_the_graphics_card()
    {
        DetectionOptions nvidia = DetectionOptions.Default with { HardwareDecode = "cuda", GpuScale = true };
        List<string> arguments = [.. BlackDetector.Arguments("file.mkv", nvidia)];

        Assert.Equal("cuda", arguments[arguments.IndexOf("-hwaccel_output_format") + 1]);
        Assert.StartsWith("scale_cuda=320:180,hwdownload,", arguments[arguments.IndexOf("-vf") + 1], StringComparison.Ordinal);

        // A decoder without a scaler on the card, or the setting off, brings the frames back to shrink.
        Assert.DoesNotContain("-hwaccel_output_format", BlackDetector.Arguments("file.mkv", nvidia with { HardwareDecode = "d3d11va" }));
        Assert.DoesNotContain("-hwaccel_output_format", BlackDetector.Arguments("file.mkv", nvidia with { GpuScale = false }));
        Assert.DoesNotContain("-hwaccel", BlackDetector.Arguments("file.mkv", nvidia.InSoftware()));

        Assert.Equal("cuda", AutoChaptersSettings.MatchingDecoder("h264_nvenc"));
        Assert.Null(AutoChaptersSettings.MatchingDecoder("libx264"));
    }

    [Fact]
    public void Black_detection_can_skip_unneeded_decoding_and_cap_its_speed()
    {
        List<string> arguments = [.. BlackDetector.Arguments("file.mkv", DetectionOptions.Default with { FastDecode = true, MaxSpeed = 20 })];
        int input = arguments.IndexOf("-i");

        Assert.Equal("nonref", arguments[arguments.IndexOf("-skip_frame") + 1]);
        Assert.True(arguments.IndexOf("-skip_frame") < input);
        if (BlackDetector.ReadRateSupported)
        {
            Assert.Equal("20", arguments[arguments.IndexOf("-readrate") + 1]);
            Assert.True(arguments.IndexOf("-readrate") < input);
        }

        List<string> flatOut = [.. BlackDetector.Arguments("file.mkv", DetectionOptions.Default)];
        Assert.DoesNotContain("-skip_frame", flatOut);
        Assert.DoesNotContain("-readrate", flatOut);
    }

    [Fact]
    public async Task Saving_a_build_writes_the_state_file_once_with_every_sequence()
    {
        string path = Path.Combine(Path.GetTempPath(), $"state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonPlaybackStateStore(path);
            IPlaybackStateStore asInterface = store;
            await asInterface.SaveAllAsync([new PlaybackState { Key = "a" }, new PlaybackState { Key = "b" }]);

            var reloaded = new JsonPlaybackStateStore(path);
            Assert.NotNull(await reloaded.GetAsync("a"));
            Assert.NotNull(await reloaded.GetAsync("b"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
