using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.Radio;

/// <summary>The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's.</summary>
public sealed class RadioSettings(IPluginContext context)
{
    public const string RoutePrefix = "/plugins/radio";

    private string? Value(string key) => context.Configuration[key]?.Trim().Trim('"') is { Length: > 0 } value ? value : null;

    public string? BrowserPath => Value("BrowserPath");

    public int CaptureFrameRate =>
        int.TryParse(Value("FrameRate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) ? Math.Clamp(fps, 5, 30) : 20;

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

    private string Local => $"http://localhost:{ServerPort}";

    /// <summary>Where ErsatzTV fetches a station's stream from: this scheduler on this machine, unless set.</summary>
    public string StreamBase => (Value("StreamAddress") ?? Local).TrimEnd('/');

    public string LiveUrl(string stationId) => $"{StreamBase}{RoutePrefix}/live/{Uri.EscapeDataString(stationId)}.ts";

    /// <summary>The page the hidden browser shows; the browser runs on this machine, so it's always local.</summary>
    public string PageUrl(string stationId) => $"{Local}{RoutePrefix}/view/{Uri.EscapeDataString(stationId)}";

    /// <summary>The station's sound, sequenced by the plugin; read by the station's own ffmpeg.</summary>
    public string SoundUrl(string stationId) => $"{Local}{RoutePrefix}/sound/{Uri.EscapeDataString(stationId)}.ts";

    /// <summary>What a station plays when it's on air: its page, its sound, and the visualizer drawn from that sound.</summary>
    public LiveStreamSpec Spec(RadioStation station)
    {
        FrameRect? rect = station.VisualizerRect;
        return new LiveStreamSpec
        {
            Id = station.Id,
            Name = station.Name,
            PageUrl = PageUrl(station.Id),
            Ffmpeg = Ffmpeg,
            BrowserPath = BrowserPath,
            CaptureFrameRate = CaptureFrameRate,
            Audio = LiveAudio.Stream,
            AudioStreamUrl = SoundUrl(station.Id),
            Volume = station.Volume,
            Visualizer = rect is null ? null : RadioLayout.Filter(station.Visualizer, rect, station.AccentColor),
            VisualizerRect = rect
        };
    }
}
