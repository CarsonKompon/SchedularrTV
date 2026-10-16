using SchedularrTV.Overlays.Components;
using SchedularrTV.Plugins.Editor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SchedularrTV.Overlays;

/// <summary>Screen bugs, lower thirds and other graphics over channels and blocks, drawn by ErsatzTV's graphics engine.</summary>
public sealed class OverlaysPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(provider =>
        {
            var renderer = new OverlayRenderer(() => context.Configuration["RenderFolder"]?.Trim().Trim('"') is { Length: > 0 } configured
                ? configured
                : Path.Combine(context.DataDirectory, "rendered"));

            // Looks not used for a month (a renamed show, an old design) are cleared out.
            _ = Task.Run(() => renderer.Prune(TimeSpan.FromDays(30)));
            return renderer;
        });

        // For the editor: design videos' lengths, sizes and stills.
        services.AddSingleton(provider =>
        {
            var videos = new OverlayVideos(() => OverlayVideos.ResolveFfmpeg(context), Path.Combine(context.DataDirectory, "stills"));

            // Frames taken for scrubbing that no one has looked at for a week are cleared out.
            _ = Task.Run(() => videos.PruneFrames(TimeSpan.FromDays(7)));
            return videos;
        });

        // Animated designs, made into videos beside the drawn ones (ErsatzTV reads both from there).
        services.AddSingleton(provider => new OverlayAnimations(
            () => provider.GetRequiredService<OverlayRenderer>().Folder,
            () => OverlayVideos.ResolveFfmpeg(context),
            provider.GetService<ILoggerFactory>()?.CreateLogger<OverlayAnimations>()));

        services.AddSingleton(provider => new OverlayPreviewVideos(Path.Combine(context.DataDirectory, "previews"),
            () => OverlayVideos.ResolveFfmpeg(context), provider.GetRequiredService<OverlayVideos>()));

        services.AddPlayoutProcessor<OverlayProcessor>();
        services.AddChannelEditorTab<OverlaysChannelTab>(context, "Overlays", order: 55, icon: "layers");
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context)
    {
        endpoints.MapGet("/api/overlays/previews/{name}", (string name, bool? download, OverlayPreviewVideos previews) =>
            previews.RenderedFile(name) is { } file
                ? Results.File(file, Path.GetExtension(file) == ".webm" ? "video/webm" : "video/mp4",
                    fileDownloadName: download == true ? name : null, enableRangeProcessing: true)
                : Results.NotFound());
    }
}
