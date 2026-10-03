using System.Text;

namespace SchedularrTV.Iptv;

/// <summary>One IPTV provider: a playlist, its guide, and which of its channels to carry under which numbers.</summary>
public sealed class IptvSource
{
    /// <summary>Stable id, used in channel ids, guide ids and stream addresses; never changes once created.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>The M3U playlist: a web address or a file on this computer.</summary>
    public string PlaylistUrl { get; set; } = string.Empty;

    /// <summary>The XMLTV guide (.xml or .xml.gz), a web address or a file; empty to use the one the playlist names.</summary>
    public string? GuideUrl { get; set; }

    /// <summary>
    /// Makes the guide with iptv-org/epg: listings grabbed from the sites iptv-org knows for each carried channel. A guide
    /// address set here still fills in channels iptv-org can't; the one the playlist names is then not used.
    /// </summary>
    public bool IptvOrgGuide { get; set; }

    /// <summary>Sent to the provider for the playlist, guide and streams; some insist on a particular one.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Copy the provider's video and audio as they are (normally fine, and nearly free) rather than re-encode them to
    /// the channel's settings. ErsatzTV still re-encodes anything it can't copy.
    /// </summary>
    public bool Passthrough { get; set; } = true;

    /// <summary>The first number given to this provider's channels; the next free one is taken from there.</summary>
    public int FirstNumber { get; set; } = 1000;

    /// <summary>The guide group (category) for its channels; empty keeps the playlist's own groups.</summary>
    public string? Group { get; set; }

    /// <summary>Carry every channel in the playlist, rather than only <see cref="Selected"/>.</summary>
    public bool IncludeAll { get; set; }

    /// <summary>The channels to carry, by <see cref="PlaylistChannel.Key"/>.</summary>
    public List<string> Selected { get; set; } = [];

    /// <summary>
    /// The number each channel was given, by key. Kept once given, so a channel keeps its number (and its place in
    /// Jellyfin) when the provider reorders or adds to the playlist.
    /// </summary>
    public Dictionary<string, int> Numbers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The guide to read: the one set here, else the first the playlist names.</summary>
    public string? EffectiveGuideUrl(IReadOnlyList<string> playlistGuides) =>
        !string.IsNullOrWhiteSpace(GuideUrl) ? GuideUrl.Trim() : playlistGuides.FirstOrDefault();

    public bool Includes(string key) => IncludeAll || Selected.Contains(key, StringComparer.Ordinal);

    public IptvSource Copy()
    {
        var copy = (IptvSource)MemberwiseClone();
        copy.Selected = [.. Selected];
        copy.Numbers = new Dictionary<string, int>(Numbers, StringComparer.Ordinal);
        return copy;
    }

    /// <summary>A safe, lowercase id from a name: letters, digits and dashes, at most <paramref name="max"/> long.</summary>
    public static string Slug(string name, int max = 40)
    {
        var slug = new StringBuilder();
        foreach (char character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string result = slug.ToString().Trim('-');
        return result.Length == 0 ? "iptv" : result.Length > max ? result[..max].TrimEnd('-') : result;
    }
}
