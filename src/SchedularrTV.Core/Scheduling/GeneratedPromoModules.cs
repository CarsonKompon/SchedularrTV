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
        GeneratedPromoPlanner.Choose(context.Config, context.Channel, context.Block, type, clipAvailable, available, at,
            endsAt: type is GeneratedPromoType.UpNext or GeneratedPromoType.NowPlaying or GeneratedPromoType.BackToTheShow);

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

        // A channel run by its rotation (or deep inside a long block) has no fixed point ahead to promote, so the
        // promo names whatever the built schedule airs a while from now instead.
        if (!targets.Any(target => target.At > breakEnd + Epsilon && target.At - cursor <= promos.Lookahead)
            && OpenTarget(context.BreakSeed, "generated-coming-up-later", breakEnd, LaterFrom, Min(LaterTo, promos.Lookahead)) is { } later)
        {
            targets.Add(later);
        }

        return targets;
    }

    // How far past the break an open Coming Up looks: beyond the next show or two (Up Next covers those).
    private static readonly TimeSpan LaterFrom = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan LaterTo = TimeSpan.FromHours(3);

    // An open Eventually has to stay inside what the schedule will have built by the time it's rendered.
    private static readonly TimeSpan BuiltMargin = TimeSpan.FromHours(2);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static PromoTarget? OpenTarget(string seed, string salt, DateTimeOffset from, TimeSpan earliest, TimeSpan latest)
    {
        if (latest <= earliest)
        {
            return null;
        }

        int minutes = DeterministicRandom.FromSeedText(seed, salt).Next((int)(latest - earliest).TotalMinutes + 1);
        return new PromoTarget(from + earliest + TimeSpan.FromMinutes(minutes), [], "", Open: true);
    }

    /// <summary>
    /// A trailer and the airing it advertises. The airing's <see cref="UpcomingAiring.Titles"/> holds the predicted show,
    /// or is empty when only the built schedule can say (a block of several shows, or rotation time).
    /// </summary>
    public async ValueTask<(GeneratedPromoTemplate Template, UpcomingAiring Airing)?> EventuallyAsync(
        BreakContext context, PromoConfig promos, bool clipAvailable, DateTimeOffset cursor, TimeSpan available, CancellationToken cancellationToken)
    {
        if (!Has(context, GeneratedPromoType.Eventually) || !PromoSelector.EventuallyRolls(promos, context.BreakSeed))
        {
            return null;
        }

        // What the schedule will reach by render time (renders happen shortly before the trailer airs).
        TimeSpan built = context.Config.Settings.Horizon - BuiltMargin;
        ChannelOutlook outlook = await context.Session.GetOutlookAsync(promos.Eventually.Horizon, cancellationToken).ConfigureAwait(false);

        // A single possible title can be named from the prediction; several (a block of shows) only once the schedule
        // reaches it, so those stay within the build horizon. Either way the trailer never names the wrong show.
        List<UpcomingAiring> airings = [.. outlook.Airings
            .Where(airing => airing.At - cursor > promos.Lookahead && airing.At - cursor <= promos.Eventually.Horizon
                && (airing.Titles.Count == 1 || (airing.Titles.Count > 1 && airing.At - cursor <= built)))
            .Select(airing => airing.Titles.Count == 1 ? airing : airing with { Titles = new HashSet<string>() })];

        // Nothing fixed ahead (a rotation-run channel): a time a day or so out, named from the built schedule.
        if (airings.Count == 0
            && OpenTarget(context.BreakSeed, "generated-eventually-later", cursor, promos.Lookahead + TimeSpan.FromMinutes(1),
                Min(built, promos.Eventually.Horizon)) is { } open)
        {
            airings.Add(new UpcomingAiring(open.At, open.At.DayOfWeek.ToString().ToLowerInvariant(), new HashSet<string>()));
        }

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
        TimeZoneInfo zone = SchedulingEngine.ResolveTimeZone(string.IsNullOrWhiteSpace(context.Channel.TimeZone)
            ? context.Config.Settings.TimeZone : context.Channel.TimeZone);
        for (int index = breakStart; index < entries.Count; index++)
        {
            ScheduleEntry generated = entries[index];
            if (generated.GeneratedTemplate is null || generated.Duration <= context.Tolerance)
            {
                continue;
            }

            List<ScheduleEntry> standIn = [];
            await context.PackAsync(generated.Start, generated.End, standIn, cancellationToken).ConfigureAwait(false);
            GeneratedPromoTemplate? template = GeneratedPromoPlanner.Source(context.Config, context.Channel, generated.BlockName)?.Promos.Templates
                .FirstOrDefault(candidate => string.Equals(candidate.Name, generated.GeneratedTemplate, StringComparison.OrdinalIgnoreCase));
            // Packing can move held/tail promos across an eligibility boundary. Check the final airtime too.
            if (template is not null && !GeneratedPromoEligibility.IsAvailable(template, generated.Start, zone))
            {
                entries.RemoveAt(index);
                entries.InsertRange(index, standIn);
                index += standIn.Count - 1;
                continue;
            }
            if (standIn.Count > 0)
            {
                entries[index] = generated with { Fallback = standIn };
            }
        }
    }
}
