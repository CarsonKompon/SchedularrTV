using System.Globalization;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Platform;

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
        RequireSilence: RequireSilence)
    {
        Threads = DecodeThreads,
        HardwareDecode = HardwareDecode,
        FastDecode = FastDecode,
        MaxSpeed = MaxSpeed,
        GpuScale = GpuScale
    };

    /// <summary>Keep decoded frames on the graphics card and shrink them there (see <see cref="DetectionOptions.GpuScale"/>). On unless turned off.</summary>
    public bool GpuScale => Flag("GpuScale", true);

    public int Concurrency => (int)Number("Concurrency", 1, 1, 8);

    /// <summary>
    /// Decoder threads per analysis (0: every core). Unset: 2, so an analysis takes a small, steady share and the
    /// channels on air keep the rest.
    /// </summary>
    public int DecodeThreads => (int)Number("DecodeThreads", 2, 0, 32);

    /// <summary>Skip decoding what black detection doesn't need (see <see cref="DetectionOptions.FastDecode"/>). On unless turned off.</summary>
    public bool FastDecode => Flag("FastDecode", true);

    /// <summary>
    /// The fastest a programme is read, in multiples of real time; 0 for no limit. Unset: 20× (a half-hour episode in
    /// about a minute and a half), which keeps an analysis gentle without holding up programmes about to air.
    /// </summary>
    public double MaxSpeed => Number("MaxSpeed", 20, 0, 1000);

    /// <summary>
    /// The <c>HardwareDecode</c> setting as ffmpeg's <c>-hwaccel</c>, or null for software. Unset (or <c>match</c>): the
    /// graphics card App settings already encodes on (see <see cref="MatchingDecoder"/>), else software; <c>auto</c> on a
    /// Raspberry Pi, whose chip decodes HEVC. Anything the card can't read falls back to software.
    /// </summary>
    public string? HardwareDecode
    {
        get
        {
            string? value = Value("HardwareDecode")?.ToLowerInvariant();
            if (value is null or "match")
            {
                value = HardwareProfile.IsLowPower ? "auto" : MatchingDecoder(EncoderInUse());
            }

            return value is null or "off" or "none" or "software" ? null : value;
        }
    }

    /// <summary>The hardware encoder App settings uses for rendered clips, else for live page channels; null for software.</summary>
    private static string? EncoderInUse()
    {
        H264Encoder rendering = RenderEncodingSettings.Current("medium").Video.Encoder;
        H264Encoder live = LiveEncodingSettings.Current.Video.Encoder;
        return rendering.Hardware ? rendering.Name : live.Hardware ? live.Name : null;
    }

    /// <summary>
    /// The decoder on the same chip as an encoder: someone who has picked their graphics card for encoding has one that
    /// can decode too. Null when there's no such encoder (or no matching decoder).
    /// </summary>
    public static string? MatchingDecoder(string? encoder) => encoder switch
    {
        "h264_nvenc" => "cuda",
        "h264_qsv" => "qsv",
        "h264_amf" => OperatingSystem.IsWindows() ? "d3d11va" : "vaapi",
        "h264_videotoolbox" => "videotoolbox",
        "h264_v4l2m2m" => "auto",
        _ => null
    };

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
