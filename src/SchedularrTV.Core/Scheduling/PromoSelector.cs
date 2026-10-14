using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Identity of a show an upcoming anchor will air, plus when it airs.</summary>
/// <param name="Open">
/// No particular show: a generated promo names whatever the built schedule airs from <paramref name="At"/> on.
/// Used where the channel's rotation, rather than an anchor or block opening, decides what plays.
/// </param>
public sealed record PromoTarget(DateTimeOffset At, IReadOnlyList<string> ShowIds, string DisplayName, bool Open = false);

/// <summary>
/// Lookahead promo matching (spec 3.2). Inspects upcoming anchors and finds promo assets whose
/// <c>promo_type</c> matches how far away the promoted show is.
/// </summary>
internal sealed class PromoSelector(ScheduleBuildSession session, PromoConfig config, IReadOnlyList<string>? baseTags)
{
    public async ValueTask<IReadOnlyList<MediaItem>> SelectAsync(
        DateTimeOffset breakStart,
        TimeSpan available,
        IReadOnlyList<PromoTarget> targets,
        CancellationToken cancellationToken)
    {
        if (!config.Enabled || targets.Count == 0 || available <= TimeSpan.Zero)
        {
            return [];
        }

        IReadOnlyList<PromoWindow> windows = config.Windows.Count > 0 || config.FromAdBreaks ? config.Windows : PromoConfig.DefaultWindows;

        List<MediaItem> selected = [];
        TimeSpan remaining = available;
        HashSet<string> used = new(StringComparer.Ordinal);

        foreach (PromoTarget target in targets.OrderBy(t => t.At))
        {
            if (selected.Count >= config.MaxPerBreak || remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan delta = target.At - breakStart;
            if (delta <= TimeSpan.Zero || delta > config.Lookahead)
            {
                continue;
            }

            PromoWindow? window = windows.OrderBy(w => w.Within).FirstOrDefault(w => delta <= w.Within);
            if (window is null)
            {
                continue;
            }

            List<string> promoTypes = [.. window.PromoTypes];

            if ((promoTypes.Count == 0 && window.Folders is null) || target.ShowIds.Count == 0)
            {
                continue;
            }

            IReadOnlyList<MediaItem> matches;
            if (window.Folders is { } folders)
            {
                matches = await FolderMatchesAsync(folders, target.ShowIds, target.At, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                List<QueryExpression> clauses = InterstitialQueryBuilder.PromoClauses(config, target.ShowIds, baseTags);
                clauses.Add(new FieldExpression("promo_type", ComparisonOperator.In, promoTypes));

                matches = await session
                    .SearchAsync(QueryExpression.And(clauses), cancellationToken)
                    .ConfigureAwait(false);
            }

            MediaItem? pick = ClockMatches(matches, target.At, window.IncludeClockVariants)
                .Where(m => m.Duration > TimeSpan.Zero && m.Duration <= remaining && used.Add(m.Id))
                .OrderBy(m => m.SortKey, StringComparer.Ordinal)
                .FirstOrDefault();

            if (pick is not null)
            {
                selected.Add(pick);
                remaining -= pick.Duration;
            }
        }

        return selected;
    }

    /// <summary>
    /// Picks the promo for one fixed position in a break. Returns the longest that still fits, so the
    /// break carries as much context as it can.
    /// </summary>
    public async ValueTask<MediaItem?> SelectSpotAsync(
        PromoSpotConfig spot,
        IReadOnlyList<string> showIds,
        DateTimeOffset at,
        TimeSpan available,
        CancellationToken cancellationToken)
    {
        if (!config.Enabled || !spot.Enabled || available <= TimeSpan.Zero || showIds.Count == 0)
        {
            return null;
        }

        IReadOnlyList<MediaItem> matches;
        if (spot.Folders is { } folders)
        {
            matches = await FolderMatchesAsync(folders, showIds, at, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            QueryExpression expression = InterstitialQueryBuilder.BuildSpot(config, spot, showIds, baseTags);
            if (expression is NoneExpression)
            {
                return null;
            }

            matches = await session.SearchAsync(expression, cancellationToken).ConfigureAwait(false);
        }

        return ClockMatches(matches, at, honourClock: true)
            .Where(m => m.Duration > TimeSpan.Zero && m.Duration <= available)
            .OrderByDescending(m => m.Duration)
            .ThenBy(m => m.SortKey, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Picks a trailer for something the channel airs later in the week. Returns null most of the
    /// time: these are mixed in among the ads rather than given a slot of their own, so the odds
    /// decide whether this break carries one at all.
    /// </summary>
    public async ValueTask<MediaItem?> SelectEventuallyAsync(
        DateTimeOffset at,
        TimeSpan available,
        string seed,
        CancellationToken cancellationToken)
    {
        EventuallyConfig eventually = config.Eventually;

        if (!EventuallyRolls(config, seed) || available <= TimeSpan.Zero)
        {
            return null;
        }

        DeterministicRandom random = DeterministicRandom.FromSeedText(seed, "eventually");
        random.Next(1_000);

        IReadOnlyList<MediaItem> found;
        if (eventually.Folders is { } folders)
        {
            found = await FolderMatchesAsync(folders, null, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            QueryExpression expression = InterstitialQueryBuilder.BuildEventually(config, eventually, baseTags);
            if (expression is NoneExpression)
            {
                return null;
            }

            found = await session.SearchAsync(expression, cancellationToken).ConfigureAwait(false);
        }

        List<MediaItem> matches = [.. found.Where(m => m.Duration > TimeSpan.Zero && m.Duration <= available)];

        if (matches.Count == 0)
        {
            return null;
        }

        // Only now is the outlook worth the recurrence expansion it costs.
        ChannelOutlook outlook = await session
            .GetOutlookAsync(eventually.Horizon, cancellationToken)
            .ConfigureAwait(false);

        List<MediaItem> candidates = [.. matches.Where(m => Airs(m, outlook, at, eventually.Horizon))];
        if (candidates.Count == 0)
        {
            return null;
        }

        // Sorted first so the shuffle starts from an order the library cannot perturb.
        candidates.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        random.Shuffle(candidates);

        return candidates[0];
    }

    /// <summary>Whether this break's odds call for a trailer at all, clip or generated.</summary>
    public static bool EventuallyRolls(PromoConfig config, string seed) =>
        config is { Enabled: true, Eventually: { Enabled: true, Chance: > 0 } eventually }
        && DeterministicRandom.FromSeedText(seed, "eventually").Next(1_000) < (int)Math.Round(Math.Clamp(eventually.Chance, 0, 1) * 1_000);

    /// <summary>
    /// True when the channel really does have a slot for the promoted show, on the day and at the time
    /// the promo's folders claim. A promo filing neither only has to name a show the channel plays.
    /// </summary>
    private static bool Airs(MediaItem promo, ChannelOutlook outlook, DateTimeOffset now, TimeSpan horizon)
    {
        if (Attribute(promo, "show_id") is not { } show)
        {
            return false;
        }

        string? day = Attribute(promo, "promo_day");
        string? clock = Attribute(promo, "promo_clock");

        // Unanchored rotation time has no day or hour to check against, so an undated trailer is the
        // only kind a base-rotation show can carry.
        if (day is null && clock is null && outlook.Rotation.Contains(show))
        {
            return true;
        }

        foreach (UpcomingAiring airing in outlook.Airings)
        {
            if (airing.At <= now || airing.At - now > horizon)
            {
                continue;
            }

            if (day is not null && !string.Equals(day, airing.Day, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (clock is not null && !ClockVariants(airing.At).Contains(clock, StringComparer.Ordinal))
            {
                continue;
            }

            if (airing.Titles.Contains(show))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The clips in an ad break's promo folders for one of <paramref name="showIds"/> (or any show, when null), read
    /// for their show, day and start time. A clip filed under a weekday only counts for an airing that day.
    /// </summary>
    private async ValueTask<IReadOnlyList<MediaItem>> FolderMatchesAsync(
        IReadOnlyList<PromoFolderSource> folders,
        IReadOnlyList<string>? showIds,
        DateTimeOffset? at,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            return [];
        }

        IReadOnlyList<MediaItem> clips = await session.SearchAsync(AdBreakCompiler.PromoPool(folders), cancellationToken).ConfigureAwait(false);
        HashSet<string>? wanted = showIds is null ? null : new(showIds, StringComparer.OrdinalIgnoreCase);
        string? day = at?.DayOfWeek.ToString().ToLowerInvariant();

        // A clip made for the show beats one for any show; the generic ones are only the fallback.
        List<MediaItem> matches = [];
        List<MediaItem> generic = [];
        foreach (MediaItem clip in clips)
        {
            if (AdBreakCompiler.Describe(clip, folders) is not { } described
                || Attribute(described, "show_id") is not { } show)
            {
                continue;
            }

            bool anyShow = folders.Any(folder => string.Equals(folder.AnyShowFolder, show, StringComparison.OrdinalIgnoreCase));
            if (wanted is not null && !anyShow && !wanted.Contains(show))
            {
                continue;
            }

            if (day is not null && Attribute(described, "promo_day") is { } filedDay && !string.Equals(filedDay, day, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (anyShow && wanted is not null ? generic : matches).Add(described);
        }

        return matches.Count > 0 ? matches : generic;
    }

    private static string? Attribute(MediaItem item, string key) =>
        item.Attributes.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// Drops promos filed under a clock folder that does not match, and puts the ones that do match
    /// first, so <c>Coming Up/4pm</c> beats the generic <c>Coming Up</c> at four o'clock.
    /// </summary>
    private static IEnumerable<MediaItem> ClockMatches(
        IEnumerable<MediaItem> items,
        DateTimeOffset at,
        bool honourClock)
    {
        HashSet<string> clocks = [.. ClockVariants(at)];

        return items
            .Select(item =>
            {
                string? clock = item.Attributes.TryGetValue("promo_clock", out string? value) ? value : null;
                bool timed = !string.IsNullOrWhiteSpace(clock);
                return (Item: item, Timed: timed, Matches: timed && honourClock && clocks.Contains(clock!));
            })
            .Where(x => !x.Timed || x.Matches)
            .OrderByDescending(x => x.Matches)
            .Select(x => x.Item);
    }

    /// <summary>Spellings of a start time a clock folder may use: 4pm, 400pm, and 830am for the half hour.</summary>
    internal static IEnumerable<string> ClockVariants(DateTimeOffset at)
    {
        int hour12 = at.Hour % 12;
        if (hour12 == 0)
        {
            hour12 = 12;
        }

        string suffix = at.Hour < 12 ? "am" : "pm";

        // The hour on its own still matches a show that starts a few minutes past it.
        yield return $"{hour12}{suffix}";
        yield return $"{hour12}{at.Minute:D2}{suffix}";
    }
}
