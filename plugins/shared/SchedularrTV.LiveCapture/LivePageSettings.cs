using System.Globalization;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Plugins;

namespace SchedularrTV.LiveCapture;

/// <summary>
/// The settings every live page plugin has (its gear on the Plugins page): the browser, ffmpeg, how often the page is
/// captured, how long a channel outlives its last viewer, decoding on the graphics chip, pacing the page's animations;
/// and the addresses worked out from the app's settings. Each plugin adds its own on top.
/// </summary>
/// <param name="context">The plugin's context.</param>
/// <param name="routePrefix">Where the plugin's endpoints are, e.g. <c>/plugins/radio</c>.</param>
/// <param name="frameRate">Capture frames per second when unset.</param>
/// <param name="lowPowerFrameRate">The same on a Raspberry Pi.</param>
/// <param name="keepAliveSeconds">How long a channel outlives its last viewer when unset.</param>
public abstract class LivePageSettings(IPluginContext context, string routePrefix, int frameRate, int lowPowerFrameRate, int keepAliveSeconds)
{
    /// <summary>The ffmpeg <c>-hwaccel</c> methods the <c>VideoDecoding</c> setting can choose.</summary>
    public static readonly IReadOnlyList<string> DecodingMethods = ["auto", "cuda", "qsv", "d3d11va", "dxva2", "vaapi", "videotoolbox", "drm"];

    protected IPluginContext Context => context;

    /// <summary>A setting's value, trimmed (and unquoted), or null when it isn't set.</summary>
    protected virtual string? Value(string key) => context.Configuration[key]?.Trim().Trim('"') is { Length: > 0 } value ? value : null;

    /// <summary>A whole-number setting, kept between <paramref name="min"/> and <paramref name="max"/>; <paramref name="fallback"/> when unset.</summary>
    protected int Number(string key, int fallback, int min, int max) =>
        int.TryParse(Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, min, max) : fallback;

    public string? BrowserPath => Value("BrowserPath");

    /// <summary>How often the page is captured (5 to 30 a second), with the plugin's own defaults when unset.</summary>
    public int CaptureFrameRate => Number("FrameRate", HardwareProfile.Pick(frameRate, lowPowerFrameRate), 5, 30);

    public TimeSpan KeepAlive => TimeSpan.FromSeconds(Number("KeepAliveSeconds", keepAliveSeconds, 0, 3600));

    /// <summary>
    /// How another stream shown in a window is decoded: null (unset, <c>off</c> or <c>software</c>) on the processor,
    /// else an ffmpeg <c>-hwaccel</c> method for the graphics chip (<c>auto</c> picks whichever works here, and ffmpeg
    /// falls back to the processor when none does).
    /// </summary>
    public string? HardwareDecoding =>
        Value("VideoDecoding")?.ToLowerInvariant() is { } method && DecodingMethods.Contains(method) ? method : null;

    /// <summary>
    /// The page's animations (CSS, and anything drawn with requestAnimationFrame) are drawn only as often as the page is
    /// captured, rather than 60 times a second. On unless the <c>PaceAnimations</c> setting is false.
    /// </summary>
    public bool PaceAnimations => !string.Equals(Value("PaceAnimations"), "false", StringComparison.OrdinalIgnoreCase);

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

    protected int ServerPort => int.TryParse(context.AppConfiguration["Server:Port"], out int port) ? port : 5252;

    /// <summary>This scheduler, as the hidden browser and the plugin's own ffmpeg (both on this machine) reach it.</summary>
    protected string Local => $"http://localhost:{ServerPort}";

    /// <summary>Where ErsatzTV fetches streams from: this scheduler on this machine, unless set.</summary>
    public string StreamBase => (Value("StreamAddress") ?? Local).TrimEnd('/');

    public string LiveUrl(string id) => $"{StreamBase}{routePrefix}/live/{Uri.EscapeDataString(id)}.ts";

    /// <summary>The plugin's own page for a channel (its <c>/view/{id}</c>); the browser runs on this machine, so it's always local.</summary>
    public string PageUrl(string id) => $"{Local}{routePrefix}/view/{Uri.EscapeDataString(id)}";

    /// <summary>A channel's stream with what every plugin sets the same way filled in; the plugin adds the rest.</summary>
    public LiveStreamSpec BaseSpec(string id, string name, string pageUrl) => new()
    {
        Id = id,
        Name = name,
        PageUrl = pageUrl,
        Ffmpeg = Ffmpeg,
        BrowserPath = BrowserPath,
        CaptureFrameRate = CaptureFrameRate,
        HardwareDecoding = HardwareDecoding,
        PaceAnimations = PaceAnimations
    };
}
