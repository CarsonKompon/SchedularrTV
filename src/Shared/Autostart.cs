using System.Diagnostics;
using System.Security;

namespace SchedularrTV.Hosting;

/// <summary>
/// Starting the app when the user signs in: the Run key on Windows, a LaunchAgent on macOS and an XDG
/// autostart entry on Linux. All are per-user, so no elevation is needed.
/// </summary>
public static class Autostart
{
    private const string Name = "SchedularrTV";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MacLabel = "com.schedularrtv.app";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>The program autostart should launch: the tray when there is one, otherwise this process.</summary>
    public static string? DefaultTarget() =>
        Environment.GetEnvironmentVariable(AppPaths.TrayPathVariable) is { Length: > 0 } tray && File.Exists(tray)
            ? tray
            : Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string { Length: > 0 };
            }

            return File.Exists(EntryPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>Points autostart at <paramref name="executable"/>, replacing any earlier entry.</summary>
    public static void Enable(string executable, IReadOnlyList<string>? arguments = null)
    {
        arguments ??= [];
        if (OperatingSystem.IsWindows())
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(Name, string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            return;
        }

        string entry = EntryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        if (OperatingSystem.IsMacOS())
        {
            string programArguments = string.Concat(new[] { executable }.Concat(arguments)
                .Select(argument => $"\n        <string>{SecurityElement.Escape(argument)}</string>"));
            File.WriteAllText(entry, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>{MacLabel}</string>
                    <key>ProgramArguments</key>
                    <array>{programArguments}
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>ProcessType</key>
                    <string>Interactive</string>
                </dict>
                </plist>
                """);
        }
        else
        {
            File.WriteAllText(entry, $"""
                [Desktop Entry]
                Type=Application
                Name=SchedularrTV
                Comment=Builds TV channel schedules
                Exec={string.Join(' ', new[] { executable }.Concat(arguments).Select(DesktopQuote))}
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
        }
    }

    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }

        string entry = EntryPath();
        if (File.Exists(entry))
        {
            File.Delete(entry);
        }
    }

    /// <summary>Opens a folder in the platform's file manager.</summary>
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        string opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(opener) { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
    }

    /// <summary>Opens a URL in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return;
        }

        Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
    }

    private static string EntryPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "LaunchAgents", $"{MacLabel}.plist");
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg, "autostart", "schedularrtv.desktop");
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static string DesktopQuote(string value) =>
        value.IndexOfAny([' ', '"', '\\', '$', '`']) < 0 ? value : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`")}\"";
}
