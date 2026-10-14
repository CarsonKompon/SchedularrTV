using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Writing;

namespace SchedularrTV.GeneratedPromos;

/// <summary>A template with its schedule-dependent text filled in, identified by what it will look and sound like.</summary>
public sealed record ResolvedGeneratedPromo(
    GeneratedPromoTemplate Template,
    IReadOnlyList<IReadOnlyList<string>> LayerLines,
    string? Script,
    string Key)
{
    /// <summary>Sub-folder of the render cache this promo is written to.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>The promoted episode's file when the template plays a clip of it.</summary>
    public string? ClipPath { get; init; }

    public TimeSpan ClipStart { get; init; }

    /// <summary>External generator arguments with <see cref="GeneratedPromoResolver.OutputToken"/> left for the render to fill in.</summary>
    public IReadOnlyList<string> GeneratorArguments { get; init; } = [];

    public IReadOnlyDictionary<string, string> VariableImages { get; init; } = new Dictionary<string, string>();

    /// <summary>When an AI writes the voice-over: what it's asked. <see cref="Script"/> is the usual read-out it falls back on.</summary>
    public AiTextJob? ScriptJob { get; init; }

    /// <summary>AI-written text layers by index; <see cref="LayerLines"/> holds their usual text until they're written.</summary>
    public IReadOnlyDictionary<int, AiTextJob> LayerJobs { get; init; } = new Dictionary<int, AiTextJob>();
}

public static partial class GeneratedPromoResolver
{
    private static readonly JsonSerializerOptions KeyJson = new() { WriteIndented = false };

    // Control characters can't come from show titles, so filling these in later can't touch real text.
    public const string OutputToken = "\u0001output\u0001";
    public const string OutputDirectoryToken = "\u0001output_dir\u0001";

    public static int ListingsNeeded(GeneratedPromoTemplate template) =>
        template.Type != GeneratedPromoType.UpNext ? 1
        : template.Generator is { } generator ? Math.Clamp(generator.Shows, 1, 10)
        : Math.Max(1, template.Layers
        .Where(layer => layer.Kind == GeneratedLayerKind.UpNext)
        .Select(layer => (layer.UpNext?.Offset ?? 0) + (layer.UpNext?.ShowsAhead ?? 1))
        .DefaultIfEmpty(1)
        .Max());

    /// <summary>Usual opener for each promo type; a template's own pre-message replaces it.</summary>
    public static string DefaultPreMessage(GeneratedPromoType type) => type switch
    {
        GeneratedPromoType.NowPlaying => "You're watching",
        GeneratedPromoType.ThisHasBeen => "This has been",
        GeneratedPromoType.ComingUp => "Coming up",
        GeneratedPromoType.Eventually => "Don't miss",
        GeneratedPromoType.YoureWatching => "You're watching",
        GeneratedPromoType.BackToTheShow => "Now, back to",
        _ => "Coming up next..."
    };

    /// <summary>
    /// Null when there is nothing to promote. <paramref name="listings"/> are the upcoming programmes
    /// for Up Next; for every other type the first listing is the promoted show.
    /// </summary>
    public static ResolvedGeneratedPromo? Resolve(
        GeneratedPromoTemplate template,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset? airsAt = null,
        GeneratedImageSubjects? imageSubjects = null)
    {
        if (listings.Count == 0)
        {
            return null;
        }

        List<UpNextListing> used = [.. listings.Take(ListingsNeeded(template))];
        IReadOnlyDictionary<string, string> images = GeneratedPromoImages.Resolve(template, listings, imageSubjects);
        DateTimeOffset promoTime = airsAt ?? used[0].Start;

        List<IReadOnlyList<string>> lines = [.. template.Layers.Select(layer => Lines(layer, used, zone, channelName, promoTime))];
        string variety = $"{string.Join('|', used.Select(listing => $"{listing.Title}@{listing.Start.UtcTicks}"))}|{promoTime.UtcTicks}";
        string? script = template.Voice is { Enabled: true } voice
            ? Script(template.Type, used, zone, promoTime,
                Tokens(Phrase(voice.PreMessages, voice.PreMessage, $"{variety}|pre"), channelName),
                Tokens(Phrase(voice.PostMessages, voice.PostMessage, $"{variety}|post"), channelName),
                variety)
            : null;

        string identity = JsonSerializer.Serialize(new { Version = 1, Template = template, Lines = lines, Script = script }, KeyJson);
        if (images.Count > 0)
        {
            identity += $"|images|{JsonSerializer.Serialize(images, KeyJson)}";
            foreach (string path in images.Values.Distinct(StringComparer.Ordinal))
            {
                if (File.Exists(path)) identity += $"|{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{new FileInfo(path).Length}";
            }
        }

        // AI-written words aren't known yet, so the promo is keyed by what the writer is asked (which takes in what it
        // may say about the shows); templates without AI writing keep the keys they had.
        AiTextJob? scriptJob = script is null ? null : GeneratedPromoWriting.Voice(template, channelName, used, zone, promoTime, script, variety);
        Dictionary<int, AiTextJob> layerJobs = [];
        for (int index = 0; index < template.Layers.Count; index++)
        {
            if (GeneratedPromoWriting.Layer(template, template.Layers[index], index, channelName, used, zone, promoTime,
                    string.Join('\n', lines[index]), variety) is { } layerJob)
            {
                layerJobs[index] = layerJob;
            }
        }

        List<AiTextJob> jobs = [.. layerJobs.OrderBy(entry => entry.Key).Select(entry => entry.Value)];
        if (scriptJob is not null)
        {
            jobs.Insert(0, scriptJob);
        }

        foreach (AiTextJob job in jobs)
        {
            identity += $"|ai|{AiTextRules.Identity(job)}";
        }

        (string Path, TimeSpan Start)? clip = Clip(template, used[0]);
        if (clip is { } source)
        {
            identity += $"|clip|{source.Path}|{source.Start.Ticks}";
        }

        // Background videos once played silently; promos that now play one's sound are rendered again.
        if (BackgroundSound(template).Enabled && template.BackgroundChoices.Any(path => !PromoRenderer.IsImage(path)))
        {
            identity += "|background-sound";
        }

        IReadOnlyList<string> arguments = template.Generator is { } generator
            ? GeneratorArguments(generator, template, channelName, used, zone, promoTime, clip, images)
            : [];
        if (arguments.Count > 0)
        {
            identity += $"|generator|{JsonSerializer.Serialize(arguments, KeyJson)}";
        }

        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return new ResolvedGeneratedPromo(template, lines, script, key)
        {
            ClipPath = clip?.Path,
            ClipStart = clip?.Start ?? TimeSpan.Zero,
            GeneratorArguments = arguments,
            VariableImages = images,
            ScriptJob = scriptJob,
            LayerJobs = layerJobs
        };
    }

    /// <summary>
    /// The generator's argument list for these shows. Each argument is filled in once, so a show title
    /// that happens to contain a <c>{token}</c> is passed through as written.
    /// </summary>
    public static IReadOnlyList<string> GeneratorArguments(
        GeneratedScriptConfig generator,
        GeneratedPromoTemplate template,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset airsAt,
        (string Path, TimeSpan Start)? clip,
        IReadOnlyDictionary<string, string>? images = null)
    {
        int divisor = (int)BigInteger.GreatestCommonDivisor(template.Width, template.Height);
        Dictionary<string, string> shared = new(StringComparer.OrdinalIgnoreCase)
        {
            ["channel"] = channelName,
            ["type"] = UnderscoredName(template.Type.ToString()),
            ["duration"] = Invariant(template.Duration.TotalSeconds),
            ["width"] = Invariant(template.Width),
            ["height"] = Invariant(template.Height),
            ["aspect"] = $"{template.Width / Math.Max(1, divisor)}:{template.Height / Math.Max(1, divisor)}",
            ["fps"] = Invariant(template.Fps),
            ["output"] = OutputToken,
            ["output_dir"] = OutputDirectoryToken,
            ["clip"] = clip?.Path ?? string.Empty,
            ["clip_start"] = Invariant(Math.Round((clip?.Start ?? TimeSpan.Zero).TotalSeconds, 3)),
            ["show_count"] = Invariant(listings.Count)
        };
        foreach ((string name, string value) in ShowTokens(listings[0], 0, zone, airsAt))
        {
            shared[name] = value;
        }
        foreach ((string name, string path) in images ?? GeneratedPromoImages.Resolve(template, listings))
        {
            shared[name] = path;
        }

        List<string> arguments = [];
        foreach (string argument in generator.Arguments)
        {
            if (string.Equals(argument.Trim(), "{shows}", StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 0; index < listings.Count; index++)
                {
                    Dictionary<string, string> values = new(shared, StringComparer.OrdinalIgnoreCase);
                    foreach ((string name, string value) in ShowTokens(listings[index], index, zone, airsAt))
                    {
                        values[name] = value;
                    }

                    arguments.AddRange(generator.PerShow.Select(part => Fill(part, values)));
                }
            }
            else if (string.Equals(argument.Trim(), "{clip_arguments}", StringComparison.OrdinalIgnoreCase))
            {
                if (clip is not null)
                {
                    arguments.AddRange(generator.ClipArguments.Select(part => Fill(part, shared)));
                }
            }
            else
            {
                arguments.Add(Fill(argument, shared));
            }
        }

        return arguments;
    }

    /// <summary>Tokens a generator argument can use, for the editor's help text.</summary>
    public static readonly IReadOnlyList<(string Token, string Meaning)> GeneratorTokens =
    [
        ("{title}", "show name"),
        ("{episode}", "episode title"),
        ("{time}", "start time, e.g. 8:30 PM"),
        ("{time_or_next}", "NEXT for the first show, else its time"),
        ("{day}", "Today, Tonight, Tomorrow or the weekday"),
        ("{when}", "day and time, e.g. Tonight at 8:30"),
        ("{index}", "1 for the first show, 2 for the next…"),
        ("{channel}", "channel name"),
        ("{type}", "up_next, now_playing…"),
        ("{duration}", "promo length in seconds"),
        ("{width}", "width"), ("{height}", "height"), ("{aspect}", "e.g. 16:9"), ("{fps}", "frame rate"),
        ("{clip}", "episode file"), ("{clip_start}", "seconds into it"),
        ("{variable_image_01}", "resolved image path for the named variable image (also works in show and clip argument groups)"),
        ("{output}", "file to write"), ("{output_dir}", "its folder")
    ];

    private static IEnumerable<(string Name, string Value)> ShowTokens(UpNextListing show, int index, TimeZoneInfo zone, DateTimeOffset airsAt)
    {
        string time = TimeZoneInfo.ConvertTime(show.Start, zone).ToString("h:mm tt", CultureInfo.InvariantCulture);
        yield return ("title", show.Title);
        yield return ("episode", show.Episode ?? string.Empty);
        yield return ("time", time);
        yield return ("time_or_next", index == 0 ? "NEXT" : time);
        yield return ("day", UpNextPromo.When(show.Start, airsAt, zone, day: true, time: false));
        yield return ("when", UpNextPromo.When(show.Start, airsAt, zone, day: true, time: true));
        yield return ("index", Invariant(index + 1));
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        TokenPattern().Replace(text, match => values.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);

    private static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string UnderscoredName(string name) =>
        string.Concat(name.Select((character, index) => index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : $"{char.ToLowerInvariant(character)}"));

    [GeneratedRegex(@"\{([a-z_][a-z_0-9]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    /// <summary>Whether the generator's arguments hand it the episode clip, so it plays the clip itself.</summary>
    public static bool UsesClip(GeneratedScriptConfig? generator) =>
        generator is not null && generator.Arguments.Concat(generator.PerShow).Concat(generator.ClipArguments)
            .Any(argument => ClipTokenPattern().IsMatch(argument));

    [GeneratedRegex(@"\{clip(_start|_arguments)?\}", RegexOptions.IgnoreCase)]
    private static partial Regex ClipTokenPattern();

    /// <summary>Whether the generator can't run without a clip: <c>{clip}</c> or <c>{clip_start}</c> outside the clip-only arguments.</summary>
    public static bool RequiresClip(GeneratedScriptConfig? generator) =>
        generator is not null && generator.Arguments.Concat(generator.PerShow).Any(argument => RequiredClipPattern().IsMatch(argument));

    [GeneratedRegex(@"\{clip(_start)?\}", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredClipPattern();

    /// <summary>
    /// The promoted episode's clip, for playing behind the promo or handing to a generator that asks for it; null
    /// uses the template's own background. Without a set start it begins somewhere in the middle of the episode,
    /// chosen per airing, so repeats look different.
    /// </summary>
    public static (string Path, TimeSpan Start)? Clip(GeneratedPromoTemplate template, UpNextListing show)
    {
        if ((template.BackgroundClip is null && !UsesClip(template.Generator)) || string.IsNullOrWhiteSpace(show.Path)
            || PromoRenderer.IsImage(show.Path) || !File.Exists(show.Path))
        {
            return null;
        }

        TimeSpan room = show.Runtime is { } runtime ? runtime - template.Duration : TimeSpan.Zero;
        if (template.BackgroundClip?.StartAt is { } start)
        {
            return (show.Path, room > TimeSpan.Zero && start > room ? room : start);
        }

        if (room <= TimeSpan.Zero)
        {
            return (show.Path, TimeSpan.Zero);
        }

        uint hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{show.Path}|{show.Start.UtcTicks}|clip")));
        double fraction = 0.15 + 0.6 * (hash / (double)uint.MaxValue);
        return (show.Path, TimeSpan.FromSeconds(Math.Round(Math.Min(room.TotalSeconds, show.Runtime!.Value.TotalSeconds * fraction))));
    }

    /// <param name="generated">The external generator's finished video, laid over the background and scaled to fit.</param>
    public static PromoComposition Compose(ResolvedGeneratedPromo promo, string? audioPath = null, double audioTempo = 1, string? generated = null)
    {
        GeneratedPromoTemplate template = promo.Template;
        // A generator that is handed the clip plays it itself, so it isn't laid underneath as well.
        string? clipPath = template.BackgroundClip is null || UsesClip(template.Generator) ? null : promo.ClipPath;
        string? background = clipPath ?? Background(template.BackgroundChoices, promo.Key);
        return new PromoComposition
        {
            Width = template.Width,
            Height = template.Height,
            Fps = template.Fps,
            Duration = template.Duration,
            BackgroundColor = SKColor.Parse(template.BackgroundColor),
            BackgroundPath = background,
            BackgroundStart = clipPath is null ? TimeSpan.Zero : promo.ClipStart,
            BackgroundAudio = clipPath is not null
                ? template.BackgroundClip is { IncludeAudio: true } clip
                    ? new PromoMusic(clipPath, clip.Volume, promo.ClipStart, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600), clip.DuckUnderVoice)
                    {
                        Levels = ClipLevels(template)
                    }
                    : null
                // A background video loops with its own sound (the render drops it if the file has none).
                : background is not null && !PromoRenderer.IsImage(background) && BackgroundSound(template) is { Enabled: true } sound
                    ? new PromoMusic(background, sound.Volume, TimeSpan.Zero, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600), sound.DuckUnderVoice)
                    : null,
            OverlayPath = generated,
            OverlayAudio = generated is not null && template.Generator is { KeepAudio: true } generator
                ? new PromoMusic(generated, generator.Volume, TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(300), generator.DuckUnderVoice, Loop: false)
                : null,
            AudioPath = audioPath,
            AudioStart = template.Voice?.Start ?? TimeSpan.Zero,
            AudioTempo = audioTempo,
            // Songs from a music library are picked (and fetched) when the promo is rendered.
            Music = template.Music?.Songs is { Count: > 0 } songs && template.Music is { FromLibrary: false } music
                && Song(songs, promo.Key) is { } song
                ? new PromoMusic(song, music.EffectiveVolume(song), music.StartFor(song), music.FadeIn, music.FadeOut, music.DuckUnderVoice)
                {
                    DuckUnderBackground = music.DuckUnderClip == true
                }
                : null,
            Layers = template.Generator is not null ? []
                : [.. template.Layers.Select((layer, index) => Layer(layer, promo.LayerLines[index], promo.VariableImages)).OfType<PromoLayer>()]
        };
    }

    /// <summary>The episode clip's volume curve from the layers that mute it, or null when none do.</summary>
    public static IReadOnlyList<PromoLevel>? ClipLevels(GeneratedPromoTemplate template)
    {
        List<PromoLayer> blockers = template.Generator is not null ? [] : [.. template.Layers
            .Where(layer => layer.MutesClip == true)
            .Select(layer => (PromoLayer)new PromoRectLayer
            {
                Bounds = SKRect.Empty,
                Start = layer.Start,
                End = layer.End,
                In = Enum.Parse<PromoTransition>(layer.TransitionIn.ToString()),
                Out = Enum.Parse<PromoTransition>(layer.TransitionOut.ToString()),
                TransitionDuration = layer.TransitionDuration,
                Opacity = layer.Opacity,
                Keyframes = Keyframes(layer)
            })];
        return blockers.Count == 0 ? null : PromoAudioEnvelope.ForBlockers(blockers, template.Duration);
    }

    /// <summary>How a background video's sound plays: the template's settings, or the usual ones (on) when it has none.</summary>
    public static GeneratedBackgroundSoundConfig BackgroundSound(GeneratedPromoTemplate template) =>
        template.BackgroundSound ?? new GeneratedBackgroundSoundConfig();

    /// <summary>The layer's keyframes in time order for drawing, or null when it has none.</summary>
    public static IReadOnlyList<PromoKeyframe>? Keyframes(GeneratedLayer layer) =>
        layer.Keyframes is { Count: > 0 } keyframes
            ? keyframes
                .OrderBy(key => key.At)
                .Select(key => new PromoKeyframe(key.At, SKRect.Create(key.X, key.Y, key.Width, key.Height), key.Opacity,
                    Enum.Parse<PromoEasing>(key.Easing.ToString())))
                .ToList()
            : null;

    /// <summary>One song per promo, fixed by its content so a re-render sounds the same.</summary>
    public static string Song(IReadOnlyList<string> songs, string key) =>
        songs[(int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|song"))) % (uint)songs.Count)];

    /// <summary>One background per promo, picked at random but fixed by its content so a re-render looks the same; null when there are none.</summary>
    public static string? Background(IReadOnlyList<string> backgrounds, string key) =>
        backgrounds.Count switch
        {
            0 => null,
            1 => backgrounds[0],
            _ => backgrounds[(int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|background"))) % (uint)backgrounds.Count)]
        };

    private static string Script(
        GeneratedPromoType type,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset airsAt,
        string? preMessage,
        string? postMessage,
        string variety)
    {
        if (type == GeneratedPromoType.UpNext)
        {
            return UpNextPromo.Script(listings, zone, preMessage, postMessage, variety);
        }

        // Null means no opener was set, so the type's usual one is said; an empty pick says nothing.
        UpNextListing show = listings[0];
        string pre = preMessage is null ? DefaultPreMessage(type) : preMessage.Trim();
        string when = UpNextPromo.When(show.Start, airsAt, zone, day: true, time: true);
        string body = (type switch
        {
            GeneratedPromoType.ComingUp => $"{pre} {(pre.Length == 0 ? when : when.ToLowerInvariant())}, {show.Title}",
            GeneratedPromoType.Eventually => $"{pre} {show.Title}, {when}",
            _ => $"{pre} {show.Title}"
        }).Trim();
        return string.Join(' ', new[] { UpNextPromo.Sentence(body, '!'), postMessage }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// One phrase from <paramref name="options"/> for this promo, or the single legacy phrase. Null when there are none,
    /// which means the usual opener; an empty entry picked means say nothing.
    /// </summary>
    public static string? Phrase(IReadOnlyList<string> options, string? legacy, string seed) =>
        options.Count > 0 ? options[UpNextPromo.Pick(seed, options.Count)]
        : string.IsNullOrWhiteSpace(legacy) ? null
        : legacy;

    private static IReadOnlyList<string> Lines(
        GeneratedLayer layer,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        string channelName,
        DateTimeOffset airsAt) =>
        layer.Kind switch
        {
            GeneratedLayerKind.UpNext => Affix([.. listings
                .Skip(layer.UpNext?.Offset ?? 0)
                .Take(layer.UpNext?.ShowsAhead ?? 1)
                .Select(listing => layer.UpNext?.IncludeTime == true
                    ? $"{UpNextPromo.Clock(listing.Start, zone)}  {listing.Title}"
                    : listing.Title)], layer.UpNext?.Prefix, layer.UpNext?.Suffix, channelName),
            GeneratedLayerKind.Text => (Tokens(layer.Text, channelName) ?? string.Empty).Replace("\r\n", "\n").Split('\n'),
            _ when GeneratedPromoKinds.IsShowLayer(layer.Kind) => ShowLines(layer, listings[0], zone, airsAt),
            _ => []
        };

    /// <summary>
    /// <paramref name="prefix"/> joined onto the first line and <paramref name="suffix"/> onto the last; new lines in
    /// either start lines of their own. Nothing is added when there are no shows to list.
    /// </summary>
    private static IReadOnlyList<string> Affix(List<string> lines, string? prefix, string? suffix, string channelName)
    {
        if (lines.Count == 0 || (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix)))
        {
            return lines;
        }

        string text = Tokens(prefix, channelName) + string.Join('\n', lines) + Tokens(suffix, channelName);
        return text.Replace("\r\n", "\n").Split('\n');
    }

    private static IReadOnlyList<string> ShowLines(GeneratedLayer layer, UpNextListing show, TimeZoneInfo zone, DateTimeOffset airsAt)
    {
        GeneratedShowOptions options = layer.Show ?? DefaultShowOptions(layer.Kind);
        List<string> lines = [];
        if (options.Title) lines.Add(show.Title);
        if (options.EpisodeTitle && !string.IsNullOrWhiteSpace(show.Episode)) lines.Add(show.Episode);
        if (options.Day || options.Time) lines.Add(UpNextPromo.When(show.Start, airsAt, zone, options.Day, options.Time));
        return lines;
    }

    public static GeneratedShowOptions DefaultShowOptions(GeneratedLayerKind kind) => kind switch
    {
        GeneratedLayerKind.ComingUp => new GeneratedShowOptions { Time = true },
        GeneratedLayerKind.Eventually => new GeneratedShowOptions { Day = true, Time = true },
        _ => new GeneratedShowOptions()
    };

    private static string? Tokens(string? text, string channelName) =>
        text?.Replace("{channel}", channelName, StringComparison.OrdinalIgnoreCase);

    private static PromoLayer? Layer(GeneratedLayer layer, IReadOnlyList<string> lines, IReadOnlyDictionary<string, string> images)
    {
        SKRect bounds = SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height);
        SKColor color = SKColor.Parse(layer.Color);
        string? imagePath = layer.VariableImage is { Length: > 0 } variable ? images.GetValueOrDefault(variable) : layer.Path;
        PromoLayer? drawn = layer.Kind switch
        {
            GeneratedLayerKind.Rect => new PromoRectLayer { Bounds = bounds, Color = color, CornerRadius = layer.CornerRadius },
            GeneratedLayerKind.Image when !string.IsNullOrWhiteSpace(imagePath) => new PromoImageLayer { Bounds = bounds, Path = imagePath },
            var kind when (kind is GeneratedLayerKind.Text or GeneratedLayerKind.UpNext || GeneratedPromoKinds.IsShowLayer(kind))
                && lines.Any(line => line.Length > 0) => new PromoTextLayer
            {
                Bounds = bounds,
                // Cased here rather than when the lines are worked out, so AI-written text is cased too.
                Lines = layer.TextCase switch
                {
                    GeneratedTextCase.Upper => lines.Select(line => line.ToUpperInvariant()).ToList(),
                    GeneratedTextCase.Lower => lines.Select(line => line.ToLowerInvariant()).ToList(),
                    _ => lines
                },
                VerticalAlign = layer.VerticalAlign switch
                {
                    GeneratedVerticalAlign.Middle => PromoVerticalAlign.Middle,
                    GeneratedVerticalAlign.Bottom => PromoVerticalAlign.Bottom,
                    _ => PromoVerticalAlign.Top
                },
                Color = color,
                MaxFontSize = layer.FontSize,
                FontFamily = layer.FontFamily,
                Bold = layer.Bold,
                Align = layer.Align switch
                {
                    GeneratedTextAlign.Center => SKTextAlign.Center,
                    GeneratedTextAlign.Right => SKTextAlign.Right,
                    _ => SKTextAlign.Left
                },
                Shadow = layer.Shadow ? new SKColor(0, 0, 0, 160) : null
            },
            _ => null
        };

        return drawn is null ? null : drawn with
        {
            Start = layer.Start,
            End = layer.End,
            In = Enum.Parse<PromoTransition>(layer.TransitionIn.ToString()),
            Out = Enum.Parse<PromoTransition>(layer.TransitionOut.ToString()),
            TransitionDuration = layer.TransitionDuration,
            Opacity = layer.Opacity,
            Keyframes = Keyframes(layer)
        };
    }
}

/// <summary>Ready-made templates, also the starting point for new ones in the editor.</summary>
public static class GeneratedPromoTemplates
{
    // The TikTok voices plugin's id and default voice. Named here rather than taken from that plugin, which is
    // optional: TikTok needs no setup, so new templates start with it.
    public const string DefaultVoiceProvider = "tiktok";
    public const string DefaultVoice = "en_us_006";

    public static GeneratedPromoTemplate UpNext(string name = "Up Next", string voice = DefaultVoice) => new()
    {
        Name = name,
        Type = GeneratedPromoType.UpNext,
        Duration = TimeSpan.FromSeconds(10),
        Voice = new GeneratedVoiceConfig
        {
            Provider = DefaultVoiceProvider,
            Voice = voice,
            PreMessages = ["Coming up next...", "Up next...", "Stay tuned for"],
            PostMessages = ["Only on {channel}!", "Right here on {channel}!", ""]
        },
        Layers =
        [
            new() { Name = "Panel", Kind = GeneratedLayerKind.Rect, X = 110, Y = 250, Width = 1220, Height = 580, Color = "#96000000",
                CornerRadius = 28, End = TimeSpan.FromSeconds(9.4), TransitionIn = GeneratedTransition.SlideRight },
            new() { Name = "Accent", Kind = GeneratedLayerKind.Rect, X = 110, Y = 250, Width = 22, Height = 580, Color = "#FFC400",
                Start = TimeSpan.FromSeconds(0.15), End = TimeSpan.FromSeconds(9.4), TransitionIn = GeneratedTransition.SlideDown },
            new() { Name = "Heading", Kind = GeneratedLayerKind.Text, Text = "UP NEXT", X = 180, Y = 290, Width = 1060, Height = 110,
                Color = "#FFC400", FontSize = 92, Start = TimeSpan.FromSeconds(0.25), End = TimeSpan.FromSeconds(9.4),
                TransitionIn = GeneratedTransition.SlideRight },
            new() { Name = "Next show", Kind = GeneratedLayerKind.UpNext, X = 180, Y = 420, Width = 1060, Height = 170, FontSize = 132,
                Start = TimeSpan.FromSeconds(0.55), End = TimeSpan.FromSeconds(9.4), TransitionIn = GeneratedTransition.Zoom,
                UpNext = new GeneratedUpNextOptions { ShowsAhead = 1 } },
            new() { Name = "Later", Kind = GeneratedLayerKind.UpNext, X = 180, Y = 650, Width = 1060, Height = 60, FontSize = 54,
                Bold = false, Color = "#E1E8F2", Start = TimeSpan.FromSeconds(1.2), End = TimeSpan.FromSeconds(9.4),
                TransitionIn = GeneratedTransition.SlideUp, UpNext = new GeneratedUpNextOptions { Offset = 1, ShowsAhead = 1, IncludeTime = true } },
            new() { Name = "Channel", Kind = GeneratedLayerKind.Text, Text = "{channel}", X = 1260, Y = 920, Width = 560, Height = 90,
                FontSize = 64, Align = GeneratedTextAlign.Right, Start = TimeSpan.FromSeconds(0.8) }
        ]
    };

    /// <summary>A working starting point for any promo type, with its required text block already placed.</summary>
    public static GeneratedPromoTemplate ForType(GeneratedPromoType type, string? name = null, string voice = DefaultVoice)
    {
        if (type == GeneratedPromoType.UpNext)
        {
            return UpNext(name ?? "Up Next", voice);
        }

        (string label, string heading, double seconds, GeneratedShowOptions show) = type switch
        {
            GeneratedPromoType.NowPlaying => ("Now Playing", "YOU'RE WATCHING", 6, new GeneratedShowOptions()),
            GeneratedPromoType.ThisHasBeen => ("This Has Been", "THIS HAS BEEN", 6, new GeneratedShowOptions()),
            GeneratedPromoType.ComingUp => ("Coming Up", "COMING UP", 8, new GeneratedShowOptions { Day = true, Time = true }),
            GeneratedPromoType.YoureWatching => ("You're Watching", "YOU'RE WATCHING", 5, new GeneratedShowOptions()),
            GeneratedPromoType.BackToTheShow => ("Back to the Show", "NOW, BACK TO", 5, new GeneratedShowOptions()),
            _ => ("Eventually", "DON'T MISS", 10, new GeneratedShowOptions { Day = true, Time = true })
        };
        TimeSpan leave = TimeSpan.FromSeconds(seconds - 0.6);
        GeneratedLayerKind kind = GeneratedPromoKinds.RequiredLayer(type);

        return new GeneratedPromoTemplate
        {
            Name = name ?? label,
            Type = type,
            Duration = TimeSpan.FromSeconds(seconds),
            Voice = new GeneratedVoiceConfig
            {
                Provider = DefaultVoiceProvider,
                Voice = voice,
                PostMessages = type is GeneratedPromoType.ComingUp or GeneratedPromoType.Eventually ? ["Only on {channel}!"] : []
            },
            Layers =
            [
                new() { Name = "Panel", Kind = GeneratedLayerKind.Rect, X = 110, Y = 250, Width = 1220, Height = 580, Color = "#96000000",
                    CornerRadius = 28, End = leave, TransitionIn = GeneratedTransition.SlideRight },
                new() { Name = "Accent", Kind = GeneratedLayerKind.Rect, X = 110, Y = 250, Width = 22, Height = 580, Color = "#FFC400",
                    Start = TimeSpan.FromSeconds(0.15), End = leave, TransitionIn = GeneratedTransition.SlideDown },
                new() { Name = "Heading", Kind = GeneratedLayerKind.Text, Text = heading, X = 180, Y = 290, Width = 1060, Height = 110,
                    Color = "#FFC400", FontSize = 92, Start = TimeSpan.FromSeconds(0.25), End = leave, TransitionIn = GeneratedTransition.SlideRight },
                new() { Name = "Show", Kind = kind, X = 180, Y = 420, Width = 1060, Height = show.Day || show.Time ? 300 : 170, FontSize = 132,
                    Start = TimeSpan.FromSeconds(0.55), End = leave, TransitionIn = GeneratedTransition.Zoom, Show = show },
                new() { Name = "Channel", Kind = GeneratedLayerKind.Text, Text = "{channel}", X = 1260, Y = 920, Width = 560, Height = 90,
                    FontSize = 64, Align = GeneratedTextAlign.Right, Start = TimeSpan.FromSeconds(0.8) }
            ]
        };
    }
}
