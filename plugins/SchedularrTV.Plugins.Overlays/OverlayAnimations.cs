using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SkiaSharp;

namespace SchedularrTV.Overlays;

/// <summary>
/// Animated designs (layers moving by keyframes) as transparent videos for ErsatzTV to lay over the picture: one for each
/// look (the design with its text filled in) and appearance length, drawn frame by frame and encoded with ffmpeg in the
/// background. Until a video is ready the design airs standing still; <see cref="Rendered"/> says when one is, so the
/// schedule can be published again with it.
/// </summary>
public sealed class OverlayAnimations : IDisposable
{
    private readonly Func<string> _folder;
    private readonly Func<string> _ffmpeg;
    private readonly ILogger? _logger;
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _failed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();
    private readonly object _start = new();
    private Task? _worker;

    /// <param name="folder">Where videos are kept: the drawn overlays' folder, which ErsatzTV reads.</param>
    /// <param name="ffmpeg">The ffmpeg to encode with.</param>
    public OverlayAnimations(Func<string> folder, Func<string> ffmpeg, ILogger? logger = null)
    {
        _folder = folder;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>A video has just been made.</summary>
    public event Action? Rendered;

    public string Folder => _folder();

    /// <summary>A failed video isn't tried again for this long.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <param name="Source">For an animated picture made into a video as it is (a file overlay's GIF), the picture; null for a design.</param>
    private sealed record Job(string Key, Overlay Design, Dictionary<string, string> Tokens, OverlayClock Clock, string? Source = null);

    /// <summary>
    /// An animated picture (a GIF a file overlay shows) as a transparent video ErsatzTV plays, made once (in the
    /// background, when <paramref name="queue"/>); null until it's made.
    /// </summary>
    public string? Converted(string picture, bool queue)
    {
        string key = ConvertedKey(picture);
        foreach (string extension in (string[])[".webm", ".mov"])
        {
            string path = Path.Combine(Folder, key + extension);
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }
        }

        if (queue && !(_failed.TryGetValue(key, out DateTime failedAt) && DateTime.UtcNow - failedAt < RetryAfter) && _queued.TryAdd(key, 0))
        {
            _jobs.Writer.TryWrite(new Job(key, new Overlay { Name = Path.GetFileName(picture) }, new Dictionary<string, string>(), new OverlayClock(0, 1, 0), picture));
            EnsureWorker();
        }

        return null;
    }

    private static string ConvertedKey(string picture)
    {
        long stamp = File.Exists(picture) ? File.GetLastWriteTimeUtc(picture).Ticks : 0;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Path.GetFullPath(picture)}|{stamp}|v1"));
        return "anim-gif-" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>A picture's frames encoded by ffmpeg as they are (their own timing and size, made even), keeping transparency.</summary>
    private async Task ConvertAsync(Job job, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        string[] start = ["-hide_banner", "-loglevel", "error", "-y", "-i", job.Source!, "-an", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=rgba"];
        try
        {
            await RunToFileAsync(job.Key, ".webm",
                [.. start, "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-b:v", "0", "-crf", "32", "-deadline", "realtime", "-cpu-used", "8",
                    "-row-mt", "1", "-auto-alt-ref", "0", "-f", "webm"], cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            await RunToFileAsync(job.Key, ".mov", [.. start, "-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le", "-f", "mov"],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunToFileAsync(string key, string extension, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string final = Path.Combine(Folder, key + extension);
        string temp = Path.Combine(Folder, $"{key}.{Guid.NewGuid():N}.part");
        var start = new ProcessStartInfo(_ffmpeg()) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(temp);
        using Process ffmpeg = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg couldn't be started.");
        Task<string> errors = ffmpeg.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await ffmpeg.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true);
            TryDelete(temp);
            throw;
        }

        string log = (await errors.ConfigureAwait(false)).Trim();
        if (ffmpeg.ExitCode != 0 || !File.Exists(temp))
        {
            TryDelete(temp);
            throw new InvalidOperationException($"ffmpeg exited with code {ffmpeg.ExitCode}: {log}");
        }

        File.Move(temp, final, overwrite: true);
    }

    /// <summary>
    /// The finished video of one appearance of <paramref name="design"/>, or null while there isn't one (it's queued to
    /// be made when <paramref name="queue"/>).
    /// </summary>
    /// <param name="length">How long the appearance (or a whole-programme overlay's animation) is.</param>
    /// <param name="wholeProgramme">Draws the overlay's fade-in into the video, and doesn't take layers out before the end, for a whole-programme overlay's opening.</param>
    public string? Find(Overlay design, IReadOnlyDictionary<string, string> tokens, double length, bool wholeProgramme, bool queue)
    {
        var clock = new OverlayClock(0, Math.Max(0.5, length), Math.Max(0, design.FadeSeconds), FadeIn: wholeProgramme, LayerOuts: !wholeProgramme);
        string key = Key(design, tokens, clock);
        foreach (string extension in (string[])[".webm", ".mov"])
        {
            string path = Path.Combine(Folder, key + extension);
            if (File.Exists(path))
            {
                Touch(path);
                return path;
            }
        }

        if (queue && !(_failed.TryGetValue(key, out DateTime failedAt) && DateTime.UtcNow - failedAt < RetryAfter) && _queued.TryAdd(key, 0))
        {
            _jobs.Writer.TryWrite(new Job(key, design.Copy(), new Dictionary<string, string>(tokens, StringComparer.Ordinal), clock));
            EnsureWorker();
        }

        return null;
    }

    /// <summary>The name a video is kept under: the look, and how its appearance is drawn.</summary>
    public static string Key(Overlay design, IReadOnlyDictionary<string, string> tokens, OverlayClock clock)
    {
        string text = string.Join('|', OverlayRenderer.Key(design, tokens),
            clock.Length.ToString("0.###", CultureInfo.InvariantCulture), clock.Fade.ToString("0.###", CultureInfo.InvariantCulture),
            clock.FadeIn, clock.LayerOuts, OverlayAnimation.FpsText(OverlayAnimation.FpsOf(design)), "v1");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "anim-" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private void EnsureWorker()
    {
        lock (_start)
        {
            if (_worker is null || _worker.IsCompleted)
            {
                _worker = Task.Run(WorkAsync);
            }
        }
    }

    private async Task WorkAsync()
    {
        CancellationToken token = _life.Token;
        try
        {
            while (await _jobs.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_jobs.Reader.TryRead(out Job? job))
                {
                    try
                    {
                        await (job.Source is null ? RenderAsync(job, token) : ConvertAsync(job, token)).ConfigureAwait(false);
                        _failed.TryRemove(job.Key, out _);
                        Rendered?.Invoke();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _failed[job.Key] = DateTime.UtcNow;
                        _logger?.LogWarning(ex, "The animated overlay {Overlay} couldn't be made into a video; it shows standing still.", job.Design.Name);
                    }
                    finally
                    {
                        _queued.TryRemove(job.Key, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Draws every frame and encodes them: VP9 WebM with alpha, or ProRes 4444 when this ffmpeg can't do VP9.</summary>
    private async Task RenderAsync(Job job, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        try
        {
            await EncodeAsync(job, ".webm",
                ["-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-b:v", "0", "-crf", "32", "-deadline", "realtime", "-cpu-used", "8",
                    "-row-mt", "1", "-auto-alt-ref", "0", "-f", "webm"], cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            await EncodeAsync(job, ".mov", ["-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le", "-f", "mov"],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EncodeAsync(Job job, string extension, IReadOnlyList<string> codec, CancellationToken cancellationToken)
    {
        int width = Math.Max(2, Math.Clamp(job.Design.Width, 64, 7680) / 2 * 2);
        int height = Math.Max(2, Math.Clamp(job.Design.Height, 64, 4320) / 2 * 2);
        double fps = OverlayAnimation.FpsOf(job.Design);
        int frames = Math.Max(1, (int)Math.Ceiling(job.Clock.Length * fps));
        string final = Path.Combine(Folder, job.Key + extension);
        string temp = Path.Combine(Folder, $"{job.Key}.{Guid.NewGuid():N}.part");

        var start = new ProcessStartInfo(_ffmpeg())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in (string[])
                 [
                     "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba",
                     "-s", $"{width}x{height}", "-r", OverlayAnimation.FpsText(fps), "-i", "-", "-an"
                 ])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string argument in codec)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(temp);

        using Process ffmpeg = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg couldn't be started.");
        var errors = new StringBuilder();
        Task drain = Task.Run(async () =>
        {
            while (await ffmpeg.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lock (errors)
                {
                    if (errors.Length < 8000) errors.AppendLine(line);
                }
            }
        }, cancellationToken);

        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            // ffmpeg wants straight alpha.
            SKImageInfo output = info.WithAlphaType(SKAlphaType.Unpremul);
            using SKSurface surface = SKSurface.Create(info);
            using var pixels = new SKBitmap(output);
            byte[] frame = new byte[output.BytesSize];
            Stream input = ffmpeg.StandardInput.BaseStream;
            for (int index = 0; index < frames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SKCanvas canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                OverlayRenderer.DrawDesign(canvas, job.Design, job.Tokens, null, job.Clock with { Seconds = index / fps });
                if (!surface.ReadPixels(output, pixels.GetPixels(), pixels.RowBytes, 0, 0))
                {
                    throw new InvalidOperationException("Couldn't read a drawn frame.");
                }

                pixels.GetPixelSpan().CopyTo(frame);
                await input.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            input.Close();
            await ffmpeg.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || cancellationToken.IsCancellationRequested)
        {
            if (!ffmpeg.HasExited)
            {
                ffmpeg.Kill(entireProcessTree: true);
            }

            TryDelete(temp);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            await ffmpeg.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (ffmpeg.ExitCode != 0 || !File.Exists(temp))
        {
            TryDelete(temp);
            string log;
            lock (errors) log = errors.ToString().Trim();
            throw new InvalidOperationException($"ffmpeg exited with code {ffmpeg.ExitCode}: {log}");
        }

        File.Move(temp, final, overwrite: true);
    }

    // Marks a video as in use (at most once a day), so pruning never removes one a playout still points at.
    private static void Touch(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        _jobs.Writer.TryComplete();
        _life.Dispose();
    }
}
