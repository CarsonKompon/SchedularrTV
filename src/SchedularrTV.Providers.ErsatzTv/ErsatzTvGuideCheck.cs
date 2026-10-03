using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>What ErsatzTV's guide holds for one channel.</summary>
public sealed record GuideChannelCheck(string TvgId, string Name, int Listings, bool OnNow, DateTimeOffset? Until);

/// <summary>The outcome of asking ErsatzTV for its playlist and guide, as the IPTV player (Jellyfin) would.</summary>
public sealed record GuideCheckResult(bool Ok, string Summary, IReadOnlyList<GuideChannelCheck> Channels, IReadOnlyList<string> Advice);

/// <summary>
/// Reads ErsatzTV Next's <c>/channels.m3u</c> and <c>/xmltv.xml</c> and compares them with the guide files written for
/// it, to explain an empty guide: ErsatzTV reading another lineup, channels whose ids don't match, or a player that
/// simply hasn't refreshed.
/// </summary>
public static partial class ErsatzTvGuideCheck
{
    [GeneratedRegex("tvg-id=\"([^\"]*)\"")]
    private static partial Regex TvgId();

    public static async Task<GuideCheckResult> RunAsync(HttpClient http, string baseUrl, string ourXmltvFolder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string root = baseUrl.TrimEnd('/');
        string playlist;
        string guide;
        try
        {
            playlist = await http.GetStringAsync($"{root}/channels.m3u", cancellationToken).ConfigureAwait(false);
            guide = await http.GetStringAsync($"{root}/xmltv.xml", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new GuideCheckResult(false, $"ErsatzTV didn't answer at {root}: {ex.Message}", [],
                ["Check that ErsatzTV is running and that the streaming port matches."]);
        }

        List<string> playlistIds = [.. TvgId().Matches(playlist).Select(match => match.Groups[1].Value)];
        XDocument document;
        try
        {
            document = XDocument.Parse(guide);
        }
        catch (System.Xml.XmlException ex)
        {
            return new GuideCheckResult(false, $"ErsatzTV's guide isn't valid XML: {ex.Message}", [], []);
        }

        Dictionary<string, string> names = document.Root?.Elements("channel")
            .Where(channel => channel.Attribute("id") is not null)
            .GroupBy(channel => (string)channel.Attribute("id")!)
            .ToDictionary(group => group.Key, group => group.First().Elements("display-name").LastOrDefault()?.Value ?? group.Key)
            ?? new Dictionary<string, string>();

        List<XElement> programmes = [.. document.Root?.Elements("programme") ?? []];
        List<GuideChannelCheck> channels = [];
        foreach (string id in playlistIds.Count > 0 ? playlistIds : [.. names.Keys])
        {
            List<(DateTimeOffset Start, DateTimeOffset Stop)> listings = [.. programmes
                .Where(programme => (string?)programme.Attribute("channel") == id)
                .Select(programme => (Parse((string?)programme.Attribute("start")), Parse((string?)programme.Attribute("stop"))))
                .Where(times => times.Item1 is not null && times.Item2 is not null)
                .Select(times => (times.Item1!.Value, times.Item2!.Value))];
            channels.Add(new GuideChannelCheck(
                id,
                names.GetValueOrDefault(id, id),
                listings.Count,
                listings.Any(listing => listing.Start <= now && listing.Stop > now),
                listings.Count > 0 ? listings.Max(listing => listing.Stop) : null));
        }

        List<string> advice = [];
        string[] ourFiles = Directory.Exists(ourXmltvFolder)
            ? [.. Directory.EnumerateFiles(ourXmltvFolder, "*.xml").Select(path => Path.GetFileNameWithoutExtension(path)!)]
            : [];

        if (channels.Count == 0)
        {
            return new GuideCheckResult(false, "ErsatzTV's playlist has no channels.", channels,
                ["Rebuild the schedule so the lineup is written, then restart ErsatzTV (it reads its channels once at startup)."]);
        }

        List<GuideChannelCheck> empty = [.. channels.Where(channel => channel.Listings == 0)];
        if (empty.Count == channels.Count)
        {
            if (ourFiles.Length > 0 && !channels.Any(channel => ourFiles.Contains(channel.TvgId, StringComparer.OrdinalIgnoreCase)))
            {
                advice.Add($"None of ErsatzTV's channel ids ({string.Join(", ", channels.Select(channel => channel.TvgId))}) match the guide files written for it "
                           + $"({string.Join(", ", ourFiles)}). ErsatzTV is probably running with a different lineup: set the lineup folder to the one it uses, "
                           + "or let SchedularrTV start ErsatzTV.");
            }
            else if (ourFiles.Length > 0)
            {
                advice.Add($"The guide files are in {ourXmltvFolder}, but ErsatzTV isn't reading them. ErsatzTV is probably running with a different lineup "
                           + "(one whose xmltv folder is elsewhere): set the lineup folder to the one it uses, or let SchedularrTV start ErsatzTV.");
            }
            else
            {
                advice.Add("No guide files have been written yet. Rebuild the schedule from the Dashboard.");
            }

            return new GuideCheckResult(false, "ErsatzTV's guide has no listings.", channels, advice);
        }

        if (empty.Count > 0)
        {
            advice.Add($"No listings for {string.Join(", ", empty.Select(channel => channel.Name))}. Rebuild the schedule; if they stay empty, restart ErsatzTV so it picks up the current channel list.");
        }

        advice.Add("ErsatzTV is serving the guide. If Jellyfin still shows none: in Jellyfin's Dashboard → Live TV, check the XMLTV guide uses "
                   + $"{root}/xmltv.xml and that the channels are mapped to it, then run Dashboard → Scheduled Tasks → Refresh Guide.");

        int total = channels.Sum(channel => channel.Listings);
        return new GuideCheckResult(empty.Count == 0, $"ErsatzTV's guide has {total} listing(s) across {channels.Count - empty.Count} of {channels.Count} channel(s).", channels, advice);
    }

    /// <summary>XMLTV times: <c>20261003120000 -0400</c>.</summary>
    internal static DateTimeOffset? Parse(string? value)
    {
        string text = value?.Trim() ?? string.Empty;

        // "-0400" becomes "-04:00", which the offset pattern reads.
        if (text.Length == 20 && text[14] == ' ' && text[15] is '+' or '-')
        {
            text = $"{text[..18]}:{text[18..]}";
        }

        return DateTimeOffset.TryParseExact(text, ["yyyyMMddHHmmss zzz", "yyyyMMddHHmmss"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
