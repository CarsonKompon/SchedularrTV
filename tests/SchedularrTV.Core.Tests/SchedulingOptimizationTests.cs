using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class SchedulingOptimizationTests
{
    [Fact]
    public void Compressed_packer_preserves_original_choices_and_order()
    {
        var random = new Random(19283);
        for (int sample = 0; sample < 300; sample++)
        {
            TimeSpan target = TimeSpan.FromMilliseconds(random.Next(1, 900000));
            TimeSpan granularity = TimeSpan.FromMilliseconds(sample % 3 == 0 ? 1 : 250);
            int repeats = random.Next(1, 4);
            MediaItem[] candidates = [.. Enumerable.Range(0, random.Next(1, 80)).Select(i =>
                TestMedia.Filler($"ad-{i}", TimeSpan.FromMilliseconds(sample % 2 == 0
                    ? 15000 * random.Next(0, 8) : random.Next(-10, 120000)), "Commercials"))];

            string[] expected = OriginalChoices(candidates, target, granularity, repeats);
            PackResult actual = KnapsackPacker.Pack(candidates, target, granularity, repeats);
            Assert.Equal(expected, actual.Items.Select(item => item.Item.Id));
            Assert.True(actual.Filled <= target);
            Assert.Equal(target, actual.Filled + actual.Residual);
        }
    }

    // Uncompressed, full-scan baseline: guards deterministic tie-breaking as well as optimal fill.
    private static string[] OriginalChoices(MediaItem[] candidates, TimeSpan target, TimeSpan granularity, int repeats)
    {
        long bucket = Math.Max(granularity.Ticks, TimeSpan.TicksPerMillisecond);
        if (target.Ticks / bucket > 40000) bucket = target.Ticks / 40000 + 1;
        int capacity = (int)(target.Ticks / bucket);
        MediaItem[] instances = [.. candidates.Where(item => item.Duration > TimeSpan.Zero && item.Duration <= target)
            .SelectMany(item => Enumerable.Repeat(item, repeats)).Take(256)];
        bool[] reachable = new bool[capacity + 1];
        int[] choices = new int[capacity + 1];
        int[] previous = new int[capacity + 1];
        reachable[0] = true;
        for (int i = 0; i < instances.Length; i++)
        {
            int weight = (int)((instances[i].Duration.Ticks + bucket - 1) / bucket);
            for (int b = capacity; b >= weight; b--)
            {
                if (reachable[b] || !reachable[b - weight]) continue;
                reachable[b] = true;
                choices[b] = i;
                previous[b] = b - weight;
            }
        }
        int best = capacity;
        while (best > 0 && !reachable[best]) best--;
        List<string> result = [];
        for (int b = best; b > 0; b = previous[b]) result.Add(instances[choices[b]].Id);
        result.Reverse();
        return [.. result];
    }

    [Theory]
    [InlineData(PlaybackOrder.Chronological)]
    [InlineData(PlaybackOrder.SeasonEpisode)]
    [InlineData(PlaybackOrder.Random)]
    public void Gap_preview_matches_filtered_order_without_advancing_state(PlaybackOrder order)
    {
        MediaItem[] pool = [.. Enumerable.Range(0, 100).Select(i =>
            TestMedia.Episode("Show", 1, i, TimeSpan.FromSeconds(i + 1)))];
        var sequence = MediaSequence.Create("preview", order, pool, null);
        sequence.Consume(sequence.Pool[^3].Id); // Ordered previews must wrap across the pool boundary.
        foreach (int max in new[] { 0, 1, 16, 200 })
        {
            string before = System.Text.Json.JsonSerializer.Serialize(sequence.ToState());
            var expected = sequence.PreviewOrder(pool.Length).Where(item => item.Duration.TotalSeconds % 3 == 0).Take(max);
            Assert.Equal(expected, sequence.PreviewForGap(max, item => item.Duration.TotalSeconds % 3 == 0));
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(sequence.ToState()));
        }
        Assert.Empty(sequence.PreviewForGap(10, _ => false));
        Assert.Empty(MediaSequence.Create("empty", order, [], null).PreviewForGap(10, _ => true));
    }

    [Fact]
    public async Task Selector_cache_shares_equivalent_pools_but_isolates_scopes_and_builds()
    {
        var selector = new ContentSelector { Query = "tag:'Ads'", Order = PlaybackOrder.Chronological };
        var config = new SchedulerConfig();
        var channel = new ChannelConfig { Id = "test", Name = "Test" };
        var library = new FakeMediaLibrary([TestMedia.Filler("ad", TimeSpan.FromSeconds(30), "Ads")]);
        var state = new InMemoryPlaybackStateStore();
        ScheduleBuildSession Session() => new(config, library, state, channel, TimeZoneInfo.Utc, DateTimeOffset.UnixEpoch);
        var session = Session();
        var shared = await session.GetSequenceAsync(selector, "shared", default);
        Assert.Same(shared, await session.GetSequenceAsync(selector, "shared", default));
        Assert.Same(shared, await session.GetSequenceAsync(new ContentSelector { Query = selector.Query }, "shared", default));
        Assert.NotSame(shared, await session.GetSequenceAsync(selector, "interstitial", default));
        Assert.NotSame(shared, await Session().GetSequenceAsync(selector, "shared", default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.GetSequenceAsync(selector, "shared", cancellation.Token));
    }
}
