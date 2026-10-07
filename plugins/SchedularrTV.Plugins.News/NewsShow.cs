using System.Text;
using System.Text.Json.Serialization;

namespace SchedularrTV.News;

/// <summary>How a news show looks.</summary>
public enum NewsTheme
{
    /// <summary>Blocky Ceefax-style pages: page numbers, coloured bars, mosaic graphics, a Fastext row.</summary>
    Teletext,

    /// <summary>A modern broadcast: lower-third headlines, the story's picture, a corner bug and a ticker.</summary>
    NewsDesk,

    /// <summary>Busy 24-hour cable news: a BREAKING banner, split panels, a crawl and a markets strip.</summary>
    CableNews,

    /// <summary>An 80s/90s local station: chunky gradients, serif titles, a VHS feel.</summary>
    RetroLocal
}

/// <summary>How much of each story is read out.</summary>
public enum SpeechMode
{
    Off,

    /// <summary>Just the headlines (and a line for each data segment).</summary>
    Headlines,

    /// <summary>The headline and the first sentence or two of the summary.</summary>
    HeadlineAndSummary,

    /// <summary>The headline and the whole summary, up to a limit.</summary>
    FullSummary
}

/// <summary>A segment besides the stories, each from a free online source.</summary>
public enum NewsDesk
{
    Weather,
    Markets,
    Sports,
    Map,
    Lottery
}

/// <summary>The lettering, besides the look's own (teletext always keeps its own).</summary>
public enum NewsFont
{
    Theme,
    Sans,
    Condensed,
    Serif,
    Slab,
    Rounded,
    Mono
}

/// <summary>A size for the ticker or the side panel.</summary>
public enum NewsSize
{
    Small,
    Normal,
    Large
}

/// <summary>What the map is drawn on.</summary>
public enum MapStyle
{
    /// <summary>The look's own: dark streets for the news desk and cable news, warm streets for the 80s look.</summary>
    Auto,
    Streets,
    Light,
    Dark,
    Satellite
}

/// <summary>An RSS or Atom feed, with what to call it on screen.</summary>
public sealed class NewsFeed
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Shown as the story's source, e.g. "BBC News"; empty to use the feed's own title.</summary>
    public string? Label { get; set; }

    public NewsFeed Copy() => (NewsFeed)MemberwiseClone();
}

/// <summary>
/// One news show: where its stories come from, which data segments it carries, how it looks and sounds, and the lengths
/// it can be scheduled for. Each show is a "virtual show" (its name) with one "episode" per length.
/// </summary>
public sealed class NewsShow
{
    /// <summary>Stable id, used in item ids and the stream address; never changes once created.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The show's title, e.g. "Channel 7 News".</summary>
    public string Name { get; set; } = string.Empty;

    public NewsTheme Theme { get; set; } = NewsTheme.NewsDesk;

    /// <summary>The station's colour (bars, the bug, the ticker), as #rrggbb.</summary>
    public string AccentColor { get; set; } = "#d7263d";

    /// <summary>A second colour (cable news's yellow, the 80s look's gold, the news desk's tags); null for the look's own.</summary>
    public string? HighlightColor { get; set; }

    /// <summary>Superseded by <see cref="FontFamily"/>; read from older saves and carried over by <see cref="NewsStore.Tidy"/>.</summary>
    public NewsFont Font { get; set; } = NewsFont.Theme;

    /// <summary>The lettering: an installed font or one of the web fonts (<see cref="LiveFonts.Web"/>); null for the look's own.</summary>
    public string? FontFamily { get; set; }

    /// <summary>How big the headlines and text are, 0.8 to 1.4.</summary>
    public double TextScale { get; set; } = 1.0;

    public NewsSize TickerSize { get; set; } = NewsSize.Normal;

    /// <summary>How fast the ticker moves, in pixels a second (40 to 240).</summary>
    public int TickerSpeed { get; set; } = 110;

    public NewsSize SidePanelSize { get; set; } = NewsSize.Normal;

    /// <summary>CRT scan lines over the whole picture.</summary>
    public bool ScanLines { get; set; }

    public bool ShowClock { get; set; } = true;

    /// <summary>The word in the corner ("LIVE"); empty to leave it out, null for the look's own.</summary>
    public string? LiveLabel { get; set; }

    // --- Stories -------------------------------------------------------------------------------------------------

    public List<NewsFeed> Feeds { get; set; } = [];

    /// <summary>At most this many stories each time round (newest first, taken in turn from each feed).</summary>
    public int StoryCount { get; set; } = 8;

    /// <summary>Stories older than this many hours are left out; 0 keeps them all.</summary>
    public int MaxAgeHours { get; set; } = 24;

    /// <summary>Show the story's picture, where the feed has one (not in the teletext look).</summary>
    public bool ShowImages { get; set; } = true;

    // --- Desks (data segments) -----------------------------------------------------------------------------------

    /// <summary>The data segments, in the order they come between the stories.</summary>
    public List<NewsDesk> Desks { get; set; } = [NewsDesk.Weather, NewsDesk.Sports, NewsDesk.Markets];

    /// <summary>Also show the data in a side panel (and the ticker) while stories are on.</summary>
    public bool ShowSidebar { get; set; } = true;

    /// <summary>The place for the weather and the map, as shown, e.g. "Toronto, Ontario".</summary>
    public string Location { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>The place's IANA time zone, for the clock; empty for this computer's.</summary>
    public string? TimeZone { get; set; }

    /// <summary>°F and mph instead of °C and km/h.</summary>
    public bool Imperial { get; set; }

    public bool TwelveHourClock { get; set; }

    /// <summary>Yahoo Finance symbols, e.g. ^GSPC (S&amp;P 500), ^DJI, ^IXIC, AAPL, BTC-USD, CADUSD=X.</summary>
    public List<string> Symbols { get; set; } = ["^GSPC", "^DJI", "^IXIC"];

    /// <summary>ESPN leagues as sport/league, e.g. basketball/nba, hockey/nhl, soccer/eng.1.</summary>
    public List<string> Leagues { get; set; } = ["basketball/nba", "hockey/nhl"];

    /// <summary>Lottery games (<see cref="LotteryGames"/> ids), e.g. powerball, megamillions.</summary>
    public List<string> Lotteries { get; set; } = ["powerball", "megamillions"];

    /// <summary>Map zoom, 6 (a region) to 14 (streets).</summary>
    public int MapZoom { get; set; } = 10;

    /// <summary>Rain radar over the map.</summary>
    public bool MapRadar { get; set; } = true;

    public MapStyle MapStyle { get; set; } = MapStyle.Auto;

    /// <summary>The radar plays its last hour as a loop, rather than only the latest picture.</summary>
    public bool MapRadarLoop { get; set; } = true;

    /// <summary>0.2 to 1.</summary>
    public double MapRadarOpacity { get; set; } = 0.7;

    /// <summary>Traffic flow colours on the roads (with a TomTom key).</summary>
    public bool MapTrafficFlow { get; set; } = true;

    /// <summary>Numbered traffic incidents, on the map and listed (with a TomTom key).</summary>
    public bool MapIncidents { get; set; } = true;

    /// <summary>How many incidents are shown, 1 to 10.</summary>
    public int MapIncidentCount { get; set; } = 6;

    /// <summary>The map drifts slowly while it's on, like a camera move.</summary>
    public bool MapDrift { get; set; } = true;

    /// <summary>A dot on the place itself.</summary>
    public bool MapMarker { get; set; } = true;

    /// <summary>Whether traffic is on the map: wanted, and the key to fetch it is set.</summary>
    public bool WantsTraffic(bool haveKey) => haveKey && (MapTrafficFlow || MapIncidents);

    // --- Ticker and wording --------------------------------------------------------------------------------------

    public bool ShowTicker { get; set; } = true;

    /// <summary>A line of your own at the front of the ticker; empty for none.</summary>
    public string? TickerMessage { get; set; }

    /// <summary>The opening line, read and shown; empty for "This is {name}. Here are the headlines."</summary>
    public string? Intro { get; set; }

    /// <summary>The closing line; empty for "That's the news for now. Stay with us."</summary>
    public string? Outro { get; set; }

    // --- Sound ---------------------------------------------------------------------------------------------------

    public SpeechMode Speech { get; set; } = SpeechMode.Off;

    /// <summary>The voice's provider (a voice plugin's id: piper, tiktok, minimax…).</summary>
    public string? VoiceProvider { get; set; }

    public string? Voice { get; set; }

    /// <summary>Read the data segments too (the weather, the scores…), not just the stories.</summary>
    public bool ReadDesks { get; set; } = true;

    /// <summary>0 to 2.</summary>
    public double SpeechVolume { get; set; } = 1.0;

    /// <summary>Music shuffled under the news (a folder, or any music library); none plays no music.</summary>
    public SchedularrTV.Core.Media.BackgroundMusic? Music { get; set; }

    /// <summary>The music folder from earlier versions; moved into <see cref="Music"/> when the shows are read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MusicFolder { get; set; }

    /// <summary>0 to 1. Lowered under the voice.</summary>
    public double MusicVolume { get; set; } = 0.35;

    // --- Pace and scheduling -------------------------------------------------------------------------------------

    /// <summary>How long a story stays up when it isn't read out (or its voice-over couldn't be made).</summary>
    public int SecondsPerStory { get; set; } = 12;

    /// <summary>How long a data segment stays up when it isn't read out.</summary>
    public int SecondsPerDesk { get; set; } = 15;

    /// <summary>The lengths, in minutes, this show can be scheduled for; each becomes an "episode".</summary>
    public List<int> Lengths { get; set; } = [2, 3, 5, 10, 30, 60];

    [JsonIgnore]
    public bool HasPlace => Latitude != 0 || Longitude != 0;

    [JsonIgnore]
    public bool Speaks => Speech != SpeechMode.Off && !string.IsNullOrWhiteSpace(VoiceProvider) && !string.IsNullOrWhiteSpace(Voice);

    /// <summary>Lengths that can be scheduled: whole minutes from 1 to 24 hours, once each, shortest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> ValidLengths => [.. Lengths.Where(minutes => minutes is > 0 and <= 1440).Distinct().Order()];

    /// <summary>Feeds with a usable http(s) address.</summary>
    [JsonIgnore]
    public IReadOnlyList<NewsFeed> ValidFeeds => [.. Feeds.Where(feed => IsWebAddress(feed.Url))];

    public NewsShow Copy()
    {
        var copy = (NewsShow)MemberwiseClone();
        copy.Feeds = [.. Feeds.Select(feed => feed.Copy())];
        copy.Desks = [.. Desks];
        copy.Symbols = [.. Symbols];
        copy.Leagues = [.. Leagues];
        copy.Lotteries = [.. Lotteries];
        copy.Lengths = [.. Lengths];
        copy.Music = Music?.Copy();
        return copy;
    }

    public static bool IsWebAddress(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A safe, lowercase id from a name: letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (char character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string result = slug.ToString().Trim('-');
        return result.Length == 0 ? "news" : result.Length > 40 ? result[..40].TrimEnd('-') : result;
    }
}

/// <summary>Where a lottery's results are read from.</summary>
public enum LotterySource
{
    /// <summary>New York State's open data (JSON); the address is the dataset's id.</summary>
    NewYork,

    /// <summary>The Western Canada Lottery Corporation's winning numbers page (official, national games); the address is the page.</summary>
    Wclc
}

/// <summary>The lottery games the plugin knows, with where their results come from (all free, no keys).</summary>
public static class LotteryGames
{
    /// <param name="MainBalls">How many numbers are main balls; any after them are the bonus ball.</param>
    public sealed record Game(string Id, string Name, string Country, LotterySource Source, string Address, int MainBalls, string BonusName, string Colour);

    public static readonly IReadOnlyList<Game> All =
    [
        // winning_numbers holds the five numbers and the Powerball last.
        new("powerball", "Powerball", "US", LotterySource.NewYork, "d6yy-54nr", 5, "Powerball", "#e4252c"),
        // winning_numbers holds the five numbers; mega_ball the Mega Ball.
        new("megamillions", "Mega Millions", "US", LotterySource.NewYork, "5xaw-6ayf", 5, "Mega Ball", "#f2b705"),
        // winning_numbers holds the six numbers; bonus the bonus number.
        new("nylotto", "NY Lotto", "US", LotterySource.NewYork, "6nbc-h7bj", 6, "Bonus", "#1c6ee8"),
        new("lottomax", "Lotto Max", "Canada", LotterySource.Wclc, "https://www.wclc.com/winning-numbers/lotto-max-extra.htm", 7, "Bonus", "#7b2cbf"),
        new("lotto649", "Lotto 6/49", "Canada", LotterySource.Wclc, "https://www.wclc.com/winning-numbers/lotto-649-extra.htm", 6, "Bonus", "#0057b8"),
        new("dailygrand", "Daily Grand", "Canada", LotterySource.Wclc, "https://www.wclc.com/winning-numbers/daily-grand-extra.htm", 5, "Grand Number", "#c9a227")
    ];

    public static Game? Find(string id) => All.FirstOrDefault(game => string.Equals(game.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>ESPN leagues offered in the editor; any other sport/league ESPN has can be typed in.</summary>
public static class SportsLeagues
{
    public static readonly IReadOnlyList<(string Id, string Name)> Common =
    [
        ("football/nfl", "NFL"),
        ("basketball/nba", "NBA"),
        ("baseball/mlb", "MLB"),
        ("hockey/nhl", "NHL"),
        ("basketball/wnba", "WNBA"),
        ("football/college-football", "College football"),
        ("basketball/mens-college-basketball", "College basketball"),
        ("soccer/eng.1", "Premier League"),
        ("soccer/usa.1", "MLS"),
        ("soccer/esp.1", "LaLiga"),
        ("soccer/uefa.champions", "Champions League"),
        ("football/cfl", "CFL")
    ];

    public static string NameOf(string id) =>
        Common.FirstOrDefault(league => string.Equals(league.Id, id, StringComparison.OrdinalIgnoreCase)).Name
        ?? id[(id.IndexOf('/') + 1)..].ToUpperInvariant();

    public static bool IsValid(string id)
    {
        string[] parts = id.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
    }
}
