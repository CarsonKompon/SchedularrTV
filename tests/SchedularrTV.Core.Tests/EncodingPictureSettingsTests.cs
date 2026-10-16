using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Service.Components.Shared;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class EncodingPictureSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_dimensions_replace_the_aspect_dropdown(bool custom)
    {
        string html = await Render(new ChannelEncodingConfig { Height = 480, Width = custom ? 640 : null });
        Assert.Contains("Resolution", html);
        Assert.Contains("480p", html);
        Assert.Contains("Custom</option>", html);
        Assert.Equal(custom, html.Contains("Width (pixels)", StringComparison.Ordinal));
        Assert.Equal(custom, html.Contains("Height (pixels)", StringComparison.Ordinal));
        Assert.Equal(!custom, html.Contains("Aspect ratio", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("source", false)]
    [InlineData("30000/1001", false)]
    [InlineData("48", true)]
    public async Task Custom_rate_input_only_shows_for_a_custom_rate(string? rate, bool custom)
    {
        string html = await Render(new ChannelEncodingConfig { FrameRate = rate });
        Assert.Contains("Match source", html);
        Assert.Contains("29.97 fps", html);
        Assert.Equal(custom, html.Contains("Custom frame rate", StringComparison.Ordinal));
    }

    private static async Task<string> Render(ChannelEncodingConfig value)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<EncodingPictureSettings>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EncodingPictureSettings.Value)] = value,
                [nameof(EncodingPictureSettings.Inherited)] = ErsatzTvEncoding.Defaults(new())
            }));
            return output.ToHtmlString();
        });
    }
}
