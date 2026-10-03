using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service;
using SchedularrTV.Service.Components.Shared;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class GeneratedPromoEditorTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"generated-promo-editor-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

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

        channel.GeneratedPromos.Templates[0].Layers.RemoveAll(layer => layer.Kind == GeneratedLayerKind.UpNext);
        editor.Validate();
        string invalid = await Render(renderer, channel);
        Assert.Contains("block to show the promoted show", invalid);
        Assert.Contains("up_next", invalid);
        Assert.Contains("text layer.", invalid);
    }

    private static async Task<string> Render(HtmlRenderer renderer, ChannelConfig channel) =>
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<GeneratedPromosTab>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(GeneratedPromosTab.Channel)] = channel }));
            return output.ToHtmlString();
        });

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
        services.AddHttpClient();
        services.AddSingleton<ITextToSpeechProvider, TikTokTextToSpeech>();
        services.AddSingleton<TextToSpeechCatalog>();
        services.AddSingleton<GeneratedPromoService>();
        services.AddSingleton<GeneratedPromoPreview>();
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
}
