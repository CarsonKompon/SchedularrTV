using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>
/// One <c>&lt;tvshow&gt;</c>, <c>&lt;episodedetails&gt;</c> or <c>&lt;movie&gt;</c> from a Kodi-style .nfo file, as written
/// by Kodi, Jellyfin, Emby, Sonarr, Radarr, tinyMediaManager and the like. Anything missing is null or empty.
/// </summary>
internal sealed record NfoInfo
{
    /// <summary>The element it came from: <c>tvshow</c>, <c>episodedetails</c>, <c>movie</c>…</summary>
    public required string Kind { get; init; }

    public string? Title { get; init; }
    public string? SortTitle { get; init; }
    public int? Year { get; init; }
    public DateOnly? Premiered { get; init; }
    public string? Plot { get; init; }
    public string? Rating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Studios { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The movie sets (collections) it belongs to.</summary>
    public IReadOnlyList<string> Sets { get; init; } = [];

    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>The running time: the stream details' exact seconds, else the runtime in minutes.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Ids at other databases: <c>tvdb_id</c>, <c>tmdb_id</c>, <c>imdb_id</c>…</summary>
    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();
}

/// <summary>Reads Kodi-style .nfo files, forgivingly: a link left after the XML, several episodes in one file, or no XML at all.</summary>
internal static partial class NfoReader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        // Several root elements (one per episode in a multi-episode file) and stray text are allowed.
        ConformanceLevel = ConformanceLevel.Fragment,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false
    };

    /// <summary>Every recognised element in the file; empty when there's none or it can't be read.</summary>
    public static IReadOnlyList<NfoInfo> Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The first element of a kind in the file, or null.</summary>
    public static NfoInfo? ReadOne(string path, params string[] kinds) =>
        Read(path).FirstOrDefault(info => kinds.Contains(info.Kind, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<NfoInfo> Parse(string text)
    {
        // The declaration is only allowed at the very start of a document, not of a fragment.
        text = Declaration().Replace(text, string.Empty);
        List<NfoInfo> found = [];
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (XNode.ReadFrom(reader) is XElement element && From(element) is { } info)
                    {
                        found.Add(info);
                    }
                }
                else if (!reader.Read())
                {
                    break;
                }
            }
        }
        catch (XmlException)
        {
            // What was read before the fault still counts (a broken second episode, say).
        }

        return found;
    }

    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "tvshow", "episodedetails", "movie", "musicvideo"
    };

    private static NfoInfo? From(XElement element)
    {
        string kind = element.Name.LocalName;
        if (!Kinds.Contains(kind))
        {
            return null;
        }

        DateOnly? premiered = Date(Text(element, "premiered")) ?? Date(Text(element, "aired")) ?? Date(Text(element, "releasedate"));
        int? year = Number(Text(element, "year")) is { } y and >= 1800 and <= 2200 ? y : premiered?.Year;

        Dictionary<string, string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement unique in element.Elements("uniqueid"))
        {
            if (unique.Attribute("type")?.Value is { Length: > 0 } type && unique.Value.Trim() is { Length: > 0 } value)
            {
                ids[LocalNaming.NormaliseIdKey(type)] = value;
            }
        }

        foreach ((string name, string key) in new[] { ("imdbid", "imdb_id"), ("imdb_id", "imdb_id"), ("tmdbid", "tmdb_id"), ("tvdbid", "tvdb_id") })
        {
            if (Text(element, name) is { } value && !ids.ContainsKey(key))
            {
                ids[key] = value;
            }
        }

        return new NfoInfo
        {
            Kind = kind.ToLowerInvariant(),
            Title = Text(element, "title"),
            SortTitle = Text(element, "sorttitle"),
            Year = year,
            Premiered = premiered,
            Plot = Text(element, "plot") ?? Text(element, "outline"),
            Rating = LocalNaming.Rating(Text(element, "mpaa") ?? Text(element, "certification")),
            Genres = List(element, "genre"),
            Studios = List(element, "studio"),
            Tags = List(element, "tag"),
            Sets = [.. element.Elements("set")
                .Select(set => (set.Element("name")?.Value ?? (set.HasElements ? null : set.Value))?.Trim())
                .OfType<string>().Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            Season = Number(Text(element, "season")),
            Episode = Number(Text(element, "episode")),
            Duration = Duration(element),
            Ids = ids
        };
    }

    private static TimeSpan? Duration(XElement element)
    {
        string? seconds = element.Element("fileinfo")?.Element("streamdetails")?.Element("video")?.Element("durationinseconds")?.Value;
        if (double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double exact) && exact > 0)
        {
            return TimeSpan.FromSeconds(exact);
        }

        return Number(Text(element, "runtime")) is { } minutes and > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }

    private static string? Text(XElement element, string name) =>
        element.Element(name)?.Value.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>Every value of a repeated element; older writers put several in one, split by " / ".</summary>
    private static IReadOnlyList<string> List(XElement element, string name) =>
        [.. element.Elements(name)
            .SelectMany(item => item.Value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static DateOnly? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text.Length >= 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly exact))
        {
            return exact;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime loose) && loose.Year > 1800
            ? DateOnly.FromDateTime(loose)
            : null;
    }

    [GeneratedRegex(@"^﻿?\s*<\?xml[^>]*\?>")]
    private static partial Regex Declaration();
}
