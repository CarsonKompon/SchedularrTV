using System.Reflection;

namespace SchedularrTV.Service.Plugins;

public enum PluginSource
{
    /// <summary>Compiled into the scheduler.</summary>
    BuiltIn,

    /// <summary>Shipped in the app's own <c>plugins</c> folder.</summary>
    Bundled,

    /// <summary>Added by the user to the data folder's <c>plugins</c> folder.</summary>
    Installed
}

/// <summary>What became of a plugin at startup.</summary>
public enum PluginStatus
{
    /// <summary>Loaded, with its services registered; whether it's running is its switch (<see cref="PluginDescriptor.Enabled"/>).</summary>
    Active,

    /// <summary>Not loaded (found after startup, or not loaded for another reason); takes a restart.</summary>
    Disabled,
    Incompatible,
    MissingDependency,
    Failed
}

/// <summary>One plugin as found at startup, and what became of it.</summary>
public sealed class PluginDescriptor
{
    public required PluginManifest Manifest { get; init; }

    public required PluginSource Source { get; init; }

    /// <summary>The plugin's folder; null for built-in plugins.</summary>
    public string? Directory { get; init; }

    /// <summary>Its switch: whether the user (or the manifest default) wants it on. Changes apply at once.</summary>
    public bool Enabled { get; set; }

    /// <summary>The load outcome. Every working plugin is loaded, on or off, so its switch can apply without a restart.</summary>
    public PluginStatus Status { get; set; } = PluginStatus.Disabled;

    /// <summary>Why it couldn't be loaded, when it couldn't.</summary>
    public string? Problem { get; set; }

    /// <summary>Set while it's switched on but a plugin it needs is switched off.</summary>
    public string? Blocked { get; internal set; }

    public IPlugin? Instance { get; set; }

    /// <summary>The entry assembly of an external plugin.</summary>
    public Assembly? Assembly { get; set; }

    internal PluginLoadContext? LoadContext { get; set; }

    /// <summary>Loaded and its services registered (running or not).</summary>
    public bool IsLoaded => Status == PluginStatus.Active;

    /// <summary>Running: loaded, switched on, and everything it needs is running.</summary>
    public bool IsActive => IsLoaded && Enabled && Blocked is null;

    public string Id => Manifest.Id;
}

/// <summary>
/// Every plugin the running server found, for the Plugins page, the sidebar and the router, and the switches that
/// turn them on and off while running.
/// </summary>
public sealed class PluginRegistry
{
    public PluginRegistry(IReadOnlyList<PluginDescriptor> plugins, string userDirectory, string? bundledDirectory)
    {
        Plugins = plugins;
        UserDirectory = userDirectory;
        BundledDirectory = bundledDirectory;
        UpdateBlocked();
    }

    public IReadOnlyList<PluginDescriptor> Plugins { get; }

    /// <summary>Where installed plugins live.</summary>
    public string UserDirectory { get; }

    public string? BundledDirectory { get; }

    /// <summary>Set once something was changed that only a restart applies (installing or removing a plugin).</summary>
    public bool RestartRequired { get; private set; }

    /// <summary>Plugins with changes waiting for a restart, so the page can say "pending".</summary>
    public HashSet<string> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which plugin registered each service, so its contributions can follow its switch (see <see cref="PluginServiceGate"/>).</summary>
    public Dictionary<ServiceDescriptor, string> ServiceOwners { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Raised when a plugin is switched on or off, or anything else about the plugins changed.</summary>
    public event Action? Changed;

    public PluginDescriptor? Find(string id) =>
        Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the plugin is running now.</summary>
    public bool IsActive(string id) => Find(id)?.IsActive == true;

    /// <summary>Whether the plugin's services were registered at startup (running or switched off).</summary>
    public bool IsLoaded(string id) => Find(id)?.IsLoaded == true;

    /// <summary>
    /// Assemblies of loaded external plugins, searched for routable pages. Every loaded plugin's pages are routed, so
    /// switching one on needs no restart; a switched-off plugin's page says so instead of showing.
    /// </summary>
    public IReadOnlyList<Assembly> PageAssemblies =>
        [.. Plugins.Where(plugin => plugin.IsLoaded && plugin.Assembly is not null && plugin.Manifest.Pages.Count > 0)
            .Select(plugin => plugin.Assembly!)
            .Where(assembly => assembly != typeof(PluginRegistry).Assembly)
            // The router keys assemblies by name; two plugins shipping the same assembly name would clash.
            .DistinctBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Sidebar entries added by running plugins.</summary>
    public IEnumerable<(PluginDescriptor Plugin, PluginPage Page)> Pages =>
        Plugins.Where(plugin => plugin.IsActive).SelectMany(plugin => plugin.Manifest.Pages.Select(page => (plugin, page)));

    /// <summary>The loaded external plugin a page component comes from, if any.</summary>
    public PluginDescriptor? OwnerOf(Type component) =>
        component.Assembly == typeof(PluginRegistry).Assembly ? null
        : Plugins.FirstOrDefault(plugin => plugin.Assembly is not null && plugin.Assembly == component.Assembly);

    /// <summary>
    /// Switches a loaded plugin on or off now. Plugins that need it follow it. Returns false when the plugin isn't
    /// loaded, so the change only applies after a restart.
    /// </summary>
    public bool SetEnabled(string id, bool enabled)
    {
        PluginDescriptor plugin = Find(id) ?? throw new InvalidOperationException($"There's no plugin '{id}'.");
        plugin.Enabled = enabled;
        UpdateBlocked();
        Changed?.Invoke();
        return plugin.IsLoaded;
    }

    public void MarkPending(string id)
    {
        Pending.Add(id);
        RestartRequired = true;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>A switched-on plugin waits while a plugin it needs is off (or waiting itself).</summary>
    private void UpdateBlocked()
    {
        foreach (PluginDescriptor plugin in Plugins)
        {
            plugin.Blocked = null;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (PluginDescriptor plugin in Plugins.Where(plugin => plugin.IsActive))
            {
                foreach (PluginDependency dependency in plugin.Manifest.Dependencies.Where(dependency => !dependency.Optional))
                {
                    if (Find(dependency.Id) is { } needed && !needed.IsActive)
                    {
                        plugin.Blocked = $"Waiting for {needed.Manifest.Name}, which is switched off.";
                        changed = true;
                        break;
                    }
                }
            }
        }
    }
}
