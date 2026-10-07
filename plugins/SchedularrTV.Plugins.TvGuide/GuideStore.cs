using System.Text.Json;

namespace SchedularrTV.TvGuide;

/// <summary>The guide channels, kept in the plugin's data folder (<c>plugin-data/tv-guide/guides.json</c>).</summary>
public sealed class GuideStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Lock _gate = new();
    private IReadOnlyList<GuideStation> _guides;

    public GuideStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "guides.json");
        _guides = Load(_path);
    }

    /// <summary>Raised after the guides are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<GuideStation> Guides
    {
        get
        {
            lock (_gate)
            {
                return _guides;
            }
        }
    }

    public GuideStation? Find(string id) =>
        Guides.FirstOrDefault(guide => string.Equals(guide.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every guide; ids are made unique and values tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<GuideStation> guides)
    {
        List<GuideStation> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase) { "preview" };
        foreach (GuideStation guide in guides)
        {
            GuideStation copy = guide.Copy();
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Program Guide" : copy.Name.Trim();
            string id = string.IsNullOrWhiteSpace(copy.Id) ? GuideStation.Slug(copy.Name) : copy.Id;
            string unique = id;
            for (int suffix = 2; !ids.Add(unique); suffix++)
            {
                unique = $"{id}-{suffix}";
            }

            copy.Id = unique;
            Tidy(copy);
            saved.Add(copy);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
        File.Move(temp, _path, overwrite: true);

        lock (_gate)
        {
            _guides = saved;
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps every value in a range the page and the stream can use.</summary>
    public static void Tidy(GuideStation guide)
    {
        guide.Lengths = [.. guide.ValidLengths];
        guide.HoursShown = Math.Clamp(guide.HoursShown, 0.5, 6);
        guide.ScrollSpeed = Math.Clamp(guide.ScrollSpeed, 0, 300);
        guide.RowHeight = Math.Clamp(guide.RowHeight, 30, 140);
        guide.FontScale = Math.Clamp(guide.FontScale, 0.5, 2.5);
        guide.FontFamily = LiveFonts.Clean(guide.FontFamily);
        guide.TopPercent = Math.Clamp(guide.TopPercent, 0, 70);
        guide.Volume = Math.Clamp(guide.Volume, 0, 1);
        guide.MusicFolder = string.IsNullOrWhiteSpace(guide.MusicFolder) ? null : guide.MusicFolder.Trim().Trim('"');
        guide.HiddenChannels = [.. guide.HiddenChannels.Distinct().Order()];
        guide.CycleChannelNumbers = [.. guide.CycleChannelNumbers.Distinct().Order()];
        guide.CycleMinutes = Math.Clamp(guide.CycleMinutes, 1, 120);
        guide.Background = Colour(guide.Background);
        guide.RowColor = Colour(guide.RowColor);
        guide.TextColor = Colour(guide.TextColor);
        guide.AccentColor = Colour(guide.AccentColor);
    }

    // Only #rgb / #rrggbb, so nothing else can reach the page's style sheet.
    private static string? Colour(string? value) =>
        value is { Length: 4 or 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit) ? value : null;

    private static IReadOnlyList<GuideStation> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<GuideStation>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
