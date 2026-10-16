using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Writing;

namespace SchedularrTV.GeneratedPromos;

/// <summary>
/// What an AI writer is asked for a promo: its voice-over (in place of the usual read-out) and any text layers set to
/// be written, with the promo's shows as the facts it may use.
/// </summary>
public static class GeneratedPromoWriting
{
    public const string Source = "generated-promos";

    // Descriptions are cut to this, so the prompt stays small enough for a small model to keep its head.
    private const int OverviewLength = 320;

    /// <summary>The voice-over job, or null when the template's voice isn't AI-written.</summary>
    public static AiTextJob? Voice(
        GeneratedPromoTemplate template,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset airsAt,
        string fallback,
        string variety)
    {
        if (template.Voice is not { Enabled: true, Writer: { } writer } voice || listings.Count == 0
            || voice.Advanced is { Enabled: true })
        {
            return null;
        }

        List<string> guidance = [];
        if (listings.Skip(1).Any(listing => Repeat(listings, listing)))
        {
            guidance.Add("When a show is on again, don't list it again in full: say something like \"followed by even more of it\" instead.");
        }

        if (writer.UseDescriptions && listings[0].Overview is { Length: > 0 })
        {
            guidance.Add("You may add one short, playful remark about the episode, using only what its description says.");
        }

        guidance.Add($"Leave room to breathe: it has to be said in about {Seconds(template.Duration - voice.Start)} seconds.");

        IReadOnlyList<UpNextListing> spoken = [.. listings.Select(show => show with { Title = UpNextPromo.SpokenTitle(show.Title) })];
        if (listings.Any(show => show.Title != UpNextPromo.SpokenTitle(show.Title)))
            guidance.Add("Use the show names exactly as given here; do not add parenthesised release years, colons or dashes to spoken titles.");

        return new AiTextJob
        {
            Config = writer,
            Use = AiTextUse.Spoken,
            Channel = channelName,
            Purpose = $"the announcer's voice-over for {Kind(template.Type)}",
            Fallback = fallback,
            Facts = Facts(template.Type, channelName, spoken, zone, airsAt, writer.UseDescriptions),
            Guidance = guidance,
            MustSay = [.. spoken.Select(listing => listing.Title).Distinct(StringComparer.OrdinalIgnoreCase)],
            MaxWords = AiTextRules.SpokenWords(template.Duration - voice.Start - TimeSpan.FromMilliseconds(300)),
            Seed = $"{variety}|voice",
            Source = Source,
            Due = airsAt
        };
    }

    /// <summary>The job for one AI-written text layer, or null when it isn't one.</summary>
    public static AiTextJob? Layer(
        GeneratedPromoTemplate template,
        GeneratedLayer layer,
        int index,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset airsAt,
        string fallback,
        string variety)
    {
        if (layer.Kind != GeneratedLayerKind.Text || layer.Writer is not { } writer || listings.Count == 0)
        {
            return null;
        }

        return new AiTextJob
        {
            Config = writer,
            Use = AiTextUse.Shown,
            Channel = channelName,
            Purpose = $"the text in a box on screen during {Kind(template.Type)}" +
                (string.IsNullOrWhiteSpace(writer.Prompt) ? ": a short, punchy caption about the show" : string.Empty),
            Fallback = fallback,
            Facts = Facts(template.Type, channelName, listings, zone, airsAt, writer.UseDescriptions),
            MaxWords = 8,
            Seed = $"{variety}|layer|{index}|{layer.Name}",
            Source = Source,
            Due = airsAt
        };
    }

    /// <summary>The promo with its AI-written words in place (or the usual ones where they couldn't be written).</summary>
    public static async Task<ResolvedGeneratedPromo> WriteAsync(ResolvedGeneratedPromo promo, AiTextWriter? writer, CancellationToken cancellationToken)
    {
        if (writer is null || (promo.ScriptJob is null && promo.LayerJobs.Count == 0))
        {
            return promo;
        }

        string? script = promo.Script;
        if (promo.ScriptJob is { } voice)
        {
            // This is a whole script: keep its sentence punctuation and clock times, unlike title-only cleanup.
            script = UpNextPromo.WithoutTitleYears((await writer.WriteAsync(voice, cancellationToken).ConfigureAwait(false)).Text);
        }

        List<IReadOnlyList<string>> lines = [.. promo.LayerLines];
        foreach ((int index, AiTextJob job) in promo.LayerJobs)
        {
            lines[index] = Lines((await writer.WriteAsync(job, cancellationToken).ConfigureAwait(false)).Text);
        }

        return promo with { Script = script, LayerLines = lines };
    }

    /// <summary>For editor stills: text layers as written so far, queueing any that aren't (the usual text until then).</summary>
    public static ResolvedGeneratedPromo Peek(ResolvedGeneratedPromo promo, AiTextWriter? writer)
    {
        if (writer is null || promo.LayerJobs.Count == 0)
        {
            return promo;
        }

        List<IReadOnlyList<string>> lines = [.. promo.LayerLines];
        foreach ((int index, AiTextJob job) in promo.LayerJobs)
        {
            lines[index] = Lines(writer.Peek(job).Text);
        }

        return promo with { LayerLines = lines };
    }

    private static IReadOnlyList<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>What the promo is, finishing "the announcer's voice-over for …".</summary>
    public static string Kind(GeneratedPromoType type) => type switch
    {
        GeneratedPromoType.NowPlaying => "an ident just before the show starts",
        GeneratedPromoType.ThisHasBeen => "a sign-off just after the show has finished",
        GeneratedPromoType.ComingUp => "a promo for a show later today",
        GeneratedPromoType.Eventually => "a promo for a show coming up later this week",
        GeneratedPromoType.YoureWatching => "the start of an ad break in the middle of the show (it carries on afterwards)",
        GeneratedPromoType.BackToTheShow => "the end of an ad break, just before the show carries on",
        GeneratedPromoType.NewEpisode => "a promo for an all-new episode of a show airing soon",
        GeneratedPromoType.RightBack => "\"we'll be right back\" as the show breaks for adverts (it carries on afterwards)",
        _ => "an \"Up Next\" promo just before the next shows"
    };

    /// <summary>The shows, as the model may talk about them.</summary>
    public static IReadOnlyList<AiTextFact> Facts(
        GeneratedPromoType type,
        string channelName,
        IReadOnlyList<UpNextListing> listings,
        TimeZoneInfo zone,
        DateTimeOffset airsAt,
        bool descriptions)
    {
        List<AiTextFact> facts = [new("Channel", channelName)];
        for (int index = 0; index < listings.Count; index++)
        {
            UpNextListing listing = listings[index];
            string label = type != GeneratedPromoType.UpNext ? "Show" : index == 0 ? "Next" : "Then";
            string when = type switch
            {
                GeneratedPromoType.UpNext when index == 0 => "right after this break",
                GeneratedPromoType.UpNext => $"at {UpNextPromo.SpokenClock(listing.Start, zone)}",
                GeneratedPromoType.ComingUp or GeneratedPromoType.Eventually or GeneratedPromoType.NewEpisode =>
                    UpNextPromo.When(listing.Start, airsAt, zone, day: true, time: true).ToLowerInvariant(),
                GeneratedPromoType.ThisHasBeen => "just finished",
                GeneratedPromoType.YoureWatching or GeneratedPromoType.BackToTheShow or GeneratedPromoType.RightBack => "being watched now",
                _ => "starting now"
            };
            string episode = string.IsNullOrWhiteSpace(listing.Episode) ? string.Empty : $", episode \"{listing.Episode}\"";
            string again = index > 0 && Repeat(listings, listing) ? " (the same show again)" : string.Empty;
            string fresh = listing.IsNew ? " (a brand-new episode, airing for the first time)" : string.Empty;
            facts.Add(new AiTextFact(label, $"{listing.Title}{episode}, {when}{again}{fresh}"));

            if (descriptions && index == 0 && listing.Overview is { Length: > 0 } overview)
            {
                facts.Add(new AiTextFact($"What this episode of {listing.Title} is about", Shorten(overview)));
            }
        }

        if (type is GeneratedPromoType.UpNext or GeneratedPromoType.NowPlaying)
        {
            facts.Add(new AiTextFact("Day", UpNextPromo.When(listings[0].Start, airsAt, zone, day: true, time: false)));
        }

        return facts;
    }

    private static bool Repeat(IReadOnlyList<UpNextListing> listings, UpNextListing listing) =>
        listings.TakeWhile(earlier => !ReferenceEquals(earlier, listing))
            .Any(earlier => string.Equals(earlier.Title, listing.Title, StringComparison.OrdinalIgnoreCase));

    private static string Seconds(TimeSpan time) => Math.Max(1, Math.Round(time.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Shorten(string text)
    {
        string flat = string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= OverviewLength)
        {
            return flat;
        }

        int cut = flat.LastIndexOf(". ", OverviewLength, StringComparison.Ordinal);
        return cut > OverviewLength / 2 ? flat[..(cut + 1)] : flat[..OverviewLength].TrimEnd() + "…";
    }
}
