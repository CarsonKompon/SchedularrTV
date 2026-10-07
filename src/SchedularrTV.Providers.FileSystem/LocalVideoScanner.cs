using System.Globalization;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>The pictures found for an item or series: its poster (or an episode's still), a landscape thumb, and fanart.</summary>
public sealed record LocalImages(string? Primary, string? Thumb, string? Backdrop)
{
    public string? For(string type) =>
        type.Equals(ArtworkTypes.Thumb, StringComparison.OrdinalIgnoreCase) ? Thumb
        : type.Equals(ArtworkTypes.Backdrop, StringComparison.OrdinalIgnoreCase) ? Backdrop
        : Primary;

    public bool IsEmpty => Primary is null && Thumb is null && Backdrop is null;
}

/// <summary>The ids of local TV shows, episodes and movies, which (unlike commercials' <c>fs:</c> ids) can have pictures.</summary>
public static class LocalLibraryIds
{
    /// <summary>Episodes and movies, by their file.</summary>
    public const string ItemPrefix = "local:";

    /// <summary>Shows, by their folder.</summary>
    public const string SeriesPrefix = "local-show:";

    /// <summary>A movie's versions, by its folder.</summary>
    public const string MoviePrefix = "local-movie:";

    public static bool IsLocalId(string? id) =>
        id is not null && (id.StartsWith(ItemPrefix, StringComparison.Ordinal) || id.StartsWith(SeriesPrefix, StringComparison.Ordinal));
}

/// <summary>A local show's extra (a theme, trailer, featurette…), for its intros and outros.</summary>
public sealed record LocalExtra(string Name, string Path, TimeSpan Duration);

/// <summary>What a TV or movie folder holds: its items, the pictures for them (and for each show) by id, and each show's folder by id.</summary>
internal sealed record LocalScan(IReadOnlyList<MediaItem> Items, IReadOnlyDictionary<string, LocalImages> Images, IReadOnlyDictionary<string, string> ShowFolders)
{
    public static readonly LocalScan Empty = new([], new Dictionary<string, LocalImages>(), new Dictionary<string, string>());
}

/// <summary>
/// Reads a folder of TV shows or movies laid out the way Sonarr, Radarr, Kodi, Jellyfin and ErsatzTV expect:
/// <code>
/// TV/Show Name (2005)/tvshow.nfo, poster.jpg, fanart.jpg
/// TV/Show Name (2005)/Season 01/Show Name - S01E02 - Title.mkv (+ .nfo, -thumb.jpg)
/// Movies/Movie Title (2010)/Movie Title (2010).mkv (+ movie.nfo or Movie Title (2010).nfo, poster.jpg, fanart.jpg)
/// </code>
/// Metadata comes from .nfo files where there are any, else from the names; folders between the root and a show or
/// movie folder (<c>TV/Cartoons/Show</c>) become tags, as they do for commercials.
/// </summary>
internal sealed class LocalVideoScanner(FileSystemLibraryOptions options, MediaProbe probe, ILogger logger)
{
    private const string ItemPrefix = LocalLibraryIds.ItemPrefix;
    private const string SeriesPrefix = LocalLibraryIds.SeriesPrefix;
    private const string MoviePrefix = LocalLibraryIds.MoviePrefix;

    public static string Id(string prefix, string path) => $"{prefix}{DeterministicRandom.Hash(path):x16}";

    private static readonly string[] ShowPosters = ["poster", "folder", "cover", "show", "default"];
    private static readonly string[] Fanart = ["fanart", "backdrop", "background", "art"];
    private static readonly string[] Landscapes = ["landscape", "thumb"];
    private static readonly string[] MoviePosters = ["poster", "folder", "cover", "movie", "default"];

    private HashSet<string> _extensions = [];
    private readonly Dictionary<string, string> _showFolders = new(StringComparer.Ordinal);

    /// <summary>A file found, and how to describe it once it's been probed (null leaves it out).</summary>
    private sealed record Candidate(FileInfo File, Func<ProbeResult?, (MediaItem Item, LocalImages Images)?> Build);

    public async Task<LocalScan> ScanAsync(MediaFolderOptions folder, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(folder.Path);
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Media folder not found, skipping: {Path}", root);
            return LocalScan.Empty;
        }

        _extensions = new HashSet<string>(options.Extensions, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LocalImages> images = new(StringComparer.Ordinal);
        List<Candidate> candidates = [];
        var rootInfo = new DirectoryInfo(root);

        if (folder.Kind == MediaKind.Movie)
        {
            FindMovies(rootInfo, rootInfo, [], folder, candidates);
        }
        else
        {
            FindShows(rootInfo, rootInfo, [], folder, candidates, images);
        }

        if (candidates.Count == 0)
        {
            logger.LogWarning("No {Kind} found under {Path}.", folder.Kind == MediaKind.Movie ? "movies" : "shows", root);
            return new LocalScan([], images, _showFolders);
        }

        IReadOnlyDictionary<string, ProbeResult> probed = await probe.ProbeAllAsync([.. candidates.Select(c => c.File)], chapters: true, cancellationToken)
            .ConfigureAwait(false);

        List<MediaItem> items = new(candidates.Count);
        int skipped = 0;
        foreach (Candidate candidate in candidates)
        {
            if (candidate.Build(probed.GetValueOrDefault(candidate.File.FullName)) is not { } built)
            {
                skipped++;
                continue;
            }

            items.Add(built.Item);
            if (!built.Images.IsEmpty)
            {
                images[built.Item.Id] = built.Images;
            }
        }

        if (skipped > 0)
        {
            logger.LogInformation("Skipped {Count} unreadable or too-short files under {Path}.", skipped, root);
        }

        return new LocalScan(items, images, _showFolders);
    }

    // ---- TV -------------------------------------------------------------------------------------------------------

    private void FindShows(DirectoryInfo root, DirectoryInfo directory, List<string> parents, MediaFolderOptions folder,
        List<Candidate> candidates, Dictionary<string, LocalImages> images)
    {
        bool isRoot = directory.FullName == root.FullName;
        if (!isRoot && LocalNaming.IsSkippedFolder(directory))
        {
            return;
        }

        if (IsShowFolder(directory, isRoot))
        {
            AddShow(directory, parents, folder, candidates, images);
            return;
        }

        List<string> beneath = isRoot ? parents : [.. parents, directory.Name];
        foreach (DirectoryInfo child in Children(directory))
        {
            FindShows(root, child, beneath, folder, candidates, images);
        }
    }

    /// <summary>
    /// A show's folder has a tvshow.nfo, season folders, or episodes of its own. The library's root is one only with a
    /// tvshow.nfo or season folders (a library of one show); loose files there are otherwise left alone. A folder whose
    /// subfolders only read as seasons the way ErsatzTV reads them (<c>1</c>, <c>Disc 2</c>, <c>Staffel 3</c>) is a show
    /// too, unless one of those subfolders is plainly a show itself (a grouping folder holding <c>Babylon 5</c>).
    /// </summary>
    private bool IsShowFolder(DirectoryInfo directory, bool isRoot)
    {
        if (File.Exists(Path.Combine(directory.FullName, "tvshow.nfo")))
        {
            return true;
        }

        DirectoryInfo[] children = [.. Children(directory).Where(child => !LocalNaming.IsSkippedFolder(child))];
        if (children.Any(child => LocalNaming.SeasonFolder(child.Name) is not null))
        {
            return true;
        }

        if (isRoot)
        {
            return false;
        }

        return Videos(directory).Any()
            || (children.Any(child => LocalNaming.SeasonFolderInShow(child.Name) is not null) && !children.Any(IsPlainlyShow));
    }

    /// <summary>A folder that's a show by its own markings (a tvshow.nfo or <c>Season</c> folders), not by holding videos.</summary>
    private static bool IsPlainlyShow(DirectoryInfo directory) =>
        File.Exists(Path.Combine(directory.FullName, "tvshow.nfo"))
        || Children(directory).Any(child => LocalNaming.SeasonFolder(child.Name) is not null);

    private void AddShow(DirectoryInfo show, List<string> parents, MediaFolderOptions folder, List<Candidate> candidates,
        Dictionary<string, LocalImages> images)
    {
        NfoInfo? nfo = NfoReader.ReadOne(Path.Combine(show.FullName, "tvshow.nfo"), "tvshow");
        FolderName name = LocalNaming.Folder(show.Name);
        string seriesId = Id(SeriesPrefix, show.FullName);
        _showFolders[seriesId] = show.FullName;
        string seriesName = nfo?.Title ?? name.Title;
        int? seriesYear = nfo?.Year ?? name.Year;

        Dictionary<string, string> pictures = Pictures(show);
        string? fanart = Pick(pictures, Fanart);
        var showImages = new LocalImages(Pick(pictures, ShowPosters), Pick(pictures, Landscapes), fanart);
        if (!showImages.IsEmpty)
        {
            images[seriesId] = showImages;
        }

        Dictionary<string, string> seriesIds = new(name.Ids, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in nfo?.Ids ?? new Dictionary<string, string>())
        {
            seriesIds[key] = value;
        }

        List<string> showTags = [.. folder.Tags];
        if (folder.TagFromFolders)
        {
            showTags.AddRange(parents);
        }

        showTags.AddRange(nfo?.Tags ?? []);

        foreach ((FileInfo file, int? folderSeason, string directory) in Episodes(show, null))
        {
            candidates.Add(new Candidate(file, probed =>
                Episode(file, folderSeason, directory, nfo, seriesId, seriesName, seriesYear, seriesIds, showTags, fanart, probed)));
        }
    }

    private (MediaItem, LocalImages)? Episode(FileInfo file, int? folderSeason, string directory, NfoInfo? show, string seriesId, string seriesName,
        int? seriesYear, IReadOnlyDictionary<string, string> seriesIds, List<string> showTags, string? fanart, ProbeResult? probed)
    {
        IReadOnlyList<NfoInfo> nfos = [.. NfoReader.Read(Path.ChangeExtension(file.FullName, ".nfo"))
            .Where(info => info.Kind == "episodedetails")];
        NfoInfo? nfo = nfos.FirstOrDefault();
        EpisodeName name = LocalNaming.Episode(file.Name, folderSeason is not null);

        TimeSpan duration = probed?.Duration ?? nfo?.Duration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero || duration < options.MinDuration)
        {
            return null;
        }

        // The season folder decides, as in ErsatzTV (and so an episode filed under Season 2 is in season 2, whatever its
        // name says); loose in the show, the .nfo, then the name.
        int? season = folderSeason ?? nfo?.Season ?? name.Season ?? name.Aired?.Year;
        // A daily show's episodes (named by date) are numbered by it within the year's season: 0315 for March 15th.
        int? episode = nfo?.Episode ?? name.Episode ?? (name.Aired is { } day ? (int?)(day.Month * 100 + day.Day) : null);
        int? last = nfos.Count > 1 ? nfos.Max(info => info.Episode) : name.LastEpisode;
        DateOnly? aired = nfo?.Premiered ?? name.Aired;

        string title = nfos.Count > 1 && nfos.All(info => info.Title is not null)
            ? string.Join(" / ", nfos.Select(info => info.Title))
            : nfo?.Title ?? name.Title ?? (episode is { } number ? $"Episode {number}" : Path.GetFileNameWithoutExtension(file.Name));

        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in seriesIds)
        {
            attributes["series_" + key] = value;
        }

        foreach ((string key, string value) in nfo?.Ids ?? new Dictionary<string, string>())
        {
            attributes[key] = value;
        }

        if (last is { } end && end > (episode ?? 0))
        {
            attributes["episode_end"] = end.ToString(CultureInfo.InvariantCulture);
        }

        IReadOnlyList<(TimeSpan Start, string? Name)> chapters = probed?.Chapters ?? [];
        AddCredits(attributes, duration, chapters);

        List<string> tags = [.. showTags, .. nfo?.Tags ?? []];
        var item = new MediaItem
        {
            Id = Id(ItemPrefix, file.FullName),
            Title = title,
            Kind = MediaKind.Episode,
            Duration = duration,
            SeriesId = seriesId,
            SeriesName = seriesName,
            SeasonNumber = season,
            EpisodeNumber = episode,
            PremiereDate = aired is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            Overview = nfo?.Plot,
            Rating = nfo?.Rating ?? show?.Rating,
            Tags = [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)],
            Genres = nfo?.Genres is { Count: > 0 } genres ? genres : show?.Genres ?? [],
            Networks = show?.Studios is { Count: > 0 } studios ? studios : nfo?.Studios ?? [],
            Collections = show?.Sets ?? [],
            SeriesYear = seriesYear,
            Chapters = [.. chapters.Select(chapter => chapter.Start).Where(start => start > TimeSpan.Zero).Distinct().Order()],
            Attributes = attributes,
            Path = file.FullName
        };

        string? still = Still(file, directory);
        return (item, new LocalImages(still, still, fanart));
    }

    /// <summary>
    /// Every episode under a show: its file, the season its folder says, and the folder it's in. Below a season folder,
    /// subfolders (<c>Season 1/Disc 2</c>) stay in that season, as in ErsatzTV.
    /// </summary>
    private IEnumerable<(FileInfo File, int? Season, string Directory)> Episodes(DirectoryInfo directory, int? season)
    {
        foreach (FileInfo file in Videos(directory))
        {
            yield return (file, season, directory.FullName);
        }

        foreach (DirectoryInfo child in Children(directory))
        {
            if (!LocalNaming.IsSkippedFolder(child))
            {
                foreach (var found in Episodes(child, season ?? LocalNaming.SeasonFolderInShow(child.Name)))
                {
                    yield return found;
                }
            }
        }
    }

    /// <summary>An episode's still: <c>name-thumb.jpg</c> or <c>name.jpg</c> beside it, or in a <c>metadata</c> folder (as Jellyfin keeps them).</summary>
    private static string? Still(FileInfo file, string directory)
    {
        string stem = Path.GetFileNameWithoutExtension(file.Name);
        Dictionary<string, string> beside = Pictures(new DirectoryInfo(directory));
        if (Pick(beside, [stem + "-thumb", stem]) is { } found)
        {
            return found;
        }

        string metadata = Path.Combine(directory, "metadata");
        return Directory.Exists(metadata) ? Pick(Pictures(new DirectoryInfo(metadata)), [stem + "-thumb", stem]) : null;
    }

    // ---- Movies ---------------------------------------------------------------------------------------------------

    private void FindMovies(DirectoryInfo root, DirectoryInfo directory, List<string> parents, MediaFolderOptions folder, List<Candidate> candidates)
    {
        bool isRoot = directory.FullName == root.FullName;
        if (!isRoot && LocalNaming.IsSkippedFolder(directory))
        {
            return;
        }

        FileInfo[] videos = [.. Videos(directory)];
        if (videos.Length > 0)
        {
            FolderName folderName = LocalNaming.Folder(directory.Name);

            // A movie's own folder: named "Title (Year)" as Radarr names it, or holding versions or parts of one movie only.
            bool ownFolder = !isRoot && (folderName.Year is not null
                || videos.Select(video => LocalNaming.MovieKey(LocalNaming.Movie(Path.GetFileNameWithoutExtension(video.Name)))).Distinct().Count() == 1);

            if (ownFolder)
            {
                AddMovie(directory, videos, folderName, parents, folder, candidates);
                return;
            }

            foreach (FileInfo video in videos)
            {
                AddMovie(directory, [video], null, parents, folder, candidates);
            }
        }

        List<string> beneath = isRoot ? parents : [.. parents, directory.Name];
        foreach (DirectoryInfo child in Children(directory))
        {
            FindMovies(root, child, beneath, folder, candidates);
        }
    }

    /// <param name="ownFolder">The movie's own folder's name, when it has one (then the folder's pictures and movie.nfo are its).</param>
    private void AddMovie(DirectoryInfo directory, FileInfo[] files, FolderName? ownFolder, List<string> parents, MediaFolderOptions folder,
        List<Candidate> candidates)
    {
        Dictionary<string, string> pictures = Pictures(directory);
        bool versions = files.Length > 1 && files.All(file => LocalNaming.Movie(Path.GetFileNameWithoutExtension(file.Name)).Part is null);
        string? groupId = versions ? Id(MoviePrefix, directory.FullName) : null;

        foreach (FileInfo file in files)
        {
            candidates.Add(new Candidate(file, probed => Movie(file, directory, ownFolder, pictures, parents, folder, groupId, probed)));
        }
    }

    private (MediaItem, LocalImages)? Movie(FileInfo file, DirectoryInfo directory, FolderName? ownFolder, Dictionary<string, string> pictures,
        List<string> parents, MediaFolderOptions folder, string? groupId, ProbeResult? probed)
    {
        string stem = Path.GetFileNameWithoutExtension(file.Name);
        NfoInfo? nfo = NfoReader.ReadOne(Path.ChangeExtension(file.FullName, ".nfo"), "movie")
                       ?? (ownFolder is not null ? NfoReader.ReadOne(Path.Combine(directory.FullName, "movie.nfo"), "movie") : null);
        MovieName name = LocalNaming.Movie(stem);

        TimeSpan duration = probed?.Duration ?? nfo?.Duration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero || duration < options.MinDuration)
        {
            return null;
        }

        // A Radarr folder's name is the tidiest; a file's may carry its quality or edition.
        string title = nfo?.Title ?? (ownFolder is { Year: not null } named ? named.Title : name.Title);
        int? year = nfo?.Year ?? ownFolder?.Year ?? name.Year;
        DateOnly? premiered = nfo?.Premiered ?? (year is { } y and >= 1 and <= 9999 ? (DateOnly?)new DateOnly(y, 1, 1) : null);
        if (name.Part is { } part && groupId is null)
        {
            title = $"{title} (Part {part})";
        }

        Dictionary<string, string> attributes = new(ownFolder?.Ids ?? LocalNaming.Folder(stem).Ids, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in nfo?.Ids ?? new Dictionary<string, string>())
        {
            attributes[key] = value;
        }

        if (name.Part is { } partNumber)
        {
            attributes["part"] = partNumber.ToString(CultureInfo.InvariantCulture);
        }

        IReadOnlyList<(TimeSpan Start, string? Name)> chapters = probed?.Chapters ?? [];
        AddCredits(attributes, duration, chapters);

        List<string> tags = [.. folder.Tags];
        if (folder.TagFromFolders)
        {
            tags.AddRange(parents);
        }

        tags.AddRange(nfo?.Tags ?? []);

        var item = new MediaItem
        {
            Id = Id(ItemPrefix, file.FullName),
            Title = title,
            Kind = MediaKind.Movie,
            Duration = duration,
            PremiereDate = premiered is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
            Overview = nfo?.Plot,
            Rating = nfo?.Rating,
            Tags = [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)],
            Genres = nfo?.Genres ?? [],
            Networks = nfo?.Studios ?? [],
            Collections = nfo?.Sets ?? [],
            Chapters = [.. chapters.Select(chapter => chapter.Start).Where(start => start > TimeSpan.Zero).Distinct().Order()],
            Attributes = attributes,
            Path = file.FullName,
            VersionGroupId = groupId,
            VersionName = groupId is null ? null : stem
        };

        // Pictures named for the file are its own; in its own folder, the folder's are too.
        LocalImages images = ownFolder is not null
            ? new LocalImages(
                Pick(pictures, [stem + "-poster", .. MoviePosters, stem]),
                Pick(pictures, [stem + "-landscape", stem + "-thumb", .. Landscapes]),
                Pick(pictures, [stem + "-fanart", stem + "-backdrop", .. Fanart]))
            : new LocalImages(
                Pick(pictures, [stem + "-poster", stem]),
                Pick(pictures, [stem + "-landscape", stem + "-thumb"]),
                Pick(pictures, [stem + "-fanart", stem + "-backdrop"]));
        return (item, images);
    }

    // ---- Shared ---------------------------------------------------------------------------------------------------

    private static void AddCredits(Dictionary<string, string> attributes, TimeSpan duration, IReadOnlyList<(TimeSpan Start, string? Name)> chapters)
    {
        if (ChapterMarkers.CreditsStart(duration, chapters) is { } credits)
        {
            attributes[MediaItem.CreditsStartAttribute] = credits.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The videos directly in a folder, less extras (trailers, samples…).</summary>
    private IEnumerable<FileInfo> Videos(DirectoryInfo directory)
    {
        IEnumerable<FileInfo> files;
        try
        {
            files = [.. directory.EnumerateFiles()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files
            .Where(file => _extensions.Contains(file.Extension) && !LocalNaming.IsExtraFile(file.Name) && !LocalNaming.IsResourceFork(file.Name))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<DirectoryInfo> Children(DirectoryInfo directory)
    {
        try
        {
            return [.. directory.EnumerateDirectories().OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The images in a folder by their name without extension (lower case).</summary>
    private static Dictionary<string, string> Pictures(DirectoryInfo directory)
    {
        Dictionary<string, string> pictures = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (FileInfo file in directory.EnumerateFiles())
            {
                if (LocalNaming.ImageExtensions.Contains(file.Extension))
                {
                    // A .jpg wins over a .tbn of the same name.
                    string stem = Path.GetFileNameWithoutExtension(file.Name);
                    if (!pictures.ContainsKey(stem) || file.Extension.Equals(".tbn", StringComparison.OrdinalIgnoreCase) is false)
                    {
                        pictures[stem] = file.FullName;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return pictures;
    }

    private static string? Pick(Dictionary<string, string> pictures, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (pictures.TryGetValue(name, out string? path))
            {
                return path;
            }
        }

        return null;
    }
}
