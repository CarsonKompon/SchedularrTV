using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Promos: This Has Been / You're Watching to open a break, Coming Up after the interstitial shows, an
/// Eventually trailer and (optionally) Up Next mixed in among the ads, and Up Next / Now Playing / Back to the
/// Show to close it. A <see cref="IPromoSpotSubstitute"/> can put generated promos in any of those spots.
/// </summary>
internal sealed class PromoBreakContributor(IPromoSpotSubstitute? generated) : IBreakContributor
{
    internal static readonly TimeSpan Epsilon = TimeSpan.FromSeconds(1);

    public async ValueTask<IBreakPlan?> PlanAsync(BreakContext context, CancellationToken cancellationToken)
    {
        PromoConfig promos = context.Block?.Promos ?? context.Channel.Promos ?? context.Config.Settings.Promos;
        if (!promos.Enabled)
        {
            return null;
        }

        var plan = new PromoBreakPlan(context, promos, ResolveBaseTags(context.Block, context.Channel, promos, context.Commercials), generated);
        await plan.ReserveAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }

    /// <summary>
    /// Which brand folders promos come from. A block or channel can point promos somewhere else
    /// without restating the whole promo block; failing that they follow the adverts.
    /// </summary>
    internal static List<string>? ResolveBaseTags(
        BlockConfig? block,
        ChannelConfig channel,
        PromoConfig promos,
        CommercialsConfig commercials) =>
        block?.PromoBaseTags
        ?? channel.PromoBaseTags
        ?? (promos.BaseTags is { Count: > 0 } ? promos.BaseTags : null)
        ?? commercials.BaseTags;

    /// <summary>
    /// Peeks the next item each selector would play (without consuming it) to learn which show the
    /// upcoming slot belongs to. That identity is what promo assets are matched against.
    /// </summary>
    internal static async ValueTask<PromoTarget?> SampleTargetAsync(
        ScheduleBuildSession session,
        IEnumerable<ContentSelector> selectors,
        DateTimeOffset at,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        foreach (ContentSelector selector in selectors)
        {
            MediaSequence? sequence = await session.GetSequenceAsync(selector, "shared", cancellationToken).ConfigureAwait(false);
            MediaItem? sample = sequence?.PreviewOrder(1).FirstOrDefault();
            if (sample is null)
            {
                continue;
            }

            List<string> ids = [];
            if (sample.SeriesId is not null)
            {
                ids.Add(sample.SeriesId);
            }

            if (sample.SeriesName is not null)
            {
                ids.Add(sample.SeriesName);
            }

            if (sample.Attributes.TryGetValue("show_id", out string? alias))
            {
                ids.Add(alias);
            }

            if (ids.Count > 0)
            {
                return new PromoTarget(at, ids, sample.SeriesName ?? fallbackName);
            }
        }

        return null;
    }
}

/// <summary>Anchor promo targets already worked out in this build.</summary>
internal sealed class PromoTargetCache : Dictionary<AnchorOccurrence, PromoTarget?>
{
}

/// <summary>One break's promos.</summary>
internal sealed class PromoBreakPlan(
    BreakContext context,
    PromoConfig promoConfig,
    List<string>? promoBaseTags,
    IPromoSpotSubstitute? generated) : IBreakPlan
{
    private static TimeSpan Epsilon => PromoBreakContributor.Epsilon;

    private readonly bool _mixUpNext = promoConfig.UpNext.Placement == UpNextPlacement.AnyBreak;

    // Reserved up front so they can be placed last: these only make sense immediately before the
    // programme they lead into.
    private MediaItem? _upNext;
    private string? _generatedUpNext;
    private MediaItem? _nowPlaying;
    private string? _generatedNowPlaying;
    private MediaItem? _thisHasBeen;
    private string? _generatedThisHasBeen;
    private MediaItem? _youreWatching;
    private string? _generatedYoureWatching;
    private MediaItem? _backToTheShow;
    private string? _generatedBackToTheShow;
    private PromoTarget? _upcoming;

    private ScheduleBuildSession Session => context.Session;

    private List<ScheduleEntry> Entries => context.Entries;

    private BlockConfig? Block => context.Block;

    public ScheduleEntry? Opener { get; private set; }

    public IReadOnlyList<ScheduleEntry> Tail { get; private set; } = [];

    public (ScheduleEntry Entry, string Salt)? Held { get; private set; }

    public async ValueTask ReserveAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset from = context.From;
        DateTimeOffset to = context.To;

        // Up Next can air in any break of the show before it; the last break is index `MidRolls`.
        DateTimeOffset? upNextAt = null;
        bool upNextElsewhere = false;
        if (context.Slot is { } slot)
        {
            int upNextBreak = slot.EndsSlot && slot.MidRolls > 0 && promoConfig.UpNext.Placement == UpNextPlacement.AnyBreak
                ? DeterministicRandom.FromSeedText(context.Config.Settings.Seed, context.Channel.Id, slot.ProgrammeStart.ToString("O"), "up-next-break").Next(slot.MidRolls + 1)
                : slot.MidRolls;
            upNextAt = !slot.IsLast && slot.Index == upNextBreak ? slot.SlotEnd : null;
            upNextElsewhere = slot.IsLast && upNextBreak != slot.MidRolls;
        }

        if (context.ContinuesSlot)
        {
            // The same show is on either side, so the break is bookended rather than handed over.
            ScheduleEntry? playing = Entries.LastOrDefault(e => e.Kind == ScheduleEntryKind.Program);
            List<string> showIds = ShowIdsOf(playing?.Item);
            TimeSpan free = to - from;

            _youreWatching = await ReserveForShowAsync(promoConfig.YoureWatching, showIds, playing?.Start, free, cancellationToken)
                .ConfigureAwait(false);

            // A generated one can stand in for (or mix with) the library clip, like the other spots.
            if (promoConfig.Enabled && promoConfig.YoureWatching.Enabled && showIds.Count > 0
                && generated?.Choose(context, GeneratedPromoType.YoureWatching, _youreWatching is not null, free, from) is { } watchingTemplate)
            {
                _youreWatching = generated.Placeholder(watchingTemplate);
                _generatedYoureWatching = watchingTemplate.Name;
            }

            free -= _youreWatching?.Duration ?? TimeSpan.Zero;

            _backToTheShow = await ReserveForShowAsync(promoConfig.BackToTheShow, showIds, playing?.Start, free, cancellationToken)
                .ConfigureAwait(false);

            if (promoConfig.Enabled && promoConfig.BackToTheShow.Enabled && showIds.Count > 0
                && generated?.Choose(context, GeneratedPromoType.BackToTheShow, _backToTheShow is not null, free, to) is { } backTemplate)
            {
                _backToTheShow = generated.Placeholder(backTemplate);
                _generatedBackToTheShow = backTemplate.Name;
            }

            // This mid-roll was picked to carry the promo for whatever follows the current show.
            if (upNextAt is { } nextShowAt)
            {
                _upcoming = await ResolveUpNextTargetAsync(nextShowAt, cancellationToken).ConfigureAwait(false);
                (_upNext, _generatedUpNext) = await ReserveUpNextAsync(free - (_backToTheShow?.Duration ?? TimeSpan.Zero), cancellationToken).ConfigureAwait(false);
            }
        }
        else if (context.Placement == BreakPlacement.BetweenProgrammes)
        {
            _upcoming = await ResolveUpNextTargetAsync(to, cancellationToken).ConfigureAwait(false);

            TimeSpan free = to - from;

            if (!upNextElsewhere)
            {
                (_upNext, _generatedUpNext) = await ReserveUpNextAsync(free, cancellationToken).ConfigureAwait(false);
            }

            free -= _upNext?.Duration ?? TimeSpan.Zero;

            _nowPlaying = await ReserveSpotAsync(promoConfig.NowPlaying, _upcoming, free, cancellationToken)
                .ConfigureAwait(false);

            if (promoConfig.NowPlaying.Enabled && _upcoming is not null
                && !(promoConfig.NowPlaying.SkipWhenSameSeries && ContinuesSameSeries(_upcoming.ShowIds))
                && generated?.Choose(context, GeneratedPromoType.NowPlaying, _nowPlaying is not null, free, to) is { } identTemplate)
            {
                _nowPlaying = generated.Placeholder(identTemplate);
                _generatedNowPlaying = identTemplate.Name;
            }

            free -= _nowPlaying?.Duration ?? TimeSpan.Zero;

            // Only once the slot is done, so a stacked slot signs off after the last episode rather
            // than between every one of them.
            if (context.EndsSlot)
            {
                _thisHasBeen = await ReserveSignOffAsync(free, cancellationToken).ConfigureAwait(false);

                List<string> finishedIds = ShowIdsOf(Entries.LastOrDefault(e => e.Kind == ScheduleEntryKind.Program)?.Item);
                bool sameShow = promoConfig.ThisHasBeen.SkipWhenSameSeries && _upcoming is not null
                    && _upcoming.ShowIds.Intersect(finishedIds, StringComparer.OrdinalIgnoreCase).Any();
                if (promoConfig.ThisHasBeen.Enabled && finishedIds.Count > 0 && !sameShow
                    && generated?.Choose(context, GeneratedPromoType.ThisHasBeen, _thisHasBeen is not null, free, from) is { } signOffTemplate)
                {
                    _thisHasBeen = generated.Placeholder(signOffTemplate);
                    _generatedThisHasBeen = signOffTemplate.Name;
                }
            }
        }

        Opener = BuildOpener();

        List<ScheduleEntry> tail = [];
        if (_upNext is not null)
        {
            if (_mixUpNext)
            {
                // A mixed-in Up Next still needs its room kept free while everything else is packed.
                Held = (UpNextEntry(_upNext), "up-next-slot");
            }
            else
            {
                tail.Add(UpNextEntry(_upNext));
            }
        }

        if (_nowPlaying is not null)
        {
            tail.Add(new ScheduleEntry
            {
                Start = to,
                Duration = _nowPlaying.Duration,
                Kind = ScheduleEntryKind.NowPlayingPromo,
                Item = _nowPlaying,
                Source = _generatedNowPlaying is null ? "now playing" : "generated now playing",
                BlockName = Block?.Name,
                PromoTargetId = _nowPlaying.Attributes.TryGetValue("show_id", out string? ident) ? ident : _nowPlaying.SeriesId,
                GeneratedTemplate = _generatedNowPlaying
            });
        }

        if (_backToTheShow is not null)
        {
            tail.Add(new ScheduleEntry
            {
                Start = to,
                Duration = _backToTheShow.Duration,
                Kind = ScheduleEntryKind.BackToTheShowPromo,
                Item = _backToTheShow,
                Source = _generatedBackToTheShow is null ? "back to the show" : "generated back to the show",
                BlockName = Block?.Name,
                PromoTargetId = _backToTheShow.Attributes.TryGetValue("show_id", out string? back) ? back : _backToTheShow.SeriesId,
                GeneratedTemplate = _generatedBackToTheShow
            });
        }

        Tail = tail;
    }

    /// <summary>Lookahead promos for shows further out than the one starting as the break ends.</summary>
    public async ValueTask<IReadOnlyList<ScheduleEntry>> AfterInterstitialsAsync(DateTimeOffset cursor, DateTimeOffset fillTo, CancellationToken cancellationToken)
    {
        if (fillTo - cursor <= context.Tolerance)
        {
            return [];
        }

        IReadOnlyList<PromoTarget> targets = await ResolvePromoTargetsAsync(cursor, cancellationToken).ConfigureAwait(false);

        var selector = new PromoSelector(Session, promoConfig, promoBaseTags);
        IReadOnlyList<MediaItem> promos = await selector
            .SelectAsync(cursor, fillTo - cursor, targets, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PromoTarget> generatedTargets = generated is null ? []
            : await generated.ComingUpTargetsAsync(context, targets, cursor, promoConfig, cancellationToken).ConfigureAwait(false);

        List<ScheduleEntry> entries = [];
        foreach ((MediaItem promo, PromoTarget? generatedFor, string? template) in PlanComingUp(promos, generatedTargets, cursor, fillTo - cursor))
        {
            entries.Add(new ScheduleEntry
            {
                Start = cursor,
                Duration = promo.Duration,
                Kind = ScheduleEntryKind.Promo,
                Item = promo,
                Source = template is null ? "promo" : "generated coming up",
                BlockName = Block?.Name,
                PromoTargetId = generatedFor?.DisplayName
                    ?? (promo.Attributes.TryGetValue("show_id", out string? target) ? target : promo.SeriesId),
                GeneratedTemplate = template,
                GeneratedTargetAt = generatedFor?.At,
                GeneratedSubject = generatedFor?.DisplayName
            });
        }

        return entries;
    }

    /// <summary>The Eventually trailer, dropped in among the ads after packing so the knapsack still sees one contiguous break.</summary>
    public async ValueTask<(ScheduleEntry Entry, string Salt)?> ReserveMixInAsync(DateTimeOffset cursor, DateTimeOffset fillTo, CancellationToken cancellationToken)
    {
        if (fillTo - cursor <= context.Tolerance)
        {
            return null;
        }

        MediaItem? eventually = await new PromoSelector(Session, promoConfig, promoBaseTags)
            .SelectEventuallyAsync(cursor, fillTo - cursor, context.BreakSeed, cancellationToken)
            .ConfigureAwait(false);

        UpcomingAiring? generatedEventually = null;
        string? eventuallyTemplate = null;
        if (generated is not null
            && await generated.EventuallyAsync(context, promoConfig, eventually is not null, cursor, fillTo - cursor, cancellationToken).ConfigureAwait(false)
                is { } trailer)
        {
            generatedEventually = trailer.Airing;
            eventually = generated.Placeholder(trailer.Template);
            eventuallyTemplate = trailer.Template.Name;
        }

        if (eventually is null)
        {
            return null;
        }

        return (new ScheduleEntry
        {
            Start = cursor,
            Duration = eventually.Duration,
            Kind = ScheduleEntryKind.EventuallyPromo,
            Item = eventually,
            Source = eventuallyTemplate is null ? "eventually" : "generated eventually",
            BlockName = Block?.Name,
            PromoTargetId = generatedEventually?.Titles.First()
                ?? (eventually.Attributes.TryGetValue("show_id", out string? eventuallyId) ? eventuallyId : eventually.SeriesId),
            GeneratedTemplate = eventuallyTemplate,
            GeneratedTargetAt = generatedEventually?.At,
            GeneratedSubject = generatedEventually?.Titles.First()
        }, "eventually-slot");
    }

    private ScheduleEntry? BuildOpener()
    {
        MediaItem? opener = _thisHasBeen ?? _youreWatching;
        if (opener is null)
        {
            return null;
        }

        return new ScheduleEntry
        {
            Start = context.From,
            Duration = opener.Duration,
            Kind = _thisHasBeen is not null
                ? ScheduleEntryKind.ThisHasBeenPromo
                : ScheduleEntryKind.YoureWatchingPromo,
            Item = opener,
            Source = _thisHasBeen is not null
                ? _generatedThisHasBeen is null ? "this has been" : "generated this has been"
                : _generatedYoureWatching is null ? "youre watching" : "generated youre watching",
            BlockName = Block?.Name,
            PromoTargetId = opener.Attributes.TryGetValue("show_id", out string? signOff) ? signOff : opener.SeriesId,
            GeneratedTemplate = _thisHasBeen is not null ? _generatedThisHasBeen : _generatedYoureWatching
        };
    }

    private ScheduleEntry UpNextEntry(MediaItem promo) => new()
    {
        Start = context.To,
        Duration = promo.Duration,
        Kind = ScheduleEntryKind.UpNextPromo,
        Item = promo,
        Source = _generatedUpNext is null ? "up next" : "generated up next",
        BlockName = Block?.Name,
        PromoTargetId = promo.Attributes.TryGetValue("show_id", out string? id) ? id : promo.SeriesId,
        GeneratedTemplate = _generatedUpNext
    };

    // A library clip, or a generated template when one applies, for the show in `_upcoming`.
    private async ValueTask<(MediaItem? Promo, string? Template)> ReserveUpNextAsync(TimeSpan free, CancellationToken cancellationToken)
    {
        MediaItem? clip = await ReserveSpotAsync(promoConfig.UpNext, _upcoming, free, cancellationToken).ConfigureAwait(false);

        return promoConfig.UpNext.Enabled && _upcoming is not null
            && !(promoConfig.UpNext.SkipWhenSameSeries && ContinuesSameSeries(_upcoming.ShowIds))
            && generated?.Choose(context, GeneratedPromoType.UpNext, clip is not null, free, context.To) is { } template
            ? (generated.Placeholder(template), template.Name)
            : (clip, null);
    }

    /// <summary>
    /// Library lookahead promos first, then at most one generated Coming Up for a later show no clip
    /// already covers. The programme starting as the break ends belongs to Up Next, so it is skipped.
    /// </summary>
    private IEnumerable<(MediaItem Promo, PromoTarget? Target, string? Template)> PlanComingUp(
        IReadOnlyList<MediaItem> promos,
        IReadOnlyList<PromoTarget> targets,
        DateTimeOffset cursor,
        TimeSpan available)
    {
        DateTimeOffset breakEnd = context.To;
        TimeSpan remaining = available;
        HashSet<string> promoted = new(_upcoming?.ShowIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (MediaItem promo in promos)
        {
            remaining -= promo.Duration;
            if (promo.Attributes.TryGetValue("show_id", out string? show)) promoted.Add(show);
            yield return (promo, null, null);
        }

        if (promos.Count >= promoConfig.MaxPerBreak || generated is null || !generated.Has(context, GeneratedPromoType.ComingUp))
        {
            yield break;
        }

        foreach (PromoTarget target in targets.OrderBy(target => target.At))
        {
            if (target.At <= breakEnd + Epsilon || target.At - cursor > promoConfig.Lookahead
                || target.ShowIds.Any(promoted.Contains))
            {
                continue;
            }

            if (generated.Choose(context, GeneratedPromoType.ComingUp, promos.Count > 0, remaining, cursor) is { } template)
            {
                yield return (generated.Placeholder(template), target, template.Name);
            }

            yield break;
        }
    }

    private async ValueTask<MediaItem?> ReserveSpotAsync(
        PromoSpotConfig spot,
        PromoTarget? target,
        TimeSpan available,
        CancellationToken cancellationToken)
    {
        if (!promoConfig.Enabled || !spot.Enabled || target is null || available <= TimeSpan.Zero)
        {
            return null;
        }

        if (spot.SkipWhenSameSeries && ContinuesSameSeries(target.ShowIds))
        {
            return null;
        }

        return await new PromoSelector(Session, promoConfig, promoBaseTags)
            .SelectSpotAsync(spot, target.ShowIds, target.At, available, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Picks a promo for a show that is already known, with no handover to reason about.</summary>
    private async ValueTask<MediaItem?> ReserveForShowAsync(
        PromoSpotConfig spot,
        IReadOnlyList<string> showIds,
        DateTimeOffset? at,
        TimeSpan available,
        CancellationToken cancellationToken)
    {
        if (!promoConfig.Enabled || !spot.Enabled || showIds.Count == 0 || at is null || available <= TimeSpan.Zero)
        {
            return null;
        }

        return await new PromoSelector(Session, promoConfig, promoBaseTags)
            .SelectSpotAsync(spot, showIds, at.Value, available, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Signs off the programme that just played, so the target is behind rather than ahead.</summary>
    private async ValueTask<MediaItem?> ReserveSignOffAsync(TimeSpan available, CancellationToken cancellationToken)
    {
        PromoSpotConfig spot = promoConfig.ThisHasBeen;

        if (!promoConfig.Enabled || !spot.Enabled || available <= TimeSpan.Zero)
        {
            return null;
        }

        ScheduleEntry? finished = Entries.LastOrDefault(e => e.Kind == ScheduleEntryKind.Program);
        List<string> showIds = ShowIdsOf(finished?.Item);

        if (showIds.Count == 0)
        {
            return null;
        }

        // Signing off a show that is about to play again reads as a mistake.
        if (spot.SkipWhenSameSeries
            && _upcoming is not null
            && _upcoming.ShowIds.Intersect(showIds, StringComparer.OrdinalIgnoreCase).Any())
        {
            return null;
        }

        return await new PromoSelector(Session, promoConfig, promoBaseTags)
            .SelectSpotAsync(spot, showIds, finished!.Start, available, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool ContinuesSameSeries(IReadOnlyList<string> showIds)
    {
        string? previousSeries = Entries.LastOrDefault(e => e.Kind == ScheduleEntryKind.Program)?.Item?.SeriesName;
        return previousSeries is not null && showIds.Contains(previousSeries, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ShowIdsOf(MediaItem? item)
    {
        List<string> ids = [];

        if (item is null)
        {
            return ids;
        }

        if (item.SeriesId is not null)
        {
            ids.Add(item.SeriesId);
        }

        if (item.SeriesName is not null)
        {
            ids.Add(item.SeriesName);
        }

        return ids;
    }

    /// <summary>Identifies the programme that starts the instant the current break ends.</summary>
    private async ValueTask<PromoTarget?> ResolveUpNextTargetAsync(DateTimeOffset boundary, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnchorOccurrence> anchors = context.Build.Anchors;
        for (int i = context.Build.AnchorIndex; i < anchors.Count && anchors[i].Start <= boundary + Epsilon; i++)
        {
            if (anchors[i].Start >= boundary - Epsilon)
            {
                return await BuildTargetAsync(anchors[i], cancellationToken).ConfigureAwait(false);
            }
        }

        BlockOccurrence? active = context.Build.Blocks.FirstOrDefault(b => b.Start <= boundary + Epsilon && b.End > boundary);

        List<ContentSelector> selectors = active is not null && active.Block.Shows.Count > 0
            ? active.Block.Shows
            : context.Channel.BaseRotation is not null ? [context.Channel.BaseRotation] : [];

        return await PromoBreakContributor.SampleTargetAsync(Session, selectors, boundary, active?.Block.Name ?? "up next", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<PromoTarget>> ResolvePromoTargetsAsync(DateTimeOffset from, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnchorOccurrence> anchors = context.Build.Anchors;
        PromoTargetCache cache = context.Build.State<PromoTargetCache>();
        List<PromoTarget> targets = [];

        for (int i = context.Build.AnchorIndex; i < anchors.Count; i++)
        {
            AnchorOccurrence anchor = anchors[i];
            if (anchor.Start <= from)
            {
                continue;
            }

            if (anchor.Start - from > promoConfig.Lookahead)
            {
                break;
            }

            if (!cache.TryGetValue(anchor, out PromoTarget? target))
            {
                target = await BuildTargetAsync(anchor, cancellationToken).ConfigureAwait(false);
                cache[anchor] = target;
            }

            if (target is not null)
            {
                targets.Add(target with { At = anchor.Start });
            }
        }

        return targets;
    }

    private ValueTask<PromoTarget?> BuildTargetAsync(AnchorOccurrence anchor, CancellationToken cancellationToken) =>
        PromoBreakContributor.SampleTargetAsync(Session, anchor.Anchor.AllSelectors(), anchor.Start, anchor.Name, cancellationToken);
}
