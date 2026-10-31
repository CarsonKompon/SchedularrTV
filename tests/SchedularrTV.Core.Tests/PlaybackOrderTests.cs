using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class PlaybackOrderTests
{
    private static List<MediaItem> Pool() =>
    [
        TestMedia.Episode("Show", 2, 1, TimeSpan.FromMinutes(22)),
        TestMedia.Episode("Show", 1, 2, TimeSpan.FromMinutes(22)),
        TestMedia.Episode("Show", 1, 1, TimeSpan.FromMinutes(22)),
        TestMedia.Episode("Show", 1, 3, TimeSpan.FromMinutes(22))
    ];

    [Fact]
    public void Season_episode_order_sorts_by_season_then_episode()
    {
        MediaSequence sequence = MediaSequence.Create("k", PlaybackOrder.SeasonEpisode, Pool(), null);

        string[] taken = Enumerable.Range(0, 4)
            .Select(_ => sequence.TryTake(null, out MediaItem item) ? item.Id : "?")
            .ToArray();

        Assert.Equal(["Show-s01e01", "Show-s01e02", "Show-s01e03", "Show-s02e01"], taken);
    }

    [Fact]
    public void Chronological_order_follows_premiere_date()
    {
        List<MediaItem> pool =
        [
            TestMedia.Episode("A", 1, 1, TimeSpan.FromMinutes(22), premiere: new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            TestMedia.Episode("B", 1, 1, TimeSpan.FromMinutes(22), premiere: new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            TestMedia.Episode("C", 1, 1, TimeSpan.FromMinutes(22), premiere: new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero))
        ];

        MediaSequence sequence = MediaSequence.Create("k", PlaybackOrder.Chronological, pool, null);

        Assert.True(sequence.TryTake(null, out MediaItem first));
        Assert.Equal("B-s01e01", first.Id);
    }

    [Fact]
    public void Shuffle_plays_every_item_once_before_repeating()
    {
        MediaSequence sequence = MediaSequence.Create("k", PlaybackOrder.Shuffle, Pool(), null);

        List<string> cycle = [];
        for (int i = 0; i < 4; i++)
        {
            Assert.True(sequence.TryTake(null, out MediaItem item));
            cycle.Add(item.Id);
        }

        Assert.Equal(4, cycle.Distinct().Count());
    }

    [Fact]
    public void Shuffle_is_reproducible_from_persisted_state()
    {
        MediaSequence first = MediaSequence.Create("channel|pool", PlaybackOrder.Shuffle, Pool(), null);
        first.TryTake(null, out MediaItem _);
        PlaybackState state = first.ToState();

        MediaSequence resumedA = MediaSequence.Create("channel|pool", PlaybackOrder.Shuffle, Pool(), state);
        MediaSequence resumedB = MediaSequence.Create("channel|pool", PlaybackOrder.Shuffle, Pool(), state);

        for (int i = 0; i < 8; i++)
        {
            resumedA.TryTake(null, out MediaItem a);
            resumedB.TryTake(null, out MediaItem b);
            Assert.Equal(a.Id, b.Id);
        }
    }

    [Fact]
    public void Two_engines_with_the_same_seed_produce_the_same_shuffle()
    {
        MediaSequence a = MediaSequence.Create("same-key", PlaybackOrder.Shuffle, Pool(), null);
        MediaSequence b = MediaSequence.Create("same-key", PlaybackOrder.Shuffle, Pool(), null);

        for (int i = 0; i < 4; i++)
        {
            a.TryTake(null, out MediaItem x);
            b.TryTake(null, out MediaItem y);
            Assert.Equal(x.Id, y.Id);
        }
    }

    [Fact]
    public void A_changed_pool_carries_on_the_cycle_with_the_newcomer_dealt_in()
    {
        MediaSequence first = MediaSequence.Create("k", PlaybackOrder.Shuffle, Pool(), null);
        Assert.True(first.TryTake(null, out MediaItem played));
        PlaybackState state = first.ToState();

        List<MediaItem> grown = Pool();
        MediaItem newcomer = TestMedia.Episode("Show", 3, 1, TimeSpan.FromMinutes(22));
        grown.Add(newcomer);

        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.Shuffle, grown, state);

        // The rest of the cycle: everything but what already played, the newcomer included, each once.
        int left = grown.Count - 1;
        List<string> rest = [];
        for (int i = 0; i < left; i++)
        {
            Assert.True(resumed.TryTake(null, out MediaItem item));
            rest.Add(item.Id);
        }

        Assert.Equal(left, rest.Distinct().Count());
        Assert.DoesNotContain(played.Id, rest);
        Assert.Contains(newcomer.Id, rest);
    }

    [Fact]
    public void A_predicate_skips_items_that_do_not_fit_without_consuming_them()
    {
        List<MediaItem> pool =
        [
            TestMedia.Episode("Long", 1, 1, TimeSpan.FromMinutes(45)),
            TestMedia.Episode("Short", 1, 1, TimeSpan.FromMinutes(11))
        ];

        MediaSequence sequence = MediaSequence.Create("k", PlaybackOrder.SeasonEpisode, pool, null);

        Assert.True(sequence.TryTake(i => i.Duration <= TimeSpan.FromMinutes(15), out MediaItem fitted));
        Assert.Equal("Short-s01e01", fitted.Id);

        Assert.True(sequence.TryTake(null, out MediaItem next));
        Assert.Equal("Long-s01e01", next.Id);
    }

    [Fact]
    public void Random_order_allows_repeats_but_stays_deterministic()
    {
        MediaSequence a = MediaSequence.Create("k", PlaybackOrder.Random, Pool(), null);
        MediaSequence b = MediaSequence.Create("k", PlaybackOrder.Random, Pool(), null);

        for (int i = 0; i < 20; i++)
        {
            a.TryTake(null, out MediaItem x);
            b.TryTake(null, out MediaItem y);
            Assert.Equal(x.Id, y.Id);
        }
    }
}
