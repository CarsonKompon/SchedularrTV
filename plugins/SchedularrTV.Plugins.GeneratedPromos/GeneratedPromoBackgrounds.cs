using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// Weighted, repeatable picks: a background (from a template or one of its variants), a variant, or one of a layer's
/// files or wordings. Each option can have a rule (the shows it suits, a daily time range, how much more likely it is).
/// </summary>
public static class GeneratedPromoBackgrounds
{
    public static GeneratedBackgroundRule? Rule(IGeneratedBackgrounds host, string path) =>
        host.BackgroundRules?.FirstOrDefault(rule => string.Equals(rule.Background, path, StringComparison.OrdinalIgnoreCase));

    public static double Weight(GeneratedBackgroundRule? rule, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at,
        bool matchCurrentShow = false, TimeZoneInfo? zone = null)
    {
        if (rule is null) return 1;
        if (rule.TimeRange is { } range)
        {
            TimeSpan local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Utc).TimeOfDay;
            if (!GeneratedPromoEligibility.Contains(range, local)) return 0;
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
    public static string? Select(IGeneratedBackgrounds host, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at, string seed,
        bool matchCurrentShow = false, TimeZoneInfo? zone = null) =>
        Pick(host.BackgroundChoices.Select(path => (path, Weight(Rule(host, path), upcoming, at, matchCurrentShow, zone))), $"{seed}|background-rules");

    /// <summary>The largest weight any of <paramref name="host"/>'s backgrounds has now (0: none can be used; 1 with none, or none with rules).</summary>
    public static double BestWeight(IGeneratedBackgrounds host, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at,
        bool matchCurrentShow = false, TimeZoneInfo? zone = null) =>
        host.BackgroundChoices.Count == 0 ? 1
            : host.BackgroundChoices.Max(path => Weight(Rule(host, path), upcoming, at, matchCurrentShow, zone));

    /// <summary>One of <paramref name="options"/>, more likely the heavier it is, fixed by <paramref name="seed"/>; null when none weighs anything.</summary>
    public static T? Pick<T>(IEnumerable<(T Option, double Weight)> options, string seed) where T : class
    {
        List<(T Option, double Weight)> candidates = [.. options.Where(candidate => candidate.Weight > 0 && double.IsFinite(candidate.Weight))];
        if (candidates.Count == 0) return null;
        double fraction = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed))) / ((double)uint.MaxValue + 1);
        double pick = fraction * candidates.Sum(candidate => candidate.Weight);
        foreach ((T option, double weight) in candidates)
        {
            pick -= weight;
            if (pick < 0) return option;
        }

        return candidates[^1].Option;
    }

    /// <summary>
    /// The variant a promo shows, or null for the template's default look. Variants that are off, that the airtime or
    /// programme doesn't suit, or whose backgrounds can't be used now are left out; the rest (and the default look,
    /// unless its weight is 0) are picked between by weight, and a variant's backgrounds that suit the shows better
    /// make it likelier as they did backgrounds. With nothing to pick, it's the default look.
    /// </summary>
    /// <param name="programme">The programme the promo is about, for variants limited by a library query; null suits none of those.</param>
    public static GeneratedPromoVariant? SelectVariant(GeneratedPromoTemplate template, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at,
        string seed, MediaItem? programme, bool matchCurrentShow = false, TimeZoneInfo? zone = null)
    {
        if (template.Variants is not { Count: > 0 } variants)
        {
            return null;
        }

        TimeZoneInfo local = zone ?? TimeZoneInfo.Utc;
        List<(GeneratedPromoVariant? Option, double Weight)> pool = [];
        double defaultWeight = template.DefaultWeight is { } own && double.IsFinite(own) ? Math.Clamp(own, 0, 1000) : 1;
        if (defaultWeight > 0)
        {
            pool.Add((null, defaultWeight * BestWeight(template, upcoming, at, matchCurrentShow, local)));
        }

        foreach (GeneratedPromoVariant variant in variants)
        {
            if (!variant.Enabled || !GeneratedPromoEligibility.IsAvailable(variant.Availability, at, local)
                || !GeneratedPromoEligibility.SuitsProgramme(variant.Availability, programme))
            {
                continue;
            }

            double weight = double.IsFinite(variant.Weight) ? Math.Clamp(variant.Weight, 0, 1000) : 1;
            pool.Add((variant, weight * BestWeight(variant, upcoming, at, matchCurrentShow, local)));
        }

        // The default look stands in for "nothing picked", so it's wrapped to tell it apart from no pick at all.
        Holder? picked = Pick(pool.Select(candidate => (new Holder(candidate.Option), candidate.Weight)), $"{seed}|variant");
        return picked?.Variant;
    }

    private sealed record Holder(GeneratedPromoVariant? Variant);

    /// <summary>
    /// Which of a layer's files or wordings a promo uses: its own, or one of its <see cref="GeneratedLayer.Alternatives"/>,
    /// by their rules, fixed by <paramref name="seed"/>. Null when the layer has no alternatives (it uses its own), or
    /// when none can be used now (it then uses its own too).
    /// </summary>
    public static string? SelectChoice(GeneratedLayer layer, IReadOnlyList<UpNextListing> upcoming, DateTimeOffset at, string seed,
        bool matchCurrentShow = false, TimeZoneInfo? zone = null)
    {
        if (layer.Alternatives is not { Count: > 0 } alternatives)
        {
            return null;
        }

        string own = (layer.Kind == GeneratedLayerKind.Text ? layer.Text : layer.Path) ?? string.Empty;
        IEnumerable<(string Value, double Weight)> options = alternatives
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
            .Select(choice => (choice.Value, Weight(choice.Rule, upcoming, at, matchCurrentShow, zone)))
            .Prepend((own, string.IsNullOrWhiteSpace(own) ? 0 : Weight(layer.ChoiceRule, upcoming, at, matchCurrentShow, zone)));
        return Pick(options, $"{seed}|choice");
    }
}
