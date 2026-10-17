using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Core.Publishing;

namespace SchedularrTV.Providers.ErsatzTv;
public static class ErsatzTvServiceCollectionExtensions
{
    public static IServiceCollection AddErsatzTvPublishing(this IServiceCollection services)
    {
        services.AddSingleton<MediaTrackProbe>();

        // Order matters: the lineup must exist before the fragments it points at are read.
        services.AddSingleton<IPlayoutPublisher, LineupPublisher>();
        services.AddSingleton<IPlayoutPublisher, PlayoutPublisher>();
        services.AddSingleton<IPlayoutPublisher, XmltvPublisher>();

        services.AddSingleton<ErsatzTvProcessSupervisor>();
        services.AddHostedService(sp => sp.GetRequiredService<ErsatzTvProcessSupervisor>());
        services.AddSingleton<IChannelViewers, ErsatzTvViewerMonitor>();

        return services;
    }
}
