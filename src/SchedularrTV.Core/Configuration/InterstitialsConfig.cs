using SchedularrTV.Core.Ordering;

using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// The three interstitial tiers consumed by the gap packer, in the order they are applied.
/// Values left null inherit from the parent scope (block → channel → settings).
/// </summary>
public sealed class InterstitialsConfig
{
    /// <summary>Tier 1: interstitial shows/shorts that collapse larger gaps between programmes.</summary>
    public ContentSelector? Shows { get; set; }

    public int? MaxShowsPerSlot { get; set; }

    /// <summary>Tier 2: strict-fit commercials and promos. Never trimmed, never overrun.</summary>
    public CommercialsConfig? Commercials { get; set; }

    /// <summary>Tier 3: trimmable filler used to zero out whatever the commercial pass could not.</summary>
    public FillerTierConfig? Filler { get; set; }

    public InterstitialsConfig Merge(InterstitialsConfig? parent) => new()
    {
        Shows = Shows ?? parent?.Shows,
        MaxShowsPerSlot = MaxShowsPerSlot ?? parent?.MaxShowsPerSlot,
        Commercials = (Commercials ?? new CommercialsConfig()).Merge(parent?.Commercials),
        Filler = (Filler ?? new FillerTierConfig()).Merge(parent?.Filler)
    };
}

/// <summary>
/// Tier 2. Commercials are guaranteed-playback assets: the packer only ever selects items whose full
/// runtime fits the remaining gap, so an ad is never cut off mid-roll.
/// </summary>
public sealed class CommercialsConfig
{
    /// <summary>Complete pool query. When set it replaces <see cref="BaseQuery"/>.</summary>
    public string? Query { get; set; }

    /// <summary>Query every commercial must satisfy, e.g. <c>tag:"Commercials"</c>.</summary>
    public string? BaseQuery { get; set; }

    /// <summary>Channel/brand tags OR-ed together, e.g. <c>["Adult Swim", "Any Channel"]</c>.</summary>
    public List<string>? BaseTags { get; set; }

    /// <summary>
    /// Second-level folder tags OR-ed together, e.g. <c>["Ads", "Bumpers"]</c>. Combined with
    /// <see cref="BaseTags"/> this expresses "these kinds of clip, from these brands" without a query.
    /// </summary>
    public List<string>? Types { get; set; }

    /// <summary>Opt-in for month/season/date/holiday tag injection (spec 1.2).</summary>
    public bool? AutoTemporalTags { get; set; }

    /// <summary>Extra query AND-ed onto the resolved pool.</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// When true (the default) commercials are never trimmed. Setting it false lets the filler pass
    /// trim a commercial as a last resort.
    /// </summary>
    public bool? StrictFit { get; set; }

    public PlaybackOrder? Order { get; set; }

    /// <summary>Never pack an item shorter than this (avoids single-frame stingers).</summary>
    public TimeSpan? MinItemDuration { get; set; }

    public TimeSpan? MaxItemDuration { get; set; }

    /// <summary>Residual gap at or below this length is left to the filler tier rather than packed.</summary>
    public TimeSpan? Tolerance { get; set; }

    /// <summary>Knapsack bucket size. Smaller is more exact but slower.</summary>
    public TimeSpan? Granularity { get; set; }

    /// <summary>Max copies of the same commercial inside a single break.</summary>
    public int? MaxRepeatsPerBreak { get; set; }

    [YamlIgnore]
    public bool IsStrictFit => StrictFit ?? true;

    public CommercialsConfig Merge(CommercialsConfig? parent) => new()
    {
        Query = Query ?? parent?.Query,
        BaseQuery = BaseQuery ?? parent?.BaseQuery,
        BaseTags = BaseTags ?? parent?.BaseTags,
        Types = Types ?? parent?.Types,
        AutoTemporalTags = AutoTemporalTags ?? parent?.AutoTemporalTags,
        Filter = Filter ?? parent?.Filter,
        StrictFit = StrictFit ?? parent?.StrictFit,
        Order = Order ?? parent?.Order,
        MinItemDuration = MinItemDuration ?? parent?.MinItemDuration,
        MaxItemDuration = MaxItemDuration ?? parent?.MaxItemDuration,
        Tolerance = Tolerance ?? parent?.Tolerance,
        Granularity = Granularity ?? parent?.Granularity,
        MaxRepeatsPerBreak = MaxRepeatsPerBreak ?? parent?.MaxRepeatsPerBreak
    };
}

/// <summary>
/// Tier 3. Filler is non-guaranteed padding: bumpers, station IDs, music videos. Because these have
/// no contractual obligation to play in full, they may be trimmed to land exactly on the next
/// programme boundary instead of leaving dead air.
/// </summary>
public sealed class FillerTierConfig
{
    public string? Query { get; set; }

    /// <summary>Query every filler item must satisfy, e.g. <c>tag:"Commercials"</c>.</summary>
    public string? BaseQuery { get; set; }

    public List<string>? BaseTags { get; set; }

    /// <summary>Second-level folder tags OR-ed together, e.g. <c>["Bumpers"]</c>.</summary>
    public List<string>? Types { get; set; }

    public bool? AutoTemporalTags { get; set; }

    public string? Filter { get; set; }

    /// <summary>Allows a longer filler item to be cut short so the gap closes exactly. Default true.</summary>
    public bool? Trimmable { get; set; }

    /// <summary>
    /// Emits the trim as a "play until the boundary then stop" hint for the playout engine rather
    /// than a hard cut. Individual items may override this with a <c>stop_before_end</c> attribute.
    /// </summary>
    public bool? StopBeforeEnd { get; set; }

    /// <summary>Set false to disable the tier entirely; residual gaps then surface as padding. Default true.</summary>
    public bool? Fallback { get; set; }

    public PlaybackOrder? Order { get; set; }

    public TimeSpan? MinItemDuration { get; set; }

    public TimeSpan? MaxItemDuration { get; set; }

    /// <summary>
    /// Residuals shorter than this are left as padding instead of producing a filler entry nobody
    /// would notice. Defaults to zero, i.e. always close the gap.
    /// </summary>
    public TimeSpan? MinTrimmedDuration { get; set; }

    [YamlIgnore]
    public bool IsTrimmable => Trimmable ?? true;

    [YamlIgnore]
    public bool IsEnabled => Fallback ?? true;

    public FillerTierConfig Merge(FillerTierConfig? parent) => new()
    {
        Query = Query ?? parent?.Query,
        BaseQuery = BaseQuery ?? parent?.BaseQuery,
        BaseTags = BaseTags ?? parent?.BaseTags,
        Types = Types ?? parent?.Types,
        AutoTemporalTags = AutoTemporalTags ?? parent?.AutoTemporalTags,
        Filter = Filter ?? parent?.Filter,
        Trimmable = Trimmable ?? parent?.Trimmable,
        StopBeforeEnd = StopBeforeEnd ?? parent?.StopBeforeEnd,
        Fallback = Fallback ?? parent?.Fallback,
        Order = Order ?? parent?.Order,
        MinItemDuration = MinItemDuration ?? parent?.MinItemDuration,
        MaxItemDuration = MaxItemDuration ?? parent?.MaxItemDuration,
        MinTrimmedDuration = MinTrimmedDuration ?? parent?.MinTrimmedDuration
    };
}
