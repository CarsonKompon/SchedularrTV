using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

public static class GeneratedPromoBackgrounds
{
    public static GeneratedBackgroundRule? Rule(GeneratedPromoTemplate template, string path) =>
        template.BackgroundRules?.FirstOrDefault(rule => string.Equals(rule.Background, path, StringComparison.OrdinalIgnoreCase));

    public static double Weight(GeneratedBackgroundRule? rule, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at,
        bool matchCurrentShow = false, TimeZoneInfo? zone = null)
    {
        if (rule is null) return 1;
        if (rule.TimeRange is { } range)
        {
            TimeSpan local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Utc).TimeOfDay;
            bool eligible = range.Start < range.End ? local >= range.Start && local < range.End
                : range.Start > range.End && (local >= range.Start || local < range.End);
            if (!eligible) return 0;
        }
        if (rule.MatchShows == false) return 1;
        IEnumerable<UpNextListing> candidates = upcoming.Take(1);
        if (!matchCurrentShow)
        {
            candidates = upcoming.Where(show => show.Start >= at).OrderBy(show => show.Start);
            candidates = rule.Within is { } within
                ? candidates.Where(show => show.Start - at <= within)
                : candidates.Take(Math.Clamp(rule.NextShows, 1, 10));
        }
        bool matches = candidates.Any(show => ShowTitleMatcher.Find(show.Title, rule.Shows, title => title) is not null);
        return matches ? (double.IsFinite(rule.MatchWeight) ? Math.Clamp(rule.MatchWeight, 1, 1000) : 1)
            : rule.OnlyWhenMatching ? 0 : 1;
    }

    /// <summary>A repeatable weighted choice. Null means no background is eligible, never a restricted fallback.</summary>
    public static string? Select(GeneratedPromoTemplate template, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at, string seed,
        bool matchCurrentShow = false, TimeZoneInfo? zone = null)
    {
        var candidates = template.BackgroundChoices.Select(path => (Path: path, Weight: Weight(Rule(template, path), upcoming, at, matchCurrentShow, zone)))
            .Where(candidate => candidate.Weight > 0).ToList();
        if (candidates.Count == 0) return null;
        double fraction = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|background-rules"))) / ((double)uint.MaxValue + 1);
        double pick = fraction * candidates.Sum(candidate => candidate.Weight);
        foreach (var candidate in candidates)
        {
            pick -= candidate.Weight;
            if (pick < 0) return candidate.Path;
        }
        return candidates[^1].Path;
    }
}
