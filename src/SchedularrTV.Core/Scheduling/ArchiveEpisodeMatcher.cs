using System.Text.RegularExpressions;
using FuzzySharp;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

public static partial class ArchiveEpisodeMatcher
{
    public static MediaItem? Match(string? archivedTitle, IEnumerable<MediaItem> episodes)
    {
        string title = Clean(archivedTitle);
        if (title.Length == 0)
        {
            return null;
        }

        var candidates = episodes.Select(item => (Item: item, Title: Clean(item.Title)))
            .Where(candidate => candidate.Title.Length > 0).OrderBy(candidate => candidate.Item.SortKey, StringComparer.Ordinal).ToArray();
        string normalized = ArchiveReader.NormalizeTitle(title);
        var exact = candidates.Where(candidate => ArchiveReader.NormalizeTitle(candidate.Title) == normalized).ToArray();
        if (exact.Length > 0)
        {
            return exact[0].Item;
        }
        Match numbered = EpisodeLabel().Match(archivedTitle?.Trim() ?? string.Empty);
        if (numbered.Success && int.TryParse(numbered.Groups["episode"].Value, out int episodeNumber))
        {
            int seasonNumber = numbered.Groups["season"].Success
                && int.TryParse(numbered.Groups["season"].Value, out int parsedSeason) ? parsedSeason : 1;
            return candidates.FirstOrDefault(candidate => candidate.Item.EpisodeNumber == episodeNumber
                && candidate.Item.SeasonNumber == seasonNumber).Item;
        }
        if (normalized.Length < 6)
        {
            return null;
        }

        string numbers = string.Join(",", Numbers().Matches(title).Select(match => match.Value));
        var ranked = candidates
            .Where(candidate => string.Join(",", Numbers().Matches(candidate.Title).Select(match => match.Value)) == numbers)
            .Select(candidate => (candidate.Item, Score: Math.Max(Fuzz.Ratio(title, candidate.Title), Fuzz.TokenSortRatio(title, candidate.Title))))
            .OrderByDescending(candidate => candidate.Score).ToArray();
        return ranked.Length > 0 && ranked[0].Score >= 88
            && (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= 5)
            ? ranked[0].Item : null;
    }

    /// <summary>
    /// A combined multi-episode file ("Big Bots / Gooey Aliens That Control Your Mind / Misplaced in Space") whose
    /// segments match the archived parts from <paramref name="start"/> on, one for one. The longest run wins, then the
    /// closest; a tie between different episodes is too ambiguous to trust.
    /// </summary>
    public static (MediaItem Item, int Parts)? MatchCombined(IReadOnlyList<string> parts, int start, IEnumerable<MediaItem> episodes)
    {
        List<(MediaItem Item, int Parts, int Score)> matches = [];
        foreach (MediaItem episode in episodes.OrderBy(item => item.SortKey, StringComparer.Ordinal))
        {
            string[] segments = episode.Title.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2 || segments.Length > parts.Count - start)
            {
                continue;
            }

            int[] scores = [.. segments.Select((segment, index) => SegmentScore(parts[start + index], segment))];
            if (scores.All(score => score > 0))
            {
                matches.Add((episode, segments.Length, scores.Sum()));
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        (MediaItem Item, int Parts, int Score) best = matches.OrderByDescending(match => match.Parts).ThenByDescending(match => match.Score).First();
        bool ambiguous = matches.Any(match => match.Parts == best.Parts && match.Score == best.Score && !SameEpisode(match.Item, best.Item));
        return ambiguous ? null : (best.Item, best.Parts);
    }

    /// <summary>Copies of one episode (a re-encode, a second library) shouldn't count as a clash.</summary>
    private static bool SameEpisode(MediaItem first, MediaItem second) =>
        first.Id == second.Id
        || (first.EpisodeNumber is not null && first.SeasonNumber == second.SeasonNumber && first.EpisodeNumber == second.EpisodeNumber)
        || ArchiveReader.NormalizeTitle(first.Title) == ArchiveReader.NormalizeTitle(second.Title);

    /// <summary>
    /// How well one archived episode name fits one segment of a combined title: 2 identical, 1 a shortened or
    /// misspelled form (archives often shorten long names), 0 not the same episode.
    /// </summary>
    private static int SegmentScore(string archived, string actual)
    {
        string a = Clean(archived), b = Clean(actual);
        string compactA = ArchiveReader.NormalizeTitle(a), compactB = ArchiveReader.NormalizeTitle(b);
        if (compactA.Length == 0 || compactB.Length == 0)
        {
            return 0;
        }

        if (compactA == compactB)
        {
            return 2;
        }

        if (Math.Min(compactA.Length, compactB.Length) >= 5 && (compactB.StartsWith(compactA, StringComparison.Ordinal) || compactA.StartsWith(compactB, StringComparison.Ordinal)))
        {
            return 1;
        }

        if (string.Join(",", Numbers().Matches(a).Select(match => match.Value)) != string.Join(",", Numbers().Matches(b).Select(match => match.Value)))
        {
            return 0;
        }

        // Every word of a two-word-or-longer short form appearing in the full name counts as the same episode.
        string[] words = a.Split(' ');
        return Math.Max(Fuzz.Ratio(a, b), Fuzz.TokenSortRatio(a, b)) >= 88
            || (words.Length >= 2 && words.All(word => b.Split(' ').Contains(word)))
            ? 1 : 0;
    }

    private static string Clean(string? title) => Spaces().Replace(
        Punctuation().Replace(Annotations().Replace(title ?? string.Empty, string.Empty).ToUpperInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"\((?:edited|repeat|rerun|uncut)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Annotations();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"^(?:(?:s(?:eason)?)\s*:?\s*(?<season>\d+)\s*[-,.:]?\s*)?(?:episode|ep|e)\s*:?\s*(?<episode>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeLabel();
}