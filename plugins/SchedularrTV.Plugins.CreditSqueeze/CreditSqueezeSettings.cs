using System.Globalization;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Hosting;
using SchedularrTV.Service.Hosting;

namespace SchedularrTV.CreditSqueeze;

/// <summary>The plugin's own settings (its gear on the Plugins page).</summary>
public sealed class CreditSqueezeSettings(IPluginContext context)
{
    private string? Value(string key) => context.Configuration[key]?.Trim().Trim('"') is { Length: > 0 } value ? value : null;

    private int Number(string key, int fallback, int min, int max) =>
        int.TryParse(Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, min, max) : fallback;

    public string DataDirectory => context.DataDirectory;

    /// <summary>Where squeezes are rendered; ErsatzTV must be able to read it.</summary>
    public string SaveFolder => Value("SaveFolder") is { } chosen ? AppPaths.Rooted(context.AppDataDirectory, chosen) : Path.Combine(context.DataDirectory, "renders");

    /// <summary>Squeezes are made once they air within this.</summary>
    public TimeSpan LeadTime => TimeSpan.FromMinutes(Number("LeadTimeMinutes", 240, 15, 2880));

    /// <summary>Renders no schedule has used for this long are deleted.</summary>
    public TimeSpan KeepFor => TimeSpan.FromDays(Number("KeepDays", 3, 1, 60));

    /// <summary>Asks Jellyfin for episodes' credits segments (as well as reading credits chapters).</summary>
    public bool UseSegments => !bool.TryParse(Value("UseSegments"), out bool use) || use;

    /// <summary>Squeezes are only made for channels someone is watching (ErsatzTV says who is).</summary>
    public bool WatchedOnly => !bool.TryParse(Value("WatchedOnly"), out bool watched) || watched;

    public (int Width, int Height) Size => Value("Resolution") == "1080p" ? (1920, 1080) : (1280, 720);

    public string Ffmpeg
    {
        get
        {
            if (Value("FfmpegPath") is { } own)
            {
                return own;
            }

            IConfiguration app = context.AppConfiguration;
            return ToolLocator.Resolve(app["GeneratedPromos:FfmpegPath"], "ffmpeg", context.AppDataDirectory);
        }
    }
}
