using Microsoft.Extensions.Options;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.GeneratedPromos.Components;
using SchedularrTV.Plugins.Editor;

namespace SchedularrTV.GeneratedPromos;

/// <summary>Promos rendered from templates, with voice-overs from the voice plugins.</summary>
public sealed class GeneratedPromosPlugin : IPlugin, IPluginEndpoints
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // Switches on the planner's generated promo choices (which need the Promos plugin, a required dependency).
        services.AddSingleton(PlannerFeature.GeneratedPromos);

        // The settings keep the section they had before this was a plugin, so existing ffmpeg paths still apply.
        services.Configure<GeneratedPromoOptions>(context.AppConfiguration.GetSection(GeneratedPromoOptions.SectionName));
        services.PostConfigure<GeneratedPromoOptions>(options =>
        {
            options.OutputPath = AppPaths.Rooted(context.AppDataDirectory, options.OutputPath);
            options.FfmpegPath = ToolLocator.Resolve(options.FfmpegPath, "ffmpeg", context.AppDataDirectory);
            options.FfprobePath = ToolLocator.Resolve(options.FfprobePath, "ffprobe", context.AppDataDirectory);
        });

        services.AddSingleton<TextToSpeechCatalog>();
        services.AddSingleton<GeneratedPromoService>();
        services.AddSingleton<IScheduleFinisher>(sp => sp.GetRequiredService<GeneratedPromoService>());
        services.AddSingleton<GeneratedPromoPreview>();
        services.AddHostedService(sp => sp.GetRequiredService<GeneratedPromoService>());
        services.AddHostedService<GeneratedPromoRefresher>();

        services.AddSingleton(new DashboardCard(typeof(RenderQueueCard), Order: 10));
        services.AddChannelEditorTab<GeneratedPromosChannelTab>(context, "Generated Promos", order: 50, icon: "sparkles");
        services.AddScheduleSettingsSection<GeneratedPromosSettingsSection>(context, "Generated promos", order: 50);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        // Only render files inside the cache folder are served, so no other path on disk can be requested.
        endpoints.MapGet("/api/generated-promos/files/{**path}", (string path, GeneratedPromoService promos) =>
            promos.RenderedFile(path) is { } file
                ? Results.File(file, "video/mp4", enableRangeProcessing: true)
                : Results.NotFound());
    }
}
