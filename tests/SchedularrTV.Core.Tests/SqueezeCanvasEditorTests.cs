using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SchedularrTV.Core.Configuration;
using SchedularrTV.CreditSqueeze;
using SchedularrTV.CreditSqueeze.Components;
using SchedularrTV.Plugins;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class SqueezeCanvasEditorTests
{
    [Fact]
    public async Task Credits_text_exposes_the_saved_fallback_and_a_canvas_preview_action()
    {
        var canvas = SqueezeCanvas.Create("16:9");
        canvas.Layers.Add(new GeneratedLayer
        {
            Kind = GeneratedLayerKind.Text, Width = 400, Height = 200,
            CreditsText = new() { FallbackText = "Thanks for watching {show}." }
        });
        string html = await Render(new SqueezeLook { UseCanvas = true, Canvases = [canvas] });
        Assert.Contains("Default text if OCR fails", html);
        Assert.Contains("Thanks for watching {show}.</textarea>", html);
        Assert.Contains("Preview default text", html);
    }

    [Fact]
    public async Task Interaction_previews_are_small_and_final_drag_uses_the_latest_absolute_delta()
    {
        var canvas = SqueezeCanvas.Create("16:9");
        var activator = new CaptureEditor();
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IJSRuntime, NoJavaScript>()
            .AddSingleton(provider => new SqueezeRenderer(new CreditSqueezeSettings(new Context()), provider, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SqueezeCanvasEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SqueezeCanvasEditor.Look)] = new SqueezeLook { UseCanvas = true, Canvases = [canvas] },
                [nameof(SqueezeCanvasEditor.Channel)] = new ChannelConfig { Id = "test", Name = "Test" }
            }));
            await activator.Editor!.UpdateInteraction(new("drag", "16:9", 0, 1, 0, false, 200, 100, false));
            Assert.Equal(480, PreviewWidth(output.ToHtmlString()));
            await activator.Editor.UpdateInteraction(new("drag", "16:9", 0, 1, 0, false, 300, 150, true));
            Assert.Equal((300f, 150f), (canvas.Layers[0].X, canvas.Layers[0].Y));
            Assert.Equal(960, PreviewWidth(output.ToHtmlString()));
            await activator.Editor.UpdateInteraction(new("drag", "4:3", 0, 2, 0, false, 999, 999, true));
            Assert.Equal((300f, 150f), (canvas.Layers[0].X, canvas.Layers[0].Y));
        });
    }

    private static int PreviewWidth(string html)
    {
        string data = System.Text.RegularExpressions.Regex.Match(html, "data:image/jpeg;base64,([^\"]+)").Groups[1].Value;
        using var bitmap = SkiaSharp.SKBitmap.Decode(Convert.FromBase64String(System.Net.WebUtility.HtmlDecode(data)));
        return bitmap.Width;
    }

    private sealed class CaptureEditor : IComponentActivator
    {
        public SqueezeCanvasEditor? Editor { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is SqueezeCanvasEditor editor) Editor = editor;
            return component;
        }
    }

    [Fact]
    public async Task Empty_design_has_a_direct_create_action()
    {
        string html = await Render(new SqueezeLook { UseCanvas = true });
        Assert.Contains("Create 16:9 canvas", html);
        Assert.Contains("New canvas aspect ratio", html);
        Assert.DoesNotContain("Credits squeeze canvas", html);
    }

    [Fact]
    public async Task Animated_design_exposes_shared_tools_and_current_values()
    {
        var canvas = SqueezeCanvas.Create("4:3");
        canvas.BackgroundColor = "#123456";
        canvas.Layers.Add(new GeneratedLayer
        {
            Name = "Title", Kind = GeneratedLayerKind.Text, Text = "Up next", Color = "#abcdef",
            Width = 200, Height = 100,
            Keyframes = [new() { Width = 200, Height = 100, Opacity = 0.25f },
                new() { At = TimeSpan.FromSeconds(2), Width = 300, Height = 100, Opacity = 1 }]
        });
        string html = await Render(new SqueezeLook { UseCanvas = true, Canvases = [canvas] });
        Assert.Contains("Credits squeeze canvas", html);
        Assert.Contains("Select tool", html);
        Assert.Contains("Add the promo / commercials panel", html);
        Assert.Contains("Step through frames", html);
        Assert.Contains("Previous keyframe", html);
        Assert.Contains("Stop animating</button>", html);
        Assert.Matches("<button(?=[^>]*class=\"[^\"]*btn-danger)(?=[^>]*title=\"Remove all keyframes)[^>]*>", html);
        Assert.Contains("layer-row selected", html);
        Assert.Contains("value=\"#123456\"", html);
        Assert.Contains("value=\"#abcdef\"", html);
        Assert.Contains("value=\"0.25\"", html);
        Assert.DoesNotContain("value=\"selected.Color\"", html);
    }

    [Fact]
    public async Task File_layer_displays_its_actual_path()
    {
        var canvas = SqueezeCanvas.Create("16:9");
        canvas.Layers.Add(new GeneratedLayer
        {
            Name = "Logo", Kind = GeneratedLayerKind.Image, Path = "missing-logo.png", Width = 100, Height = 100
        });
        string html = await Render(new SqueezeLook { UseCanvas = true, Canvases = [canvas] });
        Assert.Contains("value=\"missing-logo.png\"", html);
        Assert.DoesNotContain("value=\"selected.Path\"", html);
    }

    private static async Task<string> Render(SqueezeLook look)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime, NoJavaScript>()
            .AddSingleton(provider => new SqueezeRenderer(new CreditSqueezeSettings(new Context()), provider, TimeProvider.System, NullLogger<SqueezeRenderer>.Instance))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<SqueezeCanvasEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SqueezeCanvasEditor.Look)] = look,
                [nameof(SqueezeCanvasEditor.Channel)] = new ChannelConfig { Id = "test", Name = "Test channel" }
            }));
            return output.ToHtmlString();
        });
    }

    private sealed class Context : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "credit-squeeze", Name = "Credit squeeze" };
        public string? PluginDirectory => null;
        public string DataDirectory => Path.GetTempPath();
        public string AppDataDirectory => Path.GetTempPath();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        public IConfiguration AppConfiguration { get; } = new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
        public string StaticFilesPath => "/plugins/credit-squeeze/";
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering has no JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
