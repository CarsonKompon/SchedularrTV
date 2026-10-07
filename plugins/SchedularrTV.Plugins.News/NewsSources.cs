using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace SchedularrTV.News;

/// <summary>A place found by name, for the weather and the map.</summary>
public sealed record Place(string Label, double Latitude, double Longitude, string? TimeZone);

/// <summary>
/// The free online sources a news show draws on, each answer kept for a few minutes so several shows (and the editor's
/// preview) asking for the same thing share one request:
/// <list type="bullet">
/// <item>RSS and Atom feeds.</item>
/// <item>Open-Meteo: the weather, and finding places by name.</item>
/// <item>Yahoo Finance's chart data: quotes, with the day's prices.</item>
/// <item>ESPN's scoreboards.</item>
/// <item>New York State's open data: Powerball, Mega Millions and NY Lotto results.</item>
/// <item>RainViewer: the latest rain radar.</item>
/// <item>TomTom (with a key): traffic incidents.</item>
/// </list>
/// </summary>
public sealed class NewsSources(IHttpClientFactory httpClients, TimeProvider clock)
{
    public const string ClientName = "news";

    /// <summary>
    /// For feeds and web pages: asks like a browser does (its User-Agent, languages, compressed answers), since some
    /// sites' networks (CBC's, for one) hold a request that doesn't look like one until it times out.
    /// </summary>
    public const string PageClientName = "news-pages";

    /// <summary>How long a last good answer is used in place of one that fails.</summary>
    private static readonly TimeSpan StaleFor = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The answer kept for <paramref name="key"/> if it's fresh, else a new one; when fetching fails and there's an
    /// answer from the last few hours, that's used instead, so a feed that's briefly slow doesn't drop out.
    /// </summary>
    private async Task<T> CachedAsync<T>(string key, TimeSpan life, Func<Task<T>> fetch) where T : notnull
    {
        DateTimeOffset now = clock.GetUtcNow();
        bool have = _cache.TryGetValue(key, out var cached) && cached.Value is T;
        if (have && now - cached.At < life)
        {
            return (T)cached.Value;
        }

        T fresh;
        try
        {
            fresh = await fetch().ConfigureAwait(false);
        }
        catch (Exception) when (have && now - cached.At < StaleFor)
        {
            return (T)cached.Value;
        }

        _cache[key] = (now, fresh);

        // Old answers don't pile up.
        if (_cache.Count > 500)
        {
            foreach (var stale in _cache.Where(entry => now - entry.Value.At > TimeSpan.FromHours(6)).ToList())
            {
                _cache.TryRemove(stale.Key, out _);
            }
        }

        return fresh;
    }

    private async Task<JsonDocument> JsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpClient http = httpClients.CreateClient(ClientName);
        await using Stream body = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // --- Feeds ---------------------------------------------------------------------------------------------------

    public Task<FeedContent> FeedAsync(NewsFeed feed, CancellationToken cancellationToken) =>
        CachedAsync($"feed|{feed.Url}|{feed.Label}", TimeSpan.FromMinutes(4), async () =>
            FeedReader.Parse(await PageAsync(feed.Url, cancellationToken).ConfigureAwait(false), feed.Label));

    private static bool Retryable(Exception ex) =>
        ex is TaskCanceledException || (ex is HttpRequestException failed && (failed.StatusCode is null || (int)failed.StatusCode >= 500));

    /// <summary>A page or feed's text, asked for as a browser would; tried a second time if the first try is too slow.</summary>
    private async Task<string> PageAsync(string url, CancellationToken cancellationToken)
    {
        using HttpClient http = httpClients.CreateClient(PageClientName);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 1 && !cancellationToken.IsCancellationRequested && Retryable(ex))
            {
                // A dropped connection, a time-out or a server error: once more after a moment.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // --- Places and weather --------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Place>> SearchPlacesAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
        {
            return [];
        }

        using JsonDocument json = await JsonAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language=en&format=json",
            cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("results", out JsonElement results))
        {
            return [];
        }

        return [.. results.EnumerateArray().Select(result => new Place(
            string.Join(", ", new[] { Str(result, "name"), Str(result, "admin1"), Str(result, "country") }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct()),
            Num(result, "latitude") ?? 0,
            Num(result, "longitude") ?? 0,
            Str(result, "timezone")))];
    }

    public Task<WeatherReport> WeatherAsync(NewsShow show, CancellationToken cancellationToken)
    {
        string lat = show.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        string lon = show.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        string units = show.Imperial ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
        string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}"
                     + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m,wind_direction_10m"
                     + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                     + $"&timezone=auto&forecast_days=6{units}";
        return CachedAsync("weather|" + url, TimeSpan.FromMinutes(10), async () =>
        {
            using JsonDocument json = await JsonAsync(url, cancellationToken).ConfigureAwait(false);
            return Weather.Read(json.RootElement, show.Location, show.Imperial);
        });
    }

    // --- Markets -------------------------------------------------------------------------------------------------

    public Task<Quote> QuoteAsync(string symbol, CancellationToken cancellationToken) =>
        CachedAsync("quote|" + symbol, TimeSpan.FromMinutes(5), async () =>
        {
            using JsonDocument json = await JsonAsync(
                $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1d&interval=15m", cancellationToken).ConfigureAwait(false);
            return Markets.Read(json.RootElement, symbol);
        });

    // --- Sports --------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<ScoreGame>> ScoresAsync(string league, CancellationToken cancellationToken) =>
        CachedAsync("scores|" + league, TimeSpan.FromMinutes(3), async () =>
        {
            using JsonDocument json = await JsonAsync($"https://site.api.espn.com/apis/site/v2/sports/{league}/scoreboard", cancellationToken).ConfigureAwait(false);
            return Sports.Read(json.RootElement, SportsLeagues.NameOf(league));
        });

    // --- Lottery -------------------------------------------------------------------------------------------------

    public Task<LotteryDraw> LotteryAsync(LotteryGames.Game game, CancellationToken cancellationToken) =>
        CachedAsync("lottery|" + game.Id, TimeSpan.FromMinutes(30), async () =>
        {
            if (game.Source == LotterySource.Wclc)
            {
                return Lottery.ReadWclc(await PageAsync(game.Address, cancellationToken).ConfigureAwait(false), game);
            }

            using JsonDocument json = await JsonAsync(
                $"https://data.ny.gov/resource/{game.Address}.json?$order=draw_date%20DESC&$limit=1", cancellationToken).ConfigureAwait(false);
            JsonElement row = json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0
                ? json.RootElement[0]
                : throw new InvalidOperationException($"No {game.Name} results.");
            return Lottery.Read(row, game);
        });

    // --- Radar and traffic ---------------------------------------------------------------------------------------

    /// <summary>One radar picture: when it was taken (Unix seconds) and its tile address, with {z}/{x}/{y} to fill in.</summary>
    public sealed record RadarFrame(long Time, string Template);

    /// <summary>The radar pictures of the last hour or so, oldest first.</summary>
    public Task<IReadOnlyList<RadarFrame>> RadarFramesAsync(CancellationToken cancellationToken) =>
        CachedAsync("radar", TimeSpan.FromMinutes(5), async () =>
        {
            using JsonDocument json = await JsonAsync("https://api.rainviewer.com/public/weather-maps.json", cancellationToken).ConfigureAwait(false);
            string host = Str(json.RootElement, "host") ?? "https://tilecache.rainviewer.com";
            JsonElement past = Path(json.RootElement, "radar", "past");
            if (past.ValueKind != JsonValueKind.Array || past.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("No radar pictures.");
            }

            // 256 px tiles, colour scheme 2 (universal blue), smoothed, with snow shown.
            IReadOnlyList<RadarFrame> frames = [.. past.EnumerateArray()
                .Where(frame => Str(frame, "path") is not null)
                .Select(frame => new RadarFrame((long)(Num(frame, "time") ?? 0), $"{host}{Str(frame, "path")}/256/{{z}}/{{x}}/{{y}}/2/1_1.png"))
                .TakeLast(7)];
            return frames;
        });

    /// <summary>The tile address of the radar picture taken at <paramref name="time"/> (Unix seconds), or the latest one.</summary>
    public async Task<string> RadarTemplateAsync(long? time, CancellationToken cancellationToken)
    {
        IReadOnlyList<RadarFrame> frames = await RadarFramesAsync(cancellationToken).ConfigureAwait(false);
        return (frames.FirstOrDefault(frame => frame.Time == time) ?? frames[^1]).Template;
    }

    public Task<IReadOnlyList<TrafficIncident>> IncidentsAsync(string key, double latitude, double longitude, int zoom, CancellationToken cancellationToken)
    {
        // TomTom answers 400 for an area over 10,000 km², which a city at zoom 10 already is: the middle of the picture is asked about.
        (double west, double south, double east, double north) = MapMath.Within(MapMath.Bounds(latitude, longitude, zoom, 1280, 720), 9_000);
        string bbox = string.Join(',', new[] { west, south, east, north }.Select(value => value.ToString("0.#####", CultureInfo.InvariantCulture)));
        const string fields = "{incidents{type,geometry{type,coordinates},properties{iconCategory,magnitudeOfDelay,events{description},from,to,delay,roadNumbers}}}";
        string url = $"https://api.tomtom.com/traffic/services/5/incidentDetails?key={Uri.EscapeDataString(key)}&bbox={bbox}"
                     + $"&fields={Uri.EscapeDataString(fields)}&language=en-GB&timeValidityFilter=present";
        return CachedAsync("traffic|" + url, TimeSpan.FromMinutes(5), async () =>
        {
            using JsonDocument json = await JsonAsync(url, cancellationToken).ConfigureAwait(false);
            return Traffic.Read(json.RootElement);
        });
    }

    // --- JSON helpers --------------------------------------------------------------------------------------------

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    internal static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
                _ => null
            }
            : null;

    internal static JsonElement Path(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return default;
            }
        }

        return element;
    }
}

/// <summary>Web Mercator sums for the map: which part of the world a 1280×720 picture at a zoom covers.</summary>
public static class MapMath
{
    public static (double X, double Y) ToPixel(double latitude, double longitude, int zoom)
    {
        double scale = 256 * Math.Pow(2, zoom);
        double sin = Math.Sin(Math.Clamp(latitude, -85.05, 85.05) * Math.PI / 180);
        return ((longitude + 180) / 360 * scale, (0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI)) * scale);
    }

    public static (double Latitude, double Longitude) FromPixel(double x, double y, int zoom)
    {
        double scale = 256 * Math.Pow(2, zoom);
        double longitude = x / scale * 360 - 180;
        double n = Math.PI - 2 * Math.PI * y / scale;
        return (180 / Math.PI * Math.Atan(Math.Sinh(n)), longitude);
    }

    public static (double West, double South, double East, double North) Bounds(double latitude, double longitude, int zoom, int width, int height)
    {
        (double x, double y) = ToPixel(latitude, longitude, zoom);
        (double north, double west) = FromPixel(x - width / 2.0, y - height / 2.0, zoom);
        (double south, double east) = FromPixel(x + width / 2.0, y + height / 2.0, zoom);
        return (west, south, east, north);
    }

    /// <summary>A box's area in square kilometres (near enough, for boxes a city across).</summary>
    public static double AreaKm2((double West, double South, double East, double North) box)
    {
        const double KmPerDegree = 111.32;
        double middle = (box.North + box.South) / 2 * Math.PI / 180;
        return (box.East - box.West) * KmPerDegree * Math.Cos(middle) * (box.North - box.South) * KmPerDegree;
    }

    /// <summary>The box shrunk about its middle, keeping its shape, until it's at most <paramref name="km2"/>.</summary>
    public static (double West, double South, double East, double North) Within((double West, double South, double East, double North) box, double km2)
    {
        double area = AreaKm2(box);
        if (area <= km2)
        {
            return box;
        }

        double factor = Math.Sqrt(km2 / area) / 2;
        double centreX = (box.West + box.East) / 2, centreY = (box.South + box.North) / 2;
        double halfWidth = (box.East - box.West) * factor, halfHeight = (box.North - box.South) * factor;
        return (centreX - halfWidth, centreY - halfHeight, centreX + halfWidth, centreY + halfHeight);
    }
}

/// <summary>Open-Meteo's forecast, read into a report, with WMO weather codes turned into words and an icon name.</summary>
public static class Weather
{
    public static WeatherReport Read(JsonElement root, string place, bool imperial)
    {
        JsonElement current = NewsSources.Path(root, "current");
        int code = (int)(NewsSources.Num(current, "weather_code") ?? 0);
        bool day = (NewsSources.Num(current, "is_day") ?? 1) > 0;

        List<WeatherDay> days = [];
        JsonElement daily = NewsSources.Path(root, "daily");
        if (daily.ValueKind == JsonValueKind.Object && daily.TryGetProperty("time", out JsonElement times))
        {
            for (int index = 0; index < times.GetArrayLength(); index++)
            {
                int dayCode = (int)(At(daily, "weather_code", index) ?? 0);
                days.Add(new WeatherDay(
                    times[index].GetString() ?? "",
                    dayCode,
                    Icon(dayCode, true),
                    Condition(dayCode),
                    Math.Round(At(daily, "temperature_2m_max", index) ?? 0),
                    Math.Round(At(daily, "temperature_2m_min", index) ?? 0),
                    At(daily, "precipitation_probability_max", index) is { } chance ? (int)chance : null));
            }
        }

        return new WeatherReport(
            place,
            Math.Round(NewsSources.Num(current, "temperature_2m") ?? 0),
            Math.Round(NewsSources.Num(current, "apparent_temperature") ?? 0),
            (int)(NewsSources.Num(current, "relative_humidity_2m") ?? 0),
            Math.Round(NewsSources.Num(current, "wind_speed_10m") ?? 0),
            Compass(NewsSources.Num(current, "wind_direction_10m") ?? 0),
            code,
            Icon(code, day),
            Condition(code),
            day,
            days,
            imperial ? "°F" : "°C",
            imperial ? "mph" : "km/h");
    }

    private static double? At(JsonElement daily, string name, int index) =>
        daily.TryGetProperty(name, out JsonElement values) && index < values.GetArrayLength() && values[index].ValueKind == JsonValueKind.Number
            ? values[index].GetDouble()
            : null;

    public static string Compass(double degrees) =>
        new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Math.Round(((degrees % 360) + 360) % 360 / 45) % 8];

    /// <summary>The page's icon for a WMO code: sun, moon, partly, cloud, fog, drizzle, rain, snow, storm.</summary>
    public static string Icon(int code, bool day) => code switch
    {
        0 or 1 => day ? "sun" : "moon",
        2 => "partly",
        3 => "cloud",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "rain",
        71 or 73 or 75 or 77 or 85 or 86 => "snow",
        95 or 96 or 99 => "storm",
        _ => "cloud"
    };

    public static string Condition(int code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorms",
        96 or 99 => "Thunderstorms with hail",
        _ => "Cloudy"
    };
}

/// <summary>Yahoo Finance's chart answer, read into a quote.</summary>
public static class Markets
{
    private static readonly Dictionary<string, string> IndexNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["^GSPC"] = "S&P 500", ["^DJI"] = "Dow Jones", ["^IXIC"] = "Nasdaq", ["^RUT"] = "Russell 2000",
        ["^GSPTSE"] = "S&P/TSX", ["^FTSE"] = "FTSE 100", ["^N225"] = "Nikkei 225", ["^GDAXI"] = "DAX",
        ["^FCHI"] = "CAC 40", ["^HSI"] = "Hang Seng", ["^AXJO"] = "ASX 200", ["^VIX"] = "VIX",
        ["BTC-USD"] = "Bitcoin", ["ETH-USD"] = "Ether", ["GC=F"] = "Gold", ["CL=F"] = "Crude oil"
    };

    public static Quote Read(JsonElement root, string symbol)
    {
        JsonElement result = NewsSources.Path(root, "chart", "result");
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            string? said = NewsSources.Str(NewsSources.Path(root, "chart", "error"), "description");
            throw new InvalidOperationException(said ?? $"No quote for {symbol}.");
        }

        JsonElement first = result[0];
        JsonElement meta = NewsSources.Path(first, "meta");
        double price = NewsSources.Num(meta, "regularMarketPrice") ?? throw new InvalidOperationException($"No price for {symbol}.");
        double previous = NewsSources.Num(meta, "chartPreviousClose") ?? NewsSources.Num(meta, "previousClose") ?? price;

        List<double> spark = [];
        JsonElement quotes = NewsSources.Path(first, "indicators", "quote");
        if (quotes.ValueKind == JsonValueKind.Array && quotes.GetArrayLength() > 0 && quotes[0].TryGetProperty("close", out JsonElement closes))
        {
            spark.AddRange(closes.EnumerateArray().Where(close => close.ValueKind == JsonValueKind.Number).Select(close => Math.Round(close.GetDouble(), 4)));
        }

        string name = IndexNames.TryGetValue(symbol, out string? known) ? known
            : NewsSources.Str(meta, "shortName") ?? NewsSources.Str(meta, "longName") ?? symbol;
        double change = price - previous;
        return new Quote(symbol, name, Math.Round(price, 2), Math.Round(change, 2), previous == 0 ? 0 : Math.Round(change / previous * 100, 2),
            NewsSources.Str(meta, "currency"), spark);
    }
}

/// <summary>ESPN's scoreboard, read into games: live ones first, then finished, then those still to come.</summary>
public static class Sports
{
    public static IReadOnlyList<ScoreGame> Read(JsonElement root, string league)
    {
        List<ScoreGame> games = [];
        if (!root.TryGetProperty("events", out JsonElement events))
        {
            return games;
        }

        foreach (JsonElement game in events.EnumerateArray())
        {
            JsonElement competitions = NewsSources.Path(game, "competitions");
            if (competitions.ValueKind != JsonValueKind.Array || competitions.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement competition = competitions[0];
            ScoreTeam? home = null, away = null;
            if (competition.TryGetProperty("competitors", out JsonElement competitors))
            {
                foreach (JsonElement competitor in competitors.EnumerateArray())
                {
                    JsonElement team = NewsSources.Path(competitor, "team");
                    var entry = new ScoreTeam(
                        NewsSources.Str(team, "abbreviation") ?? "",
                        NewsSources.Str(team, "shortDisplayName") ?? NewsSources.Str(team, "displayName") ?? "",
                        NewsSources.Str(competitor, "score"),
                        "#" + (NewsSources.Str(team, "color") is { Length: 6 } colour ? colour : "555555"),
                        NewsSources.Str(team, "logo"),
                        competitor.TryGetProperty("winner", out JsonElement winner) && winner.ValueKind == JsonValueKind.True);
                    if (NewsSources.Str(competitor, "homeAway") == "home")
                    {
                        home = entry;
                    }
                    else
                    {
                        away = entry;
                    }
                }
            }

            if (home is null || away is null)
            {
                continue;
            }

            JsonElement type = NewsSources.Path(game, "status", "type");
            string state = NewsSources.Str(type, "state") ?? "pre";
            games.Add(new ScoreGame(league, state, NewsSources.Str(type, "shortDetail") ?? "", away, home));
        }

        return [.. games.OrderBy(game => game.State switch { "in" => 0, "post" => 1, _ => 2 }).Take(10)];
    }
}

/// <summary>A row of New York State's lottery results, read into a draw.</summary>
public static class Lottery
{
    public static LotteryDraw Read(JsonElement row, LotteryGames.Game game)
    {
        int[] numbers = [.. (NewsSources.Str(row, "winning_numbers") ?? "")
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : -1)
            .Where(number => number >= 0)];
        if (numbers.Length < game.MainBalls)
        {
            throw new InvalidOperationException($"{game.Name}'s latest result couldn't be read.");
        }

        int? bonus = numbers.Length > game.MainBalls ? numbers[game.MainBalls] : null;
        foreach (string field in new[] { "mega_ball", "bonus", "cash_ball", "powerball" })
        {
            if (bonus is null && int.TryParse(NewsSources.Str(row, field), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                bonus = value;
            }
        }

        string? date = NewsSources.Str(row, "draw_date") is { Length: >= 10 } drawn ? drawn[..10] : null;
        string? multiplier = NewsSources.Str(row, "multiplier") is { Length: > 0 } times ? times.TrimStart('0') : null;
        return new LotteryDraw(game.Id, game.Name, date, numbers[..game.MainBalls], bonus, game.BonusName,
            string.IsNullOrEmpty(multiplier) ? null : multiplier, game.Colour);
    }

    private static readonly System.Text.RegularExpressions.Regex WclcDate = new(
        @"^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+([A-Za-z]+\s+\d{1,2},\s+\d{4})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex WclcBonus = new(
        @"^(?:Bonus|Grand\s*Number)\s*:?\s*(\d{1,2})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The latest draw on one of WCLC's winning-numbers pages: the first date heading ("Friday, October 02, 2026"), the
    /// numbers after it, then "Bonus 26" (or the Grand Number). The page's markup is read as lines of text, so it
    /// doesn't depend on its tags or classes.
    /// </summary>
    public static LotteryDraw ReadWclc(string html, LotteryGames.Game game)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "\n");
        string[] lines = [.. System.Net.WebUtility.HtmlDecode(text).Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => line.Length > 0)];

        for (int start = 0; start < lines.Length; start++)
        {
            System.Text.RegularExpressions.Match heading = WclcDate.Match(lines[start]);
            if (!heading.Success)
            {
                continue;
            }

            List<int> numbers = [];
            int? bonus = null;
            for (int at = start + 1; at < lines.Length && at < start + 40; at++)
            {
                string line = lines[at];
                if (WclcDate.IsMatch(line))
                {
                    break;
                }

                if (numbers.Count < game.MainBalls)
                {
                    if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number is > 0 and < 100)
                    {
                        numbers.Add(number);
                    }

                    continue;
                }

                // "Bonus26", or "Bonus" on one line and "26" on the next.
                System.Text.RegularExpressions.Match label = WclcBonus.Match(line);
                if (label.Success)
                {
                    if (label.Groups[1].Success)
                    {
                        bonus = int.Parse(label.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    else if (at + 1 < lines.Length && int.TryParse(lines[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int next))
                    {
                        bonus = next;
                    }

                    break;
                }

                if (!int.TryParse(line, out _))
                {
                    // Something else (a "Gold Ball draw", "MAXPLUS" heading): there's no bonus to find.
                    break;
                }
            }

            if (numbers.Count == game.MainBalls)
            {
                string? date = DateTime.TryParseExact(heading.Groups[2].Value, ["MMMM dd, yyyy", "MMMM d, yyyy"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime drawn) ? drawn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                return new LotteryDraw(game.Id, game.Name, date, numbers, bonus, game.BonusName, null, game.Colour);
            }
        }

        throw new InvalidOperationException($"{game.Name}'s latest result couldn't be found on its page.");
    }
}

/// <summary>TomTom's incident details, read into the incidents worth showing (worst first).</summary>
public static class Traffic
{
    public static IReadOnlyList<TrafficIncident> Read(JsonElement root)
    {
        List<TrafficIncident> incidents = [];
        if (!root.TryGetProperty("incidents", out JsonElement all))
        {
            return incidents;
        }

        foreach (JsonElement incident in all.EnumerateArray())
        {
            JsonElement properties = NewsSources.Path(incident, "properties");
            JsonElement coordinates = NewsSources.Path(incident, "geometry", "coordinates");
            if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
            {
                continue;
            }

            // A point is [lon, lat]; a line is a list of them (its first point marks it).
            JsonElement point = coordinates[0].ValueKind == JsonValueKind.Array ? coordinates[0] : coordinates;
            if (point.GetArrayLength() < 2)
            {
                continue;
            }

            string description = properties.TryGetProperty("events", out JsonElement events) && events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > 0
                ? NewsSources.Str(events[0], "description") ?? ""
                : "";
            string road = properties.TryGetProperty("roadNumbers", out JsonElement roads) && roads.ValueKind == JsonValueKind.Array && roads.GetArrayLength() > 0
                ? string.Join(", ", roads.EnumerateArray().Select(number => number.GetString()).Where(number => !string.IsNullOrEmpty(number)))
                : NewsSources.Str(properties, "from") ?? "";
            incidents.Add(new TrafficIncident(
                Category((int)(NewsSources.Num(properties, "iconCategory") ?? 0)),
                description,
                road,
                (int)(NewsSources.Num(properties, "magnitudeOfDelay") ?? 0),
                (int)(NewsSources.Num(properties, "delay") ?? 0),
                point[1].GetDouble(),
                point[0].GetDouble()));
        }

        return [.. incidents.OrderByDescending(incident => incident.Magnitude).ThenByDescending(incident => incident.DelaySeconds).Take(12)];
    }

    public static string Category(int icon) => icon switch
    {
        1 => "Accident",
        2 or 3 or 4 or 5 or 10 or 11 => "Hazard",
        6 => "Jam",
        7 => "Lane closed",
        8 => "Road closed",
        9 => "Road works",
        14 => "Broken-down vehicle",
        _ => "Incident"
    };
}
