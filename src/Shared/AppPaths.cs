namespace SchedularrTV.Hosting;

/// <summary>
/// Where the app keeps its files. Shared (as a linked source file) by the server and the tray so both
/// agree on the data folder and the address without one having to ask the other.
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryVariable = "SCHEDULARRTV_DATA";
    public const string DataDirectoryArgument = "--data-dir";
    public const string SettingsFileName = "settings.json";
    public const int DefaultPort = 5252;

    /// <summary>Set by the tray on the server it launches, so the server can point autostart at the tray.</summary>
    public const string TrayPathVariable = "SCHEDULARRTV_TRAY_PATH";

    /// <summary>Shared secret the tray uses to ask its server to shut down cleanly.</summary>
    public const string ControlTokenVariable = "SCHEDULARRTV_CONTROL_TOKEN";

    /// <summary>The server exits when this process does, so a crashed tray doesn't leave it orphaned.</summary>
    public const string ParentProcessVariable = "SCHEDULARRTV_PARENT_PID";

    public const string ControlTokenHeader = "X-Control-Token";

    /// <summary>
    /// <c>--data-dir</c>, then <c>SCHEDULARRTV_DATA</c>, then the app's own folder when it already holds a
    /// schedule (running from source, or an older install), otherwise the per-user folder.
    /// </summary>
    public static string ResolveDataDirectory(IReadOnlyList<string> args, string serverRoot)
    {
        if (Argument(args, DataDirectoryArgument) is { Length: > 0 } fromArgs)
        {
            return Path.GetFullPath(fromArgs);
        }

        if (Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } fromEnvironment)
        {
            return Path.GetFullPath(fromEnvironment);
        }

        if (File.Exists(Path.Combine(serverRoot, "config", "schedule.yaml")))
        {
            return Path.GetFullPath(serverRoot);
        }

        return PerUserDirectory();
    }

    public static string PerUserDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchedularrTV");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "SchedularrTV");
        }

        // Promo renders are large, so this is data rather than config in XDG terms.
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "schedularrtv");
    }

    public static string SettingsPath(string dataDirectory) => Path.Combine(dataDirectory, SettingsFileName);

    /// <summary>A relative path made absolute against the data folder; absolute paths are left alone.</summary>
    public static string Rooted(string dataDirectory, string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(dataDirectory, path));

    /// <summary>Supports both <c>--name value</c> and <c>--name=value</c>.</summary>
    public static string? Argument(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    public static bool Flag(IReadOnlyList<string> args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    public static string ServerExecutableName => OperatingSystem.IsWindows() ? "SchedularrTV.Service.exe" : "SchedularrTV.Service";

    /// <summary>Address the server listens on.</summary>
    public static string BindUrl(int port, bool allowRemote) => $"http://{(allowRemote ? "0.0.0.0" : "localhost")}:{port}";

    /// <summary>Address a browser on this machine opens; a wildcard bind isn't something to browse to.</summary>
    public static string BrowseUrl(int port) => $"http://localhost:{port}";
}
