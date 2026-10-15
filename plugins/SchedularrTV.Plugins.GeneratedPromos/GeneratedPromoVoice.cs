using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Exact user-authored voice-over scripts, with no automatic linking phrases or AI rewriting.</summary>
public static class GeneratedPromoVoice
{
    public static string? Script(GeneratedVoiceConfig voice, IReadOnlyList<UpNextListing> upcoming,
        UpNextListing subject, string channel, TimeZoneInfo zone, string seed)
    {
        GeneratedAdvancedVoiceConfig advanced = voice.Advanced!;
        bool Available(string text) => GeneratedVoiceTokens.RequiredUpcoming(text) <= upcoming.Count && !GeneratedVoiceTokens.Unknown(text).Any();
        List<string> eligible = [.. advanced.Templates.Where(text => !string.IsNullOrWhiteSpace(text) && Available(text))];
        if (eligible.Count == 0) return null;

        string Expand(string text) => GeneratedVoiceTokens.Placeholder.Replace(text, match =>
        {
            string token = match.Groups[1].Value;
            if (GeneratedVoiceTokens.TryUpcoming(token, out int index, out string field))
            {
                UpNextListing show = upcoming[index];
                if (field == "start_time") return UpNextPromo.Clock(show.Start, zone);
                if (field == "end_time") return End(show, zone);
                if (field == "episode") return show.Episode ?? string.Empty;
                bool repeat = field != "raw" && upcoming.Take(index).Any(previous =>
                    string.Equals(ShowTitleMatcher.Words(previous.Title), ShowTitleMatcher.Words(show.Title), StringComparison.Ordinal));
                string title = UpNextPromo.SpokenTitle(show.Title);
                return repeat ? advanced.RepeatPrefix + title + advanced.RepeatSuffix : title;
            }
            return token.ToLowerInvariant() switch
            {
                "channel" => channel,
                "show" => UpNextPromo.SpokenTitle(subject.Title),
                "episode" => subject.Episode ?? string.Empty,
                "start_time" => UpNextPromo.Clock(subject.Start, zone),
                "end_time" => End(subject, zone),
                _ => match.Value
            };
        });

        string? Phrase(List<string> phrases, string? legacy, string part)
        {
            IEnumerable<string> options = phrases.Count > 0 ? phrases : legacy is not null ? [legacy] : [];
            List<string> available = [.. options.Where(Available)];
            return available.Count == 0 ? null : Expand(available[UpNextPromo.Pick($"{seed}|advanced-{part}", available.Count)]);
        }

        return string.Join(' ', new[]
        {
            advanced.IncludePreMessages ? Phrase(voice.PreMessages, voice.PreMessage, "pre") : null,
            Expand(eligible[UpNextPromo.Pick($"{seed}|advanced-script", eligible.Count)]),
            advanced.IncludePostMessages ? Phrase(voice.PostMessages, voice.PostMessage, "post") : null
        }.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!.Trim()));
    }

    private static string End(UpNextListing show, TimeZoneInfo zone) =>
        (show.End ?? (show.Runtime is { } runtime ? show.Start + runtime : null)) is { } end ? UpNextPromo.Clock(end, zone) : string.Empty;
}
