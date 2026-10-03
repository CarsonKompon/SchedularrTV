using System.Diagnostics;

namespace SchedularrTV.Service.Hosting;

/// <summary>
/// Finds ffmpeg, ffprobe and similar programs. Apps started from a desktop (Finder, the Start menu, a
/// login item) don't get the shell's PATH, so the usual install folders are searched as well.
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// A configured absolute path is used as-is; a bare name such as <c>ffmpeg</c> is searched for and
    /// left unchanged when nothing is found, so the error names what was configured.
    /// </summary>
    public static string Resolve(string? configured, string name, string? dataDirectory = null)
    {
        string value = string.IsNullOrWhiteSpace(configured) ? name : configured.Trim().Trim('"');
        if (Path.IsPathRooted(value))
        {
            return value;
        }

        if (value.Contains('/') || value.Contains('\\'))
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
        }

        return Find(value, dataDirectory) ?? value;
    }

    public static string? Find(string name, string? dataDirectory = null)
    {
        string file = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        return Candidates(dataDirectory).Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);
    }

    /// <summary>First line of <c>-version</c>, or null when the program can't be run.</summary>
    public static async Task<string?> VersionAsync(string path, CancellationToken cancellationToken = default)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(path)
            {
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            string first = output.Split('\n', 2)[0].Trim();
            return process.ExitCode == 0 && first.Length > 0 ? first : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static IEnumerable<string> Candidates(string? dataDirectory)
    {
        string app = AppContext.BaseDirectory;
        yield return app;
        yield return Path.Combine(app, "ffmpeg");
        yield return Path.Combine(app, "tools");

        // The server sits in a "server" folder beneath the tray, so tools dropped beside the tray count too.
        if (Directory.GetParent(app.TrimEnd(Path.DirectorySeparatorChar))?.FullName is { } parent)
        {
            yield return parent;
            yield return Path.Combine(parent, "ffmpeg");
            yield return Path.Combine(parent, "tools");
        }

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            yield return Path.Combine(dataDirectory, "tools");
        }

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return folder.Trim('"');
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
            yield return Path.Combine(home, "scoop", "shims");
            yield return @"C:\ProgramData\chocolatey\bin";
            yield return @"C:\ffmpeg\bin";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        }
        else
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/usr/bin";
            yield return "/snap/bin";
            yield return "/opt/local/bin";
            yield return Path.Combine(home, ".local", "bin");
        }
    }
}
