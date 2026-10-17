using System.Diagnostics;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

/// <summary>
/// Starts installing an update the server asked for: copies this tray program (without the server) to a temporary
/// folder and starts that copy as the installer, so nothing in the app's own folder is in use while it's replaced. The
/// tray quits straight afterwards; the installer waits for it to be gone.
/// </summary>
internal static class UpdateLauncher
{
    /// <summary>Temporary installer copies are named after this, and left ones cleared up at the next start.</summary>
    public const string TempPrefix = "SchedularrTV-updater-";

    // Not copied: the server (the installer doesn't need it), and anything an older install kept beside the tray.
    private static readonly string[] Skipped = ["server", "plugins", "config", "state", "logs", "etv"];

    /// <summary>Starts the installer for the request at <paramref name="requestPath"/>. Null when it's started; otherwise why not.</summary>
    public static string? Launch(string requestPath, string dataDirectory, IReadOnlyList<string> trayArguments)
    {
        string? trayPath = Environment.ProcessPath;
        if (trayPath is null || UpdateRequest.Load(requestPath) is not { } request)
        {
            return "The update request couldn't be read.";
        }

        string appRoot = UpdateRequest.AppRootFor(trayPath);
        string copy = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            CopyFolder(AppContext.BaseDirectory, copy);

            // What the installer works from, taken so this tray (or the next one) doesn't start it again.
            request.RelaunchArguments = [.. trayArguments];
            string taken = Path.Combine(Path.GetDirectoryName(requestPath)!, "installing.json");
            request.Save(taken);
            File.Delete(requestPath);

            var info = new ProcessStartInfo(Path.Combine(copy, Path.GetFileName(trayPath)))
            {
                UseShellExecute = false,
                WorkingDirectory = copy
            };
            foreach (string argument in (string[])[UpdateRequest.ApplyArgument, taken, UpdateRequest.WaitForArgument,
                         Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         UpdateRequest.AppRootArgument, appRoot, AppPaths.DataDirectoryArgument, dataDirectory])
            {
                info.ArgumentList.Add(argument);
            }

            using Process? started = Process.Start(info);
            return started is null ? "The installer couldn't be started." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"The installer couldn't be started: {ex.Message}";
        }
    }

    /// <summary>Removes installer copies left in the temporary folder by earlier updates (the running one can't remove itself).</summary>
    public static void ClearOldCopies()
    {
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(Path.GetTempPath(), TempPrefix + "*"))
            {
                if (string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use (an installer finishing up); next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string folder in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(folder);
            if (Skipped.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyFolder(folder, Path.Combine(target, name));
        }
    }
}
