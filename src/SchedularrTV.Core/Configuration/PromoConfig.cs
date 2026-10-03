namespace SchedularrTV.Core.Configuration;

/// <summary>Context-aware promo injection (spec 3.2).</summary>
public sealed class PromoConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Base query identifying promo assets, e.g. <c>tag:"Promo"</c>.</summary>
    public string? BaseQuery { get; set; }

    /// <summary>
    /// Brand folders the promos may be drawn from, e.g. <c>["Any Channel", "Cartoon Network"]</c>.
    /// Falls back to the channel's commercial base tags, so promos follow the same brand folders as
    /// the ads unless a channel says otherwise. Empty means every brand, which is rarely wanted.
    /// </summary>
    public List<string>? BaseTags { get; set; }

    /// <summary>
    /// Show folder that stands in for whichever show is being promoted, so one clip can serve a
    /// whole channel.
    /// </summary>
    public string AnyShowAlias { get; set; } = "Any Show";

    /// <summary>How far ahead the lookahead engine inspects anchors for promo targets.</summary>
    public TimeSpan Lookahead { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Maximum lookahead promos injected into a single break.</summary>
    public int MaxPerBreak { get; set; } = 2;

    /// <summary>
    /// Windows mapping "how far away the promoted show is" to the <c>promo_type</c> values that apply.
    /// Evaluated in order; the first match wins.
    /// </summary>
    public List<PromoWindow> Windows { get; set; } = [];

    /// <summary>
    /// "Coming up next" promo for the programme that follows the current one. Where it goes is set by
    /// <see cref="PromoSpotConfig.Placement"/>: any break of the show before it, or last before the new show.
    /// </summary>
    public PromoSpotConfig UpNext { get; set; } = new();

    /// <summary>
    /// Station ident for the show about to start, e.g. "You're watching Top Cat". Runs after
    /// <see cref="UpNext"/> so it is the very last thing before the programme and its intro.
    /// </summary>
    public PromoSpotConfig NowPlaying { get; set; } = new()
    {
        PromoTypes = ["now_playing"],
        Tag = "Now Playing"
    };

    /// <summary>
    /// Sign-off for the show that just finished, e.g. "This has been Top Cat". Runs first in the
    /// break after the last episode of a slot, so a stacked slot signs off once rather than between
    /// every episode.
    /// </summary>
    public PromoSpotConfig ThisHasBeen { get; set; } = new()
    {
        PromoTypes = ["this_has_been"],
        Tag = "This Has Been"
    };

    /// <summary>
    /// Ident going into a break the same show returns from, e.g. "You're watching Top Cat". Runs first
    /// in a mid-roll, or in the break between two episodes of the same slot.
    /// </summary>
    public PromoSpotConfig YoureWatching { get; set; } = new()
    {
        PromoTypes = ["youre_watching"],
        Tag = "You're Watching",
        SkipWhenSameSeries = false
    };

    /// <summary>Bumper closing a break the same show returns from. Runs last, immediately before it.</summary>
    public PromoSpotConfig BackToTheShow { get; set; } = new()
    {
        PromoTypes = ["back_to_the_show"],
        Tag = "Back To The Show",
        SkipWhenSameSeries = false
    };

    /// <summary>
    /// Trailers for a show airing further out than the windows above reach, filed under the day and
    /// time it airs. Mixed in among the ads rather than pinned to a position in the break.
    /// </summary>
    public EventuallyConfig Eventually { get; set; } = new();

    public static IReadOnlyList<PromoWindow> DefaultWindows { get; } =
    [
        new() { Within = TimeSpan.FromMinutes(30), PromoTypes = ["up_next", "next"] },
        new() { Within = TimeSpan.FromHours(3), PromoTypes = ["coming_up", "later"] },
        new() { Within = TimeSpan.FromHours(12), PromoTypes = ["later_today", "tonight"] }
    ];
}

public sealed class PromoWindow
{
    public TimeSpan Within { get; set; }

    public List<string> PromoTypes { get; set; } = [];

    /// <summary>
    /// When true a promo filed under a clock folder (<c>Coming Up/4pm</c>) is used, and preferred over
    /// the generic one, whenever the show it promotes starts at that time.
    /// </summary>
    public bool IncludeClockVariants { get; set; } = true;
}

/// <summary>
/// Trailers for a show airing later in the week, e.g. <c>Super Chunk/Eventually/Saturday/400pm</c>.
/// Unlike the fixed spots these have no position of their own: one is dropped in among the ads now
/// and then, so a show gets talked about across the days leading up to it.
/// </summary>
public sealed class EventuallyConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary><c>promo_type</c> values that identify one of these trailers.</summary>
    public List<string> PromoTypes { get; set; } = ["eventually"];

    /// <summary>Tag that identifies the promo for libraries that use tags instead of attributes.</summary>
    public string Tag { get; set; } = "Eventually";

    /// <summary>How far ahead an airing may be and still be worth advertising.</summary>
    public TimeSpan Horizon { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Odds a given break carries one, so a trailer turns up occasionally rather than in every break.
    /// Zero never plays one; one plays whenever something fits.
    /// </summary>
    public double Chance { get; set; } = 0.25;
}

/// <summary>One promo slot pinned to a fixed position in a break.</summary>
public sealed class PromoSpotConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary><c>promo_type</c> values that identify a promo for this slot.</summary>
    public List<string> PromoTypes { get; set; } = ["up_next", "next", "coming_up_next"];

    /// <summary>Tag that identifies the promo for libraries that use tags instead of attributes.</summary>
    public string Tag { get; set; } = "Up Next";

    /// <summary>
    /// Suppresses the promo when the same series plays either side of the break, so a mid-show break
    /// does not advertise or sign off the show you are already watching.
    /// </summary>
    public bool SkipWhenSameSeries { get; set; } = true;

    /// <summary>Up Next only; the other spots keep their fixed positions.</summary>
    public UpNextPlacement Placement { get; set; } = UpNextPlacement.AnyBreak;
}

public enum UpNextPlacement
{
    /// <summary>One of the breaks during the show before, mid-rolls included, somewhere among the ads.</summary>
    AnyBreak,

    /// <summary>Last in the break, straight before the new show (and any Now Playing ident).</summary>
    BeforeShow
}
