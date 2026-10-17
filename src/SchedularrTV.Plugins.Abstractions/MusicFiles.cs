using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Plugins;

/// <summary>
/// Library songs as files on this computer, for anything that needs a file to work with (a generated promo's render,
/// an audition clip): a song that's already a file (local music folders) is used where it is; one a library serves
/// (Jellyfin's) is copied once into a cache folder, its sound as it is, and reused after that.
/// </summary>
public static class MusicFiles
{
    /// <summary>The song's file, copied into <paramref name="cacheFolder"/> when it isn't one already; null when the library has no such song.</summary>
    public static async Task<string?> LocalFileAsync(IMusicLibrary library, string trackId, string ffmpeg, string cacheFolder, CancellationToken cancellationToken = default)
    {
        if (await library.GetStreamAsync(trackId, cancellationToken).ConfigureAwait(false) is not { } stream)
        {
            return null;
        }

        bool isAddress = Uri.TryCreate(stream.Input, UriKind.Absolute, out Uri? address) && !address.IsFile;
        if (stream.Headers is not { Length: > 0 } && !isAddress && File.Exists(stream.Input))
        {
            return stream.Input;
        }

        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{library.Id}|{trackId}")))[..32] + ".mka";
        string target = Path.Combine(cacheFolder, name);
        if (File.Exists(target))
        {
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return target;
        }

        Directory.CreateDirectory(cacheFolder);
        string partial = Path.Combine(cacheFolder, $"partial-{Guid.NewGuid():N}.mka");
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in CopyArguments(stream, partial))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({ffmpeg}).");
            SchedularrTV.Plugins.Performance.Work.Track(process, "Music", "Caching a song");
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0 || !File.Exists(partial))
            {
                throw new InvalidOperationException($"Couldn't copy the song from {library.Name} (ffmpeg exit {process.ExitCode}): {(await errors.ConfigureAwait(false)).Trim()}");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The ffmpeg command that copies a song's sound, untouched, into a Matroska audio file.</summary>
    public static IReadOnlyList<string> CopyArguments(MusicStream stream, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        .. stream.Headers is { Length: > 0 } headers ? (string[])["-headers", headers + "\r\n"] : [],
        "-i", stream.Input,
        "-vn", "-map", "0:a:0", "-c:a", "copy", "-f", "matroska", output
    ];

    /// <summary>
    /// One song of <paramref name="music"/>'s for something identified by <paramref name="key"/> (the same key gets the same
    /// song while the library's songs stay the same), as a file; null when there are none.
    /// </summary>
    public static async Task<string?> PickFileAsync(BackgroundMusic music, IEnumerable<IMusicLibrary> libraries, string key, string ffmpeg, string cacheFolder,
        CancellationToken cancellationToken = default)
    {
        if (!music.FromLibrary)
        {
            string[] songs = [.. BackgroundMusic.FolderSongs(music.Folder).Order(StringComparer.OrdinalIgnoreCase)];
            return songs.Length == 0 ? null : songs[Pick(key, songs.Length)];
        }

        if (!music.IsComplete || music.Library(libraries) is not { } library)
        {
            return null;
        }

        IReadOnlyList<MusicTrack> tracks = await library.GetTracksAsync(music.Query, shuffle: false, cancellationToken).ConfigureAwait(false);
        if (tracks.Count == 0)
        {
            return null;
        }

        MusicTrack[] ordered = [.. tracks.OrderBy(track => track.Id, StringComparer.Ordinal)];
        return await LocalFileAsync(library, ordered[Pick(key, ordered.Length)].Id, ffmpeg, cacheFolder, cancellationToken).ConfigureAwait(false);
    }

    private static int Pick(string key, int count) =>
        (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|song"))) % (uint)count);
}
