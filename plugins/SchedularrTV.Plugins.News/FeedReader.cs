using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SchedularrTV.News;

/// <summary>A feed as read: its own title and its stories, newest first.</summary>
public sealed record FeedContent(string? Title, IReadOnlyList<Story> Stories);

/// <summary>
/// Reads RSS 2.0, RSS 1.0 (RDF) and Atom feeds into <see cref="Story">stories</see>: plain-text summaries (HTML taken
/// out), a picture from the enclosure, Media RSS or the first image in the text, and the date in any of the forms feeds
/// use. Element names are matched whatever their namespace, so the many variants in the wild all read.
/// </summary>
public static partial class FeedReader
{
    public static FeedContent Parse(string xml, string? label = null)
    {
        XDocument document;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using (var text = new StringReader(xml.TrimStart('﻿', ' ', '\r', '\n', '\t')))
        using (var reader = XmlReader.Create(text, settings))
        {
            document = XDocument.Load(reader);
        }

        XElement root = document.Root ?? throw new FormatException("The feed is empty.");
        bool atom = root.Name.LocalName == "feed";
        string? title = atom
            ? Text(Child(root, "title"))
            : Text(Child(Child(root, "channel"), "title"));
        string source = !string.IsNullOrWhiteSpace(label) ? label.Trim() : Clean(title ?? "") is { Length: > 0 } own ? own : "News";

        IEnumerable<XElement> entries = atom
            ? root.Elements().Where(element => element.Name.LocalName == "entry")
            : root.Descendants().Where(element => element.Name.LocalName == "item");

        List<Story> stories = [];
        foreach (XElement entry in entries)
        {
            Story? story = atom ? AtomEntry(entry, source) : RssItem(entry, source);
            if (story is not null)
            {
                stories.Add(story);
            }
        }

        return new FeedContent(title is null ? null : Clean(title), [.. stories.OrderByDescending(story => story.Published ?? DateTimeOffset.MinValue)]);
    }

    private static Story? RssItem(XElement item, string source)
    {
        string headline = Clean(Text(Child(item, "title")) ?? "");
        string html = Text(Child(item, "description")) ?? Text(Child(item, "encoded")) ?? "";
        if (headline.Length == 0)
        {
            headline = FirstSentence(Clean(html));
        }

        if (headline.Length == 0)
        {
            return null;
        }

        string? guid = Text(Child(item, "guid"));
        string? link = Text(Child(item, "link")) ?? (NewsShow.IsWebAddress(guid) ? guid : null);
        string? published = Text(Child(item, "pubDate")) ?? Text(Child(item, "date")) ?? Text(Child(item, "published")) ?? Text(Child(item, "updated"));
        return new Story(headline, Summary(html, headline), link?.Trim(), Image(item, html), source, ParseDate(published));
    }

    private static Story? AtomEntry(XElement entry, string source)
    {
        string headline = Clean(Text(Child(entry, "title")) ?? "");
        string html = Text(Child(entry, "summary")) ?? Text(Child(entry, "content")) ?? "";
        if (headline.Length == 0)
        {
            return null;
        }

        XElement? alternate = entry.Elements().Where(element => element.Name.LocalName == "link")
            .OrderBy(element => (string?)element.Attribute("rel") is null or "alternate" ? 0 : 1)
            .FirstOrDefault();
        string? published = Text(Child(entry, "published")) ?? Text(Child(entry, "updated"));
        return new Story(headline, Summary(html, headline), (string?)alternate?.Attribute("href"), Image(entry, html), source, ParseDate(published));
    }

    /// <summary>The summary as plain text, without a repeat of the headline at its start.</summary>
    private static string Summary(string html, string headline)
    {
        string text = Clean(html);
        if (text.StartsWith(headline, StringComparison.OrdinalIgnoreCase))
        {
            text = text[headline.Length..].TrimStart(' ', '.', ':', '-', '–', '—');
        }

        return text.Length > 2000 ? text[..2000] : text;
    }

    /// <summary>The story's picture: an image enclosure, Media RSS content or thumbnail, or the first img in its text.</summary>
    private static string? Image(XElement item, string html)
    {
        foreach (XElement element in item.Descendants())
        {
            string name = element.Name.LocalName;
            string? url = (string?)element.Attribute("url") ?? (string?)element.Attribute("href");
            string? type = (string?)element.Attribute("type");
            string? medium = (string?)element.Attribute("medium");
            bool picture = name switch
            {
                "enclosure" => type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
                "thumbnail" => true,
                "content" => medium == "image" || type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true || LooksLikeImage(url),
                "link" => (string?)element.Attribute("rel") == "enclosure" && type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
                _ => false
            };
            if (picture && NewsShow.IsWebAddress(url))
            {
                return url!.Trim();
            }
        }

        Match img = ImgTag().Match(WebUtility.HtmlDecode(html));
        return img.Success && NewsShow.IsWebAddress(img.Groups["src"].Value) ? img.Groups["src"].Value : null;
    }

    private static bool LooksLikeImage(string? url) =>
        url is not null && ImageExtension().IsMatch(url);

    /// <summary>HTML to plain text: tags out, entities decoded (twice, for feeds that escape them twice), spaces collapsed.</summary>
    public static string Clean(string html)
    {
        string text = WebUtility.HtmlDecode(html);
        text = Breaks().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ").Trim();
        return text;
    }

    /// <summary>Up to the end of the first sentence (or the first 160 characters).</summary>
    public static string FirstSentence(string text)
    {
        Match end = SentenceEnd().Match(text);
        string sentence = end.Success && end.Index < 200 ? text[..(end.Index + 1)] : text;
        return sentence.Length > 160 ? sentence[..157].TrimEnd() + "…" : sentence;
    }

    /// <summary>The first <paramref name="sentences"/> sentences, within <paramref name="limit"/> characters.</summary>
    public static string Sentences(string text, int sentences, int limit)
    {
        int at = 0;
        for (int count = 0; count < sentences; count++)
        {
            Match end = SentenceEnd().Match(text, at);
            if (!end.Success)
            {
                at = text.Length;
                break;
            }

            at = end.Index + 1;
        }

        string taken = text[..Math.Min(at == 0 ? text.Length : at, text.Length)].Trim();
        if (taken.Length <= limit)
        {
            return taken;
        }

        // Cut at the last word that fits.
        int space = taken.LastIndexOf(' ', limit - 1);
        return taken[..(space > limit / 2 ? space : limit - 1)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    /// <summary>A feed's date: RFC 822 (with zone names such as EST), ISO 8601, or whatever .NET can read; null if none.</summary>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset direct))
        {
            return direct;
        }

        // "Tue, 06 Oct 2026 12:00:00 EST" → "06 Oct 2026 12:00:00 -05:00".
        text = DayName().Replace(text, "");
        text = ZoneName().Replace(text, match => Zones.TryGetValue(match.Groups[1].Value.ToUpperInvariant(), out string? offset) ? " " + offset : match.Value);
        text = NumericZone().Replace(text, " ${sign}${hours}:${minutes}");
        string[] formats = ["d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz", "d MMMM yyyy HH:mm:ss zzz"];
        return DateTimeOffset.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset exact)
            ? exact
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset loose) ? loose : null;
    }

    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = "+00:00", ["UT"] = "+00:00", ["UTC"] = "+00:00", ["Z"] = "+00:00",
        ["EST"] = "-05:00", ["EDT"] = "-04:00", ["CST"] = "-06:00", ["CDT"] = "-05:00",
        ["MST"] = "-07:00", ["MDT"] = "-06:00", ["PST"] = "-08:00", ["PDT"] = "-07:00",
        ["AST"] = "-04:00", ["ADT"] = "-03:00", ["NST"] = "-03:30", ["NDT"] = "-02:30",
        ["BST"] = "+01:00", ["IST"] = "+01:00", ["CET"] = "+01:00", ["CEST"] = "+02:00",
        ["EET"] = "+02:00", ["EEST"] = "+03:00", ["AEST"] = "+10:00", ["AEDT"] = "+11:00"
    };

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string? Text(XElement? element) => element is null ? null : string.IsNullOrWhiteSpace(element.Value) ? null : element.Value;

    [GeneratedRegex(@"<img[^>]+src\s*=\s*[""'](?<src>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\.(jpe?g|png|webp|gif)(\?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ImageExtension();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^\s*[A-Za-z]{3,9},\s*")]
    private static partial Regex DayName();

    [GeneratedRegex(@"\s([A-Za-z]{1,4})\s*$")]
    private static partial Regex ZoneName();

    [GeneratedRegex(@"\s(?<sign>[+-])(?<hours>\d{2}):?(?<minutes>\d{2})\s*$")]
    private static partial Regex NumericZone();
}
