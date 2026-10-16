using System.Net;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Images.SerpApi;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Images;
using SchedularrTV.Service.Images;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Variable images' automatic pictures: their queries, the picture search, and which picture each one gets.</summary>
public sealed class AutomaticImageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "automatic-image-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_folder);

    [Fact]
    public void A_query_fills_in_the_show_and_one_naming_a_missing_block_finds_nothing()
    {
        Assert.Equal("Rugrats main character png", AutomaticImageQuery.ForShow("{SHOW}  main character png", "Rugrats", null));
        Assert.Equal("Rugrats Toonami", AutomaticImageQuery.ForShow("{title} {block}", "Rugrats", "Toonami"));
        Assert.Null(AutomaticImageQuery.ForShow("{block} logo", "Rugrats", null));
        Assert.Equal("{unknown} Rugrats", AutomaticImageQuery.ForShow("{unknown} {show}", "Rugrats", null));
    }

    private sealed class FakeLookup(string? found) : IAutomaticImageLookup
    {
        public List<string> Queries { get; } = [];

        public string? Find(AutomaticImageConfig config, string query)
        {
            Queries.Add(query);
            return found;
        }
    }

    [Fact]
    public void A_shared_image_finds_a_picture_for_shows_without_one_before_using_its_default()
    {
        var shared = new GeneratedSharedImage
        {
            DefaultPath = "default.png",
            Shows = [new GeneratedShowImage { Show = "Rugrats", Path = "rugrats.png" }, new GeneratedShowImage { Show = "Toonami", Block = true, Path = "toonami.png" }],
            Automatic = new AutomaticImageConfig { Query = "{show} main character png", Transparent = true }
        };
        var variable = new GeneratedVariableImage { Shared = shared.Id, Linked = shared };
        var lookup = new FakeLookup("found.png");

        Assert.Equal("rugrats.png", variable.PathFor("Rugrats", null, lookup));
        Assert.Equal("toonami.png", variable.PathFor("Dragon Ball Z", "Toonami", lookup));
        Assert.Equal("found.png", variable.PathFor("Doug", null, lookup));
        Assert.Equal(["Doug main character png"], lookup.Queries);

        // Nothing found (yet), or no picture search: the default.
        Assert.Equal("default.png", variable.PathFor("Doug", null, new FakeLookup(null)));
        Assert.Equal("default.png", variable.PathFor("Doug", null));
    }

    [Fact]
    public void An_overlay_variable_uses_the_schedules_shared_image()
    {
        var shared = new GeneratedSharedImage
        {
            DefaultPath = "default.png",
            Shows = [new GeneratedShowImage { Show = "Rugrats", Path = "rugrats.png" }],
            Automatic = new AutomaticImageConfig { Query = "{show} logo" }
        };
        var overlay = new Overlay
        {
            Kind = OverlayKinds.File,
            VariableImage = "variable_image_01",
            VariableImages = [new GeneratedVariableImage { Name = "variable_image_01", Context = GeneratedImageContext.CurrentlyWatching, Shared = shared.Id }]
        };

        Assert.Equal("rugrats.png", OverlayImages.Resolve(overlay, "Rugrats", [], [shared]).File);
        Assert.Equal("found.png", OverlayImages.Resolve(overlay, "Doug", [], [shared], new FakeLookup("found.png")).File);
        Assert.Equal("default.png", OverlayImages.Resolve(overlay, "Doug", [], [shared]).File);
        Assert.Null(OverlayImages.Resolve(overlay, "Doug", [], []).File);
    }

    [Fact]
    public void SerpApi_asks_Google_Images_for_transparent_pictures_and_reads_the_full_size_ones()
    {
        string url = SerpApiImageSearch.Url(new ImageSearchRequest("Rugrats png") { TransparentOnly = true }, "key", safe: true, country: "US");
        Assert.Contains("engine=google_images", url);
        Assert.Contains("q=Rugrats%20png", url);
        Assert.Contains("image_color=trans", url);
        Assert.Contains("safe=active", url);
        Assert.Contains("gl=us", url);

        IReadOnlyList<ImageSearchResult> results = SerpApiImageSearch.Parse("""
            { "images_results": [
                { "position": 1, "title": "Tommy", "link": "https://example.com/page", "original": "https://example.com/tommy.png", "original_width": 800, "original_height": 600 },
                { "position": 2, "title": "No original" }
            ] }
            """, out string? error);

        Assert.Null(error);
        ImageSearchResult result = Assert.Single(results);
        Assert.Equal("https://example.com/tommy.png", result.Url);
        Assert.Equal((800, 600), (result.Width, result.Height));

        Assert.Empty(SerpApiImageSearch.Parse("""{ "error": "Invalid API key." }""", out error));
        Assert.Equal("Invalid API key.", error);
    }

    [Fact]
    public async Task The_chosen_result_counts_only_usable_transparent_pictures_and_is_kept()
    {
        var provider = new FakeSearch(["https://pictures.test/opaque.png", "https://pictures.test/broken.png", "https://pictures.test/first.png", "https://pictures.test/second.png"]);
        var http = new FakeHttp(new Dictionary<string, byte[]>
        {
            ["https://pictures.test/opaque.png"] = Png(transparent: false),
            ["https://pictures.test/broken.png"] = [1, 2, 3],
            ["https://pictures.test/first.png"] = Png(transparent: true),
            ["https://pictures.test/second.png"] = Png(transparent: true, colour: SKColors.Blue)
        });
        var images = new AutomaticImages([provider], http, _folder);
        var request = AutomaticImageRequest.For(new AutomaticImageConfig { Result = 2, Transparent = true }, "Rugrats png")!;

        string? picture = await images.GetAsync(request);

        Assert.True(picture is not null, images.Problem(request));
        using (SKBitmap bitmap = SKBitmap.Decode(picture))
        {
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(32, 32));
        }

        // Found once: asked again, it's the same picture without searching again.
        Assert.Equal(picture, images.Peek(request, queue: false));
        Assert.Equal(picture, await images.GetAsync(request));
        Assert.Equal(1, provider.Searches);

        // There's no fourth usable one.
        var tooFar = request with { Result = 4 };
        Assert.Null(await images.GetAsync(tooFar));
        Assert.Contains("no result 4", images.Problem(tooFar));
    }

    [Fact]
    public void Without_a_picture_search_installed_it_says_so()
    {
        var images = new AutomaticImages([], null, _folder);
        Assert.Contains("No picture search", images.SetupProblem(null));
        Assert.Null(images.Peek(new AutomaticImageRequest("anything", 1, false, null)));
    }

    [Fact]
    public void An_animated_picture_shows_each_frame_for_its_time_and_loops()
    {
        SKImage Frame(SKColor colour)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(4, 4));
            bitmap.Erase(colour);
            return SKImage.FromBitmap(bitmap);
        }

        var gif = new AnimatedImage([Frame(SKColors.Red), Frame(SKColors.Green), Frame(SKColors.Blue)], [0.1, 0.2, 0.1]);

        Assert.Equal(0.4, gif.Duration, 6);
        SKColor At(double seconds)
        {
            using var bitmap = SKBitmap.FromImage(gif.FrameAt(seconds));
            return bitmap.GetPixel(1, 1);
        }

        Assert.Equal(SKColors.Red, At(-1));
        Assert.Equal(SKColors.Red, At(0.05));
        Assert.Equal(SKColors.Green, At(0.15));
        Assert.Equal(SKColors.Green, At(0.29));
        Assert.Equal(SKColors.Blue, At(0.35));
        Assert.Equal(SKColors.Green, At(0.55)); // the second time round

        Assert.Null(AutomaticImages.AnimatedFormat(Png(transparent: true)));
        Assert.False(AnimatedImages.IsAnimated("missing.gif"));
        Assert.Contains("image_type=animated",
            SerpApiImageSearch.Url(new ImageSearchRequest("dancing banana") { AnimatedOnly = true }, "key", safe: true, country: null));
    }

    [Fact]
    public void Ffprobe_frame_rates_are_read_as_frames_a_second()
    {
        Assert.Equal(29.97, OverlayVideos.Rate("30000/1001"));
        Assert.Equal(25.0, OverlayVideos.Rate("25/1"));
        Assert.Null(OverlayVideos.Rate("0/0"));
        Assert.Equal(23.976, OverlayVideos.Parse("width=1920\nheight=1080\nr_frame_rate=24000/1001\nduration=5\n")!.FrameRate);
        Assert.Equal(25.0, OverlayAnimation.FpsOf(new Overlay { Fps = 25 }));
        Assert.Equal(30.0, OverlayAnimation.FpsOf(new Overlay()));
    }

    private static byte[] Png(bool transparent, SKColor? colour = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(transparent ? SKColors.Transparent : SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = colour ?? SKColors.Red })
        {
            canvas.DrawRect(16, 16, 32, 32, paint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class FakeSearch(IReadOnlyList<string> urls) : IImageSearchProvider
    {
        public int Searches { get; private set; }

        public string Id => "fake";

        public string DisplayName => "Fake";

        public Task<IReadOnlyList<ImageSearchResult>> SearchAsync(ImageSearchRequest request, CancellationToken cancellationToken = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<ImageSearchResult>>([.. urls.Select(url => new ImageSearchResult(url))]);
        }
    }

    private sealed class FakeHttp(IReadOnlyDictionary<string, byte[]> files) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(files));

        private sealed class Handler(IReadOnlyDictionary<string, byte[]> files) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out byte[]? body)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
