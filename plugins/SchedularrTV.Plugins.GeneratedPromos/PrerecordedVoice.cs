using System.Globalization;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>One spoken part of a pre-recorded voice-over, and the takes it can be said with.</summary>
/// <param name="Label">What it says ("Coming up next…", "SpongeBob SquarePants", "8", "pm"), for the editor.</param>
public sealed record PrerecordedPart(string Label, IReadOnlyList<string> Takes);

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
public sealed record PrerecordedVoiceRequest(IReadOnlyList<PrerecordedCandidate> Candidates, TimeSpan Gap, GeneratedVoiceFit Fit, bool SpeedUp, string Seed);

/// <summary>The template and takes chosen for a promo, and how long they are said one after another.</summary>
public sealed record PrerecordedChoice(PrerecordedCandidate Candidate, IReadOnlyList<string> Takes, TimeSpan Length);

/// <summary>Pre-recorded voice-overs: voice pack templates filled in for a promo's shows, and fitted to its length.</summary>
public static class PrerecordedVoice
{
    /// <summary>
    /// The voice pack templates that can say this promo's voice-over, with their takes for its shows; null when the
    /// voice-over isn't pre-recorded, its pack is missing, or no template has recordings for everything it would say.
    /// </summary>
    /// <param name="shows">The shows a part can be about: the promo's own first (for Up Next, the upcoming ones in order).</param>
    public static PrerecordedVoiceRequest? Plan(GeneratedVoiceConfig voice, IReadOnlyList<UpNextListing> shows, TimeZoneInfo zone,
        DateTimeOffset airsAt, string seed)
    {
        if (!voice.IsPrerecorded || voice.Prerecorded!.Linked is not { } pack || shows.Count == 0)
        {
            return null;
        }

        GeneratedPrerecordedVoiceConfig config = voice.Prerecorded;
        HashSet<string>? allowed = config.Templates is { Count: > 0 } chosen ? new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase) : null;
        List<PrerecordedCandidate> candidates = [];
        foreach (GeneratedVoicePackTemplate template in pack.Templates)
        {
            if (!template.Enabled || template.Parts.Count == 0 || (allowed is not null && !allowed.Contains(template.Id)))
            {
                continue;
            }

            List<PrerecordedPart>? parts = [];
            foreach (GeneratedVoicePart part in template.Parts)
            {
                GeneratedVoicePhrase? phrase = pack.Phrases.FirstOrDefault(candidate => string.Equals(candidate.Id, part.Phrase, StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<PrerecordedPart>? said = phrase is null ? null : Parts(phrase, part.Show, shows, zone, airsAt);
                if (said is null)
                {
                    parts = null;
                    break;
                }

                parts.AddRange(said);
            }

            if (parts is { Count: > 0 })
            {
                candidates.Add(new PrerecordedCandidate(template.Id, template.Name, parts));
            }
        }

        return candidates.Count == 0 ? null
            : new PrerecordedVoiceRequest(candidates, pack.Gap < TimeSpan.Zero ? TimeSpan.Zero : pack.Gap, config.Fit, config.SpeedUp, $"{seed}|prerecorded");
    }

    /// <summary>
    /// Why a voice pack template can't be said for these shows, part by part ("no take of Rugrats for Show name"); empty
    /// when it can.
    /// </summary>
    public static IReadOnlyList<string> Missing(GeneratedVoicePack pack, GeneratedVoicePackTemplate template, IReadOnlyList<UpNextListing> shows,
        TimeZoneInfo zone, DateTimeOffset airsAt)
    {
        List<string> missing = [];
        if (template.Parts.Count == 0)
        {
            missing.Add("it has no parts yet");
        }

        foreach (GeneratedVoicePart part in template.Parts)
        {
            GeneratedVoicePhrase? phrase = pack.Phrases.FirstOrDefault(candidate => string.Equals(candidate.Id, part.Phrase, StringComparison.OrdinalIgnoreCase));
            if (phrase is null)
            {
                missing.Add("a part's phrase was deleted");
                continue;
            }

            if (Parts(phrase, part.Show, shows, zone, airsAt) is not null)
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(phrase.Name) ? "a phrase" : $"“{phrase.Name}”";
            if (phrase.Kind == GeneratedVoicePhraseKind.Text)
            {
                missing.Add($"{name} has no recordings");
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
    public static IReadOnlyList<PrerecordedPart>? Parts(GeneratedVoicePhrase phrase, int show, IReadOnlyList<UpNextListing> shows,
        TimeZoneInfo zone, DateTimeOffset airsAt)
    {
        if (phrase.Kind == GeneratedVoicePhraseKind.Text)
        {
            List<string> said = Usable(phrase.Takes);
            return said.Count > 0 ? new[] { new PrerecordedPart(phrase.Name, said) } : null;
        }

        if (show < 0 || show >= shows.Count)
        {
            return null;
        }

        UpNextListing listing = shows[show];
        switch (phrase.Kind)
        {
            case GeneratedVoicePhraseKind.ShowName:
                GeneratedVoiceEntry? entry = ShowTitleMatcher.Find(listing.Title, phrase.Entries.Where(candidate => Usable(candidate.Takes).Count > 0), candidate => candidate.Key);
                return entry is null ? null : new[] { new PrerecordedPart(UpNextPromo.SpokenTitle(listing.Title), Usable(entry.Takes)) };

            case GeneratedVoicePhraseKind.Day:
                foreach (string key in DayKeys(listing.Start, airsAt, zone))
                {
                    if (Entry(phrase, key) is { } takes)
                    {
                        return new[] { new PrerecordedPart(GeneratedVoiceKeys.DayLabel(key), takes) };
                    }
                }

                return null;

            case GeneratedVoicePhraseKind.Time:
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
            TimeSpan gaps = request.Gap * Math.Max(0, candidate.Parts.Count - 1);
            TimeSpan Total() => gaps + TimeSpan.FromTicks(picks.Select((pick, index) => lengths[index][pick].Ticks).Sum());

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

        (PrerecordedCandidate winner, int[] winning, _, TimeSpan length2) = options[chosen];
        return new PrerecordedChoice(winner, [.. winning.Select((pick, index) => winner.Parts[index].Takes[pick])], length2);
    }

    private static List<string> Usable(IEnumerable<GeneratedVoiceTake> takes) =>
        [.. takes.Select(take => take.Path?.Trim() ?? string.Empty).Where(path => path.Length > 0 && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static List<string>? Entry(GeneratedVoicePhrase phrase, string key) =>
        phrase.Entries.Where(entry => string.Equals(entry.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase))
            .Select(entry => Usable(entry.Takes)).FirstOrDefault(takes => takes.Count > 0);

    /// <summary>A length in seconds, for the editor.</summary>
    public static string Seconds(TimeSpan length) => length.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
