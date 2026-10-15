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
    BackToTheShow
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
    BackToTheShow
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
        _ => GeneratedLayerKind.UpNext
    };

    public static bool IsShowLayer(GeneratedLayerKind kind) =>
        kind is GeneratedLayerKind.NowPlaying or GeneratedLayerKind.ThisHasBeen or GeneratedLayerKind.ComingUp or GeneratedLayerKind.Eventually
            or GeneratedLayerKind.YoureWatching or GeneratedLayerKind.BackToTheShow;
}

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

public sealed class GeneratedPromosConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Channel id whose own generated promos are used instead of <see cref="Templates"/>, so both stay identical.</summary>
    public string? From { get; set; }

    public List<GeneratedPromoTemplate> Templates { get; set; } = [];
}

public sealed class GeneratedPromoTemplate
{
    public string Name { get; set; } = string.Empty;

    public GeneratedPromoType Type { get; set; } = GeneratedPromoType.UpNext;

    public bool Enabled { get; set; } = true;

    /// <summary>Fixed so breaks can be packed before anything is rendered.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);

    public GeneratedPromoUsage Usage { get; set; } = GeneratedPromoUsage.Fallback;

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

public sealed class GeneratedVariableImage
{
    public string Name { get; set; } = "variable_image_01";

    /// <summary>The show a use without its own selector is resolved for (see <see cref="GeneratedImageSelectors"/>).</summary>
    public GeneratedImageContext Context { get; set; }
    /// <summary>Up Next only: zero is next, one is the show after that.</summary>
    public int Offset { get; set; }
    public string? DefaultPath { get; set; }
    public List<GeneratedShowImage> Shows { get; set; } = [];
}

public sealed class GeneratedShowImage
{
    /// <summary>Series/show title, matched without regard to case.</summary>
    public string Show { get; set; } = string.Empty;
    public string? Path { get; set; }
}

public sealed class GeneratedVoiceConfig
{
    public bool Enabled { get; set; } = true;

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

public sealed class GeneratedLayer
{
    public string Name { get; set; } = string.Empty;

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

    public TimeSpan TransitionDuration { get; set; } = TimeSpan.FromMilliseconds(400);

    public float Opacity { get; set; } = 1;

    /// <summary>Hex fill for rectangles and text, e.g. <c>#FFFFFF</c> or <c>#80000000</c>.</summary>
    public string Color { get; set; } = "#FFFFFF";

    public float CornerRadius { get; set; }

    /// <summary>Literal text for <see cref="GeneratedLayerKind.Text"/>; newlines split lines.</summary>
    public string? Text { get; set; }

    /// <summary>Image file for <see cref="GeneratedLayerKind.Image"/>.</summary>
    public string? Path { get; set; }

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
    /// the channel name. Null when unused, so templates without it keep their render keys.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Prefix { get; set; }

    /// <summary>Text after the last show, e.g. <c> on {channel}</c>; works like <see cref="Prefix"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Suffix { get; set; }
}
