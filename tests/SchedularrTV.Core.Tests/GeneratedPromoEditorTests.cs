using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.GeneratedPromos;
using SchedularrTV.Service;
using SchedularrTV.Service.Components.Shared;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoEditorTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-editor-{Guid.NewGuid():N}");

    // A still preview extracts a background video's frames in the background, so its ffmpeg can still hold the video
    // for a moment; the shared helper waits for it rather than failing the test.
    public void Dispose() => TestFolders.Delete(_directory);

    [Theory]
    [InlineData("#FFC400", "#ffc400", 100)]
    [InlineData("#80000000", "#000000", 50)]
    [InlineData("#00FFFFFF", "#ffffff", 0)]
    [InlineData("nonsense", "#ffffff", 100)]
    public void Colours_split_into_picker_value_and_opacity_and_join_back(string hex, string rgb, int alpha)
    {
        Assert.Equal((rgb, alpha), GeneratedPromoEditing.SplitColor(hex));
        (string pickerRgb, int pickerAlpha) = GeneratedPromoEditing.SplitColor(hex);
        Assert.Equal((pickerRgb, pickerAlpha), GeneratedPromoEditing.SplitColor(GeneratedPromoEditing.JoinColor(pickerRgb, pickerAlpha)));
        Assert.Equal("#FFC400", GeneratedPromoEditing.JoinColor("#ffc400", 100));
    }

    [Fact]
    public void Layers_reorder_copy_and_get_unique_names()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer heading = template.Layers[2];

        GeneratedPromoEditing.Move(template.Layers, heading, 1);
        Assert.Equal(3, template.Layers.IndexOf(heading));
        GeneratedPromoEditing.Move(template.Layers, template.Layers[0], -1);
        Assert.Equal(3, template.Layers.IndexOf(heading));

        GeneratedLayer copy = GeneratedPromoEditing.Copy(heading);
        Assert.NotSame(heading, copy);
        Assert.Equal(heading.Text, copy.Text);
        Assert.Equal("Heading 2", GeneratedPromoEditing.UniqueName(template.Layers.Select(layer => layer.Name), "Heading"));

        GeneratedLayer upNext = GeneratedPromoEditing.NewLayer(template, GeneratedLayerKind.UpNext);
        Assert.Equal("Up Next", upNext.Name);
        Assert.NotNull(upNext.UpNext);
        Assert.InRange(upNext.X, 0, template.Width - upNext.Width);
    }

    [Fact]
    public void Resizing_the_output_keeps_layers_in_proportion()
    {
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        GeneratedLayer title = template.Layers.Single(layer => layer.Name == "Next show");

        GeneratedPromoEditing.Resize(template, 1280, 720);

        Assert.Equal((1280, 720), (template.Width, template.Height));
        Assert.Equal(120, title.X);
        Assert.Equal(280, title.Y);
        Assert.Equal(707, title.Width);
        Assert.Equal(88, title.FontSize);
    }

    [Fact]
    public async Task Still_preview_is_drawn_by_the_renderer_at_the_requested_time_and_size()
    {
        using ServiceProvider services = Services();
        GeneratedPromoPreview preview = services.GetRequiredService<GeneratedPromoPreview>();
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        template.BackgroundColor = "#0000FF";
        UpNextListing[] listings = [new("SpongeBob", Noon), new("Rugrats", Noon.AddMinutes(30))];

        using SKBitmap start = SKBitmap.Decode(await preview.RenderStillAsync(template, "The Zone", listings, TimeZoneInfo.Utc, TimeSpan.Zero, 480));
        using SKBitmap later = SKBitmap.Decode(await preview.RenderStillAsync(template, "The Zone", listings, TimeZoneInfo.Utc, TimeSpan.FromSeconds(3), 480));

        Assert.Equal((480, 270), (start.Width, start.Height));
        SKColor corner = start.GetPixel(5, 5);
        Assert.True(corner.Blue > 200 && corner.Red < 40, $"background {corner}");
        SKColor panelStart = start.GetPixel(100, 195);
        SKColor panelLater = later.GetPixel(100, 195);
        Assert.True(panelLater.Blue < panelStart.Blue - 40, $"panel should darken the background once it has slid in: {panelStart} -> {panelLater}");
    }

    [Fact]
    public async Task Still_preview_uses_a_frame_from_a_background_video()
    {
        Directory.CreateDirectory(_directory);
        string video = Path.Combine(_directory, "red.mp4");
        await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:size=160x90:rate=10", "-t", "1", "-pix_fmt", "yuv420p", video);
        using ServiceProvider services = Services();
        GeneratedPromoTemplate template = GeneratedPromoTemplates.UpNext();
        template.Background = video;
        template.Layers.Clear();
        template.Layers.Add(new GeneratedLayer { Kind = GeneratedLayerKind.UpNext, X = 0, Y = 0, Width = 10, Height = 10, Opacity = 0 });

        byte[] jpeg = await services.GetRequiredService<GeneratedPromoPreview>()
            .RenderStillAsync(template, "The Zone", [new("SpongeBob", Noon)], TimeZoneInfo.Utc, TimeSpan.FromSeconds(2.5), 320);

        using SKBitmap still = SKBitmap.Decode(jpeg);
        SKColor centre = still.GetPixel(160, 90);
        Assert.True(centre.Red > 200 && centre.Green < 60, $"video frame {centre}");
    }

    [Theory]
    [InlineData(GeneratedPromoType.UpNext)]
    [InlineData(GeneratedPromoType.NowPlaying)]
    public async Task Background_rule_controls_show_names_eligibility_weight_and_time_window(GeneratedPromoType type)
    {
        using ServiceProvider services = Services();
        var template = GeneratedPromoTemplates.UpNext();
        template.Type = type;
        template.Background = "special.png";
        template.BackgroundRules = [new()
        {
            Background = "special.png", Shows = ["Rugrats", "Doug"], OnlyWhenMatching = true,
            Within = TimeSpan.FromMinutes(90), MatchWeight = 8,
            TimeRange = new() { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) }
        }];
        var channel = new ChannelConfig { Id = "zone", Name = "The Zone", GeneratedPromos = new() { Templates = [template] } };
        services.GetRequiredService<ConfigEditor>().Working.Channels.Add(channel);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await Render(renderer, channel);
        Assert.Contains(type == GeneratedPromoType.NowPlaying ? "Choose based on the Now Playing show" : "Choose based on upcoming shows", html);
        Assert.Contains("Rugrats", html);
        Assert.Contains("Doug", html);
        Assert.Contains("only when matching", html);
        Assert.Contains("Relative chance when matching", html);
        Assert.Contains("<details class=\"ed-background-rules\">", html);
        Assert.Contains("Only choose during a daily time range", html);
        Assert.Contains("value=\"22:00\"", html);
        Assert.Contains("value=\"06:00\"", html);
        if (type == GeneratedPromoType.NowPlaying)
        {
            Assert.DoesNotContain("Look ahead by", html);
            Assert.Contains("including a show that has already started", html);
        }
        else
        {
            Assert.Contains("Within the next (minutes)", html);
            Assert.Contains("value=\"90\"", html);
        }
    }

    [Fact]
    public async Task Advanced_voice_editor_shows_authored_scripts_and_optional_affixes_instead_of_AI_controls()
    {
        using ServiceProvider services = Services();
        var template = GeneratedPromoTemplates.UpNext();
        template.Voice!.Advanced = new()
        {
            Templates = ["Now, it's {up_next}. Crazy right? Then after that it's {up_next_2}!"]
        };
        template.Voice.Writer = new();
        var channel = new ChannelConfig { Id = "zone", Name = "The Zone", GeneratedPromos = new() { Templates = [template] } };
        services.GetRequiredService<ConfigEditor>().Working.Channels.Add(channel);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await Render(renderer, channel);
        Assert.Contains("Script template 1", html);
        Assert.Contains("Template read-out:", html);
        Assert.Contains("Crazy right?", html);
        Assert.Contains("Before a repeated show title", html);
        Assert.Contains("Count upcoming programmes by", html);
        Assert.Contains("Timeslot / episode (follow the channel", html);
        Assert.DoesNotContain("Count individual episodes instead of matching the image selectors", html);
        Assert.DoesNotContain("Have AI word it naturally", html);
        Assert.DoesNotContain("Said first", html);
        Assert.DoesNotContain("Said last", html);
        Assert.DoesNotContain("No usable voice-over template", html);
        template.Voice.Advanced.IncludePreMessages = true;
        template.Voice.Advanced.IncludePostMessages = true;
        html = await Render(renderer, channel);
        Assert.Contains("Said first", html);
        Assert.Contains("Said last", html);
    }

    [Fact]
    public async Task Promo_availability_editor_uses_yearless_dates_and_allows_out_of_season_preview()
    {
        using ServiceProvider services = Services();
        var template = GeneratedPromoTemplates.UpNext();
        template.Availability = new()
        {
            DateRange = new() { Start = "12-01", End = "12-31" },
            TimeRange = new() { Start = TimeSpan.FromHours(6), End = TimeSpan.FromHours(12) }
        };
        var channel = new ChannelConfig { Id = "zone", Name = "The Zone", GeneratedPromos = new() { Templates = [template] } };
        services.GetRequiredService<ConfigEditor>().Working.Channels.Add(channel);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        string html = await Render(renderer, channel);
        Assert.Contains("Promo availability", html);
        Assert.Contains("Only use this promo during a recurring date range", html);
        Assert.Contains("Start month", html);
        Assert.Contains("End day", html);
        Assert.Contains("12-01 through 12-31 each year", html);
        Assert.DoesNotContain("type=\"date\"", html);
        Assert.Contains("Outside this template", html);
        Assert.NotNull(services.GetRequiredService<GeneratedPromoPreview>().Resolve(template, channel, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task Still_preview_honours_background_eligibility()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "red.png");
        using (var bitmap = new SKBitmap(16, 16))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(path, data.ToArray());
        }
        using ServiceProvider services = Services();
        var template = new GeneratedPromoTemplate
        {
            Background = path, BackgroundColor = "#0000FF",
            BackgroundRules = [new() { Background = path, Shows = ["Rugrats"], OnlyWhenMatching = true }]
        };
        var preview = services.GetRequiredService<GeneratedPromoPreview>();
        using var excluded = SKBitmap.Decode(await preview.RenderStillAsync(template, "Channel", [new("Doug", Noon)],
            TimeZoneInfo.Utc, TimeSpan.Zero, 160, airsAt: Noon));
        using var included = SKBitmap.Decode(await preview.RenderStillAsync(template, "Channel", [new("Rugrats", Noon)],
            TimeZoneInfo.Utc, TimeSpan.Zero, 160, airsAt: Noon));
        Assert.True(excluded.GetPixel(80, 45).Blue > 200);
        Assert.True(included.GetPixel(80, 45).Red > 200);
    }

    [Fact]
    public async Task Tab_offers_a_template_then_shows_canvas_layers_voice_music_and_up_next_validation()
    {
        using ServiceProvider services = Services();
        ConfigEditor editor = services.GetRequiredService<ConfigEditor>();
        var channel = new ChannelConfig { Id = "zone", Name = "The Zone", Number = 1, BaseRotation = new ContentSelector { Query = "*" } };
        editor.Working.Channels.Add(channel);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        string empty = await Render(renderer, channel);
        Assert.Contains("Create a template", empty);
        Assert.Contains("Settings for", empty);
        Assert.Contains("Same as another channel", empty);

        channel.GeneratedPromos = new GeneratedPromosConfig { Templates = [GeneratedPromoTemplates.UpNext("Classic")] };
        channel.GeneratedPromos.Templates[0].Music = new GeneratedMusicConfig { Path = Path.Combine(_directory, "missing.mp3") };
        editor.Validate();
        string html = await Render(renderer, channel);

        Assert.Contains("Promo canvas", html);
        Assert.Contains("Next show", html);
        Assert.Contains("Up Next text", html);
        Assert.Contains("Show start times", html);
        Assert.Contains("Voice service", html);
        Assert.Contains("TikTok", html);
        Assert.Contains("Background music", html);
        Assert.Contains("Lower the music while the voice speaks", html);
        Assert.Contains("find this file", html);
        Assert.Contains("Add songs", html);
        Assert.Contains("Browse", html);
        Assert.Contains("SpongeBob SquarePants.", html);
        Assert.Contains("one of the variations", html);
        Assert.Contains("Add &quot;say nothing&quot;", html);
        Assert.Contains("(say nothing)", html);
        Assert.DoesNotContain("block to show the promoted show", html);

        // The show's own text layer is optional (a template can show its shows with variable images alone), so a
        // template without one is still valid and still opens in the editor.
        channel.GeneratedPromos.Templates[0].Layers.RemoveAll(layer => layer.Kind == GeneratedLayerKind.UpNext);
        editor.Validate();
        string withoutText = await Render(renderer, channel);
        Assert.Contains("Promo canvas", withoutText);
        Assert.DoesNotContain("block to show the promoted show", withoutText);
    }

    private static async Task<string> Render(HtmlRenderer renderer, ChannelConfig channel) =>
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<GeneratedPromosTab>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(GeneratedPromosTab.Channel)] = channel }));
            return output.ToHtmlString();
        });

    [Theory]
    [InlineData("prores_ks", "yuva444p10le", ".mov", false)]
    [InlineData("qtrle", "argb", ".mov", false)]
    [InlineData("libvpx-vp9", "yuva420p", ".webm", false)]
    [InlineData("libvpx", "yuva420p", ".webm", false)]
    [InlineData("libx264", "yuv420p", ".mp4", true)]
    public async Task Video_layer_alpha_and_keying_match_in_preview_and_render(string codec, string pixelFormat, string extension, bool key)
    {
        Directory.CreateDirectory(_directory);
        string source = Path.Combine(_directory, "source.png");
        using (var bitmap = new SKBitmap(160, 90))
        {
            bitmap.Erase(key ? SKColors.Lime : SKColors.Transparent);
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { Color = SKColors.Red })
                canvas.DrawRect(60, 25, 40, 40, paint);
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using Stream file = File.Create(source);
            png.SaveTo(file);
        }
        string video = Path.Combine(_directory, "source" + extension);
        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-loop", "1", "-i", source, "-t", "1", "-r", "10", "-c:v", codec, "-pix_fmt", pixelFormat];
        if (codec == "prores_ks") arguments.AddRange(["-profile:v", "4"]);
        if (codec == "libvpx") arguments.AddRange(["-auto-alt-ref", "0"]);
        arguments.Add(video);
        await Run("ffmpeg", arguments.ToArray());

        using ServiceProvider services = Services();
        var preview = services.GetRequiredService<GeneratedPromoPreview>();
        var layer = new GeneratedLayer
        {
            Kind = GeneratedLayerKind.Video, Path = video, Width = 160, Height = 120,
            TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None,
            Video = new() { ChromaKey = key }
        };
        var template = new GeneratedPromoTemplate
        {
            Name = "Alpha", Width = 160, Height = 120, Fps = 10, Duration = TimeSpan.FromSeconds(1), BackgroundColor = "#0000FF",
            Layers = [new() { Kind = GeneratedLayerKind.UpNext, Opacity = 0 }, layer]
        };
        UpNextListing[] listings = [new("Show", Noon)];
        using SKBitmap still = SKBitmap.Decode(await preview.RenderStillAsync(template, "Channel", listings, TimeZoneInfo.Utc, TimeSpan.FromSeconds(0.5), 160));
        AssertComposite(still);

        GeneratedPromoRender render = await preview.RenderVideoAsync(template, "Channel", listings, TimeZoneInfo.Utc);
        string renderedFrame = Path.Combine(_directory, "rendered.png");
        await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-ss", "0.5", "-i", render.Path, "-frames:v", "1", renderedFrame);
        using SKBitmap final = SKBitmap.Decode(renderedFrame);
        AssertComposite(final);

        if (key)
        {
            layer.Video!.ChromaKey = false;
            using SKBitmap unkeyed = SKBitmap.Decode(await preview.RenderStillAsync(template, "Channel", listings, TimeZoneInfo.Utc, TimeSpan.FromSeconds(0.5), 160));
            Assert.True(unkeyed.GetPixel(20, 60).Green > 200, "Changing key settings must invalidate cached preview frames.");
        }

        static void AssertComposite(SKBitmap image)
        {
            SKColor background = image.GetPixel(20, 60);
            SKColor foreground = image.GetPixel(80, 60);
            SKColor padding = image.GetPixel(80, 5);
            Assert.True(background.Blue > 200 && background.Green < 50, $"Background should show through: {background}");
            Assert.True(foreground.Red > 200 && foreground.Blue < 50, $"Foreground should remain: {foreground}");
            Assert.True(padding.Blue > 200 && padding.Red < 50, $"Fit padding should be transparent: {padding}");
        }
    }

    [Fact]
    public async Task Animated_masks_match_in_scaled_previews_and_rendered_frames()
    {
        using ServiceProvider services = Services();
        var preview = services.GetRequiredService<GeneratedPromoPreview>();
        var template = new GeneratedPromoTemplate
        {
            Name = "Moving mask", Width = 200, Height = 100, Fps = 10, Duration = TimeSpan.FromSeconds(2), BackgroundColor = "#0000FF",
            Layers =
            [
                new() { Id = "target", Kind = GeneratedLayerKind.Rect, Width = 200, Height = 100, Color = "#FF0000",
                    TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None },
                new() { Kind = GeneratedLayerKind.Rect, Width = 60, Height = 100, Color = "#FFFFFF",
                    TransitionIn = GeneratedTransition.None, TransitionOut = GeneratedTransition.None,
                    Mask = new() { Mode = GeneratedMaskMode.Subtract, Target = "target" }, Keyframes =
                    [
                        new() { At = TimeSpan.Zero, Width = 60, Height = 100 },
                        new() { At = TimeSpan.FromSeconds(2), X = 120, Width = 60, Height = 100 }
                    ] }
            ]
        };
        UpNextListing[] listings = [new("Show", Noon)];
        GeneratedPromoRender render = await preview.RenderVideoAsync(template, "Channel", listings, TimeZoneInfo.Utc);
        foreach (double second in new[] { 0.5, 1.5 })
        {
            using SKBitmap still = SKBitmap.Decode(await preview.RenderStillAsync(template, "Channel", listings, TimeZoneInfo.Utc,
                TimeSpan.FromSeconds(second), 100));
            string frame = Path.Combine(_directory, $"mask-{second}.png");
            await Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-ss", second.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-i", render.Path, "-frames:v", "1", frame);
            using SKBitmap final = SKBitmap.Decode(frame);
            int cut = second < 1 ? 50 : 120;
            int kept = second < 1 ? 150 : 50;
            Assert.True(still.GetPixel(cut / 2, 25).Blue > 200);
            Assert.True(still.GetPixel(kept / 2, 25).Red > 200);
            Assert.True(final.GetPixel(cut, 50).Blue > 200);
            Assert.True(final.GetPixel(kept, 50).Red > 200);
        }
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<SchedulerServiceOptions>>(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(_directory, "schedule.yaml"), WatchConfig = false, AutoRebuild = false
        }));
        services.AddSingleton<IOptions<GeneratedPromoOptions>>(Options.Create(new GeneratedPromoOptions { OutputPath = Path.Combine(_directory, "renders") }));
        services.AddSingleton<SchedulerConfigProvider>();
        services.AddSingleton<ConfigEditor>();
        services.AddSingleton(new TemplateAssets(_directory));
        services.AddSingleton(new TemplatePackages(_directory));
        // The canvas measures itself through JavaScript, but only after rendering in a browser, which never happens here.
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddHttpClient();
        services.AddSingleton<ITextToSpeechProvider, TikTokTextToSpeech>();
        services.AddSingleton<TextToSpeechCatalog>();
        services.AddSingleton<GeneratedPromoService>();
        services.AddSingleton<GeneratedPromoPreview>();
        services.AddSingleton(new GeneratorSetup(_directory, NullLogger<GeneratorSetup>.Instance));
        return services.BuildServiceProvider();
    }

    private static async Task Run(string file, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}
