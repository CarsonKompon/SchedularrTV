using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Plugins;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>Folders on disk as a media source, tagged by folder name: the usual home of commercials and bumpers.</summary>
public sealed class FileSystemPlugin : IBuiltInPlugin
{
    public const string PluginId = "local-media";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Local media folders",
        Version = typeof(FileSystemPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        Description = "TV shows and movies from folders on disk (laid out as Sonarr, Radarr or Kodi do, with .nfo files and pictures), "
                      + "and commercials, bumpers, station IDs and promos tagged by their folder names.",
        Author = "SchedularrTV",
        ApiVersion = PluginApi.Version.ToString(),
        SettingsLink = "settings/app"
    };

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.Configure<FileSystemLibraryOptions>(context.AppConfiguration.GetSection(FileSystemLibraryOptions.SectionName));
        services.AddFileSystemMediaLibrary();
    }
}
