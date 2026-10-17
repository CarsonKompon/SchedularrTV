using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Plugins.Writing;
using SchedularrTV.Service.Writing;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// A video layer curated by AI, as resolved for one airing: the episode its clips come from, and everything the writer
/// may say about it. The plan itself needs the episode's transcript, so it's made when the promo is.
/// </summary>
public sealed record CuratedPromoRequest(
    int LayerIndex,
    GeneratedCuratedClips Config,
    UpNextListing Show,
    string EpisodePath,
    string Channel,
    GeneratedPromoType Type,
    TimeZoneInfo Zone,
    DateTimeOffset AirsAt,
    TimeSpan Duration,
    TimeSpan VoiceStart,
    string Fallback,
    string Variety)
{
    /// <summary>The voice-over's opener picked for this promo (Said first), tokens unfilled; null says nothing extra.</summary>
    public string? Opener { get; init; }

    /// <summary>The voice-over's closer picked for this promo (Said last), tokens unfilled; null leaves the sign-off to the AI.</summary>
    public string? Closer { get; init; }

    /// <summary>The direction picked for this promo ("Mondays are all about mayhem"); null for none.</summary>
    public string? Direction { get; init; }
}

/// <summary>A moment of the episode the writer may use: what's said, and when.</summary>
public sealed record CuratedMoment(int Number, TimeSpan Start, TimeSpan End, string Text)
{
    public TimeSpan Length => End - Start;

    /// <summary>What's said just before it (within a few seconds), so the writer can tell what it's answering; null for nothing.</summary>
    public string? Before { get; init; }

    /// <summary>What's said just after it (within a few seconds); null for nothing.</summary>
    public string? After { get; init; }
}

/// <summary>
/// One beat of a curated promo, in order: the announcer speaking (<see cref="Voice"/>) over the usual background or over
/// a moment of the episode, a moment left to speak for itself (<see cref="Voice"/> null), or a pause
/// (<see cref="Pause"/>: neither).
/// </summary>
public sealed record CuratedBeat(string? Voice, int? Moment)
{
    public bool ClipSpeaks => Voice is null && Moment is not null;

    /// <summary>For a pause: how long the announcer stays quiet.</summary>
    public TimeSpan? Pause { get; init; }

    public bool IsPause => Voice is null && Moment is null && Pause is not null;

    /// <summary>
    /// For a clip: what the AI wrote after its number (<c>CLIP 9: I need a teacher's aid!</c>), which is what it thinks
    /// the clip says. Only used to catch a wrong number; never said or kept.
    /// </summary>
    public string? Quote { get; init; }

    /// <summary>A pause of <paramref name="length"/> (kept to between 0.3 and 2 seconds).</summary>
    public static CuratedBeat Silence(TimeSpan length) => new(null, null)
    {
        Pause = length < TimeSpan.FromSeconds(0.3) ? TimeSpan.FromSeconds(0.3) : length > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : length
    };
}

/// <summary>A clip the curated layer shows: from <see cref="SourceStart"/> in the episode, at <see cref="At"/> in the promo.</summary>
public sealed record CuratedClip(string Path, TimeSpan SourceStart, TimeSpan At, TimeSpan Length, bool WithSound, double Volume)
{
    /// <summary>What's said in it, when it's one of the moments (for troubleshooting); null otherwise.</summary>
    public string? Text { get; init; }
}

/// <summary>A line of the announcer's, placed in the promo, read at <see cref="Tempo"/>.</summary>
public sealed record CuratedLine(string Text, TimeSpan At, TimeSpan Length, double Tempo);

/// <summary>Where everything in a curated promo goes.</summary>
public sealed record CuratedTimeline(IReadOnlyList<CuratedLine> Lines, IReadOnlyList<CuratedClip> Clips);

/// <summary>
/// Promos whose clips are curated by AI: picking an episode's quotable moments from its transcript, asking the writer
/// for a plan (announcer lines, some over clips, and clips that speak for themselves), checking it, and laying it out
/// to fit the promo.
/// </summary>
public static partial class CuratedPromo
{
    /// <summary>The most moments the writer is shown, so the prompt stays small enough for a small model.</summary>
    public const int MomentLimit = 36;

    // Moments are cut a little before their words start and after they end, so a line isn't clipped.
    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(150);
    // Speech recognition tends to end a line a touch early, so a little more is kept after it than before.
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(450);

    // Between one beat and the next, and kept clear at the end of the promo.
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan EndRoom = TimeSpan.FromMilliseconds(300);

    private const double MaxTempo = 1.35;

    /// <summary>An announcer's pace, for estimating a line before it's spoken (previews).</summary>
    private const double WordsPerSecond = 2.6;

    /// <summary>The template's first curated video layer, if it has one and a voice to talk with.</summary>
    public static int? LayerIndex(GeneratedPromoTemplate template)
    {
        if (template.Voice is not { Enabled: true } || template.Generator is not null)
        {
            return null;
        }

        int index = template.Layers.FindIndex(layer => layer is { Kind: GeneratedLayerKind.Video, Video.Curated: not null });
        return index < 0 ? null : index;
    }

    /// <summary>
    /// The episode's moments the writer may use, numbered in time order: lines of speech of a usable length, away from
    /// the titles and credits, the most quotable of each stretch of the episode so they're spread through it.
    /// </summary>
    public static IReadOnlyList<CuratedMoment> Moments(Transcript transcript, GeneratedCuratedClips config, TimeSpan? runtime)
    {
        TimeSpan shortest = Clamp(config.MinClip, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10));
        TimeSpan longest = Clamp(config.MaxClip, shortest, TimeSpan.FromSeconds(15));
        TimeSpan end = runtime ?? (transcript.Segments.Count > 0 ? transcript.Segments.Max(segment => segment.End) : TimeSpan.Zero);
        // Intros and credits rarely make good moments (and their music confuses speech recognition): only the stretch of
        // the episode the template allows is used.
        (double fromFraction, double untilFraction) = Range(config);
        TimeSpan from = end * fromFraction;
        TimeSpan until = end * untilFraction;

        List<(TimeSpan Start, TimeSpan End, string Text)> lines = [];
        foreach (TranscriptSegment segment in transcript.Segments.OrderBy(segment => segment.Start))
        {
            string text = Speech(segment.Text);
            if (text.Length == 0 || segment.End <= segment.Start || segment.Start < from || segment.End > until)
            {
                continue;
            }

            // A line runs on into the next one when they follow closely and it's too short to be a moment, or when it
            // stops mid-sentence (speech recognition splits long sentences), so a clip never cuts someone off.
            if (lines.Count > 0 && lines[^1] is var last && segment.End - last.Start <= longest
                && ((last.End - last.Start < shortest && segment.Start - last.End < TimeSpan.FromMilliseconds(500))
                    || (!Finished(last.Text) && segment.Start - last.End < RunsOn)))
            {
                lines[^1] = (last.Start, segment.End, $"{last.Text} {text}");
                continue;
            }

            lines.Add((segment.Start, segment.End, text));
        }

        // A line that stops mid-sentence and carries straight on (but couldn't be joined up: too long together) would be
        // cut off, so it isn't a moment.
        bool CutOff(int index) => !Finished(lines[index].Text) && index + 1 < lines.Count && lines[index + 1].Start - lines[index].End < RunsOn;
        List<(TimeSpan Start, TimeSpan End, string Text)> usable = [.. lines.Where((line, index) =>
            line.End - line.Start >= shortest && line.End - line.Start <= longest && Words(line.Text) >= 2 && !CutOff(index))];
        if (usable.Count == 0)
        {
            return [];
        }

        // The best of each stretch, so the moments come from all through the episode rather than one busy scene.
        TimeSpan first = usable[0].Start;
        double span = Math.Max(1, (usable[^1].Start - first).TotalSeconds);
        List<(TimeSpan Start, TimeSpan End, string Text)> picked = [.. usable
            .GroupBy(line => Math.Min(MomentLimit - 1, (int)((line.Start - first).TotalSeconds / span * MomentLimit)))
            .Select(group => group.OrderByDescending(line => Score(line.Text, line.End - line.Start)).ThenBy(line => line.Start).First())
            .OrderBy(line => line.Start)];

        // With what's said either side, so the writer can tell what a line answers or sets up rather than guessing.
        return [.. picked.Select((line, index) =>
        {
            int at = lines.FindIndex(candidate => candidate.Start == line.Start);
            string? before = at > 0 && line.Start - lines[at - 1].End <= ContextReach ? Short(lines[at - 1].Text) : null;
            string? after = at >= 0 && at + 1 < lines.Count && lines[at + 1].Start - line.End <= ContextReach ? Short(lines[at + 1].Text) : null;
            return new CuratedMoment(index + 1, line.Start, line.End, line.Text) { Before = before, After = after };
        })];
    }

    // Lines further apart than this are probably another conversation (or another scene).
    private static readonly TimeSpan ContextReach = TimeSpan.FromSeconds(4);

    // A sentence left unfinished and picked up again within this is one sentence, split by speech recognition.
    private static readonly TimeSpan RunsOn = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// Whether a line ends a sentence: a full stop, question or exclamation mark (perhaps then a quote). One trailing off
    /// ("I need a...") or broken off ("I need a-") doesn't: speech recognition writes those where a sentence carries on.
    /// </summary>
    public static bool Finished(string text) => FinishedPattern().IsMatch(text.TrimEnd());

    [GeneratedRegex(@"(?<![.…])[.!?]['""”’)]*$")]
    private static partial Regex FinishedPattern();

    private static string Short(string text) => text.Length <= 120 ? text : text[..117].TrimEnd() + "…";

    /// <summary>
    /// The stretch of the episode clips may come from, as fractions of its length: <see cref="GeneratedCuratedClips.FromPercent"/>
    /// to <see cref="GeneratedCuratedClips.UntilPercent"/>, kept within 0–100%. One that's empty or backwards allows the whole episode.
    /// </summary>
    public static (double From, double Until) Range(GeneratedCuratedClips config)
    {
        double from = double.IsFinite(config.FromPercent) ? Math.Clamp(config.FromPercent, 0, 100) / 100 : 0;
        double until = double.IsFinite(config.UntilPercent) ? Math.Clamp(config.UntilPercent, 0, 100) / 100 : 1;
        return until > from ? (from, until) : (0, 1);
    }

    /// <summary>What's said, without speech recognition's notes: <c>[Music]</c>, <c>(laughs)</c>, ♪ and the like.</summary>
    public static string Speech(string text)
    {
        string spoken = NotesPattern().Replace(text, " ");
        spoken = string.Join(' ', spoken.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '>', '"');
        return spoken.Any(char.IsLetter) ? spoken : string.Empty;
    }

    // Lines people quote: exclamations, questions, a few words said with feeling.
    private static double Score(string text, TimeSpan length)
    {
        int words = Words(text);
        double score = 0;
        score += text.Count(character => character == '!') * 2;
        score += text.Contains('?') ? 1.5 : 0;
        score += words is >= 3 and <= 14 ? 2 : words <= 20 ? 0.5 : -1;
        score += text.Split(' ').Count(word => word.Length > 2 && word.Any(char.IsLetter) && word.Where(char.IsLetter).All(char.IsUpper)) * 0.5;
        score += length.TotalSeconds is >= 1.5 and <= 4 ? 1 : 0;
        score -= text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith('…') ? 0.3 : 0;
        return score;
    }

    private static int Words(string text) => text.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// The first of two steps (<see cref="GeneratedCuratedClips.PickClipsFirst"/>): only picking which moments to use, and
    /// in what order, for the promo's direction. What's kept is their numbers, e.g. <c>12, 4</c>.
    /// </summary>
    public static AiTextJob PickJob(CuratedPromoRequest request, IReadOnlyList<CuratedMoment> moments)
    {
        AiTextJob plan = Job(request, moments);
        int clips = Math.Clamp(request.Config.Clips, 1, 8);
        return plan with
        {
            Purpose = $"the choice of moments for a promo for {UpNextPromo.SpokenTitle(request.Show.Title)}",
            MustSay = [],
            Guidance = [.. plan.Guidance.Where(line => !line.StartsWith("Use at most", StringComparison.Ordinal))],
            Seed = $"{plan.Seed}|pick",
            Format = new PickFormat(clips, request.Config.LetClipsSpeak)
        };
    }

    /// <summary>The moments a pick names, in its order; empty when it names none of them.</summary>
    public static IReadOnlyList<CuratedMoment> Picked(string pick, IReadOnlyList<CuratedMoment> moments) =>
        [.. NumberPattern().Matches(pick)
            .Select(match => int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1)
            .Distinct()
            .Select(number => moments.FirstOrDefault(moment => moment.Number == number))
            .OfType<CuratedMoment>()];

    /// <summary>
    /// The second step: writing the voice-over around the moments already picked, which are all it's shown, and in the
    /// order it's to use them.
    /// </summary>
    public static AiTextJob WriteJob(CuratedPromoRequest request, IReadOnlyList<CuratedMoment> picked)
    {
        AiTextJob plan = Job(request, picked);
        string order = string.Join(", ", picked.Select(moment => moment.Number.ToString(CultureInfo.InvariantCulture)));
        return plan with
        {
            // Whether the AI writes this promo at all (its chance setting) was decided at the first step.
            Config = new AiTextConfig
            {
                Generator = plan.Config.Generator,
                Style = plan.Config.Style,
                Prompt = plan.Config.Prompt,
                UseDescriptions = plan.Config.UseDescriptions,
                MaxWords = plan.Config.MaxWords,
                Chance = 1
            },
            Guidance =
            [
                .. plan.Guidance.Where(line => !line.StartsWith("Use at most", StringComparison.Ordinal)),
                picked.Count == 1
                    ? $"Build the promo around moment {order}: use it once."
                    : $"These moments were picked for this promo: use each of them once, in this order: {order}."
            ],
            Seed = $"{plan.Seed}|write"
        };
    }

    [GeneratedRegex(@"\d{1,3}")]
    private static partial Regex NumberPattern();

    /// <summary>The writer's job: the plan for this promo, with the episode's moments to pick from.</summary>
    public static AiTextJob Job(CuratedPromoRequest request, IReadOnlyList<CuratedMoment> moments)
    {
        GeneratedCuratedClips config = request.Config;
        string title = UpNextPromo.SpokenTitle(request.Show.Title);
        // The channel, the show (its episode, and when it's on), and what the episode's about if the writer may know.
        List<AiTextFact> facts = [.. GeneratedPromoWriting.Facts(request.Type, request.Channel, [request.Show with { Title = title }], request.Zone,
            request.AirsAt, config.Writer.UseDescriptions)];
        foreach (CuratedMoment moment in moments)
        {
            facts.Add(new AiTextFact(MomentLabel(moment.Number), $"({Seconds(moment.Length)}s) {moment.Text}"));
            if (Context(moment) is { } context)
            {
                facts.Add(new AiTextFact(ContextLabel(moment.Number), context));
            }
        }

        List<string> guidance =
        [
            config.About == GeneratedCurationFocus.Show
                ? $"Talk about {title} in general; the moments are examples of what the show is like."
                : $"Talk about this episode of {title}; the moments are from it.",
            $"Use at most {Math.Clamp(config.Clips, 1, 8)} moments."
        ];
        if (request.Show.IsNew)
        {
            guidance.Add("This is a brand-new episode, airing for the first time.");
        }

        // Lines written in the template are said as they are; the show only has to be named by the AI when they don't.
        string? opener = Fixed(request.Opener, request);
        string? closer = Fixed(request.Closer, request);
        bool named = new[] { opener, closer }.Any(line => line is not null && AiTextRules.Mentions(line, title));

        return new AiTextJob
        {
            // The writer's settings, with this promo's direction as what it's asked to do.
            Config = new AiTextConfig
            {
                Generator = config.Writer.Generator,
                Style = config.Writer.Style,
                Prompt = request.Direction,
                UseDescriptions = config.Writer.UseDescriptions,
                MaxWords = config.Writer.MaxWords,
                Chance = config.Writer.Chance
            },
            Use = AiTextUse.Spoken,
            Channel = request.Channel,
            Purpose = $"a promo for {title}: {GeneratedPromoWriting.Kind(request.Type)}, with moments from the episode",
            Fallback = request.Fallback,
            Facts = facts,
            Guidance = guidance,
            MustSay = named ? [] : [title],
            // The template's own lines take their share of the time.
            MaxWords = Math.Max(5, AiTextRules.SpokenWords(request.Duration - request.VoiceStart - TimeSpan.FromSeconds(1.5))
                - Words(opener ?? string.Empty) - Words(closer ?? string.Empty)),
            Seed = $"{request.Variety}|curated|{request.LayerIndex}",
            Source = GeneratedPromoWriting.Source,
            Due = request.AirsAt,
            Format = new CurationFormat(config.LetClipsSpeak, Math.Clamp(config.Clips, 1, 8), opener, closer)
        };
    }

    /// <summary>
    /// The direction for one promo: one of <see cref="GeneratedCuratedClips.Directions"/>, fixed by <paramref name="seed"/>
    /// so a re-render asks the same; else the writer's single prompt; null for none.
    /// </summary>
    public static string? Direction(GeneratedCuratedClips config, string seed)
    {
        List<string> directions = [.. config.Directions.Where(direction => !string.IsNullOrWhiteSpace(direction)).Select(direction => direction.Trim())];
        return directions.Count > 0 ? directions[UpNextPromo.Pick(seed, directions.Count)]
            : string.IsNullOrWhiteSpace(config.Writer.Prompt) ? null
            : config.Writer.Prompt.Trim();
    }

    /// <summary>
    /// A line written in the template with its tokens filled in for this airing: <c>{show}</c> (or <c>{show_name}</c>),
    /// <c>{episode}</c>, <c>{time}</c> (or <c>{show_time}</c>, <c>{start_time}</c>: "7", "7:30"), <c>{when}</c> ("tonight
    /// at 7"), <c>{day}</c> ("tonight"), <c>{channel}</c>. Null when there's nothing to say.
    /// </summary>
    public static string? Fixed(string? text, CuratedPromoRequest request)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string filled = GeneratedPromoResolver.SpokenTokens(text, request.Show, request.Zone, request.AirsAt, request.Channel) ?? string.Empty;
        filled = string.Join(' ', filled.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return filled.Length == 0 ? null : char.ToUpperInvariant(filled[0]) + filled[1..];
    }

    /// <summary>The AI's beats with the template's opener before them and closer after them, each over the usual background.</summary>
    public static IReadOnlyList<CuratedBeat> WithFixedLines(IReadOnlyList<CuratedBeat> beats, CuratedPromoRequest request) =>
    [
        .. Fixed(request.Opener, request) is { } opener ? new[] { new CuratedBeat(opener, null) } : Array.Empty<CuratedBeat>(),
        .. beats,
        .. Fixed(request.Closer, request) is { } closer ? new[] { new CuratedBeat(closer, null) } : Array.Empty<CuratedBeat>()
    ];

    internal static string MomentLabel(int number) => $"Moment {number.ToString(CultureInfo.InvariantCulture)}";

    internal static string ContextLabel(int number) => $"Around moment {number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What's said either side of a moment, as the writer's shown it; null when nothing is close by.</summary>
    public static string? Context(CuratedMoment moment) =>
        (moment.Before, moment.After) switch
        {
            (null, null) => null,
            ({ } before, null) => $"just before, someone says \"{before}\"",
            (null, { } after) => $"just after, someone says \"{after}\"",
            ({ } before, { } after) => $"just before, someone says \"{before}\"; just after, \"{after}\""
        };

    /// <summary>The moments and what's around them, as both steps show them to the writer: one moment a line, its context indented under it.</summary>
    private static void AppendMoments(StringBuilder prompt, IEnumerable<AiTextFact> facts)
    {
        prompt.AppendLine("Moments from the episode (number, length, what's said). Nobody knows who says any of them:");
        List<AiTextFact> all = [.. facts];
        foreach (AiTextFact fact in all.Where(fact => fact.Label.StartsWith("Moment ", StringComparison.Ordinal)))
        {
            string number = fact.Label["Moment ".Length..];
            prompt.AppendLine($"{number} {OneLine(fact.Value)}");
            if (all.FirstOrDefault(around => around.Label == $"Around moment {number}") is { } context)
            {
                prompt.AppendLine($"    ({OneLine(context.Value)})");
            }
        }
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Names in the announcer's words that the writer wasn't given, such as a character it guessed was speaking: a
    /// capitalised word inside a sentence that isn't in anything it was told. Null when there are none.
    /// </summary>
    public static string? UnknownName(string spoken, AiTextJob job, params string?[] alsoKnown)
    {
        List<string?> sources =
        [
            .. job.Facts.Select(fact => fact.Value), job.Fallback, job.Channel, job.Purpose, job.Config.Prompt, job.Config.Style,
            .. job.Guidance, .. job.MustSay, .. alsoKnown
        ];
        string known = string.Join(' ', sources.OfType<string>().Select(text => PossessivePattern().Replace(text, string.Empty)));
        HashSet<string> knownWords = [.. AiTextRules.Normalize(known).Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        foreach (Match word in WordPattern().Matches(spoken))
        {
            string value = PossessivePattern().Replace(word.Value, string.Empty);
            if (!char.IsUpper(value[0]) || CommonCapitals.Contains(value) || SentenceStart(spoken, word.Index))
            {
                continue;
            }

            string normalized = AiTextRules.Normalize(value);
            if (normalized.Length > 0 && normalized.Split(' ').Any(part => !knownWords.Contains(part)))
            {
                return value;
            }
        }

        return null;
    }

    // Capitalised words that aren't names (days are checked on their own).
    private static readonly HashSet<string> CommonCapitals = new(StringComparer.Ordinal)
    {
        "I", "I'm", "I’m", "I'll", "I’ll", "I've", "I’ve", "I'd", "I’d", "OK", "TV",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "Mondays", "Tuesdays", "Wednesdays",
        "Thursdays", "Fridays", "Saturdays", "Sundays"
    };

    // A word starting a sentence (or the text, or a quote) is capitalised anyway.
    private static bool SentenceStart(string text, int index)
    {
        for (int at = index - 1; at >= 0; at--)
        {
            char previous = text[at];
            if (char.IsWhiteSpace(previous))
            {
                continue;
            }

            return previous is '.' or '!' or '?' or '…' or ':' or '"' or '“' or '—' or '–';
        }

        return true;
    }

    [GeneratedRegex(@"\p{L}[\p{L}\p{Nd}'’\-]*")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"['’]s\b", RegexOptions.IgnoreCase)]
    private static partial Regex PossessivePattern();

    /// <summary>
    /// The plan's beats from its kept text (one per line: <c>VO: …</c>, <c>VO 12: …</c>, <c>CLIP 7</c>); the moments
    /// they name must be among <paramref name="moments"/>.
    /// </summary>
    public static IReadOnlyList<CuratedBeat> Beats(string plan)
    {
        List<CuratedBeat> beats = [];
        foreach (CuratedBeat beat in plan.Replace("\r", string.Empty).Split('\n').SelectMany(ParseLine))
        {
            // No pause to open or close on, and never two in a row.
            if (beat.IsPause && (beats.Count == 0 || beats[^1].IsPause))
            {
                continue;
            }

            beats.Add(beat);
        }

        while (beats.Count > 0 && beats[^1].IsPause)
        {
            beats.RemoveAt(beats.Count - 1);
        }

        return beats;
    }

    /// <summary>A beat as the plan's kept text writes it.</summary>
    public static string Line(CuratedBeat beat) =>
        beat.IsPause ? $"PAUSE {beat.Pause!.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}"
        : beat.Voice is null ? $"CLIP {beat.Moment}"
        : beat.Moment is { } moment ? $"VO {moment.ToString(CultureInfo.InvariantCulture)}: {beat.Voice}"
        : $"VO: {beat.Voice}";

    /// <summary>What the announcer says in all, for checking it and showing it.</summary>
    public static string Spoken(IReadOnlyList<CuratedBeat> beats) =>
        string.Join(' ', beats.Where(beat => beat.Voice is not null).Select(beat => beat.Voice));

    /// <summary>The plan for showing in the editor: the lines, with what each clip says in quotes.</summary>
    public static string Describe(IReadOnlyList<CuratedBeat> beats, IReadOnlyList<CuratedMoment> moments)
    {
        var text = new StringBuilder();
        foreach (CuratedBeat beat in beats)
        {
            if (beat.IsPause)
            {
                text.AppendLine("[pause]");
                continue;
            }

            CuratedMoment? moment = beat.Moment is { } number ? moments.FirstOrDefault(candidate => candidate.Number == number) : null;
            string clip = moment is null ? string.Empty : $"[clip at {Clock(moment.Start)}: “{moment.Text}”]";
            text.AppendLine(beat.Voice is null ? clip : moment is null ? beat.Voice : $"{beat.Voice}  {clip}");
        }

        return text.ToString().Trim();
    }

    /// <summary>A moment as the AI is shown it, with where it is in the episode: <c>12 at 4:31 (2.1s): I didn't know that!</c>.</summary>
    public static string DescribeMoment(CuratedMoment moment) =>
        $"{moment.Number.ToString(CultureInfo.InvariantCulture)} at {Clock(moment.Start)} ({Seconds(moment.Length)}s): {moment.Text}"
        + (Context(moment) is { } context ? $"\n    ({context})" : string.Empty);

    /// <summary>
    /// The laid-out promo in time order, for troubleshooting: each line said and each clip shown, when, for how long, and
    /// where in the episode (silences are the gaps between).
    /// </summary>
    public static string DescribeTimeline(CuratedTimeline timeline)
    {
        IEnumerable<(TimeSpan At, int Order, string Text)> lines = timeline.Lines.Select(line =>
            (line.At, 0, $"{Stamp(line.At)}  VO   ({Seconds(line.Length)}s{(Math.Abs(line.Tempo - 1) > 0.001 ? $", at {line.Tempo.ToString("0.00", CultureInfo.InvariantCulture)}x" : string.Empty)})  {line.Text}"));
        IEnumerable<(TimeSpan At, int Order, string Text)> clips = timeline.Clips.Select(clip =>
            (clip.At, 1, $"{Stamp(clip.At)}  CLIP ({Seconds(clip.Length)}s)  from {Clock(clip.SourceStart)} in the episode, "
                + (clip.WithSound ? $"sound at {clip.Volume.ToString("P0", CultureInfo.InvariantCulture)}" : "silent")
                + (clip.Text is { } said ? $": “{said}”" : string.Empty)));
        return string.Join('\n', lines.Concat(clips).OrderBy(item => item.At).ThenBy(item => item.Order).Select(item => item.Text));
    }

    private static string Stamp(TimeSpan time) => time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

    private static string Clock(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static IEnumerable<CuratedBeat> ParseLine(string raw)
    {
        string line = raw.Trim().Trim('*', '_', '`').Trim();
        if (line.Length == 0)
        {
            return [];
        }

        if (PausePattern().Match(line) is { Success: true } pause)
        {
            return [CuratedBeat.Silence(double.TryParse(pause.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(1))];
        }

        if (ClipPattern().Match(line) is { Success: true } clip && int.TryParse(clip.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int bite))
        {
            string quote = Unquote(clip.Groups["text"].Value.Trim().Trim('(', ')', '[', ']'));
            return [new CuratedBeat(null, bite) { Quote = quote.Any(char.IsLetter) ? quote : null }];
        }

        if (VoicePattern().Match(line) is { Success: true } voice)
        {
            int? over = int.TryParse(voice.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
            return SaidBeats(voice.Groups["text"].Value, over);
        }

        return [];
    }

    /// <summary>
    /// An announcer line as beats: only the words to be said. Stage directions ("(pauses)", "*laughs*", "The announcer
    /// lets the silence hang.") aren't read out: one about pausing becomes a real pause, any other is dropped. The clip
    /// goes with the first words.
    /// </summary>
    private static List<CuratedBeat> SaidBeats(string text, int? moment)
    {
        List<CuratedBeat> beats = [];
        var words = new StringBuilder();
        void Say()
        {
            string said = Unquote(words.ToString().Trim());
            words.Clear();
            if (said.Length > 0 && said.Any(char.IsLetterOrDigit))
            {
                beats.Add(new CuratedBeat(said, beats.Any(beat => beat.Voice is not null) ? null : moment));
            }
        }

        void Direct(string direction)
        {
            if (PauseWordPattern().IsMatch(direction))
            {
                Say();
                beats.Add(CuratedBeat.Silence(TimeSpan.FromSeconds(1)));
            }
        }

        int at = 0;
        foreach (Match note in NotesPattern().Matches(text))
        {
            foreach (string sentence in Sentences(text[at..note.Index]))
            {
                if (StageDirectionPattern().IsMatch(sentence)) Direct(sentence);
                else words.Append(sentence).Append(' ');
            }

            Direct(note.Value);
            at = note.Index + note.Length;
        }

        foreach (string sentence in Sentences(text[at..]))
        {
            if (StageDirectionPattern().IsMatch(sentence)) Direct(sentence);
            else words.Append(sentence).Append(' ');
        }

        Say();
        return beats;
    }

    private static IEnumerable<string> Sentences(string text) =>
        SentencePattern().Split(text).Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 0);

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();
        while (trimmed.Length >= 2 && "\"“”'".Contains(trimmed[0]) && "\"“”'".Contains(trimmed[^1]))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    /// <summary>
    /// Places the plan in the promo: each line where the one before it ends (a clip under a line lasting as long as the
    /// line), each clip that speaks for itself as long as its moment. When it's too long the lines are read faster (up
    /// to 1.35×), then clips are left out from the middle, so the opening and the sign-off always stay.
    /// </summary>
    /// <param name="spoken">How long each line takes to say, at normal speed, in the order of the beats with words.</param>
    public static CuratedTimeline Layout(IReadOnlyList<CuratedBeat> beats, IReadOnlyList<TimeSpan> spoken, IReadOnlyList<CuratedMoment> moments,
        CuratedPromoRequest request)
    {
        GeneratedCuratedClips config = request.Config;
        List<(CuratedBeat Beat, TimeSpan Said)> placed = [];
        int line = 0;
        foreach (CuratedBeat beat in beats)
        {
            placed.Add((beat, beat.Voice is null ? TimeSpan.Zero : spoken.ElementAtOrDefault(line++)));
        }

        TimeSpan room = request.Duration - EndRoom - request.VoiceStart;
        double tempo = 1;
        TimeSpan Length(double atTempo) => placed.Aggregate(TimeSpan.Zero, (total, item) => total + Gap + (item.Beat.IsPause
            ? item.Beat.Pause!.Value
            : item.Beat.Voice is null
            ? BiteLength(item.Beat, moments, config)
            : item.Said / atTempo)) - Gap;

        if (room > TimeSpan.Zero && Length(1) > room)
        {
            tempo = Math.Min(MaxTempo, Length(1) / room);
        }

        // Still too long: clips go, from the middle out, keeping the first beat and the last.
        while (room > TimeSpan.Zero && Length(tempo) > room && placed.Count > 2)
        {
            int middle = Enumerable.Range(1, placed.Count - 2)
                .OrderByDescending(index => placed[index].Beat.IsPause)
                .ThenByDescending(index => placed[index].Beat.ClipSpeaks)
                .ThenBy(index => Math.Abs(index - placed.Count / 2.0))
                .First();
            placed.RemoveAt(middle);
        }

        // Lines the plan leaves without a clip get one, if asked: moments it didn't use (in an order fixed by the promo),
        // then other stretches of the episode.
        Queue<CuratedMoment> spare = new(moments
            .Where(moment => !placed.Any(item => item.Beat.Moment == moment.Number))
            .OrderBy(moment => AiTextRules.Fraction($"{request.Variety}|fill|{moment.Number}")));
        int filled = 0;
        TimeSpan? Filler()
        {
            if (spare.TryDequeue(out CuratedMoment? unused))
            {
                return Earlier(unused.Start);
            }

            TimeSpan runtime = request.Show.Runtime ?? (moments.Count > 0 ? moments[^1].End : TimeSpan.Zero);
            if (runtime <= TimeSpan.FromMinutes(1))
            {
                return moments.Count > 0 ? Earlier(moments[filled++ % moments.Count].Start) : null;
            }

            // Somewhere in the stretch clips may come from, kept a little clear of its edges.
            (double first, double last) = Range(config);
            double margin = (last - first) * 0.05;
            double fraction = first + margin + (last - first - 2 * margin) * AiTextRules.Fraction($"{request.Variety}|fill-stretch|{filled++}");
            return TimeSpan.FromSeconds(Math.Round(runtime.TotalSeconds * fraction, 1));
        }

        List<CuratedLine> lines = [];
        List<CuratedClip> clips = [];
        TimeSpan at = request.VoiceStart;
        foreach ((CuratedBeat beat, TimeSpan said) in placed)
        {
            if (beat.IsPause)
            {
                TimeSpan hold = beat.Pause!.Value + Gap;
                // The picture carries on through the pause: the clip just before it runs on (silently, so a speaking clip
                // doesn't start its next line), or with a clip under every line, another stretch of the episode.
                if (clips.Count > 0 && clips[^1] is var previous && (previous.At + previous.Length - at).Duration() < TimeSpan.FromMilliseconds(5))
                {
                    clips.Add(new CuratedClip(previous.Path, previous.SourceStart + previous.Length, at, hold, false, 0));
                }
                else if (config.ClipUnderEveryLine && Filler() is { } stretch)
                {
                    clips.Add(new CuratedClip(request.EpisodePath, stretch, at, hold, false, 0));
                }

                at += hold;
                continue;
            }

            CuratedMoment? moment = beat.Moment is { } number ? moments.FirstOrDefault(candidate => candidate.Number == number) : null;
            if (beat.Voice is null)
            {
                if (moment is null)
                {
                    continue;
                }

                // Shown through the gap after it too, so the picture doesn't drop out between beats.
                TimeSpan length = BiteLength(beat, moments, config);
                clips.Add(new CuratedClip(request.EpisodePath, Earlier(moment.Start), at, length + Gap, config.ClipVolume > 0, Math.Clamp(config.ClipVolume, 0, 1))
                {
                    Text = moment.Text
                });
                at += length + Gap;
                continue;
            }

            TimeSpan reading = said / tempo;
            lines.Add(new CuratedLine(beat.Voice, at, reading, tempo));
            if (moment is not null)
            {
                // The clip starts with the line and runs on (the episode carries on past the moment) until the next beat.
                clips.Add(new CuratedClip(request.EpisodePath, Earlier(moment.Start), at, reading + Gap, config.UnderVoiceVolume > 0,
                    Math.Clamp(config.UnderVoiceVolume, 0, 1)) { Text = moment.Text });
            }
            else if (config.ClipUnderEveryLine && Filler() is { } source)
            {
                clips.Add(new CuratedClip(request.EpisodePath, source, at, reading + Gap, config.UnderVoiceVolume > 0,
                    Math.Clamp(config.UnderVoiceVolume, 0, 1)));
            }

            at += reading + Gap;
        }

        // Nothing may run past the promo's end.
        TimeSpan end = request.Duration;
        clips = [.. clips.Where(clip => clip.At < end).Select(clip => clip.At + clip.Length > end ? clip with { Length = end - clip.At } : clip)];
        return new CuratedTimeline(lines, clips);
    }

    /// <summary>How long a line takes an announcer to say, before it's spoken (for previews).</summary>
    public static TimeSpan Estimate(string text) => TimeSpan.FromSeconds(Math.Max(0.6, Words(text) / WordsPerSecond));

    private static TimeSpan BiteLength(CuratedBeat beat, IReadOnlyList<CuratedMoment> moments, GeneratedCuratedClips config)
    {
        CuratedMoment? moment = moments.FirstOrDefault(candidate => candidate.Number == beat.Moment);
        if (moment is null)
        {
            return TimeSpan.Zero;
        }

        TimeSpan longest = Clamp(config.MaxClip, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)) + Tail + LeadIn;
        TimeSpan length = moment.Length + LeadIn + Tail;
        return length > longest ? longest : length;
    }

    private static TimeSpan Earlier(TimeSpan start) => start > LeadIn ? start - LeadIn : TimeSpan.Zero;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) => value < low ? low : value > high ? high : value;

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫♬]")]
    private static partial Regex NotesPattern();

    // "PAUSE", "PAUSE 1.5", "(pause)", "[Beat]", "Silence.", "A dramatic pause"
    [GeneratedRegex(@"^(?:[-•>\d.)\s]*)?[\[(]?\s*(?:a\s+)?(?:long\s+|short\s+|brief\s+|dramatic\s+)?(?:pause|beat|silence)\b\s*(?<s>\d+(?:\.\d+)?)?\s*s?(?:econds?)?\s*[\])]?\s*[.!…]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PausePattern();

    // A sentence about what the announcer does rather than says: "The announcer pauses to let the silence hang."
    [GeneratedRegex(@"^(?:then\s+|and\s+)?(?:the\s+)?(?:announcer|narrator|voice[- ]?over|host|vo)\b\s+(?:\w+\s+){0,2}(?:paus|wait|let|linger|hang|tak|lean|turn|whisper|shout|yell|laugh|chuckl|sigh|grin|smil|continu|says|adds|repl|clear|gasp|breath|stops|trail)|^(?:a\s+)?(?:long\s+|short\s+|brief\s+|dramatic\s+)?(?:pause|beat|silence)\b[^a-z]*$", RegexOptions.IgnoreCase)]
    private static partial Regex StageDirectionPattern();

    [GeneratedRegex(@"paus|silen|\bbeat\b|wait|hang|linger|quiet", RegexOptions.IgnoreCase)]
    private static partial Regex PauseWordPattern();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentencePattern();

    // "CLIP 7", "Clip #7: (it plays)", "- MOMENT 7", "[CLIP 7]"
    [GeneratedRegex(@"^(?:[-•>\d.)\s]*)?\[?\s*(?:clip|moment|bite|sound ?bite)\s*#?\s*(?<n>\d{1,3})\b\]?\s*(?:[:\-–—]\s*)?(?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClipPattern();

    // "VO: …", "VO 12: …", "VO over 12: …", "VO (moment 12): …", "Announcer: …"
    [GeneratedRegex(@"^(?:[-•>\d.)\s]*)?(?:voice[- ]?over|voice|v\.o\.|vo|announcer|narrator)\s*(?:[+(\[]?\s*(?:over|with|during)?\s*(?:clip|moment)?\s*#?\s*(?<n>\d{1,3})\s*[)\]]?)?\s*[:\-–—]\s*(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VoicePattern();

    /// <summary>
    /// How a curated plan is asked for and checked: one beat per line, moments named by number, the announcer's words
    /// held to the usual rules (every show named, nothing made up, not too long).
    /// </summary>
    /// <param name="opener">Said before the AI's lines, as the template writes it; null for none.</param>
    /// <param name="closer">Said after them (the sign-off), as the template writes it; null leaves the sign-off to the AI.</param>
    private sealed class CurationFormat(bool clipsSpeak, int clips, string? opener, string? closer) : AiTextFormat
    {
        public override string Id => $"curated-v5|{(clipsSpeak ? "speak" : "under")}|{clips}|{opener}|{closer}";

        public override TextGenerationRequest Request(AiTextJob job, int attempt)
        {
            int limit = AiTextRules.Limit(job);
            string channel = string.IsNullOrWhiteSpace(job.Channel) ? "a TV channel" : $"the TV channel \"{job.Channel.Trim()}\"";

            var instructions = new StringBuilder();
            instructions.AppendLine($"You write promos for {channel}: an announcer talks while moments from the episode play.");
            instructions.AppendLine("Reply with only the promo, one beat per line, each line in one of these forms:");
            instructions.AppendLine("VO: words the announcer says over the channel's usual footage");
            instructions.AppendLine("VO 12: words the announcer says while moment 12 plays quietly underneath");
            if (clipsSpeak)
            {
                instructions.AppendLine("CLIP 7  (moment 7 plays with its own sound while the announcer waits; the announcer can reply to it)");
            }

            instructions.AppendLine("PAUSE  (the announcer stays quiet for a second, for effect)");

            instructions.AppendLine("Rules:");
            instructions.AppendLine("- Use only moment numbers from the list, each at most once. Pick moments that are funny, dramatic or exciting.");
            instructions.AppendLine($"- Use at most {clips} moments in all.");
            instructions.AppendLine(clipsSpeak
                ? "- The announcer never says or quotes what's said in a moment. To use a moment's words, write CLIP and its number, and the moment plays itself."
                : "- The announcer never says or quotes what's said in a moment.");
            instructions.AppendLine("- Use only the facts you're given. Never make up shows, episodes, days, times or plot details.");
            instructions.AppendLine("- Nobody knows who says any moment, or who they're talking to. Never name, describe or guess the speaker (not \"she says\", not \"the one with the hat\"): talk about what's said, not who says it.");
            instructions.AppendLine("- Never use a name you weren't given. Read what's said around a moment to understand it, and only connect lines that really fit together.");
            instructions.AppendLine("- Write show names exactly as they're given.");
            instructions.AppendLine($"- The announcer says at most {limit} words in all.");
            instructions.AppendLine("- Write the announcer's words to be spoken: plain sentences, no abbreviations, symbols, emoji or stage directions.");
            instructions.AppendLine("- VO lines hold only the words said aloud. Never describe what the announcer does (not \"the announcer pauses\"): to pause, write PAUSE on its own line.");
            if (closer is null)
            {
                instructions.AppendLine("- Finish with a VO line that names the show and the channel.");
            }
            else
            {
                instructions.AppendLine("- Don't sign off or say when it's on: the announcer's last line is already written and is said right after yours.");
            }

            if (opener is not null)
            {
                instructions.AppendLine("- The announcer's first line is already written and is said right before yours: carry on from it, don't repeat it.");
            }

            if (job.Config.Style is { Length: > 0 } style && style.Trim().Length > 0)
            {
                instructions.AppendLine($"Style: {style.Trim()}");
            }

            var prompt = new StringBuilder();
            prompt.AppendLine($"Write {job.Purpose.Trim().TrimEnd('.')}.");
            if (job.Config.Prompt is { Length: > 0 } wanted && wanted.Trim().Length > 0)
            {
                prompt.AppendLine(wanted.Trim());
            }

            prompt.AppendLine();
            prompt.AppendLine("Facts:");
            foreach (AiTextFact fact in job.Facts.Where(fact => !IsMoment(fact) && !IsContext(fact) && !string.IsNullOrWhiteSpace(fact.Value)))
            {
                prompt.AppendLine($"- {fact.Label}: {OneLine(fact.Value)}");
            }

            prompt.AppendLine();
            AppendMoments(prompt, job.Facts);

            if (job.Guidance.Count > 0)
            {
                prompt.AppendLine();
                foreach (string line in job.Guidance)
                {
                    prompt.AppendLine(line);
                }
            }

            if (opener is not null)
            {
                prompt.AppendLine();
                prompt.AppendLine($"Already written, said first: {opener}");
            }

            if (closer is not null)
            {
                prompt.AppendLine();
                prompt.AppendLine($"Already written, said last: {closer}");
            }

            prompt.AppendLine();
            prompt.AppendLine("This only shows the format (with other moments and another show); write what's asked above, not this:");
            prompt.AppendLine("VO: This week, things get loud.");
            prompt.AppendLine("VO 4: And nobody is backing down.");
            if (clipsSpeak)
            {
                prompt.AppendLine("CLIP 9");
                prompt.AppendLine("VO: Oh, it gets worse.");
            }

            if (closer is null)
            {
                prompt.AppendLine("VO: The Example Show, tonight at 7 on Example TV!");
            }

            return new TextGenerationRequest(instructions.ToString().Trim(), prompt.ToString().Trim())
            {
                MaxTokens = limit * 3 + clips * 8 + 64,
                Temperature = attempt == 0 ? 0.8 : 0.55,
                Seed = (int)(BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Seed}|{attempt}"))) & int.MaxValue)
            };
        }

        public override (string Text, string? Problem) Accept(string reply, AiTextJob job)
        {
            string text = ThinkPattern().Replace(reply ?? string.Empty, " ");
            int open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open >= 0)
            {
                text = text[..open];
            }

            HashSet<int> known = [.. job.Facts.Where(IsMoment)
                .Select(fact => int.TryParse(fact.Label["Moment ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1)];
            List<CuratedBeat> beats = [];
            HashSet<int> used = [];
            Dictionary<int, string> said = job.Facts.Where(IsMoment)
                .Select(fact => (Number: int.TryParse(fact.Label["Moment ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1,
                    Text: fact.Value[(fact.Value.IndexOf(") ", StringComparison.Ordinal) + 2)..]))
                .Where(moment => moment.Number >= 0)
                .GroupBy(moment => moment.Number)
                .ToDictionary(group => group.Key, group => group.First().Text);
            foreach (CuratedBeat parsed in Beats(text))
            {
                CuratedBeat beat = parsed;

                // The announcer never says a moment's line: the moment plays instead (or, if it can't, the line goes).
                if (beat.Voice is { } voice && Quoted(voice, said) is { } quoted)
                {
                    if (!clipsSpeak || used.Contains(quoted) || used.Count >= clips)
                    {
                        continue;
                    }

                    beat = new CuratedBeat(null, quoted);
                }

                // A clip it describes as another moment's line ("CLIP 9: I need a teacher's aid", when that's moment 12)
                // means it got the number wrong: the line it meant plays.
                if (beat is { ClipSpeaks: true, Moment: { } claimedNumber, Quote: { } claimed }
                    && !(said.TryGetValue(claimedNumber, out string? own) && Repeats(claimed, own))
                    && Quoted(claimed, said) is { } meant)
                {
                    beat = new CuratedBeat(null, meant);
                }

                CuratedBeat kept = beat;
                if (beat.Moment is { } number && (!known.Contains(number) || used.Contains(number) || used.Count >= clips || (beat.ClipSpeaks && !clipsSpeak)))
                {
                    // A moment it can't have: a line keeps its words over the usual footage; a clip on its own goes.
                    if (beat.Voice is null)
                    {
                        continue;
                    }

                    kept = beat with { Moment = null };
                }

                if (kept.Moment is { } moment)
                {
                    used.Add(moment);
                }

                beats.Add(kept with { Voice = kept.Voice is null ? null : UpNextPromo.WithoutTitleYears(kept.Voice) });
            }

            // With the template's own lines around them, clips alone can make the promo.
            if (!beats.Any(beat => beat.Voice is not null) && opener is null && closer is null)
            {
                return (string.Empty, "it wrote no lines for the announcer");
            }

            if (used.Count == 0)
            {
                return (string.Empty, "it didn't use any of the episode's moments");
            }

            // A clip speaking last would leave the promo without a sign-off; with the template's own closer after it, it's fine.
            while (closer is null && beats.Count > 0 && beats[^1].ClipSpeaks)
            {
                beats.RemoveAt(beats.Count - 1);
            }

            string spoken = Spoken(beats);
            if (spoken.Length > 0 && AiTextRules.Check(spoken, job with { Format = null }) is { } problem)
            {
                return (string.Empty, problem);
            }

            // A name it wasn't given is a guess, usually at who's speaking in a clip.
            if (spoken.Length > 0 && UnknownName(spoken, job, opener, closer) is { } name)
            {
                return (string.Empty, $"it used the name \"{name}\", which it wasn't given (probably guessing who speaks in a clip)");
            }

            return (string.Join('\n', beats.Select(beat => beat with { Voice = beat.Voice is null ? null : AiTextRules.Tidy(beat.Voice, job) }).Select(Line)), null);
        }

        private static bool IsMoment(AiTextFact fact) => fact.Label.StartsWith("Moment ", StringComparison.Ordinal);

        private static bool IsContext(AiTextFact fact) => fact.Label.StartsWith("Around moment ", StringComparison.Ordinal);

        /// <summary>
        /// The moment whose line the announcer's words repeat (all of it, or most of a longer one), so it can play instead;
        /// null when they don't.
        /// </summary>
        private static int? Quoted(string voice, IReadOnlyDictionary<int, string> moments)
        {
            foreach ((int number, string line) in moments)
            {
                if (Repeats(voice, line))
                {
                    return number;
                }
            }

            return null;
        }

        /// <summary>Whether <paramref name="words"/> repeat <paramref name="line"/>: all of it, or most of a longer one.</summary>
        private static bool Repeats(string words, string line)
        {
            string spoken = $" {AiTextRules.Normalize(words)} ";
            string wanted = AiTextRules.Normalize(line);
            if (wanted.Length < 6)
            {
                return false;
            }

            if (spoken.Contains($" {wanted} ", StringComparison.Ordinal))
            {
                return true;
            }

            HashSet<string> spokenWords = [.. spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            string[] wantedWords = [.. wanted.Split(' ').Where(word => word.Length > 2).Distinct()];
            return wantedWords.Length >= 4 && wantedWords.Count(spokenWords.Contains) >= Math.Ceiling(wantedWords.Length * 0.75);
        }

        private static string OneLine(string text) => string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// How the first step is asked for and checked: only the numbers of the moments to use, in order. Nothing is written
    /// for the announcer yet, so the model can give the choice its whole attention.
    /// </summary>
    private sealed class PickFormat(int clips, bool clipsSpeak) : AiTextFormat
    {
        public override string Id => $"curated-pick-v2|{clips}|{(clipsSpeak ? "speak" : "under")}";

        public override TextGenerationRequest Request(AiTextJob job, int attempt)
        {
            string channel = string.IsNullOrWhiteSpace(job.Channel) ? "a TV channel" : $"the TV channel \"{job.Channel.Trim()}\"";
            var instructions = new StringBuilder();
            instructions.AppendLine($"You choose moments from a TV episode for a promo on {channel}. Someone else writes the announcer's words afterwards, around the moments you choose.");
            instructions.AppendLine("Reply with only the numbers of the moments, in the order they'll play, separated by commas. For example: 12, 4");
            instructions.AppendLine("Rules:");
            instructions.AppendLine($"- Choose 1 to {clips} moments. Fewer, well-chosen moments are better than many.");
            instructions.AppendLine("- Choose moments that fit what the promo should do, and that make sense on their own, out of context.");
            instructions.AppendLine(clipsSpeak
                ? "- A moment may be played with its own sound, so its words should land by themselves: a reaction, a punchline, an answer, a shout."
                : "- The moments play quietly under the announcer, so choose ones that look lively.");
            instructions.AppendLine("- Nobody knows who says any moment. Judge each by its own words and what's said around it, and prefer lines that are clear without knowing who says them.");
            instructions.AppendLine("- Write nothing else: no words for the announcer, no explanations.");

            var prompt = new StringBuilder();
            prompt.AppendLine("What the promo should do:");
            prompt.AppendLine(job.Config.Prompt is { Length: > 0 } wanted && wanted.Trim().Length > 0
                ? wanted.Trim()
                : "Make people want to watch: pick the funniest, most exciting or most dramatic moments.");
            prompt.AppendLine();
            prompt.AppendLine("Facts:");
            foreach (AiTextFact fact in job.Facts.Where(fact => !fact.Label.StartsWith("Moment ", StringComparison.Ordinal)
                && !fact.Label.StartsWith("Around moment ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(fact.Value)))
            {
                prompt.AppendLine($"- {fact.Label}: {OneLine(fact.Value)}");
            }

            prompt.AppendLine();
            AppendMoments(prompt, job.Facts);

            prompt.AppendLine();
            prompt.AppendLine($"Which moments (at most {clips}), in order?");
            return new TextGenerationRequest(instructions.ToString().Trim(), prompt.ToString().Trim())
            {
                MaxTokens = 48,
                Temperature = attempt == 0 ? 0.6 : 0.4,
                Seed = (int)(BitConverter.ToUInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{job.Seed}|{attempt}"))) & int.MaxValue)
            };
        }

        public override (string Text, string? Problem) Accept(string reply, AiTextJob job)
        {
            string text = ThinkPattern().Replace(reply ?? string.Empty, " ");
            int open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open >= 0)
            {
                text = text[..open];
            }

            HashSet<int> known = [.. job.Facts.Where(fact => fact.Label.StartsWith("Moment ", StringComparison.Ordinal))
                .Select(fact => int.TryParse(fact.Label["Moment ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1)];
            List<int> picked = [.. NumberPattern().Matches(text)
                .Select(match => int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1)
                .Where(known.Contains)
                .Distinct()
                .Take(clips)];
            return picked.Count == 0
                ? (string.Empty, "it didn't choose any of the listed moments")
                : (string.Join(", ", picked.Select(number => number.ToString(CultureInfo.InvariantCulture))), null);
        }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThinkPattern();
}
