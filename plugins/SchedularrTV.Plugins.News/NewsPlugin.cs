using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SchedularrTV.Plugins.Speech;

namespace SchedularrTV.News;

/// <summary>Local news breaks: headlines from feeds, with weather, markets, scores, a map and lottery results, optionally read out.</summary>
public sealed class NewsPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(sp =>
        {
            var store = new NewsStore(context.DataDirectory);

            // Each show is a virtual show: TV Shows and the pickers show a new or renamed one at once.
            store.Changed += () => sp.GetService<IMediaLibraryNotifier>()?.LibraryChanged();
            return store;
        });
        services.AddSingleton(new NewsSettings(context));
        services.AddSingleton<NewsSources>();
        services.AddSingleton<NewsPreviews>();
        services.AddSingleton(sp => new NewsVoice(
            sp.GetRequiredService<IEnumerable<ITextToSpeechProvider>>(),
            sp.GetRequiredService<NewsSettings>(),
            context.DataDirectory,
            sp.GetRequiredService<ILogger<NewsVoice>>()));
        services.AddSingleton<NewsCasts>();
        services.AddSingleton<NewsSound>();
        services.AddSingleton(sp => new NewsTiles(
            sp.GetRequiredService<NewsSources>(),
            sp.GetRequiredService<NewsSettings>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            context.DataDirectory,
            sp.GetRequiredService<ILogger<NewsTiles>>()));
        services.AddHttpClient(NewsSources.ClientName, http =>
        {
            http.Timeout = TimeSpan.FromSeconds(20);
            // OpenStreetMap's tile policy and Yahoo both want to know who's asking.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SchedularrTV/1.0; +https://github.com/) news-plugin");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, application/json, image/png, */*;q=0.8");
        });
        services.AddSingleton(sp =>
        {
            NewsStore store = sp.GetRequiredService<NewsStore>();
            NewsSettings settings = sp.GetRequiredService<NewsSettings>();
            return LiveChannelHub.Create(sp, "News", settings, id => store.Find(id) is { } show ? settings.Spec(show) : null,
                hub => new NewsHub(hub), changed => store.Changed += changed);
        });
        services.AddMediaSource<NewsLibrary>(NewsLibrary.SourceId);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        const string prefix = NewsSettings.RoutePrefix;

        // The page the hidden browser shows (and the editor's preview, with ?preview=true).
        endpoints.MapGet(prefix + "/view/{id}", (string id, bool? preview) =>
            Results.Content(NewsPage.Html(id, preview == true), "text/html; charset=utf-8"));

        // The show's look, its rundown and data, and where it is; polled by the page every second or two.
        endpoints.MapGet(prefix + "/state/{id}.json", (string id, bool? preview, NewsStore store, NewsPreviews previews, NewsCasts casts) =>
        {
            bool isPreview = preview == true;
            NewsShow? show = isPreview ? previews.Get(id) ?? store.Find(id) : store.Find(id);
            if (show is null)
            {
                return Results.NotFound();
            }

            // The preview isn't read out (it has no sound), so it never waits for voice-overs.
            return Results.Json(casts.State(NewsCasts.Key(show.Id, isPreview), show, speak: !isPreview, isPreview), NewsPage.Json);
        });

        // The show's sound (its voice-overs and music), read by its own ffmpeg.
        endpoints.MapGet(prefix + "/sound/{id}.ts", (string id, HttpContext http, NewsSound sound) => sound.ServeAsync(id, http));

        // The map's tiles: streets, radar and traffic.
        endpoints.MapGet(prefix + "/tiles/{layer}/{z:int}/{x:int}/{y:int}.png", (string layer, int z, int x, int y, NewsTiles tiles, CancellationToken cancellationToken) =>
            tiles.ServeAsync(layer, z, x, y, cancellationToken));

        // What ErsatzTV plays, and the latest frame of a running show for the editor.
        endpoints.MapLiveChannels<NewsHub>(prefix);
    }
}

/// <summary>This plugin's running shows.</summary>
public sealed class NewsHub(LiveStreamHub streams) : LiveChannelHub(streams);
