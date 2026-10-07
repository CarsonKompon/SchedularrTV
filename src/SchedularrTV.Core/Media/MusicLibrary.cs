namespace SchedularrTV.Core.Media;

/// <summary>One song in a music library.</summary>
/// <param name="Id">The library's own id for it, used to ask for its stream and lyrics.</param>
/// <param name="ArtId">What to ask the library for its cover with (often the album's); null when it has none.</param>
public sealed record MusicTrack(
    string Id,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? AlbumArtist,
    TimeSpan Duration,
    int? Year,
    IReadOnlyList<string> Genres,
    string? ArtId)
{
    public string Artist => Artists.Count > 0 ? string.Join(", ", Artists) : AlbumArtist ?? string.Empty;
}

/// <summary>A named thing in a music library (a playlist, a genre or an artist), for pickers.</summary>
public sealed record MusicChoice(string Id, string Name, int? Count = null);

/// <summary>One line of a song's lyrics; <see cref="Start"/> is null for unsynced lyrics.</summary>
public sealed record MusicLyricLine(string Text, TimeSpan? Start);

/// <summary>Which songs to take: every one, or a playlist's, or some genres' or artists'.</summary>
/// <param name="PlaylistId">A playlist's songs, in its order unless shuffled.</param>
public sealed record MusicQuery(string? PlaylistId = null, IReadOnlyList<string>? Genres = null, IReadOnlyList<string>? Artists = null, int Limit = 2000);

/// <summary>How ffmpeg reads a song: a file path or an address, and any HTTP headers it needs (kept out of the address).</summary>
public sealed record MusicStream(string Input, string? Headers = null);

/// <summary>A library of music (Jellyfin's, local folders, any plugin's) that radio channels play from.</summary>
public interface IMusicLibrary
{
    /// <summary>A stable id, saved with each station that uses it: "jellyfin", "local".</summary>
    string Id { get; }

    /// <summary>Its name as shown: "Jellyfin", "Local music folders".</summary>
    string Name { get; }

    /// <summary>Whether it's set up (an address and key, or at least one folder).</summary>
    bool IsConfigured { get; }

    /// <summary>Songs matching the query, shuffled or in order (a playlist's own order, else by artist, album and track).</summary>
    Task<IReadOnlyList<MusicTrack>> GetTracksAsync(MusicQuery query, bool shuffle, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MusicChoice>> GetPlaylistsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MusicChoice>> GetGenresAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MusicChoice>> GetArtistsAsync(CancellationToken cancellationToken = default);

    /// <summary>A song's lyrics, timed when they're synced; empty when it has none.</summary>
    Task<IReadOnlyList<MusicLyricLine>> GetLyricsAsync(string trackId, CancellationToken cancellationToken = default);

    /// <summary>Where a song can be read in full; null when it's gone.</summary>
    ValueTask<MusicStream?> GetStreamAsync(string trackId, CancellationToken cancellationToken = default);

    /// <summary>A song's cover (by its <see cref="MusicTrack.ArtId"/>), about <paramref name="width"/> pixels wide; null when there's none.</summary>
    ValueTask<ArtworkImage?> GetArtAsync(string artId, int width, CancellationToken cancellationToken = default);
}

public static class MusicLibraries
{
    /// <summary>The library a station names, else the first that's set up.</summary>
    public static IMusicLibrary? Pick(IEnumerable<IMusicLibrary> libraries, string? id)
    {
        IMusicLibrary[] all = [.. libraries];
        return (id is { Length: > 0 } ? all.FirstOrDefault(library => string.Equals(library.Id, id, StringComparison.OrdinalIgnoreCase)) : null)
            ?? all.FirstOrDefault(library => library.IsConfigured);
    }
}
