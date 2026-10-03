using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

public enum ScheduleEntryKind
{
    /// <summary>Main programming from an anchor, block pool or base rotation.</summary>
    Program,

    /// <summary>Macro-fill short/interstitial show (gap pass 1).</summary>
    Interstitial,

    /// <summary>Strict-fit commercial (gap pass 2A). Always plays in full.</summary>
    Commercial,

    /// <summary>Trimmable filler/bumper (gap pass 2B) used to zero out the residual gap.</summary>
    Filler,

    /// <summary>Context-aware promo injected by the lookahead engine.</summary>
    Promo,

    /// <summary>"Coming up next" promo placed immediately before the upcoming programme.</summary>
    UpNextPromo,

    /// <summary>Ident for the programme about to start, placed after <see cref="UpNextPromo"/>.</summary>
    NowPlayingPromo,

    /// <summary>Sign-off for the programme that just finished, placed first in the break after it.</summary>
    ThisHasBeenPromo,

    /// <summary>Ident placed first in a break the same programme returns from.</summary>
    YoureWatchingPromo,

    /// <summary>Bumper placed last in a break the same programme returns from.</summary>
    BackToTheShowPromo,

    /// <summary>Trailer for a show airing later in the week, mixed in among the ads.</summary>
    EventuallyPromo,

    /// <summary>Unfillable residual. ErsatzTV is expected to pad this.</summary>
    Padding
}

public enum ProgramOrigin
{
    Normal,
    Anchor,
    Archive,
    ArchiveRandom
}

public sealed record ScheduleEntry
{
    public required DateTimeOffset Start { get; init; }

    /// <summary>Length of the slot. Shorter than the item's runtime when <see cref="IsTrimmed"/> is set.</summary>
    public required TimeSpan Duration { get; init; }

    public DateTimeOffset End => Start + Duration;

    public required ScheduleEntryKind Kind { get; init; }

    public ProgramOrigin ProgramOrigin { get; init; }

    public MediaItem? Item { get; init; }

    /// <summary>True when the slot is shorter than the asset, so playback stops early.</summary>
    public bool IsTrimmed { get; init; }

    /// <summary>Offset into the asset this slot starts at; non-zero for a mid-roll segment.</summary>
    public TimeSpan InPoint { get; init; }

    /// <summary>Which part of a split programme this is, 1-based. Zero when the programme is whole.</summary>
    public int SegmentNumber { get; init; }

    public int SegmentCount { get; init; }

    /// <summary>Play to the boundary and stop rather than hard-cutting; a hint for the playout engine.</summary>
    public bool StopBeforeEnd { get; init; }

    /// <summary>The asset's natural runtime, recorded whenever it differs from <see cref="Duration"/>.</summary>
    public TimeSpan? SourceDuration { get; init; }

    /// <summary>Human-readable origin: block name, anchor name, or <c>base rotation</c>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Set when the entry belongs to a block window.</summary>
    public string? BlockName { get; init; }

    /// <summary>
    /// True for the second and later episodes stacked into one slot. They are still separate
    /// programmes to the playout, but a guide lists the slot as a single block of that show.
    /// </summary>
    public bool ContinuesSlot { get; init; }

    /// <summary>
    /// True for a title sequence played at the top of a slot. It is played, but a guide folds it
    /// into the listing for the programme it introduces rather than naming it.
    /// </summary>
    public bool IsSlotIntro { get; init; }

    /// <summary>
    /// True for a closing sequence played at the end of a slot, before the break that follows.
    /// Folded into the listing the same way as <see cref="IsSlotIntro"/>.
    /// </summary>
    public bool IsSlotOutro { get; init; }

    /// <summary>For promos: the show this promo advertises.</summary>
    public string? PromoTargetId { get; init; }

    /// <summary>Template that renders this promo; its file is attached once rendering finishes.</summary>
    public string? GeneratedTemplate { get; init; }

    /// <summary>For generated Coming Up and Eventually promos: when the promoted show airs.</summary>
    public DateTimeOffset? GeneratedTargetAt { get; init; }

    /// <summary>The promoted show as predicted at build time, used when the schedule does not reach it.</summary>
    public string? GeneratedSubject { get; init; }

    /// <summary>
    /// For generated promos: break content filling the same slot, aired instead while the render is
    /// missing. Times are from build; shift them by this entry's start if it has since moved.
    /// </summary>
    public IReadOnlyList<ScheduleEntry>? Fallback { get; init; }

    /// <summary>
    /// Extra facts attached by plugins on the way to the outputs, keyed <c>&lt;plugin id&gt;.&lt;name&gt;</c>, e.g. an
    /// overlay image to burn in. Outputs that don't understand a key ignore it. Never set by the planner.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

public sealed record ArchiveGuideBlock(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> Labels)
{
    public string? Show { get; init; }
    public string? Episode { get; init; }
    public TimeSpan? OriginalStart { get; init; }
    public TimeSpan? OriginalEnd { get; init; }
}

public sealed record ArchiveGuideDay(DateTimeOffset Start, DateTimeOffset End, DateOnly Date, IReadOnlyList<ArchiveGuideBlock> Blocks)
{
    public static IReadOnlyList<ArchiveGuideDay> Clip(IEnumerable<ArchiveGuideDay> days, DateTimeOffset start, DateTimeOffset end) =>
        days.Where(day => day.End > start && day.Start < end).Select(day => day with
        {
            Start = day.Start < start ? start : day.Start,
            End = day.End > end ? end : day.End,
            Blocks = day.Blocks.Where(block => block.End > start && block.Start < end).Select(block => block with
            {
                Start = block.Start < start ? start : block.Start,
                End = block.End > end ? end : block.End
            }).ToArray()
        }).ToArray();
}

public sealed record ChannelSchedule
{
    public required string ChannelId { get; init; }

    public required string ChannelName { get; init; }

    public int ChannelNumber { get; init; }

    /// <summary>Guide id shared with the playout target's lineup; defaults to the channel id.</summary>
    public string? ChannelTvgId { get; init; }

    public string TvgId => ChannelTvgId ?? ChannelId;

    public string? ChannelGroup { get; init; }

    public string? ChannelLogo { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset End { get; init; }

    public required IReadOnlyList<ScheduleEntry> Entries { get; init; }

    public bool ArchiveFollowEnabled { get; init; }

    public IReadOnlyList<ArchiveGuideDay> ArchiveDays { get; init; } = [];

    public IReadOnlyList<ScheduleDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Facts about the whole channel for the outputs, keyed <c>&lt;output or plugin&gt;.&lt;name&gt;</c>, such as
    /// <c>ersatztv.copy</c> for a channel whose streams should be passed through rather than re-encoded. Outputs
    /// ignore keys they don't understand. Never set by the planner.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    public TimeSpan TotalPadding => Entries
        .Where(e => e.Kind == ScheduleEntryKind.Padding)
        .Aggregate(TimeSpan.Zero, (acc, e) => acc + e.Duration);
}

public sealed record ScheduleDiagnostic(DiagnosticLevel Level, DateTimeOffset? At, string Message);

public enum DiagnosticLevel
{
    Info,
    Warning,
    Error
}
