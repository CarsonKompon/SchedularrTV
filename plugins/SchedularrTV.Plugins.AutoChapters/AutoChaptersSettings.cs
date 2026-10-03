using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.AutoChapters;

/// <summary>The plugin's settings (its gear on the Plugins page), read on each use so saved changes apply at once.</summary>
public sealed class AutoChaptersSettings(IPluginContext context)
{
    private string? Value(string key) => context.Configuration[key] is { Length: > 0 } value ? value.Trim().Trim('"') : null;

    private bool Flag(string key, bool fallback) => bool.TryParse(Value(key), out bool value) ? value : fallback;

    private double Number(string key, double fallback, double min, double max) =>
        double.TryParse(Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? Math.Clamp(value, min, max) : fallback;

    public bool UseForMidRolls => Flag("UseForMidRolls", true);

    public bool RequireSilence => Flag("RequireSilence", true);

    public bool ScanLibrary => Flag("ScanLibrary", false);

    public DetectionOptions Detection => new(
        MinBlack: TimeSpan.FromSeconds(Number("MinBlackSeconds", 0.4, 0.1, 5)),
        PixelThreshold: Number("BlackThreshold", 0.1, 0.01, 0.5),
        RequireSilence: RequireSilence);

    public int Concurrency => (int)Number("Concurrency", 1, 1, 8);

    /// <summary>The plugin's own choice, else the one App settings uses, else one beside ffprobe, else ffmpeg on the PATH.</summary>
    public string Ffmpeg
    {
        get
        {
            if (Value("FfmpegPath") is { } own)
            {
                return own;
            }

            IConfiguration app = context.AppConfiguration;
            if (app["GeneratedPromos:FfmpegPath"] is { Length: > 0 } configured && (configured.Contains('/') || configured.Contains('\\')))
            {
                return configured;
            }

            if (app["FileSystemLibrary:FfprobePath"] is { Length: > 0 } ffprobe && Path.IsPathRooted(ffprobe))
            {
                string sibling = Path.Combine(Path.GetDirectoryName(ffprobe)!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }

            return "ffmpeg";
        }
    }

    /// <summary>
    /// Where a library path is on this computer. The media server may report paths as it sees them (another machine
    /// or a container), so ErsatzTV's path mappings are tried when the path doesn't exist here.
    /// </summary>
    public string? LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        foreach (IConfigurationSection mapping in context.AppConfiguration.GetSection("ErsatzTv:PathMappings").GetChildren())
        {
            if (mapping["From"] is { Length: > 0 } from && mapping["To"] is { } to
                && path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                string mapped = to + path[from.Length..];
                mapped = mapped.Replace(OperatingSystem.IsWindows() ? '/' : '\\', Path.DirectorySeparatorChar);
                if (File.Exists(mapped))
                {
                    return mapped;
                }
            }
        }

        return null;
    }
}
