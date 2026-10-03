using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.WebChannels;

/// <summary>Web pages (WeatherStar, a clock, weather maps, any address) as live, schedulable programmes.</summary>
public sealed class WebChannelsPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(sp =>
        {
            var store = new ChannelStore(context.DataDirectory, context.AppDataDirectory);

            // Each channel is a show: TV Shows and the pickers show a new or renamed one at once.
            store.Changed += () => sp.GetService<IMediaLibraryNotifier>()?.LibraryChanged();
            return store;
        });
        services.AddSingleton(new WebChannelSettings(context));
        services.AddSingleton(sp =>
        {
            ChannelStore store = sp.GetRequiredService<ChannelStore>();
            WebChannelSettings settings = sp.GetRequiredService<WebChannelSettings>();
            var hub = new LiveStreamHub(
                id => store.Find(id) is { } channel ? settings.Spec(channel) : null,
                () => settings.KeepAlive,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("WebChannels"));

            // A channel whose settings changed starts again with them on its next viewer.
            store.Changed += () => _ = hub.RestartChangedAsync();
            return new ChannelHub(hub);
        });
        services.AddSingleton<Geocoder>();
        services.AddHttpClient(Geocoder.ClientName, http => http.Timeout = TimeSpan.FromSeconds(15));
        services.AddMediaSource<WebChannelLibrary>(WebChannelLibrary.SourceId);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        const string prefix = WebChannelSettings.RoutePrefix;

        // The clock preset's page; everything it shows comes in its address.
        endpoints.MapGet(prefix + "/clock.html", () => Results.Content(ClockPage.Html, "text/html; charset=utf-8"));

        // What ErsatzTV plays: the channel's live MPEG-TS, started on demand.
        endpoints.MapGet(prefix + "/live/{id}.ts", (string id, HttpContext http, ChannelHub hub) => hub.Streams.ServeAsync(id, http));

        // The latest frame of a running channel, for the Web channels page.
        endpoints.MapGet(prefix + "/snapshot/{id}.jpg", (string id, ChannelHub hub) =>
            hub.Streams.Snapshot(id) is { } frame ? Results.File(frame, "image/jpeg") : Results.NotFound());
    }
}

/// <summary>
/// This plugin's running channels. Wrapped in a type of its own so it can't be mistaken for another live page plugin's
/// <see cref="LiveStreamHub"/> (the TV guide's) should both ever share a service container.
/// </summary>
public sealed class ChannelHub(LiveStreamHub streams) : IAsyncDisposable
{
    public LiveStreamHub Streams { get; } = streams;

    public ValueTask DisposeAsync() => Streams.DisposeAsync();
}

/// <summary>The clock preset's page: one self-contained HTML file in the plugin.</summary>
public static class ClockPage
{
    private static readonly Lazy<string> Template = new(() =>
    {
        using Stream stream = typeof(ClockPage).Assembly.GetManifestResourceStream("clock.html")
            ?? throw new InvalidOperationException("The clock page is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Html => Template.Value;
}
