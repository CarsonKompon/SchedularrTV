using System.Globalization;
using System.Text.RegularExpressions;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>What a show folder's name says: <c>Show Name (2005) [tvdbid-12345]</c>.</summary>
internal sealed record FolderName(string Title, int? Year, IReadOnlyDictionary<string, string> Ids);

/// <summary>Where an episode file sits in its show, as its name says.</summary>
internal sealed record EpisodeName(int? Season, int? Episode, int? LastEpisode, DateOnly? Aired, string? Title);

/// <summary>What a movie file's (or folder's) name says: <c>Movie Title (2010) - 1080p</c>.</summary>
internal sealed record MovieName(string Title, int? Year, int? Part);

/// <summary>
/// The naming conventions shared by Sonarr, Radarr, Kodi, Jellyfin, Plex and ErsatzTV's local libraries: show folders
/// with season folders and <c>S01E02</c> episodes, movie folders named <c>Title (Year)</c>, and the extras folders and
/// suffixes they all leave out.
/// </summary>
internal static partial class LocalNaming
{
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>([".jpg", ".jpeg", ".png", ".webp", ".tbn"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders of extras (trailers, featurettes…) and housekeeping, never episodes or movies.</summary>
    private static readonly HashSet<string> ExtrasFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "extra", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts", "trailers",
        "trailer", "other", "others", "samples", "sample", "backdrops", "theme-music", "theme music", "clips", "metadata",
        ".actors", "@eadir", "#recycle", "$recycle.bin", "lost+found"
    };

    /// <summary>A folder whose contents are left out: extras, hidden folders, and any holding a <c>.ignore</c> file (as Jellyfin does).</summary>
    public static bool IsSkippedFolder(DirectoryInfo folder) =>
        ExtrasFolders.Contains(folder.Name) || folder.Name.StartsWith('.')
        || (folder.Attributes & FileAttributes.Hidden) != 0 && folder.Parent is not null
        || File.Exists(Path.Combine(folder.FullName, ".ignore"));

    /// <summary>A video that's an extra rather than an episode or the movie itself: <c>Movie-trailer.mkv</c>, <c>sample.mkv</c>.</summary>
    public static bool IsExtraFile(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Equals("sample", StringComparison.OrdinalIgnoreCase) || stem.Equals("trailer", StringComparison.OrdinalIgnoreCase)
               || ExtraSuffix().IsMatch(stem);
    }

    /// <summary><c>Season 1</c>, <c>Season 01</c>, <c>S01</c>, <c>Series 2</c>; <c>Specials</c> is season 0.</summary>
    public static int? SeasonFolder(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Equals("specials", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("special", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        Match match = SeasonFolderPattern().Match(trimmed);
        return match.Success && int.TryParse(match.Groups["s"].Value, CultureInfo.InvariantCulture, out int season) ? season : null;
    }

    /// <summary>A show or movie folder's name: its title, the year in brackets, and ids written in it (<c>[tvdbid-123]</c>, <c>{tmdb-456}</c>).</summary>
    public static FolderName Folder(string name)
    {
        Dictionary<string, string> ids = new(StringComparer.OrdinalIgnoreCase);
        string title = IdTag().Replace(name, match =>
        {
            ids[NormaliseIdKey(match.Groups["key"].Value)] = match.Groups["value"].Value.Trim();
            return " ";
        });

        title = Spaces().Replace(title, " ").Trim();
        int? year = null;
        Match yearMatch = TrailingYear().Match(title);
        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
            title = yearMatch.Groups["title"].Value.Trim();
        }

        return new FolderName(title.Length == 0 ? name.Trim() : title, year, ids);
    }

    /// <summary>
    /// Reads an episode file's name: <c>Show - S01E02 - Title</c>, <c>S01E02E03</c> or <c>S01E02-E03</c> (one file, two
    /// episodes), <c>1x02</c>, a date (<c>Show 2024-03-15</c>, for daily shows), or, inside a season folder, a leading
    /// number (<c>02 - Title</c>) or <c>Episode 2</c>.
    /// </summary>
    public static EpisodeName Episode(string fileName, bool inSeasonFolder)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);

        Match match = SeasonEpisode().Match(stem);
        if (!match.Success)
        {
            match = CrossEpisode().Match(stem);
        }

        if (match.Success)
        {
            int season = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
            int first = int.Parse(match.Groups["e"].Value, CultureInfo.InvariantCulture);
            int? last = match.Groups["x"].Captures.Count > 0
                ? int.Parse(match.Groups["x"].Captures[^1].Value, CultureInfo.InvariantCulture)
                : null;
            return new EpisodeName(season, first, last is { } end && end > first ? end : null, null, TitleAfter(stem, match));
        }

        Match date = AirDate().Match(stem);
        if (date.Success && DateOnly.TryParseExact($"{date.Groups["y"].Value}-{date.Groups["m"].Value}-{date.Groups["d"].Value}", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly aired))
        {
            return new EpisodeName(null, null, null, aired, TitleAfter(stem, date));
        }

        if (inSeasonFolder)
        {
            Match numbered = LeadingNumber().Match(stem);
            if (!numbered.Success)
            {
                numbered = EpisodeWord().Match(stem);
            }

            if (numbered.Success)
            {
                return new EpisodeName(null, int.Parse(numbered.Groups["e"].Value, CultureInfo.InvariantCulture), null, null, TitleAfter(stem, numbered));
            }
        }

        return new EpisodeName(null, null, null, null, null);
    }

    /// <summary>A movie's title and year from a file or folder name (<c>Movie.Title.2010.1080p.BluRay</c>, <c>Movie Title (2010) - 4K</c>), and a part number (<c>cd1</c>, <c>part2</c>).</summary>
    public static MovieName Movie(string name)
    {
        string stem = name;
        int? part = null;
        Match partMatch = PartSuffix().Match(stem);
        if (partMatch.Success)
        {
            part = int.Parse(partMatch.Groups["n"].Value, CultureInfo.InvariantCulture);
            stem = stem[..partMatch.Index];
        }

        FolderName folder = Folder(stem);
        if (folder.Year is not null)
        {
            // "Title (2010)" whole, or with a version after it: "Title (2010) - 1080p".
            return new MovieName(Clean(folder.Title), folder.Year, part);
        }

        Match match = MovieYear().Match(stem);
        if (match.Success)
        {
            return new MovieName(Clean(match.Groups["title"].Value), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture), part);
        }

        return new MovieName(Clean(StripQuality(stem)), null, part);
    }

    /// <summary>The same movie however its file is named, for telling its versions and parts from other movies in a folder.</summary>
    public static string MovieKey(MovieName name) => $"{name.Title.ToLowerInvariant()}|{name.Year}";

    /// <summary>"Rated PG-13", "US:TV-PG", "US:PG-13 / GB:12A" → "PG-13", "TV-PG", "PG-13".</summary>
    public static string? Rating(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string first = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? raw;
        if (first.StartsWith("rated ", StringComparison.OrdinalIgnoreCase))
        {
            first = first[6..];
        }

        int colon = first.IndexOf(':');
        if (colon >= 0 && colon <= 3)
        {
            first = first[(colon + 1)..];
        }

        first = first.Trim();
        return first.Length == 0 ? null : first;
    }

    public static string NormaliseIdKey(string key) => key.ToLowerInvariant() switch
    {
        "tvdb" or "tvdbid" => "tvdb_id",
        "tmdb" or "tmdbid" => "tmdb_id",
        "imdb" or "imdbid" => "imdb_id",
        string other => other.EndsWith("_id", StringComparison.Ordinal) ? other : other + "_id"
    };

    private static string? TitleAfter(string stem, Match match)
    {
        string rest = stem[(match.Index + match.Length)..].Trim(' ', '-', '.', '_', '–');
        rest = Clean(StripQuality(rest));
        return rest.Length == 0 ? null : rest;
    }

    private static string StripQuality(string text)
    {
        Match quality = QualityTag().Match(text);
        return (quality.Success ? text[..quality.Index] : text).Trim(' ', '-', '.', '_', '[', '(');
    }

    /// <summary>Dots and underscores as spaces when a name has no spaces of its own (<c>The.Movie.Title</c>).</summary>
    private static string Clean(string text)
    {
        string cleaned = text.Contains(' ') ? text : text.Replace('.', ' ').Replace('_', ' ');
        return Spaces().Replace(cleaned, " ").Trim(' ', '-', '.', '_');
    }

    [GeneratedRegex(@"-(?:trailer|sample|featurette|behindthescenes|deleted|deletedscene|interview|scene|short|other|extra|clip)\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraSuffix();

    [GeneratedRegex(@"^(?:season|series|s)[ ._-]*(?<s>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();

    [GeneratedRegex(@"[\[\{]\s*(?<key>tvdbid|tvdb|tmdbid|tmdb|imdbid|imdb)\s*[-=]\s*(?<value>[^\]\}]+)[\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex IdTag();

    [GeneratedRegex(@"^(?<title>.*?)\s*\((?<year>(?:18|19|20)\d{2})\)(?:\s*-\s*.*)?$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"(?<![a-z0-9])S(?<s>\d{1,4})[ ._]?E(?<e>\d{1,4})(?:[ ._]?-?[ ._]?E(?<x>\d{1,4})|-(?<x>\d{1,3})(?![\dpPiIkK]))*", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?:-x?(?<x>\d{2,3}))?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex CrossEpisode();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[-. _](?<m>0[1-9]|1[0-2])[-. _](?<d>0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex AirDate();

    [GeneratedRegex(@"^(?<e>\d{1,3})(?:\s*[-._]\s*|\s+|$)")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"(?<![a-z])(?:episode|ep|e)[ ._]?(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWord();

    [GeneratedRegex(@"^(?<title>.+?)[ ._\[\(]+(?<year>(?:18|19|20)\d{2})(?:[\]\)]|(?=[ ._-])|$)")]
    private static partial Regex MovieYear();

    [GeneratedRegex(@"[ ._-]*(?:cd|dvd|part|pt|disc|disk)[ ._-]*(?<n>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex PartSuffix();

    [GeneratedRegex(@"[ ._\[\(-]*\b(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|web[ ._-]?dl|webrip|bluray|blu-ray|bdrip|brrip|hdtv|dvdrip|remux|x264|x265|h[ .]?264|h[ .]?265|hevc|aac|ac3|eac3|dts|truehd|atmos|proper|repack|hdr|sdr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
