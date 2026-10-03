using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Providers.Jellyfin;
using SchedularrTV.Service.Components.Shared;

namespace SchedularrTV.Service.Plugins;

/// <summary>
/// The settings of the built-in plugins whose settings live in the app settings (not under <c>Plugins:&lt;id&gt;</c>):
/// the same sections App settings shows, opened on their own from the plugin's gear on the Plugins page.
/// </summary>
/// <remarks>Each takes an <c>AppSettingsForm Form</c> and a <c>Changed</c> callback.</remarks>
public static class BuiltInSettingsPanels
{
    public static Type? For(string pluginId) => pluginId switch
    {
        JellyfinPlugin.PluginId => typeof(JellyfinSettings),
        ErsatzTvPlugin.PluginId => typeof(ErsatzSettings),
        FileSystemPlugin.PluginId => typeof(FolderSettings),
        _ => null
    };
}
