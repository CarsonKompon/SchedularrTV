using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Media;

/// <summary>Which of a music library's songs background music takes.</summary>
public enum BackgroundMusicPick
{
    All,
    Playlist,
    Genres,
    Artists
}

/// <summary>
/// Music played under something (a web page channel, the TV guide, a news show, a generated promo): the songs in a
/// folder on disk, or songs from any music library (<see cref="IMusicLibrary"/>: Jellyfin's, local music folders, a
/// plugin's), all of them or a playlist's, some genres' or some artists'. Saved as it is in each plugin's settings.
/// </summary>
public sealed class BackgroundMusic
{
    /// <summary>A folder whose songs (in any sub-folder) are played; used when <see cref="LibraryId"/> is empty.</summary>
    public string? Folder { get; set; }

    /// <summary>The music library's <see cref="IMusicLibrary.Id"/> ("jellyfin", "local", a plugin's).</summary>
    public string? LibraryId { get; set; }

    public BackgroundMusicPick Pick { get; set; }

    public string? PlaylistId { get; set; }

    /// <summary>The playlist's name when it was chosen, for showing without asking the library.</summary>
    public string? PlaylistName { get; set; }

    public List<string> Genres { get; set; } = [];

    public List<string> Artists { get; set; } = [];

    [JsonIgnore, YamlIgnore]
    public bool FromLibrary => !string.IsNullOrWhiteSpace(LibraryId);

    /// <summary>Whether there's any music to play (a folder named, or a library chosen).</summary>
    [JsonIgnore, YamlIgnore]
    public bool IsSet => FromLibrary || !string.IsNullOrWhiteSpace(Folder);

    /// <summary>Changes whenever the songs it stands for do, so a player can tell when to start again.</summary>
    [JsonIgnore, YamlIgnore]
    public string Key => FromLibrary
        ? string.Join('|', "library", LibraryId, Pick, Pick == BackgroundMusicPick.Playlist ? PlaylistId : null,
            Pick == BackgroundMusicPick.Genres ? string.Join(',', Genres) : null, Pick == BackgroundMusicPick.Artists ? string.Join(',', Artists) : null)
        : "folder|" + Folder;

    /// <summary>The library query for <see cref="Pick"/>.</summary>
    [JsonIgnore, YamlIgnore]
    public MusicQuery Query => Pick switch
    {
        BackgroundMusicPick.Playlist => new MusicQuery(PlaylistId: PlaylistId),
        BackgroundMusicPick.Genres => new MusicQuery(Genres: Genres),
        BackgroundMusicPick.Artists => new MusicQuery(Artists: Artists),
        _ => new MusicQuery()
    };

    /// <summary>Whether a library choice is complete (a playlist chosen for a playlist, at least one genre for genres…).</summary>
    [JsonIgnore, YamlIgnore]
    public bool IsComplete => !FromLibrary || Pick switch
    {
        BackgroundMusicPick.Playlist => !string.IsNullOrWhiteSpace(PlaylistId),
        BackgroundMusicPick.Genres => Genres.Count > 0,
        BackgroundMusicPick.Artists => Artists.Count > 0,
        _ => true
    };

    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(
        [".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav", ".wma", ".alac", ".aif", ".aiff", ".ape", ".wv", ".mka"],
        StringComparer.OrdinalIgnoreCase);

    public static BackgroundMusic FromFolder(string folder) => new() { Folder = folder };

    /// <summary>
    /// Tidied for saving: trimmed, empty lists for the picks not in use, and null when nothing is set. A music folder
    /// saved by an earlier version (<paramref name="legacyFolder"/>) becomes the folder when nothing else is set.
    /// </summary>
    public static BackgroundMusic? Tidy(BackgroundMusic? music, string? legacyFolder = null)
    {
        music ??= string.IsNullOrWhiteSpace(legacyFolder) ? null : new BackgroundMusic { Folder = legacyFolder };
        if (music is null)
        {
            return null;
        }

        music.Folder = string.IsNullOrWhiteSpace(music.Folder) ? null : music.Folder.Trim().Trim('"');
        music.LibraryId = string.IsNullOrWhiteSpace(music.LibraryId) ? null : music.LibraryId.Trim();
        music.Genres = [.. (music.Genres ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        music.Artists = [.. (music.Artists ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (music.FromLibrary)
        {
            music.Folder = null;
        }

        if (music.Pick != BackgroundMusicPick.Playlist)
        {
            music.PlaylistId = null;
            music.PlaylistName = null;
        }

        return music.IsSet ? music : null;
    }

    public BackgroundMusic Copy() => new()
    {
        Folder = Folder,
        LibraryId = LibraryId,
        Pick = Pick,
        PlaylistId = PlaylistId,
        PlaylistName = PlaylistName,
        Genres = [.. Genres],
        Artists = [.. Artists]
    };

    /// <summary>A short description: "music from D:\Music", "Jellyfin: the playlist Beds".</summary>
    public string Describe(IEnumerable<IMusicLibrary>? libraries = null)
    {
        if (!FromLibrary)
        {
            return Folder is { Length: > 0 } folder ? folder : "no music";
        }

        string name = libraries?.FirstOrDefault(library => string.Equals(library.Id, LibraryId, StringComparison.OrdinalIgnoreCase))?.Name ?? LibraryId!;
        return Pick switch
        {
            BackgroundMusicPick.Playlist => $"{name}: the playlist {PlaylistName ?? PlaylistId ?? "(none chosen)"}",
            BackgroundMusicPick.Genres => $"{name}: {string.Join(", ", Genres)}",
            BackgroundMusicPick.Artists => $"{name}: {string.Join(", ", Artists)}",
            _ => $"{name}: every song"
        };
    }

    /// <summary>The library it plays from: the one it names, null when that's not installed or not set up.</summary>
    public IMusicLibrary? Library(IEnumerable<IMusicLibrary> libraries) =>
        FromLibrary
            ? libraries.FirstOrDefault(library => library.IsConfigured && string.Equals(library.Id, LibraryId, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// The songs once through, shuffled, as ffmpeg reads them: a folder's files, or the library's songs (each asked
    /// for its stream only when it's reached). Empty when there are none or the library isn't there.
    /// </summary>
    public async IAsyncEnumerable<MusicStream> SongsAsync(IEnumerable<IMusicLibrary> libraries, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!FromLibrary)
        {
            foreach (string path in FolderSongs(Folder))
            {
                yield return new MusicStream(path);
            }

            yield break;
        }

        if (!IsComplete || Library(libraries) is not { } library)
        {
            yield break;
        }

        IReadOnlyList<MusicTrack> tracks = await library.GetTracksAsync(Query, shuffle: true, cancellationToken).ConfigureAwait(false);
        foreach (MusicTrack track in tracks)
        {
            if (await library.GetStreamAsync(track.Id, cancellationToken).ConfigureAwait(false) is { } stream)
            {
                yield return stream;
            }
        }
    }

    /// <summary>The audio files in a folder and its sub-folders, shuffled; none when it can't be read.</summary>
    public static string[] FolderSongs(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            string[] songs = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)) && !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal))];
            Random.Shared.Shuffle(songs);
            return songs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
