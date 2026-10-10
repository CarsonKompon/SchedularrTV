using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Service;
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

    [Theory]
    [InlineData("ffmpeg", "Transcoder (ffmpeg)")]
    [InlineData("ersatztv-channe", "Channel worker")]
    [InlineData("ersatztv-next", "ErsatzTV")]
    [InlineData("msedge", "Browser")]
    [InlineData("something", "something")]
    public void Processes_are_described_by_their_part_in_the_setup(string name, string role) =>
        Assert.Equal(role, ResourceMonitor.Role(name));
}
