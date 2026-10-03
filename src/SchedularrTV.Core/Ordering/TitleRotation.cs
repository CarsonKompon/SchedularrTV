using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Ordering;

/// <summary>
/// Lays a pool out so whole titles take turns, rather than letting one sort key decide the running
/// order of everything at once.
/// </summary>
/// <remarks>
/// A flat <see cref="PlaybackOrder.Chronological"/> pool sorts every episode of every show by air
/// date, so the oldest series plays to exhaustion before the next one starts. Grouping by title and
/// round-robining between the groups keeps the mix while each title still advances in its own order.
/// </remarks>
public static class TitleRotation
{
    /// <summary>Per-title overrides, keyed by the title as the library reports it.</summary>
    public sealed record TitleRule(PlaybackOrder? Order, int Weight);

    public static List<MediaItem> Build(
        IEnumerable<MediaItem> items,
        TitleOrder titleOrder,
        PlaybackOrder episodeOrder,
        IReadOnlyDictionary<string, TitleRule> rules,
        IReadOnlyList<string> preferredOrder,
        string seed)
    {
        List<IGrouping<string, MediaItem>> groups = [.. items.GroupBy(TitleOf, StringComparer.OrdinalIgnoreCase)];

        if (groups.Count <= 1)
        {
            // Nothing to interleave; fall back to a plain sort so behaviour is unchanged.
            return groups.Count == 0 ? [] : Lay(groups[0], episodeOrder, rules, seed);
        }

        List<Queue<MediaItem>> queues = [];
        List<int> weights = [];

        foreach (IGrouping<string, MediaItem> group in Arrange(groups, titleOrder, preferredOrder, seed))
        {
            rules.TryGetValue(group.Key, out TitleRule? rule);
            queues.Add(new Queue<MediaItem>(Lay(group, episodeOrder, rules, seed)));
            weights.Add(Math.Max(1, rule?.Weight ?? 1));
        }

        List<MediaItem> ordered = new(groups.Sum(g => g.Count()));
        List<int> credit = [.. queues.Select(_ => 0)];

        // Smooth weighted round-robin: a weight of 3 earns three turns per cycle but spread through
        // it, so a heavily weighted title does not play three episodes back to back.
        while (true)
        {
            int total = 0;

            for (int i = 0; i < queues.Count; i++)
            {
                if (queues[i].Count > 0)
                {
                    credit[i] += weights[i];
                    total += weights[i];
                }
            }

            if (total == 0)
            {
                break;
            }

            int best = -1;
            for (int i = 0; i < queues.Count; i++)
            {
                if (queues[i].Count > 0 && (best < 0 || credit[i] > credit[best]))
                {
                    best = i;
                }
            }

            ordered.Add(queues[best].Dequeue());
            credit[best] -= total;
        }

        return ordered;
    }

    /// <summary>
    /// Orders one title's items. Shuffle and random normally happen at take-time through the bag, but a
    /// pre-ordered pool is walked straight through, so the shuffle has to be baked in here.
    /// </summary>
    private static List<MediaItem> Lay(
        IGrouping<string, MediaItem> group,
        PlaybackOrder episodeOrder,
        IReadOnlyDictionary<string, TitleRule> rules,
        string seed)
    {
        rules.TryGetValue(group.Key, out TitleRule? rule);
        PlaybackOrder order = rule?.Order ?? episodeOrder;

        List<MediaItem> laid = MediaSequence.SortFor(group, order);

        if (order is PlaybackOrder.Shuffle or PlaybackOrder.Random)
        {
            DeterministicRandom.FromSeedText(seed, group.Key).Shuffle(laid);
        }

        return laid;
    }

    /// <summary>Groups a title by series, treating a film or loose clip as a title of its own.</summary>
    public static string TitleOf(MediaItem item) =>
        !string.IsNullOrWhiteSpace(item.SeriesName) ? item.SeriesName : item.Title;

    /// <summary>Titles named in the collection lead, in the order they were added.</summary>
    internal static IEnumerable<IGrouping<string, MediaItem>> Arrange(
        List<IGrouping<string, MediaItem>> groups,
        TitleOrder titleOrder,
        IReadOnlyList<string> preferredOrder,
        string seed)
    {
        if (titleOrder == TitleOrder.Shuffle)
        {
            List<IGrouping<string, MediaItem>> shuffled = [.. groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)];
            DeterministicRandom.FromSeedText(seed, "\u0000titles").Shuffle(shuffled);
            return shuffled;
        }

        Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < preferredOrder.Count; i++)
        {
            rank.TryAdd(preferredOrder[i], i);
        }

        return groups
            .OrderBy(g => rank.TryGetValue(g.Key, out int index) ? index : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Flattens a selector's includes into per-title overrides.</summary>
    public static (Dictionary<string, TitleRule> Rules, List<string> Order) RulesFrom(IEnumerable<ContentInclude> includes)
    {
        Dictionary<string, TitleRule> rules = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (ContentInclude include in includes)
        {
            string? title = include.Show ?? include.Movie;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            order.Add(title);
            rules[title] = new TitleRule(include.Order, include.Weight ?? 1);
        }

        return (rules, order);
    }
}
