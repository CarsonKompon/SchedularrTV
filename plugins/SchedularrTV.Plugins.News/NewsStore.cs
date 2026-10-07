using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SchedularrTV.News;

/// <summary>The news shows, kept in the plugin's data folder (<c>plugin-data/news/shows.json</c>).</summary>
public sealed partial class NewsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private IReadOnlyList<NewsShow> _shows;

    public NewsStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "shows.json");
        _shows = Load(_path);
    }

    /// <summary>Raised after the shows are saved.</summary>
    public event Action? Changed;

    public IReadOnlyList<NewsShow> Shows
    {
        get
        {
            lock (_gate)
            {
                return _shows;
            }
        }
    }

    public NewsShow? Find(string id) =>
        Shows.FirstOrDefault(show => string.Equals(show.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every show; ids are made unique and values tidied. Throws if the file can't be written.</summary>
    public void Save(IEnumerable<NewsShow> shows)
    {
        List<NewsShow> saved = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (NewsShow show in shows)
        {
            NewsShow copy = show.Copy();
            Tidy(copy);
            string id = string.IsNullOrWhiteSpace(copy.Id) ? NewsShow.Slug(copy.Name) : copy.Id;
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
            _shows = saved;
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps every value in a range the page, the sources and the stream can use.</summary>
    public static void Tidy(NewsShow show)
    {
        show.Name = string.IsNullOrWhiteSpace(show.Name) ? "Local News" : show.Name.Trim();
        show.AccentColor = IsColour(show.AccentColor) ? show.AccentColor : "#d7263d";
        show.Feeds = [.. show.Feeds
            .Select(feed => new NewsFeed { Url = feed.Url?.Trim() ?? string.Empty, Label = Trimmed(feed.Label) })
            .Where(feed => feed.Url.Length > 0)
            .DistinctBy(feed => feed.Url, StringComparer.OrdinalIgnoreCase)];
        show.StoryCount = Math.Clamp(show.StoryCount, 1, 40);
        show.MaxAgeHours = Math.Clamp(show.MaxAgeHours, 0, 24 * 14);
        show.Desks = [.. show.Desks.Where(desk => Enum.IsDefined(desk)).Distinct()];
        show.Latitude = Math.Clamp(show.Latitude, -90, 90);
        show.Longitude = Math.Clamp(show.Longitude, -180, 180);
        show.TimeZone = Trimmed(show.TimeZone);
        show.Location = show.Location?.Trim() ?? string.Empty;
        show.Symbols = [.. show.Symbols.Select(symbol => symbol.Trim().ToUpperInvariant()).Where(IsSymbol).Distinct().Take(12)];
        show.Leagues = [.. show.Leagues.Select(league => league.Trim().ToLowerInvariant()).Where(SportsLeagues.IsValid).Distinct().Take(6)];
        show.Lotteries = [.. show.Lotteries.Where(id => LotteryGames.Find(id) is not null).Select(id => LotteryGames.Find(id)!.Id).Distinct()];
        show.MapZoom = Math.Clamp(show.MapZoom, 6, 14);
        show.MapRadarOpacity = Math.Clamp(show.MapRadarOpacity, 0.2, 1);
        show.MapIncidentCount = Math.Clamp(show.MapIncidentCount, 1, 10);
        show.HighlightColor = IsColour(show.HighlightColor) ? show.HighlightColor : null;
        // The old lettering presets became font families.
        show.FontFamily = LiveFonts.Clean(show.FontFamily) ?? LiveFonts.FromPreset(show.Font.ToString());
        show.Font = NewsFont.Theme;
        show.TextScale = Math.Clamp(show.TextScale, 0.8, 1.4);
        show.TickerSpeed = Math.Clamp(show.TickerSpeed, 40, 240);
        show.LiveLabel = show.LiveLabel is null ? null : Limit(show.LiveLabel.Trim(), 16);
        show.TickerMessage = Limit(Trimmed(show.TickerMessage), 300);
        show.Intro = Limit(Trimmed(show.Intro), 300);
        show.Outro = Limit(Trimmed(show.Outro), 300);
        show.VoiceProvider = Trimmed(show.VoiceProvider);
        show.Voice = Trimmed(show.Voice);
        show.SpeechVolume = Math.Clamp(show.SpeechVolume, 0, 2);
        show.MusicFolder = Trimmed(show.MusicFolder)?.Trim('"');
        show.MusicVolume = Math.Clamp(show.MusicVolume, 0, 1);
        show.SecondsPerStory = Math.Clamp(show.SecondsPerStory, 4, 120);
        show.SecondsPerDesk = Math.Clamp(show.SecondsPerDesk, 4, 120);
        show.Lengths = [.. show.ValidLengths];
    }

    public static bool IsColour(string? value) => value is not null && HexColour().IsMatch(value);

    /// <summary>A Yahoo Finance symbol: letters, digits and ^ . - = only.</summary>
    public static bool IsSymbol(string value) =>
        value.Length is > 0 and <= 20 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '^' or '.' or '-' or '=');

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Limit(string? value, int length) => value is { } text && text.Length > length ? text[..length] : value;

    private static IReadOnlyList<NewsShow> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<NewsShow>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();
}
