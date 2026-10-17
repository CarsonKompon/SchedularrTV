using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>Which promo spot a generated template can fill.</summary>
public enum GeneratedPromoType
{
    /// <summary>The programme starting as the break ends, and optionally the ones after it.</summary>
    UpNext,

    /// <summary>Ident for the programme about to start.</summary>
    NowPlaying,

    /// <summary>Sign-off for the programme that just finished.</summary>
    ThisHasBeen,

    /// <summary>A show airing later today, within the promo lookahead.</summary>
    ComingUp,

    /// <summary>A show airing later in the week, mixed in among the ads.</summary>
    Eventually,

    /// <summary>Opens a mid-show break: the show being watched, which carries on afterwards.</summary>
    YoureWatching,

    /// <summary>Closes a mid-show break, just before the show carries on.</summary>
    BackToTheShow,

    /// <summary>
    /// The next new episode (a drop's first airing) the channel airs, mixed in among the ads like Eventually:
    /// "an all-new episode, Monday at 8".
    /// </summary>
    NewEpisode,

    /// <summary>Opens a mid-show break, before the ads: "we'll be right back" to the show being watched.</summary>
    RightBack
}

/// <summary>Which airings of its show a generated template is for.</summary>
public enum GeneratedEpisodeFilter
{
    /// <summary>New episodes and repeats alike.</summary>
    Any,

    /// <summary>Only new episodes (a drop's first airing). Preferred over others when the episode is new.</summary>
    New,

    /// <summary>Only repeats: anything that isn't a new episode.</summary>
    Repeats
}

/// <summary>When a template is used relative to real promo clips.</summary>
public enum GeneratedPromoUsage
{
    /// <summary>Only when the library has no clip for the spot.</summary>
    Fallback,

    /// <summary>Also mixed in with real clips according to <see cref="GeneratedPromoTemplate.MixChance"/>.</summary>
    Mixed
}

public enum GeneratedLayerKind
{
    /// <summary>Upcoming programmes, one per line.</summary>
    UpNext,
    Text,
    Rect,
    Image,

    /// <summary>The programme about to start.</summary>
    NowPlaying,

    /// <summary>The programme that just finished.</summary>
    ThisHasBeen,

    /// <summary>The show being promoted for later today.</summary>
    ComingUp,

    /// <summary>The show being promoted for later in the week.</summary>
    Eventually,

    /// <summary>The show being watched, as a mid-show break opens.</summary>
    YoureWatching,

    /// <summary>The show being watched, as a mid-show break ends.</summary>
    BackToTheShow,

    /// <summary>
    /// Draws nothing itself: layers whose <see cref="GeneratedLayer.Parent"/> is this group are placed, rotated, faded and
    /// timed inside it, so moving or animating the group carries them along while they keep their own animation.
    /// </summary>
    Group,

    /// <summary>A video playing in a box: a file, or a clip of an episode (see <see cref="GeneratedLayer.Video"/>).</summary>
    Video,

    /// <summary>The new episode being promoted.</summary>
    NewEpisode,

    /// <summary>The show being watched, as a mid-show break opens with "we'll be right back".</summary>
    RightBack
}

/// <summary>When a video layer's video starts playing.</summary>
public enum GeneratedVideoPlayback
{
    /// <summary>From the start of the promo, so it's already playing when the layer appears.</summary>
    WithPromo,

    /// <summary>From when the layer starts to appear.</summary>
    WithLayer
}

/// <summary>What a <see cref="GeneratedLayerKind.Video"/> layer plays, and how.</summary>
public sealed class GeneratedVideoOptions
{
    /// <summary>
    /// Null plays the layer's own file (<see cref="GeneratedLayer.Path"/>); otherwise a clip of an episode, chosen like a
    /// variable image's show (see <see cref="GeneratedImageSelectors"/>): <c>promoted</c>, <c>watching</c>, <c>next</c>,
    /// <c>next2</c>…
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Episode { get; set; }

    /// <summary>Where in the episode the clip starts; null picks a spot in its middle, different for each airing.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? ClipStart { get; set; }

    /// <summary>Where in the file it starts.</summary>
    public TimeSpan FileStart { get; set; }

    public GeneratedVideoPlayback Playback { get; set; } = GeneratedVideoPlayback.WithLayer;

    /// <summary>Starts over when it runs out; otherwise it stays on its last frame.</summary>
    public bool Loop { get; set; }

    /// <summary>Fills the box (cropping the video) rather than fitting inside it.</summary>
    public bool Fill { get; set; }

    /// <summary>Removes a solid background colour from this video.</summary>
    public bool ChromaKey { get; set; }

    public string KeyColor { get; set; } = "#00FF00";

    /// <summary>Colour matching distance, from 0.01 to 1.</summary>
    public double KeyTolerance { get; set; } = 0.2;

    /// <summary>Softness of the cutout edges, from 0 to 1.</summary>
    public double KeySoftness { get; set; } = 0.1;

    /// <summary>Plays the video's sound while the layer shows, as loud as the layer is opaque.</summary>
    public bool IncludeAudio { get; set; }

    /// <summary>0 to 1, relative to the video's own level.</summary>
    public double Volume { get; set; } = 1;

    /// <summary>Lowers its sound while the voice-over speaks.</summary>
    public bool DuckUnderVoice { get; set; } = true;

    /// <summary>
    /// Curated by AI: the episode (<see cref="Episode"/>) is transcribed, and a language model picks its moments and
    /// writes the voice-over around them, so the layer cuts between those clips while the announcer talks about them
    /// (or lets a clip speak for itself). Null plays one clip as usual, which is also what airs whenever it can't be
    /// curated. Left out of render keys while null.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedCuratedClips? Curated { get; set; }
}

/// <summary>What an AI-curated promo talks about.</summary>
public enum GeneratedCurationFocus
{
    /// <summary>This episode: its story and its moments.</summary>
    Episode,

    /// <summary>The show in general, with the episode's moments as examples of it.</summary>
    Show
}

/// <summary>
/// How a video layer's clips are curated by AI (see <see cref="GeneratedVideoOptions.Curated"/>): the voice-over is
/// written with the episode's transcript to hand, as lines said over the template's usual background, lines said over
/// a chosen clip, and clips left to speak for themselves.
/// </summary>
public sealed class GeneratedCuratedClips
{
    /// <summary>Who writes it and how: the writer, its style, extra direction ("Mondays are all about mayhem").</summary>
    public AiTextConfig Writer { get; set; } = new();

    public GeneratedCurationFocus About { get; set; } = GeneratedCurationFocus.Episode;

    /// <summary>The most clips of the episode it may use, 1 to 8.</summary>
    public int Clips { get; set; } = 3;

    /// <summary>The shortest a moment of the episode may be to be picked.</summary>
    public TimeSpan MinClip { get; set; } = TimeSpan.FromSeconds(1.2);

    /// <summary>The longest a moment of the episode may be to be picked.</summary>
    public TimeSpan MaxClip { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Clips only come from after this far into the episode (0 to 100%), to leave out its intro and theme song.
    /// </summary>
    public double FromPercent { get; set; } = 5;

    /// <summary>
    /// Clips only come from before this far into the episode (0 to 100%), to leave out its credits and outro.
    /// </summary>
    public double UntilPercent { get; set; } = 95;

    /// <summary>
    /// Lets a clip play with its own sound while the announcer waits, for a back-and-forth ("Did you know…?" "I didn't
    /// know that…"). Off, every clip plays under the announcer.
    /// </summary>
    public bool LetClipsSpeak { get; set; } = true;

    /// <summary>How loud a clip speaking for itself is, 0 to 1, relative to the episode.</summary>
    public double ClipVolume { get; set; } = 1;

    /// <summary>How loud a clip under the announcer is, 0 to 1 (0 is silent).</summary>
    public double UnderVoiceVolume { get; set; } = 0.15;

    /// <summary>The speech recognition plugin's id; null uses the first one that's ready.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Transcriber { get; set; }

    /// <summary>
    /// Directions for the voice-over ("Mondays are all about mayhem"), one picked for each promo so airings vary. Empty
    /// uses <see cref="AiTextConfig.Prompt"/> of <see cref="Writer"/> (the single direction older files have), if any.
    /// </summary>
    public List<string> Directions { get; set; } = [];

    /// <summary>
    /// Plans in two steps, each its own request to the model: first it only picks the moments to use (and their order)
    /// for the direction, then it writes the voice-over around just those. Easier for small models than doing both at
    /// once. Off, one request does both.
    /// </summary>
    public bool PickClipsFirst { get; set; } = true;

    /// <summary>
    /// Plays a clip under every announcer line the plan leaves without one (Said first and Said last included): a moment
    /// the plan didn't use, or else another stretch of the episode. Off, those lines are said over the usual background.
    /// </summary>
    public bool ClipUnderEveryLine { get; set; }

    /// <summary>What the layer shows when the promo can't be curated (the usual read-out is said then).</summary>
    public GeneratedCurationFallback WhenNotCurated { get; set; } = GeneratedCurationFallback.Nothing;
}

/// <summary>What a curated layer shows when its promo can't be curated.</summary>
public enum GeneratedCurationFallback
{
    /// <summary>Nothing: the background and the layers underneath show, like an ordinary promo.</summary>
    Nothing,

    /// <summary>One clip of the episode, for as long as the layer's on screen.</summary>
    OneClip
}

public static class GeneratedPromoKinds
{
    /// <summary>The text block a template of this type must place.</summary>
    public static GeneratedLayerKind RequiredLayer(GeneratedPromoType type) => type switch
    {
        GeneratedPromoType.NowPlaying => GeneratedLayerKind.NowPlaying,
        GeneratedPromoType.ThisHasBeen => GeneratedLayerKind.ThisHasBeen,
        GeneratedPromoType.ComingUp => GeneratedLayerKind.ComingUp,
        GeneratedPromoType.Eventually => GeneratedLayerKind.Eventually,
        GeneratedPromoType.YoureWatching => GeneratedLayerKind.YoureWatching,
        GeneratedPromoType.BackToTheShow => GeneratedLayerKind.BackToTheShow,
        GeneratedPromoType.NewEpisode => GeneratedLayerKind.NewEpisode,
        GeneratedPromoType.RightBack => GeneratedLayerKind.RightBack,
        _ => GeneratedLayerKind.UpNext
    };

    public static bool IsShowLayer(GeneratedLayerKind kind) =>
        kind is GeneratedLayerKind.NowPlaying or GeneratedLayerKind.ThisHasBeen or GeneratedLayerKind.ComingUp or GeneratedLayerKind.Eventually
            or GeneratedLayerKind.YoureWatching or GeneratedLayerKind.BackToTheShow or GeneratedLayerKind.NewEpisode
            or GeneratedLayerKind.RightBack;

    /// <summary>Whether a template with <paramref name="filter"/> suits an airing that is (or isn't) a new episode.</summary>
    public static bool Suits(GeneratedEpisodeFilter? filter, bool isNew) => filter switch
    {
        GeneratedEpisodeFilter.New => isNew,
        GeneratedEpisodeFilter.Repeats => !isNew,
        _ => true
    };
}

public enum GeneratedShape { Rectangle, Ellipse, Triangle, Diamond, Star }

public enum GeneratedTransition
{
    None,
    Fade,
    SlideLeft,
    SlideRight,
    SlideUp,
    SlideDown,
    Zoom
}

public enum GeneratedTextAlign
{
    Left,
    Center,
    Right
}

/// <summary>Where a text layer's lines sit up and down its box.</summary>
public enum GeneratedVerticalAlign
{
    Top,
    Middle,
    Bottom
}

/// <summary>How a text layer's letters are cased.</summary>
public enum GeneratedTextCase
{
    AsWritten,
    Upper,
    Lower
}

/// <summary>Text styling shared by generated promos and overlays.</summary>
public static class GeneratedTextStyle
{
    /// <summary><paramref name="text"/> cased as <paramref name="textCase"/> asks; null (or as written) leaves it alone.</summary>
    public static string Cased(string text, GeneratedTextCase? textCase) => textCase switch
    {
        GeneratedTextCase.Upper => text.ToUpperInvariant(),
        GeneratedTextCase.Lower => text.ToLowerInvariant(),
        _ => text
    };
}

public sealed class GeneratedPromosConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Channel id whose own generated promos are used instead of <see cref="Templates"/>, so both stay identical.</summary>
    public string? From { get; set; }

    public List<GeneratedPromoTemplate> Templates { get; set; } = [];
}

public enum GeneratedUpcomingGrouping
{
    /// <summary>One entry per scheduled slot: stacked episodes stay together; otherwise each episode is a slot.</summary>
    Timeslot,
    /// <summary>Combine consecutive slots naming the same show.</summary>
    Show
}

public sealed class GeneratedPromoTemplate
{
    public string Name { get; set; } = string.Empty;

    public GeneratedPromoType Type { get; set; } = GeneratedPromoType.UpNext;

    /// <summary>Shared upcoming sequence for text, images, voice-over, generators and background rules.</summary>
    public GeneratedUpcomingGrouping UpcomingGrouping { get; set; } = GeneratedUpcomingGrouping.Timeslot;

    public bool Enabled { get; set; } = true;

    /// <summary>Fixed so breaks can be packed before anything is rendered.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional channel-local daily and annually recurring restrictions on when this entire promo may air.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedPromoAvailability? Availability { get; set; }

    public GeneratedPromoUsage Usage { get; set; } = GeneratedPromoUsage.Fallback;

    /// <summary>
    /// Limits the template to new episodes (a drop's first airing) or to repeats of the show it names. Null (the
    /// default) suits both, and keeps render keys of templates without it unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedEpisodeFilter? Episodes { get; set; }

    /// <summary>For <see cref="GeneratedPromoUsage.Mixed"/>: odds of choosing this over an available clip.</summary>
    public double MixChance { get; set; } = 0.5;

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public int Fps { get; set; } = 30;

    /// <summary>Hex colour behind everything when there is no background media.</summary>
    public string BackgroundColor { get; set; } = "#101828";

    /// <summary>Image or video filling the frame. Videos loop.</summary>
    /// <remarks>The single-background form; <see cref="Backgrounds"/> adds more to pick from.</remarks>
    public string? Background { get; set; }

    /// <summary>
    /// Images or videos to pick from, one per promo (fixed by what the promo is, so a re-render looks the same), along
    /// with <see cref="Background"/>. Null (rather than empty) keeps render keys of templates without them unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Backgrounds { get; set; }

    /// <summary>Optional show-based eligibility and relative selection weights, keyed by background path.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedBackgroundRule>? BackgroundRules { get; set; }

    /// <summary>
    /// Layers placed differently over particular backgrounds (to follow what's in a video, say): while the promo uses
    /// that background, each listed layer takes its position, size, opacity and keyframes from here instead. Null when
    /// there are none, so templates without them keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedBackgroundOverride>? BackgroundOverrides { get; set; }

    /// <summary>Every background to pick from: <see cref="Background"/> first, then <see cref="Backgrounds"/>, without blanks or repeats.</summary>
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> BackgroundChoices =>
        [.. (Background is { Length: > 0 } single ? (Backgrounds ?? []).Prepend(single) : Backgrounds ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The sound of a background video (not an image, nor the episode clip, which has its own). Null plays it with the
    /// usual settings; turning it off keeps the settings with <see cref="GeneratedBackgroundSoundConfig.Enabled"/> false.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedBackgroundSoundConfig? BackgroundSound { get; set; }

    /// <summary>Plays a clip of the promoted episode behind everything; <see cref="Background"/> stands in when its file can't be read.</summary>
    public GeneratedClipConfig? BackgroundClip { get; set; }

    /// <summary>When set, an external program draws the video instead of <see cref="Layers"/>; voice and music are added on top.</summary>
    public GeneratedScriptConfig? Generator { get; set; }

    public GeneratedVoiceConfig? Voice { get; set; }

    public GeneratedMusicConfig? Music { get; set; }

    /// <summary>Drawn in list order, so later layers sit on top.</summary>
    public List<GeneratedLayer> Layers { get; set; } = [];

    /// <summary>Named image slots shared by canvas layers and external generator arguments.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedVariableImage>? VariableImages { get; set; }

    /// <summary>
    /// Only in template packages: the shared images the template's variables use, so the package works on its own.
    /// Never in the schedule file, where they're kept once under the plugin's settings.
    /// </summary>
    [YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedSharedImage>? SharedImages { get; set; }
}

public sealed class GeneratedPromoAvailability
{
    public GeneratedBackgroundTimeRange? TimeRange { get; set; }
    public GeneratedAnnualDateRange? DateRange { get; set; }

    /// <summary>
    /// The programme the promo is about must match this library query (<c>genre:'Action'</c>,
    /// <c>kind == 'Movie' AND year &gt;= 1990 AND year &lt; 2000</c>): the show up next for Up Next and Now Playing, the one
    /// being watched for This Has Been, You're Watching, Back to the Show and We'll Be Right Back, and the promoted one for
    /// Coming Up, Eventually and New Episode. Null when any programme will do.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Query { get; set; }
}

/// <summary>Inclusive month/day endpoints, repeated each year; an earlier end crosses New Year.</summary>
public sealed class GeneratedAnnualDateRange
{
    public string Start { get; set; } = "12-01";
    public string End { get; set; } = "12-31";
}

public sealed class GeneratedBackgroundRule
{
    public string Background { get; set; } = string.Empty;
    /// <summary>Null keeps existing show rules enabled; false allows a time-only rule.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? MatchShows { get; set; }
    /// <summary>Daily eligibility in the channel's local time, independent of show matching.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedBackgroundTimeRange? TimeRange { get; set; }
    public List<string> Shows { get; set; } = [];
    /// <summary>Check the next N listings using the template's upcoming grouping. Ignored when Within is set.</summary>
    public int NextShows { get; set; } = 1;
    /// <summary>Alternatively check shows starting between the promo's airtime and this far ahead.</summary>
    public TimeSpan? Within { get; set; }
    /// <summary>Exclude this background entirely when none of its shows matches.</summary>
    public bool OnlyWhenMatching { get; set; }
    /// <summary>Relative chance on a match. Ordinary backgrounds, and nonmatching preferred backgrounds, weigh 1.</summary>
    public double MatchWeight { get; set; } = 5;
}

public sealed class GeneratedBackgroundTimeRange
{
    /// <summary>Inclusive local time of day.</summary>
    public TimeSpan Start { get; set; } = TimeSpan.FromHours(6);
    /// <summary>Exclusive local time of day; earlier than Start means the range crosses midnight.</summary>
    public TimeSpan End { get; set; } = TimeSpan.FromHours(12);
}

public enum GeneratedImageContext { PromotedShow, CurrentlyWatching, UpNext }

/// <summary>
/// Which show a use of a variable image is resolved for, written after its name: <c>promoted</c> (the show the promo is
/// for), <c>watching</c> (the one on now, or before the break), <c>next</c> (the next show) or <c>next2</c> to
/// <c>next10</c> (that many shows ahead), as in <c>{variable_image_01:next2}</c>.
/// </summary>
public static class GeneratedImageSelectors
{
    public const int MaxAhead = 10;

    public static bool TryParse(string? selector, out GeneratedImageContext context, out int offset)
    {
        string value = selector?.Trim().ToLowerInvariant() ?? string.Empty;
        (context, offset) = (GeneratedImageContext.PromotedShow, 0);
        switch (value)
        {
            case "promoted" or "show":
                return true;
            case "watching" or "current":
                context = GeneratedImageContext.CurrentlyWatching;
                return true;
            case "next":
                context = GeneratedImageContext.UpNext;
                return true;
        }

        if (value.StartsWith("next", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int ahead)
            && ahead is >= 1 and <= MaxAhead)
        {
            (context, offset) = (GeneratedImageContext.UpNext, ahead - 1);
            return true;
        }

        return false;
    }

    /// <summary>The selector for a show context: <c>promoted</c>, <c>watching</c>, <c>next</c> or <c>next2</c> and on.</summary>
    public static string Format(GeneratedImageContext context, int offset) => context switch
    {
        GeneratedImageContext.CurrentlyWatching => "watching",
        GeneratedImageContext.UpNext => offset <= 0 ? "next" : $"next{offset + 1}",
        _ => "promoted"
    };

    /// <summary>A use of <paramref name="name"/>: the name alone (its own show context) or <c>name:selector</c>.</summary>
    public static string Reference(string name, string? selector) =>
        string.IsNullOrWhiteSpace(selector) ? name : $"{name}:{selector.Trim().ToLowerInvariant()}";

    /// <summary><c>{variable_image_01}</c> or <c>{variable_image_01:next2}</c> in an argument: the name, and the selector if there is one.</summary>
    public static readonly System.Text.RegularExpressions.Regex Placeholder =
        new(@"\{(variable_image_[a-z0-9_]+)(?::([a-z0-9_]+))?\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>A default image and per-show/per-block overrides: a variable image's own, or a shared one's.</summary>
public interface IGeneratedImageChoices
{
    string? DefaultPath { get; set; }
    List<GeneratedShowImage> Shows { get; set; }

    /// <summary>
    /// A picture search used when no show or block override matches, such as <c>{show} main character png</c>; the
    /// default is used when it finds nothing. Null searches for nothing.
    /// </summary>
    AutomaticImageConfig? Automatic { get; set; }
}

/// <summary>
/// Finds automatic images' pictures for whoever resolves variable images (the scheduler's picture searches), so the
/// configuration doesn't depend on them.
/// </summary>
public interface IAutomaticImageLookup
{
    /// <summary>The picture found for <paramref name="config"/> with <paramref name="query"/> filled in, or null when there isn't one (yet).</summary>
    string? Find(AutomaticImageConfig config, string query);
}

/// <summary>Automatic image queries: their tokens filled in.</summary>
public static class AutomaticImageQuery
{
    private static readonly System.Text.RegularExpressions.Regex Token = new(@"\{([a-z_][a-z_0-9]*)\}",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The query with its tokens filled in, ignoring case and tidying spaces; unknown tokens are left as written. Null
    /// when it's empty, or a token it uses has nothing here (a block name for a show outside any block, say).
    /// </summary>
    public static string? Fill(string? query, IReadOnlyDictionary<string, string?> tokens)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        bool missing = false;
        string filled = Token.Replace(query, match =>
        {
            string name = match.Groups[1].Value;
            string? key = tokens.Keys.FirstOrDefault(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                return match.Value;
            }

            string? value = tokens[key];
            missing |= string.IsNullOrWhiteSpace(value);
            return value ?? string.Empty;
        });
        return missing || string.IsNullOrWhiteSpace(filled)
            ? null
            : string.Join(' ', filled.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A variable image's query for a show: <c>{show}</c> (or <c>{title}</c>) is its title, <c>{block}</c> the block it airs in.</summary>
    public static string? ForShow(string? query, string? show, string? block) =>
        Fill(query, new Dictionary<string, string?> { ["show"] = show, ["title"] = show, ["block"] = block });

    /// <summary>The tokens a variable image's query can use, for help text.</summary>
    public const string Help = "{show} is the show's title and {block} the block it airs in.";
}

public static class GeneratedImageChoices
{
    /// <summary>
    /// The file for a programme: an override naming its show first, then one naming the block it airs in, then a
    /// picture found automatically (with <paramref name="automatic"/>), then the default. Empty when none applies.
    /// </summary>
    public static string PathFor(IGeneratedImageChoices choices, string? show, string? block, IAutomaticImageLookup? automatic = null) =>
        Match(choices, show, block: false) ?? Match(choices, block, block: true) ?? Automatic(choices, show, block, automatic)
        ?? choices.DefaultPath ?? string.Empty;

    private static string? Automatic(IGeneratedImageChoices choices, string? show, string? block, IAutomaticImageLookup? automatic) =>
        automatic is not null && !string.IsNullOrWhiteSpace(show) && choices.Automatic is { } config
        && AutomaticImageQuery.ForShow(config.Query, show, block) is { } query
            ? automatic.Find(config, query)
            : null;

    /// <summary>What an override is matched by: whether it names a block, and the trimmed name.</summary>
    public static string Key(GeneratedShowImage show) => (show.Block ? "block:" : "show:") + show.Show.Trim().ToLowerInvariant();

    // Loosely matched (see ShowTitleMatcher), so "Powerpuff Girls" finds "The Powerpuff Girls" and "Squibillies" finds "Squidbillies".
    private static string? Match(IGeneratedImageChoices choices, string? name, bool block) => Scheduling.ShowTitleMatcher.Find(name,
        choices.Shows.Where(candidate => candidate.Block == block && !string.IsNullOrWhiteSpace(candidate.Path)), candidate => candidate.Show)?.Path;
}

public sealed class GeneratedVariableImage : IGeneratedImageChoices
{
    public string Name { get; set; } = "variable_image_01";

    /// <summary>The show a use without its own selector is resolved for (see <see cref="GeneratedImageSelectors"/>).</summary>
    public GeneratedImageContext Context { get; set; }
    /// <summary>Up Next only: zero is next, one is the show after that.</summary>
    public int Offset { get; set; }

    /// <summary>
    /// Id of a shared image (<c>settings.plugins.generated-promos.shared_images</c>) whose default and overrides this
    /// variable uses instead of its own, so several templates can be kept in step from one place.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Shared { get; set; }

    /// <summary>The shared image <see cref="Shared"/> names, found when the configuration is loaded or edited.</summary>
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public GeneratedSharedImage? Linked { get; set; }

    public string? DefaultPath { get; set; }
    public List<GeneratedShowImage> Shows { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AutomaticImageConfig? Automatic { get; set; }

    /// <summary>Whether this variable takes its images from a shared image.</summary>
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool IsShared => !string.IsNullOrWhiteSpace(Shared);

    /// <summary>
    /// Where the images come from: the linked shared image, or this variable's own. A link whose shared image is
    /// missing has no images (rather than quietly falling back to stale ones).
    /// </summary>
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public IGeneratedImageChoices Choices => IsShared ? (IGeneratedImageChoices?)Linked ?? GeneratedSharedImage.Missing : this;

    /// <summary>
    /// The file for a programme: an override naming its show first, then one naming the block it airs in, then
    /// the default. Empty when none applies.
    /// </summary>
    public string PathFor(string? show, string? block, IAutomaticImageLookup? automatic = null) => GeneratedImageChoices.PathFor(Choices, show, block, automatic);

    /// <summary>A copy, still linked to the same shared image.</summary>
    public GeneratedVariableImage Copy() => new()
    {
        Name = Name, Context = Context, Offset = Offset, Shared = Shared, Linked = Linked, DefaultPath = DefaultPath,
        Shows = [.. Shows.Select(show => new GeneratedShowImage { Show = show.Show, Block = show.Block, Path = show.Path })],
        Automatic = Automatic?.Copy()
    };
}

/// <summary>
/// A variable image's pictures kept once for every template (<c>settings.plugins.generated-promos.shared_images</c>).
/// Identified by <see cref="Id"/>, so two with the same name never mix, including across imports.
/// </summary>
public sealed class GeneratedSharedImage : IGeneratedImageChoices
{
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "Shared image";

    public string? DefaultPath { get; set; }

    public List<GeneratedShowImage> Shows { get; set; } = [];

    /// <summary>A picture search for shows without an override; see <see cref="IGeneratedImageChoices.Automatic"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AutomaticImageConfig? Automatic { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Stands in for a link to a shared image that doesn't exist: no images.</summary>
    internal static readonly GeneratedSharedImage Missing = new() { Id = string.Empty, Name = string.Empty };
}

/// <summary>Finding and linking shared images.</summary>
public static class GeneratedSharedImages
{
    public static GeneratedSharedImage? Find(IEnumerable<GeneratedSharedImage>? library, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null
            : library?.FirstOrDefault(image => string.Equals(image.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Points every template variable that names a shared image at it (or at nothing, when it's gone).</summary>
    public static void Link(SchedulerConfig config)
    {
        foreach (GeneratedPromoTemplate template in Templates(config))
        {
            Link(template, config.Settings.GeneratedPromosSharedImages);
        }
    }

    public static void Link(GeneratedPromoTemplate template, IEnumerable<GeneratedSharedImage>? library)
    {
        foreach (GeneratedVariableImage image in template.VariableImages ?? [])
        {
            image.Linked = image.IsShared ? Find(library, image.Shared) : null;
        }
    }

    /// <summary>Every generated promo template in the schedule, on channels and blocks.</summary>
    public static IEnumerable<GeneratedPromoTemplate> Templates(SchedulerConfig config) =>
        config.Channels.SelectMany(channel => (channel.GeneratedPromos?.Templates ?? [])
            .Concat(channel.Blocks.SelectMany(block => block.GeneratedPromos?.Templates ?? [])));

    /// <summary>
    /// How many variables use the shared image: generated promo templates', and any plugin's (overlays' variable images
    /// name it the same way, by its id, which is unique enough to be found in their sections as written).
    /// </summary>
    public static int Uses(SchedulerConfig config, string id) =>
        Templates(config).SelectMany(template => template.VariableImages ?? [])
            .Count(image => string.Equals(image.Shared, id, StringComparison.OrdinalIgnoreCase))
        + PluginUses(config, id);

    private static int PluginUses(SchedulerConfig config, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return 0;
        }

        int uses = 0;
        IEnumerable<Dictionary<string, object?>?> sections = config.Channels
            .SelectMany(channel => channel.Blocks.Select(block => block.Plugins).Prepend(channel.Plugins));
        foreach (Dictionary<string, object?>? plugins in sections)
        {
            foreach (object? section in plugins?.Values ?? Enumerable.Empty<object?>())
            {
                if (section is null)
                {
                    continue;
                }

                string text;
                try
                {
                    text = SchedulerConfigLoader.SerializeSection(section);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    continue;
                }

                for (int at = text.IndexOf(id, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(id, at + id.Length, StringComparison.OrdinalIgnoreCase))
                {
                    uses++;
                }
            }
        }

        return uses;
    }
}

public sealed class GeneratedShowImage
{
    /// <summary>Series/show title, or the block name when <see cref="Block"/> is set; matched without regard to case.</summary>
    public string Show { get; set; } = string.Empty;

    /// <summary>
    /// Matches the schedule block a programme airs in instead of its show. Used when no show override matches.
    /// </summary>
    public bool Block { get; set; }

    public string? Path { get; set; }
}

public sealed class GeneratedVoiceConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>User-authored scripts instead of automatic wording; null keeps legacy read-outs and render keys.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedAdvancedVoiceConfig? Advanced { get; set; }

    /// <summary>Text-to-speech provider id, e.g. <c>tiktok</c>.</summary>
    public string Provider { get; set; } = "tiktok";

    /// <summary>Provider-specific voice name or id.</summary>
    public string Voice { get; set; } = string.Empty;

    /// <summary>Openers to pick from per promo; an empty entry says nothing, an empty list uses the type's usual opener.</summary>
    public List<string> PreMessages { get; set; } = [];

    /// <summary>Closers to pick from per promo, e.g. <c>Only on {channel}!</c>; an empty entry says nothing.</summary>
    public List<string> PostMessages { get; set; } = [];

    /// <summary>Single opener from older files; moved into <see cref="PreMessages"/> on load.</summary>
    public string? PreMessage { get; set; }

    /// <summary>Single closer from older files; moved into <see cref="PostMessages"/> on load.</summary>
    public string? PostMessage { get; set; }

    /// <summary>Delay from the start of the promo before the voice begins.</summary>
    public TimeSpan Start { get; set; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Has a language model word the read-out; null reads the usual wording.</summary>
    /// <remarks>Left out of render keys while null, so templates without it keep their renders.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiTextConfig? Writer { get; set; }
}

public sealed class GeneratedAdvancedVoiceConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>One eligible template is chosen per promo. Missing referenced upcoming listings make a template ineligible.</summary>
    public List<string> Templates { get; set; } = [];
    public bool IncludePreMessages { get; set; }
    public bool IncludePostMessages { get; set; }
    /// <summary>Added to a later upcoming title when the same show was listed earlier; spaces are literal.</summary>
    public string RepeatPrefix { get; set; } = "more ";
    public string RepeatSuffix { get; set; } = string.Empty;
}

public sealed class GeneratedMusicConfig
{
    /// <summary>Songs to choose from, one per promo; each loops when shorter than the promo.</summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>Single-song form from older files; moved into <see cref="Paths"/> on load.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Songs from a music library (Jellyfin's, local music folders, a plugin's) instead of <see cref="Paths"/>: one per
    /// promo, chosen by what the promo is, so a re-render sounds the same. While it's set, <see cref="Paths"/> are kept
    /// but not used. Null (rather than empty) keeps render keys of templates without it unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SchedularrTV.Core.Media.BackgroundMusic? Library { get; set; }

    /// <summary>Whether the songs come from <see cref="Library"/> rather than <see cref="Paths"/>.</summary>
    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool FromLibrary => Library is { FromLibrary: true };

    [YamlIgnore]
    public IReadOnlyList<string> Songs =>
        [.. (Path is { Length: > 0 } path ? Paths.Prepend(path) : Paths).Where(song => !string.IsNullOrWhiteSpace(song)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>0 to 1, relative to the file's own level: the master volume, which each song's own one multiplies.</summary>
    public double Volume { get; set; } = 0.35;

    /// <summary>
    /// Per-song volumes (0 to 2, 1 = as loud as the master) keyed by path, for songs louder or quieter than the rest;
    /// null (rather than empty) keeps render keys of templates without them unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? SongVolumes { get; set; }

    /// <summary>The song's own volume, 1 when it has none.</summary>
    public double VolumeFor(string song)
    {
        if (SongVolumes is not null)
        {
            foreach ((string path, double volume) in SongVolumes)
            {
                if (string.Equals(path, song, StringComparison.OrdinalIgnoreCase)) return volume;
            }
        }

        return 1;
    }

    /// <summary>How loud the song plays: the master volume times its own, at most full level.</summary>
    public double EffectiveVolume(string song) => Math.Clamp(Volume * VolumeFor(song), 0, 1);

    /// <summary>Offset into the song where the promo starts, for songs without their own entry in <see cref="SongStarts"/>.</summary>
    public TimeSpan StartAt { get; set; }

    /// <summary>Per-song offsets keyed by path; null (rather than empty) keeps render cache keys of older templates unchanged.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TimeSpan>? SongStarts { get; set; }

    /// <summary>Where the promo starts in <paramref name="song"/>.</summary>
    public TimeSpan StartFor(string song)
    {
        if (SongStarts is not null)
        {
            foreach ((string path, TimeSpan start) in SongStarts)
            {
                if (string.Equals(path, song, StringComparison.OrdinalIgnoreCase)) return start;
            }
        }

        return StartAt;
    }

    public TimeSpan FadeIn { get; set; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan FadeOut { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Lowers the music while the voice-over is speaking.</summary>
    public bool DuckUnderVoice { get; set; } = true;

    /// <summary>
    /// Lowers the music while the episode clip's sound is playing. Null (not false) when off, so templates without it
    /// keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? DuckUnderClip { get; set; }
}

/// <summary>How a background video's own sound plays under the promo.</summary>
public sealed class GeneratedBackgroundSoundConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>0 to 1, relative to the video's own level.</summary>
    public double Volume { get; set; } = 1;

    /// <summary>Lowers it while the voice-over is speaking.</summary>
    public bool DuckUnderVoice { get; set; } = true;
}

public sealed class GeneratedClipConfig
{
    /// <summary>Offset into the episode; null picks a point in its middle, different for each airing.</summary>
    public TimeSpan? StartAt { get; set; }

    public bool IncludeAudio { get; set; }

    /// <summary>0 to 1, relative to the episode's own level.</summary>
    public double Volume { get; set; } = 0.6;

    /// <summary>Lowers the episode's sound while the voice-over is speaking.</summary>
    public bool DuckUnderVoice { get; set; } = true;
}

/// <summary>
/// An external program that renders a promo's video. Arguments may use <c>{tokens}</c>; <c>{shows}</c> expands
/// <see cref="PerShow"/> once per listed show and <c>{clip_arguments}</c> expands <see cref="ClipArguments"/>
/// when an episode clip is available.
/// </summary>
public sealed class GeneratedScriptConfig
{
    public string Command { get; set; } = "node";

    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// A command line run once in <see cref="WorkingDirectory"/> before the program is first used there (and again if
    /// it changes), e.g. <c>npm install</c>. It goes through the system shell. Null (rather than empty) keeps render
    /// keys of templates without it unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SetupCommand { get; set; }

    public List<string> Arguments { get; set; } = [];

    public List<string> PerShow { get; set; } = [];

    public List<string> ClipArguments { get; set; } = [];

    /// <summary>Up Next only: how many upcoming shows are passed.</summary>
    public int Shows { get; set; } = 1;

    /// <summary>Container for <c>{output}</c>; also decides whether the program can keep an alpha channel.</summary>
    public string OutputExtension { get; set; } = ".mp4";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Mixes in the generated video's own sound, if it has any.</summary>
    public bool KeepAudio { get; set; } = true;

    public double Volume { get; set; } = 1;

    public bool DuckUnderVoice { get; set; } = true;
}

/// <summary>Where a layer is, how big and how opaque, and how that changes over the promo.</summary>
public interface IGeneratedPose
{
    float X { get; set; }

    float Y { get; set; }

    float Width { get; set; }

    float Height { get; set; }

    float Opacity { get; set; }

    /// <summary>Degrees clockwise, about the middle of the box; null is 0.</summary>
    float? Rotation { get; set; }

    /// <summary>Corner pin: where each corner of the box is pulled to, for a perspective look; null leaves it square.</summary>
    GeneratedCorners? Corners { get; set; }

    /// <summary>While there are any, they set the pose over time instead of the values above.</summary>
    List<GeneratedKeyframe>? Keyframes { get; set; }
}

/// <summary>
/// A corner pin: how far (in pixels, along the box's own edges, before it's turned) each corner of a layer's box is
/// pulled from where it would be. The layer's contents are stretched to the four corners with perspective, so text can
/// sit on the side of a building, or follow a turning block's face as the corners are keyframed.
/// </summary>
public sealed class GeneratedCorners
{
    public float TopLeftX { get; set; }
    public float TopLeftY { get; set; }
    public float TopRightX { get; set; }
    public float TopRightY { get; set; }
    public float BottomRightX { get; set; }
    public float BottomRightY { get; set; }
    public float BottomLeftX { get; set; }
    public float BottomLeftY { get; set; }

    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool IsSquare => new[] { TopLeftX, TopLeftY, TopRightX, TopRightY, BottomRightX, BottomRightY, BottomLeftX, BottomLeftY }
        .All(value => Math.Abs(value) < 0.01f);

    [YamlIgnore, System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinite => new[] { TopLeftX, TopLeftY, TopRightX, TopRightY, BottomRightX, BottomRightY, BottomLeftX, BottomLeftY }
        .All(float.IsFinite);
}

/// <summary>How a template's layers are placed over one of its backgrounds, where that differs from the template.</summary>
public sealed class GeneratedBackgroundOverride
{
    /// <summary>The background file, as listed in the template's backgrounds.</summary>
    public string Background { get; set; } = string.Empty;

    public List<GeneratedLayerOverride> Layers { get; set; } = [];

    /// <summary>
    /// Layers coloured differently over this background (darker text over a light one, say), by layer id. Kept apart
    /// from <see cref="Layers"/>, so recolouring a layer leaves it following the template's position and keyframes.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedLayerColor>? Colors { get; set; }

    /// <summary>
    /// Layers timed differently over this background (when they appear and leave, and how they come and go), by layer
    /// id. Kept apart from <see cref="Layers"/> and <see cref="Colors"/>, so retiming a layer leaves the rest following the template.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedLayerTiming>? Timings { get; set; }
}

/// <summary>When a layer is on screen, and how it comes and goes.</summary>
public interface IGeneratedTiming
{
    TimeSpan Start { get; set; }

    /// <summary>Null keeps it until the promo ends.</summary>
    TimeSpan? End { get; set; }

    GeneratedTransition TransitionIn { get; set; }

    GeneratedTransition TransitionOut { get; set; }

    /// <summary>How long the enter transition takes (and the exit, unless <see cref="TransitionOutDuration"/> says otherwise).</summary>
    TimeSpan TransitionDuration { get; set; }

    /// <summary>How long the exit transition takes; null takes as long as the enter (<see cref="TransitionDuration"/>).</summary>
    TimeSpan? TransitionOutDuration { get; set; }
}

/// <summary>A layer's whole timing over one background, replacing the layer's own for it.</summary>
public sealed class GeneratedLayerTiming : IGeneratedTiming
{
    /// <summary>The layer's <see cref="GeneratedLayer.Id"/>.</summary>
    public string Layer { get; set; } = string.Empty;

    public TimeSpan Start { get; set; }

    public TimeSpan? End { get; set; }

    public GeneratedTransition TransitionIn { get; set; } = GeneratedTransition.Fade;

    public GeneratedTransition TransitionOut { get; set; } = GeneratedTransition.Fade;

    public TimeSpan TransitionDuration { get; set; } = TimeSpan.FromMilliseconds(400);

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? TransitionOutDuration { get; set; }
}

/// <summary>A layer's colour over one background, replacing <see cref="GeneratedLayer.Color"/> for it.</summary>
public sealed class GeneratedLayerColor
{
    /// <summary>The layer's <see cref="GeneratedLayer.Id"/>.</summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>Hex colour, e.g. <c>#202020</c> or <c>#80000000</c>.</summary>
    public string Color { get; set; } = "#FFFFFF";
}

/// <summary>A layer's whole pose over one background, replacing the template's own for it.</summary>
public sealed class GeneratedLayerOverride : IGeneratedPose
{
    /// <summary>The layer's <see cref="GeneratedLayer.Id"/>.</summary>
    public string Layer { get; set; } = string.Empty;

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; } = 400;

    public float Height { get; set; } = 100;

    public float Opacity { get; set; } = 1;

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? Rotation { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedCorners? Corners { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedKeyframe>? Keyframes { get; set; }
}

/// <summary>
/// A variable (or shared) image's automatic picture: for a show without an override of its own, a picture search (a
/// plugin such as SerpApi) with a query like <c>{show} main character png</c>. What it finds is kept, so the same query
/// always gets the same picture. Used wherever variable images are, in generated promos and overlays.
/// </summary>
public sealed class AutomaticImageConfig
{
    /// <summary>What to search for; <c>{show}</c> and <c>{block}</c> are filled in for the show first.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Which result to use: 1 is the first (best) one, 2 the second…, counting only pictures that can be used.</summary>
    public int Result { get; set; } = 1;

    /// <summary>Only pictures with a transparent background.</summary>
    public bool Transparent { get; set; }

    /// <summary>Only animated pictures (GIFs), which play in promos and overlays.</summary>
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Animated { get; set; }

    /// <summary>The picture search's id; null uses the first one set up.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; set; }

    public AutomaticImageConfig Copy() => (AutomaticImageConfig)MemberwiseClone();
}

/// <summary>Add reveals inside the combined masks; subtract hides inside them, after any add masks.</summary>
public enum GeneratedMaskMode { Add, Subtract }

/// <summary>A shape or image used only as an alpha mask for a drawable layer below it.</summary>
public sealed class GeneratedLayerMask
{
    public GeneratedMaskMode Mode { get; set; } = GeneratedMaskMode.Subtract;

    /// <summary>The target layer's stable id.</summary>
    public string? Target { get; set; }
}

public sealed class GeneratedLayer : IGeneratedPose, IGeneratedTiming
{
    /// <summary>
    /// Stable identity for per-background placements (<see cref="GeneratedPromoTemplate.BackgroundOverrides"/>), groups,
    /// and mask targets; assigned when needed, so templates without them keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="Id"/> of the <see cref="GeneratedLayerKind.Group"/> this layer is inside; null at the top. A layer
    /// inside a group has its position in the group's space (from the group's top-left corner), so it moves, rotates and
    /// fades with the group. Layers still draw in list order; the editor keeps a group's layers right after it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Parent { get; set; }

    /// <summary>When set, this shape or image masks its target instead of drawing as visible artwork.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedLayerMask? Mask { get; set; }

    /// <summary>
    /// Locked in the editor: it can't be picked or moved on the canvas (a locked group locks what's inside it). Only
    /// for editing, so it's left out of render keys and doesn't change how the promo looks.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public bool Locked { get; set; }

    public GeneratedLayerKind Kind { get; set; } = GeneratedLayerKind.Text;

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; } = 400;

    public float Height { get; set; } = 100;

    public TimeSpan Start { get; set; }

    /// <summary>Null keeps the layer until the promo ends.</summary>
    public TimeSpan? End { get; set; }

    public GeneratedTransition TransitionIn { get; set; } = GeneratedTransition.Fade;

    public GeneratedTransition TransitionOut { get; set; } = GeneratedTransition.Fade;

    /// <summary>How long the enter transition takes (and the exit, unless <see cref="TransitionOutDuration"/> is set).</summary>
    public TimeSpan TransitionDuration { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long the exit transition takes; null takes as long as the enter. Null keeps render keys of older templates.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? TransitionOutDuration { get; set; }

    public float Opacity { get; set; } = 1;

    /// <summary>Hex fill for rectangles and text, e.g. <c>#FFFFFF</c> or <c>#80000000</c>.</summary>
    public string Color { get; set; } = "#FFFFFF";

    public float CornerRadius { get; set; }

    /// <summary>Geometry of a Rect layer; null retains the original rectangle.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedShape? Shape { get; set; }

    /// <summary>
    /// Clockwise degrees around the box's centre, for every kind of layer (a group turns everything inside it about its
    /// centre); null is 0. Keyframes animate it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? Rotation { get; set; }

    /// <summary>Corner pin for a perspective look (see <see cref="GeneratedCorners"/>); null leaves the box square. Keyframes animate it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedCorners? Corners { get; set; }

    /// <summary>Gaussian edge softness in output pixels (sigma); null means sharp edges.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? SoftEdges { get; set; }

    /// <summary>Literal text for <see cref="GeneratedLayerKind.Text"/>; newlines split lines.</summary>
    public string? Text { get; set; }

    /// <summary>Image file for <see cref="GeneratedLayerKind.Image"/>, or video file for <see cref="GeneratedLayerKind.Video"/>.</summary>
    public string? Path { get; set; }

    /// <summary>For <see cref="GeneratedLayerKind.Video"/>: what it plays and how.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedVideoOptions? Video { get; set; }

    /// <summary>A named variable image instead of the static image path.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableImage { get; set; }
    /// <summary>
    /// Which show <see cref="VariableImage"/> is resolved for here (<c>promoted</c>, <c>watching</c>, <c>next</c>,
    /// <c>next2</c>…); null uses the variable's own show context. See <see cref="GeneratedImageSelectors"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableImageShow { get; set; }

    public string FontFamily { get; set; } = "Segoe UI";

    public float FontSize { get; set; } = 72;

    public bool Bold { get; set; } = true;

    public GeneratedTextAlign Align { get; set; } = GeneratedTextAlign.Left;

    /// <summary>Where the lines sit up and down the box; null is the top. Null rather than top keeps render keys of older templates.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedVerticalAlign? VerticalAlign { get; set; }

    /// <summary>Upper or lower cases every letter (show titles and AI-written text too); null keeps the text as written.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedTextCase? TextCase { get; set; }

    /// <summary>
    /// Breaks long lines between words to fill the box's width before the text shrinks. Null (off) keeps render keys of
    /// older templates.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? WordWrap { get; set; }

    /// <summary>
    /// Extra space between letters, as a percentage of the font size (10 spaces them out a little, negative tightens
    /// them, -50 to 200). Null (the font's own spacing) keeps render keys of older templates.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? LetterSpacing { get; set; }

    /// <summary>
    /// The distance between lines, as a multiple of the font's own (1.5 is half as much again, 0.8 tighter; 0.3 to 5).
    /// Null (the font's own) keeps render keys of older templates.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? LineSpacing { get; set; }

    public bool Shadow { get; set; } = true;

    public GeneratedUpNextOptions? UpNext { get; set; }

    /// <summary>Settings for Now Playing, This Has Been, Coming Up and Eventually text blocks.</summary>
    public GeneratedShowOptions? Show { get; set; }

    /// <summary>
    /// Mutes the episode clip's sound as much as the layer is opaque: as it fades in the clip fades out, and as it cuts
    /// in the sound cuts out. Null (not false) when off, so templates without it keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? MutesClip { get; set; }

    /// <summary>For <see cref="GeneratedLayerKind.Text"/>: has a language model write it, with <see cref="Text"/> as the fallback.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiTextConfig? Writer { get; set; }

    /// <summary>
    /// Animates position, size and opacity: while there are any, they replace <see cref="X"/>, <see cref="Y"/>,
    /// <see cref="Width"/>, <see cref="Height"/> and <see cref="Opacity"/>, which are kept for when keyframes are turned
    /// off. Enter and exit transitions still play on top. Null when off, so templates without them keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<GeneratedKeyframe>? Keyframes { get; set; }
}

/// <summary>How a keyframe moves on to the next one.</summary>
public enum GeneratedEasing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,

    /// <summary>Stays put, then jumps to the next keyframe when it's reached.</summary>
    Hold
}

/// <summary>A layer's pose at one moment; between keyframes it moves from one to the next.</summary>
public sealed class GeneratedKeyframe
{
    /// <summary>From the start of the promo (not of the layer).</summary>
    public TimeSpan At { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Width { get; set; } = 400;

    public float Height { get; set; } = 100;

    public float Opacity { get; set; } = 1;

    /// <summary>Degrees clockwise about the middle of the box.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitDefaults)]
    public float Rotation { get; set; }

    /// <summary>The corner pin at this moment; null is square.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GeneratedCorners? Corners { get; set; }

    /// <summary>How it moves from here to the next keyframe.</summary>
    public GeneratedEasing Easing { get; set; } = GeneratedEasing.Linear;
}

/// <summary>Each enabled part is drawn on its own line, in this order.</summary>
public sealed class GeneratedShowOptions
{
    public bool Title { get; set; } = true;

    public bool EpisodeTitle { get; set; }

    /// <summary>Today, Tonight, Tomorrow, or the weekday.</summary>
    public bool Day { get; set; }

    public bool Time { get; set; }
}

public sealed class GeneratedUpNextOptions
{
    /// <summary>Programmes skipped first: 0 starts with the one that follows the break, 1 with the one after.</summary>
    public int Offset { get; set; }

    /// <summary>Programmes listed, each on its own line.</summary>
    public int ShowsAhead { get; set; } = 1;

    /// <summary>Prefixes each line with its local start time.</summary>
    public bool IncludeTime { get; set; }

    /// <summary>
    /// Text before the first show, e.g. <c>Up next: </c>; a new line in it puts it on its own line. <c>{channel}</c> is
    /// the channel name; <c>{start_time}</c> and <c>{end_time}</c> are the selected shows' first start and last end,
    /// in channel-local time. Null when unused, so templates without it keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Prefix { get; set; }

    /// <summary>Text after the last show, e.g. <c> on {channel}</c>; works like <see cref="Prefix"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Suffix { get; set; }
}
