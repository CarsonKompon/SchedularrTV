using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace SchedularrTV.Iptv;

/// <summary>The plugin's own settings (its gear on the Plugins page), with their defaults.</summary>
public sealed class IptvSettings(IPluginContext context)
{
    public const string RoutePrefix = "/plugins/iptv";

    private string? Value(string key) => context.Configuration[key]?.Trim().Trim('"') is { Length: > 0 } value ? value : null;

    public TimeSpan RefreshInterval =>
        TimeSpan.FromHours(int.TryParse(Value("RefreshHours"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) ? Math.Clamp(hours, 1, 48) : 6);

    public TimeSpan KeepAlive =>
        TimeSpan.FromSeconds(int.TryParse(Value("KeepAliveSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? Math.Clamp(seconds, 0, 600) : 30);

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

    /// <summary>Where ErsatzTV fetches the relayed streams from: this scheduler on this machine, unless set.</summary>
    public string StreamBase => (Value("StreamAddress") ?? $"http://localhost:{ServerPort}").TrimEnd('/');

    public string LiveUrl(string sourceId, int number) =>
        $"{StreamBase}{RoutePrefix}/live/{Uri.EscapeDataString(sourceId)}/{number.ToString(CultureInfo.InvariantCulture)}.ts";
}
