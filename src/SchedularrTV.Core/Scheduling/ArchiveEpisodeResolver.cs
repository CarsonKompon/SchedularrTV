using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

public static class ArchiveEpisodeResolver
{
    public static ArchiveEpisodeAssignment? Assignment(ArchiveFollowConfig follow, string show, string? episode) =>
        follow.Episodes.LastOrDefault(assignment =>
            string.Equals(assignment.Show.Trim(), show.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(assignment.Episode.Trim(), episode?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<MediaItem?> MatchParts(string? title, IReadOnlyList<MediaItem> pool)
    {
        MediaItem? whole = ArchiveEpisodeMatcher.Match(title, pool);
        if (whole is not null)
        {
            return [whole];
        }
        string[] parts = (title ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return [null];
        }

        // Several episodes listed together may be one combined file in the library, or separate files, or a mix.
        List<MediaItem?> matched = [];
        for (int index = 0; index < parts.Length;)
        {
            if (ArchiveEpisodeMatcher.MatchCombined(parts, index, pool) is { } combined)
            {
                matched.Add(combined.Item);
                index += combined.Parts;
            }
            else
            {
                matched.Add(ArchiveEpisodeMatcher.Match(parts[index], pool));
                index++;
            }
        }

        return matched;
    }

    public static IReadOnlyList<MediaItem?> Resolve(ArchiveFollowConfig follow, string show, string? title, IReadOnlyList<MediaItem> pool)
    {
        ArchiveEpisodeAssignment? assignment = Assignment(follow, show, title);
        MediaItem? assigned = assignment is null ? null : pool.FirstOrDefault(item => item.Id == assignment.MediaId);
        return assigned is null ? MatchParts(title, pool) : [assigned];
    }
}