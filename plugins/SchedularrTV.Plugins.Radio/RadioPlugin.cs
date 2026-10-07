using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Radio;

/// <summary>Radio channels: music from a music library (Jellyfin's, local folders, a plugin's) or an internet station, with its cover, lyrics and a visualizer.</summary>
public sealed class RadioPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(sp =>
        {
            var store = new RadioStore(context.DataDirectory);

            // Each station is a show: TV Shows and the pickers show a new or renamed one at once.
            store.Changed += () => sp.GetService<IMediaLibraryNotifier>()?.LibraryChanged();
            return store;
        });
        services.AddSingleton(new RadioSettings(context));
        services.AddSingleton<RadioPlayers>();
        services.AddSingleton<RadioSound>();
        services.AddSingleton<RadioPreviews>();
        services.AddSingleton<OnlineLookups>();
        services.AddHttpClient(OnlineLookups.ClientName, http =>
        {
            http.Timeout = TimeSpan.FromSeconds(10);
            // LRCLIB asks to be told who's asking.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SchedularrTV/1.0 (+https://github.com/)");
        });
        services.AddHttpClient(RadioSound.StreamClientName, http =>
        {
            // A station's stream never ends; only reaching it is timed.
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SchedularrTV/1.0");
        });
        services.AddSingleton(sp =>
        {
            RadioStore store = sp.GetRequiredService<RadioStore>();
            RadioSettings settings = sp.GetRequiredService<RadioSettings>();
            return LiveChannelHub.Create(sp, "Radio", settings, id => store.Find(id) is { } station ? settings.Spec(station) : null,
                hub => new RadioHub(hub), changed => store.Changed += changed);
        });
        services.AddMediaSource<RadioLibrary>("radio");
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        const string prefix = RadioSettings.RoutePrefix;

        // The page the hidden browser shows ("preview" is the editor's, before a station is saved).
        endpoints.MapGet(prefix + "/view/{id}", (string id, bool? preview, RadioStore store) =>
        {
            RadioStation station = store.Find(id) ?? new RadioStation { Id = id, Name = "Radio" };
            return Results.Content(RadioPage.Html(RadioPageConfig.For(station, preview == true)), "text/html; charset=utf-8");
        });

        // What's playing, polled by the page every second. The editor's preview gets a sample while the station is off air.
        endpoints.MapGet(prefix + "/now/{id}.json", async (string id, bool? preview, RadioPlayers players, RadioPreviews previews, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            NowPlayingState? state = players.Get(id);
            if (state is null && preview == true)
            {
                state = await previews.SampleAsync(id, cancellationToken);
            }

            DateTimeOffset now = clock.GetUtcNow();
            return Results.Json(new NowPlayingResponse(state?.Song, (state?.StartedAt ?? now).ToUnixTimeMilliseconds(), state?.Next, state?.Problem,
                now.ToUnixTimeMilliseconds()), RadioPage.Json);
        });

        // A library song's cover, from the music library it's in (so a media server's key never reaches the page).
        endpoints.MapGet(prefix + "/art/{libraryId}/{artId}", async (string libraryId, string artId, HttpContext http, CancellationToken cancellationToken) =>
        {
            IMusicLibrary? library = http.RequestServices.GetServices<IMusicLibrary>()
                .FirstOrDefault(candidate => string.Equals(candidate.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            if (library is null || await library.GetArtAsync(artId, 800, cancellationToken) is not { } image)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(image.Content, image.ContentType);
        });

        // The station's sound, read by its own ffmpeg.
        endpoints.MapGet(prefix + "/sound/{id}.ts", (string id, HttpContext http, RadioSound sound) => sound.ServeAsync(id, http));

        // What ErsatzTV plays, and the latest frame of a running station for the editor.
        endpoints.MapLiveChannels<RadioHub>(prefix);
    }
}

/// <summary>This plugin's running stations.</summary>
public sealed class RadioHub(LiveStreamHub streams) : LiveChannelHub(streams);
