namespace SchedularrTV.Core.Configuration;

/// <summary>What the clips under a folder of an ad break are used for.</summary>
public enum AdFolderRole
{
    /// <summary>Commercials: whole clips packed to fit the break, never cut off.</summary>
    Ads,

    /// <summary>Station idents and "we'll be right back" clips; where they go is <see cref="AdBreakConfig.BumperPlacement"/>.</summary>
    Bumpers,

    /// <summary>Clips that close whatever the ads can't, cut short (or looped) so a break never ends on a black screen.</summary>
    Filler,

    /// <summary>Leaves a folder out, inside a folder that is used.</summary>
    Skip,

    /// <summary>One clip opens a block using this collection, before its first programme.</summary>
    BlockStart,

    /// <summary>One clip closes a block using this collection, reserved at its end.</summary>
    BlockEnd
}

/// <summary>Where an ad break's bumpers go.</summary>
public enum BumperPlacement
{
    /// <summary>Mixed in with the ads.</summary>
    Mixed,

    /// <summary>One opens the break and one closes it.</summary>
    StartAndEnd,

    /// <summary>One opens the break.</summary>
    Start,

    /// <summary>One closes the break.</summary>
    End
}

/// <summary>The kinds of promo an ad break can draw from a folder.</summary>
public enum AdPromoType
{
    /// <summary>"Up next: …", for the show that follows.</summary>
    UpNext,

    /// <summary>"You're watching …" ident for the show about to start, last thing before it.</summary>
    NowPlaying,

    /// <summary>"This has been …" sign-off, first thing after a show's last episode.</summary>
    ThisHasBeen,

    /// <summary>"You're watching …" going into a break the same show comes back from.</summary>
    YoureWatching,

    /// <summary>"And now back to …" closing a break the same show comes back from.</summary>
    BackToTheShow,

    /// <summary>For a show starting within a few hours.</summary>
    ComingUp,

    /// <summary>For a show starting later today.</summary>
    LaterToday,

    /// <summary>Trailers for a show airing later in the week, mixed in among the ads.</summary>
    Eventually
}

/// <summary>A folder of an ad break and what its clips are for. A folder inside another one overrides it.</summary>
public sealed class AdBreakFolder
{
    /// <summary>Full path of the folder, e.g. <c>D:\Media\Commercials\Any Channel\Ads</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public AdFolderRole Role { get; set; } = AdFolderRole.Ads;
}

/// <summary>A promo an ad break plays, and the folder its clips are in.</summary>
public sealed class AdBreakPromo
{
    public AdPromoType Type { get; set; } = AdPromoType.UpNext;

    /// <summary>Full path of the folder, e.g. <c>D:\Media\Commercials\Any Channel\Up Next</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Subfolders are named after shows (<c>Up Next/Top Cat/clip.mp4</c>) and only play for that show.</summary>
    public bool PerShow { get; set; } = true;

    /// <summary>Clips not inside a show's folder (or inside the "Any Show" folder) play for any show.</summary>
    public bool AnyShow { get; set; } = true;

    /// <summary>Folders named for a start time (<c>4pm</c>, <c>8:30pm</c>) only play for a show starting then, and are preferred then.</summary>
    public bool Times { get; set; }

    /// <summary>Folders named for a weekday (<c>Saturday</c>) only play for a show airing that day.</summary>
    public bool Days { get; set; }

    /// <summary>Leaves the promo out when the same show plays either side of the break. Null keeps the usual for its type.</summary>
    public bool? SkipWhenSameSeries { get; set; }

    /// <summary>Up Next only: in any break of the show before, or last before the new show.</summary>
    public UpNextPlacement? Placement { get; set; }

    /// <summary>Coming Up and Later Today: how far ahead the promoted show may start.</summary>
    public TimeSpan? Within { get; set; }

    /// <summary>Eventually only: the odds a break carries one (0 to 1).</summary>
    public double? Chance { get; set; }

    /// <summary>Eventually only: how far ahead an airing may be.</summary>
    public TimeSpan? Horizon { get; set; }

    /// <summary>The <c>Within</c> a Coming Up or Later Today promo uses when it has none of its own.</summary>
    public static TimeSpan DefaultWithin(AdPromoType type) => type == AdPromoType.ComingUp ? TimeSpan.FromHours(3) : TimeSpan.FromHours(12);

    /// <summary>The folder name that conventionally holds this kind of promo, e.g. "Up Next".</summary>
    public static string FolderName(AdPromoType type) => type switch
    {
        AdPromoType.UpNext => "Up Next",
        AdPromoType.NowPlaying => "Now Playing",
        AdPromoType.ThisHasBeen => "This Has Been",
        AdPromoType.YoureWatching => "You're Watching",
        AdPromoType.BackToTheShow => "Back To The Show",
        AdPromoType.ComingUp => "Coming Up",
        AdPromoType.LaterToday => "Later Today",
        AdPromoType.Eventually => "Eventually",
        _ => type.ToString()
    };

    /// <summary>Whether a folder is named for this kind of promo ("Up Next", "UpNext", "up_next"…).</summary>
    public static AdPromoType? FromFolderName(string name)
    {
        string key = new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        return key switch
        {
            "upnext" or "next" or "comingupnext" => AdPromoType.UpNext,
            "nowplaying" or "nowshowing" => AdPromoType.NowPlaying,
            "thishasbeen" or "thatwas" => AdPromoType.ThisHasBeen,
            "yourewatching" or "youarewatching" => AdPromoType.YoureWatching,
            "backtotheshow" or "backtoshow" or "andnowbackto" or "backto" => AdPromoType.BackToTheShow,
            "comingup" or "later" => AdPromoType.ComingUp,
            "latertoday" or "tonight" => AdPromoType.LaterToday,
            "eventually" or "thisweek" or "comingsoon" => AdPromoType.Eventually,
            _ => null
        };
    }
}

/// <summary>
/// A reusable set of ad break content: which folders of the commercials libraries hold ads, bumpers and filler, the
/// promos, and how they're picked. Channels and blocks list the ad breaks they use (an "Any channel" set alongside a
/// "Kids channel" one, say), and their clips are pooled.
/// </summary>
public sealed class AdBreakConfig
{
    /// <summary>
    /// The folder holding everything this ad break uses, picked when it was made; the editor shows just this folder's
    /// tree. Null shows every commercials library (an ad break moved from the older settings has none).
    /// </summary>
    public string? Root { get; set; }

    public List<AdBreakFolder> Folders { get; set; } = [];

    /// <summary>
    /// Folders of promos sorted by show and promo type in any order (<c>Promos/Looney Tunes/Back To The Show/</c> or
    /// <c>Promos/Up Next/Top Cat/</c>): each clip's promo type comes from the folder named for one, its show from the other.
    /// </summary>
    public List<AdBreakPromoRoot> PromoRoots { get; set; } = [];

    /// <summary>
    /// Clips inside a folder named for a month, season, holiday or date (<c>December</c>, <c>Winter</c>,
    /// <c>Christmas</c>, <c>12-25</c>) only play then. Everything else (an <c>Any Time</c> folder, or none) always plays.
    /// </summary>
    public bool Seasonal { get; set; }

    public BumperPlacement BumperPlacement { get; set; } = BumperPlacement.Mixed;

    public List<AdBreakPromo> Promos { get; set; } = [];

    /// <summary>A show folder by this name, inside a promo folder, holds clips for any show.</summary>
    public string? AnyShowFolder { get; set; }

    /// <summary>
    /// A show folder by this name, inside a promo folder, holds clips for any movie: used instead of the "any show" ones
    /// when the programme is a movie (a clip for that movie still comes first), and never for a show. Null: "Any Movie".
    /// </summary>
    public string? AnyMovieFolder { get; set; }

    /// <summary>Most Coming Up and Later Today promos in one break. Null: 2.</summary>
    public int? MaxPromosPerBreak { get; set; }

    /// <summary>How far ahead Coming Up and Later Today look for a show to promote. Null: 12 hours.</summary>
    public TimeSpan? Lookahead { get; set; }

    /// <summary>Fine tuning of how ads are packed (lengths, repeats, tolerance). Pools set here are ignored.</summary>
    public CommercialsConfig? Commercials { get; set; }

    /// <summary>Fine tuning of how filler is used (trimming, looping). Pools set here are ignored.</summary>
    public FillerTierConfig? Filler { get; set; }

    /// <summary>
    /// Clips chosen by tag and query rather than by folder, as the older break and promo settings chose them (they're
    /// moved here when a schedule is upgraded). Pooled with the folders' clips.
    /// </summary>
    public AdBreakTagRules? TagRules { get; set; }

    public const string DefaultAnyShowFolder = "Any Show";

    public const string DefaultAnyMovieFolder = "Any Movie";
}

/// <summary>
/// A folder of promos sorted by show and type: every promo type is found by its folder's name ("Up Next",
/// "You're Watching", "Back to the Show"…), and the other folder on the way is the show it's for.
/// </summary>
public sealed class AdBreakPromoRoot
{
    /// <summary>Full path of the folder, e.g. <c>D:\Media\Commercials\Promos</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Clips in no show's folder (or in the "Any Show" folder) play for any show.</summary>
    public bool AnyShow { get; set; } = true;

    /// <summary>Folders named for a start time (<c>8pm</c>) only play for a show starting then.</summary>
    public bool Times { get; set; } = true;

    /// <summary>Folders named for a weekday (<c>Saturday</c>) only play for a show airing that day.</summary>
    public bool Days { get; set; } = true;
}

/// <summary>An ad break's clips chosen by tag and query, as the older break and promo settings chose them.</summary>
public sealed class AdBreakTagRules
{
    /// <summary>The ads pool: query, base query, brand tags, clip folder tags, seasonal tags and filter.</summary>
    public CommercialsConfig? Commercials { get; set; }

    /// <summary>The filler pool, likewise. Null when there's no tag-based filler.</summary>
    public FillerTierConfig? Filler { get; set; }

    /// <summary>Promos matched by <c>promo_type</c> or tag, and the settings that went with them.</summary>
    public PromoConfig? Promos { get; set; }

    /// <summary>Brand folders the tag-based promos come from.</summary>
    public List<string>? PromoBaseTags { get; set; }

    [YamlDotNet.Serialization.YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Commercials is null && Filler is null && Promos is null;
}
