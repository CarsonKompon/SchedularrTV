using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Plugins;

/// <summary>
/// A plugin's entry point. The scheduler creates one instance per start (with a parameterless constructor) and
/// calls <see cref="ConfigureServices"/> before the app is built, so everything a plugin adds is ordinary
/// dependency injection: register the extension points in <see cref="PluginServiceCollectionExtensions"/>,
/// hosted services, options and anything else.
/// </summary>
/// <remarks>
/// <para>Plugins run with the same permissions as the scheduler.</para>
/// <para>
/// <see cref="ConfigureServices"/> runs at startup whether the plugin is switched on or off, and the plugin's switch
/// then applies while running: its contributions (media sources, outputs, processors, voices, editor sections and
/// so on) are left out while it's off, its hosted services are stopped, and its endpoints and pages answer "not
/// found". Settings saved on the Plugins page apply at once too, provided they're read through options
/// (<c>IOptions&lt;T&gt;.Value</c> when needed, not kept from a constructor) or <see cref="IPluginContext.Configuration"/>.
/// Installing, updating or removing a plugin's files takes a restart.
/// </para>
/// </remarks>
public interface IPlugin
{
    void ConfigureServices(IServiceCollection services, IPluginContext context);
}

/// <summary>A plugin compiled into the scheduler; it carries its manifest instead of a <c>plugin.json</c>.</summary>
public interface IBuiltInPlugin : IPlugin
{
    PluginManifest Manifest { get; }
}

/// <summary>Optionally implemented by an <see cref="IPlugin"/> to add HTTP endpoints, under <c>/plugins/&lt;id&gt;</c> by convention.</summary>
public interface IPluginEndpoints
{
    void MapEndpoints(IEndpointRouteBuilder endpoints, IPluginContext context);
}

/// <summary>What the scheduler tells a plugin about itself and its surroundings.</summary>
public interface IPluginContext
{
    PluginManifest Manifest { get; }

    /// <summary>The plugin's own folder; null for built-in plugins.</summary>
    string? PluginDirectory { get; }

    /// <summary>A folder the plugin may write to, kept across updates: <c>&lt;data folder&gt;/plugin-data/&lt;id&gt;</c>.</summary>
    string DataDirectory { get; }

    /// <summary>The scheduler's data folder.</summary>
    string AppDataDirectory { get; }

    /// <summary>The plugin's settings: the <c>Plugins:&lt;id&gt;</c> configuration section.</summary>
    IConfiguration Configuration { get; }

    /// <summary>The whole app configuration, for plugins that read established sections such as <c>Jellyfin</c>.</summary>
    IConfiguration AppConfiguration { get; }

    /// <summary>
    /// Whether another plugin is installed and loaded (switched on or off), for optional dependencies. Its services
    /// are registered either way; its contributions just drop out of the lists while it's off.
    /// </summary>
    bool IsLoaded(string pluginId);

    /// <summary>Static files in the plugin's <c>wwwroot</c> folder are served from here, e.g. <c>/plugins/acme.bug/</c>.</summary>
    string StaticFilesPath { get; }
}
