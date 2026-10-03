using System.Text.Json;

namespace SchedularrTV.WebChannels;

/// <summary>
/// The web page channels, kept in the plugin's data folder (<c>plugin-data/web-channels/channels.json</c>). The first
/// time there's none, the old WeatherStar plugin's stations (<c>plugin-data/ws4kp/stations.json</c>) are taken over
/// as WeatherStar channels.
/// </summary>
public sealed class ChannelStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _path;
    private readonly Lock _gate = new();
    private IReadOnlyList<WebChannel> _channels;

    /// <param name="dataDirectory">The plugin's own data folder.</param>
    /// <param name="appDataDirectory">The scheduler's data folder, to find the old WeatherStar stations; null to not look.</param>
    public ChannelStore(string dataDirectory, string? appDataDirectory = null)
    {
        _path = Path.Combine(dataDirectory, "channels.json");
        _channels = Load(_path);

        string? legacy = appDataDirectory is null ? null : Path.Combine(appDataDirectory, "plugin-data", WebChannelSettings.LegacyPluginId, "stations.json");
        if (!File.Exists(_path) && legacy is not null && File.Exists(legacy))
        {
            // The stations' fields have the same names here, and a channel with no preset is a WeatherStar one.
            IReadOnlyList<WebChannel> stations = Load(legacy);
            if (stations.Count > 0)
            {
                try
                {
                    Save(stations);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Shown anyway; they're written the next time something is saved.
                    _channels = stations;
                }
            }
        }
    }

    /// <summary>Raised after the channels are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<WebChannel> Channels
    {
        get
        {
            lock (_gate)
            {
                return _channels;
            }
        }
    }

    public WebChannel? Find(string id) =>
        Channels.FirstOrDefault(channel => string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every channel; ids are made unique and values tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<WebChannel> channels)
    {
        List<WebChannel> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (WebChannel channel in channels)
        {
            WebChannel copy = channel.Copy();
            Tidy(copy);
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? copy.PresetInfo.DefaultName(null) : copy.Name.Trim();
            string id = string.IsNullOrWhiteSpace(copy.Id) ? WebChannel.Slug(copy.Name) : copy.Id;
            string unique = id;
            for (int suffix = 2; !ids.Add(unique); suffix++)
            {
                unique = $"{id}-{suffix}";
            }

            copy.Id = unique;
            saved.Add(copy);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
        File.Move(temp, _path, overwrite: true);

        lock (_gate)
        {
            _channels = saved;
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps every value in a range the pages and the stream can use.</summary>
    public static void Tidy(WebChannel channel)
    {
        WebPreset preset = channel.PresetInfo;
        channel.Preset = preset.Id;
        channel.Lengths = [.. channel.ValidLengths];
        channel.MusicVolume = Math.Clamp(channel.MusicVolume, 0, 1);
        channel.Speed = Math.Clamp(channel.Speed, 0.5, 1.5);
        channel.Latitude = Math.Clamp(channel.Latitude, -90, 90);
        channel.Longitude = Math.Clamp(channel.Longitude, -180, 180);
        channel.Zoom = channel.Zoom == 0 || !preset.HasZoom ? channel.Zoom : Math.Clamp(channel.Zoom, preset.MinZoom, preset.MaxZoom);
        channel.ClockStyle = channel.ClockStyle == "analog" ? "analog" : "digital";
        channel.MusicFolder = Trimmed(channel.MusicFolder)?.Trim('"');
        channel.Url = Trimmed(channel.Url);
        channel.TimeZone = Trimmed(channel.TimeZone);
        channel.Message = Trimmed(channel.Message) is { } message && message.Length > 500 ? message[..500] : Trimmed(channel.Message);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<WebChannel> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<WebChannel>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
