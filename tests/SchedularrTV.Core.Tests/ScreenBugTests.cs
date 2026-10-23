using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Playout;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ScreenBugTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "screen-bugs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() => TestFolders.Delete(_folder);

    private static SchedulerConfig Config() => new()
    {
        Titles =
        {
            ["Bugged"] = new TitleDefaults
            {
                ScreenBug = true,
                Seasons =
                {
                    [2] = new SeasonDefaults { ScreenBug = false, Episodes = { [5] = new EpisodeDefaults { ScreenBug = true } } },
                    [3] = new SeasonDefaults { Episodes = { [1] = new EpisodeDefaults { ScreenBug = false } } }
                }
            },
            ["Clean"] = new TitleDefaults { Seasons = { [1] = new SeasonDefaults { Episodes = { [2] = new EpisodeDefaults { ScreenBug = true } } } } }
        }
    };

    [Theory]
    [InlineData("Bugged", 1, 1, true)]   // show
    [InlineData("Bugged", 2, 1, false)]  // season overrides show
    [InlineData("Bugged", 2, 5, true)]   // episode overrides season
    [InlineData("Bugged", 3, 1, false)]  // episode overrides show
    [InlineData("Bugged", 3, 2, true)]   // season without its own setting follows the show
    [InlineData("Clean", 1, 1, false)]
    [InlineData("Clean", 1, 2, true)]
    [InlineData("Unknown", 1, 1, false)]
    public void Most_specific_setting_wins(string series, int season, int episode, bool expected) =>
        Assert.Equal(expected, ShowScreenBugs.HasScreenBug(Config(), TestMedia.Episode(series, season, episode, TimeSpan.FromMinutes(22))));

    [Fact]
    public void Settings_round_trip_through_yaml()
    {
        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(Config()));
        Assert.True(parsed.Titles["Bugged"].ScreenBug);
        Assert.False(parsed.Titles["Bugged"].Seasons[2].ScreenBug);
        Assert.True(parsed.Titles["Bugged"].Seasons[2].Episodes[5].ScreenBug);
        Assert.Null(parsed.Titles["Clean"].ScreenBug);
    }

    [Fact]
    public async Task Screen_bug_overlays_stand_down_on_recordings_with_their_own()
    {
        Overlay bug = new() { Name = "Bug", Kind = OverlayKinds.File, File = "bug.png", FadeSeconds = 0, ScreenBug = true };
        Overlay third = new() { Name = "Third", Kind = OverlayKinds.File, File = "third.png", FadeSeconds = 0 };
        var channel = new ChannelConfig
        {
            Id = "channel", Name = "Channel", Number = 1,
            Plugins = new() { [OverlayResolver.PluginId] = new OverlaysSection { Overlays = [bug, third] } }
        };
        SchedulerConfig config = Config();
        config.Channels.Add(channel);
        ScheduleEntry Programme(string series, int minute) => new()
        {
            Start = At.AddMinutes(minute), Duration = TimeSpan.FromMinutes(20), Kind = ScheduleEntryKind.Program,
            Item = TestMedia.Episode(series, 1, 1, TimeSpan.FromMinutes(20))
        };
        var schedule = new ChannelSchedule
        {
            ChannelId = channel.Id, ChannelName = channel.Name, Start = At, End = At.AddHours(1),
            Entries = [Programme("Bugged", 0), Programme("Clean", 20)]
        };

        var processor = new OverlayProcessor(new OverlayRenderer(_folder), NullLogger<OverlayProcessor>.Instance);
        ChannelSchedule processed = await processor.ProcessAsync(new PlayoutContext(config, channel, schedule), CancellationToken.None);

        Assert.Equal(["third.png"], Paths(processed.Entries[0]));
        Assert.Equal(["bug.png", "third.png"], Paths(processed.Entries[1]));
    }

    private static string[] Paths(ScheduleEntry entry) =>
        [.. ((JsonArray)JsonNode.Parse(entry.Annotations![OverlayProcessor.GraphicsAnnotation])!).Select(layer => (string)layer!["source"]!["path"]!)];
}
