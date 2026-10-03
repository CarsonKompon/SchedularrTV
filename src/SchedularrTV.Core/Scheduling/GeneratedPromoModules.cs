using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Generated promos in the promo spots: picks templates while a break is planned; rendering happens later.</summary>
internal sealed class GeneratedPromoSubstitute : IPromoSpotSubstitute
{
    private static TimeSpan Epsilon => PromoBreakContributor.Epsilon;

    public bool Has(BreakContext context, GeneratedPromoType type) =>
        GeneratedPromoPlanner.Has(context.Config, context.Channel, context.Block, type);

    public GeneratedPromoTemplate? Choose(BreakContext context, GeneratedPromoType type, bool clipAvailable, TimeSpan available, DateTimeOffset at) =>
        GeneratedPromoPlanner.Choose(context.Config, context.Channel, context.Block, type, clipAvailable, available, at);

    public MediaItem Placeholder(GeneratedPromoTemplate template) => GeneratedPromoPlanner.Placeholder(template);

    /// <summary>
    /// Anchors plus the openings of upcoming blocks. Clips are only matched to anchors, but a generated
    /// promo reads the real programme from the built schedule, so a block's first show can be named too.
    /// </summary>
    public async ValueTask<IReadOnlyList<PromoTarget>> ComingUpTargetsAsync(
        BreakContext context, IReadOnlyList<PromoTarget> anchorTargets, DateTimeOffset cursor, PromoConfig promos, CancellationToken cancellationToken)
    {
        if (!Has(context, GeneratedPromoType.ComingUp))
        {
            return [];
        }

        DateTimeOffset breakEnd = context.To;
        List<PromoTarget> targets = [.. anchorTargets];
        foreach (BlockOccurrence block in context.Build.Blocks)
        {
            if (block.Start <= breakEnd + Epsilon || block.Start - cursor > promos.Lookahead
                || targets.Any(target => (target.At - block.Start).Duration() < Epsilon))
            {
                continue;
            }

            if (await PromoBreakContributor.SampleTargetAsync(context.Session, block.Block.Shows, block.Start, block.Block.Name, cancellationToken).ConfigureAwait(false) is { } target)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    public async ValueTask<(GeneratedPromoTemplate Template, UpcomingAiring Airing)?> EventuallyAsync(
        BreakContext context, PromoConfig promos, bool clipAvailable, DateTimeOffset cursor, TimeSpan available, CancellationToken cancellationToken)
    {
        if (!Has(context, GeneratedPromoType.Eventually) || !PromoSelector.EventuallyRolls(promos, context.BreakSeed))
        {
            return null;
        }

        ChannelOutlook outlook = await context.Session.GetOutlookAsync(promos.Eventually.Horizon, cancellationToken).ConfigureAwait(false);
        // Only airings with a single possible title, so the trailer can never name the wrong show.
        List<UpcomingAiring> airings = [.. outlook.Airings.Where(airing => airing.Titles.Count == 1
            && airing.At - cursor > promos.Lookahead && airing.At - cursor <= promos.Eventually.Horizon)];
        if (airings.Count > 0
            && Choose(context, GeneratedPromoType.Eventually, clipAvailable, available, context.From) is { } trailer)
        {
            return (trailer, airings[DeterministicRandom.FromSeedText(context.BreakSeed, "generated-eventually-airing").Next(airings.Count)]);
        }

        return null;
    }
}

/// <summary>
/// A render can miss its slot (or be skipped for an unwatched channel), so each generated promo carries
/// ordinary break content of the same length to air in its place instead of black.
/// </summary>
internal sealed class GeneratedPromoStandIns : IBreakFinalizer
{
    public async ValueTask FinalizeAsync(BreakContext context, int breakStart, CancellationToken cancellationToken)
    {
        List<ScheduleEntry> entries = context.Entries;
        for (int index = breakStart; index < entries.Count; index++)
        {
            ScheduleEntry generated = entries[index];
            if (generated.GeneratedTemplate is null || generated.Duration <= context.Tolerance)
            {
                continue;
            }

            List<ScheduleEntry> standIn = [];
            await context.PackAsync(generated.Start, generated.End, standIn, cancellationToken).ConfigureAwait(false);
            if (standIn.Count > 0)
            {
                entries[index] = generated with { Fallback = standIn };
            }
        }
    }
}
