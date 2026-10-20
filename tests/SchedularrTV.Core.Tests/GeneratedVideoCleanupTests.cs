using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedVideoCleanupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "promo-cleanup-" + Guid.NewGuid().ToString("N"));
    public GeneratedVideoCleanupTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => TestFolders.Delete(_folder);
    private GeneratedVideoCleanup Recipe(string? file = null) => new()
    {
        Source = file ?? Path.Combine(_folder, "source.mov"),
        Regions = [new() { Start = .5, End = 4, Keys = [new() { At = 2, X = .1, Y = .2, Width = .3, Height = .1 }] }]
    };
    private PromoVideoCleanupService Service() => new(Options.Create(new GeneratedPromoOptions { OutputPath = _folder }), NullLogger<PromoVideoCleanupService>.Instance);

    [Fact]
    public void Recipes_round_trip_and_copies_do_not_share_corrections()
    {
        var template = new GeneratedPromoTemplate { Name = "Clean", VideoCleanup = [Recipe()] };
        string yaml = SchedulerConfigLoader.SerializeSection(template);
        var parsed = SchedulerConfigLoader.ConvertSection<GeneratedPromoTemplate>(template)!;
        Assert.Contains("video_cleanup:", yaml);
        Assert.Equal(.3, Assert.Single(Assert.Single(parsed.VideoCleanup!).Regions).Keys[0].Width);
        var copy = GeneratedPromoEditing.Copy(template);
        copy.VideoCleanup![0].Regions[0].Keys[0].X = .6;
        Assert.Equal(.1, template.VideoCleanup![0].Regions[0].Keys[0].X);
        Assert.DoesNotContain("VideoCleanup", JsonSerializer.Serialize(new GeneratedPromoTemplate()));
    }

    [Fact]
    public async Task Package_remapping_keeps_cleanup_with_its_source()
    {
        string original = Path.Combine(_folder, "original.mov"), moved = Path.Combine(_folder, "moved.mov");
        var template = new GeneratedPromoTemplate { Background = original, VideoCleanup = [Recipe(original)] };
        await GeneratedPromoPackages.MapPathsAsync(template, p => Task.FromResult(p == original ? moved : p));
        Assert.Equal(moved, template.Background);
        Assert.Equal(moved, template.VideoCleanup![0].Source);
    }

    [Fact]
    public void Source_and_correction_changes_invalidate_the_cache_but_enabling_does_not()
    {
        var recipe = Recipe(); File.WriteAllText(recipe.Source, "first");
        string first = PromoVideoCleanupService.Identity(recipe);
        recipe.Enabled = false;
        Assert.Equal(first, PromoVideoCleanupService.Identity(recipe));
        recipe.Regions[0].Name = "Renamed"; recipe.Regions[0].Id = "new-id";
        Assert.Equal(first, PromoVideoCleanupService.Identity(recipe));
        recipe.Regions[0].Keys[0].X += .01;
        Assert.NotEqual(first, PromoVideoCleanupService.Identity(recipe));
        recipe.Regions[0].Keys[0].X -= .01;
        File.WriteAllText(recipe.Source, "different content, different size");
        Assert.NotEqual(first, PromoVideoCleanupService.Identity(recipe));
    }

    [Fact]
    public void Invalid_geometry_and_reference_times_are_rejected()
    {
        var recipe = Recipe();
        recipe.Regions[0].Keys[0].Width = double.NaN;
        Assert.Throws<ArgumentException>(() => PromoVideoCleanupService.Validate(recipe));
        recipe = Recipe(); recipe.Regions[0].Start = 3;
        Assert.Throws<ArgumentException>(() => PromoVideoCleanupService.Validate(recipe));
        recipe = Recipe(); recipe.Regions[0].Keys[0].X = .95;
        Assert.Throws<ArgumentException>(() => PromoVideoCleanupService.Validate(recipe));
    }

    [Fact]
    public async Task Both_visual_paths_use_the_same_cache_without_changing_audio_or_timing()
    {
        using var service = Service();
        var recipe = Recipe();
        File.WriteAllText(recipe.Source, "source");
        string folder = Path.Combine(service.CacheDirectory, PromoVideoCleanupService.Identity(recipe));
        Directory.CreateDirectory(folder);
        string cached = Path.Combine(folder, "clean.mkv");
        File.WriteAllText(cached, "cached");
        File.WriteAllText(Path.Combine(folder, "mask.mkv"), "mask");
        var group = new PromoGroupLayer { Bounds = SKRect.Create(0, 0, 100, 100) };
        var video = new PromoVideoLayer { Path = recipe.Source, Bounds = SKRect.Create(5, 6, 40, 30), SourceStart = TimeSpan.FromSeconds(1),
            StartsWithLayer = true, Loop = true, Parent = group, ChromaKey = true, Rotation = 35 };
        var audio = new PromoMusic(recipe.Source, .5, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero, false);
        var original = new PromoComposition { Duration = TimeSpan.FromSeconds(4), BackgroundPath = recipe.Source,
            BackgroundStart = TimeSpan.FromSeconds(.25), BackgroundAudio = audio, LayerAudio = [audio], Layers = [group, video] };
        var template = new GeneratedPromoTemplate { VideoCleanup = [recipe] };
        foreach (bool wait in new[] { false, true })
        {
            var prepared = await service.PrepareAsync(original, template, wait);
            Assert.Equal(cached, prepared.BackgroundPath);
            var drawn = Assert.Single(prepared.Layers.OfType<PromoVideoLayer>());
            Assert.Equal(video with { Path = cached }, drawn);
            Assert.Same(group, drawn.Parent);
            Assert.Same(audio, prepared.BackgroundAudio);
            Assert.Same(audio, Assert.Single(prepared.LayerAudio));
            Assert.Equal(original.BackgroundStart, prepared.BackgroundStart);
        }
        Assert.Equal(recipe.Source, original.BackgroundPath);
        Assert.Equal(recipe.Source, video.Path);
    }

    [Fact]
    public async Task Unprocessed_stills_do_not_launch_engine_and_disabled_effects_do_not_block_rendering()
    {
        using var service = Service();
        var recipe = Recipe();
        var template = new GeneratedPromoTemplate { VideoCleanup = [recipe] };
        var composition = new PromoComposition { Duration = TimeSpan.FromSeconds(4), BackgroundPath = recipe.Source };
        Assert.Equal(recipe.Source, (await service.PrepareAsync(composition, template, false)).BackgroundPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(composition, template, true));
        recipe.Enabled = false;
        Assert.Equal(recipe.Source, (await service.PrepareAsync(composition, template, true)).BackgroundPath);
    }

    [Fact]
    public void Drafts_survive_dialogs_but_do_not_mutate_the_applied_template()
    {
        using var service = Service();
        var template = new GeneratedPromoTemplate(); var recipe = Recipe();
        service.KeepDraft(template, recipe);
        recipe.Regions[0].Keys[0].X = .5;
        Assert.Equal(.1, service.Draft(template, recipe.Source)!.Regions[0].Keys[0].X);
        Assert.Null(template.VideoCleanup);
        service.ForgetDraft(template, recipe.Source);
        Assert.Null(service.Draft(template, recipe.Source));
    }

    [Theory]
    [InlineData(false, 0, 0, 0)]
    [InlineData(false, 1, .5, 1.5)]
    [InlineData(false, 1, 5, 1.9)]
    [InlineData(true, 1, 5, 0)]
    [InlineData(true, .5, 2, .5)]
    public void Preview_source_time_matches_loop_and_hold(bool loop, double start, double playback, double expected)
    {
        var video = new PromoVideoLayer { Path = "source.mov", Bounds = SKRect.Create(0, 0, 100, 100), Loop = loop, SourceStart = TimeSpan.FromSeconds(start) };
        Assert.Equal(expected, video.SourceSecond(TimeSpan.FromSeconds(playback), 2, 10), 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_cleanup_pixels_reach_the_final_render_and_source_preview(bool background)
    {
        using var service = Service();
        var recipe = Recipe(Path.Combine(_folder, "original.mkv"));
        await Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=64x64:r=10:d=1", "-c:v", "ffv1", recipe.Source);
        string folder = Path.Combine(service.CacheDirectory, PromoVideoCleanupService.Identity(recipe));
        Directory.CreateDirectory(folder);
        string clean = Path.Combine(folder, "clean.mkv");
        await Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=lime:s=64x64:r=10:d=1", "-c:v", "ffv1", clean);
        File.Copy(clean, Path.Combine(folder, "mask.mkv"));
        var composition = new PromoComposition
        {
            Width = 64, Height = 64, Fps = 10, Duration = TimeSpan.FromSeconds(1),
            BackgroundPath = background ? recipe.Source : null,
            Layers = background ? [] : [new PromoVideoLayer { Path = recipe.Source, Bounds = SKRect.Create(0, 0, 64, 64), In = PromoTransition.None, Out = PromoTransition.None }]
        };
        var prepared = await service.PrepareAsync(composition, new GeneratedPromoTemplate { VideoCleanup = [recipe] }, true);
        string rendered = Path.Combine(_folder, "render.mp4");
        await new PromoRenderer().RenderAsync(prepared, rendered);
        using var preview = SKBitmap.Decode(await service.FrameAsync(clean, .3));
        using var exported = SKBitmap.Decode(await service.FrameAsync(rendered, .3));
        foreach (SKBitmap image in new[] { preview, exported })
        {
            SKColor pixel = image.GetPixel(image.Width / 2, image.Height / 2);
            Assert.True(pixel.Green > 220 && pixel.Red < 30, $"Expected cleaned green pixels, found {pixel}");
        }
    }

    private static async Task Tool(string name, params string[] arguments)
    {
        var info = new ProcessStartInfo(name) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
    }

    [Fact]
    public async Task Removal_mask_preview_is_pink_only_where_pixels_were_selected()
    {
        using var service = Service();
        var recipe = Recipe(); File.WriteAllText(recipe.Source, "source");
        string folder = Path.Combine(service.CacheDirectory, PromoVideoCleanupService.Identity(recipe));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "clean.mkv"), "cache marker");
        await Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=10:d=1",
            "-vf", "drawbox=x=16:y=16:w=32:h=32:color=white:t=fill,format=gray", "-c:v", "ffv1", Path.Combine(folder, "mask.mkv"));
        using var image = SKBitmap.Decode(await service.MaskFrameAsync(recipe, .2));
        Assert.Equal(0, image.GetPixel(10, 10).Alpha);
        SKColor pink = image.GetPixel(image.Width / 2, image.Height / 2);
        Assert.InRange(pink.Alpha, (byte)145, (byte)160);
        Assert.True(pink.Red > 240 && pink.Green < 60 && pink.Blue > 100);
    }

    [Fact]
    public void Tracking_report_distinguishes_gaps_from_real_poses_in_both_directions()
    {
        var report = CleanupTrackingReport.Parse("""
            {"fps":10,"frame_count":8,"regions":[{"start_frame":0,"end_frame":7,
             "frames":[{"frame":2,"reference":4,"matrix":[[1,0,0],[0,1,0],[0,0,1]]},
                       {"frame":3,"reference":4,"matrix":[[1,0,0],[0,1,0],[0,0,1]]},
                       {"frame":4,"reference":4,"matrix":[[1,0,0],[0,1,0],[0,0,1]]},
                       {"frame":5,"reference":4,"matrix":[[1,0,0],[0,1,0],[0,0,1]]}],
             "untracked_frames":[0,1,6,7],"references":[{"frame":4,"backward_frames":2,"forward_frames":1,"mask_pixels":32}]}]}
            """);
        var region = Assert.Single(report.Regions);
        Assert.Null(region.At(1));
        Assert.True(region.At(2)!.Frame < region.At(2)!.Reference);
        Assert.True(region.At(5)!.Frame > region.At(5)!.Reference);
        Assert.Equal(new[] { new CleanupCoverage(0, 1, false), new CleanupCoverage(2, 5, true), new CleanupCoverage(6, 7, false) }, region.Coverage);
        Assert.Equal(2, region.References[0].BackwardFrames);
    }

    [Fact]
    public void Detection_controls_invalidate_processed_results()
    {
        var recipe = Recipe();
        string original = PromoVideoCleanupService.Identity(recipe);
        recipe.Regions[0].Sensitivity = 90;
        Assert.NotEqual(original, PromoVideoCleanupService.Identity(recipe));
        recipe.Regions[0].Sensitivity = 50;
        recipe.Regions[0].Ink = GeneratedRemovalInk.Dark;
        Assert.NotEqual(original, PromoVideoCleanupService.Identity(recipe));
    }
}
