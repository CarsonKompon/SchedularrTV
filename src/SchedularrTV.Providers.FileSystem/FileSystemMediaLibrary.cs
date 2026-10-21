using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>
/// Media library backed by folders on disk. Commercials and other filler are tagged by their folder path; a folder of
/// TV shows or movies (<see cref="MediaFolderOptions.Kind"/>) is read as a library instead (see <see cref="LocalVideoScanner"/>).
/// </summary>
/// <remarks>
/// <c>Commercials\Adult Swim\Bumpers\Any Time\x.mp4</c> becomes an item tagged
/// <c>Commercials</c>, <c>Adult Swim</c>, <c>Bumpers</c>, <c>Any Time</c>, so the existing query
/// language reaches it with no extra syntax:
/// <c>tag:"Commercials" AND tag:"Adult Swim" AND tag:"Bumpers" AND (tag:"Any Time" OR tag:"January")</c>.
/// Beneath a <c>Promos</c> folder the next two levels are read as the show and the promo type
/// instead — <c>Cartoon Network\Promos\Top Cat\Up Next</c> — which is how a file on disk carries the
/// <c>show_id</c> and <c>promo_type</c> that promo targeting matches on.
/// </remarks>
public sealed class FileSystemMediaLibrary(
    IOptions<FileSystemLibraryOptions> options,
    ILogger<FileSystemMediaLibrary> logger,
    TimeProvider? timeProvider = null,
    SchedularrTV.Plugins.IMediaLibraryNotifier? notifier = null) : IMediaLibrary, IMediaSourceInfo, IExtrasSource
{
    public string Name => "Local folders";

    /// <summary>Whether any folder (commercials, TV or movies) is set up.</summary>
    public bool IsConfigured => _options.Folders.Count > 0;

    private volatile MediaSourceProgress? _loading;

    /// <summary>Set while the folders are being read; null otherwise.</summary>
    public MediaSourceProgress? Loading => _loading;

    public event Action? LoadingChanged;

    private void Report(MediaSourceProgress? progress)
    {
        _loading = progress;
        LoadingChanged?.Invoke();
    }

    /// <summary>Folders have no server of their own doing anything.</summary>
    public ValueTask<MediaSourceActivity?> GetActivityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<MediaSourceActivity?>(null);

    /// <summary>Folders are added and removed one by one rather than left out by name.</summary>
    public ValueTask<IReadOnlyList<MediaSourceLibrary>> GetLibrariesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MediaSourceLibrary>>([]);

    public bool Owns(string id) => id.StartsWith(LocalLibraryIds.SeriesPrefix, StringComparison.Ordinal);

    public async ValueTask<IReadOnlyList<MediaExtra>> GetExtrasAsync(string id, CancellationToken cancellationToken = default) =>
        await ExtrasAsync(id, cancellationToken).ConfigureAwait(false);

    // Read on each use: settings saved in the app apply straight away.
    private FileSystemLibraryOptions _options => options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>One scan's results, swapped in whole so a reader never sees half of one scan and half of another.</summary>
    private sealed record Snapshot(
        IReadOnlyList<MediaItem> Items,
        IReadOnlyDictionary<string, LocalImages> Images,
        IReadOnlyDictionary<string, string> ShowFolders);

    private static readonly Snapshot Empty = new([], new Dictionary<string, LocalImages>(), new Dictionary<string, string>());

    // Kept and served while the next scan runs, so a periodic rescan never leaves the library empty.
    private RefreshingSnapshot<Snapshot>? _cache;

    private RefreshingSnapshot<Snapshot> Cache => LazyInitializer.EnsureInitialized(ref _cache, () =>
    {
        var cache = new RefreshingSnapshot<Snapshot>((_, ct) => ScanAsync(ct), Name, _time, logger);
        // The library pages are built from this, so they're told when a newer scan lands.
        cache.RefreshedInBackground += () => notifier?.LibraryChanged();
        return cache;
    });

    /// <summary>
    /// A local TV show's extras (by the show's id): the videos in its <c>Extras</c>, <c>Trailers</c>, <c>Featurettes</c>…
    /// folders (in the show's folder or a season's) and those named as extras (<c>Show-trailer.mkv</c>), measured with
    /// ffprobe. Empty for a show that isn't from a local folder, or has none.
    /// </summary>
    public async ValueTask<IReadOnlyList<MediaExtra>> ExtrasAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.ShowFolders.TryGetValue(seriesId, out string? folder) || !Directory.Exists(folder))
        {
            return [];
        }

        HashSet<string> extensions = new(_options.Extensions, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> ignored = new(StringComparer.OrdinalIgnoreCase);
        List<FileInfo> files = [.. new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(file => extensions.Contains(file.Extension) && !LocalNaming.IsResourceFork(file.Name)
                && !InIgnoredFolder(folder, file.DirectoryName, ignored)
                && (LocalNaming.IsExtraFile(file.Name) || InExtrasFolder(folder, file)))
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)];
        if (files.Count == 0)
        {
            return [];
        }

        // One probe at a time, so this and a scan never write the probe cache together.
        IReadOnlyDictionary<string, TimeSpan> durations;
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            durations = await new MediaProbe(_options, logger).ProbeAllAsync(files, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }

        return [.. files.Select(file => new MediaExtra(
            Path.GetFileNameWithoutExtension(file.Name),
            file.FullName,
            durations.TryGetValue(file.FullName, out TimeSpan duration) ? duration : TimeSpan.Zero))];
    }

    /// <summary>Whether a folder between the show's and the file is an extras folder.</summary>
    private static bool InExtrasFolder(string showFolder, FileInfo file)
    {
        string? relative = Path.GetDirectoryName(Path.GetRelativePath(showFolder, file.FullName));
        return !string.IsNullOrEmpty(relative)
            && relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(LocalNaming.IsExtrasFolder);
    }

    /// <summary>The pictures found beside a TV show, episode or movie in a local library (by its id), or null.</summary>
    public async ValueTask<LocalImages?> ImagesAsync(string id, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Images.GetValueOrDefault(id);
    }

    public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return QueryEvaluator.Filter(snapshot.Items, expression);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Folders.Count > 0)
        {
            await Cache.RefreshAsync(Fingerprint(), cancellationToken).ConfigureAwait(false);
        }
    }

    // What a snapshot was scanned with: when the folders are changed in the app it's rescanned straight away.
    private string Fingerprint() =>
        System.Text.Json.JsonSerializer.Serialize(new { _options.Folders, _options.Extensions, _options.MinDuration });

    private async ValueTask<Snapshot> EnsureSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_options.Folders.Count == 0)
        {
            return Empty;
        }

        // Once scanned, the snapshot is served straight away; when it's old it's rescanned in the background.
        return await Cache.GetAsync(Fingerprint(), _options.CacheDuration, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Snapshot> ScanAsync(CancellationToken cancellationToken)
    {
        // One at a time with extras probing, so the two never write the probe cache together.
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var probe = new MediaProbe(_options, logger);
            List<MediaItem> items = [];
            Dictionary<string, LocalImages> images = new(StringComparer.Ordinal);
            Dictionary<string, string> showFolders = new(StringComparer.Ordinal);
            DateTimeOffset started = _time.GetUtcNow();
            int done = 0;

            foreach (MediaFolderOptions folder in _options.Folders)
            {
                string folderName = Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string context = $"{folderName} (folder {++done} of {_options.Folders.Count})";
                void Progress(string stage, int count, int? total) =>
                    Report(new MediaSourceProgress($"{context} — {stage}", count, total, started));
                Progress("Finding video files", 0, null);
                if (folder.Kind == MediaKind.Music)
                {
                    // Songs aren't schedule items; radio channels read them through LocalMusicLibrary.
                    continue;
                }

                if (folder.Kind is MediaKind.Episode or MediaKind.Movie)
                {
                    // A TV or movie library: shows, seasons and movies read the way Sonarr, Radarr and Kodi lay them out.
                    LocalScan scan = await new LocalVideoScanner(_options, probe, logger, Progress).ScanAsync(folder, cancellationToken).ConfigureAwait(false);
                    items.AddRange(scan.Items);
                    foreach ((string id, LocalImages found) in scan.Images)
                    {
                        images[id] = found;
                    }

                    foreach ((string id, string showFolder) in scan.ShowFolders)
                    {
                        showFolders[id] = showFolder;
                    }
                }
                else
                {
                    items.AddRange(await ScanFolderAsync(folder, probe, cancellationToken, Progress).ConfigureAwait(false));
                }
            }

            logger.LogInformation("File system library scanned: {Count} items across {Folders} folder(s).", items.Count, _options.Folders.Count);
            return new Snapshot(items, images, showFolders);
        }
        finally
        {
            if (_loading is not null)
            {
                Report(null);
            }

            _scanLock.Release();
        }
    }

    private async Task<IReadOnlyList<MediaItem>> ScanFolderAsync(
        MediaFolderOptions folder,
        MediaProbe probe,
        CancellationToken cancellationToken,
        Action<string, int, int?> progress)
    {
        string root = Path.GetFullPath(folder.Path);

        if (!Directory.Exists(root))
        {
            logger.LogWarning("Media folder not found, skipping: {Path}", root);
            return [];
        }

        HashSet<string> extensions = new(_options.Extensions, StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        // As in ErsatzTV: hidden (dot) folders, folders marked .etvignore (or .ignore) and macOS "._" files are left out.
        Dictionary<string, bool> ignored = new(StringComparer.OrdinalIgnoreCase);
        List<FileInfo> files = [];
        foreach (FileInfo file in new DirectoryInfo(root)
            .EnumerateFiles("*", enumeration)
            .Where(f => extensions.Contains(f.Extension) && !LocalNaming.IsResourceFork(f.Name) && !InIgnoredFolder(root, f.DirectoryName, ignored)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file);
            progress("Finding video files", files.Count, null);
        }

        if (files.Count == 0)
        {
            logger.LogWarning("No media files matched under {Path}.", root);
            return [];
        }

        IReadOnlyDictionary<string, TimeSpan> durations = await probe.ProbeAllAsync(files, cancellationToken, progress).ConfigureAwait(false);

        List<MediaItem> items = new(files.Count);
        int skipped = 0;

        foreach (FileInfo file in files)
        {
            progress("Building library items", items.Count + skipped, files.Count);
            if (!durations.TryGetValue(file.FullName, out TimeSpan duration) || duration < _options.MinDuration)
            {
                skipped++;
                continue;
            }

            (List<string> tags, Dictionary<string, string> attributes) = Describe(root, file.FullName, folder);

            items.Add(new MediaItem
            {
                Id = $"fs:{DeterministicRandom.Hash(file.FullName):x16}",
                Title = Path.GetFileNameWithoutExtension(file.Name),
                Kind = folder.Kind,
                Duration = duration,
                Tags = tags,
                Attributes = attributes,
                Path = file.FullName
            });
        }

        if (skipped > 0)
        {
            logger.LogInformation("Skipped {Count} unreadable or too-short files under {Path}.", skipped, root);
        }

        return items;
    }

    /// <summary>Whether a folder between the root and a file is hidden or marked to be ignored (remembered per folder).</summary>
    internal static bool InIgnoredFolder(string root, string? directory, Dictionary<string, bool> known)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length <= top.Length)
        {
            return false;
        }

        if (known.TryGetValue(full, out bool cached))
        {
            return cached;
        }

        bool result = LocalNaming.IsIgnoredFolder(full) || InIgnoredFolder(root, Path.GetDirectoryName(full), known);
        known[full] = result;
        return result;
    }

    internal static List<string> BuildTags(string root, string filePath, MediaFolderOptions folder) =>
        Describe(root, filePath, folder).Tags;

    /// <summary>
    /// Splits the folders above a file into tags and attributes. An attribute folder is not also a
    /// tag: the show and promo type would otherwise pollute the tag set with values nothing queries.
    /// </summary>
    internal static (List<string> Tags, Dictionary<string, string> Attributes) Describe(
        string root,
        string filePath,
        MediaFolderOptions folder)
    {
        List<string> tags = [.. folder.Tags];
        Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);

        if (!folder.TagFromFolders)
        {
            return (tags, attributes);
        }

        // A folder given no tags of its own is tagged with its name, as ErsatzTV tags "other videos" with the library
        // folder's name (D:\Media\Commercials → Commercials) as well as the folders below it.
        if (tags.Count == 0 && Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } rootName)
        {
            tags.Add(rootName);
        }

        string? relativeDirectory = Path.GetDirectoryName(Path.GetRelativePath(root, filePath));
        if (string.IsNullOrEmpty(relativeDirectory) || relativeDirectory == ".")
        {
            return (tags, attributes);
        }

        string[] segments = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Depth caps tagging only. An attribute is metadata rather than a browsing level, so burying
        // a promo deeper than the cap should not silently strip the show it advertises.
        int depth = folder.MaxTagDepth is { } max ? Math.Min(max, segments.Length) : segments.Length;
        int promoRoot = -1;

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (TryReadAttribute(segment, folder, out string? key, out string? value))
            {
                attributes[key] = value;
                continue;
            }

            if (promoRoot < 0
                && !string.IsNullOrWhiteSpace(folder.PromoFolder)
                && string.Equals(segment, folder.PromoFolder, StringComparison.OrdinalIgnoreCase))
            {
                promoRoot = i;
            }
            else if (promoRoot >= 0)
            {
                switch (i - promoRoot)
                {
                    case 1:
                        attributes["show_id"] = segment;
                        continue;
                    case 2:
                        attributes["promo_type"] = NormalisePromoType(segment);
                        continue;

                    // `Eventually/Saturday/400pm` files the airing day above the time. Either may be
                    // used on its own, so the folder is read by what it says rather than by depth.
                    case 3:
                    case 4:
                        if (TryReadDay(segment, out string day))
                        {
                            attributes["promo_day"] = day;
                        }
                        else
                        {
                            attributes["promo_clock"] = NormaliseClock(segment);
                        }

                        continue;
                }
            }

            if (i < depth && !tags.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(segment);
            }
        }

        return (tags, attributes);
    }

    /// <summary>
    /// "Up Next" is how a folder reads; <c>up_next</c> is how a promo type is written. Apostrophes are
    /// dropped rather than split on, so "You're Watching" reads as <c>youre_watching</c>.
    /// </summary>
    internal static string NormalisePromoType(string folderName)
    {
        string stripped = folderName.ToLowerInvariant().Replace("'", string.Empty).Replace("\u2019", string.Empty);

        return string.Join('_', stripped.Split(
            [.. stripped.Where(c => !char.IsLetterOrDigit(c)).Distinct()],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Reduces "8:30 AM" and "830am" to one spelling so a folder can be written either way.</summary>
    internal static string NormaliseClock(string folderName) =>
        new string([.. folderName.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// Recognises a day folder: a weekday, or Weekdays / Weekends (see <see cref="SchedularrTV.Core.Scheduling.PromoDays"/>).
    /// Letters only, so a numeric clock folder is never mistaken for the underlying enum value.
    /// </summary>
    internal static bool TryReadDay(string folderName, out string day) =>
        SchedularrTV.Core.Scheduling.PromoDays.TryRead(folderName, out day);

    private static bool TryReadAttribute(
        string segment,
        MediaFolderOptions folder,
        [NotNullWhen(true)] out string? key,
        [NotNullWhen(true)] out string? value)
    {
        key = null;
        value = null;

        int separator = segment.IndexOf('=');
        if (separator <= 0 || separator == segment.Length - 1)
        {
            return false;
        }

        string candidate = segment[..separator].Trim();
        if (!folder.AttributeFolderPrefixes.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        key = candidate.ToLowerInvariant();
        value = segment[(separator + 1)..].Trim();
        return value.Length > 0;
    }
}
