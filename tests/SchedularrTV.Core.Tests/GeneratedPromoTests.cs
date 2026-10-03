using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tts_text_splits_on_punctuation_and_then_words_within_300_utf8_bytes()
    {
        string sentence = string.Join(' ', Enumerable.Repeat("cartoon", 60)) + ".";
        IReadOnlyList<string> chunks = TikTokTtsClient.SplitText($"Coming up next... {sentence} Then more, soon!");

        Assert.Equal($"Coming up next... {sentence} Then more, soon!", string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.InRange(Encoding.UTF8.GetByteCount(chunk), 1, 300));
        Assert.True(chunks.Count > 1);
        Assert.Equal(["Short. Text!"], TikTokTtsClient.SplitText("Short. Text!"));
    }

    [Fact]
    public async Task Tts_falls_back_to_the_next_endpoint_and_joins_chunks_in_order()
    {
        var handler = new TtsHandler();
        var client = new TikTokTtsClient(new HttpClient(handler),
        [
            new(new Uri("https://first.test/tts"), "data"),
            new(new Uri("https://second.test/tts"), "base64")
        ]);
        string text = string.Join(' ', Enumerable.Repeat("alpha", 70)) + ". Omega!";

        byte[] audio = await client.SynthesizeAsync(text, "en_us_006");

        Assert.Equal(string.Concat(TikTokTtsClient.SplitText(text)), Encoding.UTF8.GetString(audio));
        Assert.Contains(handler.Requests, request => request.Host == "first.test");
        Assert.All(handler.Requests.Where(request => request.Host == "second.test"), request => Assert.Equal("en_us_006", request.Voice));
    }

    [Fact]
    public void Up_next_uses_distinct_programme_starts_and_builds_the_voice_over()
    {
        var schedule = new ChannelSchedule
        {
            ChannelId = "zone", ChannelName = "The Zone", Start = Noon, End = Noon.AddHours(2),
            Entries =
            [
                Entry(Noon.AddMinutes(-30), "Earlier"),
                Entry(Noon, "Theme", intro: true),
                Entry(Noon.AddSeconds(20), "SpongeBob SquarePants"),
                Entry(Noon.AddMinutes(11), "SpongeBob SquarePants", continues: true),
                Entry(Noon.AddMinutes(22), null, kind: ScheduleEntryKind.Commercial),
                Entry(Noon.AddMinutes(30), "Jimmy Neutron: Boy Genius"),
                Entry(Noon.AddMinutes(40), "Jimmy Neutron: Boy Genius", segment: 2),
                Entry(Noon.AddMinutes(60), "Rocket Power")
            ]
        };
        TimeZoneInfo eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        IReadOnlyList<UpNextListing> listings = UpNextPromo.Listings(schedule, Noon, 2);

        Assert.Equal(["SpongeBob SquarePants", "Jimmy Neutron: Boy Genius"], listings.Select(listing => listing.Title));
        Assert.Equal(
            "Coming up next... SpongeBob SquarePants. Then Jimmy Neutron: Boy Genius at 12:30! Only on The Zone!",
            UpNextPromo.Script(listings, eastern, postMessage: "Only on The Zone!"));
        Assert.Equal("Coming up next... Wow! Wow! Wubbzy!",
            UpNextPromo.Script([new UpNextListing("Wow! Wow! Wubbzy!", Noon)], eastern));
    }

    [Theory]
    [InlineData(PromoTransition.Fade, 0.0, 0f, 0f, 1f)]
    [InlineData(PromoTransition.SlideRight, 0.2, 0.875f, -15f, 1f)]
    [InlineData(PromoTransition.Zoom, 0.2, 0.875f, 0f, 0.975f)]
    [InlineData(PromoTransition.None, 0.0, 1f, 0f, 1f)]
    public void Transitions_ease_in_and_out(PromoTransition transition, double seconds, float opacity, float offsetX, float scale)
    {
        var layer = new PromoRectLayer
        {
            Bounds = SKRect.Create(0, 0, 10, 10), In = transition, Out = transition,
            Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3), TransitionDuration = TimeSpan.FromSeconds(0.4)
        };

        PromoLayerFrame frame = PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(1 + seconds), TimeSpan.FromSeconds(5), 1000)!.Value;

        Assert.Equal(opacity, frame.Opacity, 3);
        Assert.Equal(offsetX, frame.OffsetX, 3);
        Assert.Equal(scale, frame.Scale, 3);
        Assert.Null(PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), 1000));
        double mirrored = Math.Max(seconds, 0.001);
        Assert.Equal(PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(1 + mirrored), TimeSpan.FromSeconds(5), 1000)!.Value.Opacity,
            PromoAnimation.Evaluate(layer, TimeSpan.FromSeconds(3 - mirrored), TimeSpan.FromSeconds(5), 1000)!.Value.Opacity, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renderer_produces_an_h264_aac_video_with_the_composition_length(bool videoBackground)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"generated-promo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string? background = null;
            if (videoBackground)
            {
                background = Path.Combine(directory, "background.mp4");
                await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x36:rate=10",
                    "-t", "0.3", "-pix_fmt", "yuv420p", background);
            }

            var composition = new PromoComposition
            {
                Width = 160, Height = 90, Fps = 10, Duration = TimeSpan.FromSeconds(1), BackgroundPath = background,
                Layers =
                [
                    new PromoRectLayer { Bounds = SKRect.Create(10, 10, 80, 40), Color = SKColors.Red },
                    new PromoTextLayer { Bounds = SKRect.Create(10, 55, 140, 25), Lines = ["Up next"], MaxFontSize = 20 }
                ]
            };
            string output = Path.Combine(directory, "promo.mp4");

            PromoRenderResult result = await new PromoRenderer().RenderAsync(composition, output);

            Assert.Equal(10, result.Frames);
            using JsonDocument probe = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-show_entries",
                "format=duration:stream=codec_type,codec_name,width,height", "-of", "json", output));
            JsonElement[] streams = [.. probe.RootElement.GetProperty("streams").EnumerateArray()];
            Assert.Contains(streams, stream => stream.GetProperty("codec_name").GetString() == "h264"
                && stream.GetProperty("width").GetInt32() == 160 && stream.GetProperty("height").GetInt32() == 90);
            Assert.Contains(streams, stream => stream.GetProperty("codec_name").GetString() == "aac");
            Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture), 0.95, 1.1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Frames_draw_layers_in_order_with_background_and_opacity()
    {
        var composition = new PromoComposition
        {
            Width = 20, Height = 20, Duration = TimeSpan.FromSeconds(1), BackgroundColor = SKColors.Blue,
            Layers =
            [
                new PromoRectLayer { Bounds = SKRect.Create(0, 0, 20, 20), Color = SKColors.Red, In = PromoTransition.None, Out = PromoTransition.None },
                new PromoRectLayer { Bounds = SKRect.Create(0, 0, 10, 20), Color = SKColors.Lime, Opacity = 0.5f, In = PromoTransition.None, Out = PromoTransition.None }
            ]
        };
        using var bitmap = new SKBitmap(20, 20);
        using var canvas = new SKCanvas(bitmap);
        using var images = new PromoImageCache();

        PromoRenderer.DrawFrame(canvas, composition, TimeSpan.FromSeconds(0.5), images);

        Assert.Equal(SKColors.Red, bitmap.GetPixel(15, 10));
        SKColor blended = bitmap.GetPixel(5, 10);
        Assert.InRange(blended.Red, 120, 135);
        Assert.InRange(blended.Green, 120, 135);
        Assert.Equal(0, blended.Blue);
    }

    private static ScheduleEntry Entry(DateTimeOffset start, string? series, bool intro = false, bool continues = false,
        int segment = 0, ScheduleEntryKind kind = ScheduleEntryKind.Program) => new()
    {
        Start = start, Duration = TimeSpan.FromMinutes(10), Kind = kind, IsSlotIntro = intro, ContinuesSlot = continues,
        SegmentNumber = segment,
        Item = series is null ? null : new MediaItem { Id = $"{series}-{start:HHmmss}", Title = $"{series} episode", SeriesName = series, Kind = MediaKind.Episode }
    };

    private static async Task<string> Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return output;
    }

    private sealed class TtsHandler : HttpMessageHandler
    {
        public List<(string Host, string Voice)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content!.Headers.ContentLength);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            using JsonDocument body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            string text = body.RootElement.GetProperty("text").GetString()!;
            lock (Requests) Requests.Add((request.RequestUri!.Host, body.RootElement.GetProperty("voice").GetString()!));
            if (request.RequestUri.Host == "first.test")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            // Responses arrive out of order to prove the client keeps chunk order.
            await Task.Delay(text.Length % 3 * 10, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }))
            };
        }
    }
}
