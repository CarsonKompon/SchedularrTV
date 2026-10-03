using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ArchiveEpisodeFallbackTests
{
    [Fact]
    public async Task Unmatched_segment_gets_random_same_show_episode_without_repeating_known_segments()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"archive-segment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "2002", "09"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "2002", "09", "2002-09-15.json"),
                """{"schedule":[{"time":"00:00","end_time":"00:30","show":"Shorts","episode":"Known / Missing"}]}""");
            var channel = new ChannelConfig
            {
                Id = "test", Name = "Test",
                Follow = new() { Enabled = true, Directory = directory, StartYear = 2002, EndYear = 2002 }
            };
            var start = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
            var schedule = await new SchedulingEngine(new FakeMediaLibrary([
                Episode("known", "Known"), Episode("random", "Alternative")]), new InMemoryPlaybackStateStore())
                .BuildAsync(new SchedulerConfig(), channel, start, start.AddMinutes(30));
            Assert.Equal(["known", "random"], schedule.Entries.Where(entry => entry.Item?.Kind == MediaKind.Episode).Select(entry => entry.Item!.Id));
            DateTimeOffset cursor = schedule.Start;
            foreach (ScheduleEntry entry in schedule.Entries)
            {
                Assert.Equal(cursor, entry.Start);
                cursor = entry.End;
            }
            Assert.Equal(schedule.End, cursor);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static MediaItem Episode(string id, string title) => new()
    {
        Id = id, SeriesName = "Shorts", Title = title, Kind = MediaKind.Episode, Duration = TimeSpan.FromMinutes(7)
    };
}