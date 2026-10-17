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

    /// <summary>
    /// Decodes only what the black and silence filters need: frames nothing else refers to (most B-frames) are skipped,
    /// and so is the deblocking filter. A fade to black lasts far longer than the frames skipped, so the same breaks are
    /// found for well under half the decoding.
    /// </summary>
    public bool FastDecode { get; init; }

    /// <summary>
    /// The fastest the file is read, in multiples of real time (ffmpeg's <c>-readrate</c>); 0 for as fast as it can.
    /// Capping it spreads an analysis over longer at a fraction of the CPU.
    /// </summary>
    public double MaxSpeed { get; init; }

    /// <summary>
    /// With <c>cuda</c>, <c>vaapi</c> or <c>qsv</c> decoding, the frames stay on the graphics card and are shrunk there
    /// (<c>scale_cuda</c> and the like), so only a tiny picture comes back to the processor instead of every full-size
    /// frame. Other decoders ignore it.
    /// </summary>
    public bool GpuScale { get; init; }

    /// <summary>The GPU scaler for <see cref="HardwareDecode"/> when <see cref="GpuScale"/> is on and it has one; null otherwise.</summary>
    public string? GpuScaler => GpuScale ? HardwareDecode switch
    {
        // A fixed size: black is judged by the share of dark pixels, so the picture's shape doesn't matter.
        "cuda" => "scale_cuda=320:180",
        "vaapi" => "scale_vaapi=w=320:h=180",
        "qsv" => "scale_qsv=w=320:h=180",
        _ => null
    } : null;

    /// <summary>The same options decoding in software, for when the hardware decoder can't read a file.</summary>
    public DetectionOptions InSoftware() => this with { HardwareDecode = null, GpuScale = false };
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

    private static volatile bool _readRateSupported = true;

    /// <summary>Whether this ffmpeg takes <c>-readrate</c> (5.1 and later); found out the first time it's tried.</summary>
    public static bool ReadRateSupported => _readRateSupported;

    public static IReadOnlyList<string> Arguments(string file, DetectionOptions options)
    {
        string minBlack = options.MinBlack.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string threshold = options.PixelThreshold.ToString("0.###", CultureInfo.InvariantCulture);
        List<string> arguments = ["-hide_banner", "-nostats", "-nostdin"];
        string? gpuScaler = options.GpuScaler;
        if (options.HardwareDecode is { Length: > 0 } hardware)
        {
            arguments.AddRange(["-hwaccel", hardware]);
            if (gpuScaler is not null)
            {
                // Decoded frames stay in the card's memory for its scaler.
                arguments.AddRange(["-hwaccel_output_format", hardware]);
            }
        }

        if (options.Threads > 0)
        {
            arguments.AddRange(["-threads", options.Threads.ToString(CultureInfo.InvariantCulture)]);
        }

        if (options.FastDecode)
        {
            arguments.AddRange(["-skip_frame", "nonref", "-skip_loop_filter", "all"]);
        }

        if (options.MaxSpeed > 0 && ReadRateSupported)
        {
            arguments.AddRange(["-readrate", options.MaxSpeed.ToString("0.##", CultureInfo.InvariantCulture)]);
        }

        // Shrunk on the card and brought back small (8- or 10-bit, whichever it decoded, then to grey for the black check),
        // or else with the cheapest software scaler: the picture is only checked for black, so smoothness doesn't matter.
        string shrink = gpuScaler is not null
            ? $"{gpuScaler},hwdownload,format=nv12|p010le,format=gray"
            : "scale=320:-2:flags=fast_bilinear";
        arguments.AddRange(
        [
            "-i", file,
            "-map", "0:v:0", "-vf", $"{shrink},blackdetect=d={minBlack}:pic_th=0.98:pix_th={threshold}"
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

    private static int _gpuScaleFailures;

    /// <summary>
    /// Whether frames are still kept on the graphics card: not after it has failed a few times running (an ffmpeg built
    /// without the card's scaler, say), so every file isn't tried that way first only to fail.
    /// </summary>
    public static bool GpuScaleWorks => Volatile.Read(ref _gpuScaleFailures) < 3;

    /// <summary>
    /// Runs ffmpeg over a file. Throws when ffmpeg can't be started or fails. With a hardware decoder, frames are first
    /// kept and shrunk on the graphics card (when it can); if ffmpeg fails that way the frames come back full size, and if
    /// it fails with the hardware decoder at all, the file is read again in software.
    /// </summary>
    public static async Task<Detection> AnalyseAsync(string ffmpeg, string file, TimeSpan? knownDuration, DetectionOptions options, CancellationToken cancellationToken)
    {
        if (options.GpuScaler is not null && GpuScaleWorks)
        {
            try
            {
                Detection found = await AnalyseOnceAsync(ffmpeg, file, knownDuration, options, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _gpuScaleFailures, 0);
                return found;
            }
            catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _gpuScaleFailures);
            }
        }

        options = options with { GpuScale = false };
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
        SchedularrTV.Plugins.Performance.Work.Track(process, "Auto Chapters", "Chapter detection");
        try
        {
            // Analysis is background work: at the lowest priority it only ever gets what nothing else (the channels on
            // air included) wants, so it can't make the rest of the computer stutter.
            process.PriorityClass = ProcessPriorityClass.Idle;
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
            // ffmpeg older than 5.1 has no -readrate: read flat out from now on rather than not at all.
            if (options.MaxSpeed > 0 && _readRateSupported && lines.Any(line => line.Contains("readrate", StringComparison.OrdinalIgnoreCase)))
            {
                _readRateSupported = false;
                return await AnalyseOnceAsync(ffmpeg, file, knownDuration, options, cancellationToken).ConfigureAwait(false);
            }

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
