using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SchedularrTV.Plugins.Performance;

namespace SchedularrTV.GeneratedPromos;

public sealed record PromoVideoInfo(double Duration, double FrameRate, string? Decoder);

/// <summary>FFmpeg's native VP8/VP9 decoders discard WebM alpha; libvpx preserves it.</summary>
public static class PromoVideoDecoder
{
    public static async Task<double> DurationAsync(string ffprobePath, string path, CancellationToken cancellationToken) =>
        (await InfoAsync(ffprobePath, path, cancellationToken).ConfigureAwait(false)).Duration;

    public static async Task<PromoVideoInfo> InfoAsync(string ffprobePath, string path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(ffprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration,avg_frame_rate,r_frame_rate,codec_name:format=duration", "-of", "json", path })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ffprobe.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var text = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) return new(0, 0, null);
            using var doc = JsonDocument.Parse(await text);
            JsonElement? stream = doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0 ? streams[0] : null;
            string? duration = stream is { } video && video.TryGetProperty("duration", out var own) ? own.GetString()
                : doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var total) ? total.GetString() : null;
            double seconds = double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? Math.Max(0, value) : 0;
            string? Read(string key) => stream is { } element && element.TryGetProperty(key, out var item) ? item.GetString() : null;
            double Rate(string? text)
            {
                string[] parts = (text ?? "").Split('/');
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double top)) return 0;
                double bottom = 1;
                if (parts.Length > 1 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bottom)) return 0;
                double rate = top / bottom;
                return double.IsFinite(rate) && rate > 0 ? rate : 0;
            }
            double fps = Rate(Read("avg_frame_rate"));
            if (fps <= 0) fps = Rate(Read("r_frame_rate"));
            return new(seconds, fps, Read("codec_name") switch { "vp8" => "libvpx", "vp9" => "libvpx-vp9", _ => null });
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    public static async Task<string?> FindAsync(string ffprobePath, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var info = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in (string[])["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name",
                     "-of", "default=noprint_wrappers=1:nokey=1", path])
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffprobe could not be started.");
        Work.Track(process, "Generated Promos", "Probing media");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string codec = (await output.ConfigureAwait(false)).Trim();
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? codec switch { "vp8" => "libvpx", "vp9" => "libvpx-vp9", _ => null } : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
