using SchedularrTV.Core.Temporal;
using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

public sealed class SchedulerConfig
{
    public SchedulerSettings Settings { get; set; } = new();

    public Dictionary<string, ContentSelector> GlobalCollections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-show settings that apply wherever the show is scheduled, keyed by series name. A collection
    /// naming its own blocks for a title overrides what is here rather than adding to it.
    /// </summary>
    public Dictionary<string, TitleDefaults> Titles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ad break collections by name: the folders their ads, bumpers, filler and promos come from. Channels and blocks
    /// pick them by name (<see cref="ChannelConfig.AdBreaks"/>).
    /// </summary>
    public Dictionary<string, AdBreakConfig> AdBreaks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Holidays that apply to every channel (<c>global_holidays:</c>), after the channel's own: a channel holiday whose
    /// days hold a moment wins over these. A channel can turn them off (<see cref="ChannelConfig.IgnoreGlobalHolidays"/>).
    /// </summary>
    public List<ChannelHoliday> GlobalHolidays { get; set; } = [];

    public List<ChannelConfig> Channels { get; set; } = [];

    /// <summary>
    /// The document this was parsed from still had promos, generated promos or Archive Follow at their old
    /// top-level keys rather than under <c>plugins:</c>. Saving it writes the new layout.
    /// </summary>
    [YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesOldLayout { get; internal set; }
}

/// <summary>Settings attached to a show itself rather than to one collection's use of it.</summary>
public sealed class TitleDefaults
{
    public List<BumperBlock> Bumpers { get; set; } = [];

    public List<TitleAlias> Aliases { get; set; } = [];

    public Dictionary<int, SeasonDefaults> Seasons { get; set; } = [];

    /// <summary>
    /// The recordings have a channel's screen bug burned in, so overlays marked as screen bugs stand down while they
    /// air. A season or episode setting wins over this. Null is no.
    /// </summary>
    public bool? ScreenBug { get; set; }
}

public sealed class SeasonDefaults
{
    public List<TitleAlias> Aliases { get; set; } = [];

    /// <summary>Overrides the show's <see cref="TitleDefaults.ScreenBug"/> for this season; null follows the show.</summary>
    public bool? ScreenBug { get; set; }

    /// <summary>Per-episode settings, keyed by episode number within the season.</summary>
    public Dictionary<int, EpisodeDefaults> Episodes { get; set; } = [];

    /// <summary>Whether anything here differs from the defaults (an empty season isn't kept in the YAML).</summary>
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool HasSettings => Aliases.Count > 0 || ScreenBug is not null || Episodes.Values.Any(episode => episode.HasSettings);
}

public sealed class EpisodeDefaults
{
    /// <summary>Overrides the season's (and show's) screen bug setting for this episode; null follows the season.</summary>
    public bool? ScreenBug { get; set; }

    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool HasSettings => ScreenBug is not null;
}

public sealed class TitleAlias
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Use for generated promo speech, captions and variable images. A season preference wins over the show's.</summary>
    public bool Preferred { get; set; }
}

public sealed class SchedulerSettings
{
    /// <summary>IANA or Windows time zone id used when a channel does not override it.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>How far ahead playouts are generated.</summary>
    public TimeSpan Horizon { get; set; } = TimeSpan.FromHours(48);

    /// <summary>How often the background builder re-runs.</summary>
    public TimeSpan RebuildInterval { get; set; } = TimeSpan.FromHours(1);

    public TemporalTagOptions TemporalTags { get; set; } = new();

    /// <summary>
    /// Media-server libraries to ignore, by name. A DVR or streaming-cache library holds its own
    /// copies of shows, which otherwise surface as a second, near-empty copy of the same series.
    /// </summary>
    public List<string> ExcludedLibraries { get; set; } = [];

    public InterstitialsConfig Interstitials { get; set; } = new()
    {
        Commercials = new CommercialsConfig
        {
            BaseQuery = "tag:\"Commercials\"",
            Tolerance = TimeSpan.FromMilliseconds(500),
            Granularity = TimeSpan.FromMilliseconds(250),
            MaxRepeatsPerBreak = 1
        },
        Filler = new FillerTierConfig { Trimmable = true, Fallback = true }
    };

    public PromoConfig Promos { get; set; } = new();

    /// <summary>
    /// Set once the tag-based break and promo settings have been moved into ad breaks. From then on a channel's
    /// breaks come only from its ad breaks: one with none has empty breaks rather than falling back to the
    /// tag-based defaults above.
    /// </summary>
    public bool? AdBreaksMigrated { get; set; }

    /// <summary>
    /// Ad breaks (<see cref="SchedulerConfig.AdBreaks"/>) for channels that don't pick their own. Empty leaves such
    /// channels with empty breaks.
    /// </summary>
    public List<string>? DefaultAdBreaks { get; set; }

    /// <summary>Extra entropy folded into every deterministic seed; changing it reshuffles everything.</summary>
    public string Seed { get; set; } = "schedularrtv";

    /// <summary>Full path where generated promo videos and voice-overs are kept; unset uses the service default.</summary>
    public string? GeneratedPromosPath { get; set; }

    /// <summary>Rendered promos no schedule uses are deleted after this long; zero keeps them for good.</summary>
    public TimeSpan GeneratedPromosKeepVideos { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Voice-overs not used for this long are deleted; zero keeps them for good.</summary>
    public TimeSpan GeneratedPromosKeepAudio { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Promos start rendering only once they air within this long; zero renders the whole playout ahead.</summary>
    public TimeSpan GeneratedPromosLeadTime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Only render for channels someone is tuned to; the rest air each slot's stand-in commercials.</summary>
    public bool GeneratedPromosWatchedOnly { get; set; } = true;

    /// <summary>
    /// Variable images kept once for every generated promo template (<c>plugins.generated-promos.shared_images</c>);
    /// a template's variable uses one by its id.
    /// </summary>
    public List<GeneratedSharedImage> GeneratedPromosSharedImages { get; set; } = [];

    /// <summary>
    /// Pre-recorded voice-over phrases and templates kept once for every generated promo template
    /// (<c>plugins.generated-promos.voice_packs</c>); a template's voice-over uses one by its id.
    /// </summary>
    public List<GeneratedVoicePack> GeneratedPromosVoicePacks { get; set; } = [];

    /// <summary>
    /// Global settings of installed plugins, keyed by plugin id; channels and blocks can override them. The
    /// bundled features (promos, generated promos) are kept in their typed properties instead, and only share
    /// this section in the YAML (<see cref="PluginSections"/>).
    /// </summary>
    public Dictionary<string, object?>? Plugins { get; set; }

    /// <summary>The <c>plugins:</c> section as written to YAML: <see cref="Plugins"/> plus the bundled features.</summary>
    [YamlMember(Alias = "plugins")]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, object?>? PluginSections
    {
        get => FeatureSections.Compose(this);
        set => Plugins = value;
    }
}

public sealed class ChannelConfig
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public int Number { get; set; }

    public string? Group { get; set; }

    /// <summary>
    /// Guide id, derived from <see cref="Number"/>. It is not authored: a hand-set value silently
    /// drifts when the number changes, and the two must agree for the guide to line up.
    /// </summary>
    [YamlIgnore]
    public string TvgId => $"schedularrtv.{Number}";

    /// <summary>
    /// Anchor for the deterministic grid. Slot boundaries are measured from here rather than local
    /// midnight, and a rebuild starts on the most recent boundary instead of the instant it runs, so
    /// re-generating never shifts the timeline off the grid. Null keeps local midnight.
    /// </summary>
    public DateTimeOffset? ScheduleStart { get; set; }

    /// <summary>Channel logo URL surfaced in the M3U and guide.</summary>
    public string? Logo { get; set; }

    public string? TimeZone { get; set; }

    public bool? AutoTemporalTags { get; set; }

    /// <summary>
    /// Rounds each programme's end up to the next multiple of this interval and fills the remainder
    /// with interstitials/commercials. This is what produces regular ad breaks on a channel whose
    /// programmes would otherwise run back to back. Null disables slot alignment.
    /// </summary>
    public TimeSpan? PadToNearest { get; set; }

    /// <summary>
    /// Caps the break created by <see cref="PadToNearest"/>. When rounding up to the next slot would
    /// leave a longer gap than this, the break is shortened to the cap instead — otherwise a
    /// 15-minute show on a 30-minute grid produces 15 minutes of adverts. Null keeps the grid rigid.
    /// </summary>
    public TimeSpan? MaxBreak { get; set; }

    /// <summary>
    /// Fills a slot with further episodes of the same title instead of a long advert break. Two
    /// eleven-minute episodes make a half-hour slot the way they did on air, rather than one episode
    /// followed by nineteen minutes of commercials. Only ever adds episodes that fit whole.
    /// </summary>
    public bool? StackToFill { get; set; }

    /// <summary>
    /// Keeps the channel playing around the clock, as a broadcast channel would, rather than starting when someone
    /// tunes in and stopping 90 seconds after the last viewer leaves. Tuning in is instant and the channel is always
    /// mid-programme, at the cost of transcoding it all the time. Null or false is the usual on-demand behaviour.
    /// </summary>
    public bool? AlwaysOn { get; set; }

    /// <summary>
    /// Whether an <see cref="AlwaysOn"/> channel counts as watched for the features that only work on watched channels
    /// (generated promos, credit squeezing, chapter scanning). Keeping the channel playing looks the same to ErsatzTV
    /// as someone watching, so real viewers can no longer be told apart: this decides which way to treat it. Null is
    /// true. Never counted as a direct viewer in the viewing stats either way.
    /// </summary>
    public bool? AlwaysOnCountsAsWatched { get; set; }

    /// <summary>
    /// How ErsatzTV encodes this channel, where it should differ from the app-wide setting (App settings, Hardware and
    /// video encoding). Null, or any field left out, takes the app-wide value.
    /// </summary>
    public ChannelEncodingConfig? Encoding { get; set; }

    /// <summary>Which audio track plays and whether subtitles are included (and how). Null plays each file's defaults.</summary>
    public ChannelTracksConfig? Tracks { get; set; }

    /// <summary>Places breaks inside a programme rather than only after it.</summary>
    public MidRollConfig? MidRoll { get; set; }

    /// <summary>Brings forward the shows people watch on this channel and rests the ones they skip.</summary>
    public ViewerAwareConfig? ViewerAware { get; set; }

    /// <summary>Fallback rotation used whenever no anchor or block is active (spec 2.1 Base Flow).</summary>
    public ContentSelector? BaseRotation { get; set; }

    /// <summary>Tier 1 shorthand for <c>interstitials.shows</c>.</summary>
    public ContentSelector? InterstitialShows { get; set; }

    /// <summary>
    /// Names of the ad break collections (<see cref="SchedulerConfig.AdBreaks"/>) this channel's breaks are filled from;
    /// their clips are pooled. Empty uses the tag-based settings (<see cref="Interstitials"/>, <see cref="Promos"/>).
    /// </summary>
    public List<string>? AdBreaks { get; set; }

    public InterstitialsConfig? Interstitials { get; set; }

    /// <summary>Shorthand for <c>interstitials.commercials.base_tags</c>, matching the spec's YAML example.</summary>
    public List<string>? CommercialBaseTags { get; set; }

    /// <summary>
    /// Brand folders promos are drawn from, without having to restate the whole promo block. Falls
    /// back to <see cref="CommercialBaseTags"/>, so promos follow the ads by default.
    /// </summary>
    public List<string>? PromoBaseTags { get; set; }

    public PromoConfig? Promos { get; set; }

    public GeneratedPromosConfig? GeneratedPromos { get; set; }

    public ArchiveFollowConfig? Follow { get; set; }

    public List<BlockConfig> Blocks { get; set; } = [];

    /// <summary>Channel-level fixed slots. Equivalent to a block with a time and no end time.</summary>
    public List<AnchorConfig> Anchors { get; set; } = [];

    /// <summary>
    /// Days of the year when what plays is narrowed to holiday episodes (see <see cref="ChannelHoliday"/>). The first
    /// one whose days include a programme's start applies to it.
    /// </summary>
    public List<ChannelHoliday> Holidays { get; set; } = [];

    /// <summary>Leaves the global holidays (<see cref="SchedulerConfig.GlobalHolidays"/>) off this channel. Null is no.</summary>
    public bool? IgnoreGlobalHolidays { get; set; }

    /// <summary>
    /// Per-channel settings of installed plugins, keyed by plugin id; each plugin reads its own section. Promos,
    /// generated promos and Archive Follow live in their typed properties and are only written here in YAML.
    /// </summary>
    public Dictionary<string, object?>? Plugins { get; set; }

    /// <summary>The <c>plugins:</c> section as written to YAML: <see cref="Plugins"/> plus the bundled features.</summary>
    [YamlMember(Alias = "plugins")]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, object?>? PluginSections
    {
        get => FeatureSections.Compose(this);
        set => Plugins = value;
    }
}
