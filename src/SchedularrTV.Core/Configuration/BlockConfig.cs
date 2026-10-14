using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// A time-windowed override (spec 2.1). A block with no <see cref="EndTime"/> behaves as a
/// channel-level anchor slot: it starts at <see cref="Time"/> and lasts as long as its content.
/// </summary>
public sealed class BlockConfig
{
    public required string Name { get; set; }

    public bool FollowOnly { get; set; }

    public TimeSpan? Time { get; set; }

    /// <summary>May be earlier than <see cref="Time"/>, in which case the block crosses midnight.</summary>
    public TimeSpan? EndTime { get; set; }

    public Recurrence? Recurrence { get; set; }

    public List<DayOfWeek> Days { get; set; } = [];

    public BlockSchedule? Schedule { get; set; }

    /// <summary>Higher wins when two blocks overlap. Specific-date blocks default to a higher priority.</summary>
    public int? Priority { get; set; }

    /// <summary>Time-locked slots inside the block window (spec 2.1.1).</summary>
    public List<AnchorConfig> Anchors { get; set; } = [];

    /// <summary>General rotation pool for unanchored time in the block.</summary>
    public List<ContentSelector> Shows { get; set; } = [];

    /// <summary>Tier 1 shorthand for <c>interstitials.shows</c>.</summary>
    public ContentSelector? InterstitialShows { get; set; }

    /// <summary>Ad break collections used inside this block instead of the channel's. Empty uses the channel's.</summary>
    public List<string>? AdBreaks { get; set; }

    public InterstitialsConfig? Interstitials { get; set; }

    /// <summary>Shorthand for <c>interstitials.commercials.base_tags</c>, matching the spec's YAML example.</summary>
    public List<string>? CommercialBaseTags { get; set; }

    /// <summary>Brand folders promos are drawn from inside this block.</summary>
    public List<string>? PromoBaseTags { get; set; }

    public bool? AutoTemporalTags { get; set; }

    /// <summary>Overrides the channel's slot alignment inside this block.</summary>
    public TimeSpan? PadToNearest { get; set; }

    /// <summary>Overrides the channel's maximum break length inside this block.</summary>
    public TimeSpan? MaxBreak { get; set; }

    /// <summary>Overrides whether short programmes are stacked to fill a slot inside this block.</summary>
    public bool? StackToFill { get; set; }

    /// <summary>Overrides the channel's mid-roll behaviour inside this block.</summary>
    public MidRollConfig? MidRoll { get; set; }

    public PromoConfig? Promos { get; set; }

    /// <summary>Null uses the channel's generated promos.</summary>
    public GeneratedPromosConfig? GeneratedPromos { get; set; }

    /// <summary>Plugin settings for this block, keyed by plugin id; they override the channel's.</summary>
    public Dictionary<string, object?>? Plugins { get; set; }

    /// <summary>The <c>plugins:</c> section as written to YAML: <see cref="Plugins"/> plus the bundled features.</summary>
    [YamlMember(Alias = "plugins")]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, object?>? PluginSections
    {
        get => FeatureSections.Compose(this);
        set => Plugins = value;
    }

    /// <summary>True when this block is really a fixed-time anchor rather than a window.</summary>
    [YamlIgnore]
    public bool IsAnchorSlot => !FollowOnly && EndTime is null && Time is not null;

    [YamlIgnore]
    public int EffectivePriority => Priority ?? (!FollowOnly && Schedule?.Date is not null ? 100 : IsAnchorSlot ? 50 : 10);
}
