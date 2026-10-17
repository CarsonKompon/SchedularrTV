using System.Diagnostics;

namespace SchedularrTV.Service.Plugins;

/// <summary>
/// For a source checkout: every plugin in the repository's <c>plugins</c> folder is built (when it's missing or older
/// than its sources) and put in the checkout's <c>.plugins</c> folder, where it's loaded from, so running the
/// scheduler from source always has all of them, current, without installing anything.
/// </summary>
/// <remarks>
/// The plugins can't be build dependencies of the scheduler (several reference it), so they're built here instead,
/// with <c>dotnet build --no-dependencies</c> against the scheduler that's already built. Nothing is built when
/// they're up to date, so starting is only slower after a plugin (or the scheduler) has changed. Set
/// <c>SCHEDULARRTV_BUILD_PLUGINS=false</c> to load whatever was last built without building.
/// </remarks>
public static class SourcePlugins
{
    public const string SolutionFileName = "SchedularrTV.slnx";
    public const string BuildVariable = "SCHEDULARRTV_BUILD_PLUGINS";

    /// <summary>
    /// A checkout's plugins folder (ignored by git): the repository's plugins are built into it and loaded from it, and
    /// plugins installed from the Plugins page go here too, never in the per-user data folder.
    /// </summary>
    public const string InstallFolder = ".plugins";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The repository's root folder when the scheduler runs from a checkout, else null.</summary>
    public static string? Root(params string[] starts)
    {
        foreach (string start in starts)
        {
            DirectoryInfo? directory = new(start);
            for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The build output folder of every plugin in the checkout (each holding its <c>plugin.json</c>), building the
    /// ones that need it first. Progress and build errors go to the console: this runs before logging is set up.
    /// </summary>
    public static IReadOnlyList<string> Prepare(string root)
    {
        string pluginsFolder = Path.Combine(root, "plugins");
        if (!Directory.Exists(pluginsFolder))
        {
            return [];
        }

        // Built the way the running scheduler was: …/bin/<configuration>/<framework>/, or a folder inside that (the
        // tray runs the server from its own bin/<configuration>/<framework>/server/).
        string running = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        (string configuration, string framework) = BuildOf(running);

        List<Project> projects = [.. Directory.EnumerateDirectories(pluginsFolder)
            .Where(folder => File.Exists(Path.Combine(folder, PluginLoader.ManifestFileName)))
            .Select(folder => Directory.EnumerateFiles(folder, "*.csproj").FirstOrDefault())
            .OfType<string>()
            .Select(project => new Project(project, Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, framework)))];

        bool build = !string.Equals(Environment.GetEnvironmentVariable(BuildVariable), "false", StringComparison.OrdinalIgnoreCase);
        List<Project> stale = build ? [.. projects.Where(project => IsStale(project, root, running))] : [];
        if (stale.Count > 0)
        {
            Build(stale, root, configuration, running);
        }

        // Each is loaded from the checkout's .plugins/<project> folder. A Debug build puts it there itself (see
        // plugins/Directory.Build.targets); anything built otherwise, or before that, is copied over here.
        string checkout = Path.Combine(root, InstallFolder);
        List<string> folders = [];
        foreach (Project project in projects.Where(project => File.Exists(Path.Combine(project.Output, PluginLoader.ManifestFileName))))
        {
            string folder = Path.Combine(checkout, project.Name);
            try
            {
                Sync(project.Output, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"  Couldn't copy {project.Name} into {checkout}: {ex.Message}");
            }

            if (File.Exists(Path.Combine(folder, PluginLoader.ManifestFileName)))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    /// <summary>The configuration and framework of a build output folder, from the folders after its last <c>bin</c>.</summary>
    public static (string Configuration, string Framework) BuildOf(string folder)
    {
        string[] parts = folder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int bin = Array.FindLastIndex(parts, part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase));
        string framework = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        return bin >= 0 && bin + 2 < parts.Length && parts[bin + 2].StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? (parts[bin + 1], parts[bin + 2])
            : ("Debug", framework);
    }

    /// <summary>Copies files that are new or changed in the build output into the plugin's checkout folder.</summary>
    private static void Sync(string output, string folder)
    {
        foreach (string file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(folder, Path.GetRelativePath(output, file));
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(file)
                && new FileInfo(target).Length == new FileInfo(file).Length)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private sealed record Project(string Path, string Output)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

        public string Assembly => System.IO.Path.Combine(Output, Name + ".dll");

        public string Folder => System.IO.Path.GetDirectoryName(Path)!;
    }

    /// <summary>Built before, or older than its own files, the shared plugin code, or the scheduler it's built against.</summary>
    private static bool IsStale(Project project, string root, string running)
    {
        if (!File.Exists(project.Assembly) || !File.Exists(Path.Combine(project.Output, PluginLoader.ManifestFileName)))
        {
            return true;
        }

        DateTime built = File.GetLastWriteTimeUtc(project.Assembly);
        IEnumerable<string> inputs = Sources(project.Folder)
            .Concat(Sources(Path.Combine(root, "plugins", "shared")))
            .Concat([Path.Combine(root, "Directory.Build.props"), Path.Combine(root, "plugins", "Directory.Build.targets")])
            .Concat(new[] { "SchedularrTV.Service.dll", "SchedularrTV.Core.dll", "SchedularrTV.Plugins.Abstractions.dll" }
                .Select(name => Path.Combine(running, name)));
        return inputs.Any(input => File.Exists(input) && File.GetLastWriteTimeUtc(input) > built);
    }

    private static IEnumerable<string> Sources(string folder) =>
        !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(file =>
            {
                string relative = Path.GetRelativePath(folder, file);
                return !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            });

    private static void Build(IReadOnlyList<Project> stale, string root, string configuration, string running)
    {
        Console.WriteLine($"Building {stale.Count} plugin(s) from source: {string.Join(", ", stale.Select(project => project.Name.Replace("SchedularrTV.Plugins.", string.Empty)))}…");

        // The shared plugin library first, since --no-dependencies only uses what's already built.
        string shared = Path.Combine(root, "plugins", "shared");
        if (Directory.Exists(shared))
        {
            foreach (string library in Directory.EnumerateFiles(shared, "*.csproj", SearchOption.AllDirectories))
            {
                Run(library, configuration);
            }
        }

        Parallel.ForEach(stale, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            project => Run(project.Path, configuration));
    }

    private static void Run(string project, string configuration)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(project)!
        };
        foreach (string argument in (string[])["build", project, "-c", configuration, "--no-dependencies", "-nologo", "-v:q", "-clp:ErrorsOnly"])
        {
            info.ArgumentList.Add(argument);
        }

        string name = Path.GetFileNameWithoutExtension(project);
        try
        {
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("dotnet could not be started.");
            SchedularrTV.Plugins.Performance.Work.TrackAs(process, "SchedularrTV", $"Building plugin {name}");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(BuildTimeout))
            {
                process.Kill(entireProcessTree: true);
                Console.WriteLine($"  {name}: the build took too long; using what was built before, if anything.");
                return;
            }

            if (process.ExitCode != 0)
            {
                string log = $"{output.Result}\n{error.Result}".Trim();
                Console.WriteLine($"  {name} didn't build; using what was built before, if anything:\n{log[Math.Max(0, log.Length - 2000)..]}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"  {name}: couldn't run dotnet to build it ({ex.Message}).");
        }
    }
}
