using System.Diagnostics;
using System.Globalization;
using SkiaSharp;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Small, seekable file-video frames for interactive canvases. Call sequentially per editor.</summary>
public sealed class CanvasVideoPreview(string ffmpeg, string ffprobe)
{
    private readonly Dictionary<string, PromoVideoInfo> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _frames = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public async Task<byte[]> FrameAsync(PromoVideoLayer layer, TimeSpan playback, int fps, CancellationToken cancellationToken)
    {
        var file = new FileInfo(layer.Path);
        if (!file.Exists) throw new IOException($"Video file not found: {Path.GetFileName(layer.Path)}");
        string sourceKey = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        if (!_sources.TryGetValue(sourceKey, out var source))
        {
            source = await PromoVideoDecoder.InfoAsync(ffprobe, file.FullName, cancellationToken).ConfigureAwait(false);
            if (_sources.Count >= 32) _sources.Clear();
            _sources[sourceKey] = source;
        }
        double second = layer.SourceSecond(playback, source.Duration, fps);
        // The timeline can run faster than the file. Its last frame starts at the
        // source frame interval, not one timeline frame before the end.
        if (source.Duration > 0 && source.FrameRate > 0)
            second = Math.Min(second, Math.Max(0, source.Duration - 1 / source.FrameRate));
        (int width, int height) = layer.FrameSize;
        float scale = Math.Min(1, 480f / Math.Max(width, height));
        var preview = layer with { Bounds = SKRect.Create(0, 0, width * scale, height * scale), Keyframes = null };
        string filter = preview.SizeFilter(fps);
        string seek = second.ToString("0.######", CultureInfo.InvariantCulture);
        string key = $"{sourceKey}|{seek}|{filter}";
        if (_frames.TryGetValue(key, out var cached)) return cached;

        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", seek];
        if (source.Decoder is { } decoder) args.AddRange(["-c:v", decoder]);
        args.AddRange(["-i", file.FullName, "-an", "-sn", "-vf", filter, "-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-f", "image2pipe", "pipe:1"]);
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start ffmpeg for the video preview.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        using var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output, limit.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await Task.WhenAll(copy, process.WaitForExitAsync(limit.Token)).ConfigureAwait(false);
            string message = await error.ConfigureAwait(false);
            if (process.ExitCode != 0 || output.Length == 0) throw new IOException($"Couldn't decode {file.Name}: {(string.IsNullOrWhiteSpace(message) ? "No frame at this time." : message.Trim())}");
            byte[] bytes = output.ToArray();
            while (_frames.Count >= 24 && _order.TryDequeue(out string? oldest)) _frames.Remove(oldest);
            _frames[key] = bytes;
            _order.Enqueue(key);
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Video preview timed out: {file.Name}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            try { await Task.WhenAll(copy, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
}
