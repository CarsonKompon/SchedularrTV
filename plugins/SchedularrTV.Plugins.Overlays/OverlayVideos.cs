using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SchedularrTV.Service.Hosting;

namespace SchedularrTV.Overlays;

/// <summary>What the editor needs to know of a video: how long it runs and its size.</summary>
/// <param name="FrameRate">Frames a second; null when ffprobe doesn't say.</param>
public sealed record OverlayVideoInfo(TimeSpan? Duration, int Width, int Height, double? FrameRate = null)
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
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate:format=duration", "-of", "default=nw=1", path],
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

    /// <summary>ffprobe's <c>width=</c>, <c>height=</c>, <c>r_frame_rate=</c> (like <c>30000/1001</c>) and <c>duration=</c> lines.</summary>
    public static OverlayVideoInfo? Parse(string output)
    {
        int width = 0, height = 0;
        TimeSpan? duration = null;
        double? frameRate = null;
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
                case "r_frame_rate":
                    frameRate = Rate(value);
                    break;
            }
        }

        return width <= 0 && height <= 0 && duration is null ? null : new OverlayVideoInfo(duration, width, height, frameRate);
    }

    /// <summary>A rate as ffprobe writes it (<c>30000/1001</c>, <c>25/1</c>, <c>30</c>), rounded to 3 places; null for none or nonsense.</summary>
    public static double? Rate(string value)
    {
        string[] parts = value.Trim().Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double top))
        {
            return null;
        }

        double bottom = 1;
        if (parts.Length > 1 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bottom))
        {
            return null;
        }

        double rate = bottom == 0 ? 0 : top / bottom;
        return rate is >= 1 and <= 240 ? Math.Round(rate, 3) : null;
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

    // ---- Frames, for scrubbing ----

    // One extraction of a video's frames at a time each, however often the editor asks.
    private readonly ConcurrentDictionary<string, Task> _preparing = new(StringComparer.Ordinal);

    // Frames are taken at the timeline's rate (the overlay's), so each rate has its own.
    private string FramesFolder(string path, double fps) => Path.Combine(folder, "frames", $"{Key(path)}-{OverlayAnimation.FpsText(fps)}");

    /// <summary>
    /// The frame <paramref name="seconds"/> into the video at <paramref name="fps"/> (the timeline's frame rate), as a
    /// PNG keeping its transparency: one of those taken in the background by <see cref="PrepareFramesAsync"/> when it's
    /// there, otherwise taken now on its own. Past the end it's the last frame. Null when ffmpeg can't take one.
    /// </summary>
    public async Task<string?> FrameAsync(string path, double seconds, double fps, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        double Fps = Math.Clamp(fps, 1, 240);
        OverlayVideoInfo? info = await InfoAsync(path, cancellationToken).ConfigureAwait(false);
        double last = info?.Duration is { } length ? Math.Max(0, length.TotalSeconds - 1.0 / Fps) : double.MaxValue;
        int index = (int)Math.Round(Math.Clamp(seconds, 0, last) * Fps);
        string frames = FramesFolder(path, Fps);

        // ffmpeg numbers the frames it writes from 1.
        string prepared = Path.Combine(frames, $"{index + 1:00000}.png");
        if (File.Exists(prepared))
        {
            return prepared;
        }

        string single = Path.Combine(frames, $"at-{index}.png");
        if (File.Exists(single))
        {
            return single;
        }

        Directory.CreateDirectory(frames);
        string temp = Path.Combine(frames, $"at-{index}.{Guid.NewGuid():N}.png");
        List<string> arguments = ["-v", "error", "-y", "-ss", (index / (double)Fps).ToString("0.####", CultureInfo.InvariantCulture)];
        arguments.AddRange(Decoder(path));
        arguments.AddRange(["-i", path, "-frames:v", "1", "-pix_fmt", "rgba", "-vf", "scale='min(1280,iw)':-2", temp]);
        try
        {
            (int code, _) = await RunAsync(Ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
            if (code != 0 || !File.Exists(temp))
            {
                TryDelete(temp);
                return null;
            }

            File.Move(temp, single, overwrite: true);
            return single;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            TryDelete(temp);
            return null;
        }
    }

    /// <summary>
    /// Takes every frame of the video's first <paramref name="seconds"/> in one go, in the background, so scrubbing
    /// over it is quick once done. Done once a video (and again for a longer stretch).
    /// </summary>
    public Task PrepareFramesAsync(string path, double seconds, double fps)
    {
        if (!File.Exists(path))
        {
            return Task.CompletedTask;
        }

        double wanted = Math.Clamp(Math.Ceiling(seconds), 1, 300);
        double rate = Math.Clamp(fps, 1, 240);
        return _preparing.GetOrAdd($"{Key(path)}|{wanted}|{OverlayAnimation.FpsText(rate)}", _ => Task.Run(() => ExtractAsync(path, wanted, rate)));
    }

    private async Task ExtractAsync(string path, double seconds, double Fps)
    {
        string frames = FramesFolder(path, Fps);
        string done = Path.Combine(frames, $"done-{seconds.ToString("0", CultureInfo.InvariantCulture)}");
        if (File.Exists(done))
        {
            return;
        }

        string temp = Path.Combine(frames, $"extract-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            List<string> arguments = ["-v", "error", "-y"];
            arguments.AddRange(Decoder(path));
            arguments.AddRange(["-i", path, "-t", seconds.ToString("0", CultureInfo.InvariantCulture),
                "-vf", $"fps={OverlayAnimation.FpsText(Fps)},scale='min(1280,iw)':-2", "-pix_fmt", "rgba", Path.Combine(temp, "%05d.png")]);
            (int code, _) = await RunAsync(Ffmpeg, arguments, CancellationToken.None, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            if (code != 0)
            {
                return;
            }

            foreach (string frame in Directory.EnumerateFiles(temp, "*.png"))
            {
                File.Move(frame, Path.Combine(frames, Path.GetFileName(frame)), overwrite: true);
            }

            await File.WriteAllTextAsync(done, string.Empty).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                       or UnauthorizedAccessException or OperationCanceledException)
        {
            // Frames are then taken one at a time as they're asked for.
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Removes videos' frames the editor hasn't taken for <paramref name="age"/>.</summary>
    public void PruneFrames(TimeSpan age)
    {
        string root = Path.Combine(folder, "frames");
        if (!Directory.Exists(root))
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - age;
        foreach (string video in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(video) < cutoff)
                {
                    Directory.Delete(video, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ffmpeg's own VP9 decoder drops the alpha channel; libvpx keeps it.
    private static string[] Decoder(string path) =>
        Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? ["-c:v", "libvpx-vp9"] : [];

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

    private static async Task<(int Code, string Output)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
        SchedularrTV.Plugins.Performance.Work.Track(process, "Overlays", $"Preparing overlay video ({Path.GetFileNameWithoutExtension(program)})");
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
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
