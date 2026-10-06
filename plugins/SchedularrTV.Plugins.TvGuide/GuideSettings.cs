using Microsoft.Extensions.Configuration;

namespace SchedularrTV.TvGuide;

/// <summary>
/// The plugin's own settings (its gear on the Plugins page), and the addresses it works out from the app's. What every
/// live page plugin has comes from <see cref="LivePageSettings"/>: captured 24 times a second (12 on a Raspberry Pi; the
/// listings scroll all the time, so every frame is new), kept on 90 seconds after the last viewer.
/// </summary>
public sealed class GuideSettings(IPluginContext context) : LivePageSettings(context, RoutePrefix, 24, 12, 90)
{
    public const string RoutePrefix = "/plugins/tv-guide";

    /// <summary>A channel's stream from ErsatzTV, for the video window or the sound.</summary>
    public string ChannelUrl(int number) =>
        $"http://localhost:{(int.TryParse(Context.AppConfiguration["ErsatzTv:Lineup:Port"], out int port) ? port : 8409)}/channel/{number}.m3u8";

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

        foreach (IConfigurationSection mapping in Context.AppConfiguration.GetSection("ErsatzTv:PathMappings").GetChildren())
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
            if (Context.AppConfiguration["FileSystemLibrary:FfprobePath"] is { Length: > 0 } configured && Path.IsPathRooted(configured))
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
    public string FeedUrl(string guideId) => $"{Local}{RoutePrefix}/feed/{Uri.EscapeDataString(guideId)}.ts";

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

        return BaseSpec(guide.Id, guide.Name, PageUrl(guide.Id)) with
        {
            WindowStreamUrl = window,
            Window = window is null ? null : guide.Window,
            Audio = sound is not null ? LiveAudio.Stream : guide.Sound == GuideSound.Silence ? LiveAudio.Silence : LiveAudio.Music,
            AudioStreamUrl = sound,
            MusicFolder = guide.MusicFolder,
            Volume = guide.Volume
        };
    }
}
