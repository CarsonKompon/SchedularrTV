using SchedularrTV.Overlays.Components;
using SchedularrTV.Plugins.Editor;

namespace SchedularrTV.Overlays;

/// <summary>Screen bugs, lower thirds and other graphics over channels and blocks, drawn by ErsatzTV's graphics engine.</summary>
public sealed class OverlaysPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(_ =>
        {
            var renderer = new OverlayRenderer(() => context.Configuration["RenderFolder"]?.Trim().Trim('"') is { Length: > 0 } configured
                ? configured
                : Path.Combine(context.DataDirectory, "rendered"));

            // Looks not used for a month (a renamed show, an old design) are cleared out.
            _ = Task.Run(() => renderer.Prune(TimeSpan.FromDays(30)));
            return renderer;
        });

        services.AddPlayoutProcessor<OverlayProcessor>();
        services.AddChannelEditorTab<OverlaysChannelTab>(context, "Overlays", order: 55, icon: "layers");
    }
}
