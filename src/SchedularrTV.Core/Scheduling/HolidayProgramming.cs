using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Programmes on a channel's holidays (<see cref="ChannelHoliday"/>): the holiday episodes of what the block (or base
/// rotation) plays, or of the whole library, played in their own order so the shows' usual runs carry on untouched
/// afterwards. With fewer shows than the holiday asks for, those shows get their share of the time and the rest is
/// filled from the fallback collection, or (null back to the engine) with the usual episodes.
/// </summary>
internal static class HolidayProgramming
{
    /// <summary>The holiday's pick for a slot starting <paramref name="at"/>, or null for the usual programming.</summary>
    public static async ValueTask<ProgramPick?> TakeAsync(
        ScheduleBuildSession session,
        ChannelHoliday holiday,
        string rotationKey,
        IReadOnlyList<ContentSelector> selectors,
        DateTimeOffset at,
        TimeSpan gap,
        CancellationToken cancellationToken)
    {
        if (!QueryParser.TryParse(holiday.Query, out QueryExpression query, out _))
        {
            return null;
        }

        QueryExpression pool;
        if (holiday.Global || selectors.Count == 0)
        {
            pool = query;
        }
        else
        {
            QueryExpression[] usual;
            try
            {
                usual = [.. selectors.Select(session.ExpressionOf)];
            }
            catch (SchedulerConfigurationException)
            {
                return null; // reported where the selector is scheduled
            }

            pool = QueryExpression.And([QueryExpression.Or(usual), query]);
        }

        IReadOnlyList<MediaItem> found = await session.SearchAsync(pool, cancellationToken).ConfigureAwait(false);
        int shows = found.Select(TitleRotation.TitleOf).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int minimum = Math.Max(1, holiday.MinimumShows);

        bool fits(MediaItem item) => item.Duration > TimeSpan.Zero && item.Duration <= gap;

        if (shows > 0 && HolidayTurn(session, $"{holiday.Name}|{rotationKey}", shows, minimum))
        {
            MediaSequence sequence = await session.GetPoolSequenceAsync(pool, holiday.Order ?? PlaybackOrder.Shuffle, null, null,
                $"holiday|{holiday.Name}", cancellationToken).ConfigureAwait(false);
            sequence.At = at;
            if (!sequence.IsEmpty && sequence.TryTake(fits, out MediaItem chosen))
            {
                return SchedulingEngine.Picked(chosen, sequence, SelectorFor(session, holiday, selectors, chosen));
            }
        }

        // Filling in: the fallback collection, or the usual episodes when there's none (or it has nothing that fits).
        if (holiday.Fallback is { Length: > 0 } fallback && session.Config.GlobalCollections.ContainsKey(fallback))
        {
            ContentSelector fill = Selector(session, $"fallback|{fallback}", () => new ContentSelector { Collection = fallback });
            MediaSequence? sequence = await session.GetSequenceAsync(fill, "shared", cancellationToken).ConfigureAwait(false);
            if (sequence is { IsEmpty: false })
            {
                sequence.At = at;
                if (sequence.TryTake(fits, out MediaItem chosen))
                {
                    return SchedulingEngine.Picked(chosen, sequence, fill);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this slot is the holiday's: always with enough shows, otherwise each found show's share of the
    /// minimum (two of five: two slots in five), spread evenly and the same every build.
    /// </summary>
    public static bool HolidayTurn(ScheduleBuildSession session, string key, int shows, int minimum)
    {
        if (shows >= minimum)
        {
            return true;
        }

        session.HolidayTurns.TryGetValue(key, out int turn);
        session.HolidayTurns[key] = turn + 1;
        return Turn(turn, shows, minimum);
    }

    /// <summary>Whether turn <paramref name="turn"/> (from 0) is one of the <paramref name="shows"/> in every <paramref name="minimum"/>.</summary>
    public static bool Turn(int turn, int shows, int minimum) =>
        (long)(turn + 1) * shows / minimum > (long)turn * shows / minimum;

    /// <summary>
    /// The selector a holiday episode counts as played from, for its bumpers and labels: the block's selector that has
    /// it, else one standing for the holiday's own query.
    /// </summary>
    private static ContentSelector SelectorFor(ScheduleBuildSession session, ChannelHoliday holiday, IReadOnlyList<ContentSelector> selectors, MediaItem item)
    {
        foreach (ContentSelector selector in selectors)
        {
            try
            {
                if (QueryEvaluator.Matches(item, session.ExpressionOf(selector)))
                {
                    return selector;
                }
            }
            catch (SchedulerConfigurationException)
            {
            }
        }

        return Selector(session, $"query|{holiday.Name}|{holiday.Query}", () => new ContentSelector { Query = holiday.Query });
    }

    private static ContentSelector Selector(ScheduleBuildSession session, string key, Func<ContentSelector> make)
    {
        if (!session.HolidaySelectors.TryGetValue(key, out ContentSelector? selector))
        {
            selector = make();
            session.HolidaySelectors[key] = selector;
        }

        return selector;
    }
}
