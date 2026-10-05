using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SchedularrTV.AutoChapters;

/// <summary>How a fade to black is recognised.</summary>
public sealed record DetectionOptions(TimeSpan MinBlack, double PixelThreshold, bool RequireSilence)
{
    public static DetectionOptions Default { get; } = new(TimeSpan.FromSeconds(0.4), 0.1, true);

    /// <summary>Decoder threads; 0 lets ffmpeg use every core.</summary>
    public int Threads { get; init; }

    /// <summary>
    /// ffmpeg's <c>-hwaccel</c> (<c>auto</c>, <c>drm</c> on a Raspberry Pi, <c>cuda</c>, <c>qsv</c>…), or null to decode
    /// in software. Only the decode moves to the video chip; the frames come back for the black and silence filters.
    /// </summary>
    public string? HardwareDecode { get; init; }

    /// <summary>The same options decoding in software, for when the hardware decoder can't read a file.</summary>
    public DetectionOptions InSoftware() => this with { HardwareDecode = null };
}

/// <summary>A stretch of black picture or silent sound, in seconds from the start of the file.</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Middle => Start + (End - Start) / 2;

    public bool Overlaps(TimeRange other, TimeSpan slack) => Start - slack < other.End && other.Start < End + slack;
}

/// <summary>What analysing one file found.</summary>
public sealed record Detection(IReadOnlyList<TimeSpan> Markers, TimeSpan? Duration, int BlackSpans, int SilentSpans);

/// <summary>
/// Finds the points where a programme fades to black and back (where its ad breaks were) with ffmpeg's
/// <c>blackdetect</c> and <c>silencedetect</c> filters. The picture is shrunk first, which keeps the decode cheap
/// without changing what counts as black.
/// </summary>
public static partial class BlackDetector
{
    /// <summary>Markers this close to the start or end are titles and credits, not breaks.</summary>
    public static readonly TimeSpan EdgeMargin = TimeSpan.FromSeconds(45);

    /// <summary>Fades this close together are one break (a fade out, a station card, a fade in).</summary>
    public static readonly TimeSpan MergeWithin = TimeSpan.FromSeconds(20);

    /// <summary>How far apart picture and sound may drop out and still be the same break.</summary>
    public static readonly TimeSpan SilenceSlack = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"black_start:\s*([\d.]+)\s+black_end:\s*([\d.]+)")]
    private static partial Regex BlackLine();

    [GeneratedRegex(@"silence_start:\s*(-?[\d.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end:\s*([\d.]+)")]
    private static partial Regex SilenceEnd();

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):([\d.]+)")]
    private static partial Regex DurationLine();

    public static IReadOnlyList<string> Arguments(string file, DetectionOptions options)
    {
        string minBlack = options.MinBlack.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string threshold = options.PixelThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        List<string> arguments = ["-hide_banner", "-nostats", "-nostdin"];
        if (options.HardwareDecode is { Length: > 0 } hardware)
        {
            arguments.AddRange(["-hwaccel", hardware]);
        }

        if (options.Threads > 0)
        {
            arguments.AddRange(["-threads", options.Threads.ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(
        [
            "-i", file,
            "-map", "0:v:0", "-vf", $"scale=320:-2,blackdetect=d={minBlack}:pic_th=0.98:pix_th={threshold}"
        ]);

        if (options.RequireSilence)
        {
            // The trailing "?" lets a file without sound through; then no fade can be confirmed by it.
            arguments.AddRange(["-map", "0:a:0?", "-af", $"silencedetect=n=-45dB:d={minBlack}"]);
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.AddRange(["-sn", "-dn", "-f", "null", "-"]);
        return arguments;
    }

    /// <summary>
    /// Runs ffmpeg over a file. Throws when ffmpeg can't be started or fails. If a hardware decoder was asked for and
    /// ffmpeg fails with it, the file is read again in software.
    /// </summary>
    public static async Task<Detection> AnalyseAsync(string ffmpeg, string file, TimeSpan? knownDuration, DetectionOptions options, CancellationToken cancellationToken)
    {
        if (options.HardwareDecode is { Length: > 0 })
        {
            try
            {
                return await AnalyseOnceAsync(ffmpeg, file, knownDuration, options, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
            {
                options = options.InSoftware();
            }
        }

        return await AnalyseOnceAsync(ffmpeg, file, knownDuration, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Detection> AnalyseOnceAsync(string ffmpeg, string file, TimeSpan? knownDuration, DetectionOptions options,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in Arguments(file, options))
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({ffmpeg}).");
        try
        {
            // Analysis runs flat out in the background; it shouldn't make the rest of the computer stutter.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
        }

        List<string> lines = [];
        Task output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lines.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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

        if (process.ExitCode != 0)
        {
            string tail = string.Join(" ", lines.TakeLast(3)).Trim();
            throw new InvalidOperationException($"ffmpeg couldn't read the file (exit code {process.ExitCode}): {tail}");
        }

        return Parse(lines, knownDuration, options);
    }

    /// <summary>Turns ffmpeg's report into chapter markers: the middle of each fade to black (with silence, if required).</summary>
    public static Detection Parse(IEnumerable<string> lines, TimeSpan? knownDuration, DetectionOptions options)
    {
        List<TimeRange> black = [];
        List<TimeRange> silence = [];
        TimeSpan? openSilence = null;
        TimeSpan? duration = knownDuration is { Ticks: > 0 } ? knownDuration : null;

        foreach (string line in lines)
        {
            if (BlackLine().Match(line) is { Success: true } blackMatch)
            {
                black.Add(new TimeRange(Seconds(blackMatch.Groups[1].Value), Seconds(blackMatch.Groups[2].Value)));
                continue;
            }

            if (SilenceStart().Match(line) is { Success: true } silenceStart)
            {
                openSilence = Seconds(silenceStart.Groups[1].Value);
                continue;
            }

            if (SilenceEnd().Match(line) is { Success: true } silenceEnd && openSilence is { } began)
            {
                silence.Add(new TimeRange(began < TimeSpan.Zero ? TimeSpan.Zero : began, Seconds(silenceEnd.Groups[1].Value)));
                openSilence = null;
                continue;
            }

            if (duration is null && DurationLine().Match(line) is { Success: true } durationMatch)
            {
                duration = TimeSpan.FromHours(int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture))
                           + TimeSpan.FromMinutes(int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture))
                           + Seconds(durationMatch.Groups[3].Value);
            }
        }

        // Silence still running at the end of the file.
        if (openSilence is { } trailing && duration is { } end)
        {
            silence.Add(new TimeRange(trailing, end));
        }

        IEnumerable<TimeRange> breaks = options.RequireSilence
            ? black.Where(fade => silence.Any(quiet => fade.Overlaps(quiet, SilenceSlack)))
            : black;

        List<TimeSpan> markers = [];
        foreach (TimeRange fade in breaks.OrderBy(fade => fade.Start))
        {
            TimeSpan at = fade.Middle;
            if (at < EdgeMargin || (duration is { } length && at > length - EdgeMargin))
            {
                continue;
            }

            // A fade out, a moment of black and a fade in close together are one break: keep the first.
            if (markers.Count > 0 && at - markers[^1] < MergeWithin)
            {
                continue;
            }

            markers.Add(TimeSpan.FromMilliseconds(Math.Round(at.TotalMilliseconds)));
        }

        return new Detection(markers, duration, black.Count, silence.Count);
    }

    private static TimeSpan Seconds(string text) =>
        TimeSpan.FromSeconds(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
}
