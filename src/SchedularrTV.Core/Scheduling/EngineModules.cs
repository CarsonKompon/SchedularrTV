using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Scheduling;

// ---------------------------------------------------------------------------------------------------------------
// Planner extension points. The engine keeps the timeline, programme selection, stacking, bumpers, mid-roll
// segmentation and break packing (interstitial shows, commercials, filler, padding). Features that add to that
// (Archive Follow, promos, generated promos) plug in here. They are internal for now and will become plugin
// contracts once they've settled; see docs/engine-extension-points.md.
//
// Rule for implementations: same inputs, same output, and the same sequences consumed in the same order, since
// playback cursors are saved between builds and every later break depends on them.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>
/// A feature of the planner that a bundled plugin switches on. The scheduler only runs the features of the
/// plugins that are enabled; their settings stay in the schedule but are ignored while the plugin is off.
/// </summary>
/// <remarks>The planner's extension points are internal for now, so only the bundled features exist.</remarks>
public sealed class PlannerFeature
{
    private PlannerFeature(string id) => Id = id;

    /// <summary>The id of the plugin that provides it.</summary>
    public string Id { get; }

    /// <summary>Promo clips in breaks (Up Next, Coming Up, Eventually and the rest).</summary>
    public static PlannerFeature Promos { get; } = new(BuiltInPluginIds.Promos);

    /// <summary>Promos rendered from templates in place of (or mixed with) promo clips. Needs <see cref="Promos"/>.</summary>
    public static PlannerFeature GeneratedPromos { get; } = new(BuiltInPluginIds.GeneratedPromos);

    /// <summary>Replays an archived schedule over the channel's own blocks.</summary>
    public static PlannerFeature ArchiveFollow { get; } = new(BuiltInPluginIds.ArchiveFollow);

    public static IReadOnlyList<PlannerFeature> All { get; } = [Promos, GeneratedPromos, ArchiveFollow];

    public override string ToString() => Id;
}

/// <summary>The features the engine runs, in order. <see cref="Default"/> is everything the scheduler ships with.</summary>
internal sealed class EngineModules
{
    public IReadOnlyList<ITimelineContributor> Overlays { get; init; } = [];

    public IReadOnlyList<IBreakContributor> Breaks { get; init; } = [];

    public IReadOnlyList<IBreakFinalizer> Finalizers { get; init; } = [];

    public bool HasPromos { get; init; }

    public bool HasGeneratedPromos { get; init; }

    public bool HasArchiveFollow { get; init; }

    public static EngineModules Default { get; } = For(PlannerFeature.All);

    /// <summary>The modules for a set of features; generated promos only take effect alongside promos.</summary>
    public static EngineModules For(IEnumerable<PlannerFeature> features)
    {
        HashSet<PlannerFeature> on = [.. features];
        bool promos = on.Contains(PlannerFeature.Promos);
        bool generated = promos && on.Contains(PlannerFeature.GeneratedPromos);
        bool follow = on.Contains(PlannerFeature.ArchiveFollow);

        return new EngineModules
        {
            Overlays = follow ? [new ArchiveFollowTimeline()] : [],
            // Bumpers first: their closing clip goes before the closing promos (Up Next, Now Playing), and their
            // opening one after the opening promo, as it's added after the interstitial shows.
            Breaks = promos
                ? [new BumperBreakContributor(), new PromoBreakContributor(generated ? new GeneratedPromoSubstitute() : null)]
                : [new BumperBreakContributor()],
            Finalizers = generated ? [new GeneratedPromoStandIns()] : [],
            HasPromos = promos,
            HasGeneratedPromos = generated,
            HasArchiveFollow = follow
        };
    }
}

// --- Timeline ---------------------------------------------------------------------------------------------------

/// <summary>Lays extra time-boxed slots over the regular timeline, such as the archived schedule Archive Follow replays.</summary>
internal interface ITimelineContributor
{
    /// <summary>Null when the feature doesn't apply to this channel; an overlay (even an empty one) otherwise.</summary>
    ValueTask<TimelineOverlay?> OverlayAsync(TimelineContext context, CancellationToken cancellationToken);
}

internal sealed record TimelineContext(
    SchedulerConfig Config,
    ChannelConfig Channel,
    IMediaLibrary Library,
    TimeZoneInfo TimeZone,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<BlockOccurrence> Regular);

/// <summary>
/// Overlay slots win over everything regular they cover: they're flattened on top of the regular blocks and drop
/// the anchors inside them. Each slot's block maps to an <see cref="IOverlaySlot"/> that chooses its programmes.
/// </summary>
internal sealed record TimelineOverlay(
    IReadOnlyList<BlockOccurrence> Blocks,
    IReadOnlyDictionary<BlockConfig, IOverlaySlot> Slots,
    IReadOnlyList<ScheduleDiagnostic> Diagnostics,
    IReadOnlyList<ArchiveGuideDay> GuideDays);

/// <summary>Chooses what airs in one overlay slot, before the block's ordinary rotation is consulted.</summary>
internal interface IOverlaySlot
{
    ValueTask<OverlayPick> PickAsync(ScheduleBuildSession session, ChannelConfig channel, TimeSpan gap, CancellationToken cancellationToken);
}

/// <summary>Either a programme to air, or the selectors to rotate through instead.</summary>
internal sealed record OverlayPick(ProgramPick? Pick, IReadOnlyList<ContentSelector> Fallback);

/// <summary>
/// The chosen programme and the sequence it came from, so a slot can pull more of it. A <see cref="FixedStack"/>
/// is aired as given and fills the slot to its boundary; <see cref="OriginOf"/> labels its programme entries.
/// </summary>
internal sealed record ProgramPick(
    MediaItem Item,
    MediaSequence Sequence,
    ContentSelector Selector,
    IReadOnlyList<MediaItem>? FixedStack = null,
    Func<MediaItem?, ProgramOrigin>? OriginOf = null)
{
    /// <summary>Ids of the programmes in this pick that are new episodes (a drop's first airing).</summary>
    public HashSet<string> Premieres { get; } = new(StringComparer.Ordinal);
}

// --- Breaks -----------------------------------------------------------------------------------------------------

/// <summary>Everything about one channel build that outlives a single break.</summary>
internal sealed class BuildContext(
    ScheduleBuildSession session,
    IReadOnlyList<BlockOccurrence> blocks,
    IReadOnlyList<AnchorOccurrence> anchors,
    List<ScheduleEntry> entries)
{
    private readonly Dictionary<Type, object> _state = [];

    public ScheduleBuildSession Session => session;

    public IReadOnlyList<BlockOccurrence> Blocks => blocks;

    public IReadOnlyList<AnchorOccurrence> Anchors => anchors;

    /// <summary>The next anchor still to air; the engine keeps it current.</summary>
    public int AnchorIndex { get; set; }

    public List<ScheduleEntry> Entries => entries;

    /// <summary>Ids of the new episodes (drop premieres) in the slot being laid out; the engine keeps it current.</summary>
    public IReadOnlySet<string> Premieres { get; set; } = new HashSet<string>();

    /// <summary>Per-build scratch state for a module, created on first use.</summary>
    public T State<T>() where T : class, new()
    {
        if (!_state.TryGetValue(typeof(T), out object? value))
        {
            value = new T();
            _state[typeof(T)] = value;
        }

        return (T)value;
    }
}

/// <summary>Where a break sits within the slot it belongs to; null for a gap with no programme around it.</summary>
/// <param name="ProgrammeStart">When the programme before the break started.</param>
/// <param name="SlotEnd">When the slot ends.</param>
/// <param name="EndsSlot">Whether this programme is the slot's last.</param>
/// <param name="MidRolls">Mid-roll breaks in the programme (zero when they're too short to be worth it).</param>
/// <param name="Index">This break's index among the programme's segments.</param>
/// <param name="IsLast">Whether this is the break after the programme's last segment.</param>
internal sealed record SlotBreak(DateTimeOffset ProgrammeStart, DateTimeOffset SlotEnd, bool EndsSlot, int MidRolls, int Index, bool IsLast);

/// <summary>One break being built, as modules see it.</summary>
internal sealed class BreakContext
{
    public required BuildContext Build { get; init; }

    public required SchedulerConfig Config { get; init; }

    public required ChannelConfig Channel { get; init; }

    public required BlockConfig? Block { get; init; }

    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    public required BreakPlacement Placement { get; init; }

    /// <summary>The slot's last programme just finished.</summary>
    public required bool EndsSlot { get; init; }

    /// <summary>The same slot carries on after the break (a mid-roll, or between stacked episodes).</summary>
    public required bool ContinuesSlot { get; init; }

    public required SlotBreak? Slot { get; init; }

    public required InterstitialsConfig Interstitials { get; init; }

    public required TimeSpan Tolerance { get; init; }

    /// <summary>Seeds every stateless choice made for this break.</summary>
    public required string BreakSeed { get; init; }

    /// <summary>Packs commercials and filler between two times (consuming their sequences), as the engine does.</summary>
    public required Func<DateTimeOffset, DateTimeOffset, List<ScheduleEntry>, CancellationToken, ValueTask<DateTimeOffset>> PackAsync { get; init; }

    public ScheduleBuildSession Session => Build.Session;

    /// <summary>Everything scheduled so far, including this break's entries once the engine has added them.</summary>
    public List<ScheduleEntry> Entries => Build.Entries;

    public CommercialsConfig Commercials => Interstitials.Commercials!;
}

/// <summary>Adds its own items to breaks, such as promos.</summary>
internal interface IBreakContributor
{
    /// <summary>Called at the start of each break; null when it has nothing for this one.</summary>
    ValueTask<IBreakPlan?> PlanAsync(BreakContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A contributor's items for one break. The engine places them at fixed points: the opener first, then
/// interstitial shows, then <see cref="AfterInterstitialsAsync"/>'s items at the cursor, then the ads with
/// <see cref="ReserveMixInAsync"/>'s item and <see cref="Held"/> dropped in among them, then padding, then
/// <see cref="Tail"/>. Every entry's start is set by the engine.
/// </summary>
internal interface IBreakPlan
{
    ScheduleEntry? Opener { get; }

    /// <summary>Played last, in order; their time is taken off the end of the break before anything else is packed.</summary>
    IReadOnlyList<ScheduleEntry> Tail { get; }

    /// <summary>Kept free while the break is filled, then mixed in among the ads with this salt.</summary>
    (ScheduleEntry Entry, string Salt)? Held { get; }

    /// <summary>Items appended straight after interstitial shows; <paramref name="fillTo"/> is where filling must stop.</summary>
    ValueTask<IReadOnlyList<ScheduleEntry>> AfterInterstitialsAsync(DateTimeOffset cursor, DateTimeOffset fillTo, CancellationToken cancellationToken);

    /// <summary>An item to mix in among the ads, reserved before they're packed.</summary>
    ValueTask<(ScheduleEntry Entry, string Salt)?> ReserveMixInAsync(DateTimeOffset cursor, DateTimeOffset fillTo, CancellationToken cancellationToken);
}

/// <summary>Runs over a finished break, e.g. to attach stand-in content to entries that may not render.</summary>
internal interface IBreakFinalizer
{
    /// <param name="breakStart">Index of the break's first entry in <see cref="BreakContext.Entries"/>.</param>
    ValueTask FinalizeAsync(BreakContext context, int breakStart, CancellationToken cancellationToken);
}

/// <summary>
/// Generated promos as seen by the promo contributor: a generated template can stand in for (or be mixed in with)
/// a library clip in each promo spot.
/// </summary>
internal interface IPromoSpotSubstitute
{
    bool Has(BreakContext context, GeneratedPromoType type);

    GeneratedPromoTemplate? Choose(BreakContext context, GeneratedPromoType type, bool clipAvailable, TimeSpan available, DateTimeOffset at, bool isNew = false);

    /// <summary>A New Episode promo and when the new episode it advertises is expected; null when none applies.</summary>
    ValueTask<(GeneratedPromoTemplate Template, DateTimeOffset? Expected)?> NewEpisodeAsync(
        BreakContext context, PromoConfig promos, DateTimeOffset cursor, TimeSpan available, CancellationToken cancellationToken);

    /// <summary>The stand-in media item scheduled for a template until it's rendered.</summary>
    MediaItem Placeholder(GeneratedPromoTemplate template);

    /// <summary>Anchor targets plus later block openings a generated Coming Up could name.</summary>
    ValueTask<IReadOnlyList<PromoTarget>> ComingUpTargetsAsync(
        BreakContext context, IReadOnlyList<PromoTarget> anchorTargets, DateTimeOffset cursor, PromoConfig promos, CancellationToken cancellationToken);

    /// <summary>A generated trailer for a later airing, or null.</summary>
    ValueTask<(GeneratedPromoTemplate Template, UpcomingAiring Airing)?> EventuallyAsync(
        BreakContext context, PromoConfig promos, bool clipAvailable, DateTimeOffset cursor, TimeSpan available, CancellationToken cancellationToken);
}
