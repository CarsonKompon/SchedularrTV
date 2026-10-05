using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.WebChannels;

/// <summary>The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's.</summary>
public sealed class WebChannelSettings(IPluginContext context)
{
    public const string RoutePrefix = "/plugins/web-channels";

    /// <summary>The plugin this one replaces; its settings are used until this one's are set.</summary>
    public const string LegacyPluginId = "ws4kp";

    private string? Value(string key)
    {
        foreach (string? raw in new[] { context.Configuration[key], context.AppConfiguration[$"Plugins:{LegacyPluginId}:{key}"] })
        {
            if (raw?.Trim().Trim('"') is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    public string WeatherStarSite => Value("WeatherStarUrl") ?? WebPresets.DefaultWeatherStarSite;

    public string? BrowserPath => Value("BrowserPath");

    /// <summary>Unset: 15, or 8 on a Raspberry Pi.</summary>
    public int CaptureFrameRate =>
        int.TryParse(Value("FrameRate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) ? Math.Clamp(fps, 5, 30)
            : SchedularrTV.Core.Platform.HardwareProfile.Pick(15, 8);

    public TimeSpan KeepAlive =>
        TimeSpan.FromSeconds(int.TryParse(Value("KeepAliveSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? Math.Clamp(seconds, 0, 3600) : 90);

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

    private int ServerPort => int.TryParse(context.AppConfiguration["Server:Port"], out int port) ? port : 5252;

    /// <summary>Where ErsatzTV fetches streams from: this scheduler on this machine, unless set.</summary>
    public string StreamBase => (Value("StreamAddress") ?? $"http://localhost:{ServerPort}").TrimEnd('/');

    public string LiveUrl(string channelId) => $"{StreamBase}{RoutePrefix}/live/{Uri.EscapeDataString(channelId)}.ts";

    /// <summary>Where pages are for the hidden browser, which runs on this machine, so the plugin's own pages are local.</summary>
    public PageAddresses ForBrowser => new(WeatherStarSite, $"http://localhost:{ServerPort}");

    /// <summary>Where pages are for previews in the app: the plugin's own pages relative to it.</summary>
    public PageAddresses ForPreview => new(WeatherStarSite, null);

    public string PageUrl(WebChannel channel) => channel.PresetInfo.PageUrl(channel, ForBrowser);

    /// <summary>What a channel plays when it's on air.</summary>
    public LiveStreamSpec Spec(WebChannel channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        PageUrl = PageUrl(channel),
        Ffmpeg = Ffmpeg,
        BrowserPath = BrowserPath,
        CaptureFrameRate = CaptureFrameRate,
        Audio = string.IsNullOrWhiteSpace(channel.MusicFolder) ? LiveAudio.Silence : LiveAudio.Music,
        MusicFolder = channel.MusicFolder,
        Volume = channel.MusicVolume
    };
}
