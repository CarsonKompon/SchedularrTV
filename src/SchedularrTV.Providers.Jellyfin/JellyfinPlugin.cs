using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Plugins;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>Jellyfin as a media source: shows, movies, collections and artwork.</summary>
public sealed class JellyfinPlugin : IBuiltInPlugin
{
    public const string PluginId = "jellyfin";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Jellyfin",
        Version = typeof(JellyfinPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        Description = "Reads shows, movies, collections and artwork from a Jellyfin server.",
        Author = "SchedularrTV",
        ApiVersion = PluginApi.Version.ToString(),
        // The editor still uses Jellyfin directly for artwork, library lists and extras.
        Required = true,
        SettingsLink = "settings/app"
    };

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.Configure<JellyfinOptions>(context.AppConfiguration.GetSection(JellyfinOptions.SectionName));
        services.AddJellyfinMediaLibrary();
    }
}
