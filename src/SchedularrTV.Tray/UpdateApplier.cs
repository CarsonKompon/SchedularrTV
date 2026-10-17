using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using SchedularrTV.Hosting;

namespace SchedularrTV.Tray;

/// <summary>Where an update has got to, for the progress window.</summary>
/// <param name="Fraction">0 to 1 through the current step; null when it can't be told (the window shows it as busy).</param>
internal sealed record UpdateProgress(string Title, string? Detail = null, double? Fraction = null);

/// <summary>How an update ended.</summary>
/// <param name="RolledBack">It failed after something was replaced, and the previous version was put back.</param>
internal sealed record UpdateOutcome(bool Succeeded, string Message, bool RolledBack = false);

/// <summary>
/// Installs an update once the app has quit: waits for it to be gone, downloads the new app and plugins (checking each
/// against the release's checksum), unpacks them beside the app, swaps the new version in with the old one kept aside,
/// updates the plugins the same way, and starts SchedularrTV again. If anything fails once files have moved, or the new
/// version doesn't start, everything is put back as it was and the previous version is started instead.
/// </summary>
internal sealed class UpdateApplier(UpdateRequest request, string requestPath, string appRoot, string dataDirectory, int waitForProcess)
{
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);
    private static readonly HttpClient Http = CreateClient();

    private readonly string _root = Path.GetFullPath(appRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private readonly List<(string Target, string? Backup)> _replacedPlugins = [];
    private string? _appBackup;
    private bool _appReplaced;
    private StreamWriter? _log;

    /// <summary>The tray program inside the app folder: <c>SchedularrTV.exe</c>, or <c>Contents/MacOS/SchedularrTV</c> in a .app.</summary>
    private string TrayIn(string root) => _root.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(root, "Contents", "MacOS", TrayName)
        : Path.Combine(root, TrayName);

    private static string TrayName => Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name ? name : OperatingSystem.IsWindows() ? "SchedularrTV.exe" : "SchedularrTV";

    private string Parent => Path.GetDirectoryName(_root) ?? throw new InvalidOperationException($"{_root} has no parent folder.");

    public async Task<UpdateOutcome> RunAsync(IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        OpenLog();
        Log($"Installing SchedularrTV {request.Version} ({request.Channel}) into {_root}.");
        string staging = Path.Combine(Parent, $".SchedularrTV-update-{Guid.NewGuid():N}");
        try
        {
            progress.Report(new UpdateProgress("Waiting for SchedularrTV to close…"));
            await WaitForQuitAsync(cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(staging);
            Dictionary<UpdateDownload, string> files = await DownloadAllAsync(staging, progress, cancellationToken).ConfigureAwait(false);

            progress.Report(new UpdateProgress("Unpacking…"));
            string? newRoot = null;
            Dictionary<UpdateDownload, string> newPlugins = [];
            foreach ((UpdateDownload download, string file) in files)
            {
                string into = Path.Combine(staging, "unpacked", Path.GetFileNameWithoutExtension(download.Name));
                Unpack(file, into);
                if (download.Kind == UpdateDownload.AppKind)
                {
                    newRoot = FindAppRoot(into);
                }
                else
                {
                    newPlugins[download] = FindPluginFolder(into, download.PluginId);
                }
            }

            progress.Report(new UpdateProgress("Installing…", "Replacing the app"));
            if (newRoot is not null)
            {
                ReplaceApp(newRoot);
            }

            foreach ((UpdateDownload download, string folder) in newPlugins)
            {
                progress.Report(new UpdateProgress("Installing…", $"Updating {download.PluginName ?? download.PluginId}"));
                ReplacePlugin(download, folder);
            }

            progress.Report(new UpdateProgress("Starting SchedularrTV…"));
            Process? started = Start(TrayIn(_root));
            if (!await WaitUntilServingAsync(started, cancellationToken).ConfigureAwait(false))
            {
                Log("The new version didn't start; putting the previous one back.");
                Stop(started);
                StopServers();
                RollBack();
                Start(TrayIn(_root));
                return new UpdateOutcome(false,
                    $"SchedularrTV {request.Version} didn't start, so the previous version was put back and started again. Details are in {UpdateRequest.LogPathFor(dataDirectory)}.",
                    RolledBack: true);
            }

            CleanUp();
            Log("Done.");
            return new UpdateOutcome(true, $"SchedularrTV {request.Version} is installed and running.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Failed: {ex}");
            bool rolledBack = _appReplaced || _replacedPlugins.Count > 0;
            try
            {
                RollBack();
            }
            catch (Exception rollback) when (rollback is not OutOfMemoryException)
            {
                Log($"Putting the previous version back failed too: {rollback}");
                return new UpdateOutcome(false,
                    $"The update failed ({ex.GetBaseException().Message}), and so did putting the previous version back. It's kept in {_appBackup ?? "(not moved)"}; see {UpdateRequest.LogPathFor(dataDirectory)}.");
            }

            // Whatever happened, SchedularrTV is started again as it was.
            Start(TrayIn(_root));
            return new UpdateOutcome(false,
                $"The update failed: {ex.GetBaseException().Message}{(rolledBack ? " The previous version was put back." : string.Empty)}"
                + (request.ReleaseUrl is { Length: > 0 } url ? $" You can download it from {url}." : string.Empty),
                rolledBack);
        }
        finally
        {
            TryDelete(staging);
            TryDeleteFile(requestPath);
            _log?.Dispose();
        }
    }

    /// <summary>Waits for the tray, then its server, to exit; ends them if they're still there after a minute.</summary>
    private async Task WaitForQuitAsync(CancellationToken cancellationToken)
    {
        try
        {
            using Process tray = Process.GetProcessById(waitForProcess);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QuitTimeout);
            try
            {
                await tray.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log("The tray didn't close in time; ending it.");
                Stop(tray);
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }

        DateTime until = DateTime.UtcNow + QuitTimeout;
        while (ServersRunning().Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        StopServers();
    }

    /// <summary>The app's servers still running from its folder.</summary>
    private List<Process> ServersRunning()
    {
        string name = Path.GetFileNameWithoutExtension(AppPaths.ServerExecutableName);
        List<Process> running = [];
        foreach (Process process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.MainModule?.FileName is { } file && Path.GetFullPath(file).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    running.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Can't tell where it's from (another user's, say): not ours to wait for.
            }

            process.Dispose();
        }

        return running;
    }

    private void StopServers()
    {
        foreach (Process server in ServersRunning())
        {
            Log($"Ending the server (pid {server.Id}).");
            Stop(server);
            server.Dispose();
        }
    }

    private static void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task<Dictionary<UpdateDownload, string>> DownloadAllAsync(string staging, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(staging, "downloads");
        Directory.CreateDirectory(folder);
        long total = Math.Max(1, request.Downloads.Sum(download => Math.Max(0, download.Size)));
        long done = 0;
        Dictionary<UpdateDownload, string> files = [];
        foreach (UpdateDownload download in request.Downloads)
        {
            string label = download.Kind == UpdateDownload.AppKind ? $"SchedularrTV {request.Version}" : download.PluginName ?? download.PluginId ?? download.Name;
            string file = Path.Combine(folder, SafeName(download.Name));
            Log($"Downloading {download.Name}.");
            long before = done;
            await DownloadAsync(download.Url, file, read =>
            {
                done = before + read;
                progress.Report(new UpdateProgress("Downloading…", $"{label} · {Megabytes(done)} of {Megabytes(total)}", Math.Min(1, done / (double)total)));
            }, cancellationToken).ConfigureAwait(false);
            done = before + Math.Max(download.Size, new FileInfo(file).Length);

            if (download.Sha256 is { Length: > 0 } expected)
            {
                progress.Report(new UpdateProgress("Checking the download…", label));
                string actual = await HashAsync(file, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{download.Name} didn't download correctly (its checksum doesn't match the release's). Nothing was changed.");
                }
            }

            files[download] = file;
        }

        return files;
    }

    private static async Task DownloadAsync(string url, string file, Action<long> progress, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = File.Create(file);
        byte[] buffer = new byte[1 << 17];
        long read = 0;
        DateTime reported = DateTime.MinValue;
        int count;
        while ((count = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            read += count;
            if (DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(100))
            {
                reported = DateTime.UtcNow;
                progress(read);
            }
        }

        progress(read);
    }

    private static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Unpacks a .zip, or a .tar.gz with its Unix permissions (so the programs in it stay runnable).</summary>
    internal static void Unpack(string file, string into)
    {
        Directory.CreateDirectory(into);
        if (file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, into, overwriteFiles: true);
        }
        else
        {
            ZipFile.ExtractToDirectory(file, into, overwriteFiles: true);
        }
    }

    /// <summary>The unpacked app: the .app on a Mac, else the folder holding the tray program.</summary>
    internal string FindAppRoot(string unpacked)
    {
        IEnumerable<string> folders = Directory.EnumerateDirectories(unpacked)
            .Where(folder => !string.Equals(Path.GetFileName(folder), "__MACOSX", StringComparison.OrdinalIgnoreCase));
        if (_root.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return folders.FirstOrDefault(folder => folder.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(TrayIn(folder)))
                ?? throw new InvalidDataException("The download has no SchedularrTV.app in it.");
        }

        if (File.Exists(TrayIn(unpacked)))
        {
            return unpacked;
        }

        return folders.FirstOrDefault(folder => File.Exists(TrayIn(folder)))
            ?? throw new InvalidDataException($"The download has no {TrayName} in it.");
    }

    /// <summary>The unpacked plugin's folder: the one holding its plugin.json.</summary>
    internal static string FindPluginFolder(string unpacked, string? pluginId)
    {
        if (File.Exists(Path.Combine(unpacked, "plugin.json")))
        {
            return unpacked;
        }

        List<string> candidates = [.. Directory.EnumerateDirectories(unpacked).Where(folder => File.Exists(Path.Combine(folder, "plugin.json")))];
        return candidates.FirstOrDefault(folder => string.Equals(Path.GetFileName(folder), pluginId, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault()
            ?? throw new InvalidDataException($"The plugin download for {pluginId} has no plugin.json in it.");
    }

    /// <summary>
    /// Moves the current version aside and the new one into its place, then brings across everything of the old one
    /// the new one doesn't have (plugins kept in the app's folder, a schedule an old install kept beside the app…).
    /// </summary>
    private void ReplaceApp(string newRoot)
    {
        string backup = $"{_root}.previous";
        _appBackup = backup;
        TryDelete(backup);
        Retry(() => Directory.Move(_root, backup), $"move {_root} aside (is a file in it still open?)");
        _appReplaced = true;
        Directory.Move(newRoot, _root);
        CarryOver(backup, _root, _carried);
        Log($"Replaced {_root}; the previous version is in {backup}. Brought across: {(_carried.Count == 0 ? "nothing" : string.Join(", ", _carried))}.");
    }

    // What was brought across from the previous version (relative paths), to take back if the update is undone.
    private readonly List<string> _carried = [];

    private void ReplacePlugin(UpdateDownload download, string folder)
    {
        string target = Path.GetFullPath(download.TargetDirectory ?? throw new InvalidDataException($"No folder is known for the plugin {download.PluginId}."));
        string? backup = null;
        if (Directory.Exists(target))
        {
            backup = $"{target}.previous-update";
            TryDelete(backup);
            Retry(() => Directory.Move(target, backup), $"move the plugin {download.PluginId} aside");
        }

        _replacedPlugins.Add((target, backup));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MoveFolder(folder, target);
        Log($"Updated the plugin {download.PluginId} in {target}.");
    }

    /// <summary>Puts back whatever was replaced, newest first. Safe to call when nothing was.</summary>
    private void RollBack()
    {
        for (int index = _replacedPlugins.Count - 1; index >= 0; index--)
        {
            (string target, string? backup) = _replacedPlugins[index];
            TryDelete(target);
            if (backup is not null && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }
        }

        _replacedPlugins.Clear();
        if (_appReplaced && _appBackup is not null && Directory.Exists(_appBackup))
        {
            // What was brought across to the new version goes back with it (as it is now: a schedule kept there, say).
            if (Directory.Exists(_root))
            {
                foreach (string relative in _carried)
                {
                    string now = Path.Combine(_root, relative);
                    string back = Path.Combine(_appBackup, relative);
                    if (File.Exists(now) && !File.Exists(back))
                    {
                        File.Move(now, back);
                    }
                    else if (Directory.Exists(now) && !Directory.Exists(back))
                    {
                        Directory.Move(now, back);
                    }
                }

                TryDelete(_root);
            }

            Directory.Move(_appBackup, _root);
            Log($"Put the previous version back in {_root}.");
        }

        _carried.Clear();
        _appReplaced = false;
    }

    private void CleanUp()
    {
        if (_appBackup is not null)
        {
            TryDelete(_appBackup);
        }

        foreach ((_, string? backup) in _replacedPlugins)
        {
            if (backup is not null)
            {
                TryDelete(backup);
            }
        }
    }

    /// <summary>
    /// Moves into <paramref name="to"/> whatever <paramref name="from"/> has that it doesn't, folder by folder, noting
    /// each thing moved (relative to <paramref name="to"/>) in <paramref name="moved"/>.
    /// </summary>
    internal static void CarryOver(string from, string to, List<string> moved, string relative = "")
    {
        foreach (string file in Directory.EnumerateFiles(from))
        {
            string name = Path.GetFileName(file);
            string target = Path.Combine(to, name);
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                File.Move(file, target);
                moved.Add(Path.Combine(relative, name));
            }
        }

        foreach (string folder in Directory.EnumerateDirectories(from))
        {
            string name = Path.GetFileName(folder);
            string target = Path.Combine(to, name);
            if (Directory.Exists(target))
            {
                CarryOver(folder, target, moved, Path.Combine(relative, name));
            }
            else if (!File.Exists(target))
            {
                Directory.Move(folder, target);
                moved.Add(Path.Combine(relative, name));
            }
        }
    }

    /// <summary>A folder moved, or copied then removed where a move can't be made (across drives).</summary>
    private static void MoveFolder(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            CopyFolder(from, to);
            TryDelete(from);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string folder in Directory.EnumerateDirectories(from))
        {
            CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    /// <summary>Starts SchedularrTV again the way it was started (its arguments, its data folder).</summary>
    private Process? Start(string tray)
    {
        try
        {
            var info = new ProcessStartInfo(tray) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(tray)! };
            foreach (string argument in request.RelaunchArguments)
            {
                info.ArgumentList.Add(argument);
            }

            Log($"Starting {tray}.");
            return Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log($"Couldn't start {tray}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Whether the started SchedularrTV's server answers within a couple of minutes (and the tray is still running).</summary>
    private async Task<bool> WaitUntilServingAsync(Process? tray, CancellationToken cancellationToken)
    {
        if (tray is null)
        {
            return false;
        }

        int port = AppSettingsFile.Port(new AppSettingsFile(AppPaths.SettingsPath(dataDirectory)).Load());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        DateTime until = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync($"{AppPaths.BrowseUrl(port)}/api/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            if (tray.HasExited)
            {
                Log($"The new tray exited (code {tray.ExitCode}) before its server answered.");
                return false;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Tries a move a few times: antivirus and indexers hold new files open for a moment.</summary>
    private void Retry(Action action, string what)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Log($"Couldn't {what} yet ({ex.Message}); trying again.");
                Thread.Sleep(500);
            }
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string Megabytes(long bytes) => $"{bytes / 1_048_576.0:0.0} MB";

    private void OpenLog()
    {
        try
        {
            string path = UpdateRequest.LogPathFor(dataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log = null;
        }
    }

    internal void Log(string line)
    {
        string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
        try
        {
            _log?.WriteLine(stamped);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        Console.WriteLine(stamped);
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SchedularrTV-Updater/1.0");
        return http;
    }
}
