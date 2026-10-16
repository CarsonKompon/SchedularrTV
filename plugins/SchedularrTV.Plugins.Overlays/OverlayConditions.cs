using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Overlays;

/// <summary>What's airing, as an overlay's <see cref="OverlayConditions.On"/> names it.</summary>
public static class OverlayOn
{
    /// <summary>An episode of a show.</summary>
    public const string Episode = "episode";

    /// <summary>A film.</summary>
    public const string Movie = "movie";

    /// <summary>Any programme (episodes, films and anything else that isn't a break).</summary>
    public const string Programme = "programme";

    /// <summary>An Up Next promo.</summary>
    public const string UpNext = "up_next";

    /// <summary>Any promo, Up Next included.</summary>
    public const string Promo = "promo";

    public const string Commercial = "commercial";

    /// <summary>Filler and bumpers.</summary>
    public const string Filler = "filler";

    /// <summary>Short programmes filling a break.</summary>
    public const string Interstitial = "interstitial";

    /// <summary>Anything in a break: promos, commercials, filler and interstitials.</summary>
    public const string Break = "break";

    public static readonly string[] All = [Episode, Movie, Programme, UpNext, Promo, Commercial, Filler, Interstitial, Break];

    /// <summary>Values that reach into breaks, so an overlay naming one is considered there even when it's for programmes.</summary>
    public static bool IsBreak(string value) => value is UpNext or Promo or Commercial or Filler or Interstitial or Break;

    public static string Label(string value) => value switch
    {
        Episode => "Episodes",
        Movie => "Movies",
        Programme => "Any programme",
        UpNext => "Up Next promos",
        Promo => "Any promo",
        Commercial => "Commercials",
        Filler => "Filler and bumpers",
        Interstitial => "Interstitials",
        Break => "Anything in a break",
        _ => value
    };
}

/// <summary>Whether a programme is a new episode or a repeat.</summary>
public static class OverlayAiring
{
    /// <summary>A new episode: the first airing of one released by a <c>drop</c> order.</summary>
    public const string New = "new";

    /// <summary>Anything that isn't a new episode.</summary>
    public const string Repeat = "repeat";

    /// <summary>A film (for <see cref="OverlayConditions.Next"/>).</summary>
    public const string Movie = "movie";

    /// <summary>An episode of a show, new or not (for <see cref="OverlayConditions.Next"/>).</summary>
    public const string Episode = "episode";
}

/// <summary>
/// When an overlay is allowed to show at all. Every condition that's set must hold; an empty one isn't checked. They're
/// judged on each entry, so an overlay can be for new episodes only, for films, for Up Next promos, and so on.
/// </summary>
public sealed class OverlayConditions
{
    /// <summary>What's airing: <see cref="OverlayOn"/> values, any of which will do. Empty means anything the overlay's
    /// <see cref="Overlay.AppliesTo"/> allows.</summary>
    public List<string> On { get; set; } = [];

    /// <summary>
    /// <see cref="OverlayAiring.New"/> or <see cref="OverlayAiring.Repeat"/>: the programme airing (or, in a break, the
    /// one the break interrupts or follows) is a new episode, or isn't.
    /// </summary>
    public string? Episodes { get; set; }

    /// <summary>
    /// What's on after this: <see cref="OverlayAiring.New"/> (a new episode), <see cref="OverlayAiring.Repeat"/>,
    /// <see cref="OverlayAiring.Movie"/> or <see cref="OverlayAiring.Episode"/>.
    /// </summary>
    public string? Next { get; set; }

    /// <summary>The programme matches this library query (<c>show:'Rugrats'</c>, <c>genre:'Comedy'</c>).</summary>
    public string? Query { get; set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasConditions =>
        On.Count > 0 || !string.IsNullOrWhiteSpace(Episodes) || !string.IsNullOrWhiteSpace(Next) || !string.IsNullOrWhiteSpace(Query);

    /// <summary>The conditions reach into breaks (an Up Next promo, commercials…).</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public bool TargetsBreaks => On.Any(OverlayOn.IsBreak);

    public OverlayConditions Copy()
    {
        var copy = (OverlayConditions)MemberwiseClone();
        copy.On = [.. On];
        return copy;
    }

    /// <summary>Short summary for the overlay list, e.g. "new episodes · up next".</summary>
    public string Describe()
    {
        List<string> parts = [.. On.Select(value => OverlayOn.Label(value).ToLowerInvariant())];
        if (Episodes == OverlayAiring.New) parts.Add("new episodes");
        if (Episodes == OverlayAiring.Repeat) parts.Add("repeats");
        if (!string.IsNullOrWhiteSpace(Next)) parts.Add(Next == OverlayAiring.New ? "before a new episode" : $"before a {Next}");
        if (!string.IsNullOrWhiteSpace(Query)) parts.Add("matching a query");
        return string.Join(" · ", parts);
    }
}

/// <summary>Judges <see cref="OverlayConditions"/> on one entry of the schedule.</summary>
public static class OverlayConditionMatcher
{
    /// <summary>
    /// Whether <paramref name="overlay"/>'s conditions hold on <c>ordered[index]</c>. <paramref name="programme"/> is
    /// the programme airing or, in a break, the one before it; <paramref name="next"/> is the programme after.
    /// </summary>
    public static bool Matches(Overlay overlay, ScheduleEntry entry, ScheduleEntry? programme, ScheduleEntry? next)
    {
        if (overlay.Conditions is not { HasConditions: true } conditions)
        {
            return true;
        }

        if (conditions.On.Count > 0 && !conditions.On.Any(value => IsOn(value, entry)))
        {
            return false;
        }

        if (conditions.Episodes is { Length: > 0 } episodes)
        {
            bool fresh = programme?.IsPremiere == true;
            if ((episodes == OverlayAiring.New && !fresh) || (episodes == OverlayAiring.Repeat && fresh))
            {
                return false;
            }
        }

        if (conditions.Next is { Length: > 0 } wanted && !NextIs(wanted, next))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(conditions.Query)
            && (programme?.Item is not { } item || OverlayVariants.Query(conditions.Query) is not { } query
                || !SchedularrTV.Core.Query.QueryEvaluator.Matches(item, query)))
        {
            return false;
        }

        return true;
    }

    /// <summary>Whether the entry is what <paramref name="value"/> (an <see cref="OverlayOn"/> value) names.</summary>
    public static bool IsOn(string value, ScheduleEntry entry)
    {
        bool programme = entry.Kind == ScheduleEntryKind.Program && !entry.IsSlotIntro && !entry.IsSlotOutro;
        bool promo = entry.Kind is ScheduleEntryKind.Promo or ScheduleEntryKind.UpNextPromo or ScheduleEntryKind.NowPlayingPromo
            or ScheduleEntryKind.ThisHasBeenPromo or ScheduleEntryKind.YoureWatchingPromo or ScheduleEntryKind.BackToTheShowPromo
            or ScheduleEntryKind.EventuallyPromo or ScheduleEntryKind.NewEpisodePromo or ScheduleEntryKind.RightBackPromo;
        return value switch
        {
            OverlayOn.Episode => programme && entry.Item?.Kind == MediaKind.Episode,
            OverlayOn.Movie => programme && entry.Item?.Kind == MediaKind.Movie,
            OverlayOn.Programme => programme,
            OverlayOn.UpNext => entry.Kind == ScheduleEntryKind.UpNextPromo,
            OverlayOn.Promo => promo,
            OverlayOn.Commercial => entry.Kind == ScheduleEntryKind.Commercial,
            OverlayOn.Filler => entry.Kind == ScheduleEntryKind.Filler,
            OverlayOn.Interstitial => entry.Kind == ScheduleEntryKind.Interstitial,
            OverlayOn.Break => !programme && entry.Kind != ScheduleEntryKind.Program,
            _ => false
        };
    }

    private static bool NextIs(string wanted, ScheduleEntry? next) => wanted switch
    {
        OverlayAiring.New => next?.IsPremiere == true,
        OverlayAiring.Repeat => next is not null && !next.IsPremiere,
        OverlayAiring.Movie => next?.Item?.Kind == MediaKind.Movie,
        OverlayAiring.Episode => next?.Item?.Kind == MediaKind.Episode,
        _ => true
    };
}

/// <summary>
/// Overlays that aren't additive take turns: where several would be on screen at once, one of them (picked at random,
/// but the same each time the schedule is published) shows and the others stand down. Additive ones always show.
/// </summary>
public static class OverlayContention
{
    /// <summary>One overlay that would show on an entry, with each of its timing rules' appearances there.</summary>
    public sealed record Candidate(Overlay Overlay, IReadOnlyList<(Overlay Rule, OverlayShowing Showing)> Rules);

    /// <summary>
    /// The candidates that show, in their original order: every additive one, and one from each group of non-additive
    /// ones whose appearances overlap.
    /// </summary>
    public static IReadOnlyList<Candidate> Resolve(IReadOnlyList<Candidate> candidates, ScheduleEntry entry, string seed)
    {
        List<int> exclusive = [.. Enumerable.Range(0, candidates.Count).Where(i => !IsAdditive(candidates[i].Overlay))];
        if (exclusive.Count <= 1)
        {
            return candidates;
        }

        List<List<(long From, long To)>> windows = [.. candidates.Select(candidate => Windows(candidate, entry))];

        // Group overlapping ones (an overlap chain counts as one group).
        int[] group = [.. Enumerable.Range(0, candidates.Count)];
        int Find(int i)
        {
            while (group[i] != i)
            {
                i = group[i];
            }

            return i;
        }
        for (int a = 0; a < exclusive.Count; a++)
        {
            for (int b = a + 1; b < exclusive.Count; b++)
            {
                if (Overlap(windows[exclusive[a]], windows[exclusive[b]]))
                {
                    group[Find(exclusive[b])] = Find(exclusive[a]);
                }
            }
        }

        HashSet<int> dropped = [];
        foreach (IGrouping<int, int> contest in exclusive.GroupBy(Find).Where(contest => contest.Count() > 1))
        {
            List<int> members = [.. contest.OrderBy(i => candidates[i].Overlay.Name, StringComparer.Ordinal).ThenBy(i => i)];
            string names = string.Join("|", members.Select(i => candidates[i].Overlay.Name));
            int winner = members[DeterministicRandom.FromSeedText(seed, names, "overlay-contention").Next(members.Count)];
            dropped.UnionWith(members.Where(i => i != winner));
        }

        return [.. candidates.Where((_, i) => !dropped.Contains(i))];
    }

    /// <summary>Overlays saved before the additive setting existed keep stacking, as they always did.</summary>
    public static bool IsAdditive(Overlay overlay) => overlay.Additive ?? true;

    /// <summary>When a candidate is on screen during the entry, in milliseconds from its start.</summary>
    internal static List<(long From, long To)> Windows(Candidate candidate, ScheduleEntry entry)
    {
        long length = (long)entry.Duration.TotalMilliseconds;
        List<(long From, long To)> windows = [];
        foreach ((_, OverlayShowing showing) in candidate.Rules)
        {
            if (showing.Timing is not { } timing)
            {
                windows.Add((0, length));
                continue;
            }

            long visible = Math.Max(1, timing.VisibleMs);
            long every = Math.Max(1, timing.FrequencyMs);
            long first;
            if (timing.Wall)
            {
                // Wall-clock cycles run from the Unix epoch; find the first appearance still on at the entry's start.
                long startMs = (entry.Start - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerMillisecond;
                long sinceCycle = ((startMs - timing.PhaseMs) % every + every) % every;
                first = -sinceCycle;
            }
            else
            {
                first = timing.PhaseMs;
            }

            int guard = 0;
            for (long at = first; at < length && guard < 5_000; at += every, guard++)
            {
                long to = at + visible;
                if (to > 0)
                {
                    windows.Add((Math.Max(0, at), Math.Min(length, to)));
                }
            }
        }

        return windows;
    }

    internal static bool Overlap(List<(long From, long To)> a, List<(long From, long To)> b)
    {
        // Both sorted by start, then walked together: a window can only overlap the other list's windows near it.
        List<(long From, long To)> left = [.. a.OrderBy(window => window.From)];
        List<(long From, long To)> right = [.. b.OrderBy(window => window.From)];
        int i = 0, j = 0;
        while (i < left.Count && j < right.Count)
        {
            if (left[i].From < right[j].To && right[j].From < left[i].To)
            {
                return true;
            }

            if (left[i].To <= right[j].To)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return false;
    }
}
