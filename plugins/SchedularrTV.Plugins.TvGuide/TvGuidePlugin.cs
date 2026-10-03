using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.TvGuide;

/// <summary>Scrolling listings channels drawn from the published schedules.</summary>
public sealed class TvGuidePlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new GuideStore(context.DataDirectory));
        services.AddSingleton(new GuideSettings(context));
        services.AddSingleton<GuideData>();
        services.AddSingleton(sp =>
        {
            GuideStore store = sp.GetRequiredService<GuideStore>();
            GuideSettings settings = sp.GetRequiredService<GuideSettings>();
            var hub = new LiveStreamHub(
                id => store.Find(id) is { } guide ? settings.Spec(guide) : null,
                () => settings.KeepAlive,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("TvGuide"));

            // A guide whose settings changed starts again with them on its next viewer.
            store.Changed += () => _ = hub.RestartChangedAsync();
            return hub;
        });
        services.AddMediaSource<GuideLibrary>("tv-guide");
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        const string prefix = GuideSettings.RoutePrefix;

        // The page the hidden browser shows ("preview" is the editor's, before a guide is saved).
        endpoints.MapGet(prefix + "/view/{id}", (string id, bool? preview, GuideStore store) =>
        {
            GuideStation guide = store.Find(id) ?? new GuideStation { Id = id };
            return Results.Content(GuidePage.Html(GuidePageConfig.For(guide, preview == true)), "text/html; charset=utf-8");
        });

        // Every channel's programmes for the next few hours.
        endpoints.MapGet(prefix + "/listings.json", (double? hours, GuideData data, TimeProvider clock) =>
            Results.Json(data.Listings(clock.GetUtcNow(), TimeSpan.FromHours(Math.Clamp(hours ?? 2, 0.5, 6)))));

        // The ads for a guide's top panel. The editor's preview passes the query it's trying out.
        endpoints.MapGet(prefix + "/ads/{id}.json", async (string id, string? query, GuideStore store, GuideData data, CancellationToken cancellationToken) =>
        {
            GuideStation guide = (store.Find(id) ?? new GuideStation { Id = id }).Copy();
            if (query is not null)
            {
                guide.AdsQuery = query;
            }

            IReadOnlyList<GuideAd> ads = await data.AdsAsync(guide, cancellationToken);
            return Results.Json(ads.Select((ad, index) => new
            {
                ad.Title,
                Url = $"{prefix}/ad/{Uri.EscapeDataString(id)}/{index}?query={Uri.EscapeDataString(guide.AdsQuery)}",
                Seconds = ad.Duration.TotalSeconds
            }));
        });

        endpoints.MapGet(prefix + "/ad/{id}/{index:int}", async (string id, int index, string? query, GuideStore store, GuideData data, CancellationToken cancellationToken) =>
        {
            GuideStation guide = (store.Find(id) ?? new GuideStation { Id = id }).Copy();
            if (query is not null)
            {
                guide.AdsQuery = query;
            }

            IReadOnlyList<GuideAd> ads = await data.AdsAsync(guide, cancellationToken);
            return index >= 0 && index < ads.Count && File.Exists(ads[index].File)
                ? Results.File(ads[index].File, GuideData.ContentType(ads[index].File), enableRangeProcessing: true)
                : Results.NotFound();
        });

        // What ErsatzTV plays: the guide's live MPEG-TS, started on demand.
        endpoints.MapGet(prefix + "/live/{id}.ts", (string id, HttpContext http, LiveStreamHub hub) => hub.ServeAsync(id, http));

        // The latest frame of a running guide, for the editor.
        endpoints.MapGet(prefix + "/snapshot/{id}.jpg", (string id, LiveStreamHub hub) =>
            hub.Snapshot(id) is { } frame ? Results.File(frame, "image/jpeg") : Results.NotFound());
    }
}
