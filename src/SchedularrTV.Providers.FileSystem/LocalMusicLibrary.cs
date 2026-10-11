using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>A song file's tags and length, as ffprobe read them, kept until the file changes.</summary>
internal sealed record MusicProbeEntry(long Length, long LastWriteTicks, long DurationTicks, Dictionary<string, string> Tags);

/// <summary>
/// Music from folders on disk, for radio channels: <c>Artist/Album/01 - Title.flac</c> (or any layout), with titles,
/// artists, albums, genres and years from the files' tags (read once with ffprobe and kept), falling back to the folder
/// and file names; covers from <c>cover.jpg</c>, <c>folder.jpg</c>, <c>front.jpg</c> or <c>album.jpg</c> in the song's
/// folder; lyrics from an <c>.lrc</c> (timed) or <c>.txt</c> file beside the song, else its lyrics tag; and
/// <c>.m3u</c>/<c>.m3u8</c> files as playlists. Hidden folders, <c>.etvignore</c>d ones and <c>._</c> files are left out.
/// </summary>
public sealed partial class LocalMusicLibrary(
    IOptions<FileSystemLibraryOptions> options,
    ILogger<LocalMusicLibrary> logger,
    TimeProvider? timeProvider = null) : IMusicLibrary
{
    public const string LibraryId = "local";
    private const string TrackPrefix = "localmusic:";
    private const string ArtPrefix = "localart:";
    private const string PlaylistPrefix = "localplaylist:";

    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(
        [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".aif", ".aiff", ".alac", ".ape", ".wv", ".mka", ".dsf"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart", "albumartsmall"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private FileSystemLibraryOptions Options => options.Value;

    private Snapshot _snapshot = Snapshot.Empty;
    private DateTimeOffset _scannedAt = DateTimeOffset.MinValue;
    private string _scannedWith = string.Empty;

    /// <summary>One scan: the songs (by id, in library order), where each lives, the covers and the playlists.</summary>
    private sealed record Snapshot(
        IReadOnlyList<MusicTrack> Tracks,
        IReadOnlyDictionary<string, string> Paths,
        IReadOnlyDictionary<string, string> Art,
        IReadOnlyDictionary<string, (string Name, IReadOnlyList<string> TrackIds)> Playlists)
    {
        public static readonly Snapshot Empty = new([], new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, (string, IReadOnlyList<string>)>());
    }

    public string Id => LibraryId;

    public string Name => "Local music folders";

    private IEnumerable<string> Roots => Options.Folders
        .Where(folder => folder.Kind == MediaKind.Music && !string.IsNullOrWhiteSpace(folder.Path))
        .Select(folder => Path.GetFullPath(folder.Path));

    public bool IsConfigured => Roots.Any();

    public async Task<int> CountTracksAsync(CancellationToken cancellationToken = default) =>
        (await EnsureAsync(cancellationToken).ConfigureAwait(false)).Tracks.Count;

    public async Task<IReadOnlyList<MusicTrack>> GetTracksAsync(MusicQuery query, bool shuffle, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<MusicTrack> tracks;
        if (query.PlaylistId is { } playlistId)
        {
            Dictionary<string, MusicTrack> byId = snapshot.Tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
            tracks = snapshot.Playlists.TryGetValue(playlistId, out var playlist)
                ? playlist.TrackIds.Where(byId.ContainsKey).Select(id => byId[id])
                : [];
        }
        else
        {
            tracks = snapshot.Tracks;
            if (query.Genres is { Count: > 0 } genres)
            {
                tracks = tracks.Where(track => track.Genres.Any(genre => genres.Contains(genre, StringComparer.OrdinalIgnoreCase)));
            }

            if (query.Artists is { Count: > 0 } artists)
            {
                tracks = tracks.Where(track => track.Artists.Any(artist => artists.Contains(artist, StringComparer.OrdinalIgnoreCase))
                    || (track.AlbumArtist is { } albumArtist && artists.Contains(albumArtist, StringComparer.OrdinalIgnoreCase)));
            }
        }

        List<MusicTrack> chosen = [.. tracks];
        if (shuffle)
        {
            new DeterministicRandom((ulong)Random.Shared.NextInt64()).Shuffle(chosen);
        }

        return [.. chosen.Take(Math.Clamp(query.Limit, 1, 10000))];
    }

    public async Task<IReadOnlyList<MusicChoice>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return [.. snapshot.Playlists
            .Select(pair => new MusicChoice(pair.Key, pair.Value.Name, pair.Value.TrackIds.Count))
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<MusicChoice>> GetGenresAsync(CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return Counted(snapshot.Tracks.SelectMany(track => track.Genres));
    }

    public async Task<IReadOnlyList<MusicChoice>> GetArtistsAsync(CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return Counted(snapshot.Tracks.Select(track => track.AlbumArtist is { Length: > 0 } albumArtist ? albumArtist : track.Artists.FirstOrDefault())
            .OfType<string>());
    }

    public async Task<IReadOnlyList<MusicLyricLine>> GetLyricsAsync(string trackId, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Paths.TryGetValue(trackId, out string? path))
        {
            return [];
        }

        foreach (string sidecar in (string[])[Path.ChangeExtension(path, ".lrc"), Path.ChangeExtension(path, ".txt")])
        {
            if (File.Exists(sidecar))
            {
                try
                {
                    return ParseLyrics(await File.ReadAllTextAsync(sidecar, cancellationToken).ConfigureAwait(false));
                }
                catch (IOException)
                {
                }
            }
        }

        // The lyrics tag, as ffprobe read it with the rest.
        return _cache.TryGetValue(path, out MusicProbeEntry? entry) && Tag(entry.Tags, "lyrics", "unsyncedlyrics", "lyrics-eng") is { } embedded
            ? ParseLyrics(embedded)
            : [];
    }

    public async ValueTask<MusicStream?> GetStreamAsync(string trackId, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Paths.TryGetValue(trackId, out string? path) && File.Exists(path) ? new MusicStream(path) : null;
    }

    public async ValueTask<ArtworkImage?> GetArtAsync(string artId, int width, CancellationToken cancellationToken = default)
    {
        Snapshot snapshot = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Art.TryGetValue(artId, out string? path) || !File.Exists(path))
        {
            return null;
        }

        string type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
        return new ArtworkImage(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), type);
    }

    /// <summary>
    /// Lyrics from an .lrc file (<c>[01:23.45]A line</c>, several stamps on a line repeating it; tags like <c>[ar:…]</c>
    /// skipped) or plain text (one unsynced line each).
    /// </summary>
    public static IReadOnlyList<MusicLyricLine> ParseLyrics(string text)
    {
        List<MusicLyricLine> timed = [];
        List<MusicLyricLine> plain = [];
        foreach (string raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            MatchCollection stamps = LrcStamp().Matches(line);
            if (stamps.Count > 0 && stamps[0].Index == 0)
            {
                string words = line[(stamps[^1].Index + stamps[^1].Length)..].Trim();
                foreach (Match stamp in stamps)
                {
                    double seconds = int.Parse(stamp.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
                        + double.Parse(stamp.Groups["s"].Value.Replace(':', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
                    timed.Add(new MusicLyricLine(words, TimeSpan.FromSeconds(seconds)));
                }
            }
            else if (!LrcTag().IsMatch(line))
            {
                plain.Add(new MusicLyricLine(line, null));
            }
        }

        return timed.Count > 0 ? [.. timed.OrderBy(line => line.Start)] : plain;
    }

    /// <summary>
    /// A song's title, artist and album from where it sits when its tags don't say: <c>Artist/Album/01 - Title</c>, or
    /// <c>Artist - Title</c> in a folder of its own.
    /// </summary>
    public static (string Title, string? Artist, string? Album, int? Track) FromNames(string root, string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        Match numbered = LeadingTrack().Match(stem);
        int? track = numbered.Success ? int.Parse(numbered.Groups["n"].Value, CultureInfo.InvariantCulture) : null;
        string rest = numbered.Success ? stem[numbered.Length..] : stem;

        string[] folders = (Path.GetDirectoryName(Path.GetRelativePath(root, path)) ?? string.Empty)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        string? album = folders.Length >= 1 ? folders[^1] : null;
        string? artist = folders.Length >= 2 ? folders[^2] : null;

        int dash = rest.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            artist ??= rest[..dash].Trim();
            rest = rest[(dash + 3)..];
        }

        string title = rest.Trim(' ', '-', '.', '_');
        return (title.Length == 0 ? stem : title, artist, album, track);
    }

    // --- Scanning ----------------------------------------------------------------------------------

    private ConcurrentDictionary<string, MusicProbeEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    private string CachePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Options.CachePath)) ?? ".", "music-cache.json");

    private string Fingerprint() => string.Join('|', Roots) + "|" + Options.FfprobePath;

    private async ValueTask<Snapshot> EnsureAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return Snapshot.Empty;
        }

        if (_scannedWith == Fingerprint() && _time.GetUtcNow() - _scannedAt < Options.CacheDuration)
        {
            return _snapshot;
        }

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string with = Fingerprint();
            if (_scannedWith == with && _time.GetUtcNow() - _scannedAt < Options.CacheDuration)
            {
                return _snapshot;
            }

            _snapshot = await ScanAsync(cancellationToken).ConfigureAwait(false);
            _scannedAt = _time.GetUtcNow();
            _scannedWith = with;
            return _snapshot;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<Snapshot> ScanAsync(CancellationToken cancellationToken)
    {
        if (!_cacheLoaded)
        {
            _cache = Load(CachePath);
            _cacheLoaded = true;
        }

        List<(string Root, FileInfo File)> songs = [];
        List<(string Root, FileInfo File)> playlists = [];
        Dictionary<string, bool> ignored = new(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (string root in Roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                logger.LogWarning("Music folder not found, skipping: {Path}", root);
                continue;
            }

            foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("*", enumeration))
            {
                if (LocalNaming.IsResourceFork(file.Name) || FileSystemMediaLibrary.InIgnoredFolder(root, file.DirectoryName, ignored))
                {
                    continue;
                }

                if (AudioExtensions.Contains(file.Extension))
                {
                    songs.Add((root, file));
                }
                else if (file.Extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    playlists.Add((root, file));
                }
            }
        }

        List<FileInfo> toProbe = [.. songs.Select(song => song.File).Where(file => !Fresh(file))];
        if (toProbe.Count > 0)
        {
            logger.LogInformation("Reading the tags of {Count} new or changed songs.", toProbe.Count);
            await Parallel.ForEachAsync(toProbe,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Options.ProbeConcurrency), CancellationToken = cancellationToken },
                async (file, token) =>
                {
                    if (await ProbeAsync(file, token).ConfigureAwait(false) is { } entry)
                    {
                        _cache[file.FullName] = entry;
                    }
                }).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        List<(MusicTrack Track, int? Disc, int? Number)> tracks = [];
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        Dictionary<string, string> art = new(StringComparer.Ordinal);
        Dictionary<string, string?> coverByFolder = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string root, FileInfo file) in songs)
        {
            if (!_cache.TryGetValue(file.FullName, out MusicProbeEntry? entry) || entry.DurationTicks <= 0)
            {
                continue;
            }

            (string nameTitle, string? nameArtist, string? nameAlbum, int? nameTrack) = FromNames(root, file.FullName);
            Dictionary<string, string> tags = entry.Tags;
            string? artistTag = Tag(tags, "artist");
            string? albumArtist = Tag(tags, "album_artist", "albumartist", "album artist");
            IReadOnlyList<string> artists = [];
            if (artistTag is { } artistText)
            {
                artists = Split(artistText);
            }
            else if ((nameArtist ?? albumArtist) is { } fallbackArtist)
            {
                artists = [fallbackArtist];
            }

            string id = TrackPrefix + DeterministicRandom.Hash(file.FullName).ToString("x16", CultureInfo.InvariantCulture);
            paths[id] = file.FullName;

            string folder = file.DirectoryName ?? root;
            if (!coverByFolder.TryGetValue(folder, out string? cover))
            {
                cover = Cover(folder);
                coverByFolder[folder] = cover;
            }

            string? own = Cover(folder, Path.GetFileNameWithoutExtension(file.Name));
            string? artPath = own ?? cover;
            string? artId = null;
            if (artPath is not null)
            {
                artId = ArtPrefix + DeterministicRandom.Hash(artPath).ToString("x16", CultureInfo.InvariantCulture);
                art[artId] = artPath;
            }

            int? year = Tag(tags, "date", "year", "originaldate") is { Length: >= 4 } date
                && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedYear) ? parsedYear : null;
            var track = new MusicTrack(
                id,
                Tag(tags, "title") ?? nameTitle,
                artists,
                Tag(tags, "album") ?? nameAlbum,
                albumArtist,
                TimeSpan.FromTicks(entry.DurationTicks),
                year,
                Tag(tags, "genre") is { } genre ? Split(genre) : [],
                artId);
            tracks.Add((track, Number(Tag(tags, "disc", "discnumber")), Number(Tag(tags, "track", "tracknumber")) ?? nameTrack));
        }

        List<MusicTrack> ordered = [.. tracks
            .OrderBy(entry => entry.Track.AlbumArtist ?? entry.Track.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Track.Album ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Disc ?? 0)
            .ThenBy(entry => entry.Number ?? int.MaxValue)
            .ThenBy(entry => entry.Track.Title, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Track)];

        Dictionary<string, string> idByPath = paths.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (string Name, IReadOnlyList<string> TrackIds)> lists = new(StringComparer.Ordinal);
        foreach ((_, FileInfo file) in playlists)
        {
            IReadOnlyList<string> ids = await ReadPlaylistAsync(file, idByPath, cancellationToken).ConfigureAwait(false);
            if (ids.Count > 0)
            {
                lists[PlaylistPrefix + DeterministicRandom.Hash(file.FullName).ToString("x16", CultureInfo.InvariantCulture)] =
                    (Path.GetFileNameWithoutExtension(file.Name), ids);
            }
        }

        logger.LogInformation("Local music: {Count} songs and {Playlists} playlists.", ordered.Count, lists.Count);
        return new Snapshot(ordered, paths, art, lists);
    }

    private bool Fresh(FileInfo file) =>
        _cache.TryGetValue(file.FullName, out MusicProbeEntry? cached) && cached.Length == file.Length && cached.LastWriteTicks == file.LastWriteTimeUtc.Ticks;

    /// <summary>The songs a playlist names that are in the library: paths relative to the playlist, or absolute.</summary>
    private static async Task<IReadOnlyList<string>> ReadPlaylistAsync(FileInfo playlist, Dictionary<string, string> idByPath, CancellationToken cancellationToken)
    {
        try
        {
            List<string> ids = [];
            foreach (string raw in await File.ReadAllLinesAsync(playlist.FullName, cancellationToken).ConfigureAwait(false))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string path;
                try
                {
                    path = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(playlist.DirectoryName ?? string.Empty, line));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (idByPath.TryGetValue(path, out string? id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>A folder's cover, or (with <paramref name="stem"/>) a picture named for one song.</summary>
    private static string? Cover(string folder, string? stem = null)
    {
        try
        {
            Dictionary<string, string> pictures = new(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (LocalNaming.ImageExtensions.Contains(file.Extension) && !file.Extension.Equals(".tbn", StringComparison.OrdinalIgnoreCase))
                {
                    pictures.TryAdd(Path.GetFileNameWithoutExtension(file.Name), file.FullName);
                }
            }

            IEnumerable<string> names = stem is null ? CoverNames : [stem];
            foreach (string name in names)
            {
                if (pictures.TryGetValue(name, out string? found))
                {
                    return found;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private async Task<MusicProbeEntry?> ProbeAsync(FileInfo file, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Options.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-v", "error", "-show_entries", "format=duration:format_tags:stream_tags", "-select_streams", "a:0", "-of", "json", file.FullName])
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start())
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Options.ProbeTimeout);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return process.ExitCode == 0 ? Parse(file, output) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("ffprobe timed out for {Path}.", file.FullName);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogDebug(ex, "ffprobe failed for {Path}.", file.FullName);
            return null;
        }
    }

    /// <summary>ffprobe's JSON for a song: its length, and its tags (the container's, then the stream's, as Ogg keeps them there).</summary>
    internal static MusicProbeEntry? Parse(FileInfo file, string output)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(output);
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("format", out JsonElement format) || !format.TryGetProperty("duration", out JsonElement durationText)
                || !double.TryParse(durationText.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || seconds <= 0 || double.IsInfinity(seconds))
            {
                return null;
            }

            Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
            void Add(JsonElement holder)
            {
                if (holder.TryGetProperty("tags", out JsonElement list) && list.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty tag in list.EnumerateObject())
                    {
                        if (tag.Value.ValueKind == JsonValueKind.String && tag.Value.GetString() is { Length: > 0 } value)
                        {
                            tags.TryAdd(tag.Name, value);
                        }
                    }
                }
            }

            Add(format);
            if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    Add(stream);
                }
            }

            return new MusicProbeEntry(file.Length, file.LastWriteTimeUtc.Ticks, TimeSpan.FromSeconds(seconds).Ticks, tags);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Tag(Dictionary<string, string> tags, params string[] names)
    {
        // Tags read back from the cache lose the dictionary's case-insensitivity, and files spell them TITLE or title.
        foreach (string name in names)
        {
            string? value = tags.TryGetValue(name, out string? exact)
                ? exact
                : tags.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>"Rock; Pop", "Rock/Pop" or "A, B" as separate values.</summary>
    private static IReadOnlyList<string> Split(string value) =>
        [.. value.Split([';', '/', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>"3", "3/12" → 3.</summary>
    private static int? Number(string? value) =>
        value is not null && int.TryParse(value.Split('/')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;

    private static IReadOnlyList<MusicChoice> Counted(IEnumerable<string> names) =>
        [.. names.Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicChoice(group.First(), group.First(), group.Count()))
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)];

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            string temp = CachePath + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, _cache, Json, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, CachePath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not keep the music tags; they'll be read again next time.");
        }
    }

    private static ConcurrentDictionary<string, MusicProbeEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = File.OpenRead(path);
                Dictionary<string, MusicProbeEntry>? loaded = JsonSerializer.Deserialize<Dictionary<string, MusicProbeEntry>>(stream, Json);
                return new ConcurrentDictionary<string, MusicProbeEntry>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }

        return new ConcurrentDictionary<string, MusicProbeEntry>(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\[(?<m>\d{1,3}):(?<s>\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex LrcStamp();

    [GeneratedRegex(@"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex LrcTag();

    [GeneratedRegex(@"^(?<n>\d{1,3})(?:\s*[-.)_]\s*|\s+)")]
    private static partial Regex LeadingTrack();
}
