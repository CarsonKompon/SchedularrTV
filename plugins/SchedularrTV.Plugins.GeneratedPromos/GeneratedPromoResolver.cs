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

    /// <summary>Background chosen by show rules; null also represents no eligible background while rules are enabled.</summary>
    public string? SelectedBackground { get; init; }

    /// <summary>The id of the variant picked for this promo; null for the template's default look (or a template without variants).</summary>
    public string? Variant { get; init; }

    /// <summary>The file or wording picked for each layer (by index) that has others to pick from and didn't use its own.</summary>
    public IReadOnlyDictionary<int, string> LayerChoices { get; init; } = new Dictionary<int, string>();

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

    /// <summary>What each video layer plays, by index: its file, or a clip of its episode. Missing when there's nothing to play.</summary>
    public IReadOnlyDictionary<int, PromoVideoSource> LayerVideos { get; init; } = new Dictionary<int, PromoVideoSource>();

    /// <summary>
    /// The video layer curated by AI, when the template has one: its episode and what the writer may say. The plan is
    /// made with the render; until then (and whenever it can't be) the layer plays its one clip as usual.
    /// </summary>
    public CuratedPromoRequest? Curation { get; init; }

    /// <summary>The curated layer's clips, once its plan is laid out; null plays the layer as usual.</summary>
    public IReadOnlyList<CuratedClip>? CuratedClips { get; init; }

    /// <summary>
    /// A pre-recorded voice-over: what it can be made from. One template and its takes are chosen (to fit the promo)
    /// when it's rendered; <see cref="Script"/> only describes one for the editor.
    /// </summary>
    public PrerecordedVoiceRequest? Prerecorded { get; init; }

    /// <summary>
    /// The voice-over is pre-recorded, but nothing could be said from the recordings for these shows, so <see cref="Script"/>
    /// is spoken by text-to-speech instead.
    /// </summary>
    public bool TtsFallback { get; init; }

    /// <summary>
    /// Whether a variable image's automatic picture was still being searched for, so the promo will look different once
    /// it's found (it isn't made until then).
    /// </summary>
    public bool ImagesPending { get; init; }
}

/// <summary>A video file and where in it a video layer starts playing.</summary>
public sealed record PromoVideoSource(string Path, TimeSpan Start);

public static partial class GeneratedPromoResolver
{
    private static readonly JsonSerializerOptions KeyJson = new() { WriteIndented = false };

    // Control characters can't come from show titles, so filling these in later can't touch real text.
    public const string OutputToken = "\u0001output\u0001";
    public const string OutputDirectoryToken = "\u0001output_dir\u0001";

    /// <summary>
    /// How many upcoming shows an Up Next promo is about, which is also how many the automatic voice-over reads out: as far ahead
    /// as its Up Next text lists, or its image layers show (an image of the show after next counts as two).
    /// </summary>
    public static int ListingsNeeded(GeneratedPromoTemplate template) =>
        template.Type != GeneratedPromoType.UpNext ? 1
        : template.Generator is { } generator ? Math.Clamp(generator.Shows, 1, 10)
        : Math.Max(1, template.Layers
            .Where(layer => layer.Kind == GeneratedLayerKind.UpNext)
            .Select(layer => (layer.UpNext?.Offset ?? 0) + (layer.UpNext?.ShowsAhead ?? 1))
            .Concat(GeneratedPromoImages.UpcomingShown(template))
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
        GeneratedPromoType.NewEpisode => "Don't miss an all-new",
        GeneratedPromoType.RightBack => "We'll be right back with more",
        _ => "Coming up next..."
    };

    /// <summary>
    /// Null when there is nothing to promote. <paramref name="listings"/> are the upcoming programmes
    /// for Up Next; for every other type the first listing is the promoted show.
    /// </summary>
    /// <param name="where">The channel and block the promo airs in, for pre-recorded voice-overs that name them.</param>
    public static ResolvedGeneratedPromo? Resolve(
        GeneratedPromoTemplate template,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset? airsAt = null,
        GeneratedImageSubjects? imageSubjects = null,
        IAutomaticImageLookup? automaticImages = null,
        GeneratedVoiceContext? where = null,
        SchedularrTV.Core.Media.MediaItem? programme = null)
    {
        if (listings.Count == 0)
        {
            return null;
        }

        IReadOnlyList<UpNextListing> upcoming = imageSubjects?.Upcoming
            ?? (template.Type is GeneratedPromoType.UpNext or GeneratedPromoType.NowPlaying ? listings : []);
        List<UpNextListing> used = [.. (template.Type == GeneratedPromoType.UpNext && upcoming.Count > 0 ? upcoming : listings)
            .Take(ListingsNeeded(template))];
        // Variable images' automatic pictures are part of the promo's key, so finding one later makes the promo again with it.
        IReadOnlyDictionary<string, string> images = GeneratedPromoImages.Resolve(template, listings, imageSubjects, automaticImages);
        DateTimeOffset promoTime = airsAt ?? used[0].Start;
        string variety = $"{string.Join('|', used.Select(listing => $"{listing.Title}@{listing.Start.UtcTicks}"))}|{promoTime.UtcTicks}";

        // What pick rules (backgrounds', variants' and layers') look at: the Now Playing show itself, which is
        // authoritative even when it started before the promo or the schedule still calls the previous one "current" at
        // a break boundary; otherwise the shows coming up.
        bool nowPlaying = template.Type == GeneratedPromoType.NowPlaying;
        IReadOnlyList<UpNextListing> ruleSubjects = nowPlaying ? used
            : imageSubjects?.Upcoming ?? (template.Type == GeneratedPromoType.UpNext ? listings : []);

        // The variant this promo shows (null: the template's default look), picked once per promo.
        GeneratedPromoVariant? variant = GeneratedPromoBackgrounds.SelectVariant(template, ruleSubjects, promoTime, variety, programme, nowPlaying, zone);

        // Each layer's file or wording, for layers with others to pick from; the rest use their own.
        Dictionary<int, string> choices = [];
        List<GeneratedLayer> effective = new(template.Layers.Count);
        for (int index = 0; index < template.Layers.Count; index++)
        {
            GeneratedLayer layer = template.Layers[index];
            string own = (layer.Kind == GeneratedLayerKind.Text ? layer.Text : layer.Path) ?? string.Empty;
            if (GeneratedPromoBackgrounds.SelectChoice(layer, ruleSubjects, promoTime, $"{variety}|layer|{layer.Id ?? index.ToString(CultureInfo.InvariantCulture)}",
                    nowPlaying, zone) is { } chosen && !string.Equals(chosen, own, StringComparison.Ordinal))
            {
                choices[index] = chosen;
                effective.Add(layer.WithChoice(chosen));
            }
            else
            {
                effective.Add(layer);
            }
        }

        List<IReadOnlyList<string>> lines = [.. effective.Select(layer => Lines(layer,
            layer.Kind == GeneratedLayerKind.UpNext ? upcoming : used, zone, channelName, promoTime))];

        // A pre-recorded voice-over: the voice pack templates that can say it for these shows (one is chosen, with its
        // takes, when the promo is made). Its read-out is only for the editor.
        PrerecordedVoiceRequest? prerecorded = template.Voice is { Enabled: true, IsPrerecorded: true } recordedVoice
            ? PrerecordedVoice.Plan(recordedVoice, PrerecordedVoice.Shows(template.Type, used, upcoming), zone, promoTime, variety, where)
            : null;
        // No voice pack template can be said for these shows (a recording is missing): text-to-speech says it instead, when
        // the template allows it, with its text-to-speech settings.
        bool ttsFallback = prerecorded is null && template.Voice is { Enabled: true, IsPrerecorded: true, Prerecorded.FallbackToTts: true };
        string? script = template.Voice is { Enabled: true, IsPrerecorded: true } && !ttsFallback
            ? prerecorded is null ? null : PrerecordedVoice.Describe(prerecorded)
            : template.Voice is { Enabled: true } voice
            ? voice.Advanced is { Enabled: true }
                ? GeneratedPromoVoice.Script(voice, upcoming, used[0], channelName, zone, variety)
                : Script(template.Type, used, zone, promoTime,
                SpokenTokens(Phrase(voice.PreMessages, voice.PreMessage, $"{variety}|pre"), used[0], zone, promoTime, channelName),
                SpokenTokens(Phrase(voice.PostMessages, voice.PostMessage, $"{variety}|post"), used[0], zone, promoTime, channelName),
                variety)
            : null;

        string identity = JsonSerializer.Serialize(new { Version = 1, Template = template, Lines = lines, Script = script }, KeyJson);
        foreach (GeneratedVideoCleanup removal in template.VideoCleanup ?? [])
            if (removal.Enabled && removal.Regions.Count > 0)
                identity += $"|video-cleanup|{PromoVideoCleanupService.Identity(removal)}";
        if (ttsFallback)
        {
            // Recording what's missing later makes the promo again, with the recordings.
            identity += "|tts-fallback";
        }

        if (prerecorded is not null)
        {
            // What it can be made from: the templates and takes, and each take's file as it is now (a re-recorded take
            // makes the promo again).
            identity += $"|prerecorded|{JsonSerializer.Serialize(prerecorded, KeyJson)}";
            foreach (string take in prerecorded.Candidates.SelectMany(candidate => candidate.Parts).SelectMany(part => part.Takes).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(take)) identity += $"|{take}|{File.GetLastWriteTimeUtc(take).Ticks}|{new FileInfo(take).Length}";
            }
        }
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
            // A layer the picked variant doesn't show isn't worth writing.
            if (!GeneratedPromoVariants.Visible(template, variant?.Id, template.Layers[index]))
            {
                continue;
            }

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

        // Each video layer's file and start: an episode's clip changes with the shows, so the promo does too.
        Dictionary<int, PromoVideoSource> videos = [];
        for (int index = 0; index < template.Layers.Count; index++)
        {
            if (VideoSource(template, effective[index], index, listings, imageSubjects, promoTime) is { } video)
            {
                videos[index] = video;
                identity += $"|video|{index}|{video.Path}|{video.Start.Ticks}";
                if (File.Exists(video.Path)) identity += $"|{File.GetLastWriteTimeUtc(video.Path).Ticks}";
            }
        }

        // A video layer curated by AI: its plan needs the episode's transcript, so it's written when the promo is made,
        // keyed (like AI writing) by what's asked: the template, the episode, and the usual read-out it falls back on.
        CuratedPromoRequest? curation = null;
        if (script is not null && prerecorded is null && !ttsFallback && CuratedPromo.LayerIndex(template) is { } curatedIndex && videos.TryGetValue(curatedIndex, out PromoVideoSource? curatedVideo)
            && template.Layers[curatedIndex].Video is { Curated: { } curated, Episode: { } curatedEpisode }
            && GeneratedImageSelectors.TryParse(curatedEpisode, out GeneratedImageContext curatedContext, out int curatedOffset)
            && GeneratedPromoImages.Show(template, listings, imageSubjects, curatedContext, curatedOffset) is { } curatedShow)
        {
            // The voice-over's own opener and closer (Said first / Said last) go around the AI's lines, picked with the
            // same seeds as the usual read-out; with advanced voice-over, only when it includes them.
            GeneratedVoiceConfig curatedVoice = template.Voice!;
            bool advanced = curatedVoice.Advanced is { Enabled: true };
            curation = new CuratedPromoRequest(curatedIndex, curated, curatedShow, curatedVideo.Path, channelName, template.Type, zone, promoTime,
                template.Duration, curatedVoice.Start, script, variety)
            {
                Opener = advanced && !curatedVoice.Advanced!.IncludePreMessages ? null
                    : Phrase(curatedVoice.PreMessages, curatedVoice.PreMessage, $"{variety}|pre"),
                Closer = advanced && !curatedVoice.Advanced!.IncludePostMessages ? null
                    : Phrase(curatedVoice.PostMessages, curatedVoice.PostMessage, $"{variety}|post"),
                Direction = CuratedPromo.Direction(curated, $"{variety}|direction|{curatedIndex}")
            };
            identity += $"|curated|{curatedIndex}|{curatedShow.Title}|{curatedShow.Episode}|{curatedShow.IsNew}";
        }

        // Background videos once played silently; promos that now play one's sound are rendered again.
        if (BackgroundSound(template).Enabled && template.BackgroundChoices
                .Concat((template.Variants ?? []).SelectMany(each => each.BackgroundChoices)).Any(path => !PromoRenderer.IsImage(path)))
        {
            identity += "|background-sound";
        }

        // The variant and each layer's pick are what the promo is, so another pick is another promo.
        if (template.HasVariants)
        {
            identity += $"|variant|{variant?.Id ?? "default"}";
        }

        if (choices.Count > 0)
        {
            identity += $"|choices|{JsonSerializer.Serialize(choices, KeyJson)}";
            foreach (string path in choices.Values.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                identity += $"|{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
            }
        }

        IReadOnlyList<string> arguments = template.Generator is { } generator
            ? GeneratorArguments(generator, template, channelName, used, zone, promoTime, clip, images)
            : [];
        if (arguments.Count > 0)
        {
            identity += $"|generator|{JsonSerializer.Serialize(arguments, KeyJson)}";
        }

        // With variants, the background comes from the picked variant (or the template's, for the default look and
        // variants without backgrounds of their own); otherwise from the template, by its rules when it has any.
        string? selectedBackground = null;
        IGeneratedBackgrounds backgroundHost = variant is { BackgroundChoices.Count: > 0 } ? variant : template;
        if (template.HasVariants || template.BackgroundRules is { Count: > 0 })
        {
            selectedBackground = GeneratedPromoBackgrounds.Select(backgroundHost, ruleSubjects, promoTime, identity + variety, nowPlaying, zone);
            identity += $"|selected-background|{JsonSerializer.Serialize(selectedBackground)}";
        }
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        return new ResolvedGeneratedPromo(template, lines, script, key)
        {
            SelectedBackground = selectedBackground,
            Variant = variant?.Id,
            LayerChoices = choices,
            ClipPath = clip?.Path,
            ClipStart = clip?.Start ?? TimeSpan.Zero,
            GeneratorArguments = arguments,
            VariableImages = images,
            ScriptJob = scriptJob,
            LayerJobs = layerJobs,
            LayerVideos = videos,
            Curation = curation,
            Prerecorded = prerecorded,
            TtsFallback = ttsFallback,
            ImagesPending = automaticImages is SchedularrTV.Service.Images.AutomaticImageLookup { Pending: true }
        };
    }

    /// <summary>
    /// What a video layer plays: its own file from its set start, or a clip of the episode it names (promoted, watching,
    /// next, next2…). Null for other layers, and when there's no file to play (no episode there, or one that can't be read).
    /// </summary>
    public static PromoVideoSource? VideoSource(GeneratedPromoTemplate template, GeneratedLayer layer, int index,
        IReadOnlyList<UpNextListing> listings, GeneratedImageSubjects? subjects, DateTimeOffset airsAt)
    {
        if (layer.Kind != GeneratedLayerKind.Video)
        {
            return null;
        }

        GeneratedVideoOptions options = layer.Video ?? new GeneratedVideoOptions();
        if (options.Episode is not { } episode)
        {
            return string.IsNullOrWhiteSpace(layer.Path) || PromoRenderer.IsImage(layer.Path)
                ? null
                : new PromoVideoSource(layer.Path, options.FileStart < TimeSpan.Zero ? TimeSpan.Zero : options.FileStart);
        }

        if (!GeneratedImageSelectors.TryParse(episode, out GeneratedImageContext context, out int offset)
            || GeneratedPromoImages.Show(template, listings, subjects, context, offset) is not { Path: { Length: > 0 } path } show
            || PromoRenderer.IsImage(path) || !File.Exists(path))
        {
            return null;
        }

        // A set start (kept clear of the end), or somewhere in the middle third-ish, different for each airing and layer.
        TimeSpan room = show.Runtime is { } runtime ? runtime - template.Duration : TimeSpan.Zero;
        if (options.ClipStart is { } start)
        {
            return new PromoVideoSource(path, room > TimeSpan.Zero && start > room ? room : start);
        }

        if (room <= TimeSpan.Zero)
        {
            return new PromoVideoSource(path, TimeSpan.Zero);
        }

        uint hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{show.Start.UtcTicks}|{airsAt.UtcTicks}|video|{index}")));
        double fraction = 0.15 + 0.6 * (hash / (double)uint.MaxValue);
        return new PromoVideoSource(path, TimeSpan.FromSeconds(Math.Round(Math.Min(room.TotalSeconds, show.Runtime!.Value.TotalSeconds * fraction))));
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
        ("{new}", "NEW for a new episode (a drop's first airing), else blank"),
        ("{channel}", "channel name"),
        ("{type}", "up_next, now_playing…"),
        ("{duration}", "promo length in seconds"),
        ("{width}", "width"), ("{height}", "height"), ("{aspect}", "e.g. 16:9"), ("{fps}", "frame rate"),
        ("{clip}", "episode file"), ("{clip_start}", "seconds into it"),
        ("{variable_image_01}", "resolved image path for the named variable image, for its own show context (also works in show and clip argument groups)"),
        ("{variable_image_01:next2}", "the same variable image for a chosen show: :promoted, :watching, :next, or :next2 to :next10 for that many shows ahead"),
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
        yield return ("new", show.IsNew ? "NEW" : string.Empty);
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        TokenPattern().Replace(text, match => values.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);

    private static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string UnderscoredName(string name) =>
        string.Concat(name.Select((character, index) => index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : $"{char.ToLowerInvariant(character)}"));

    // A name, optionally with a selector after a colon, as in {variable_image_01:next2}; looked up as written.
    [GeneratedRegex(@"\{([a-z_][a-z_0-9]*(?::[a-z0-9_]+)?)\}", RegexOptions.IgnoreCase)]
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
        string? background = clipPath ?? (template.HasVariants || template.BackgroundRules is { Count: > 0 }
            ? promo.SelectedBackground : Background(template.BackgroundChoices, promo.Key));
        // Layers are placed, timed and coloured as the picked variant has them. Older templates (per-background layouts
        // not yet turned into variants) go by the background; over an episode clip there's none, so they sit where the
        // template puts them.
        string? edits = template.HasVariants ? promo.Variant : clipPath is null ? background : null;
        Dictionary<int, PromoLayer> drawnByIndex = [];
        List<(PromoVideoLayer Layer, CuratedClip Clip)> curatedDrawn = [];
        List<PromoLayer> layers = template.Generator is not null ? [] : DrawnLayers(promo, edits, drawnByIndex, curatedDrawn);
        return new PromoComposition
        {
            LayerAudio = [.. VideoAudio(template, drawnByIndex), .. CuratedAudio(curatedDrawn, template.Duration)],
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
            AudioVolume = template.Voice?.EffectiveVolume ?? 1,
            // Songs from a music library are picked (and fetched) when the promo is rendered.
            Music = template.Music?.Songs is { Count: > 0 } songs && template.Music is { FromLibrary: false } music
                && Song(songs, promo.Key) is { } song
                ? new PromoMusic(song, music.EffectiveVolume(song), music.StartFor(song), music.FadeIn, music.FadeOut, music.DuckUnderVoice)
                {
                    DuckUnderBackground = music.DuckUnderClip == true
                }
                : null,
            Layers = layers
        };
    }

    /// <summary>
    /// The sound of each video layer that includes it: playing in step with its picture (from the promo's start, or
    /// held back until the layer appears), as loud as the layer is opaque, so it fades in and out with it.
    /// </summary>
    private static List<PromoMusic> VideoAudio(GeneratedPromoTemplate template, IReadOnlyDictionary<int, PromoLayer> drawn)
    {
        List<PromoMusic> sounds = [];
        foreach ((int index, PromoLayer layer) in drawn.OrderBy(entry => entry.Key))
        {
            if (layer is not PromoVideoLayer video || template.Layers[index].Video is not { IncludeAudio: true } options)
            {
                continue;
            }

            sounds.Add(new PromoMusic(video.Path, Math.Clamp(options.Volume, 0, 1), video.SourceStart, TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10), options.DuckUnderVoice, Loop: video.Loop)
            {
                Delay = video.StartsWithLayer ? video.Start : TimeSpan.Zero,
                Levels = PromoAudioEnvelope.ForLayer(video, template.Duration)
            });
        }

        return sounds;
    }

    /// <summary>
    /// The sound of a curated layer's clips: a clip speaking for itself as loud as it's set, a clip under the announcer
    /// quietly (or not at all). Each plays in step with its picture and stops with it.
    /// </summary>
    private static List<PromoMusic> CuratedAudio(IReadOnlyList<(PromoVideoLayer Layer, CuratedClip Clip)> clips, TimeSpan duration)
    {
        List<PromoMusic> sounds = [];
        foreach ((PromoVideoLayer layer, CuratedClip clip) in clips)
        {
            if (!clip.WithSound || clip.Volume <= 0)
            {
                continue;
            }

            sounds.Add(new PromoMusic(clip.Path, clip.Volume, clip.SourceStart, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(60),
                DuckUnderVoice: false, Loop: false)
            {
                Delay = clip.At,
                Levels = PromoAudioEnvelope.ForLayer(layer, duration)
            });
        }

        return sounds;
    }

    /// <summary>
    /// The template's layers for drawing, in list order, each linked to the group it's inside. A layer inside a group
    /// that isn't in the template (or a group inside itself) is drawn at the top level instead.
    /// </summary>
    /// <param name="byIndex">Filled with each drawn layer by its index in the template.</param>
    /// <param name="curated">
    /// Filled with the clips a curated layer becomes (one video layer each, placed and timed as its plan says) when the
    /// promo has them; that layer is then left out of <paramref name="byIndex"/>, as its sound is its clips'.
    /// </param>
    private static List<PromoLayer> DrawnLayers(ResolvedGeneratedPromo promo, string? background, Dictionary<int, PromoLayer> byIndex,
        List<(PromoVideoLayer Layer, CuratedClip Clip)> curated)
    {
        GeneratedPromoTemplate template = promo.Template;
        Dictionary<string, int> groups = new(StringComparer.Ordinal);
        for (int index = 0; index < template.Layers.Count; index++)
        {
            if (template.Layers[index] is { Kind: GeneratedLayerKind.Group, Id: { Length: > 0 } id })
            {
                groups.TryAdd(id, index);
            }
        }

        var built = new Dictionary<int, PromoLayer?>();
        var building = new HashSet<int>();
        PromoLayer? Build(int index)
        {
            if (built.TryGetValue(index, out PromoLayer? done))
            {
                return done;
            }

            GeneratedLayer layer = promo.LayerChoices.TryGetValue(index, out string? chosen) ? template.Layers[index].WithChoice(chosen) : template.Layers[index];
            PromoLayer? drawn = Layer(layer, Pose(template, background, layer), TimingFor(template, background, layer),
                ColorFor(template, background, layer), promo.LayerLines[index], promo.VariableImages, promo.LayerVideos.GetValueOrDefault(index));
            building.Add(index);
            if (drawn is not null && layer.Parent is { } parentId && groups.TryGetValue(parentId, out int parentIndex)
                && !building.Contains(parentIndex) && Build(parentIndex) is PromoGroupLayer parent)
            {
                drawn = drawn with { Parent = parent };
            }

            building.Remove(index);
            built[index] = drawn;
            return drawn;
        }

        List<PromoLayer> layers = [];
        for (int index = 0; index < template.Layers.Count; index++)
        {
            // Layers of other variants, and the template's layers this variant hides (with what's inside them), aren't drawn.
            if (!GeneratedPromoVariants.Visible(template, background, template.Layers[index]) || Build(index) is not { } drawn)
            {
                continue;
            }

            // A curated layer is its clips: each a cut of the episode where the plan puts it, in the layer's box.
            if (drawn is PromoVideoLayer video && promo.Curation?.LayerIndex == index && promo.CuratedClips is { Count: > 0 } clips)
            {
                foreach (CuratedClip clip in clips)
                {
                    PromoVideoLayer cut = video with
                    {
                        Path = clip.Path,
                        SourceStart = clip.SourceStart,
                        Start = clip.At,
                        End = clip.At + clip.Length,
                        In = PromoTransition.None,
                        Out = PromoTransition.None,
                        StartsWithLayer = true,
                        Loop = false
                    };
                    layers.Add(cut);
                    curated.Add((cut, clip));
                }

                continue;
            }

            // A curated layer without its clips (not curated this time, or not yet) shows nothing, if it's set to.
            if (drawn is PromoVideoLayer && template.Layers[index].Video is { Curated.WhenNotCurated: GeneratedCurationFallback.Nothing })
            {
                continue;
            }

            layers.Add(drawn);
            byIndex[index] = drawn;
        }

        return layers;
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
                TransitionOutDuration = layer.TransitionOutDuration,
                Opacity = layer.Opacity,
                Keyframes = Keyframes(layer)
            })];
        return blockers.Count == 0 ? null : PromoAudioEnvelope.ForBlockers(blockers, template.Duration);
    }

    /// <summary>How a background video's sound plays: the template's settings, or the usual ones (on) when it has none.</summary>
    public static GeneratedBackgroundSoundConfig BackgroundSound(GeneratedPromoTemplate template) =>
        template.BackgroundSound ?? new GeneratedBackgroundSoundConfig();

    /// <summary>
    /// Where the layer is placed over <paramref name="background"/>: that background's own placement of it, if the
    /// template has one, otherwise the layer's.
    /// </summary>
    public static IGeneratedPose Pose(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        Placement(template, background, layer) ?? (IGeneratedPose)layer;

    /// <summary>
    /// The layer edits that <paramref name="key"/> stands for: the variant with that id, or (in older templates not yet
    /// turned into variants) the background with that path's layout. Null for the template's own layout.
    /// </summary>
    public static IGeneratedLayerEdits? Edits(GeneratedPromoTemplate template, string? key) =>
        key is null ? null
            : (IGeneratedLayerEdits?)template.Variant(key)
              ?? template.BackgroundOverrides?.FirstOrDefault(tracked => string.Equals(tracked.Background, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The variant's (<paramref name="background"/>: its id, or an older template's background) own placement of the layer, or null when it uses the template's.</summary>
    public static GeneratedLayerOverride? Placement(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        layer.Id is not { Length: > 0 } id ? null
            : Edits(template, background)?.Layers.FirstOrDefault(placed => string.Equals(placed.Layer, id, StringComparison.Ordinal));

    /// <summary>The variant's own colour for the layer, or null when it uses the layer's.</summary>
    public static GeneratedLayerColor? Recolour(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        layer.Id is not { Length: > 0 } id ? null
            : Edits(template, background)?.Colors?.FirstOrDefault(colour => string.Equals(colour.Layer, id, StringComparison.Ordinal));

    /// <summary>The variant's own timing of the layer, or null when it uses the layer's.</summary>
    public static GeneratedLayerTiming? Retiming(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        layer.Id is not { Length: > 0 } id ? null
            : Edits(template, background)?.Timings?.FirstOrDefault(timed => string.Equals(timed.Layer, id, StringComparison.Ordinal));

    /// <summary>When the layer shows over <paramref name="background"/>, and how it comes and goes: that background's own timing, or the layer's.</summary>
    public static IGeneratedTiming TimingFor(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        Retiming(template, background, layer) ?? (IGeneratedTiming)layer;

    /// <summary>The layer's colour over <paramref name="background"/>: that background's own, or the layer's.</summary>
    public static string ColorFor(GeneratedPromoTemplate template, string? background, GeneratedLayer layer) =>
        Recolour(template, background, layer)?.Color ?? layer.Color;

    /// <summary>The pose's keyframes in time order for drawing, or null when it has none.</summary>
    public static IReadOnlyList<PromoKeyframe>? Keyframes(IGeneratedPose layer) =>
        layer.Keyframes is { Count: > 0 } keyframes
            ? keyframes
                .OrderBy(key => key.At)
                .Select(key => new PromoKeyframe(key.At, SKRect.Create(key.X, key.Y, key.Width, key.Height), key.Opacity,
                    Enum.Parse<PromoEasing>(key.Easing.ToString()), float.IsFinite(key.Rotation) ? key.Rotation : 0, PromoCorners.From(key.Corners)))
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
        // A new episode is said to be one, unless the opener already says so (New Episode's does).
        string title = show.IsNew && type != GeneratedPromoType.NewEpisode && !pre.Contains("new", StringComparison.OrdinalIgnoreCase)
            ? $"{SpokenNew} {UpNextPromo.SpokenTitle(show.Title)}"
            : UpNextPromo.SpokenTitle(show.Title);
        string body = (type switch
        {
            GeneratedPromoType.ComingUp => $"{pre} {(pre.Length == 0 ? when : when.ToLowerInvariant())}, {title}",
            GeneratedPromoType.Eventually or GeneratedPromoType.NewEpisode => $"{pre} {title}, {when}",
            _ => $"{pre} {title}"
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
            GeneratedLayerKind.UpNext => UpNextLines([.. listings
                .Skip(layer.UpNext?.Offset ?? 0)
                .Take(layer.UpNext?.ShowsAhead ?? 1)], layer.UpNext, zone, channelName),
            GeneratedLayerKind.Text => NewTokens(Tokens(layer.Text, channelName) ?? string.Empty, listings).Replace("\r\n", "\n").Split('\n'),
            _ when GeneratedPromoKinds.IsShowLayer(layer.Kind) => ShowLines(layer, listings[0], zone, airsAt),
            _ => []
        };

    /// <summary>
    /// The prefix joined onto the first line and the suffix onto the last; new lines in
    /// either start lines of their own. Nothing is added when there are no shows to list.
    /// </summary>
    private static IReadOnlyList<string> UpNextLines(IReadOnlyList<UpNextListing> listings, GeneratedUpNextOptions? options,
        TimeZoneInfo zone, string channelName)
    {
        List<string> lines = [.. listings.Select(listing => options?.IncludeTime == true
            ? $"{UpNextPromo.Clock(listing.Start, zone)}  {listing.Title}" : listing.Title)];
        string? prefix = options?.Prefix;
        string? suffix = options?.Suffix;
        if (lines.Count == 0 || (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix)))
        {
            return lines;
        }

        DateTimeOffset? end = listings[^1].End
            ?? (listings[^1].Runtime is { } runtime ? listings[^1].Start + runtime : null);
        string Expand(string? text) => (Tokens(text, channelName) ?? string.Empty)
            .Replace("{start_time}", UpNextPromo.Clock(listings[0].Start, zone), StringComparison.OrdinalIgnoreCase)
            .Replace("{end_time}", end is { } until ? UpNextPromo.Clock(until, zone) : string.Empty, StringComparison.OrdinalIgnoreCase);
        string text = Expand(prefix) + string.Join('\n', lines) + Expand(suffix);
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
        GeneratedLayerKind.Eventually or GeneratedLayerKind.NewEpisode => new GeneratedShowOptions { Day = true, Time = true },
        _ => new GeneratedShowOptions()
    };

    private static string? Tokens(string? text, string channelName) =>
        text?.Replace("{channel}", channelName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An opener or closer with its tokens filled in for <paramref name="show"/>: <c>{show}</c> (or <c>{show_name}</c>),
    /// <c>{episode}</c>, <c>{time}</c> (or <c>{show_time}</c>, <c>{start_time}</c>: "7", "7:30"), <c>{when}</c> ("tonight
    /// at 7"), <c>{day}</c> ("tonight") and <c>{channel}</c>. Unknown tokens are left as written; text without tokens is
    /// returned as it is.
    /// </summary>
    public static string? SpokenTokens(string? text, UpNextListing show, TimeZoneInfo zone, DateTimeOffset airsAt, string channelName)
    {
        if (text is null || !text.Contains('{'))
        {
            return text;
        }

        string time = UpNextPromo.SpokenClock(show.Start, zone);
        string title = UpNextPromo.SpokenTitle(show.Title);
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["show"] = title,
            ["show_name"] = title,
            ["episode"] = show.Episode ?? string.Empty,
            ["time"] = time,
            ["show_time"] = time,
            ["start_time"] = time,
            ["when"] = UpNextPromo.When(show.Start, airsAt, zone, day: true, time: true).ToLowerInvariant(),
            ["day"] = UpNextPromo.When(show.Start, airsAt, zone, day: true, time: false).ToLowerInvariant(),
            ["channel"] = channelName
        };
        return SpokenTokenPattern().Replace(text, match => tokens.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);
    }

    [GeneratedRegex(@"\{([a-z_]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex SpokenTokenPattern();

    /// <summary>
    /// <c>{new}</c> reads "NEW" and <c>{new_episode}</c> "NEW EPISODE" when the promoted episode is a new one (a drop's
    /// first airing), and nothing otherwise, so one template can flag new episodes.
    /// </summary>
    public static string NewTokens(string text, IReadOnlyList<UpNextListing> listings)
    {
        if (!text.Contains("{new", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        bool fresh = listings.Count > 0 && listings[0].IsNew;
        return text
            .Replace("{new_episode}", fresh ? "NEW EPISODE" : string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{new}", fresh ? "NEW" : string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How a new episode is introduced when a promo names it aloud.</summary>
    internal const string SpokenNew = "an all-new";

    /// <param name="pose">Where it's placed: the layer itself, or the chosen background's placement of it.</param>
    /// <param name="colour">Its colour: the layer's, or the chosen background's for it.</param>
    /// <param name="timing">When it shows and how it comes and goes: the layer's, or the chosen background's for it.</param>
    /// <param name="video">For a video layer: what it plays (none: nothing is drawn).</param>
    private static PromoLayer? Layer(GeneratedLayer layer, IGeneratedPose pose, IGeneratedTiming timing, string colour, IReadOnlyList<string> lines,
        IReadOnlyDictionary<string, string> images, PromoVideoSource? video = null)
    {
        SKRect bounds = SKRect.Create(pose.X, pose.Y, pose.Width, pose.Height);
        SKColor color = SKColor.Parse(colour);
        string? imagePath = layer.VariableImage is { Length: > 0 } variable
            ? images.GetValueOrDefault(GeneratedImageSelectors.Reference(variable, layer.VariableImageShow))
            : layer.Path;
        PromoLayer? drawn = layer.Kind switch
        {
            GeneratedLayerKind.Rect => new PromoRectLayer
            {
                Bounds = bounds, Color = color, CornerRadius = layer.CornerRadius,
                Shape = layer.Shape ?? GeneratedShape.Rectangle,
                SoftEdges = float.IsFinite(layer.SoftEdges ?? 0) ? Math.Clamp(layer.SoftEdges ?? 0, 0, 200) : 0
            },
            // Always drawn (as nothing), so the layers inside it have it to be placed in.
            GeneratedLayerKind.Group => new PromoGroupLayer { Bounds = bounds },
            // A missing additive mask must remain an empty matte, not disappear and reveal the whole target.
            GeneratedLayerKind.Image when !string.IsNullOrWhiteSpace(imagePath) || layer.Mask is not null =>
                new PromoImageLayer { Bounds = bounds, Path = imagePath ?? string.Empty },
            GeneratedLayerKind.Video when video is not null => new PromoVideoLayer
            {
                Bounds = bounds,
                Path = video.Path,
                SourceStart = video.Start,
                StartsWithLayer = (layer.Video?.Playback ?? GeneratedVideoPlayback.WithLayer) == GeneratedVideoPlayback.WithLayer,
                Loop = layer.Video?.Loop == true,
                Fill = layer.Video?.Fill == true,
                Stretch = layer.Video?.Stretch == true,
                ChromaKey = layer.Video?.ChromaKey == true,
                KeyColor = layer.Video?.KeyColor ?? "#00FF00",
                KeyTolerance = layer.Video?.KeyTolerance ?? 0.2,
                KeySoftness = layer.Video?.KeySoftness ?? 0.1
            },
            var kind when (kind is GeneratedLayerKind.Text or GeneratedLayerKind.UpNext || GeneratedPromoKinds.IsShowLayer(kind))
                && lines.Any(line => line.Length > 0) => new PromoTextLayer
            {
                Bounds = bounds,
                // Cased here rather than when the lines are worked out, so AI-written text is cased too.
                Lines = layer.TextCase is null or GeneratedTextCase.AsWritten
                    ? lines
                    : lines.Select(line => GeneratedTextStyle.Cased(line, layer.TextCase)).ToList(),
                WordWrap = layer.WordWrap == true,
                LetterSpacing = SchedularrTV.Service.Rendering.TextFit.LetterSpacing(layer.LetterSpacing),
                LineSpacing = SchedularrTV.Service.Rendering.TextFit.LineSpacing(layer.LineSpacing),
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
                FontWeight = layer.FontWeight,
                Italic = layer.Italic,
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
            Id = layer.Id,
            Mask = layer.Mask is { } mask ? new PromoLayerMask(mask.Mode, mask.Target) : null,
            Start = timing.Start,
            End = timing.End,
            In = Enum.Parse<PromoTransition>(timing.TransitionIn.ToString()),
            Out = Enum.Parse<PromoTransition>(timing.TransitionOut.ToString()),
            TransitionDuration = timing.TransitionDuration,
            TransitionOutDuration = timing.TransitionOutDuration,
            Opacity = pose.Opacity,
            Rotation = Degrees(pose.Rotation),
            Corners = PromoCorners.From(pose.Corners),
            Keyframes = Keyframes(pose)
        };
    }

    /// <summary>A rotation fit to draw: within a turn, and 0 for nothing or nonsense.</summary>
    public static float Degrees(float? rotation) => rotation is { } value && float.IsFinite(value) ? value % 360 : 0;
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
            GeneratedPromoType.NewEpisode => ("New Episode", "ALL-NEW EPISODE", 10, new GeneratedShowOptions { Day = true, Time = true }),
            GeneratedPromoType.RightBack => ("We'll Be Right Back", "WE'LL BE RIGHT BACK", 5, new GeneratedShowOptions()),
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
                PostMessages = type is GeneratedPromoType.ComingUp or GeneratedPromoType.Eventually or GeneratedPromoType.NewEpisode ? ["Only on {channel}!"] : []
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
