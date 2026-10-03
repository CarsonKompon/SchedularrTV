using System.Globalization;
using System.Text.RegularExpressions;

namespace SchedularrTV.Iptv;

/// <summary>One channel in an IPTV playlist.</summary>
/// <param name="Key">Identifies the channel within its playlist, stably across refreshes: its guide id, else its name.</param>
public sealed record PlaylistChannel(
    string Key,
    string Name,
    string Url,
    string? TvgId = null,
    string? TvgName = null,
    string? Logo = null,
    string? Group = null,
    int? Number = null,
    string? UserAgent = null,
    string? Referrer = null);

/// <summary>A parsed playlist: its channels, and the guide addresses its header names (<c>url-tvg</c>, <c>x-tvg-url</c>).</summary>
public sealed record Playlist(IReadOnlyList<PlaylistChannel> Channels, IReadOnlyList<string> GuideUrls);

/// <summary>Reads extended M3U playlists as IPTV providers write them.</summary>
public static partial class M3uParser
{
    [GeneratedRegex("""([A-Za-z0-9_-]+)\s*=\s*"([^"]*)"|([A-Za-z0-9_-]+)\s*=\s*([^\s,"]+)""")]
    private static partial Regex Attribute();

    public static Playlist Parse(string text)
    {
        List<PlaylistChannel> channels = [];
        List<string> guides = [];
        Dictionary<string, string>? pending = null;
        string? pendingName = null;
        string? group = null;
        string? userAgent = null;
        string? referrer = null;
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, string> header = Attributes(line);
                foreach (string name in new[] { "url-tvg", "x-tvg-url", "tvg-url" })
                {
                    if (header.TryGetValue(name, out string? value))
                    {
                        // Several guides may be listed, comma separated.
                        guides.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                }

                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                (string attributes, string name) = SplitInfo(line);
                pending = Attributes(attributes);
                pendingName = name;
                group = null;
                userAgent = null;
                referrer = null;
                continue;
            }

            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                group = line[8..].Trim();
                continue;
            }

            if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                string option = line[11..].Trim();
                if (option.StartsWith("http-user-agent=", StringComparison.OrdinalIgnoreCase))
                {
                    userAgent = option[16..].Trim();
                }
                else if (option.StartsWith("http-referrer=", StringComparison.OrdinalIgnoreCase))
                {
                    referrer = option[14..].Trim();
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            // A stream address, which belongs to the #EXTINF before it.
            if (pending is null)
            {
                continue;
            }

            string? tvgId = Value(pending, "tvg-id");
            string? tvgName = Value(pending, "tvg-name");
            string display = !string.IsNullOrWhiteSpace(pendingName) ? pendingName : tvgName ?? tvgId ?? line;
            string baseKey = tvgId ?? display;
            string key = baseKey;
            for (int suffix = 2; !keys.Add(key); suffix++)
            {
                // The same guide id for several streams (HD and SD, say): each gets its own key, in playlist order.
                key = $"{baseKey}#{suffix}";
            }

            channels.Add(new PlaylistChannel(
                key,
                display,
                line,
                tvgId,
                tvgName,
                Value(pending, "tvg-logo") ?? Value(pending, "logo"),
                Value(pending, "group-title") ?? group,
                int.TryParse(Value(pending, "tvg-chno") ?? Value(pending, "channel-number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null,
                Value(pending, "http-user-agent") ?? Value(pending, "user-agent") ?? userAgent,
                Value(pending, "http-referrer") ?? referrer));

            pending = null;
            pendingName = null;
        }

        return new Playlist(channels, [.. guides.Distinct(StringComparer.Ordinal)]);
    }

    private static string? Value(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static Dictionary<string, string> Attributes(string text)
    {
        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attribute().Matches(text))
        {
            string name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            string value = match.Groups[1].Success ? match.Groups[2].Value : match.Groups[4].Value;
            attributes.TryAdd(name, value);
        }

        return attributes;
    }

    /// <summary>
    /// Splits <c>#EXTINF:-1 tvg-id="a,b" group-title="News",Channel Name</c> at the comma that ends the attributes:
    /// the first one outside quotes.
    /// </summary>
    private static (string Attributes, string Name) SplitInfo(string line)
    {
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                return (line[..index], line[(index + 1)..].Trim());
            }
        }

        return (line, string.Empty);
    }
}
