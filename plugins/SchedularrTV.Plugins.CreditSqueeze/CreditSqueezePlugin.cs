using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SchedularrTV.CreditSqueeze.Components;
using SchedularrTV.Plugins.Editor;

namespace SchedularrTV.CreditSqueeze;

/// <summary>
/// Credit squeeze: as a show's end credits (or its own outro) roll, they shrink into a box and the rest of the picture
/// says what's on next, the way networks do.
/// </summary>
public sealed class CreditSqueezePlugin : IPlugin, IPluginEndpoints
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        // The editor's preview videos; only files in the previews folder are served.
        endpoints.MapGet("/api/credit-squeeze/preview/{name}", (string name, SqueezeRenderer renderer) =>
            renderer.PreviewFile(name) is { } file ? Results.File(file, "video/mp4", enableRangeProcessing: true) : Results.NotFound());
    }

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(new CreditSqueezeSettings(context));
        services.AddSingleton<CreditsMarkers>();
        services.AddHostedService(sp => sp.GetRequiredService<CreditsMarkers>());
        services.AddSingleton(sp =>
        {
            var renderer = ActivatorUtilities.CreateInstance<SqueezeRenderer>(sp);

            // A squeeze just made, or credits just found, go in at the next publish.
            renderer.Made += () => sp.GetService<IScheduleRepublisher>()?.RequestRepublish();
            sp.GetRequiredService<CreditsMarkers>().Found += () => sp.GetService<IScheduleRepublisher>()?.RequestRepublish();
            return renderer;
        });
        services.AddHostedService(sp => sp.GetRequiredService<SqueezeRenderer>());
        services.AddPlayoutProcessor<CreditSqueezeProcessor>();
        services.AddHostedService<TuningInWatcher>();
        services.AddChannelEditorTab<CreditSqueezeChannelTab>(context, "Credit Squeeze", order: 60, icon: "film");
    }
}
