using Microsoft.Extensions.Configuration;

namespace SchedularrTV.Service.Plugins;

internal sealed class PluginContext(PluginDescriptor plugin, string appDataDirectory, IConfiguration configuration, Func<string, bool> isLoaded) : IPluginContext
{
    public PluginManifest Manifest => plugin.Manifest;

    public string? PluginDirectory => plugin.Directory;

    public string DataDirectory
    {
        get
        {
            string folder = Path.Combine(appDataDirectory, "plugin-data", plugin.Id);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public string AppDataDirectory => appDataDirectory;

    public IConfiguration Configuration => configuration.GetSection($"Plugins:{plugin.Id}");

    public IConfiguration AppConfiguration => configuration;

    public bool IsLoaded(string pluginId) => isLoaded(pluginId);

    public string StaticFilesPath => $"/plugins/{plugin.Id}";
}
