using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using SchedularrTV.Hosting;
using SchedularrTV.Service.Hosting;

namespace SchedularrTV.Service.Plugins;

public sealed record PluginInstallResult(bool Ok, string Message, PluginManifest? Manifest = null);

/// <summary>
/// Installs, removes, switches and configures plugins. Switching a plugin on or off and changing its settings apply
/// at once; installing, updating and removing are staged for the next start, because a loaded plugin's code can't be
/// swapped out of a running server.
/// </summary>
/// <remarks>Installing runs arbitrary code on the next start, so the UI only offers it to a browser on this machine.</remarks>
public sealed class PluginManager(PluginRegistry registry, AppRuntime runtime, ILogger<PluginManager> logger, ScheduleRebuildService? rebuilds = null)
{
    public const long MaxPackageBytes = 256L * 1024 * 1024;

    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    public PluginRegistry Registry => registry;

    /// <summary>
    /// Unpacks a plugin zip into the pending folder; it's moved into place and loaded on the next start. A zip of
    /// several plugin folders (such as a release's all-plugins zip) installs each of them.
    /// </summary>
    public async Task<PluginInstallResult> InstallAsync(Stream package, CancellationToken cancellationToken)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"plugin-{Guid.NewGuid():N}.zip");
        try
        {
            await using (FileStream file = File.Create(temp))
            {
                await package.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            using ZipArchive zip = ZipFile.OpenRead(temp);
            IReadOnlyList<ZipArchiveEntry> manifests = Manifests(zip);
            if (manifests.Count == 0)
            {
                return new PluginInstallResult(false, $"This zip has no {PluginLoader.ManifestFileName} at its top level or in its folders, so it isn't a plugin.");
            }

            if (manifests.Count == 1)
            {
                return await InstallOneAsync(zip, manifests[0], cancellationToken).ConfigureAwait(false);
            }

            List<PluginInstallResult> results = [];
            foreach (ZipArchiveEntry manifest in manifests)
            {
                results.Add(await InstallOneAsync(zip, manifest, cancellationToken).ConfigureAwait(false));
            }

            return new PluginInstallResult(results.All(result => result.Ok), string.Join(" ", results.Select(result => result.Message)));
        }
        catch (InvalidDataException)
        {
            return new PluginInstallResult(false, "That file isn't a valid zip.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new PluginInstallResult(false, $"Couldn't install the plugin: {ex.Message}");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// The plugins in a zip: <c>plugin.json</c> at its root (one plugin), else in each top-level folder (one plugin
    /// zipped as a folder, or a bundle of plugin folders), else in the folders of a single top-level folder.
    /// </summary>
    private static IReadOnlyList<ZipArchiveEntry> Manifests(ZipArchive zip)
    {
        List<ZipArchiveEntry> all = [.. zip.Entries.Where(entry => entry.Name.Equals(PluginLoader.ManifestFileName, StringComparison.OrdinalIgnoreCase))];
        static int Depth(ZipArchiveEntry entry) => entry.FullName.Count(c => c is '/' or '\\');

        foreach (int depth in new[] { 0, 1, 2 })
        {
            List<ZipArchiveEntry> found = [.. all.Where(entry => Depth(entry) == depth)];
            if (found.Count > 0)
            {
                return depth == 0 ? [found[0]] : found;
            }
        }

        return [];
    }

    private async Task<PluginInstallResult> InstallOneAsync(ZipArchive zip, ZipArchiveEntry manifestEntry, CancellationToken cancellationToken)
    {
        try
        {
            string prefix = manifestEntry.FullName[..^manifestEntry.Name.Length];
            PluginManifest? manifest;
            await using (Stream stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(stream, PluginLoader.ManifestJson, cancellationToken).ConfigureAwait(false);
            }

            if (manifest is null)
            {
                return new PluginInstallResult(false, $"{PluginLoader.ManifestFileName} is empty.");
            }

            PluginLoader.Sanitize(manifest);
            manifest.Required = false;
            manifest.SettingsLink = null;

            if (PluginLoader.Validate(manifest) is { } problem)
            {
                return new PluginInstallResult(false, problem, manifest);
            }

            if (!Version.TryParse(manifest.ApiVersion, out Version? api) || !PluginApi.IsCompatible(api))
            {
                return new PluginInstallResult(false, $"{manifest.Name} needs plugin API {manifest.ApiVersion}; this version of the scheduler provides {PluginApi.Version}.", manifest);
            }

            if (registry.Find(manifest.Id) is { Source: PluginSource.BuiltIn })
            {
                return new PluginInstallResult(false, $"'{manifest.Id}' is the id of a built-in plugin.", manifest);
            }

            if (!zip.Entries.Any(entry => string.Equals(entry.FullName, prefix + manifest.EntryAssembly, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(entry.FullName.Replace('\\', '/'), (prefix + manifest.EntryAssembly).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                return new PluginInstallResult(false, $"The zip doesn't contain {manifest.EntryAssembly}.", manifest);
            }

            string staging = Path.Combine(registry.UserDirectory, PluginLoader.PendingFolder, manifest.Id);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            List<ZipArchiveEntry> files = [.. zip.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.FullName.Length > prefix.Length)];

            // The upload limit applies to the compressed file; this stops a small zip unpacking to fill the disk.
            if (files.Sum(entry => entry.Length) > MaxUnpackedBytes)
            {
                return new PluginInstallResult(false, $"This plugin unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB.", manifest);
            }

            Directory.CreateDirectory(staging);
            string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            try
            {
                foreach (ZipArchiveEntry entry in files)
                {
                    // Refuse entries that would escape the plugin's folder ("zip slip").
                    string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName[prefix.Length..]));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(staging, recursive: true);
                        return new PluginInstallResult(false, $"The zip contains an unsafe path: {entry.FullName}", manifest);
                    }

                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
            catch
            {
                // Never leave a half-written plugin to be moved into place on the next start.
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }

                throw;
            }

            // A reinstall cancels a pending removal.
            string marker = Path.Combine(registry.UserDirectory, PluginLoader.RemoveFolder, manifest.Id);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            registry.MarkPending(manifest.Id);
            logger.LogInformation("Staged plugin {Plugin} {Version} for the next start.", manifest.Id, manifest.Version);
            bool update = registry.Find(manifest.Id) is not null;
            return new PluginInstallResult(true, $"{manifest.Name} {manifest.Version} {(update ? "will be updated" : "will be installed")} when the scheduler restarts.", manifest);
        }
        catch (JsonException ex)
        {
            return new PluginInstallResult(false, $"{manifestEntry.FullName} couldn't be read: {ex.Message}");
        }
    }

    /// <summary>Removes an installed plugin on the next start. Its settings and data folder are kept.</summary>
    public void Uninstall(string id)
    {
        PluginDescriptor plugin = registry.Find(id) ?? throw new InvalidOperationException($"There's no plugin '{id}'.");
        if (plugin.Source != PluginSource.Installed)
        {
            throw new InvalidOperationException($"{plugin.Manifest.Name} came with the scheduler and can't be removed; disable it instead.");
        }

        // The marker names the folder, which a hand-copied plugin may have named differently from its id.
        string removals = Path.Combine(registry.UserDirectory, PluginLoader.RemoveFolder);
        Directory.CreateDirectory(removals);
        File.WriteAllText(Path.Combine(removals, FolderName(plugin)), DateTimeOffset.UtcNow.ToString("O"));
        string staged = Path.Combine(registry.UserDirectory, PluginLoader.PendingFolder, plugin.Id);
        if (Directory.Exists(staged))
        {
            Directory.Delete(staged, recursive: true);
        }

        registry.MarkPending(plugin.Id);
    }

    public bool IsPendingRemoval(string id) =>
        registry.Find(id) is { Source: PluginSource.Installed } plugin
        && File.Exists(Path.Combine(registry.UserDirectory, PluginLoader.RemoveFolder, FolderName(plugin)));

    private static string FolderName(PluginDescriptor plugin) =>
        plugin.Directory is { } directory ? Path.GetFileName(directory) : plugin.Id;

    public bool IsPendingInstall(string id) => Directory.Exists(Path.Combine(registry.UserDirectory, PluginLoader.PendingFolder, id));

    public bool? SavedEnabled(string id) =>
        AppSettingsFile.GetBool(runtime.Settings.Load(), $"{PluginLoader.StatesSection}:{id}");

    public void SetEnabled(string id, bool enabled)
    {
        PluginDescriptor plugin = registry.Find(id) ?? throw new InvalidOperationException($"There's no plugin '{id}'.");
        if (plugin.Manifest.Required && !enabled)
        {
            throw new InvalidOperationException($"{plugin.Manifest.Name} is required and can't be disabled yet.");
        }

        JsonObject file = runtime.Settings.Load();
        AppSettingsFile.Set(file, $"{PluginLoader.StatesSection}:{id}", JsonValue.Create(enabled));
        runtime.SaveSettings(file);

        // A loaded plugin switches now; one that couldn't load (or arrived since startup) only on the next start.
        bool before = plugin.IsActive;
        if (!registry.SetEnabled(id, enabled))
        {
            registry.MarkPending(id);
            return;
        }

        logger.LogInformation("Plugin {Plugin} switched {State}.", id, enabled ? "on" : "off");

        // Its media, planner features and outputs change what the schedule is made from.
        if (before != plugin.IsActive)
        {
            rebuilds?.RequestFullRebuild();
        }
    }

    /// <summary>The values saved under <c>Plugins:&lt;id&gt;</c>, for the settings form.</summary>
    public Dictionary<string, string?> LoadSettings(string id)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        if (AppSettingsFile.Get(runtime.Settings.Load(), $"Plugins:{id}") is JsonObject section)
        {
            foreach ((string key, JsonNode? value) in section)
            {
                values[key] = value is JsonValue scalar && scalar.TryGetValue(out string? text) ? text : value?.ToJsonString();
            }
        }

        return values;
    }

    public void SaveSettings(string id, IReadOnlyDictionary<string, string?> values)
    {
        JsonObject file = runtime.Settings.Load();
        foreach ((string key, string? value) in values)
        {
            if (key.Contains(':'))
            {
                continue;
            }

            AppSettingsFile.Set(file, $"Plugins:{id}:{key}", string.IsNullOrEmpty(value) ? null : JsonValue.Create(value));
        }

        // Reloaded into the running configuration, so the plugin's options pick the values up straight away.
        runtime.SaveSettings(file);
        registry.NotifyChanged();
    }
}
