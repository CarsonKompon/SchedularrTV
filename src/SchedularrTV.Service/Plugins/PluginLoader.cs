using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PluginEditing = SchedularrTV.Plugins.Editor;

namespace SchedularrTV.Service.Plugins;

/// <summary>
/// Finds, checks and loads plugins at startup, then lets each active one register its services. Runs before the
/// host is built, so nothing here can use dependency injection or logging; problems are recorded on each
/// <see cref="PluginDescriptor"/> and logged once the host is up.
/// </summary>
public static partial class PluginLoader
{
    public const string ManifestFileName = "plugin.json";
    public const string StatesSection = "PluginStates";
    public const string PendingFolder = ".pending";
    public const string RemoveFolder = ".remove";

    internal static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // No leading, trailing or doubled dots: Windows silently drops trailing dots from folder names.
    [GeneratedRegex(@"^(?=.{2,64}$)[a-z0-9](?:[a-z0-9-]|\.(?=[a-z0-9]))*[a-z0-9]$")]
    internal static partial Regex IdPattern();

    public const string PluginsDirectoryArgument = "--plugins-dir";

    /// <summary>Where plugins went before they lived beside the app (and still go for source checkouts and macOS).</summary>
    public static string UserDirectory(string dataDirectory) => Path.Combine(dataDirectory, "plugins");

    public static string? BundledDirectory()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "plugins");
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// Where plugins are installed: a <c>plugins</c> folder in the app's own folder (beside the tray app, or beside the
    /// server when it runs on its own), so updating the app by replacing its files keeps them, and a plugin is updated by
    /// replacing its folder there. <c>--plugins-dir</c> picks another folder.
    /// </summary>
    /// <remarks>
    /// The data folder is used instead for a source checkout (the build output is cleaned and rebuilt), inside a macOS
    /// .app (replacing the app replaces everything in it, and writing into it breaks its signature), and when the app's
    /// folder can't be written to.
    /// </remarks>
    /// <param name="sourceRoot">A checkout's root: plugins installed while running from it go in its <c>.plugins</c> folder.</param>
    public static string ResolveInstallDirectory(IReadOnlyList<string> args, string dataDirectory, bool runningFromSource, string? sourceRoot = null)
    {
        if (SchedularrTV.Hosting.AppPaths.Argument(args, PluginsDirectoryArgument) is { Length: > 0 } chosen)
        {
            return Path.GetFullPath(chosen);
        }

        if (runningFromSource && sourceRoot is not null)
        {
            return Path.Combine(sourceRoot, SourcePlugins.InstallFolder);
        }

        string programFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (runningFromSource || programFolder.Contains(".app" + Path.DirectorySeparatorChar + "Contents", StringComparison.Ordinal))
        {
            return UserDirectory(dataDirectory);
        }

        // The download puts the tray at the top of the folder and the server in "server/" beside it.
        string appFolder = programFolder;
        if (string.Equals(Path.GetFileName(programFolder), "server", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(programFolder) is { } parent
            && (File.Exists(Path.Combine(parent, "SchedularrTV.exe")) || File.Exists(Path.Combine(parent, "SchedularrTV"))))
        {
            appFolder = parent;
        }

        string folder = Path.Combine(appFolder, "plugins");
        return CanWrite(folder) ? folder : UserDirectory(dataDirectory);
    }

    private static IEnumerable<string> PluginFolders(string? parent) =>
        parent is null || !Directory.Exists(parent)
            ? []
            : Directory.EnumerateDirectories(parent).Where(path => !Path.GetFileName(path).StartsWith('.'));

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Plugins installed in the data folder by an earlier version are moved into the app's plugins folder (unless one
    /// of the same name is already there), so they keep working and update in the new place.
    /// </summary>
    private static void MoveLegacyPlugins(string legacyDirectory, string installDirectory)
    {
        if (!Directory.Exists(legacyDirectory)
            || string.Equals(Path.GetFullPath(legacyDirectory), Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyPendingChanges(legacyDirectory);
        foreach (string folder in Directory.EnumerateDirectories(legacyDirectory).Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            string target = Path.Combine(installDirectory, Path.GetFileName(folder));
            if (Directory.Exists(target))
            {
                continue;
            }

            try
            {
                MoveDirectory(folder, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left where it is and tried again next start; the plugin isn't loaded from there meanwhile.
            }
        }
    }

    // Directory.Move can't cross drives, and the data folder and the app are often on different ones.
    private static void MoveDirectory(string source, string destination)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }

        string partial = destination + ".moving";
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(partial, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        Directory.CreateDirectory(partial);
        Directory.Move(partial, destination);
        Directory.Delete(source, recursive: true);
    }

    /// <summary>Discovers, resolves and loads; the returned registry's active plugins are ready to configure.</summary>
    /// <param name="installDirectory">Where plugins are installed (see <see cref="ResolveInstallDirectory"/>); the data folder's <c>plugins</c> when null.</param>
    /// <param name="sourcePlugins">
    /// Running from a checkout: each plugin's build output folder (see <see cref="SourcePlugins"/>), loaded as included
    /// plugins. The per-user data folder's plugins are then left alone, neither loaded nor moved.
    /// </param>
    public static PluginRegistry Load(string dataDirectory, IConfiguration configuration, IReadOnlyList<IBuiltInPlugin> builtIns, string? installDirectory = null,
        IReadOnlyList<string>? sourcePlugins = null)
    {
        string userDirectory = installDirectory ?? UserDirectory(dataDirectory);
        Directory.CreateDirectory(userDirectory);
        if (sourcePlugins is null)
        {
            MoveLegacyPlugins(UserDirectory(dataDirectory), userDirectory);
        }

        ApplyPendingChanges(userDirectory);

        // A server run on its own installs into the plugins folder beside it, which is also where shipped plugins would sit.
        string? bundledDirectory = sourcePlugins is null && BundledDirectory() is { } bundled
            && !string.Equals(Path.GetFullPath(bundled), Path.GetFullPath(userDirectory), StringComparison.OrdinalIgnoreCase) ? bundled : null;

        List<PluginDescriptor> plugins = [];
        foreach (IBuiltInPlugin builtIn in builtIns)
        {
            plugins.Add(new PluginDescriptor { Manifest = builtIn.Manifest, Source = PluginSource.BuiltIn, Instance = builtIn });
        }

        // Installed plugins can replace a bundled one of the same id (a newer version), never a built-in one.
        Dictionary<string, PluginDescriptor> external = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string Directory, PluginSource Source)> found =
            (sourcePlugins ?? []).Select(directory => (directory, PluginSource.Bundled))
            .Concat(PluginFolders(bundledDirectory).Select(directory => (directory, PluginSource.Bundled)))
            .Concat(PluginFolders(userDirectory)
                // In a checkout the repository's own plugins sit in the same folder as installed ones.
                .Where(directory => sourcePlugins is null || !sourcePlugins.Any(source =>
                    string.Equals(Path.GetFullPath(source), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)))
                .Select(directory => (directory, PluginSource.Installed)));
        foreach ((string directory, PluginSource source) in found)
        {
            PluginDescriptor descriptor = Discover(directory, source);
            if (plugins.Any(plugin => string.Equals(plugin.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)))
            {
                descriptor.Status = PluginStatus.Failed;
                descriptor.Problem = $"'{descriptor.Id}' is the id of a built-in plugin.";
                plugins.Add(descriptor);
                continue;
            }

            external[descriptor.Id] = descriptor;
        }

        plugins.AddRange(external.Values.OrderBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase));

        foreach (PluginDescriptor plugin in plugins.Where(plugin => plugin.Problem is null))
        {
            bool? chosen = configuration.GetSection(StatesSection).GetValue<bool?>(plugin.Id);
            plugin.Enabled = plugin.Manifest.Required || (chosen ?? plugin.Manifest.EnabledByDefault);

            // Switched-off plugins are loaded too: their switch is checked while running, so turning one on needs no restart.
            plugin.Status = Version.TryParse(plugin.Manifest.ApiVersion, out Version? api) && PluginApi.IsCompatible(api)
                ? PluginStatus.Active
                : PluginStatus.Incompatible;
            if (plugin.Status == PluginStatus.Incompatible)
            {
                plugin.Problem = $"Built for plugin API {plugin.Manifest.ApiVersion}; this version of the scheduler provides {PluginApi.Version}.";
            }
        }

        ResolveDependencies(plugins);

        // Dependencies first, so each plugin's load context can hand out its dependencies' assemblies.
        foreach (PluginDescriptor plugin in Ordered(plugins).Where(plugin => plugin.IsLoaded && plugin.Source != PluginSource.BuiltIn).ToList())
        {
            PluginLoadContext[] dependencies = [.. plugin.Manifest.Dependencies
                .Select(dependency => plugins.FirstOrDefault(other => string.Equals(other.Id, dependency.Id, StringComparison.OrdinalIgnoreCase))?.LoadContext)
                .OfType<PluginLoadContext>()];
            LoadAssembly(plugin, dependencies);
        }

        // Loading can fail, which can strand plugins that depend on it.
        ResolveDependencies(plugins);

        return new PluginRegistry(plugins, userDirectory, bundledDirectory);
    }

    /// <summary>
    /// Lets each loaded plugin register its services, dependencies first, whether it's switched on or not; what it
    /// adds is then gated by its switch while running (<see cref="PluginServiceGate"/>). A plugin that throws is marked
    /// failed and the collection is rolled back to before it ran, so a broken plugin can't leave half its services behind.
    /// </summary>
    /// <remarks>
    /// Plugins register straight into the host's collection (not a scratch one copied across) so that
    /// <c>TryAdd</c> calls, including those inside <c>AddHttpClient</c> and <c>AddOptions</c>, see what's
    /// already there instead of adding the framework's services a second time.
    /// </remarks>
    public static void ConfigureServices(PluginRegistry registry, IServiceCollection services, string dataDirectory, IConfiguration configuration)
    {
        foreach (PluginDescriptor plugin in Ordered(registry.Plugins))
        {
            if (!plugin.IsLoaded || plugin.Instance is null)
            {
                continue;
            }

            // A dependency that failed while registering takes its dependents down with it.
            if (plugin.Manifest.Dependencies.FirstOrDefault(dependency => !dependency.Optional && !registry.IsLoaded(dependency.Id)) is { } broken)
            {
                plugin.Status = PluginStatus.MissingDependency;
                plugin.Problem = $"Needs the '{broken.Id}' plugin, which failed to start.";
                continue;
            }

            ServiceDescriptor[] before = [.. services];
            try
            {
                plugin.Instance.ConfigureServices(services, new PluginContext(plugin, dataDirectory, configuration, registry.IsLoaded));
            }
            catch (Exception ex)
            {
                services.Clear();
                foreach (ServiceDescriptor service in before)
                {
                    services.Add(service);
                }

                plugin.Status = PluginStatus.Failed;
                plugin.Problem = $"Failed while starting: {ex.GetBaseException().Message}";
                continue;
            }

            PluginServiceGate.Claim(registry, plugin.Id, services, before);
        }
    }

    /// <summary>
    /// A schedule editor section for each active plugin that declared fields for its YAML sections in its
    /// manifest, unless it registered its own component for that place.
    /// </summary>
    public static void AddGeneratedEditorSections(PluginRegistry registry, IServiceCollection services)
    {
        List<PluginEditing.PluginEditorSection> own = [.. services
            .Where(service => service.ServiceType == typeof(PluginEditing.PluginEditorSection) && service.ImplementationInstance is not null)
            .Select(service => (PluginEditing.PluginEditorSection)service.ImplementationInstance!)];

        foreach (PluginDescriptor plugin in registry.Plugins.Where(plugin => plugin.IsLoaded))
        {
            foreach ((PluginEditing.EditorSurface surface, List<PluginSetting> fields) in new[]
                     {
                         (PluginEditing.EditorSurface.Channel, plugin.Manifest.ChannelSettings),
                         (PluginEditing.EditorSurface.Block, plugin.Manifest.BlockSettings),
                         (PluginEditing.EditorSurface.ScheduleSettings, plugin.Manifest.ScheduleSettings)
                     })
            {
                if (fields.Count == 0 || own.Any(section => section.Surface == surface && string.Equals(section.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var section = ServiceDescriptor.Singleton(new PluginEditing.PluginEditorSection
                {
                    PluginId = plugin.Id,
                    Title = plugin.Manifest.Name,
                    Surface = surface,
                    Component = typeof(Components.Shared.PluginSectionForm),
                    Icon = "puzzle"
                });
                services.Add(section);
                registry.ServiceOwners[section] = plugin.Id;
            }
        }
    }

    /// <summary>Active plugins, each after the plugins it depends on.</summary>
    public static IEnumerable<PluginDescriptor> Ordered(IReadOnlyList<PluginDescriptor> plugins)
    {
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        List<PluginDescriptor> ordered = [];
        List<PluginDescriptor> remaining = [.. plugins];
        while (remaining.Count > 0)
        {
            PluginDescriptor? next = remaining.FirstOrDefault(plugin => plugin.Manifest.Dependencies
                .All(dependency => placed.Contains(dependency.Id) || plugins.All(other => !string.Equals(other.Id, dependency.Id, StringComparison.OrdinalIgnoreCase) || !other.IsLoaded)));

            // A cycle is caught by ResolveDependencies; fall back to declared order rather than looping.
            next ??= remaining[0];
            remaining.Remove(next);
            placed.Add(next.Id);
            ordered.Add(next);
        }

        return ordered;
    }

    private static PluginDescriptor Discover(string directory, PluginSource source)
    {
        string folderName = Path.GetFileName(directory);
        string manifestPath = Path.Combine(directory, ManifestFileName);
        PluginManifest manifest;
        string? problem = null;
        try
        {
            manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), ManifestJson) ?? new PluginManifest()
                : new PluginManifest();
            if (!File.Exists(manifestPath))
            {
                problem = $"No {ManifestFileName} in {directory}.";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            manifest = new PluginManifest();
            problem = $"{ManifestFileName} couldn't be read: {ex.Message}";
        }

        Sanitize(manifest);

        // Built-in-only flags can't be claimed by a plugin.
        manifest.Required = false;
        manifest.SettingsLink = null;
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            manifest.Id = folderName.ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            manifest.Name = manifest.Id;
        }

        problem ??= Validate(manifest);
        return new PluginDescriptor
        {
            Manifest = manifest,
            Source = source,
            Directory = directory,
            Status = problem is null ? PluginStatus.Disabled : PluginStatus.Failed,
            Problem = problem
        };
    }

    /// <summary>
    /// JSON <c>null</c> overrides the defaults of non-nullable members, so a hand-written manifest with
    /// <c>"pages": null</c> would otherwise surface as a crash far from its cause.
    /// </summary>
    internal static PluginManifest Sanitize(PluginManifest manifest)
    {
        manifest.Id ??= string.Empty;
        manifest.Name ??= string.Empty;
        manifest.Version = string.IsNullOrWhiteSpace(manifest.Version) ? "1.0.0" : manifest.Version;
        manifest.ApiVersion ??= string.Empty;
        manifest.Dependencies = [.. (manifest.Dependencies ?? []).Where(dependency => dependency is { Id.Length: > 0 })];
        manifest.Settings = Fields(manifest.Settings);
        manifest.ChannelSettings = Fields(manifest.ChannelSettings);
        manifest.BlockSettings = Fields(manifest.BlockSettings);
        manifest.ScheduleSettings = Fields(manifest.ScheduleSettings);

        manifest.Pages = [.. (manifest.Pages ?? []).Where(page => page is { Path.Length: > 0 })];
        foreach (PluginPage page in manifest.Pages)
        {
            page.Title ??= page.Path;
        }

        return manifest;
    }

    private static List<PluginSetting> Fields(List<PluginSetting>? fields)
    {
        List<PluginSetting> kept = [.. (fields ?? []).Where(setting => setting is { Key.Length: > 0 })];
        foreach (PluginSetting setting in kept)
        {
            setting.Label ??= setting.Key;
            setting.Options ??= [];
        }

        return kept;
    }

    /// <summary>What's wrong with a manifest, or null. Shared with the installer.</summary>
    internal static string? Validate(PluginManifest manifest)
    {
        if (!IdPattern().IsMatch(manifest.Id))
        {
            return $"The id '{manifest.Id}' must be 2-64 lowercase letters, digits, dots or dashes.";
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || Path.IsPathRooted(manifest.EntryAssembly) || manifest.EntryAssembly.Contains(".."))
        {
            return "The manifest needs an entryAssembly: the plugin's .dll, relative to its folder.";
        }

        if (!Version.TryParse(manifest.ApiVersion, out _))
        {
            return $"apiVersion '{manifest.ApiVersion}' isn't a version such as 1.0.";
        }

        foreach (PluginPage page in manifest.Pages)
        {
            if (!page.Path.StartsWith($"/plugins/{manifest.Id}", StringComparison.OrdinalIgnoreCase))
            {
                return $"Page '{page.Path}' must live under /plugins/{manifest.Id}.";
            }
        }

        return null;
    }

    private static void LoadAssembly(PluginDescriptor plugin, IReadOnlyList<PluginLoadContext> dependencies)
    {
        try
        {
            string entry = Path.GetFullPath(Path.Combine(plugin.Directory!, plugin.Manifest.EntryAssembly!));
            if (!entry.StartsWith(Path.GetFullPath(plugin.Directory!), StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            {
                throw new FileNotFoundException($"{plugin.Manifest.EntryAssembly} isn't in the plugin's folder.");
            }

            var context = new PluginLoadContext(entry, dependencies);
            Assembly assembly = context.LoadEntry();
            plugin.LoadContext = context;
            Type[] candidates = plugin.Manifest.EntryType is { Length: > 0 } name
                ? [assembly.GetType(name, throwOnError: true)!]
                : [.. assembly.GetExportedTypes().Where(type => typeof(IPlugin).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false })];
            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(candidates.Length == 0
                    ? "The assembly has no public IPlugin class."
                    : "The assembly has several IPlugin classes; set entryType in the manifest.");
            }

            plugin.Instance = (IPlugin)Activator.CreateInstance(candidates[0])!;
            plugin.Assembly = assembly;
        }
        catch (Exception ex)
        {
            plugin.Status = PluginStatus.Failed;
            plugin.Problem = $"Couldn't load: {ex.GetBaseException().Message}";
        }
    }

    /// <summary>
    /// Stops loading plugins whose required dependencies are missing, broken, too old or circular. (One that's only
    /// switched off doesn't count: the plugin then waits for it while running, see <see cref="PluginRegistry"/>.)
    /// </summary>
    private static void ResolveDependencies(List<PluginDescriptor> plugins)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (PluginDescriptor plugin in plugins.Where(plugin => plugin.IsLoaded))
            {
                foreach (PluginDependency dependency in plugin.Manifest.Dependencies.Where(dependency => !dependency.Optional))
                {
                    PluginDescriptor? target = plugins.FirstOrDefault(other => string.Equals(other.Id, dependency.Id, StringComparison.OrdinalIgnoreCase));
                    string? problem = target is null ? $"Needs the '{dependency.Id}' plugin, which isn't installed."
                        : !target.IsLoaded ? $"Needs the '{target.Manifest.Name}' plugin, which isn't working."
                        : dependency.MinVersion is { } minimum && Older(target.Manifest.Version, minimum) ? $"Needs {target.Manifest.Name} {minimum} or newer (installed: {target.Manifest.Version})."
                        : null;
                    if (problem is not null)
                    {
                        plugin.Status = PluginStatus.MissingDependency;
                        plugin.Problem = problem;
                        changed = true;
                        break;
                    }
                }
            }
        }

        // Whatever is still active but can't be ordered is part of a cycle.
        HashSet<string> settled = new(StringComparer.OrdinalIgnoreCase);
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (PluginDescriptor plugin in plugins.Where(plugin => plugin.IsLoaded && !settled.Contains(plugin.Id)))
            {
                // Only required dependencies can form a loop; optional ones just order loading when they can.
                if (plugin.Manifest.Dependencies.All(dependency => dependency.Optional || settled.Contains(dependency.Id)
                        || !plugins.Any(other => other.IsLoaded && string.Equals(other.Id, dependency.Id, StringComparison.OrdinalIgnoreCase))))
                {
                    settled.Add(plugin.Id);
                    progress = true;
                }
            }
        }

        foreach (PluginDescriptor plugin in plugins.Where(plugin => plugin.IsLoaded && !settled.Contains(plugin.Id)))
        {
            plugin.Status = PluginStatus.Failed;
            plugin.Problem = "Its dependencies form a loop.";
        }
    }

    private static bool Older(string version, string minimum) =>
        Version.TryParse(version.Split('-', '+')[0], out Version? have) && Version.TryParse(minimum.Split('-', '+')[0], out Version? want) && have < want;

    /// <summary>Installs and removals staged while running; applied before anything is loaded.</summary>
    private static void ApplyPendingChanges(string userDirectory)
    {
        string remove = Path.Combine(userDirectory, RemoveFolder);
        if (Directory.Exists(remove))
        {
            foreach (string marker in Directory.EnumerateFiles(remove))
            {
                // Keep the marker if the folder couldn't go yet, so it's retried next start.
                if (TryDelete(Path.Combine(userDirectory, Path.GetFileName(marker))))
                {
                    File.Delete(marker);
                }
            }
        }

        string pending = Path.Combine(userDirectory, PendingFolder);
        if (Directory.Exists(pending))
        {
            foreach (string staged in Directory.EnumerateDirectories(pending))
            {
                string id = Path.GetFileName(staged);

                // The old version may live in a folder named differently from its id (copied in by hand).
                IEnumerable<string> previous = Directory.EnumerateDirectories(userDirectory)
                    .Where(folder => !Path.GetFileName(folder).StartsWith('.')
                                     && (string.Equals(Path.GetFileName(folder), id, StringComparison.OrdinalIgnoreCase) || ManifestId(folder) == id))
                    .ToList();
                if (!previous.All(TryDelete))
                {
                    continue;
                }

                try
                {
                    Directory.Move(staged, Path.Combine(userDirectory, id));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still locked (a native library in use); try again on the next start.
                }
            }
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ManifestId(string folder)
    {
        try
        {
            string path = Path.Combine(folder, ManifestFileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), ManifestJson)?.Id : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
