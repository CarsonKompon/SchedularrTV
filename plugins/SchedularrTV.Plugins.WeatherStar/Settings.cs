using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.WeatherStar;

/// <summary>The plugin's own settings (its gear on the Plugins page), with their defaults.</summary>
public sealed class WeatherStarSettings(IPluginContext context)
{
    public const string DefaultSite = "https://mwood77.github.io/ws4kp-international/";

    private string? Value(string key) => context.Configuration[key] is { Length: > 0 } value ? value.Trim().Trim('"') : null;

    public string Site => Value("WeatherStarUrl") ?? DefaultSite;

    public string? BrowserPath => Value("BrowserPath");

    public int CaptureFrameRate =>
        int.TryParse(Value("FrameRate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) ? Math.Clamp(fps, 5, 30) : 15;

    public TimeSpan KeepAlive =>
        TimeSpan.FromSeconds(int.TryParse(Value("KeepAliveSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? Math.Clamp(seconds, 0, 3600) : 90);

    /// <summary>
    /// The plugin's own choice, else the one App settings uses for generated promos, else one beside the ffprobe the
    /// commercials library uses, else ffmpeg on the PATH.
    /// </summary>
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

    /// <summary>The address ErsatzTV fetches streams from: this scheduler on this machine, unless set.</summary>
    public string StreamBase =>
        (Value("StreamAddress") ?? $"http://localhost:{(int.TryParse(context.AppConfiguration["Server:Port"], out int port) ? port : 5252)}").TrimEnd('/');
}

/// <summary>Where each station's live stream is served.</summary>
public sealed class StreamAddress(WeatherStarSettings settings)
{
    public const string RoutePrefix = "/plugins/ws4kp/live";

    public string LiveUrl(string stationId) => $"{settings.StreamBase}{RoutePrefix}/{Uri.EscapeDataString(stationId)}.ts";
}
