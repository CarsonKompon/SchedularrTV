using System.Globalization;

namespace SchedularrTV.Core.Platform;

/// <summary>One H.264 encoder ffmpeg can use, and what it needs.</summary>
/// <param name="Name">ffmpeg's name for it (<c>libx264</c>, <c>h264_v4l2m2m</c>…).</param>
/// <param name="Label">What the UI calls it.</param>
/// <param name="Hardware">Runs on a video chip rather than the CPU: no presets or quality (CRF) setting, only a bitrate.</param>
public sealed record H264Encoder(string Name, string Label, bool Hardware)
{
    public static readonly H264Encoder Software = new("libx264", "Software (libx264)", false);

    /// <summary>Everything the settings offer. Only <see cref="Software"/> works everywhere.</summary>
    public static readonly IReadOnlyList<H264Encoder> All =
    [
        Software,
        new("h264_v4l2m2m", "Raspberry Pi 4 hardware (h264_v4l2m2m)", true),
        new("h264_nvenc", "NVIDIA (h264_nvenc)", true),
        new("h264_qsv", "Intel Quick Sync (h264_qsv)", true),
        new("h264_amf", "AMD (h264_amf)", true),
        new("h264_videotoolbox", "Apple VideoToolbox (h264_videotoolbox)", true)
    ];

    public static H264Encoder Find(string? name) =>
        All.FirstOrDefault(encoder => string.Equals(encoder.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Software;
}

/// <summary>
/// The x264 speed presets, fastest first. Faster means less CPU for the same picture size, at the cost of quality
/// per bit (and, for quality-based encodes, bigger files).
/// </summary>
public static class X264Presets
{
    public static readonly IReadOnlyList<string> All = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow"];

    public static string Normalise(string? preset, string fallback) =>
        All.FirstOrDefault(known => string.Equals(known, preset?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;

    /// <summary>Slower than <c>veryfast</c>: noticeably heavy on a low-power CPU.</summary>
    public static bool IsSlow(string preset) => All.ToList().IndexOf(preset) > All.ToList().IndexOf("veryfast");
}

/// <summary>
/// How a piece of video is encoded: which encoder, how hard it works, and how many CPU threads it may use. Turns into
/// ffmpeg's <c>-c:v</c> arguments with <see cref="VideoArguments"/>.
/// </summary>
public sealed record VideoEncoderChoice
{
    public H264Encoder Encoder { get; init; } = H264Encoder.Software;

    /// <summary>The x264 preset (also mapped onto NVENC and Quick Sync's own); ignored by the other hardware encoders.</summary>
    public string Preset { get; init; } = "veryfast";

    /// <summary>CPU threads for the encoder; 0 lets ffmpeg decide (every core).</summary>
    public int Threads { get; init; }

    /// <summary>
    /// The <c>-c:v …</c> arguments. A software encode uses <paramref name="crf"/> when given (quality-based, for clips
    /// rendered to files) or else <paramref name="bitrateKbps"/>; hardware encoders always use the bitrate, since
    /// they have no CRF. <paramref name="profileHigh"/> asks for H.264 High profile where the encoder takes it.
    /// </summary>
    public IReadOnlyList<string> VideoArguments(int bitrateKbps, int? crf = null, bool profileHigh = true, string? tune = null)
    {
        string Kbps(int kbps) => $"{kbps.ToString(CultureInfo.InvariantCulture)}k";
        List<string> arguments = ["-c:v", Encoder.Name];
        switch (Encoder.Name)
        {
            case "libx264":
                arguments.AddRange(["-preset", Preset]);
                if (tune is not null)
                {
                    arguments.AddRange(["-tune", tune]);
                }

                break;
            case "h264_nvenc":
                arguments.AddRange(["-preset", NvencPreset(Preset)]);
                break;
            case "h264_qsv":
                arguments.AddRange(["-preset", Preset is "ultrafast" or "superfast" ? "veryfast" : Preset is "slow" ? "slow" : Preset]);
                break;
            case "h264_videotoolbox":
                arguments.AddRange(["-realtime", "1"]);
                break;
        }

        if (profileHigh && Encoder.Name != "h264_v4l2m2m")
        {
            arguments.AddRange(["-profile:v", "high"]);
        }

        if (!Encoder.Hardware && crf is { } quality)
        {
            arguments.AddRange(["-crf", quality.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.AddRange(["-b:v", Kbps(bitrateKbps)]);
            // The Pi's V4L2 encoder takes only a target bitrate.
            if (Encoder.Name != "h264_v4l2m2m")
            {
                arguments.AddRange(["-maxrate", Kbps(bitrateKbps), "-bufsize", Kbps(bitrateKbps * 2)]);
            }
        }

        if (Threads > 0 && !Encoder.Hardware)
        {
            arguments.AddRange(["-threads", Threads.ToString(CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    private static string NvencPreset(string x264) => x264 switch
    {
        "ultrafast" => "p1",
        "superfast" => "p2",
        "veryfast" => "p3",
        "faster" or "fast" => "p4",
        "medium" => "p5",
        _ => "p6"
    };
}

/// <summary>
/// How live page channels (web pages, radio, the TV guide channel, streamers) are encoded: the <c>LiveEncoding</c> app
/// settings. Blank settings take the defaults for this computer (<see cref="HardwareProfile"/>): 720p30 with x264
/// <c>veryfast</c>, or 540p30 with <c>ultrafast</c> on a Raspberry Pi.
/// </summary>
public sealed record LiveEncodingSettings
{
    public const string Section = "LiveEncoding";

    public static readonly IReadOnlyList<int> Heights = [360, 480, 540, 720];
    public static readonly IReadOnlyList<int> FrameRates = [15, 24, 25, 30];

    public VideoEncoderChoice Video { get; init; } = new();

    /// <summary>The stream's height; the width follows at 16:9. Pages are still laid out at 1280×720 and scaled.</summary>
    public int Height { get; init; } = 720;

    public int Width => WidthFor(Height);

    /// <summary>The 16:9 width for a height, rounded to an even number: 1280, 960, 854, 640.</summary>
    public static int WidthFor(int height) => (height * 16 / 9 + 1) / 2 * 2;

    public int FrameRate { get; init; } = 30;

    public int BitrateKbps { get; init; } = 3500;

    /// <summary>Changes whenever anything that would change the running stream does.</summary>
    public string Fingerprint => $"{Video.Encoder.Name}/{Video.Preset}/{Video.Threads}/{Width}x{Height}@{FrameRate}/{BitrateKbps}";

    /// <summary>The standard or low-power defaults, whichever this computer gets.</summary>
    public static LiveEncodingSettings Defaults => HardwareProfile.IsLowPower
        ? new LiveEncodingSettings { Video = new VideoEncoderChoice { Preset = "ultrafast" }, Height = 540, FrameRate = 30, BitrateKbps = 2500 }
        : new LiveEncodingSettings();

    /// <summary>The settings in use now (see <see cref="PlatformSettings"/>).</summary>
    public static LiveEncodingSettings Current => From(PlatformSettings.Read);

    /// <summary>
    /// Reads the settings from a lookup of app configuration keys (<c>configuration[key]</c>), filling in the
    /// defaults for anything blank.
    /// </summary>
    public static LiveEncodingSettings From(Func<string, string?> read)
    {
        LiveEncodingSettings defaults = Defaults;
        H264Encoder encoder = read($"{Section}:Encoder") is { Length: > 0 } name ? H264Encoder.Find(name) : defaults.Video.Encoder;
        return new LiveEncodingSettings
        {
            Video = new VideoEncoderChoice
            {
                Encoder = encoder,
                Preset = X264Presets.Normalise(read($"{Section}:Preset"), defaults.Video.Preset),
                Threads = Int(read($"{Section}:Threads"), 0, 16) ?? defaults.Video.Threads
            },
            Height = Int(read($"{Section}:Height"), 0, 2160) is { } height && Heights.Contains(height) ? height : defaults.Height,
            FrameRate = Int(read($"{Section}:FrameRate"), 0, 60) is { } fps && FrameRates.Contains(fps) ? fps : defaults.FrameRate,
            BitrateKbps = Int(read($"{Section}:BitrateKbps"), 300, 20000) ?? defaults.BitrateKbps
        };
    }

    internal static int? Int(string? raw, int min, int max) =>
        int.TryParse(raw?.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, min, max) : null;
}

/// <summary>
/// How clips rendered ahead of time (generated promos, credit squeezes) are encoded: the <c>Rendering</c> app settings.
/// These run in the background, so on a Raspberry Pi they default to a faster preset and two threads, leaving the
/// rest of the CPU to the channels on air.
/// </summary>
public sealed record RenderEncodingSettings
{
    public const string Section = "Rendering";

    public VideoEncoderChoice Video { get; init; } = new();

    /// <summary>The bitrate hardware encoders use, which can't encode to a quality instead.</summary>
    public int HardwareBitrateKbps { get; init; } = 8000;

    /// <summary>
    /// The defaults for this computer: <paramref name="standardPreset"/> (each renderer's own, <c>medium</c> for promos
    /// and <c>veryfast</c> for credit squeezes), or on a Raspberry Pi <c>superfast</c> with two threads.
    /// </summary>
    public static RenderEncodingSettings Defaults(string standardPreset) => HardwareProfile.IsLowPower
        ? new RenderEncodingSettings { Video = new VideoEncoderChoice { Preset = "superfast", Threads = 2 }, HardwareBitrateKbps = 6000 }
        : new RenderEncodingSettings { Video = new VideoEncoderChoice { Preset = standardPreset } };

    /// <summary>The settings in use now (see <see cref="PlatformSettings"/>).</summary>
    public static RenderEncodingSettings Current(string standardPreset) => From(PlatformSettings.Read, standardPreset);

    public static RenderEncodingSettings From(Func<string, string?> read, string standardPreset)
    {
        RenderEncodingSettings defaults = Defaults(standardPreset);
        H264Encoder encoder = read($"{Section}:Encoder") is { Length: > 0 } name ? H264Encoder.Find(name) : defaults.Video.Encoder;
        return new RenderEncodingSettings
        {
            Video = new VideoEncoderChoice
            {
                Encoder = encoder,
                Preset = X264Presets.Normalise(read($"{Section}:Preset"), defaults.Video.Preset),
                Threads = LiveEncodingSettings.Int(read($"{Section}:Threads"), 0, 16) ?? defaults.Video.Threads
            },
            HardwareBitrateKbps = LiveEncodingSettings.Int(read($"{Section}:HardwareBitrateKbps"), 500, 40000) ?? defaults.HardwareBitrateKbps
        };
    }
}
