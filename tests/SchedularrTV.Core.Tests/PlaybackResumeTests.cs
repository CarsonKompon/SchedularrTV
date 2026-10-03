using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class PlaybackResumeTests
{
    private static List<MediaItem> Episodes(int count, string series = "Show") =>
        [.. Enumerable.Range(1, count).Select(e => new MediaItem
        {
            Id = $"{series}-{e}",
            Title = $"{series} {e}",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(22),
            SeriesName = series,
            SeasonNumber = 1,
            EpisodeNumber = e,
            PremiereDate = new DateTimeOffset(2000, 1, e, 0, 0, 0, TimeSpan.Zero)
        })];

    private static PlaybackState PlayThrough(IReadOnlyList<MediaItem> pool, int take)
    {
        MediaSequence sequence = MediaSequence.Create("k", PlaybackOrder.Chronological, pool, null);

        for (int i = 0; i < take; i++)
        {
            Assert.True(sequence.TryTake(null, out _));
        }

        return sequence.ToState();
    }

    [Fact]
    public void A_new_episode_in_the_library_does_not_restart_the_show()
    {
        List<MediaItem> pool = Episodes(10);
        PlaybackState state = PlayThrough(pool, 4);

        // Jellyfin picks up an extra episode, which changes the pool fingerprint.
        List<MediaItem> grown = [.. pool, new MediaItem
        {
            Id = "Show-11",
            Title = "Show 11",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(22),
            SeriesName = "Show",
            SeasonNumber = 1,
            EpisodeNumber = 11,
            PremiereDate = new DateTimeOffset(2000, 1, 11, 0, 0, 0, TimeSpan.Zero)
        }];

        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.Chronological, grown, state);

        Assert.True(resumed.TryTake(null, out MediaItem next));
        Assert.Equal(5, next.EpisodeNumber);
    }

    [Fact]
    public void Removing_an_earlier_episode_still_resumes_in_place()
    {
        List<MediaItem> pool = Episodes(10);
        PlaybackState state = PlayThrough(pool, 6);

        List<MediaItem> trimmed = [.. pool.Where(i => i.EpisodeNumber != 2)];

        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.Chronological, trimmed, state);

        Assert.True(resumed.TryTake(null, out MediaItem next));
        Assert.Equal(7, next.EpisodeNumber);
    }

    [Fact]
    public void Changing_the_order_keeps_the_place_rather_than_restarting()
    {
        List<MediaItem> pool = Episodes(10);
        PlaybackState state = PlayThrough(pool, 4);

        // Same pool, different order: the fingerprint matches so the cursor is honoured, but even if
        // it did not, the last item would still be found.
        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.SeasonEpisode, pool, state);

        Assert.True(resumed.TryTake(null, out MediaItem next));
        Assert.Equal(5, next.EpisodeNumber);
    }

    [Fact]
    public void A_last_item_that_no_longer_exists_starts_from_the_top()
    {
        List<MediaItem> pool = Episodes(10);
        PlaybackState state = PlayThrough(pool, 4);

        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.Chronological, Episodes(5, "Other"), state);

        Assert.True(resumed.TryTake(null, out MediaItem next));
        Assert.Equal(1, next.EpisodeNumber);
    }

    [Fact]
    public void An_episode_inserted_before_the_current_position_is_not_jumped_back_to()
    {
        List<MediaItem> pool = Episodes(10);
        PlaybackState state = PlayThrough(pool, 6);

        // A missing early episode is added to the library later.
        List<MediaItem> withGapFilled = [.. pool, new MediaItem
        {
            Id = "Show-2b",
            Title = "Show 2b",
            Kind = MediaKind.Episode,
            Duration = TimeSpan.FromMinutes(22),
            SeriesName = "Show",
            SeasonNumber = 1,
            EpisodeNumber = 2,
            PremiereDate = new DateTimeOffset(2000, 1, 2, 12, 0, 0, TimeSpan.Zero)
        }];

        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.Chronological, withGapFilled, state);

        // Playback carries on from where it was rather than rewinding to the new episode.
        Assert.True(resumed.TryTake(null, out MediaItem next));
        Assert.Equal(7, next.EpisodeNumber);

        // It is still in the pool, so it airs when the rotation comes back around.
        Assert.Contains(resumed.Pool, i => i.Id == "Show-2b");
    }

    [Fact]
    public void Clearing_the_state_starts_over()
    {
        List<MediaItem> pool = Episodes(10);
        _ = PlayThrough(pool, 4);

        MediaSequence fresh = MediaSequence.Create("k", PlaybackOrder.Chronological, pool, null);

        Assert.True(fresh.TryTake(null, out MediaItem next));
        Assert.Equal(1, next.EpisodeNumber);
    }

    [Fact]
    public void A_shuffle_cycle_does_not_replay_the_last_item_first()
    {
        List<MediaItem> pool = Episodes(6);
        MediaSequence sequence = MediaSequence.Create("k", PlaybackOrder.Shuffle, pool, null);

        MediaItem last = null!;
        for (int i = 0; i < 6; i++)
        {
            Assert.True(sequence.TryTake(null, out last));
        }

        PlaybackState state = sequence.ToState();

        // A grown pool forces a fresh bag; the item that just aired should not lead it.
        MediaSequence resumed = MediaSequence.Create("k", PlaybackOrder.Shuffle, Episodes(7), state);

        Assert.True(resumed.TryTake(null, out MediaItem next));
        Assert.NotEqual(last.Id, next.Id);
    }
}
