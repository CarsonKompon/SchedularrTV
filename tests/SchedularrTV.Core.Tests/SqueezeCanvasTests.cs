using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Providers.ErsatzTv;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class SqueezeCanvasTests
{
    private static SqueezeJob Job() => new("credits.mp4", TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(10),
        new("Next show", null, "8 PM", null), [], "Channel", "Up next", "#FFFFFF", "#000000", 1, false);

    [Theory]
    [InlineData("16:9", 1280)]
    [InlineData("4:3", 960)]
    [InlineData("1:1", 720)]
    public void A_new_canvas_is_only_the_full_screen_source(string ratio, int width)
    {
        var canvas = SqueezeCanvas.Create(ratio);
        var source = Assert.Single(canvas.Layers);
        Assert.Equal(GeneratedLayerKind.Video, source.Kind);
        Assert.Equal(SqueezeCanvas.Credits, source.Video!.Episode);
        Assert.True(source.Video.Stretch);
        Assert.Equal((0f, 0f, (float)width, 720f), (source.X, source.Y, source.Width, source.Height));
        var composition = canvas.Compose(Job());
        var video = Assert.IsType<PromoVideoLayer>(Assert.Single(composition.Layers));
        Assert.Equal(Job().SourceStart, video.SourceStart);
        Assert.False(video.Loop);
        Assert.False(video.StartsWithLayer);
        Assert.Equal(Job().SourcePath, Assert.Single(composition.LayerAudio).Path);
    }

    [Fact]
    public void Canvas_selection_matches_source_shape_and_always_uses_a_lone_design()
    {
        var wide = SqueezeCanvas.Create("16:9");
        var classic = SqueezeCanvas.Create("4:3");
        Assert.Same(wide, SqueezeCanvas.Select([classic, wide], 16 / 9d));
        Assert.Same(classic, SqueezeCanvas.Select([wide, classic], 4 / 3d));
        Assert.Same(classic, SqueezeCanvas.Select([classic], 16 / 9d));
        Assert.Same(wide, SqueezeCanvas.Select([classic, wide], null));
        Assert.Throws<ArgumentException>(() => SqueezeCanvas.Create(""));
    }

    [Fact]
    public void Copying_a_look_isolates_canvas_layers_and_keyframes_and_presets_stay_presets()
    {
        var original = new CreditSqueezeSection { UseCanvas = true, Canvases = [SqueezeCanvas.Create("4:3")] };
        original.Canvases[0].Layers[0].Keyframes = [new() { Width = 960, Height = 720 }];
        var copy = original.WithOnly(original);
        copy.Canvases[0].Layers[0].Keyframes![0].Width = 100;
        copy.Canvases[0].Layers[0].Video!.Stretch = false;
        Assert.Equal(960, original.Canvases[0].Layers[0].Keyframes![0].Width);
        Assert.True(original.Canvases[0].Layers[0].Video!.Stretch);
        Assert.True(copy.UseCanvas);
        Assert.False(new CreditSqueezeSection().UseCanvas);
        Assert.Null(Job().Canvases);
    }

    [Fact]
    public void Channel_crop_inherits_and_changes_the_render_key()
    {
        using var services = new ServiceCollection().AddSingleton<IOptions<ErsatzTvOptions>>(Options.Create(
            new ErsatzTvOptions { Encoding = new ErsatzTvEncodingOptions { ScalingMode = "crop", AspectRatio = "16:9" } })).BuildServiceProvider();
        using var renderer = new SqueezeRenderer(null!, services, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance);
        var channel = new ChannelConfig { Id = "channel", Name = "Channel" };
        var job = Job() with { Canvases = [SqueezeCanvas.Create("4:3")] };
        var cropped = renderer.ForChannel(job, channel);
        Assert.True(cropped.CanvasCrop);
        channel.Encoding = new ChannelEncodingConfig { ScalingMode = "scale_and_pad" };
        var fitted = renderer.ForChannel(job, channel);
        Assert.False(fitted.CanvasCrop);
        Assert.NotEqual(SqueezeRenderer.Key(cropped), SqueezeRenderer.Key(fitted));
        Assert.Equal("4:3", SqueezeCanvas.Select(cropped.Canvases, 16 / 9d)!.AspectRatio);
    }

    [Fact]
    public void Stretch_differs_from_fit_and_crop_and_is_shared_with_generated_promos()
    {
        var video = new PromoVideoLayer { Path = "file.mp4", Bounds = SKRect.Create(0, 0, 320, 720), Stretch = true };
        Assert.DoesNotContain("force_original_aspect_ratio", video.SizeFilter(30));
        Assert.Contains("scale=320:720", video.SizeFilter(30));
        Assert.Contains("force_original_aspect_ratio=decrease", (video with { Stretch = false }).SizeFilter(30));
        Assert.Contains("force_original_aspect_ratio=increase", (video with { Stretch = false, Fill = true }).SizeFilter(30));
        var template = new GeneratedPromoTemplate { Layers = [new GeneratedLayer { Kind = GeneratedLayerKind.Video, Video = new() { Stretch = true } }] };
        var promo = new ResolvedGeneratedPromo(template, [Array.Empty<string>()], null, "test")
        { LayerVideos = new Dictionary<int, PromoVideoSource> { [0] = new("file.mp4", TimeSpan.Zero) } };
        Assert.True(Assert.Single(GeneratedPromoResolver.Compose(promo).Layers.OfType<PromoVideoLayer>()).Stretch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rendered_source_really_squeezes_and_reveals_the_background_with_channel_fit(bool crop)
    {
        string folder = Path.Combine(Path.GetTempPath(), "squeeze-canvas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "source.mp4"), output = Path.Combine(folder, "squeeze.mp4"), frame = Path.Combine(folder, "frame.png");
            await Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=30:d=1", "-pix_fmt", "yuv420p", source);
            var canvas = SqueezeCanvas.Create("4:3");
            canvas.BackgroundColor = "#0000FF";
            var layer = canvas.Layers[0];
            layer.Video!.IncludeAudio = false;
            layer.Keyframes = [new() { Width = 960, Height = 720 }, new() { At = TimeSpan.FromSeconds(0.2), Width = 480, Height = 720 }];
            var job = Job() with { SourcePath = source, SourceStart = TimeSpan.Zero, Length = TimeSpan.FromSeconds(0.5), CanvasCrop = crop };
            await new PromoRenderer().RenderAsync(canvas.Compose(job, outputHeight: 144), output);
            await Run("ffmpeg", "-v", "error", "-y", "-ss", "0.3", "-i", output, "-frames:v", "1", frame);
            using var bitmap = SKBitmap.Decode(await File.ReadAllBytesAsync(frame));
            Assert.Equal((192, 144), (bitmap.Width, bitmap.Height));
            Assert.True(bitmap.GetPixel(40, 72).Red > 220, "The source remains in its squeezed layer.");
            Assert.True(bitmap.GetPixel(140, 72).Blue > 220, "The source's animated resize reveals the background.");
            var top = bitmap.GetPixel(40, 8);
            if (crop) Assert.True(top.Red > 220, "Channel fill removes source bars before squeezing.");
            else Assert.True(top.Red < 20 && top.Green < 20 && top.Blue < 20, "Channel fit keeps bars inside the squeezed source.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task Run(string tool, params string[] arguments)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, await errors);
    }
}
