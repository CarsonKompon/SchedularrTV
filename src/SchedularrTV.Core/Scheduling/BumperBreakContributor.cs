using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// An ad break's bumpers at either end of the break ("we'll be right back", "and now back to…"): one straight after
/// any opening promo and interstitial shows, one just before the closing promos. Only for ad breaks whose bumpers
/// aren't simply mixed in with the ads.
/// </summary>
internal sealed class BumperBreakContributor : IBreakContributor
{
    public async ValueTask<IBreakPlan?> PlanAsync(BreakContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<AdPoolSource> opening = context.Interstitials.OpeningBumpers ?? [];
        IReadOnlyList<AdPoolSource> closing = context.Interstitials.ClosingBumpers ?? [];
        if (opening.Count == 0 && closing.Count == 0)
        {
            return null;
        }

        var plan = new BumperBreakPlan(context, opening);
        if (closing.Count > 0)
        {
            // Never more than half the break, so the ads still have room.
            TimeSpan room = TimeSpan.FromTicks((context.To - context.From).Ticks / 2);
            if (await TakeAsync(context, closing, "bumpers-closing", context.To, room, cancellationToken).ConfigureAwait(false) is { } clip)
            {
                plan.Tail = [Entry(context, clip)];
            }
        }

        return plan;
    }

    internal static async ValueTask<MediaItem?> TakeAsync(
        BreakContext context, IReadOnlyList<AdPoolSource> sources, string scope, DateTimeOffset at, TimeSpan room, CancellationToken cancellationToken)
    {
        if (room <= context.Tolerance)
        {
            return null;
        }

        QueryExpression pool = AdBreakCompiler.Pool(sources, null, at, context.Config.Settings.TemporalTags);
        if (pool is NoneExpression)
        {
            return null;
        }

        MediaSequence sequence = await context.Session
            .GetPoolSequenceAsync(pool, PlaybackOrder.Shuffle, null, null, scope, cancellationToken, AdBreakCompiler.PoolIdentity(sources, null))
            .ConfigureAwait(false);

        return sequence.TryTakeForGap(item => item.Duration > TimeSpan.Zero && item.Duration <= room, out MediaItem clip) ? clip : null;
    }

    internal static ScheduleEntry Entry(BreakContext context, MediaItem clip) => new()
    {
        Start = context.From,
        Duration = clip.Duration,
        Kind = ScheduleEntryKind.Commercial,
        Item = clip,
        Source = "bumper",
        BlockName = context.Block?.Name
    };
}

/// <summary>One break's bumpers.</summary>
internal sealed class BumperBreakPlan(BreakContext context, IReadOnlyList<AdPoolSource> opening) : IBreakPlan
{
    public ScheduleEntry? Opener => null;

    public IReadOnlyList<ScheduleEntry> Tail { get; set; } = [];

    public (ScheduleEntry Entry, string Salt)? Held => null;

    public async ValueTask<IReadOnlyList<ScheduleEntry>> AfterInterstitialsAsync(DateTimeOffset cursor, DateTimeOffset fillTo, CancellationToken cancellationToken)
    {
        if (opening.Count == 0)
        {
            return [];
        }

        TimeSpan room = TimeSpan.FromTicks((fillTo - cursor).Ticks / 2);
        MediaItem? clip = await BumperBreakContributor.TakeAsync(context, opening, "bumpers-opening", cursor, room, cancellationToken).ConfigureAwait(false);
        if (clip is null)
        {
            return [];
        }

        return [BumperBreakContributor.Entry(context, clip)];
    }

    public ValueTask<(ScheduleEntry Entry, string Salt)?> ReserveMixInAsync(DateTimeOffset cursor, DateTimeOffset fillTo, CancellationToken cancellationToken) =>
        ValueTask.FromResult<(ScheduleEntry Entry, string Salt)?>(null);
}
