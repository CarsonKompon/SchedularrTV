using System.Diagnostics;
using System.Text.Json;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Overlays;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class OverlayPreviewVideoTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "overlay-preview-tests-" + Guid.NewGuid().ToString("N"));
    private OverlayPreviewVideos Renderer() => new(Path.Combine(_folder, "previews"), () => "ffmpeg", new OverlayVideos(() => "ffmpeg", Path.Combine(_folder, "frames")));
    public void Dispose() => TestFolders.Delete(_folder);

    private static Overlay Design() => new()
    {
        Width = 64, Height = 64, Fps = 10, FadeSeconds = 0,
        Layers = [new() { Kind = OverlayLayerKinds.Rect, X = 16, Y = 16, Width = 32, Height = 32, Color = "#ff0000" }]
    };

    [Theory]
    [InlineData(true, "vp9")]
    [InlineData(false, "h264")]
    public async Task Exports_playable_video_with_the_requested_format_dimensions_and_alpha(bool transparent, string codec)
    {
        var renderer = Renderer();
        OverlayPreviewVideo video = await renderer.RenderAsync(Design(), new Dictionary<string, string>(), 0.5, false, transparent, 64);
        string file = Assert.IsType<string>(renderer.RenderedFile(video.Name));
        using JsonDocument probe = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_name,width,height,r_frame_rate:format=duration", "-of", "json", file));
        JsonElement stream = probe.RootElement.GetProperty("streams")[0];
        Assert.Equal(codec, stream.GetProperty("codec_name").GetString());
        Assert.Equal(64, stream.GetProperty("width").GetInt32());
        Assert.Equal(64, stream.GetProperty("height").GetInt32());
        Assert.Equal("10/1", stream.GetProperty("r_frame_rate").GetString());
        Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 0.49, 0.61);
        using SKBitmap frame = await Frame(file, 0.2, transparent);
        Assert.Equal(transparent ? 0 : 255, frame.GetPixel(4, 4).Alpha);
        Assert.True(frame.GetPixel(32, 32).Red > 220);
        Assert.True(frame.GetPixel(32, 32).Green < 30);
        Assert.False(File.Exists(file + ".part"));
        Assert.Null(renderer.RenderedFile("../" + video.Name));
        Assert.Null(renderer.RenderedFile(video.Name + ".part"));
        Assert.Null(renderer.RenderedFile("preview-not-a-guid.webm"));
    }

    [Fact]
    public async Task Export_draws_keyframes_and_masks_on_each_frame()
    {
        Overlay design = Design();
        design.Layers[0].Id = "target";
        design.Layers[0].Keyframes =
        [
            new() { At = TimeSpan.Zero, X = 0, Y = 16, Width = 16, Height = 32, Opacity = 100 },
            new() { At = TimeSpan.FromSeconds(0.4), X = 40, Y = 16, Width = 16, Height = 32, Opacity = 100 }
        ];
        design.Layers.Add(new() { Kind = OverlayLayerKinds.Rect, Width = 64, Height = 24,
            Mask = new() { Target = "target", Mode = GeneratedMaskMode.Subtract } });
        var renderer = Renderer();
        var video = await renderer.RenderAsync(design, new Dictionary<string, string>(), 0.5, true, true, 64);
        string file = renderer.RenderedFile(video.Name)!;
        using SKBitmap first = await Frame(file, 0, true);
        using SKBitmap last = await Frame(file, 0.4, true);
        Assert.True(first.GetPixel(8, 32).Alpha > 245);
        Assert.Equal(0, first.GetPixel(8, 20).Alpha);
        Assert.Equal(0, last.GetPixel(8, 32).Alpha);
        Assert.True(last.GetPixel(48, 32).Alpha > 245);
        Assert.Equal(0, last.GetPixel(48, 20).Alpha);
        Assert.Equal(TimeSpan.Zero, design.Layers[0].Keyframes![0].At);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Video_layers_and_file_overlays_play_real_frames(bool fileOverlay)
    {
        Directory.CreateDirectory(_folder);
        string source = Path.Combine(_folder, "source.mp4");
        await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=32x32:r=10:d=0.2",
            "-f", "lavfi", "-i", "color=blue:s=32x32:r=10:d=0.2", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", source);
        Overlay overlay = Design();
        if (fileOverlay)
        {
            overlay.Kind = OverlayKinds.File; overlay.File = source; overlay.Anchor = "center"; overlay.SizePercent = 50;
        }
        else
        {
            overlay.Layers[0].Kind = OverlayLayerKinds.Video; overlay.Layers[0].Path = source;
        }
        var renderer = Renderer();
        var video = await renderer.RenderAsync(overlay, new Dictionary<string, string>(), 0.4, true, true, 64);
        using SKBitmap first = await Frame(renderer.RenderedFile(video.Name)!, 0, true);
        using SKBitmap last = await Frame(renderer.RenderedFile(video.Name)!, 0.3, true);
        Assert.True(first.GetPixel(first.Width / 2, first.Height / 2).Red > 220);
        Assert.True(last.GetPixel(last.Width / 2, last.Height / 2).Blue > 220);
        Assert.Equal(0, last.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public async Task Cancellation_cleans_partial_output_and_releases_the_render_slot()
    {
        var renderer = Renderer();
        using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress(update => { if (update.Message == "Rendering preview…") cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(Design(), new Dictionary<string, string>(), 5, false, true,
            64, progress, cancel.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "previews")));
        var next = await renderer.RenderAsync(Design(), new Dictionary<string, string>(), 0.2, true, true, 64);
        Assert.NotNull(renderer.RenderedFile(next.Name));
    }

    private sealed class CallbackProgress(Action<OverlayPreviewProgress> action) : IProgress<OverlayPreviewProgress>
    {
        public void Report(OverlayPreviewProgress value) => action(value);
    }

    private async Task<SKBitmap> Frame(string video, double at, bool alpha)
    {
        string frame = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".png");
        string[] decoder = alpha ? ["-c:v", "libvpx-vp9"] : [];
        await Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", .. decoder, "-i", video, "-ss",
            at.ToString(System.Globalization.CultureInfo.InvariantCulture), "-frames:v", "1", "-pix_fmt", "rgba", frame]);
        return SKBitmap.Decode(frame);
    }

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
