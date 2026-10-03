using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Editor;
using SchedularrTV.Service.Components.Features;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.Service.Plugins;

/// <summary>Promo clips in breaks: Up Next, Now Playing, Coming Up, Eventually and the rest.</summary>
public sealed class PromosPlugin : IBuiltInPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = BuiltInPluginIds.Promos,
        Name = "Promos",
        Version = BuiltInPlugins.Version,
        Description = "Airs promo clips from your commercials library in breaks: what's up next, what's on later, and trailers for later in the week.",
        Author = "SchedularrTV",
        ApiVersion = PluginApi.Version.ToString()
    };

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(PlannerFeature.Promos);
        services.AddChannelEditorTab<PromosChannelTab>(context, "Promos", order: 40, icon: "megaphone");
        services.AddBlockEditorSection<PromosBlockSection>(context, "Promos", order: 40);
        services.AddScheduleSettingsSection<PromosSettingsSection>(context, "Promos", order: 40);
    }
}

/// <summary>Replays a real channel's archived schedule over the channel's own blocks. Off until switched on.</summary>
public sealed class ArchiveFollowPlugin : IBuiltInPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = BuiltInPluginIds.ArchiveFollow,
        Name = "Archive Follow",
        Version = BuiltInPlugins.Version,
        Description = "Follows a real channel's archived schedules for the same date in past years, airing what it aired from your library.",
        Author = "SchedularrTV",
        ApiVersion = PluginApi.Version.ToString(),
        EnabledByDefault = false,
        Pages = [new PluginPage { Path = "/archive", Title = "Archive schedules", Icon = "history" }]
    };

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(PlannerFeature.ArchiveFollow);
        services.AddSingleton<ArchiveBrowser>();
        services.AddChannelEditorTab<ArchiveFollowChannelTab>(context, "Follow", order: 20, icon: "history");
    }
}
