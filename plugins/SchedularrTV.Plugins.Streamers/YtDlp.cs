using System.Diagnostics;
using System.Runtime.InteropServices;
using SchedularrTV.Service.Hosting;

namespace SchedularrTV.Streamers;

/// <summary>
/// yt-dlp, which turns a live Twitch channel or YouTube stream into the address of its video. Found where it's installed,
/// or downloaded into the plugin's own folder from yt-dlp's GitHub releases.
/// </summary>
public sealed class YtDlp(StreamerSettings settings, ILogger<YtDlp> logger)
{
    private static readonly HttpClient Downloads = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SchedularrTV-Streamers/1.0");
        return client;
    }

    /// <summary>Where a downloaded copy goes.</summary>
    public string DownloadPath => Path.Combine(settings.DataDirectory, "tools", OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>The plugin's setting, else a downloaded copy, else one installed on this computer; null when there's none.</summary>
    public string? Executable =>
        settings.YtDlpPath is { } own ? own
        : File.Exists(DownloadPath) ? DownloadPath
        : ToolLocator.Find("yt-dlp", settings.AppDataDirectory);

    /// <summary>The file yt-dlp publishes for this computer, or null where it has none.</summary>
    public static string? Asset()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSArchitecture == Architecture.X86 ? "yt-dlp_x86.exe" : "yt-dlp.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "yt-dlp_linux",
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => null
            };
        }

        return null;
    }

    /// <summary>Downloads the latest yt-dlp into the plugin's folder.</summary>
    public async Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string asset = Asset() ?? throw new PlatformNotSupportedException("yt-dlp isn't published for this kind of computer.");
        string target = DownloadPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + ".download";
        using (HttpResponseMessage response = await Downloads.GetAsync($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}",
                   HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream file = File.Create(temp);
            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total is > 0)
                {
                    progress?.Report((double)done / total.Value);
                }
            }
        }

        File.Move(temp, target, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        progress?.Report(1);
        logger.LogInformation("Streamers: downloaded yt-dlp to {Path}.", target);
        return target;
    }

    /// <summary>yt-dlp's version, or null when it can't be run.</summary>
    public async Task<string?> VersionAsync(CancellationToken cancellationToken)
    {
        if (Executable is not { } program)
        {
            return null;
        }

        try
        {
            (int code, string output, _) = await RunAsync(program, ["--version"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return code == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The address of a live stream's video and sound (an HLS playlist), at most <see cref="StreamerSettings.MaxHeight"/> high.
    /// </summary>
    public async Task<string> ResolveAsync(string pageUrl, CancellationToken cancellationToken)
    {
        string program = Executable ?? throw new InvalidOperationException("yt-dlp isn't installed: download it on the Streamers page.");
        int height = settings.MaxHeight;
        (int code, string output, string errors) = await RunAsync(program,
            ["--no-warnings", "--no-playlist", "--no-cache-dir", "-f", $"best[height<={height}]/best", "-g", pageUrl],
            TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        string? url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        if (code != 0 || url is null)
        {
            string said = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {code}";
            throw new InvalidOperationException($"yt-dlp couldn't open the stream: {said}");
        }

        return url;
    }

    /// <summary>
    /// The address of a recording's picture, and of its sound when that's separate (YouTube keeps them apart above 360p).
    /// </summary>
    public async Task<(string Video, string? Audio)> ResolveRecordingAsync(string pageUrl, StreamPlatform platform, CancellationToken cancellationToken)
    {
        string program = Executable ?? throw new InvalidOperationException("yt-dlp isn't installed: download it on the Streamers page.");
        int height = settings.MaxHeight;
        string format = platform == StreamPlatform.YouTube
            ? $"bv*[height<={height}][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<={height}]+ba/b[height<={height}]/b"
            : $"best[height<={height}]/best";
        (int code, string output, string errors) = await RunAsync(program,
            ["--no-warnings", "--no-playlist", "--no-cache-dir", "-f", format, "-g", pageUrl],
            TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        string[] urls = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase))];
        if (code != 0 || urls.Length == 0)
        {
            string said = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {code}";
            throw new InvalidOperationException($"yt-dlp couldn't open the recording: {said}");
        }

        return (urls[0], urls.Length > 1 ? urls[1] : null);
    }

    private static async Task<(int Code, string Output, string Errors)> RunAsync(string program, IEnumerable<string> arguments, TimeSpan limit,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw new TimeoutException($"{Path.GetFileName(program)} took too long.");
        }
    }
}
