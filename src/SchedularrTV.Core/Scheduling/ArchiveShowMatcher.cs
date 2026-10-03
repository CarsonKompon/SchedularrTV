using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

public sealed class ArchiveShowMatcher
{
    private readonly MediaItem[] _titles;
    private readonly Dictionary<string, string[]> _normalized;
    private readonly Dictionary<string, string?> _matches = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveShowMatcher(IEnumerable<string> titles)
    {
        _titles = titles.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(title => new MediaItem { Id = title, Title = title }).ToArray();
        _normalized = _titles.GroupBy(item => ArchiveReader.NormalizeTitle(item.Title))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Title).ToArray(), StringComparer.Ordinal);
    }

    public string? Resolve(ArchiveFollowConfig follow, string archived) => follow.Shows.TryGetValue(archived, out string? assigned)
        ? string.IsNullOrWhiteSpace(assigned) ? null : assigned
        : Match(archived);

    public string? Match(string archived)
    {
        if (_matches.TryGetValue(archived, out string? cached))
        {
            return cached;
        }

        string? match = _normalized.TryGetValue(ArchiveReader.NormalizeTitle(archived), out string[]? exact)
            ? exact.Length == 1 ? exact[0] : null
            : ArchiveEpisodeMatcher.Match(archived, _titles)?.Title;
        _matches[archived] = match;
        return match;
    }
}