using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SchedularrTV.Iptv.IptvOrg;

/// <summary>A program to run: Node.js, and npm's script beside it.</summary>
public sealed record NodeRuntime(string Node, string NpmCli, string Version);

/// <summary>
/// What iptv-org/epg's grabber needs, set up in the plugin's own folder: Node.js (the one installed on this computer
/// when it's new enough, else a portable copy downloaded from nodejs.org) and the iptv-org/epg code with its packages.
/// The code is downloaded again (and its packages reinstalled) once a week, as listings sites change.
/// </summary>
public sealed class IptvOrgTools(IHttpClientFactory http, IptvSettings settings, ILogger<IptvOrgTools> logger)
{
    private const int MinimumNodeMajor = 20;
    private static readonly TimeSpan CodeFreshFor = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What's being set up right now, for the IPTV page; null when nothing is.</summary>
    public string? Stage { get; private set; }

    /// <summary>0 to 1 for the current download, when it's known.</summary>
    public double? Progress { get; private set; }

    /// <summary>Raised when <see cref="Stage"/> or <see cref="Progress"/> changes.</summary>
    public event Action? Changed;

    public string Root => Path.Combine(settings.DataDirectory, "iptv-org");

    public string EpgFolder => Path.Combine(Root, "epg");

    private string NodeFolder => Path.Combine(Root, "node");

    private string InstalledMarker => Path.Combine(EpgFolder, ".schedularrtv-installed");

    /// <summary>True when the grabber is ready to run (it may still be refreshed when next used).</summary>
    public bool Ready => File.Exists(InstalledMarker);

    /// <summary>Makes sure Node.js and the grabber are there and current; returns the Node.js to run it with.</summary>
    public async Task<NodeRuntime> PrepareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NodeRuntime node = await NodeAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(InstalledMarker) || DateTime.UtcNow - File.GetLastWriteTimeUtc(InstalledMarker) > CodeFreshFor)
            {
                try
                {
                    await InstallCodeAsync(node, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(InstalledMarker))
                {
                    // The week-old grabber still works better than none.
                    logger.LogWarning("IPTV: couldn't update iptv-org/epg ({Message}); keeping the copy from before.", ex.Message);
                }
            }

            return node;
        }
        finally
        {
            Report(null, null);
            _gate.Release();
        }
    }

    // --- Node.js ---------------------------------------------------------------------------------

    private async Task<NodeRuntime> NodeAsync(CancellationToken cancellationToken)
    {
        foreach (string candidate in NodeCandidates())
        {
            if (await CheckAsync(candidate, cancellationToken).ConfigureAwait(false) is { } found)
            {
                return found;
            }
        }

        await DownloadNodeAsync(cancellationToken).ConfigureAwait(false);
        foreach (string candidate in Downloaded())
        {
            if (await CheckAsync(candidate, cancellationToken).ConfigureAwait(false) is { } found)
            {
                return found;
            }
        }

        throw new InvalidOperationException("Node.js was downloaded but won't run on this computer.");
    }

    /// <summary>The plugin's setting, a copy downloaded before, then one installed on this computer.</summary>
    private IEnumerable<string> NodeCandidates()
    {
        if (settings.NodePath is { } configured)
        {
            yield return configured;
        }

        foreach (string downloaded in Downloaded())
        {
            yield return downloaded;
        }

        string file = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(folder.Trim().Trim('"'), file);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        string[] usual = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")]
            : ["/opt/homebrew/bin/node", "/usr/local/bin/node", "/usr/bin/node"];
        foreach (string path in usual.Where(File.Exists))
        {
            yield return path;
        }
    }

    private IEnumerable<string> Downloaded() =>
        Directory.Exists(NodeFolder)
            ? Directory.EnumerateDirectories(NodeFolder)
                .Select(folder => OperatingSystem.IsWindows() ? Path.Combine(folder, "node.exe") : Path.Combine(folder, "bin", "node"))
                .Where(File.Exists)
            : [];

    /// <summary>The Node.js at <paramref name="node"/>, if it runs, is new enough and has npm beside it.</summary>
    private async Task<NodeRuntime?> CheckAsync(string node, CancellationToken cancellationToken)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(node))!;
        string? npm = new[]
        {
            Path.Combine(folder, "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(folder, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js")
        }.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (npm is null)
        {
            return null;
        }

        try
        {
            ProcessResult result = await RunAsync(node, ["--version"], null, TimeSpan.FromSeconds(15), null, cancellationToken).ConfigureAwait(false);
            string version = result.Output.Trim();
            return result.Code == 0 && Major(version) >= MinimumNodeMajor ? new NodeRuntime(node, npm, version) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    public static int Major(string version) =>
        int.TryParse(version.TrimStart('v', 'V').Split('.')[0], out int major) ? major : 0;

    /// <summary>The newest long-term-support Node.js for this computer, from nodejs.org.</summary>
    private async Task DownloadNodeAsync(CancellationToken cancellationToken)
    {
        string platform = Platform() ?? throw new PlatformNotSupportedException("Node.js isn't published for this kind of computer; install it and set its path in the plugin's settings.");
        Report("Finding the latest Node.js", null);
        HttpClient client = http.CreateClient(IptvFetcher.ClientName);
        string version;
        await using (Stream index = await client.GetStreamAsync("https://nodejs.org/dist/index.json", cancellationToken).ConfigureAwait(false))
        {
            using JsonDocument json = await JsonDocument.ParseAsync(index, cancellationToken: cancellationToken).ConfigureAwait(false);
            version = LatestLts(json.RootElement, MinimumNodeMajor) ?? throw new InvalidOperationException("nodejs.org lists no suitable Node.js.");
        }

        string extension = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
        string name = $"node-{version}-{platform}";
        string archive = Path.Combine(Root, $"{name}.{extension}");
        Directory.CreateDirectory(Root);
        await DownloadAsync($"https://nodejs.org/dist/{version}/{name}.{extension}", archive, $"Downloading Node.js {version}", cancellationToken)
            .ConfigureAwait(false);

        Report($"Unpacking Node.js {version}", null);
        string staging = Path.Combine(NodeFolder, ".unpacking");
        TryDelete(staging);
        Directory.CreateDirectory(staging);
        try
        {
            if (extension == "zip")
            {
                ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            }
            else
            {
                await using FileStream file = File.OpenRead(archive);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
            }

            string unpacked = Directory.EnumerateDirectories(staging).Single();
            string target = Path.Combine(NodeFolder, name);
            TryDelete(target);
            Directory.Move(unpacked, target);
            logger.LogInformation("IPTV: downloaded Node.js {Version} for the iptv-org guide grabber.", version);
        }
        finally
        {
            TryDelete(staging);
            TryDeleteFile(archive);
        }
    }

    /// <summary>nodejs.org's name for this computer's builds (<c>win-x64</c>, <c>linux-arm64</c>, <c>darwin-arm64</c>…).</summary>
    public static string? Platform()
    {
        string? arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        string? os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : null;
        return arch is null || os is null ? null : $"{os}-{arch}";
    }

    /// <summary>The newest long-term-support version (<c>v22.11.0</c>) of at least <paramref name="minimumMajor"/> in nodejs.org's index.</summary>
    public static string? LatestLts(JsonElement index, int minimumMajor)
    {
        if (index.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The index is newest first.
        foreach (JsonElement release in index.EnumerateArray())
        {
            bool lts = release.TryGetProperty("lts", out JsonElement flag) && flag.ValueKind == JsonValueKind.String;
            if (lts && release.TryGetProperty("version", out JsonElement version) && version.GetString() is { } text && Major(text) >= minimumMajor)
            {
                return text;
            }
        }

        return null;
    }

    // --- iptv-org/epg --------------------------------------------------------------------------------

    private async Task InstallCodeAsync(NodeRuntime node, CancellationToken cancellationToken)
    {
        string archive = Path.Combine(Root, "epg-master.zip");
        await DownloadAsync("https://codeload.github.com/iptv-org/epg/zip/refs/heads/master", archive, "Downloading iptv-org/epg", cancellationToken)
            .ConfigureAwait(false);

        Report("Unpacking iptv-org/epg", null);
        string staging = Path.Combine(Root, ".epg-unpacking");
        TryDelete(staging);
        try
        {
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            string unpacked = Directory.EnumerateDirectories(staging).Single();

            Report("Installing iptv-org/epg's packages (npm install; a few minutes the first time)", null);
            ProcessResult install = await RunAsync(node.Node, [node.NpmCli, "install", "--no-audit", "--no-fund", "--loglevel=error"], unpacked,
                TimeSpan.FromMinutes(30), GrabberEnvironment(node), cancellationToken).ConfigureAwait(false);
            if (install.Code != 0)
            {
                throw new InvalidOperationException($"npm install failed: {LastLines(install.Errors.Length > 0 ? install.Errors : install.Output)}");
            }

            TryDelete(EpgFolder);
            Directory.Move(unpacked, EpgFolder);
            await File.WriteAllTextAsync(InstalledMarker, DateTime.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("IPTV: iptv-org/epg is installed.");
        }
        finally
        {
            TryDelete(staging);
            TryDeleteFile(archive);
        }
    }

    /// <summary>What the grabber is run with: Node.js on the path (scripts start more of it), and no git hooks.</summary>
    public static Dictionary<string, string> GrabberEnvironment(NodeRuntime node) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = Path.GetDirectoryName(node.Node) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        ["HUSKY"] = "0",
        ["NODE_OPTIONS"] = "--max-old-space-size=2048"
    };

    // --- Running and downloading ---------------------------------------------------------------------

    public sealed record ProcessResult(int Code, string Output, string Errors);

    /// <param name="onLine">Called with each line the program prints (either stream), as it prints it.</param>
    public static async Task<ProcessResult> RunAsync(string program, IEnumerable<string> arguments, string? workingDirectory, TimeSpan limit,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
        var output = new System.Text.StringBuilder();
        var errors = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (output) output.AppendLine(line.Data);
            onLine?.Invoke(line.Data);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (errors) errors.AppendLine(line.Data);
            onLine?.Invoke(line.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{Path.GetFileName(program)} took longer than {limit.TotalMinutes:0} minutes.");
        }

        lock (output)
        lock (errors)
        {
            return new ProcessResult(process.ExitCode, output.ToString(), errors.ToString());
        }
    }

    /// <summary>The last few lines a program printed, for an error message.</summary>
    public static string LastLines(string text, int count = 3) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));

    private async Task DownloadAsync(string url, string target, string stage, CancellationToken cancellationToken)
    {
        Report(stage, 0);
        using HttpResponseMessage response = await http.CreateClient(IptvFetcher.ClientName)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        string temp = target + ".download";
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (FileStream file = File.Create(temp))
        {
            byte[] buffer = new byte[1 << 16];
            long done = 0;
            long reported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total is > 0 && done - reported > total.Value / 50)
                {
                    reported = done;
                    Report(stage, (double)done / total.Value);
                }
            }
        }

        File.Move(temp, target, overwrite: true);
    }

    private void Report(string? stage, double? progress)
    {
        Stage = stage;
        Progress = progress;
        Changed?.Invoke();
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
}
