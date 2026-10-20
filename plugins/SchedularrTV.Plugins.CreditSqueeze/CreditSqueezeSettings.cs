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

    /// <summary>
    /// For episodes whose credits aren't known: squeeze this much of their end anyway (zero: leave them be). A channel
    /// or block can set its own.
    /// </summary>
    public TimeSpan AssumedCredits => TimeSpan.FromSeconds(Number("AssumedCreditsSeconds", 0, 0, 600));

    public (int Width, int Height) Size => Value("Resolution") == "1080p" ? (1920, 1080) : (1280, 720);

    public string ManagedTesseractFolder => Path.Combine(DataDirectory, "tools", "tesseract");
    public string? ConfiguredTesseract => Value("TesseractPath");
    public CreditsOcrOptions Ocr
    {
        get
        {
            string managed = Path.Combine(ManagedTesseractFolder, "tesseract.exe");
            bool downloaded = false;
            try
            {
                string marker = Path.Combine(ManagedTesseractFolder, ".ready");
                downloaded = File.Exists(managed) && File.Exists(marker) && File.ReadAllText(marker) == (ConfiguredTesseract ?? "");
            }
            catch (IOException) { }
            string executable = downloaded ? managed : ToolLocator.Resolve(ConfiguredTesseract, "tesseract");
            if (!downloaded && executable == "tesseract" && OperatingSystem.IsWindows())
            {
                executable = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                    .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Tesseract-OCR", "tesseract.exe"))
                    .FirstOrDefault(File.Exists) ?? executable;
            }
            return new(executable, Value("OcrLanguage") ?? "eng", Number("OcrSampleSeconds", 1, 1, 5),
                DataDirectory: downloaded ? Path.Combine(ManagedTesseractFolder, "tessdata") : null);
        }
    }

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
