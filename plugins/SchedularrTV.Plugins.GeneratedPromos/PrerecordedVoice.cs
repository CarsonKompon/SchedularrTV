using System.Globalization;
using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>One spoken part of a pre-recorded voice-over, and the takes it can be said with.</summary>
/// <param name="Label">What it says ("Coming up next…", "SpongeBob SquarePants", "8", "pm"), for the editor.</param>
/// <param name="Before">The pause before it (ignored for the first part); negative overlaps it with the part before.</param>
public sealed record PrerecordedPart(string Label, IReadOnlyList<string> Takes, TimeSpan Before = default);

/// <summary>A voice pack template that can be used for a promo: its parts with the takes each can use.</summary>
public sealed record PrerecordedCandidate(string Template, string Name, IReadOnlyList<PrerecordedPart> Parts)
{
    /// <summary>What it says, part by part.</summary>
    public string Describe() => string.Join(" · ", Parts.Select(part => part.Label));
}

/// <summary>
/// What a promo's pre-recorded voice-over can be made from: every template of its voice pack that suits it, each with
/// its takes. Which one (and which takes) is chosen when the promo is made, once the takes' lengths are known.
/// </summary>
/// <param name="Gap">The pack's pause between parts, for parts without their own (each part's is in <see cref="PrerecordedPart.Before"/>).</param>
/// <param name="TrimSilence">Whether takes have the silence at either end trimmed before they're measured and joined.</param>
/// <param name="NormalizeLevels">Whether each take is brought to the same loudness before they're joined.</param>
public sealed record PrerecordedVoiceRequest(IReadOnlyList<PrerecordedCandidate> Candidates, TimeSpan Gap, GeneratedVoiceFit Fit, bool SpeedUp, string Seed,
    bool TrimSilence = false, bool NormalizeLevels = false);

/// <summary>Where a promo airs, for voice-overs that name it: the channel's id, and the block it airs in (if any).</summary>
/// <param name="ChannelName">The channel's name, for the editor's read-out.</param>
public sealed record GeneratedVoiceContext(string ChannelId, string? Block = null, string? ChannelName = null);

/// <summary>The template and takes chosen for a promo, and how long they are said one after another.</summary>
/// <param name="Joints">The pause before each take after the first (negative overlaps), already kept within what the takes allow.</param>
public sealed record PrerecordedChoice(PrerecordedCandidate Candidate, IReadOnlyList<string> Takes, TimeSpan Length, IReadOnlyList<TimeSpan> Joints);

/// <summary>Pre-recorded voice-overs: voice pack templates filled in for a promo's shows, and fitted to its length.</summary>
public static class PrerecordedVoice
{
    /// <summary>
    /// The voice pack templates that can say this promo's voice-over, with their takes for its shows; null when the
    /// voice-over isn't pre-recorded, its pack is missing, or no template has recordings for everything it would say.
    /// </summary>
    /// <param name="shows">The shows a part can be about: the promo's own first (for Up Next, the upcoming ones in order).</param>
    /// <param name="where">The channel (and block) the promo airs on, for channel name phrases; without it they can't be said.</param>
    public static PrerecordedVoiceRequest? Plan(GeneratedVoiceConfig voice, IReadOnlyList<UpNextListing> shows, TimeZoneInfo zone,
        DateTimeOffset airsAt, string seed, GeneratedVoiceContext? where = null)
    {
        if (!voice.IsPrerecorded || voice.Prerecorded!.Linked is not { } pack || shows.Count == 0)
        {
            return null;
        }

        GeneratedPrerecordedVoiceConfig config = voice.Prerecorded;
        List<PrerecordedCandidate> candidates = [];
        foreach (GeneratedVoicePackTemplate template in Usable(config, pack))
        {
            TimeSpan gap = GeneratedVoicePack.ClampGap(pack.Gap);
            List<PrerecordedPart>? parts = [];
            IReadOnlyList<GeneratedVoiceVariant> variants = Variants(pack, template, shows);
            for (int index = 0; index < template.Parts.Count; index++)
            {
                GeneratedVoicePart part = template.Parts[index];
                GeneratedVoicePhrase? phrase = pack.Phrases.FirstOrDefault(candidate => string.Equals(candidate.Id, part.Phrase, StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<PrerecordedPart>? said = phrase is null ? null : Parts(phrase, part.Show, shows, zone, airsAt, where, variants[index]);
                if (said is null)
                {
                    parts = null;
                    break;
                }

                // The part's own pause goes before it; a time's hour, minutes and am/pm keep the pack's between them.
                TimeSpan before = part.Gap is { } own ? GeneratedVoicePack.ClampGap(own) : gap;
                parts.AddRange(said.Select((piece, index) => piece with { Before = index == 0 ? before : gap }));
            }

            if (parts is { Count: > 0 })
            {
                candidates.Add(new PrerecordedCandidate(template.Id, template.Name, parts));
            }
        }

        return candidates.Count == 0 ? null
            : new PrerecordedVoiceRequest(candidates, GeneratedVoicePack.ClampGap(pack.Gap), config.Fit, config.SpeedUp, $"{seed}|prerecorded", pack.TrimSilence,
                config.LevelsNormalized);
    }

    /// <summary>
    /// The templates a promo may use: the pack's that it allows (unless they're turned off), then its own inline ones,
    /// which are built from the same pack's phrases. Templates that are off or have no parts are left out.
    /// </summary>
    public static IEnumerable<GeneratedVoicePackTemplate> Usable(GeneratedPrerecordedVoiceConfig config, GeneratedVoicePack pack)
    {
        IEnumerable<GeneratedVoicePackTemplate> fromPack = [];
        if (config.PackTemplatesUsed)
        {
            HashSet<string>? allowed = config.Templates is { Count: > 0 } chosen ? new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase) : null;
            fromPack = pack.Templates.Where(template => allowed is null || allowed.Contains(template.Id));
        }

        return fromPack.Concat(config.InlineTemplates ?? []).Where(template => template.Enabled && template.Parts is { Count: > 0 });
    }

    /// <summary>
    /// Why a voice pack template can't be said for these shows, part by part ("no take of Rugrats for Show name"); empty
    /// when it can.
    /// </summary>
    public static IReadOnlyList<string> Missing(GeneratedVoicePack pack, GeneratedVoicePackTemplate template, IReadOnlyList<UpNextListing> shows,
        TimeZoneInfo zone, DateTimeOffset airsAt, GeneratedVoiceContext? where = null)
    {
        List<string> missing = [];
        if (template.Parts.Count == 0)
        {
            missing.Add("it has no parts yet");
        }

        IReadOnlyList<GeneratedVoiceVariant> variants = Variants(pack, template, shows);
        for (int index = 0; index < template.Parts.Count; index++)
        {
            GeneratedVoicePart part = template.Parts[index];
            GeneratedVoicePhrase? phrase = pack.Phrases.FirstOrDefault(candidate => string.Equals(candidate.Id, part.Phrase, StringComparison.OrdinalIgnoreCase));
            if (phrase is null)
            {
                missing.Add("a part's phrase was deleted");
                continue;
            }

            if (Parts(phrase, part.Show, shows, zone, airsAt, where, variants[index]) is not null)
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(phrase.Name) ? "a phrase" : $"“{phrase.Name}”";
            if (phrase.Kind == GeneratedVoicePhraseKind.Text)
            {
                missing.Add($"{name} has no recordings");
            }
            else if (phrase.Kind == GeneratedVoicePhraseKind.ChannelName)
            {
                missing.Add(where is null ? $"{name} needs to know the channel"
                    : $"{name} has no recording for this channel{(phrase.UseBlockNames && where.Block is { } inBlock ? $" (or its block “{inBlock}”)" : "")}");
            }
            else if (part.Show < 0 || part.Show >= shows.Count)
            {
                missing.Add($"{name} is about show {part.Show + 1}, and this promo only has {shows.Count}");
            }
            else
            {
                UpNextListing listing = shows[part.Show];
                missing.Add(phrase.Kind switch
                {
                    GeneratedVoicePhraseKind.ShowName => $"{name} has no recording of “{listing.Title}”",
                    GeneratedVoicePhraseKind.Day => $"{name} has no recording of “{GeneratedVoiceKeys.DayLabel(DayKeys(listing.Start, airsAt, zone)[^1])}” (or today/tonight/tomorrow)",
                    GeneratedVoicePhraseKind.DayTime => $"{name} has no recording of “{GeneratedVoiceKeys.DayTimeLabel(GeneratedVoiceKeys.DayTime(DayKeys(listing.Start, airsAt, zone)[^1], WholeTimeKeys(phrase, listing.Start, zone, phrase.AmPmInTake)[^1]))}” (or today/tonight/tomorrow at that time)",
                    GeneratedVoicePhraseKind.Time when phrase.Combined =>
                        $"{name} has no whole take of “{GeneratedVoiceKeys.TimeLabel(WholeTimeKeys(phrase, listing.Start, zone, phrase.AmPmInTake)[^1])}”"
                        + $"{(!phrase.TwentyFourHour && !phrase.AmPmInTake && phrase.SayAmPm ? " (with AM/PM)" : "")}, and its parts aren't all recorded",
                    _ => $"{name} is missing {string.Join(", ", TimeKeys(phrase, listing.Start, zone).Where(key => Entry(phrase, key) is null).Select(key => $"“{GeneratedVoiceKeys.TimeLabel(key)}”"))}"
                });
            }
        }

        return missing;
    }

    /// <summary>What one of the templates would say, for the editor's read-out (the one actually said is chosen when it's made).</summary>
    public static string Describe(PrerecordedVoiceRequest request) =>
        request.Candidates[UpNextPromo.Pick($"{request.Seed}|describe", request.Candidates.Count)].Describe();

    /// <summary>
    /// What a phrase says about show <paramref name="show"/> (0 is the promo's own): one part, or for a time several (hour,
    /// minutes, am/pm). Null when that show isn't there or the phrase has no recording for what it would say.
    /// </summary>
    /// <param name="variant">For show name phrases: plainly, "more …" or "even more …" (see <see cref="Variants"/>).</param>
    public static IReadOnlyList<PrerecordedPart>? Parts(GeneratedVoicePhrase phrase, int show, IReadOnlyList<UpNextListing> shows,
        TimeZoneInfo zone, DateTimeOffset airsAt, GeneratedVoiceContext? where = null, GeneratedVoiceVariant variant = GeneratedVoiceVariant.Plain)
    {
        if (phrase.Kind == GeneratedVoicePhraseKind.Text)
        {
            List<string> said = Usable(phrase.Takes);
            return said.Count > 0 ? new[] { new PrerecordedPart(phrase.Name, said) } : null;
        }

        if (phrase.Kind == GeneratedVoicePhraseKind.ChannelName)
        {
            // The block's own name while the promo airs in it (when it's recorded), otherwise the channel's.
            if (where is null || string.IsNullOrWhiteSpace(where.ChannelId))
            {
                return null;
            }

            if (phrase.UseBlockNames && !string.IsNullOrWhiteSpace(where.Block)
                && Entry(phrase, GeneratedVoiceKeys.Block(where.ChannelId, where.Block)) is { } blockTakes)
            {
                return new[] { new PrerecordedPart(where.Block, blockTakes) };
            }

            return Entry(phrase, GeneratedVoiceKeys.Channel(where.ChannelId)) is { } channelTakes
                ? new[] { new PrerecordedPart(string.IsNullOrWhiteSpace(where.ChannelName) ? phrase.Name : where.ChannelName, channelTakes) }
                : null;
        }

        if (show < 0 || show >= shows.Count)
        {
            return null;
        }

        UpNextListing listing = shows[show];
        switch (phrase.Kind)
        {
            case GeneratedVoicePhraseKind.ShowName:
                // Matched among shows with takes first; only the match's files are checked (a pack can hold hundreds of shows).
                // A mention after the first says "more …" or "even more …", falling back to the variant before when unrecorded.
                GeneratedVoiceVariant[] tries = variant switch
                {
                    GeneratedVoiceVariant.EvenMore => [GeneratedVoiceVariant.EvenMore, GeneratedVoiceVariant.More, GeneratedVoiceVariant.Plain],
                    GeneratedVoiceVariant.More => [GeneratedVoiceVariant.More, GeneratedVoiceVariant.Plain],
                    _ => [GeneratedVoiceVariant.Plain]
                };
                GeneratedVoiceEntry? entry = ShowTitleMatcher.Find(listing.Title,
                    phrase.Entries.Where(candidate => tries.Any(each => candidate.TakesOf(each) is { Count: > 0 })), candidate => candidate.Key);
                if (entry is null)
                {
                    return null;
                }

                string title = UpNextPromo.SpokenTitle(listing.Title);
                foreach (GeneratedVoiceVariant each in tries)
                {
                    List<string> showTakes = Usable(entry.TakesOf(each) ?? []);
                    if (showTakes.Count > 0)
                    {
                        return new[] { new PrerecordedPart(VariantLabel(each, title), showTakes) };
                    }
                }

                return null;

            case GeneratedVoicePhraseKind.Day:
                foreach (string key in DayKeys(listing.Start, airsAt, zone))
                {
                    if (Entry(phrase, key) is { } takes)
                    {
                        return new[] { new PrerecordedPart(GeneratedVoiceKeys.DayLabel(key), takes) };
                    }
                }

                return null;

            case GeneratedVoicePhraseKind.DayTime:
                foreach (string day in DayKeys(listing.Start, airsAt, zone))
                {
                    foreach (string time in WholeTimeKeys(phrase, listing.Start, zone, phrase.AmPmInTake))
                    {
                        string key = GeneratedVoiceKeys.DayTime(day, time);
                        if (Entry(phrase, key) is { } takes)
                        {
                            return new[] { new PrerecordedPart(GeneratedVoiceKeys.DayTimeLabel(key), takes) };
                        }
                    }
                }

                return null;

            case GeneratedVoicePhraseKind.Time:
                // Said whole when that time is recorded ("2:45 PM", or "2:45" then a separate "PM"); otherwise from its parts.
                if (phrase.Combined && Whole(phrase, listing.Start, zone) is { } whole)
                {
                    return whole;
                }

                List<PrerecordedPart> parts = [];
                foreach (string key in TimeKeys(phrase, listing.Start, zone))
                {
                    if (Entry(phrase, key) is not { } takes)
                    {
                        return null;
                    }

                    parts.Add(new PrerecordedPart(GeneratedVoiceKeys.TimeLabel(key), takes));
                }

                return parts;

            default:
                return null;
        }
    }

    /// <summary>
    /// How each part of a template says a show's name: plainly the first time the template names a show, "more …" the
    /// second time it names the same show (by another part, or another upcoming slot that's the same show), and
    /// "even more …" after that. Parts that aren't show names are <see cref="GeneratedVoiceVariant.Plain"/>.
    /// </summary>
    public static IReadOnlyList<GeneratedVoiceVariant> Variants(GeneratedVoicePack pack, GeneratedVoicePackTemplate template, IReadOnlyList<UpNextListing> shows)
    {
        Dictionary<string, int> mentions = new(StringComparer.Ordinal);
        GeneratedVoiceVariant[] variants = new GeneratedVoiceVariant[template.Parts.Count];
        for (int index = 0; index < template.Parts.Count; index++)
        {
            GeneratedVoicePart part = template.Parts[index];
            GeneratedVoicePhrase? phrase = pack.Phrases.FirstOrDefault(candidate => string.Equals(candidate.Id, part.Phrase, StringComparison.OrdinalIgnoreCase));
            if (phrase?.Kind != GeneratedVoicePhraseKind.ShowName || part.Show < 0 || part.Show >= shows.Count)
            {
                continue;
            }

            string show = ShowTitleMatcher.Words(shows[part.Show].Title);
            int before = mentions.GetValueOrDefault(show);
            mentions[show] = before + 1;
            variants[index] = before switch
            {
                0 => GeneratedVoiceVariant.Plain,
                1 => GeneratedVoiceVariant.More,
                _ => GeneratedVoiceVariant.EvenMore
            };
        }

        return variants;
    }

    /// <summary>A show name as a variant says it: "Rugrats", "More Rugrats", "Even more Rugrats".</summary>
    public static string VariantLabel(GeneratedVoiceVariant variant, string title) => variant switch
    {
        GeneratedVoiceVariant.More => $"More {title}",
        GeneratedVoiceVariant.EvenMore => $"Even more {title}",
        _ => title
    };

    /// <summary>
    /// The day keys a show's airing can be said with, best first: tonight (from 5pm) or today, tomorrow night or
    /// tomorrow, then its weekday, which stands in when the others weren't recorded.
    /// </summary>
    public static IReadOnlyList<string> DayKeys(DateTimeOffset start, DateTimeOffset airsAt, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(start, zone);
        int days = (local.Date - TimeZoneInfo.ConvertTime(airsAt, zone).Date).Days;
        bool evening = local.Hour >= 17;
        string weekday = local.DayOfWeek.ToString().ToLowerInvariant();
        if (days <= 0)
        {
            return evening ? new[] { "tonight", "today", weekday } : new[] { "today", weekday };
        }

        return days == 1
            ? evening ? new[] { "tomorrow night", "tomorrow", weekday } : new[] { "tomorrow", weekday }
            : new[] { weekday };
    }

    /// <summary>
    /// The whole-time keys a start time can be said with, best first: noon or midnight (for 12 o'clock), then the time
    /// itself (<see cref="GeneratedVoiceKeys.WholeTime"/>), with AM/PM in it when <paramref name="amPm"/> on a 12-hour clock.
    /// </summary>
    public static IReadOnlyList<string> WholeTimeKeys(GeneratedVoicePhrase phrase, DateTimeOffset start, TimeZoneInfo zone, bool amPm)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(start, zone);
        string time = GeneratedVoiceKeys.WholeTime(local.Hour, local.Minute, phrase.TwentyFourHour, amPm && !phrase.TwentyFourHour);
        return local.Minute != 0 ? new[] { time }
            : local.Hour == 12 ? new[] { GeneratedVoiceKeys.Noon, time }
            : local.Hour == 0 ? new[] { GeneratedVoiceKeys.Midnight, time }
            : new[] { time };
    }

    /// <summary>
    /// A time said whole: its whole take, then a separate AM/PM take when that isn't in the whole one (and is said). Null
    /// when the whole time (or that AM/PM) isn't recorded.
    /// </summary>
    private static IReadOnlyList<PrerecordedPart>? Whole(GeneratedVoicePhrase phrase, DateTimeOffset start, TimeZoneInfo zone)
    {
        bool separateAmPm = !phrase.TwentyFourHour && !phrase.AmPmInTake && phrase.SayAmPm;
        foreach (string key in WholeTimeKeys(phrase, start, zone, phrase.AmPmInTake))
        {
            if (Entry(phrase, key) is not { } takes)
            {
                continue;
            }

            List<PrerecordedPart> parts = [new PrerecordedPart(GeneratedVoiceKeys.TimeLabel(key), takes)];
            if (separateAmPm && key is not (GeneratedVoiceKeys.Noon or GeneratedVoiceKeys.Midnight))
            {
                string half = TimeZoneInfo.ConvertTime(start, zone).Hour < 12 ? GeneratedVoiceKeys.Am : GeneratedVoiceKeys.Pm;
                if (Entry(phrase, half) is not { } halfTakes)
                {
                    return null;
                }

                parts.Add(new PrerecordedPart(GeneratedVoiceKeys.TimeLabel(half), halfTakes));
            }

            return parts;
        }

        return null;
    }

    /// <summary>
    /// The time parts a start time is said with: noon or midnight when recorded (for 12 o'clock), otherwise the hour, the
    /// minutes when it isn't on the hour, and am/pm on a 12-hour clock that says it.
    /// </summary>
    public static IReadOnlyList<string> TimeKeys(GeneratedVoicePhrase phrase, DateTimeOffset start, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(start, zone);
        if (local.Minute == 0 && local.Hour == 12 && Entry(phrase, GeneratedVoiceKeys.Noon) is not null)
        {
            return new[] { GeneratedVoiceKeys.Noon };
        }

        if (local.Minute == 0 && local.Hour == 0 && Entry(phrase, GeneratedVoiceKeys.Midnight) is not null)
        {
            return new[] { GeneratedVoiceKeys.Midnight };
        }

        List<string> keys = [GeneratedVoiceKeys.Hour(phrase.TwentyFourHour ? local.Hour : local.Hour % 12 == 0 ? 12 : local.Hour % 12)];
        if (local.Minute != 0)
        {
            keys.Add(GeneratedVoiceKeys.Minute(local.Minute));
        }

        if (!phrase.TwentyFourHour && phrase.SayAmPm)
        {
            keys.Add(local.Hour < 12 ? GeneratedVoiceKeys.Am : GeneratedVoiceKeys.Pm);
        }

        return keys;
    }

    /// <summary>
    /// The template and takes to say, given each take's length and the time there is (<paramref name="room"/>): a random
    /// pick (fixed by the promo, so a re-render sounds the same), with shorter takes swapped in until it fits. With
    /// <see cref="GeneratedVoiceFit.Fill"/>, longer takes are swapped in while they still fit and the longest template
    /// wins. When nothing fits, the shortest there is (the render then speeds it up, if it's allowed to).
    /// </summary>
    public static PrerecordedChoice Choose(PrerecordedVoiceRequest request, Func<string, TimeSpan> length, TimeSpan room)
    {
        if (request.Candidates.Count == 0)
        {
            throw new InvalidOperationException("The voice pack has no template that can be said for this promo.");
        }

        List<(PrerecordedCandidate Candidate, int[] Picks, TimeSpan[][] Lengths, TimeSpan Total)> options = [];
        foreach (PrerecordedCandidate candidate in request.Candidates)
        {
            TimeSpan[][] lengths = [.. candidate.Parts.Select(part => part.Takes.Select(length).ToArray())];
            int[] picks = [.. candidate.Parts.Select((part, index) =>
                UpNextPromo.Pick($"{request.Seed}|{candidate.Template}|{index}", part.Takes.Count))];
            TimeSpan Total()
            {
                TimeSpan total = lengths[0][picks[0]];
                for (int index = 1; index < picks.Length; index++)
                {
                    TimeSpan take = lengths[index][picks[index]];
                    total += Joint(candidate.Parts[index].Before, lengths[index - 1][picks[index - 1]], take) + take;
                }

                return total;
            }

            // Too long: the part that can save the most switches to its shortest take, until it fits or can't get shorter.
            while (Total() > room)
            {
                int best = -1;
                TimeSpan saving = TimeSpan.Zero;
                for (int index = 0; index < picks.Length; index++)
                {
                    TimeSpan shortest = lengths[index].Min();
                    if (lengths[index][picks[index]] - shortest > saving)
                    {
                        saving = lengths[index][picks[index]] - shortest;
                        best = index;
                    }
                }

                if (best < 0) break;
                picks[best] = Array.IndexOf(lengths[best], lengths[best].Min());
            }

            // Filling the time: the longest swap that still fits, one at a time.
            if (request.Fit == GeneratedVoiceFit.Fill && Total() <= room)
            {
                while (true)
                {
                    (int Part, int Take) best = (-1, -1);
                    TimeSpan gain = TimeSpan.Zero;
                    TimeSpan total = Total();
                    for (int index = 0; index < picks.Length; index++)
                    {
                        for (int take = 0; take < lengths[index].Length; take++)
                        {
                            TimeSpan extra = lengths[index][take] - lengths[index][picks[index]];
                            if (extra > gain && total + extra <= room)
                            {
                                gain = extra;
                                best = (index, take);
                            }
                        }
                    }

                    if (best.Part < 0) break;
                    picks[best.Part] = best.Take;
                }
            }

            options.Add((candidate, picks, lengths, Total()));
        }

        List<int> fitting = [.. Enumerable.Range(0, options.Count).Where(index => options[index].Total <= room)];
        int chosen = fitting.Count == 0
            ? Enumerable.Range(0, options.Count).MinBy(index => options[index].Total)
            : request.Fit == GeneratedVoiceFit.Fill
                ? fitting.MaxBy(index => options[index].Total)
                : fitting[UpNextPromo.Pick($"{request.Seed}|template", fitting.Count)];

        (PrerecordedCandidate winner, int[] winning, TimeSpan[][] winningLengths, TimeSpan length2) = options[chosen];
        List<TimeSpan> joints = [];
        for (int index = 1; index < winning.Length; index++)
        {
            joints.Add(Joint(winner.Parts[index].Before, winningLengths[index - 1][winning[index - 1]], winningLengths[index][winning[index]]));
        }

        return new PrerecordedChoice(winner, [.. winning.Select((pick, index) => winner.Parts[index].Takes[pick])], length2, joints);
    }

    /// <summary>
    /// The pause between two takes: <paramref name="before"/>, or for an overlap at most 40% of the shorter take, so a
    /// crossfade never swallows a word whole.
    /// </summary>
    public static TimeSpan Joint(TimeSpan before, TimeSpan previous, TimeSpan next)
    {
        if (before >= TimeSpan.Zero)
        {
            return before;
        }

        TimeSpan most = TimeSpan.FromTicks((long)(Math.Min(previous.Ticks, next.Ticks) * 0.4));
        return -before > most ? -most : before;
    }

    private static List<string> Usable(IEnumerable<GeneratedVoiceTake> takes) =>
        [.. takes.Select(take => take.Path?.Trim() ?? string.Empty).Where(path => path.Length > 0 && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static List<string>? Entry(GeneratedVoicePhrase phrase, string key) =>
        phrase.Entries.Where(entry => string.Equals(entry.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase))
            .Select(entry => Usable(entry.Takes)).FirstOrDefault(takes => takes.Count > 0);

    /// <summary>The loudness every take is brought to when levels are evened out (EBU R128 integrated loudness).</summary>
    public const double TargetLoudness = -18;

    /// <summary>The highest a take's peak may be raised to, so evening it out never clips it.</summary>
    public const double PeakCeiling = -1;

    /// <summary>The most a take is turned up or down, so a near-silent or broken take isn't blown up.</summary>
    public const double MaxLevelChange = 20;

    /// <summary>Changes smaller than this aren't worth a new file.</summary>
    public const double LevelTolerance = 0.5;

    /// <summary>
    /// How many dB to turn a take up (or down, negative) to bring it to <see cref="TargetLoudness"/>: from its integrated
    /// loudness, or for a take too short to measure that way (under about 0.4 s), its mean volume. A boost stops short of
    /// pushing its peak over <see cref="PeakCeiling"/>, and no change is more than <see cref="MaxLevelChange"/>. Zero when
    /// it can't be measured or is already within <see cref="LevelTolerance"/>.
    /// </summary>
    public static double LevelGain(double? integratedLufs, double? meanDb, double? peakDb)
    {
        // ebur128 reports -70 LUFS (its gate) when there's too little to measure.
        double? loudness = integratedLufs is { } lufs && double.IsFinite(lufs) && lufs > -69 ? lufs
            : meanDb is { } mean && double.IsFinite(mean) && mean > -69 ? mean
            : null;
        if (loudness is not { } measured)
        {
            return 0;
        }

        double gain = TargetLoudness - measured;
        if (gain > 0 && peakDb is { } peak && double.IsFinite(peak))
        {
            gain = Math.Min(gain, Math.Max(0, PeakCeiling - peak));
        }

        gain = Math.Clamp(gain, -MaxLevelChange, MaxLevelChange);
        return Math.Abs(gain) < LevelTolerance ? 0 : Math.Round(gain, 2);
    }

    /// <summary>
    /// Reads a take's loudness from ffmpeg's ebur128 and volumedetect output: integrated loudness (the summary's, which
    /// comes last), true peak, mean and max volume. Nulls for what isn't there.
    /// </summary>
    public static (double? Integrated, double? Mean, double? Peak) ParseLoudness(string ffmpegOutput)
    {
        static double? Last(string text, string pattern)
        {
            MatchCollection matches = System.Text.RegularExpressions.Regex.Matches(text, pattern);
            if (matches.Count == 0) return null;
            string value = matches[^1].Groups[1].Value;
            return value is "-inf" ? double.NegativeInfinity
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;
        }

        double? integrated = Last(ffmpegOutput, @"\bI:\s+(-inf|-?\d+(?:\.\d+)?)\s+LUFS");
        double? truePeak = Last(ffmpegOutput, @"\bPeak:\s+(-inf|-?\d+(?:\.\d+)?)\s+dBFS");
        double? mean = Last(ffmpegOutput, @"mean_volume:\s+(-inf|-?\d+(?:\.\d+)?)\s+dB");
        double? max = Last(ffmpegOutput, @"max_volume:\s+(-inf|-?\d+(?:\.\d+)?)\s+dB");
        double? peak = truePeak is { } tp && double.IsFinite(tp) ? max is { } m && double.IsFinite(m) ? Math.Max(tp, m) : tp : max;
        return (integrated, mean, peak);
    }

    /// <summary>A length in seconds, for the editor.</summary>
    public static string Seconds(TimeSpan length) => length.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
