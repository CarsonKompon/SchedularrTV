using System.Text.Json;

namespace SchedularrTV.Radio;

/// <summary>The radio stations, kept in the plugin's data folder (<c>plugin-data/radio/stations.json</c>).</summary>
public sealed class RadioStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Lock _gate = new();
    private IReadOnlyList<RadioStation> _stations;

    public RadioStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "stations.json");
        _stations = Load(_path);
    }

    /// <summary>Raised after the stations are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<RadioStation> Stations
    {
        get
        {
            lock (_gate)
            {
                return _stations;
            }
        }
    }

    public RadioStation? Find(string id) =>
        Stations.FirstOrDefault(station => string.Equals(station.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every station; ids are made unique and values tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<RadioStation> stations)
    {
        List<RadioStation> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase) { "preview" };
        foreach (RadioStation station in stations)
        {
            RadioStation copy = station.Copy();
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Radio" : copy.Name.Trim();
            string id = string.IsNullOrWhiteSpace(copy.Id) ? RadioStation.Slug(copy.Name) : copy.Id;
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
            _stations = saved;
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps every value in a range the page and the stream can use.</summary>
    public static void Tidy(RadioStation station)
    {
        station.Lengths = [.. station.ValidLengths];
        station.Volume = Math.Clamp(station.Volume, 0, 2);
        station.LyricsOffset = Math.Clamp(station.LyricsOffset, -30, 30);
        station.AccentColor = RadioLayout.IsColour(station.AccentColor) ? station.AccentColor.ToLowerInvariant() : "#ff5c8a";
        station.BackgroundColor = RadioLayout.IsColour(station.BackgroundColor) ? station.BackgroundColor.ToLowerInvariant() : "#101828";
        station.VisualizerColor = RadioLayout.IsColour(station.VisualizerColor) ? station.VisualizerColor!.ToLowerInvariant() : null;
        station.TextColor = RadioLayout.IsColour(station.TextColor) ? station.TextColor!.ToLowerInvariant() : null;
        station.VisualizerSize = Math.Clamp(station.VisualizerSize, 0.5, 1.5);
        station.VisualizerIntensity = Math.Clamp(station.VisualizerIntensity, 0.25, 3);
        station.TextScale = Math.Clamp(station.TextScale, 0.8, 1.4);
        station.Genres = [.. station.Genres.Where(genre => !string.IsNullOrWhiteSpace(genre)).Select(genre => genre.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        station.Artists = [.. station.Artists.Where(artist => !string.IsNullOrWhiteSpace(artist)).Select(artist => artist.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        station.StreamUrl = string.IsNullOrWhiteSpace(station.StreamUrl) ? null : station.StreamUrl.Trim();
        station.Message = string.IsNullOrWhiteSpace(station.Message) ? null : station.Message.Trim() is { Length: > 300 } long_ ? long_[..300] : station.Message.Trim();
    }

    private static IReadOnlyList<RadioStation> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<RadioStation>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
