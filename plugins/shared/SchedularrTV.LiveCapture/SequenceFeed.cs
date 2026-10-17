using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SchedularrTV.LiveCapture;

/// <summary>One stretch of a <see cref="SequenceFeed"/>: what plays, and for how long.</summary>
/// <param name="Input">A file, a stream address, or (with <paramref name="Lavfi"/>) an ffmpeg lavfi source.</param>
/// <param name="Duration">How long it plays: a commercial's length, or how long a channel stays up.</param>
/// <param name="Realtime">Read at its own speed (files and lavfi); a live stream already arrives that way.</param>
/// <param name="HasAudio">False to play silence with it, for a file without sound.</param>
public sealed record FeedSegment(string Input, TimeSpan Duration, bool Realtime, bool HasAudio = true, bool Lavfi = false, string? Label = null)
{
    /// <summary>A short black, silent stretch, for when there's nothing to play (or it failed).</summary>
    public static FeedSegment Black(int width, int height, TimeSpan duration) =>
        new($"color=c=black:s={width}x{height}:r={LiveEncodingSettings.Current.FrameRate}", duration, Realtime: true, HasAudio: false, Lavfi: true, Label: "black");
}

/// <summary>
/// One endless MPEG-TS stream made of a sequence of things played one after another (commercials, or a different
/// channel every few minutes), each normalised to the same size, frame rate and codecs, and stamped on one running
/// clock so the stream's timestamps keep going up across the joins. Used as the picture-in-picture window (and its
/// sound) of a live page channel.
/// </summary>
/// <remarks>
/// Each item is its own short ffmpeg run whose output is appended to the response; the timestamps continue because
/// each run starts at the wall-clock time since the stream began. The few hundred milliseconds between runs show as
/// the last picture held, and silence.
/// </remarks>
public static class SequenceFeed
{
    /// <param name="next">The next segment to play; null for nothing right now (a short black stretch plays).</param>
    public static async Task ServeAsync(HttpContext context, Func<CancellationToken, Task<FeedSegment?>> next, string ffmpeg, int width, int height,
        ILogger logger)
    {
        CancellationToken cancellationToken = context.RequestAborted;
        context.Response.ContentType = "video/mp2t";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        int failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                FeedSegment segment;
                try
                {
                    segment = await next(cancellationToken).ConfigureAwait(false) ?? FeedSegment.Black(width, height, TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't choose what plays next in the window; playing black.");
                    segment = FeedSegment.Black(width, height, TimeSpan.FromSeconds(10));
                }

                TimeSpan started = clock.Elapsed;
                bool ok = await PlayAsync(context.Response.Body, segment, ffmpeg, width, height, started, logger, cancellationToken).ConfigureAwait(false);

                // Something that fails straight away (a missing file, a channel that won't start) mustn't spin: after a
                // few in a row, black plays for a moment before trying again.
                failures = ok || clock.Elapsed - started > TimeSpan.FromSeconds(3) ? 0 : failures + 1;
                if (failures >= 3)
                {
                    failures = 0;
                    await PlayAsync(context.Response.Body, FeedSegment.Black(width, height, TimeSpan.FromSeconds(5)), ffmpeg, width, height, clock.Elapsed,
                        logger, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The viewer (the page channel's own ffmpeg) went away.
        }
    }

    /// <summary>The ffmpeg command for one segment, its timestamps starting at <paramref name="offset"/>.</summary>
    /// <param name="encoding">The live encoding settings (frame rate, encoder, preset); those in use now if not given.</param>
    public static IReadOnlyList<string> Arguments(FeedSegment segment, int width, int height, TimeSpan offset, LiveEncodingSettings? encoding = null)
    {
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        encoding ??= LiveEncodingSettings.Current;
        int fps = encoding.FrameRate;
        List<string> arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin"];
        if (segment.Realtime)
        {
            arguments.Add("-re");
        }

        if (segment.Lavfi)
        {
            arguments.AddRange(["-f", "lavfi"]);
        }

        arguments.AddRange(["-i", segment.Input]);
        if (!segment.HasAudio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        }

        arguments.AddRange(
        [
            "-map", "0:v:0", "-map", segment.HasAudio ? "0:a:0" : "1:a:0",
            "-t", Number(Math.Max(segment.Duration.TotalSeconds, 0.5)),
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p",
            "-af", "aresample=48000:async=1,aformat=channel_layouts=stereo"
        ]);
        // Only ever decoded again by the page channel's own ffmpeg, so it uses the live settings' encoder and speed.
        arguments.AddRange(encoding.Video.VideoArguments(2500, tune: "zerolatency"));
        arguments.AddRange(
        [
            "-g", (fps * 2).ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2",
            "-output_ts_offset", Number(offset.TotalSeconds),
            "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "pipe:1"
        ]);
        return arguments;
    }

    /// <returns>Whether the segment played through.</returns>
    private static async Task<bool> PlayAsync(Stream output, FeedSegment segment, string ffmpeg, int width, int height, TimeSpan offset, ILogger logger,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (string argument in Arguments(segment, width, height, offset))
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start ffmpeg ({ffmpeg}).");
            SchedularrTV.Plugins.Performance.Work.TrackAs(process, "Live channels", "Playing window video (ffmpeg)");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Couldn't start ffmpeg for the window: {Message}", ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return false;
        }

        using (process)
        {
            process.ErrorDataReceived += (_, line) =>
            {
                if (!string.IsNullOrWhiteSpace(line.Data))
                {
                    logger.LogDebug("ffmpeg (window, {Label}): {Line}", segment.Label ?? segment.Input, line.Data);
                }
            };
            process.BeginErrorReadLine();

            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    logger.LogInformation("The window's {Label} stopped early (ffmpeg exit code {Code}).", segment.Label ?? segment.Input, process.ExitCode);
                }

                return process.ExitCode == 0;
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }
}
