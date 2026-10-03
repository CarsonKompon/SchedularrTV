using System.Reflection;
using System.Runtime.Loader;

namespace SchedularrTV.Service.Plugins;

/// <summary>
/// Loads one plugin and its private dependencies in isolation. Anything the scheduler itself ships with is
/// taken from the scheduler instead, so the contract types (IPlugin, MediaItem, IServiceCollection...) are the
/// same types on both sides, and two plugins can carry different versions of the same private library.
/// </summary>
/// <remarks>
/// Managed assemblies are read from a stream rather than mapped from disk, so a plugin's files aren't locked
/// and can be replaced while the scheduler runs; the new files load on the next start.
/// </remarks>
internal sealed class PluginLoadContext(string entryPath, IReadOnlyList<PluginLoadContext> dependencies)
    : AssemblyLoadContext($"plugin:{Path.GetFileNameWithoutExtension(entryPath)}", isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryPath);

    public Assembly LoadEntry() => LoadFile(entryPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostAssembly(assemblyName))
        {
            // Defer to the default context, which already has (or can find) the scheduler's copy.
            return null;
        }

        // A plugin that depends on another sees that plugin's assemblies, so they can share types.
        foreach (PluginLoadContext dependency in dependencies)
        {
            if (dependency.Assemblies.FirstOrDefault(loaded => AssemblyName.ReferenceMatchesDefinition(assemblyName, loaded.GetName())) is { } shared)
            {
                return shared;
            }
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFile(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private Assembly LoadFile(string path)
    {
        using FileStream assembly = File.OpenRead(path);
        string symbols = Path.ChangeExtension(path, ".pdb");
        using FileStream? pdb = File.Exists(symbols) ? File.OpenRead(symbols) : null;
        return LoadFromStream(assembly, pdb);
    }

    /// <summary>
    /// Every assembly the scheduler ships with (the runtime's trusted platform assemblies: the framework plus
    /// the app's own files), by simple name. Matching by name means the scheduler's version always wins, which
    /// is what keeps shared types identical; a plugin built against a newer version of one of these gets the
    /// scheduler's copy. Decided up front rather than by trying loads, so it doesn't depend on load order.
    /// </summary>
    private static readonly HashSet<string> HostAssemblies = new(
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OfType<string>()
            .Concat(Default.Assemblies.Select(assembly => assembly.GetName().Name).OfType<string>()),
        StringComparer.OrdinalIgnoreCase);

    private static bool IsHostAssembly(AssemblyName name) => name.Name is { } simple && HostAssemblies.Contains(simple);
}
