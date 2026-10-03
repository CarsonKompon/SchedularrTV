using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.Iptv;

/// <summary>IPTV channels (an M3U playlist and its XMLTV guide) published alongside the scheduler's own.</summary>
public sealed class IptvPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new SourceStore(context.DataDirectory));
        services.AddSingleton(new IptvSettings(context));
        services.AddSingleton<IptvCatalog>();
        services.AddSingleton<IptvFetcher>();
        services.AddSingleton<RelayHub>();
        services.AddHttpClient(IptvFetcher.ClientName, http => http.Timeout = TimeSpan.FromMinutes(5));
        services.AddExternalChannels<IptvChannels>();

        // Also resolvable on its own, so the IPTV page can ask for a refresh.
        services.AddSingleton<IptvRefresher>();
        services.AddHostedService(sp => sp.GetRequiredService<IptvRefresher>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        // What ErsatzTV plays: the provider's stream for a carried channel, relayed as MPEG-TS.
        endpoints.MapGet(IptvSettings.RoutePrefix + "/live/{source}/{number:int}.ts", async (string source, int number, HttpContext http, IptvChannels channels, RelayHub hub) =>
        {
            if (channels.Target(source, number) is not { } target)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await hub.ServeAsync(target, http).ConfigureAwait(false);
        });
    }
}
