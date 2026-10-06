using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Service.Hosting;

namespace SchedularrTV.Overlays;

/// <summary>What the editor needs to know of a video: how long it runs and its size.</summary>
public sealed record OverlayVideoInfo(TimeSpan? Duration, int Width, int Height)
{
    /// <summary>Height ÷ width; null without a size.</summary>
    public double? Aspect => Width > 0 && Height > 0 ? Height / (double)Width : null;
}

/// <summary>
/// For the editor: a design's videos measured (with ffprobe) and a still taken from each (with ffmpeg), from part-way
/// through, where an animated design is usually fully in. Both are kept, keyed by the file and when it last changed.
/// </summary>
public sealed class OverlayVideos(Func<string> ffmpeg, string folder)
{
    private readonly ConcurrentDictionary<string, OverlayVideoInfo?> _info = new(StringComparer.Ordinal);

    public string Folder => folder;

    private string Ffmpeg => ffmpeg();

    private string Ffprobe
    {
        get
        {
            string tool = Ffmpeg;
            return Path.IsPathRooted(tool)
                ? Path.Combine(Path.GetDirectoryName(tool)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")
                : "ffprobe";
        }
    }

    /// <summary>Its length and size; null when ffprobe can't say (or isn't there).</summary>
    public async Task<OverlayVideoInfo?> InfoAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string key = Key(path);
        if (_info.TryGetValue(key, out OverlayVideoInfo? known))
        {
            return known;
        }

        try
        {
            (int code, string output) = await RunAsync(Ffprobe,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "default=nw=1", path],
                cancellationToken).ConfigureAwait(false);
            OverlayVideoInfo? info = code == 0 ? Parse(output) : null;
            _info[key] = info;
            return info;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>ffprobe's <c>width=</c>, <c>height=</c> and <c>duration=</c> lines.</summary>
    public static OverlayVideoInfo? Parse(string output)
    {
        int width = 0, height = 0;
        TimeSpan? duration = null;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string name = line[..equals], value = line[(equals + 1)..];
            switch (name)
            {
                case "width":
                    _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                    break;
                case "height":
                    _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                    break;
                case "duration" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0:
                    duration = TimeSpan.FromSeconds(seconds);
                    break;
            }
        }

        return width <= 0 && height <= 0 && duration is null ? null : new OverlayVideoInfo(duration, width, height);
    }

    /// <summary>A PNG still (keeping its transparency) from part-way through; null when ffmpeg can't make one.</summary>
    public async Task<string?> PosterAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string poster = Path.Combine(folder, Key(path) + ".png");
        if (File.Exists(poster))
        {
            return poster;
        }

        Directory.CreateDirectory(folder);
        OverlayVideoInfo? info = await InfoAsync(path, cancellationToken).ConfigureAwait(false);
        double at = info?.Duration is { } length ? length.TotalSeconds / 2 : 0;
        string temp = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(poster)}.{Guid.NewGuid():N}.png");

        List<string> arguments = ["-v", "error", "-y", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture)];

        // ffmpeg's own VP9 decoder drops the alpha channel; libvpx keeps it.
        if (Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-c:v", "libvpx-vp9"]);
        }

        arguments.AddRange(["-i", path, "-frames:v", "1", "-pix_fmt", "rgba", "-vf", "scale='min(1280,iw)':-2", temp]);

        try
        {
            (int code, _) = await RunAsync(Ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
            if (code != 0 || !File.Exists(temp))
            {
                TryDelete(temp);
                return null;
            }

            File.Move(temp, poster, overwrite: true);
            return poster;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            TryDelete(temp);
            return null;
        }
    }

    private static string Key(string path)
    {
        long stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Path.GetFullPath(path)}|{stamp}"));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
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

            throw;
        }

        _ = await errors.ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false));
    }

    /// <summary>The ffmpeg the scheduler's other tools use: the Generated promos setting, else one found nearby or on the PATH.</summary>
    public static string ResolveFfmpeg(IPluginContext context) =>
        ToolLocator.Resolve(context.AppConfiguration["GeneratedPromos:FfmpegPath"], "ffmpeg", context.AppDataDirectory);
}
