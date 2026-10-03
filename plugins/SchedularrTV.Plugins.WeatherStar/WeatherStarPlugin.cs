using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.WeatherStar;

/// <summary>Live WeatherStar 4000+ forecasts as schedulable programmes.</summary>
public sealed class WeatherStarPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new StationStore(context.DataDirectory));
        services.AddSingleton(new WeatherStarSettings(context));
        services.AddSingleton<StreamAddress>();
        services.AddSingleton<StreamHub>();
        services.AddSingleton<Geocoder>();
        services.AddHttpClient(Geocoder.ClientName, http => http.Timeout = TimeSpan.FromSeconds(15));
        services.AddMediaSource<WeatherLibrary>("ws4kp");
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        // What ErsatzTV plays: the station's live MPEG-TS, started on demand.
        endpoints.MapGet(StreamAddress.RoutePrefix + "/{station}.ts", (string station, HttpContext http, StreamHub hub) => hub.ServeAsync(station, http));

        // The latest frame of a running station, for the Weather stations page.
        endpoints.MapGet("/plugins/ws4kp/snapshot/{station}.jpg", (string station, StreamHub hub) =>
            hub.Snapshot(station) is { } frame ? Results.File(frame, "image/jpeg") : Results.NotFound());
    }
}
