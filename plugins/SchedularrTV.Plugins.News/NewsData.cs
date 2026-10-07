namespace SchedularrTV.News;

// What the page is sent (as camelCase JSON) and the rundown is written from.

/// <summary>A story from a feed: its headline, a plain-text summary, its picture (if any) and where it's from.</summary>
public sealed record Story(string Title, string Summary, string? Link, string? Image, string Source, DateTimeOffset? Published);

/// <summary>A day of the forecast. <paramref name="Date"/> is yyyy-MM-dd, in the place's own time.</summary>
public sealed record WeatherDay(string Date, int Code, string Icon, string Condition, double High, double Low, int? RainChance);

/// <summary>The weather now and the days ahead, in the show's units.</summary>
public sealed record WeatherReport(
    string Place,
    double Temperature,
    double FeelsLike,
    int Humidity,
    double Wind,
    string WindFrom,
    int Code,
    string Icon,
    string Condition,
    bool IsDay,
    IReadOnlyList<WeatherDay> Days,
    string TemperatureUnit,
    string WindUnit);

/// <summary>A market quote, with the day's prices for a sparkline.</summary>
public sealed record Quote(string Symbol, string Name, double Price, double Change, double ChangePercent, string? Currency, IReadOnlyList<double> Spark);

public sealed record ScoreTeam(string Abbreviation, string Name, string? Score, string Colour, string? Logo, bool Winner);

/// <summary>A game: <paramref name="State"/> is pre, in or post; <paramref name="Status"/> is ESPN's short line ("Final", "Q3 4:12", "7:30 PM").</summary>
public sealed record ScoreGame(string League, string State, string Status, ScoreTeam Away, ScoreTeam Home);

/// <summary>A lottery draw. <paramref name="Date"/> is yyyy-MM-dd.</summary>
public sealed record LotteryDraw(string Game, string Name, string? Date, IReadOnlyList<int> Numbers, int? Bonus, string BonusName, string? Multiplier, string Colour);

/// <summary>A traffic incident on the map. <paramref name="Magnitude"/>: 0 unknown, 1 minor to 4 closed.</summary>
public sealed record TrafficIncident(string Category, string Description, string Road, int Magnitude, int DelaySeconds, double Latitude, double Longitude);

/// <summary>
/// The map segment: where it's centred, and what's drawn over it. <paramref name="Traffic"/> is whether there's any traffic
/// (flow colours or incidents); <paramref name="RadarFrames"/> are the radar pictures' times (Unix seconds), oldest first.
/// </summary>
public sealed record MapReport(
    string Place,
    double Latitude,
    double Longitude,
    int Zoom,
    bool Radar,
    bool Traffic,
    bool TrafficFlow,
    IReadOnlyList<TrafficIncident> Incidents,
    IReadOnlyList<long> RadarFrames);

/// <summary>Everything fetched for a show at one time, with what couldn't be.</summary>
public sealed record NewsSnapshot(
    DateTimeOffset FetchedAt,
    IReadOnlyList<Story> Stories,
    WeatherReport? Weather,
    IReadOnlyList<Quote> Quotes,
    IReadOnlyList<ScoreGame> Scores,
    IReadOnlyList<LotteryDraw> Lottery,
    MapReport? Map,
    IReadOnlyList<string> Problems)
{
    public static NewsSnapshot Empty(DateTimeOffset at) => new(at, [], null, [], [], [], null, []);
}
