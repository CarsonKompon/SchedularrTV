using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.Streamers;

/// <summary>The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's.</summary>
public sealed class StreamerSettings(IPluginContext context)
{
    public const string RoutePrefix = "/plugins/streamers";

    private string? Value(string key) => context.Configuration[key]?.Trim().Trim('"') is { Length: > 0 } value ? value : null;

    private int Number(string key, int fallback, int min, int max) =>
        int.TryParse(Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, min, max) : fallback;

    public string DataDirectory => context.DataDirectory;

    public string AppDataDirectory => context.AppDataDirectory;

    public string? BrowserPath => Value("BrowserPath");

    public string? YtDlpPath => Value("YtDlpPath");

    /// <summary>How often each streamer is checked for being live.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Number("PollSeconds", 60, 20, 900));

    /// <summary>The tallest picture asked of a stream (the channel itself is always 720p).</summary>
    public int MaxHeight => Number("MaxHeight", 720, 360, 1080);

    public int CaptureFrameRate => Number("FrameRate", 15, 5, 30);

    public TimeSpan KeepAlive => TimeSpan.FromSeconds(Number("KeepAliveSeconds", 60, 0, 3600));

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

    /// <summary>The time zone predictions are worked out in: the schedule's, else this computer's.</summary>
    public TimeZoneInfo TimeZone
    {
        get
        {
            string? id = Value("TimeZone");
            try
            {
                return id is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Local;
            }
        }
    }

    private int ServerPort => int.TryParse(context.AppConfiguration["Server:Port"], out int port) ? port : 5252;

    private string Local => $"http://localhost:{ServerPort}";

    /// <summary>Where ErsatzTV fetches a channel's stream from: this scheduler on this machine, unless set.</summary>
    public string StreamBase => (Value("StreamAddress") ?? Local).TrimEnd('/');

    public string LiveUrl(string channelId) => $"{StreamBase}{RoutePrefix}/live/{Uri.EscapeDataString(channelId)}.ts";

    /// <summary>The page the hidden browser shows; the browser runs on this machine, so it's always local.</summary>
    public string PageUrl(string channelId) => $"{Local}{RoutePrefix}/view/{Uri.EscapeDataString(channelId)}";
}
