using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.TvGuide;

/// <summary>The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's.</summary>
public sealed class GuideSettings(IPluginContext context)
{
    public const string RoutePrefix = "/plugins/tv-guide";

    private string? Value(string key) => context.Configuration[key] is { Length: > 0 } value ? value.Trim().Trim('"') : null;

    public string? BrowserPath => Value("BrowserPath");

    public int CaptureFrameRate =>
        int.TryParse(Value("FrameRate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) ? Math.Clamp(fps, 5, 30) : 24;

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

    /// <summary>Where ErsatzTV fetches the guide's stream from: this scheduler on this machine, unless set.</summary>
    public string StreamBase => (Value("StreamAddress") ?? $"http://localhost:{ServerPort}").TrimEnd('/');

    public string LiveUrl(string guideId) => $"{StreamBase}{RoutePrefix}/live/{Uri.EscapeDataString(guideId)}.ts";

    /// <summary>The page the hidden browser shows; the browser runs on this machine, so it's always local.</summary>
    public string PageUrl(string guideId) => $"http://localhost:{ServerPort}{RoutePrefix}/view/{Uri.EscapeDataString(guideId)}";

    /// <summary>A channel's stream from ErsatzTV, for the video window or the sound.</summary>
    public string ChannelUrl(int number) =>
        $"http://localhost:{(int.TryParse(context.AppConfiguration["ErsatzTv:Lineup:Port"], out int port) ? port : 8409)}/channel/{number}.m3u8";

    /// <summary>
    /// Where a library path is on this computer. The media server may report paths as it sees them, so ErsatzTV's path
    /// mappings are tried when the path doesn't exist here.
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
            if (mapping["From"] is { Length: > 0 } from && mapping["To"] is { } to && path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                string mapped = (to + path[from.Length..]).Replace(OperatingSystem.IsWindows() ? '/' : '\\', Path.DirectorySeparatorChar);
                if (File.Exists(mapped))
                {
                    return mapped;
                }
            }
        }

        return null;
    }

    /// <summary>What a guide plays when it's on air.</summary>
    public LiveStreamSpec Spec(GuideStation guide) => new()
    {
        Id = guide.Id,
        Name = guide.Name,
        PageUrl = PageUrl(guide.Id),
        Ffmpeg = Ffmpeg,
        BrowserPath = BrowserPath,
        CaptureFrameRate = CaptureFrameRate,
        WindowStreamUrl = guide.Top == TopPanel.Channel && guide.WindowChannel is { } window ? ChannelUrl(window) : null,
        Window = guide.Top == TopPanel.Channel ? guide.Window : null,
        Audio = guide.Sound switch
        {
            GuideSound.Channel when guide.HeardChannel is not null => LiveAudio.Stream,
            GuideSound.Silence => LiveAudio.Silence,
            _ => LiveAudio.Music
        },
        AudioStreamUrl = guide.HeardChannel is { } heard ? ChannelUrl(heard) : null,
        MusicFolder = guide.MusicFolder,
        Volume = guide.Volume
    };
}
