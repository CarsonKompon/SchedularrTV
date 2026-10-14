using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Core.Temporal;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class BlockBoundaryClipTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);
    private const string Root = "D:/Media/Ads";
    private static MediaItem Clip(string path, int seconds = 30) => new()
    {
        Id = path, Path = path, Title = path, Kind = MediaKind.Filler, Duration = TimeSpan.FromSeconds(seconds)
    };

    private static (SchedulerConfig Config, ChannelConfig Channel, List<MediaItem> Items) Setup()
    {
        AdBreakConfig collection = new()
        {
            Folders = [new() { Path = Root, Role = AdFolderRole.Ads },
                new() { Path = Root + "/Block Start", Role = AdFolderRole.BlockStart },
                new() { Path = Root + "/Block End", Role = AdFolderRole.BlockEnd }]
        };
        ChannelConfig channel = new()
        {
            Id = "channel", Name = "Channel", Number = 1, TimeZone = "UTC", AdBreaks = ["Break"],
            PadToNearest = TimeSpan.FromMinutes(30),
            BaseRotation = new() { Include = [new() { Show = "Show" }] },
            Blocks = [new() { Name = "Evening", Time = TimeSpan.FromHours(20), EndTime = TimeSpan.FromHours(21),
                Shows = [new() { Include = [new() { Show = "Show" }] }] }]
        };
        SchedulerConfig config = new() { Channels = [channel], AdBreaks = new() { ["Break"] = collection } };
        return (config, channel, [TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(22)),
            TestMedia.Episode("Show", 1, 2, TimeSpan.FromMinutes(22)), Clip(Root + "/ad.mp4"),
            Clip(Root + "/Block Start/open.mp4"), Clip(Root + "/Block End/close.mp4")]);
    }

    private static async Task<ChannelSchedule> Build(SchedulerConfig config, ChannelConfig channel, List<MediaItem> items,
        DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        await new SchedulingEngine(new FakeMediaLibrary(items), new InMemoryPlaybackStateStore())
            .BuildAsync(config, channel, start ?? At, end ?? At.AddHours(1));

    private static void Contiguous(ChannelSchedule schedule)
    {
        Assert.Equal(schedule.Start, schedule.Entries[0].Start);
        for (int i = 1; i < schedule.Entries.Count; i++) Assert.Equal(schedule.Entries[i - 1].End, schedule.Entries[i].Start);
        Assert.Equal(schedule.End, schedule.Entries[^1].End);
    }

    [Fact]
    public async Task Whole_clips_bookend_the_block_once_and_are_not_mixed_into_its_breaks()
    {
        var (config, channel, items) = Setup();
        ChannelSchedule result = await Build(config, channel, items);
        Assert.Equal("block start", result.Entries[0].Source);
        Assert.Equal("block end", result.Entries[^1].Source);
        Assert.Single(result.Entries, entry => entry.Item?.Path?.EndsWith("open.mp4") == true);
        Assert.Single(result.Entries, entry => entry.Item?.Path?.EndsWith("close.mp4") == true);
        Assert.Equal(2, result.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Program));
        Assert.All(result.Entries.Where(entry => entry.Source.StartsWith("block ")), entry =>
        {
            Assert.Equal("Evening", entry.BlockName);
            Assert.Equal(TimeSpan.FromSeconds(30), entry.Duration);
            Assert.False(entry.IsTrimmed);
        });
        Contiguous(result);
    }

    [Theory]
    [InlineData(0, 30, 1, 0)]
    [InlineData(30, 60, 0, 1)]
    [InlineData(15, 45, 0, 0)]
    public async Task Partial_windows_only_play_real_boundaries(int start, int end, int openings, int closings)
    {
        var (config, channel, items) = Setup();
        ChannelSchedule result = await Build(config, channel, items, At.AddMinutes(start), At.AddMinutes(end));
        Assert.Equal(openings, result.Entries.Count(entry => entry.Source == "block start"));
        Assert.Equal(closings, result.Entries.Count(entry => entry.Source == "block end"));
        Contiguous(result);
    }

    [Fact]
    public async Task Adjacent_blocks_each_get_their_own_pair_and_base_rotation_gets_none()
    {
        var (config, channel, items) = Setup();
        channel.Blocks.Add(new() { Name = "Later", Time = TimeSpan.FromHours(21), EndTime = TimeSpan.FromHours(22), Shows = channel.Blocks[0].Shows });
        ChannelSchedule result = await Build(config, channel, items, At.AddHours(-1), At.AddHours(2));
        Assert.Equal(2, result.Entries.Count(entry => entry.Source == "block start"));
        Assert.Equal(2, result.Entries.Count(entry => entry.Source == "block end"));
        Assert.DoesNotContain(result.Entries, entry => entry.Start < At && entry.Source.StartsWith("block "));
        Assert.Equal("block end", result.Entries.Single(entry => entry.End == At.AddHours(1)).Source);
        Assert.Equal("block start", result.Entries.Single(entry => entry.Start == At.AddHours(1)).Source);
        Contiguous(result);
    }

    [Fact]
    public async Task Block_collection_overrides_inherited_collection_and_seasonal_nested_skips_apply()
    {
        var (config, channel, items) = Setup();
        config.AdBreaks["Own"] = new()
        {
            Seasonal = true,
            Folders = [new() { Path = Root + "/Own", Role = AdFolderRole.BlockStart },
                new() { Path = Root + "/Own/Old", Role = AdFolderRole.Skip }]
        };
        channel.Blocks[0].AdBreaks = ["Own"];
        items.AddRange([Clip(Root + "/Own/July/summer.mp4"), Clip(Root + "/Own/December/winter.mp4"), Clip(Root + "/Own/Old/old.mp4")]);
        ChannelSchedule result = await Build(config, channel, items);
        Assert.Equal(Root + "/Own/July/summer.mp4", result.Entries[0].Item!.Path);
        Assert.DoesNotContain(result.Entries, entry => entry.Source == "block end");
        Contiguous(result);
    }

    [Fact]
    public async Task Cross_midnight_blocks_and_start_only_anchor_blocks_have_bookends()
    {
        var (config, channel, items) = Setup();
        channel.Blocks[0].Time = TimeSpan.FromHours(23.5);
        channel.Blocks[0].EndTime = TimeSpan.FromMinutes(30);
        ChannelSchedule midnight = await Build(config, channel, items, At.AddHours(3.5), At.AddHours(4.5));
        Assert.Equal("block start", midnight.Entries[0].Source);
        Assert.Equal("block end", midnight.Entries[^1].Source);
        Contiguous(midnight);

        channel.Blocks[0].Time = TimeSpan.FromHours(20);
        channel.Blocks[0].EndTime = null;
        ChannelSchedule anchored = await Build(config, channel, items);
        Assert.Equal(["block start", "Evening", "block end"], anchored.Entries.Take(3).Select(entry => entry.Source));
        Contiguous(anchored);
    }

    [Fact]
    public async Task Oversized_boundary_clips_are_skipped_rather_than_cut_or_overrun()
    {
        var (config, channel, items) = Setup();
        items = [.. items.Where(item => item.Path?.Contains("/Block ") != true),
            Clip(Root + "/Block Start/long.mp4", 7200), Clip(Root + "/Block End/long.mp4", 7200)];
        ChannelSchedule result = await Build(config, channel, items);
        Assert.DoesNotContain(result.Entries, entry => entry.Source.StartsWith("block "));
        Contiguous(result);
    }

    [Fact]
    public void Folder_picker_and_yaml_roundtrip_keep_boundary_roles_out_of_ads()
    {
        var (config, channel, _) = Setup();
        AdBreakConfig collection = config.AdBreaks["Break"];
        AdBreakFolderJobs.Set(collection, Root + "/Block Start", "blockstart");
        AdBreakFolderJobs.Set(collection, Root + "/Block End", "blockend");
        Assert.Equal("Block start", AdBreakFolderJobs.Explicit(collection, Root + "/Block Start")!.Label);
        Assert.Equal("Block end", AdBreakFolderJobs.Explicit(collection, Root + "/Block End")!.Label);
        SchedulerConfig parsed = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config));
        Assert.Contains(parsed.AdBreaks["Break"].Folders, folder => folder.Role == AdFolderRole.BlockStart);
        Assert.Contains(parsed.AdBreaks["Break"].Folders, folder => folder.Role == AdFolderRole.BlockEnd);
        QueryExpression ads = InterstitialQueryBuilder.BuildCommercials(SchedulingEngine.ResolvedBreaks(config, channel).Commercials!, At, new TemporalTagOptions());
        Assert.False(QueryEvaluator.Matches(Clip(Root + "/Block Start/open.mp4"), ads));
        Assert.False(QueryEvaluator.Matches(Clip(Root + "/Block End/close.mp4"), ads));
        Assert.True(QueryEvaluator.Matches(Clip(Root + "/ad.mp4"), ads));
    }

    [Fact]
    public async Task Archive_follow_uses_one_pair_for_adjacent_slots_in_a_mapped_block()
    {
        string directory = Path.Combine(Path.GetTempPath(), "block-bookends-" + Guid.NewGuid().ToString("N"));
        string month = Path.Combine(directory, "2002", "07");
        Directory.CreateDirectory(month);
        try
        {
            File.WriteAllText(Path.Combine(month, "2002-07-10.json"), """
                {"schedule":[
                    {"time":"20:00","end_time":"20:30","show":"Show","blocks":["Evening"]},
                    {"time":"20:30","end_time":"21:00","show":"Show","blocks":["Evening"]}
                ]}
                """);
            var (config, channel, items) = Setup();
            channel.Follow = new() { Enabled = true, Directory = directory, StartYear = 2002, EndYear = 2002,
                Blocks = new() { ["Evening"] = "Evening" } };
            channel.Blocks[0].FollowOnly = true;
            ChannelSchedule result = await Build(config, channel, items);
            Assert.Equal("block start", result.Entries[0].Source);
            Assert.Equal("block end", result.Entries[^1].Source);
            Assert.Single(result.Entries, entry => entry.Source == "block start");
            Assert.Single(result.Entries, entry => entry.Source == "block end");
            Contiguous(result);

            ChannelSchedule partial = await Build(config, channel, items, At.AddMinutes(30));
            Assert.DoesNotContain(partial.Entries, entry => entry.Source == "block start");
            Assert.Equal("block end", partial.Entries[^1].Source);
            Contiguous(partial);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
