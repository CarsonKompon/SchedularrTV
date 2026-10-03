using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Scheduling;

/// <summary>One clip in a block, with the episodes it is reserved for.</summary>
public sealed record ResolvedClip(MediaItem Item, IReadOnlyList<string> Episodes);

/// <summary>A configured block with its clips resolved to library items.</summary>
public sealed record ResolvedBumper(BumperBlock Block, IReadOnlyList<ResolvedClip> Clips)
{
    /// <summary>
    /// Shortest clip that could ever play for this episode. Sizing a slot on this rather than on the
    /// clip finally chosen is what lets "longest that fits" spend the break time instead of pushing
    /// the slot over its grid boundary.
    /// </summary>
    public TimeSpan MinimumFor(MediaItem? episode)
    {
        IReadOnlyList<ResolvedClip> candidates = CandidatesFor(episode);

        return candidates.Count == 0
            ? TimeSpan.Zero
            : candidates.Min(c => c.Item.Duration);
    }

    /// <summary>
    /// False when the episode's own file says it already carries this sequence, so a copy that has
    /// the intro baked in does not get a second one in front of it.
    /// </summary>
    public bool AppliesTo(MediaItem? episode)
    {
        if (Block.Seasons.Count > 0 && (episode?.SeasonNumber is not int season || !Block.Seasons.Contains(season)))
        {
            return false;
        }

        if (Block.SkipWhenFileContains.Count == 0 || episode?.Path is not { Length: > 0 } path)
        {
            return true;
        }

        string name = System.IO.Path.GetFileName(path);

        return !Block.SkipWhenFileContains.Any(marker =>
            !string.IsNullOrWhiteSpace(marker)
            && name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Clips eligible for an episode: the ones pinned to it if any, otherwise the general pool.
    /// A pinned clip never plays with anything else, which is what makes "only for this episode" hold.
    /// </summary>
    public IReadOnlyList<ResolvedClip> CandidatesFor(MediaItem? episode)
    {
        if (!AppliesTo(episode))
        {
            return [];
        }

        string? code = EpisodeCode(episode);

        List<ResolvedClip> pinned = [.. Clips.Where(c =>
            c.Episodes.Count > 0
            && code is not null
            && c.Episodes.Contains(code, StringComparer.OrdinalIgnoreCase))];

        return pinned.Count > 0 ? pinned : [.. Clips.Where(c => c.Episodes.Count == 0)];
    }

    /// <summary>Picks the clip to play, honouring the block's behaviour and the room left in the slot.</summary>
    public MediaItem? Choose(MediaItem? episode, TimeSpan available, string seed)
    {
        List<ResolvedClip> fitting = [.. CandidatesFor(episode).Where(c => c.Item.Duration <= available)];

        if (fitting.Count == 0)
        {
            return null;
        }

        return Block.Selection switch
        {
            BumperSelection.LongestThatFits => fitting
                .OrderByDescending(c => c.Item.Duration)
                .ThenBy(c => c.Item.SortKey, StringComparer.Ordinal)
                .First().Item,

            BumperSelection.ShortestThatFits => fitting
                .OrderBy(c => c.Item.Duration)
                .ThenBy(c => c.Item.SortKey, StringComparer.Ordinal)
                .First().Item,

            _ => Pick(fitting, seed)
        };
    }

    // Seeded rather than truly random so a rebuild reproduces the same airing.
    private MediaItem Pick(List<ResolvedClip> fitting, string seed)
    {
        List<MediaItem> ordered = [.. fitting
            .Select(c => c.Item)
            .OrderBy(i => i.SortKey, StringComparer.Ordinal)];

        DeterministicRandom random = DeterministicRandom.FromSeedText(seed, Block.Name);
        return ordered[random.Next(ordered.Count)];
    }

    internal static string? EpisodeCode(MediaItem? episode) =>
        episode is { SeasonNumber: not null, EpisodeNumber: not null }
            ? $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}"
            : null;
}
