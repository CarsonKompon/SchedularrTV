using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using SchedularrTV.Plugins.Performance;

namespace SchedularrTV.AutoChapters;

/// <summary>What ffmpeg says is in a video file: enough to play it for review and to step through it a frame at a time.</summary>
public sealed record MediaInfo(double Duration, double Fps, string Container, string? VideoCodec, string? AudioCodec, int Width, int Height, int Chapters)
{
    /// <summary>One frame, in seconds (25 fps when ffmpeg didn't say).</summary>
    public double FrameSeconds => Fps is > 1 and < 300 ? 1 / Fps : 1 / 25.0;

    private static readonly HashSet<string> BrowserVideo = new(StringComparer.OrdinalIgnoreCase) { "h264", "vp8", "vp9", "av1", "hevc" };
    private static readonly HashSet<string> BrowserAudio = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "opus", "vorbis", "flac" };
    private static readonly HashSet<string> BrowserContainers = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm", ".mkv" };

    /// <summary>
    /// Whether a browser may play the file as it is. HEVC is a maybe (Edge and Safari, Chrome with a hardware decoder); the
    /// page checks with the browser and falls back to a converted preview if it can't.
    /// </summary>
    public bool MaybePlayable(string file) =>
        BrowserContainers.Contains(Path.GetExtension(file))
        && VideoCodec is not null && BrowserVideo.Contains(VideoCodec)
        && (AudioCodec is null || BrowserAudio.Contains(AudioCodec));
}

/// <summary>
/// Plays programmes for the review queue: probes them, serves the file as it is to browsers that can play it, converts
/// a light preview from any point for those that can't, and takes single frames for scrubbing and marker thumbnails.
/// </summary>
public sealed partial class ReviewMedia(AutoChaptersSettings settings, ILogger<ReviewMedia> logger)
{
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, MediaInfo Info)> _probed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _frames = new(3);

    [GeneratedRegex(@"Input #0, (.+?), from ")]
    private static partial Regex InputLine();

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):([\d.]+)")]
    private static partial Regex DurationLine();

    [GeneratedRegex(@"Stream #0:\d+.*?: Video: (\w+)")]
    private static partial Regex VideoLine();

    [GeneratedRegex(@"Stream #0:\d+.*?: Audio: (\w+)")]
    private static partial Regex AudioLine();

    [GeneratedRegex(@"([\d.]+) fps")]
    private static partial Regex FpsPart();

    [GeneratedRegex(@"([\d.]+)(k?) tbr")]
    private static partial Regex TbrPart();

    [GeneratedRegex(@", (\d{2,5})x(\d{2,5})[\s,]")]
    private static partial Regex SizePart();

    [GeneratedRegex(@"^\s*Chapter #0:\d+")]
    private static partial Regex ChapterLine();

    /// <summary>What's in a file (cached until it changes). Throws when ffmpeg can't read it.</summary>
    public async Task<MediaInfo> ProbeAsync(string file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        if (_probed.TryGetValue(file, out var cached) && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc)
        {
            return cached.Info;
        }

        // Without an output ffmpeg just describes its input (and exits 1, which is expected).
        var (_, lines) = await Ffmpeg.RunAsync(settings.Ffmpeg, ["-hide_banner", "-nostdin", "-i", file], null, null, cancellationToken)
            .ConfigureAwait(false);
        MediaInfo probed = ParseProbe(lines) ?? throw new AnalysisException(
            BlackDetector.Explain(1, lines), string.Join(" ", lines.TakeLast(3)).Trim());
        _probed[file] = (info.Length, info.LastWriteTimeUtc, probed);
        return probed;
    }

    /// <summary>Forgets what was probed of a file (after its chapters are written).</summary>
    public void Forget(string file) => _probed.TryRemove(file, out _);

    /// <summary>Reads ffmpeg's description of its input; null when it didn't recognise the file at all.</summary>
    public static MediaInfo? ParseProbe(IEnumerable<string> lines)
    {
        string? container = null;
        double duration = 0;
        string? video = null;
        string? audio = null;
        double fps = 0;
        int width = 0;
        int height = 0;
        int chapters = 0;
        foreach (string line in lines)
        {
            if (container is null && InputLine().Match(line) is { Success: true } input)
            {
                container = input.Groups[1].Value;
            }
            else if (duration == 0 && DurationLine().Match(line) is { Success: true } length)
            {
                duration = int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                           + int.Parse(length.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                           + double.Parse(length.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else if (ChapterLine().IsMatch(line))
            {
                chapters++;
            }
            else if (video is null && !line.Contains("attached pic", StringComparison.OrdinalIgnoreCase) && VideoLine().Match(line) is { Success: true } stream)
            {
                video = stream.Groups[1].Value;
                if (FpsPart().Match(line) is { Success: true } rate)
                {
                    fps = double.Parse(rate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                else if (TbrPart().Match(line) is { Success: true } tbr)
                {
                    fps = double.Parse(tbr.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) * (tbr.Groups[2].Value == "k" ? 1000 : 1);
                }

                if (SizePart().Match(line) is { Success: true } size)
                {
                    width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                    height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            else if (audio is null && AudioLine().Match(line) is { Success: true } sound)
            {
                audio = sound.Groups[1].Value;
            }
        }

        return container is null && video is null ? null : new MediaInfo(duration, fps, container ?? "", video, audio, width, height, chapters);
    }

    // Square pixels (DVDs and some SD recordings are stored stretched), then no taller than asked.
    private static string Shrink(int height) =>
        $"scale=trunc(ih*dar/2)*2:ih,setsar=1,scale=-2:'min({height},ih)'";

    /// <summary>One frame as a JPEG, exactly at <paramref name="seconds"/>; null when there's no picture there.</summary>
    public async Task<byte[]?> FrameAsync(string file, double seconds, int height, CancellationToken cancellationToken)
    {
        await _frames.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var jpeg = new MemoryStream();
            var (exit, _) = await Ffmpeg.RunAsync(settings.Ffmpeg,
            [
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-ss", Seconds(seconds), "-i", file,
                "-map", "0:v:0", "-frames:v", "1", "-vf", Shrink(Math.Clamp(height, 90, 1080)),
                "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "pipe:1"
            ], jpeg, null, cancellationToken).ConfigureAwait(false);
            return exit == 0 && jpeg.Length > 0 ? jpeg.ToArray() : null;
        }
        finally
        {
            _frames.Release();
        }
    }

    /// <summary>
    /// A light preview from <paramref name="start"/> on (540p H.264 and stereo AAC, fragmented MP4) for files the browser
    /// can't play as they are. Its own clock starts at zero; the page adds the start back.
    /// </summary>
    public async Task StreamAsync(string file, double start, HttpContext http)
    {
        CancellationToken cancellationToken = http.RequestAborted;
        http.Response.ContentType = "video/mp4";
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var (exit, lines) = await Ffmpeg.RunAsync(settings.Ffmpeg,
            [
                "-hide_banner", "-nostats", "-nostdin", "-loglevel", "error",
                "-ss", Seconds(start), "-i", file,
                "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn",
                "-vf", Shrink(540) + ",format=yuv420p",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-g", "48",
                "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"
            ], http.Response.Body, "Review preview", cancellationToken).ConfigureAwait(false);
            if (exit != 0 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Auto Chapters review preview of {File} stopped (exit {Exit}): {Tail}", file, exit, string.Join(" ", lines.TakeLast(2)));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The page moved on (seeked, paused, or went to the next programme).
        }
    }

    public static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Runs ffmpeg (or another tool) to the end, gathering what it prints and, optionally, copying its output somewhere.</summary>
internal static class Ffmpeg
{
    /// <param name="action">Shown on the performance page while it runs; null to leave it off.</param>
    public static async Task<(int ExitCode, List<string> Errors)> RunAsync(string exe, IEnumerable<string> arguments, Stream? output, string? action,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(exe)}.");
        if (action is not null)
        {
            Work.Track(process, "Auto Chapters", action);
        }

        List<string> lines = [];
        Task errors = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    lock (lines)
                    {
                        lines.Add(line);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Killed part way (the page moved on).
            }
        }, CancellationToken.None);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output ?? Stream.Null, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        lock (lines)
        {
            return (process.ExitCode, [.. lines]);
        }
    }
}
