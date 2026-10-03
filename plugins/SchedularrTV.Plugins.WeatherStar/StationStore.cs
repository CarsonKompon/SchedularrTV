using System.Text.Json;

namespace SchedularrTV.WeatherStar;

/// <summary>The weather stations, kept in the plugin's data folder (<c>plugin-data/ws4kp/stations.json</c>).</summary>
public sealed class StationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Lock _gate = new();
    private IReadOnlyList<WeatherStation> _stations;

    public StationStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "stations.json");
        _stations = Load(_path);
    }

    /// <summary>Raised after the stations are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<WeatherStation> Stations
    {
        get
        {
            lock (_gate)
            {
                return _stations;
            }
        }
    }

    public WeatherStation? Find(string id) =>
        Stations.FirstOrDefault(station => string.Equals(station.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every station; ids are made unique and lengths tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<WeatherStation> stations)
    {
        List<WeatherStation> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (WeatherStation station in stations)
        {
            WeatherStation copy = station.Copy();
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Weather" : copy.Name.Trim();
            string id = string.IsNullOrWhiteSpace(copy.Id) ? WeatherStation.Slug(copy.Name) : copy.Id;
            string unique = id;
            for (int suffix = 2; !ids.Add(unique); suffix++)
            {
                unique = $"{id}-{suffix}";
            }

            copy.Id = unique;
            copy.Lengths = [.. copy.ValidLengths];
            copy.MusicVolume = Math.Clamp(copy.MusicVolume, 0, 1);
            copy.Speed = Math.Clamp(copy.Speed, 0.5, 1.5);
            copy.MusicFolder = string.IsNullOrWhiteSpace(copy.MusicFolder) ? null : copy.MusicFolder.Trim().Trim('"');
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

    private static IReadOnlyList<WeatherStation> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<WeatherStation>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
