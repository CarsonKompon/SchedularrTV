using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GapShuffleTests
{
    [Fact]
    public void Gap_preview_filters_before_the_candidate_limit_and_keeps_long_items_for_later()
    {
        var pool = Enumerable.Range(0, 300).Select(index => TestMedia.Filler($"long-{index}", TimeSpan.FromMinutes(30), "Ads"))
            .Append(TestMedia.Filler("short", TimeSpan.FromSeconds(30), "Ads")).ToArray();
        var sequence = MediaSequence.Create("gap", PlaybackOrder.Shuffle, pool, null);
        PlaybackState state = sequence.ToState();
        state.Bag.Clear();
        state.Bag.AddRange(pool.Select(item => item.Id));
        sequence = MediaSequence.Create("gap", PlaybackOrder.Shuffle, pool, state);
        Assert.Equal("short", Assert.Single(sequence.PreviewForGap(256, item => item.Duration.TotalSeconds <= 60)).Id);
        sequence.Consume("short");
        Assert.Empty(sequence.PreviewForGap(256, item => item.Duration.TotalSeconds < 10));
        Assert.Equal("short", Assert.Single(sequence.PreviewForGap(256, item => item.Duration.TotalSeconds <= 60)).Id);
        // Offered again, but not put back in the bag: it has had its turn this cycle.
        Assert.Equal(300, sequence.ToState().Bag.Distinct().Count());
        Assert.Equal(new[] { "short" }, sequence.ToState().Played);
        Assert.Contains(sequence.PreviewForGap(256, item => item.Duration.TotalMinutes <= 40), item => item.Id.StartsWith("long-"));
    }

    [Fact]
    public void Shorts_can_recycle_fitting_items_without_changing_normal_show_shuffle_rules()
    {
        var pool = new[] { TestMedia.Episode("Short", 1, 1, TimeSpan.FromMinutes(2)),
            TestMedia.Episode("Long", 1, 1, TimeSpan.FromMinutes(30)) };
        var sequence = MediaSequence.Create("shorts", PlaybackOrder.Shuffle, pool, null);
        Assert.True(sequence.TryTake(item => item.Duration.TotalMinutes <= 5, out MediaItem first));
        Assert.False(sequence.TryTake(item => item.Duration.TotalMinutes <= 5, out _));
        Assert.True(sequence.TryTakeForGap(item => item.Duration.TotalMinutes <= 5, out MediaItem repeated));
        Assert.Equal(first.Id, repeated.Id);
        Assert.False(sequence.TryTakeForGap(item => item.Duration.TotalSeconds < 1, out _));
        var resumed = MediaSequence.Create("shorts", PlaybackOrder.Shuffle, pool, sequence.ToState());
        Assert.True(resumed.TryTake(null, out MediaItem remaining));
        Assert.Equal("Long", remaining.SeriesName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Commercials_recover_from_an_unfittable_shuffle_tail_and_respect_break_repeat_limits(int maxRepeats)
    {
        DateTimeOffset start = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var commercials = new CommercialsConfig { Query = "tag:'Ads'", Order = PlaybackOrder.Shuffle, MaxRepeatsPerBreak = maxRepeats };
        var config = new SchedulerConfig();
        config.Settings.Interstitials = new() { Commercials = commercials, Filler = new() { Fallback = false } };
        config.Settings.Promos.Enabled = false;
        int slotMinutes = 1 + 2 * maxRepeats;
        var channel = new ChannelConfig { Id = "test", Name = "Test", PadToNearest = TimeSpan.FromMinutes(slotMinutes),
            BaseRotation = new() { Query = "kind:'Episode'" } };
        var ads = Enumerable.Range(0, 4).Select(index => TestMedia.Filler($"ad-{index}", TimeSpan.FromSeconds(30), "Ads"))
            .Append(TestMedia.Filler("long", TimeSpan.FromMinutes(30), "Ads")).ToArray();
        string query = InterstitialQueryBuilder.BuildCommercials(commercials, start, config.Settings.TemporalTags).ToQueryString();
        string key = $"test|commercials|{DeterministicRandom.Hash($"{query}|{PlaybackOrder.Shuffle}||"):x16}";
        var sequence = MediaSequence.Create(key, PlaybackOrder.Shuffle, ads, null);
        foreach (MediaItem ad in ads.Skip(1).Take(3)) sequence.Consume(ad.Id);
        var state = new InMemoryPlaybackStateStore();
        await state.SaveAsync(sequence.ToState());
        var engine = new SchedulingEngine(new FakeMediaLibrary([.. ads, TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(1))]), state);
        ChannelSchedule schedule = await engine.BuildAsync(config, channel, start, start.AddMinutes(slotMinutes * 2));
        Assert.DoesNotContain(schedule.Entries, entry => entry.Kind == ScheduleEntryKind.Padding);
        Assert.Equal(8 * maxRepeats, schedule.Entries.Count(entry => entry.Kind == ScheduleEntryKind.Commercial));
        for (int slot = 0; slot < 2; slot++)
        {
            var actual = schedule.Entries.Where(entry => entry.Kind == ScheduleEntryKind.Commercial
                && entry.Start >= start.AddMinutes(slot * slotMinutes) && entry.Start < start.AddMinutes((slot + 1) * slotMinutes)).ToArray();
            Assert.Equal(4, actual.Select(entry => entry.Item!.Id).Distinct().Count());
            Assert.All(actual.GroupBy(entry => entry.Item!.Id), group => Assert.Equal(maxRepeats, group.Count()));
            Assert.DoesNotContain(actual, entry => entry.IsTrimmed);
        }
    }
}