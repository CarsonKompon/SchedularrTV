using System.Diagnostics;
using SkiaSharp;

namespace SchedularrTV.Overlays;

public sealed record OverlayPreviewProgress(string Message, double Percent);
public sealed record OverlayPreviewVideo(string Name, int Width, int Height, double Seconds, bool Transparent)
{
    public string Url => $"api/overlays/previews/{Name}";
}

/// <summary>On-demand editor previews, separate from the on-air render cache. Includes real video-layer frames.</summary>
public sealed class OverlayPreviewVideos(string folder, Func<string> ffmpeg, OverlayVideos videos)
{
    private readonly SemaphoreSlim _render = new(1, 1);

    public async Task<OverlayPreviewVideo> RenderAsync(Overlay overlay, IReadOnlyDictionary<string, string> tokens,
        double seconds, bool wholeProgramme, bool transparent, int maxWidth = 1280,
        IProgress<OverlayPreviewProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds is < 0.1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Preview length must be between 0.1 and 300 seconds.");
        Overlay snapshot = overlay.Copy();
        var text = new Dictionary<string, string>(tokens);
        progress?.Report(new("Waiting to render…", 0));
        await _render.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RenderCoreAsync(snapshot, text, seconds, wholeProgramme, transparent, maxWidth, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _render.Release(); }
    }

    private async Task<OverlayPreviewVideo> RenderCoreAsync(Overlay overlay, IReadOnlyDictionary<string, string> tokens,
        double seconds, bool wholeProgramme, bool transparent, int maxWidth, IProgress<OverlayPreviewProgress>? progress, CancellationToken token)
    {
        int sourceWidth = overlay.Kind == OverlayKinds.File ? 1920 : Math.Clamp(overlay.Width, 64, 7680);
        int sourceHeight = overlay.Kind == OverlayKinds.File ? 1080 : Math.Clamp(overlay.Height, 64, 4320);
        double scale = maxWidth <= 0 ? 1 : Math.Min(1, Math.Clamp(maxWidth, 64, 7680) / (double)sourceWidth);
        int width = Math.Max(2, (int)Math.Round(sourceWidth * scale) / 2 * 2);
        int height = Math.Max(2, (int)Math.Round(sourceHeight * scale) / 2 * 2);
        double fps = OverlayAnimation.FpsOf(overlay);
        int frames = Math.Max(1, (int)Math.Ceiling(seconds * fps));
        var clock = new OverlayClock(0, seconds, Math.Max(0, overlay.FadeSeconds), FadeIn: true, FadeOut: !wholeProgramme, LayerOuts: !wholeProgramme);
        var clips = overlay.Kind == OverlayKinds.File
            ? OverlayRenderer.IsVideo(overlay.File) ? new[] { (Key: overlay.File!, Path: overlay.File!, Start: 0d) } : []
            : overlay.Layers.Where(layer => layer.Kind == OverlayLayerKinds.Video && !string.IsNullOrWhiteSpace(layer.Path))
                .Select(layer => (Key: OverlayRenderer.FrameKey(overlay, layer), Path: layer.Path!, Start: OverlayHierarchy.EffectiveTiming(overlay, layer).InSeconds)).ToArray();

        progress?.Report(new("Preparing video frames…", 0));
        foreach (string path in clips.Select(clip => clip.Path).Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(path)) throw new InvalidOperationException($"Video file not found: {path}");
            await videos.PrepareFramesAsync(path, seconds, fps).WaitAsync(token).ConfigureAwait(false);
        }
        if (overlay.Kind == OverlayKinds.File && (string.IsNullOrWhiteSpace(overlay.File) || !File.Exists(overlay.File)))
            throw new InvalidOperationException("Choose an available picture or video for the file overlay before rendering a preview.");

        Directory.CreateDirectory(folder);
        string name = "preview-" + Guid.NewGuid().ToString("N") + (transparent ? ".webm" : ".mp4");
        string final = Path.Combine(folder, name);
        string temp = final + ".part";
        var start = new ProcessStartInfo(ffmpeg())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        string[] codec = transparent
            ? ["-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-b:v", "0", "-crf", "30", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1", "-auto-alt-ref", "0", "-f", "webm"]
            : ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-preset", "veryfast", "-movflags", "+faststart", "-f", "mp4"];
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba",
                     "-s", $"{width}x{height}", "-r", OverlayAnimation.FpsText(fps), "-i", "-", "-an", .. codec, temp])
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg couldn't be started.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            SKImageInfo output = info.WithAlphaType(SKAlphaType.Unpremul);
            using SKSurface surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Couldn't create the preview canvas.");
            using var pixels = new SKBitmap(output);
            byte[] bytes = new byte[output.BytesSize];
            for (int index = 0; index < frames; index++)
            {
                token.ThrowIfCancellationRequested();
                double at = index / fps;
                Dictionary<string, string> stills = new(StringComparer.Ordinal);
                foreach (var clip in clips)
                {
                    if (at < clip.Start || stills.ContainsKey(clip.Key)) continue;
                    string? image = await videos.FrameAsync(clip.Path, at - clip.Start, fps, token).ConfigureAwait(false);
                    if (image is null) throw new InvalidOperationException($"Couldn't decode a video frame: {clip.Path}");
                    stills[clip.Key] = image;
                }
                OverlayRenderer.DrawPreviewFrame(surface.Canvas, overlay, tokens, width, height, !transparent, stills, clock with { Seconds = at });
                if (!surface.ReadPixels(output, pixels.GetPixels(), pixels.RowBytes, 0, 0))
                    throw new InvalidOperationException("Couldn't read a preview frame.");
                pixels.GetPixelSpan().CopyTo(bytes);
                await process.StandardInput.BaseStream.WriteAsync(bytes, token).ConfigureAwait(false);
                if (index % Math.Max(1, (int)fps / 4) == 0)
                    progress?.Report(new("Rendering preview…", (index + 1) * 100d / frames));
            }
            process.StandardInput.Close();
            progress?.Report(new("Finishing video…", 100));
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string log = (await errors.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0 || !File.Exists(temp)) throw new InvalidOperationException($"ffmpeg couldn't render the preview: {log}");
            File.Move(temp, final);
            return new(name, width, height, frames / fps, transparent);
        }
        catch (Exception ex)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string log = (await errors.ConfigureAwait(false)).Trim();
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (ex is IOException && log.Length > 0) throw new InvalidOperationException($"ffmpeg couldn't render the preview: {log}", ex);
            throw;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Only completed preview files in this service's directory can be downloaded.</summary>
    public string? RenderedFile(string name)
    {
        if (Path.GetFileName(name) != name || !name.StartsWith("preview-", StringComparison.Ordinal)
            || Path.GetExtension(name) is not (".webm" or ".mp4")
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name)[8..], "N", out _)) return null;
        string path = Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }
}
