using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>
/// The pictures beside local TV shows and movies (poster.jpg, fanart.jpg, an episode's -thumb.jpg). A large picture is
/// scaled down once with ffmpeg to about the width asked for, and kept, so a page of posters doesn't send megabytes each.
/// </summary>
public sealed class LocalArtworkSource(
    FileSystemMediaLibrary library,
    IOptions<FileSystemLibraryOptions> options,
    ILogger<LocalArtworkSource> logger) : IArtworkSource
{
    /// <summary>The widths pictures are scaled to; a request is rounded up to the next, so a picture is kept at few sizes.</summary>
    private static readonly int[] Widths = [160, 240, 320, 480, 640, 960, 1280];

    // Smaller than this is sent as it is: scaling would save little.
    private const long ScaleAbove = 160 * 1024;

    private readonly SemaphoreSlim _scaling = new(2, 2);
    private readonly ConcurrentDictionary<string, Task<string?>> _inProgress = new(StringComparer.Ordinal);
    private bool _ffmpegMissing;

    public bool Owns(string id) => LocalLibraryIds.IsLocalId(id);

    public async ValueTask<ArtworkImage?> GetImageAsync(string id, string type, int width, CancellationToken cancellationToken = default)
    {
        if (await library.ImagesAsync(id, cancellationToken).ConfigureAwait(false) is not { } images || images.For(type) is not { } path)
        {
            return null;
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        string? scaled = file.Length > ScaleAbove && !_ffmpegMissing
            ? await ScaledAsync(file, Widths.FirstOrDefault(size => size >= width, Widths[^1]), cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            return scaled is not null
                ? new ArtworkImage(await File.ReadAllBytesAsync(scaled, cancellationToken).ConfigureAwait(false), "image/jpeg")
                : new ArtworkImage(await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false), ContentType(file.Extension));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The picture scaled to a width (made now if it isn't already kept), or null when it can't be.</summary>
    private Task<string?> ScaledAsync(FileInfo file, int width, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.CachePath)) ?? ".", "artwork");
        string key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{width}";
        string target = Path.Combine(folder, $"{DeterministicRandom.Hash(key):x16}-{width.ToString(CultureInfo.InvariantCulture)}.jpg");
        if (File.Exists(target))
        {
            return Task.FromResult<string?>(target);
        }

        // Several requests for the same picture share one ffmpeg.
        Task<string?> scaling = _inProgress.GetOrAdd(target, _ => ScaleAsync(file.FullName, target, width));
        _ = scaling.ContinueWith(done => _inProgress.TryRemove(target, out Task<string?>? _), TaskScheduler.Default);
        return scaling.WaitAsync(cancellationToken);
    }

    private async Task<string?> ScaleAsync(string source, string target, int width)
    {
        await _scaling.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".tmp.jpg";
            var start = new ProcessStartInfo(Ffmpeg())
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (string argument in (string[])["-hide_banner", "-v", "error", "-y", "-i", source,
                         "-vf", $"scale='min({width.ToString(CultureInfo.InvariantCulture)},iw)':-2", "-frames:v", "1", "-q:v", "3", temp])
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg didn't start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string said = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                logger.LogDebug("Couldn't scale {Path}: {Said}", source, said.Trim());
                File.Delete(temp);
                return null;
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No ffmpeg: pictures are sent as they are from now on.
            _ffmpegMissing = true;
            logger.LogInformation("ffmpeg wasn't found, so local pictures are sent at full size.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Couldn't scale {Path}.", source);
            return null;
        }
        finally
        {
            _scaling.Release();
        }
    }

    /// <summary>ffmpeg beside the ffprobe in use, else the one on the PATH.</summary>
    private string Ffmpeg()
    {
        string ffprobe = options.Value.FfprobePath;
        if (Path.IsPathRooted(ffprobe))
        {
            string sibling = Path.Combine(Path.GetDirectoryName(ffprobe)!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return "ffmpeg";
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };
}
