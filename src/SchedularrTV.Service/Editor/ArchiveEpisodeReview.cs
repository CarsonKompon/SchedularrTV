using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Service.Editor;

public sealed record ArchiveEpisodeReview(ArchiveEpisode Archive, string? LibraryShow)
{
    public static IReadOnlyList<ArchiveEpisodeReview> Unmatched(
        ArchiveFollowConfig follow, IReadOnlyList<ArchiveEpisode> episodes, IReadOnlyList<MediaItem> library,
        CancellationToken cancellationToken = default)
    {
        var pools = library.GroupBy(TitleRotation.TitleOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MediaItem>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matcher = new ArchiveShowMatcher(pools.Keys);
        List<ArchiveEpisodeReview> result = [];
        foreach (ArchiveEpisode episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? title = matcher.Resolve(follow, episode.Show);
            IReadOnlyList<MediaItem> pool = title is not null && pools.TryGetValue(title, out var available) ? available : [];
            if (pool.Count == 0 || ArchiveEpisodeResolver.MatchParts(episode.Episode, pool).Any(item => item is null))
            {
                result.Add(new(episode, pool.Count == 0 ? null : title));
            }
        }
        return result;
    }
}