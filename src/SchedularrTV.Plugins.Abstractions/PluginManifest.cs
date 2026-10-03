using System.Text.Json.Serialization;

namespace SchedularrTV.Plugins;

/// <summary>The plugin contract version this build of the scheduler implements.</summary>
public static class PluginApi
{
    /// <summary>
    /// A plugin declares the version it was built against in <see cref="PluginManifest.ApiVersion"/>. It loads when
    /// the major versions match and its minor version is no newer than this; a major bump means a breaking change.
    /// </summary>
    /// <remarks>
    /// 1.1 added editor sections (<c>channelSettings</c>, <c>blockSettings</c>, <c>scheduleSettings</c>). 1.2 added
    /// media enrichers (<c>AddMediaEnricher</c>). 1.3 added external channels (<c>AddExternalChannels</c>) and
    /// <c>IScheduleRepublisher</c>. 1.4 added <c>IMediaLibraryNotifier</c>.
    /// </remarks>
    public static readonly Version Version = new(1, 4);

    public static bool IsCompatible(Version wanted) => wanted.Major == Version.Major && wanted.Minor <= Version.Minor;
}

/// <summary>
/// Describes a plugin: <c>plugin.json</c> in a plugin's folder, or <see cref="IBuiltInPlugin.Manifest"/> for the
/// plugins compiled into the scheduler.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Unique, stable id: lowercase letters, digits, dots and dashes, e.g. <c>acme.screen-bug</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The plugin's own version, e.g. <c>1.2.0</c>.</summary>
    public string Version { get; set; } = "1.0.0";

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? Website { get; set; }

    /// <summary>The <see cref="PluginApi.Version"/> the plugin was built against, e.g. <c>1.0</c>.</summary>
    public string ApiVersion { get; set; } = "1.0";

    /// <summary>File name of the assembly holding the <see cref="IPlugin"/> class, relative to the plugin folder.</summary>
    public string? EntryAssembly { get; set; }

    /// <summary>Full name of the <see cref="IPlugin"/> class when the assembly has more than one.</summary>
    public string? EntryType { get; set; }

    public List<PluginDependency> Dependencies { get; set; } = [];

    /// <summary>Whether a newly found plugin starts enabled.</summary>
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>Built-in plugins the rest of the app can't run without yet; they can't be switched off.</summary>
    [JsonIgnore]
    public bool Required { get; set; }

    /// <summary>Settings shown on the Plugins page and stored under <c>Plugins:&lt;id&gt;</c> in settings.json.</summary>
    public List<PluginSetting> Settings { get; set; } = [];

    /// <summary>
    /// Fields of the plugin's <c>plugins.&lt;id&gt;</c> section on each channel, shown as a tab on the channel's page.
    /// Keys are written to the YAML as given, so use snake_case to match the rest of the file.
    /// </summary>
    public List<PluginSetting> ChannelSettings { get; set; } = [];

    /// <summary>Fields of the plugin's section on each block, shown in the block editor.</summary>
    public List<PluginSetting> BlockSettings { get; set; } = [];

    /// <summary>Fields of the plugin's section under <c>settings:</c>, shown on the Schedule settings page.</summary>
    public List<PluginSetting> ScheduleSettings { get; set; } = [];

    /// <summary>
    /// Full name of a Razor component (in the entry assembly) shown below the generated settings fields, for
    /// anything they can't do (downloads, tests). It receives a <c>[Parameter] PluginSettingsEditor Editor</c>,
    /// and is only shown while the plugin is loaded. List no settings to have it shown on its own.
    /// </summary>
    public string? SettingsComponent { get; set; }

    /// <summary>Pages the plugin adds to the sidebar; each must be a routable component in the entry assembly.</summary>
    public List<PluginPage> Pages { get; set; } = [];

    /// <summary>Where the settings for a built-in plugin are edited, if not on the Plugins page.</summary>
    [JsonIgnore]
    public string? SettingsLink { get; set; }
}

public sealed class PluginDependency
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Lowest acceptable version of the dependency, e.g. <c>1.1.0</c>; null accepts any.</summary>
    public string? MinVersion { get; set; }

    /// <summary>An optional dependency is used when present and enabled; the plugin still loads without it.</summary>
    public bool Optional { get; set; }
}

public enum PluginSettingType
{
    Text,
    Secret,
    Number,
    Boolean,
    File,
    Folder,
    Choice
}

public sealed class PluginSetting
{
    /// <summary>Key under the plugin's configuration section, e.g. <c>ImagePath</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PluginSettingType Type { get; set; } = PluginSettingType.Text;

    public string? Description { get; set; }

    public string? Default { get; set; }

    public string? Placeholder { get; set; }

    /// <summary>Choices for <see cref="PluginSettingType.Choice"/>.</summary>
    public List<string> Options { get; set; } = [];
}

public sealed class PluginPage
{
    /// <summary>The page's route, which must start with <c>/plugins/&lt;id&gt;</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>One of the app's icon names, e.g. <c>image</c>; a generic icon otherwise.</summary>
    public string? Icon { get; set; }
}
