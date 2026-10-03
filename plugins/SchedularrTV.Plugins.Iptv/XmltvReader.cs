using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SchedularrTV.Iptv;

/// <summary>One listing from a provider's guide, with the element as the provider wrote it.</summary>
public sealed record GuideProgramme(DateTimeOffset Start, DateTimeOffset Stop, string Title, string? SubTitle, string? Description, string Xml);

/// <summary>What was kept of a provider's guide: every channel's names, and the listings of the channels wanted.</summary>
public sealed class XmltvGuide
{
    /// <summary>Guide channel id → its display names.</summary>
    public Dictionary<string, List<string>> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guide channel id → its listings, in time order.</summary>
    public Dictionary<string, List<GuideProgramme>> Programmes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The guide id for a playlist channel: its own <c>tvg-id</c> when the guide has it, else a guide channel whose
    /// display name is the channel's <c>tvg-name</c> or name. Null when nothing matches.
    /// </summary>
    public string? Match(PlaylistChannel channel)
    {
        if (channel.TvgId is { } id && Channels.ContainsKey(id))
        {
            return id;
        }

        foreach (string? name in new[] { channel.TvgName, channel.Name, channel.TvgId })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foreach ((string guideId, List<string> names) in Channels)
            {
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    return guideId;
                }
            }
        }

        // A guide with listings but no <channel> entries (it happens): trust the tvg-id.
        return channel.TvgId is { } listed && Programmes.ContainsKey(listed) ? listed : null;
    }

    /// <summary>
    /// Two guides as one: <paramref name="preferred"/>'s listings where it has them, <paramref name="other"/>'s for the
    /// rest. Either may be null; null when both are.
    /// </summary>
    public static XmltvGuide? Merge(XmltvGuide? preferred, XmltvGuide? other)
    {
        if (preferred is null || other is null)
        {
            return preferred ?? other;
        }

        var merged = new XmltvGuide();
        foreach (XmltvGuide guide in new[] { preferred, other })
        {
            foreach ((string id, List<string> names) in guide.Channels)
            {
                if (merged.Channels.TryGetValue(id, out List<string>? known))
                {
                    known.AddRange(names.Where(name => !known.Contains(name, StringComparer.OrdinalIgnoreCase)));
                }
                else
                {
                    merged.Channels[id] = [.. names];
                }
            }

            foreach ((string id, List<GuideProgramme> listings) in guide.Programmes)
            {
                merged.Programmes.TryAdd(id, listings);
            }
        }

        return merged;
    }
}

/// <summary>
/// Reads XMLTV guides, which can run to hundreds of megabytes, a listing at a time, keeping only the channels and
/// times asked for.
/// </summary>
public static class XmltvReader
{
    /// <param name="wanted">
    /// Given every guide channel read so far (id → display names) when the first listing is reached, returns the ids
    /// whose listings to keep.
    /// </param>
    public static XmltvGuide Read(Stream stream, Func<XmltvGuide, ISet<string>> wanted, DateTimeOffset from, DateTimeOffset to)
    {
        var guide = new XmltvGuide();
        ISet<string>? keep = null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false
        };

        using XmlReader reader = XmlReader.Create(stream, settings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
            {
                reader.Read();
                continue;
            }

            if (reader.LocalName == "channel")
            {
                if (XNode.ReadFrom(reader) is XElement channel && (string?)channel.Attribute("id") is { Length: > 0 } id)
                {
                    if (!guide.Channels.TryGetValue(id, out List<string>? names))
                    {
                        guide.Channels[id] = names = [];
                    }

                    names.AddRange(channel.Elements("display-name").Select(name => name.Value.Trim()).Where(name => name.Length > 0));
                }

                continue;
            }

            if (reader.LocalName == "programme")
            {
                keep ??= wanted(guide);
                string? channel = reader.GetAttribute("channel");
                if (channel is null || !keep.Contains(channel))
                {
                    reader.Skip();
                    continue;
                }

                if (XNode.ReadFrom(reader) is XElement element && Programme(element, from, to) is { } programme)
                {
                    if (!guide.Programmes.TryGetValue(channel, out List<GuideProgramme>? list))
                    {
                        guide.Programmes[channel] = list = [];
                    }

                    list.Add(programme);
                }

                continue;
            }

            reader.Skip();
        }

        foreach (List<GuideProgramme> list in guide.Programmes.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return guide;
    }

    private static GuideProgramme? Programme(XElement element, DateTimeOffset from, DateTimeOffset to)
    {
        if (ParseTime((string?)element.Attribute("start")) is not { } start)
        {
            return null;
        }

        // A listing with no stop runs until the next one; it's given an hour here and trimmed when the schedule is made.
        DateTimeOffset stop = ParseTime((string?)element.Attribute("stop")) ?? start.AddHours(1);
        if (stop <= start || stop <= from || start >= to)
        {
            return null;
        }

        string title = element.Elements("title").FirstOrDefault()?.Value.Trim() is { Length: > 0 } named ? named : "Untitled";
        return new GuideProgramme(
            start,
            stop,
            title,
            element.Elements("sub-title").FirstOrDefault()?.Value.Trim(),
            element.Elements("desc").FirstOrDefault()?.Value.Trim(),
            element.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>XMLTV times: <c>20261003120000 -0400</c>, also without seconds, without an offset (UTC), or <c>+04:00</c>.</summary>
    public static DateTimeOffset? ParseTime(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits < 12)
        {
            return null;
        }

        string stamp = text[..Math.Min(digits, 14)].PadRight(14, '0');
        if (!DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return null;
        }

        TimeSpan offset = TimeSpan.Zero;
        string zone = text[digits..].Trim().Replace(":", string.Empty, StringComparison.Ordinal);
        if (zone.Length == 5 && zone[0] is '+' or '-'
            && int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            && int.TryParse(zone.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            offset = new TimeSpan(hours, minutes, 0);
            if (zone[0] == '-')
            {
                offset = -offset;
            }
        }

        try
        {
            return new DateTimeOffset(local, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
