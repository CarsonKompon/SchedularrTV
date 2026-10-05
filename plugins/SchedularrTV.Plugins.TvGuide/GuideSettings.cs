using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.TvGuide;

/// <summary>The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's.</summary>
public sealed class GuideSettings(IPluginContext context)
{
    public const string RoutePrefix = "/plugins/tv-guide";

    private string? Value(string key) => context.Configuration[key] is { Length: > 0 } value ? value.Trim().Trim('"') : null;

    public string? BrowserPath => Value("BrowserPath");

    /// <summary>Unset: 24, or 12 on a Raspberry Pi (the listings scroll all the time, so every frame is new).</summary>
    public int CaptureFrameRate =>
        int.TryParse(Value("FrameRate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) ? Math.Clamp(fps, 5, 30)
            : SchedularrTV.Core.Platform.HardwareProfile.Pick(24, 12);

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

    /// <summary>ffprobe: App settings' own, else the one beside ffmpeg, else ffprobe on the PATH.</summary>
    public string Ffprobe
    {
        get
        {
            if (context.AppConfiguration["FileSystemLibrary:FfprobePath"] is { Length: > 0 } configured && Path.IsPathRooted(configured))
            {
                return configured;
            }

            string ffmpeg = Ffmpeg;
            if (Path.IsPathRooted(ffmpeg))
            {
                string sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }

            return "ffprobe";
        }
    }

    /// <summary>The window's feed (commercials, or channels in turn) for a guide; local, like the page.</summary>
    public string FeedUrl(string guideId) => $"http://localhost:{ServerPort}{RoutePrefix}/feed/{Uri.EscapeDataString(guideId)}.ts";

    /// <summary>What plays in a guide's video window: its feed, the one channel chosen, or nothing.</summary>
    public string? WindowUrl(GuideStation guide) =>
        guide.Window is null ? null
        : guide.WindowIsFeed ? FeedUrl(guide.Id)
        : guide.Top == TopPanel.Channel && guide.WindowChannel is { } channel ? ChannelUrl(channel)
        : null;

    /// <summary>What a guide plays when it's on air.</summary>
    public LiveStreamSpec Spec(GuideStation guide)
    {
        string? window = WindowUrl(guide);
        string? sound = guide.Sound switch
        {
            GuideSound.Window => window,
            GuideSound.Channel => guide.SoundChannel is { } heard ? ChannelUrl(heard)
                // A channel's sound with none chosen has always meant the one in the window.
                : guide.Top == TopPanel.Channel ? window : null,
            _ => null
        };

        return new LiveStreamSpec
        {
            Id = guide.Id,
            Name = guide.Name,
            PageUrl = PageUrl(guide.Id),
            Ffmpeg = Ffmpeg,
            BrowserPath = BrowserPath,
            CaptureFrameRate = CaptureFrameRate,
            WindowStreamUrl = window,
            Window = window is null ? null : guide.Window,
            Audio = sound is not null ? LiveAudio.Stream : guide.Sound == GuideSound.Silence ? LiveAudio.Silence : LiveAudio.Music,
            AudioStreamUrl = sound,
            MusicFolder = guide.MusicFolder,
            Volume = guide.Volume
        };
    }
}
