using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoVideoTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-video-{Guid.NewGuid():N}");

    public GeneratedPromoVideoTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private string File(string name)
    {
        string path = Path.Combine(_directory, name);
        System.IO.File.WriteAllText(path, "not really a video");
        return path;
    }

    private static GeneratedPromoTemplate Template(GeneratedLayer video) => new()
    {
        Name = "Videos",
        Duration = TimeSpan.FromSeconds(8),
        Layers = [new() { Name = "Next", Kind = GeneratedLayerKind.UpNext }, video]
    };

    [Fact]
    public void A_video_file_plays_in_its_box_and_its_sound_waits_for_it()
    {
        string sting = File("sting.mp4");
        var layer = new GeneratedLayer
        {
            Name = "Sting", Kind = GeneratedLayerKind.Video, Path = sting, X = 100, Y = 50, Width = 320, Height = 180,
            Start = TimeSpan.FromSeconds(2), TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None,
            Video = new() { FileStart = TimeSpan.FromSeconds(3), IncludeAudio = true, Volume = 0.5, Fill = true }
        };
        GeneratedPromoTemplate template = Template(layer);

        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", [new UpNextListing("Show", Noon.AddMinutes(30))], TimeZoneInfo.Utc, Noon)!;
        PromoComposition composition = GeneratedPromoResolver.Compose(promo);

        PromoVideoLayer video = Assert.Single(composition.Layers.OfType<PromoVideoLayer>());
        Assert.Equal(sting, video.Path);
        Assert.Equal(TimeSpan.FromSeconds(3), video.SourceStart);
        Assert.True(video.StartsWithLayer);
        Assert.True(video.Fill);
        Assert.Equal((320, 180), video.FrameSize);

        // It starts playing as it appears; with the promo, it would already be two seconds in.
        Assert.Equal(TimeSpan.Zero, video.PlaybackAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(1.5), video.PlaybackAt(TimeSpan.FromSeconds(3.5)));
        Assert.Equal(TimeSpan.FromSeconds(3.5), (video with { StartsWithLayer = false }).PlaybackAt(TimeSpan.FromSeconds(3.5)));

        PromoMusic sound = Assert.Single(composition.LayerAudio);
        Assert.Equal(sting, sound.Path);
        Assert.Equal(0.5, sound.Volume);
        Assert.Equal(TimeSpan.FromSeconds(3), sound.StartAt);
        Assert.Equal(TimeSpan.FromSeconds(2), sound.Delay);
        // Silent before the layer shows, full while it does.
        Assert.Equal(0, sound.Levels![0].Level);
        Assert.Contains(sound.Levels, level => level.At >= TimeSpan.FromSeconds(2) && level.Level == 1);

        layer.Video!.IncludeAudio = false;
        Assert.Empty(GeneratedPromoResolver.Compose(GeneratedPromoResolver.Resolve(template, "Channel",
            [new UpNextListing("Show", Noon.AddMinutes(30))], TimeZoneInfo.Utc, Noon)!).LayerAudio);
    }

    [Fact]
    public void An_episode_clip_comes_from_the_show_it_names()
    {
        string next = File("next.mkv");
        string later = File("later.mkv");
        var layer = new GeneratedLayer { Name = "Later clip", Kind = GeneratedLayerKind.Video, Video = new() { Episode = "next2", ClipStart = TimeSpan.FromMinutes(5) } };
        GeneratedPromoTemplate template = Template(layer);
        UpNextListing[] listings =
        [
            new UpNextListing("Rugrats", Noon.AddMinutes(30)) { Path = next, Runtime = TimeSpan.FromMinutes(22) },
            new UpNextListing("Doug", Noon.AddMinutes(60)) { Path = later, Runtime = TimeSpan.FromMinutes(22) }
        ];

        // A clip two shows ahead makes the Up Next promo about two shows.
        Assert.Equal(2, GeneratedPromoResolver.ListingsNeeded(template));
        ResolvedGeneratedPromo promo = GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!;
        Assert.Equal(new PromoVideoSource(later, TimeSpan.FromMinutes(5)), promo.LayerVideos[1]);

        // A set start too close to the end is pulled back so the clip has room.
        layer.Video!.ClipStart = TimeSpan.FromMinutes(30);
        Assert.Equal(TimeSpan.FromMinutes(22) - template.Duration,
            GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!.LayerVideos[1].Start);

        // Without a set start it's somewhere in the middle, the same each time for the same airing.
        layer.Video.ClipStart = null;
        TimeSpan picked = GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!.LayerVideos[1].Start;
        Assert.InRange(picked, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(22) - template.Duration);
        Assert.Equal(picked, GeneratedPromoResolver.Resolve(template, "Channel", listings, TimeZoneInfo.Utc, Noon)!.LayerVideos[1].Start);

        // With no such show (or no file), nothing is drawn.
        ResolvedGeneratedPromo oneShow = GeneratedPromoResolver.Resolve(template, "Channel", listings[..1], TimeZoneInfo.Utc, Noon)!;
        Assert.Empty(oneShow.LayerVideos);
        Assert.Empty(GeneratedPromoResolver.Compose(oneShow).Layers.OfType<PromoVideoLayer>());
    }

    [Fact]
    public void Video_frames_are_drawn_into_the_box()
    {
        var video = new PromoVideoLayer
        {
            Bounds = SKRect.Create(50, 50, 100, 100), Path = "clip.mp4", In = PromoTransition.None, Out = PromoTransition.None
        };
        using var bitmap = new SKBitmap(200, 200);
        using var red = new SKBitmap(10, 10);
        red.Erase(SKColors.Red);
        using SKImage frame = SKImage.FromBitmap(red);
        using (var canvas = new SKCanvas(bitmap))
        using (var images = new PromoImageCache())
        {
            PromoRenderer.DrawFrame(canvas, new PromoComposition { Width = 200, Height = 200, Duration = TimeSpan.FromSeconds(5), Layers = [video] },
                TimeSpan.FromSeconds(1), images, drawBackground: false, videos: new FixedVideoFrames(new Dictionary<PromoVideoLayer, SKImage> { [video] = frame }));
        }

        Assert.Equal(SKColors.Red, bitmap.GetPixel(100, 100));
        Assert.Equal(0, bitmap.GetPixel(20, 20).Alpha);
    }

    [Fact]
    public void Video_layers_need_something_to_play()
    {
        var layer = new GeneratedLayer { Name = "Empty", Kind = GeneratedLayerKind.Video, Video = new() };
        GeneratedPromoTemplate template = Template(layer);
        var config = new SchedulerConfig
        {
            Channels = [new() { Id = "channel", Name = "Channel", Number = 1, BaseRotation = new() { Query = "*" },
                GeneratedPromos = new() { Templates = [template] } }]
        };
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("a video layer needs"));

        layer.Video!.Episode = "later";
        Assert.Contains(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("video.episode"));

        layer.Video.Episode = "next";
        Assert.DoesNotContain(SchedulerConfigLoader.Validate(config).Errors, error => error.Contains("Empty"));
        GeneratedLayer loaded = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(config)).Channels[0].GeneratedPromos!.Templates[0].Layers[1];
        Assert.Equal(GeneratedLayerKind.Video, loaded.Kind);
        Assert.Equal("next", loaded.Video!.Episode);
    }
}
