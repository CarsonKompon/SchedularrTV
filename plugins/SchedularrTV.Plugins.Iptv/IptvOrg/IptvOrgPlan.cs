using System.Xml.Linq;

namespace SchedularrTV.Iptv.IptvOrg;

/// <summary>
/// One channel for iptv-org/epg to grab: the guide id it's written under (the playlist's <c>tvg-id</c>, so the guide
/// matches the playlist exactly) and the listings site it's read from.
/// </summary>
/// <param name="ByName">True when the playlist gave no <c>tvg-id</c> and the channel was found by its name.</param>
/// <param name="Sites">How many listings sites cover the channel (so how many there are to try).</param>
public sealed record GrabChannel(string XmltvId, IptvOrgGuideEntry Entry, string PlaylistName, bool ByName, int Sites = 1);

/// <summary>Works out what iptv-org/epg should grab for a provider's carried channels, and writes it as a channels file.</summary>
public static class IptvOrgPlan
{
    /// <summary>
    /// A playlist channel's iptv-org id and feed: from its <c>tvg-id</c> (<c>CNN.us</c>, or <c>CNN.us@East</c> with a
    /// feed), else by its name among iptv-org's channel names. Null when it can't be told.
    /// </summary>
    public static (string Channel, string? Feed, bool ByName)? Identify(PlaylistChannel channel, IReadOnlyDictionary<string, string> idsByName)
    {
        if (channel.TvgId is { Length: > 0 } tvgId)
        {
            int at = tvgId.IndexOf('@');
            return at > 0 ? (tvgId[..at], tvgId[(at + 1)..], false) : (tvgId, null, false);
        }

        return ByName(channel, idsByName) is { } id ? (id, null, true) : null;
    }

    /// <summary>The iptv-org id whose name (or other name) is the channel's, if any.</summary>
    public static string? ByName(PlaylistChannel channel, IReadOnlyDictionary<string, string> idsByName)
    {
        foreach (string? name in new[] { channel.TvgName, channel.Name, StripQuality(channel.Name) })
        {
            if (!string.IsNullOrWhiteSpace(name) && idsByName.TryGetValue(name.Trim(), out string? id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// One listings site per channel: the one for the channel's own feed first, then one for the channel as a whole,
    /// then any. A site that gave a channel nothing last time (<paramref name="failed"/>) is passed over until every
    /// site has been tried, then they're tried again from the top.
    /// </summary>
    public static List<GrabChannel> Plan(
        IEnumerable<PlaylistChannel> carried,
        IReadOnlyList<IptvOrgGuideEntry> guides,
        IReadOnlyDictionary<string, string> idsByName,
        IReadOnlyDictionary<string, HashSet<string>>? failed = null)
    {
        ILookup<string, IptvOrgGuideEntry> byChannel = guides.ToLookup(entry => entry.Channel, StringComparer.OrdinalIgnoreCase);
        List<GrabChannel> plan = [];
        HashSet<string> planned = new(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistChannel channel in carried)
        {
            if (Identify(channel, idsByName) is not { } identity)
            {
                continue;
            }

            string xmltvId = channel.TvgId is { Length: > 0 } given ? given : identity.Channel;
            if (!planned.Add(xmltvId))
            {
                continue;
            }

            List<IptvOrgGuideEntry> candidates = [.. byChannel[identity.Channel]
                .OrderBy(entry => string.Equals(entry.Feed, identity.Feed, StringComparison.OrdinalIgnoreCase) ? 0 : entry.Feed is null ? 1 : 2)];

            // A provider's own tvg-id that iptv-org doesn't use: try the channel's name instead (still listed under the tvg-id).
            if (candidates.Count == 0 && !identity.ByName && ByName(channel, idsByName) is { } named)
            {
                candidates = [.. byChannel[named].OrderBy(entry => entry.Feed is null ? 0 : 1)];
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            int sites = candidates.Count;
            if (failed?.TryGetValue(xmltvId, out HashSet<string>? tried) == true && candidates.Any(entry => !tried.Contains(entry.Site)))
            {
                candidates = [.. candidates.Where(entry => !tried.Contains(entry.Site))];
            }

            plan.Add(new GrabChannel(xmltvId, candidates[0], channel.Name, identity.ByName, sites));
        }

        return plan;
    }

    /// <summary>The plan as an iptv-org/epg channels file (<c>--channels</c>).</summary>
    public static string ChannelsXml(IEnumerable<GrabChannel> plan)
    {
        var root = new XElement("channels", plan.Select(channel => new XElement("channel",
            new XAttribute("site", channel.Entry.Site),
            new XAttribute("lang", channel.Entry.Lang),
            new XAttribute("xmltv_id", channel.XmltvId),
            new XAttribute("site_id", channel.Entry.SiteId),
            channel.Entry.SiteName)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Declaration + Environment.NewLine + root;
    }

    /// <summary>
    /// Notes how a channel's grab went: listings clear its record; none mark the site as tried, so the next grab uses
    /// another. Once all of the channel's sites have been tried the round starts again.
    /// </summary>
    public static void Remember(Dictionary<string, HashSet<string>> failed, GrabChannel channel, bool listed)
    {
        int sites = channel.Sites;
        if (listed)
        {
            failed.Remove(channel.XmltvId);
            return;
        }

        if (!failed.TryGetValue(channel.XmltvId, out HashSet<string>? tried))
        {
            failed[channel.XmltvId] = tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        tried.Add(channel.Entry.Site);
        if (tried.Count >= sites)
        {
            tried.Clear();
        }
    }

    // "CNN HD", "CNN (720p)", "CNN [Geo-blocked]": iptv-org's names leave these off.
    private static string? StripQuality(string name)
    {
        string trimmed = System.Text.RegularExpressions.Regex.Replace(name, @"\s*(\([^)]*\)|\[[^\]]*\])\s*", " ").Trim();
        trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+(HD|SD|FHD|UHD|4K)$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return trimmed.Length > 0 && trimmed != name ? trimmed : null;
    }
}
