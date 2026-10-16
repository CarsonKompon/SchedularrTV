using System.Net;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Images.SerpApi;
using SchedularrTV.Overlays;
using SchedularrTV.Plugins.Images;
using SchedularrTV.Service.Images;
using SkiaSharp;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>Automatic image layers: their queries, the picture search, and which picture each one gets.</summary>
public sealed class AutomaticImageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "automatic-image-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_folder);

    [Fact]
    public void A_query_fills_its_tokens_and_one_naming_a_missing_show_finds_nothing()
    {
        Dictionary<string, string> tokens = new(StringComparer.OrdinalIgnoreCase) { ["up_next"] = "Rugrats", ["up_next_2"] = "" };

        Assert.Equal("Rugrats main character png", GeneratedPromoResolver.FillQuery("{UP_NEXT}  main character png", tokens));
        Assert.Null(GeneratedPromoResolver.FillQuery("{up_next_2} logo", tokens));
        Assert.Equal("{unknown} Rugrats", GeneratedPromoResolver.FillQuery("{unknown} {up_next}", tokens));

        Assert.Equal("Doug logo", OverlayImages.FillQuery("{title} logo", new Dictionary<string, string> { ["title"] = "Doug", ["next"] = "" }));
        Assert.Null(OverlayImages.FillQuery("{next} logo", new Dictionary<string, string> { ["title"] = "Doug", ["next"] = "" }));
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

        Assert.NotNull(picture);
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
