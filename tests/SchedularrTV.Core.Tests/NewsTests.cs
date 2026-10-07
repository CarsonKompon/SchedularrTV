using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.LiveCapture;
using SchedularrTV.News;
using SchedularrTV.Plugins;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class NewsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "news-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // --- Feeds ---------------------------------------------------------------------------------------------------

    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/" xmlns:atom="http://www.w3.org/2005/Atom">
          <channel>
            <title>City News &amp; Weather</title>
            <atom:link href="https://example.com/rss" rel="self" />
            <item>
              <title>Older story</title>
              <link>https://example.com/older</link>
              <description>Older.</description>
              <pubDate>Mon, 05 Oct 2026 08:00:00 EST</pubDate>
            </item>
            <item>
              <title>Council approves transit line</title>
              <link>https://example.com/transit</link>
              <description><![CDATA[<p>Council approves transit line. The <b>14-km</b> line opens in 2031.</p><p>Work starts in spring.</p>]]></description>
              <media:thumbnail url="https://example.com/transit.jpg" width="640" height="360" />
              <pubDate>Tue, 06 Oct 2026 12:30:00 +0000</pubDate>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void An_rss_feed_reads_newest_first_with_plain_summaries_pictures_and_dates()
    {
        FeedContent feed = FeedReader.Parse(Rss);

        Assert.Equal("City News & Weather", feed.Title);
        Assert.Equal(2, feed.Stories.Count);
        Story first = feed.Stories[0];
        Assert.Equal("Council approves transit line", first.Title);
        // The headline repeated at the start of the summary is dropped, and the HTML taken out.
        Assert.Equal("The 14-km line opens in 2031. Work starts in spring.", first.Summary);
        Assert.Equal("https://example.com/transit.jpg", first.Image);
        Assert.Equal("https://example.com/transit", first.Link);
        Assert.Equal("City News & Weather", first.Source);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 30, 0, TimeSpan.Zero), first.Published);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.FromHours(-5)), feed.Stories[1].Published);
    }

    [Fact]
    public void A_feeds_own_name_gives_way_to_the_label_chosen_for_it()
    {
        Assert.Equal("Local", FeedReader.Parse(Rss, "Local").Stories[0].Source);
    }

    [Fact]
    public void An_atom_feed_reads_its_entries_and_alternate_links()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom News</title>
              <entry>
                <title>Bridge reopens</title>
                <link rel="self" href="https://example.com/self" />
                <link rel="alternate" href="https://example.com/bridge" />
                <summary type="html">&lt;p&gt;The bridge reopened on Tuesday.&lt;img src="https://example.com/bridge.png"&gt;&lt;/p&gt;</summary>
                <updated>2026-10-06T09:15:00Z</updated>
              </entry>
            </feed>
            """;

        Story story = Assert.Single(FeedReader.Parse(atom).Stories);

        Assert.Equal("Bridge reopens", story.Title);
        Assert.Equal("https://example.com/bridge", story.Link);
        Assert.Equal("The bridge reopened on Tuesday.", story.Summary);
        Assert.Equal("https://example.com/bridge.png", story.Image);
        Assert.Equal("Atom News", story.Source);
    }

    [Theory]
    [InlineData("Tue, 06 Oct 2026 12:30:00 GMT", 12)]
    [InlineData("Tue, 06 Oct 2026 08:30:00 EDT", 12)]
    [InlineData("06 Oct 2026 14:30:00 +0200", 12)]
    [InlineData("2026-10-06T12:30:00Z", 12)]
    public void Feed_dates_read_in_all_their_forms(string text, int utcHour)
    {
        DateTimeOffset? date = FeedReader.ParseDate(text);

        Assert.NotNull(date);
        Assert.Equal(utcHour, date.Value.UtcDateTime.Hour);
        Assert.Equal(30, date.Value.UtcDateTime.Minute);
    }

    [Fact]
    public void Summaries_are_cut_to_whole_sentences_and_words()
    {
        const string text = "First sentence here. Second one follows. Third is long and goes on and on.";

        Assert.Equal("First sentence here. Second one follows.", FeedReader.Sentences(text, 2, 280));
        Assert.EndsWith("…", FeedReader.Sentences(text, 3, 30));
        Assert.True(FeedReader.Sentences(text, 3, 30).Length <= 31);
    }

    [Fact]
    public void Stories_are_taken_from_each_feed_in_turn_recent_and_once_only()
    {
        DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        Story Make(string title, double hoursAgo) => new(title, "", null, null, "x", now.AddHours(-hoursAgo));
        var busy = new FeedContent("Busy", [Make("A1", 1), Make("A2", 2), Make("A3", 3), Make("Old", 48)]);
        var quiet = new FeedContent("Quiet", [Make("B1", 1), Make("a1!", 1)]);
        var show = new NewsShow { StoryCount = 4, MaxAgeHours = 24 };

        IReadOnlyList<Story> picked = NewsCasts.PickStories(show, [busy, quiet], now);

        Assert.Equal(["A1", "B1", "A2", "A3"], picked.Select(story => story.Title));
    }

    // --- The rundown ---------------------------------------------------------------------------------------------

    private static NewsSnapshot Snapshot(int stories) => new(
        DateTimeOffset.UnixEpoch,
        [.. Enumerable.Range(1, stories).Select(n => new Story($"Story {n}", $"Summary {n}. More.", null, null, "Feed", null))],
        new WeatherReport("Toronto, Ontario", 12, 9, 70, 15, "NW", 2, "partly", "Partly cloudy", true,
            [new WeatherDay("2026-10-06", 2, "partly", "Partly cloudy", 15, 6, 10), new WeatherDay("2026-10-07", 63, "rain", "Rain", 11, 7, 90)], "°C", "km/h"),
        [new Quote("^GSPC", "S&P 500", 5432.17, 41.2, 0.76, "USD", [1, 2])],
        [new ScoreGame("NBA", "post", "Final", new ScoreTeam("ATL", "Hawks", "123", "#c8102e", null, false), new ScoreTeam("MEM", "Grizzlies", "132", "#5d76a9", null, true))],
        [],
        null,
        []);

    [Fact]
    public void The_rundown_opens_spreads_the_data_between_the_stories_and_closes()
    {
        var show = new NewsShow { Name = "Channel 7 News", Desks = [NewsDesk.Weather, NewsDesk.Markets, NewsDesk.Sports, NewsDesk.Lottery] };

        IReadOnlyList<RundownItem> items = NewsRundown.Compose(show, Snapshot(6));

        // No lottery results, so no lottery segment.
        Assert.Equal(
            ["intro", "story", "story", "weather", "story", "story", "markets", "story", "story", "sports", "outro"],
            items.Select(item => item.Kind));
        Assert.Equal("NBA", items.Single(item => item.Kind == "sports").Key);
        Assert.Equal("This is Channel 7 News. Here are the latest headlines.", items[0].Speech);
    }

    [Fact]
    public void How_much_of_a_story_is_read_follows_the_shows_choice()
    {
        var story = new Story("Bridge reopens", "Traffic is flowing again. Crews worked overnight. More to come later today.", null, null, "x", null);

        Assert.Equal("Bridge reopens.", NewsRundown.StoryScript(story, SpeechMode.Headlines));
        Assert.Equal("Bridge reopens. Traffic is flowing again. Crews worked overnight.", NewsRundown.StoryScript(story, SpeechMode.HeadlineAndSummary));
        Assert.Equal("Bridge reopens. Traffic is flowing again. Crews worked overnight. More to come later today.", NewsRundown.StoryScript(story, SpeechMode.FullSummary));
    }

    [Fact]
    public void The_data_segments_read_naturally()
    {
        NewsSnapshot snapshot = Snapshot(0);

        Assert.Equal("Now the weather in Toronto. It's 12 degrees and partly cloudy, feeling like 9 degrees. Today, a high of 15 degrees and a low of 6 degrees. Tomorrow, rain, with a high of 11 degrees.",
            NewsRundown.WeatherScript(snapshot.Weather!));
        Assert.Equal("On the markets, the S and P 500 is up 0.8 percent at 5,432.17.", NewsRundown.MarketsScript(snapshot.Quotes));
        Assert.Equal("In the NBA: Grizzlies beat Hawks, 132 to 123.", NewsRundown.SportsScript("NBA", snapshot.Scores));
    }

    [Fact]
    public void Segments_run_for_their_voice_over_or_the_shows_own_lengths()
    {
        var show = new NewsShow { SecondsPerStory = 10, SecondsPerDesk = 12 };
        RundownItem[] items =
        [
            new("intro", 0, null, "", "Hello", 0, 0),
            new("story", 0, null, "", "Read", 0, 0) { Clip = "clip.pcm", ClipMs = 7000 },
            new("story", 1, null, "", "", 0, 0),
            new("weather", 0, null, "", "", 0, 0)
        ];

        IReadOnlyList<RundownItem> timed = NewsRundown.Time(show, items);

        Assert.Equal([0L, 6000L, 14700L, 24700L], timed.Select(item => item.StartMs));
        Assert.Equal([6000L, 8700L, 10000L, 12000L], timed.Select(item => item.DurationMs));
        Assert.Equal(36700, new Rundown(1, NewsSnapshot.Empty(DateTimeOffset.UnixEpoch), timed).LengthMs);
    }

    // --- The clock -----------------------------------------------------------------------------------------------

    private static Rundown Loop(long version, params long[] lengths)
    {
        long at = 0;
        List<RundownItem> items = [];
        foreach (long length in lengths)
        {
            items.Add(new RundownItem("story", items.Count, null, "", "", at, length));
            at += length;
        }

        return new Rundown(version, NewsSnapshot.Empty(DateTimeOffset.UnixEpoch), items);
    }

    [Fact]
    public void A_rundown_loops_and_a_new_one_takes_over_at_the_end_of_the_loop()
    {
        var cast = new NewsCast();
        cast.Publish(Loop(1, 1000, 1000), nowMs: 10_000, immediately: false);

        (Rundown? rundown, long start, bool waiting) = cast.At(15_500);
        Assert.Equal(1, rundown!.Version);
        Assert.Equal(14_000, start);
        Assert.False(waiting);

        // A refresh part-way through a loop waits for it to end.
        cast.Publish(Loop(2, 3000), nowMs: 15_500, immediately: false);
        Assert.Equal(1, cast.At(15_900).Rundown!.Version);
        Assert.True(cast.At(15_900).Waiting);

        (rundown, start, _) = cast.At(16_100);
        Assert.Equal(2, rundown!.Version);
        Assert.Equal(16_000, start);
    }

    [Fact]
    public void Changed_settings_go_on_at_once_and_a_break_starts_from_the_top()
    {
        var cast = new NewsCast();
        cast.Publish(Loop(1, 5000), nowMs: 0, immediately: false);
        cast.Publish(Loop(2, 5000), nowMs: 2000, immediately: true);
        Assert.Equal((2L, 2000L), (cast.At(2500).Rundown!.Version, cast.At(2500).StartMs));

        cast.Publish(Loop(3, 5000), nowMs: 3000, immediately: false);
        cast.Restart(nowMs: 4000);
        Assert.Equal((3L, 4000L), (cast.At(4100).Rundown!.Version, cast.At(4100).StartMs));
    }

    // --- Data sources --------------------------------------------------------------------------------------------

    [Fact]
    public void Powerball_and_mega_millions_results_read_with_their_bonus_balls()
    {
        using JsonDocument powerball = JsonDocument.Parse("""{"draw_date":"2026-10-04T00:00:00.000","winning_numbers":"04 12 27 39 61 09","multiplier":"03"}""");
        using JsonDocument mega = JsonDocument.Parse("""{"draw_date":"2026-09-04T00:00:00.000","winning_numbers":"06 26 34 59 61","mega_ball":"24"}""");

        LotteryDraw first = Lottery.Read(powerball.RootElement, LotteryGames.Find("powerball")!);
        LotteryDraw second = Lottery.Read(mega.RootElement, LotteryGames.Find("megamillions")!);

        Assert.Equal([4, 12, 27, 39, 61], first.Numbers);
        Assert.Equal(9, first.Bonus);
        Assert.Equal("3", first.Multiplier);
        Assert.Equal("2026-10-04", first.Date);
        Assert.Equal([6, 26, 34, 59, 61], second.Numbers);
        Assert.Equal(24, second.Bonus);
        Assert.Null(second.Multiplier);
    }

    [Fact]
    public void Espn_scoreboards_read_live_games_first()
    {
        const string json = """
            {"events":[
              {"status":{"type":{"state":"post","shortDetail":"Final"}},"competitions":[{"competitors":[
                {"homeAway":"home","winner":true,"score":"132","team":{"abbreviation":"MEM","shortDisplayName":"Grizzlies","color":"5d76a9","logo":"https://a.espncdn.com/mem.png"}},
                {"homeAway":"away","winner":false,"score":"123","team":{"abbreviation":"ATL","shortDisplayName":"Hawks","color":"c8102e"}}]}]},
              {"status":{"type":{"state":"in","shortDetail":"Q3 4:12"}},"competitions":[{"competitors":[
                {"homeAway":"home","score":"81","team":{"abbreviation":"TOR","shortDisplayName":"Raptors"}},
                {"homeAway":"away","score":"78","team":{"abbreviation":"BOS","shortDisplayName":"Celtics","color":"007a33"}}]}]}
            ]}
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        IReadOnlyList<ScoreGame> games = Sports.Read(document.RootElement, "NBA");

        Assert.Equal(["in", "post"], games.Select(game => game.State));
        Assert.Equal("Q3 4:12", games[0].Status);
        Assert.Equal("#555555", games[0].Home.Colour);
        Assert.True(games[1].Home.Winner);
        Assert.Equal("https://a.espncdn.com/mem.png", games[1].Home.Logo);
    }

    [Fact]
    public void Yahoo_chart_data_reads_into_a_quote_with_its_sparkline()
    {
        const string json = """
            {"chart":{"result":[{"meta":{"currency":"USD","symbol":"^GSPC","regularMarketPrice":5432.17,"chartPreviousClose":5390.97,"shortName":"S&P 500"},
              "indicators":{"quote":[{"close":[5391.0,null,5410.5,5432.17]}]}}],"error":null}}
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        Quote quote = Markets.Read(document.RootElement, "^GSPC");

        Assert.Equal("S&P 500", quote.Name);
        Assert.Equal(41.2, quote.Change);
        Assert.Equal(0.76, quote.ChangePercent);
        Assert.Equal([5391.0, 5410.5, 5432.17], quote.Spark);
    }

    [Fact]
    public void Lotto_max_and_649_read_from_wcls_page_whatever_its_markup()
    {
        // As the page has them: the newest draw first, the numbers in a list, "Bonus" and its number together, then other draws.
        const string max = """
            <h4>Friday, October 02, 2026</h4>
            <ul class="pastWinNum"><li>2</li><li>4</li><li>20</li><li>23</li><li>25</li><li>41</li><li>47</li>
            <li class="pastWinNumBonus"><span>Bonus</span>26</li></ul>
            <p>MAXPLUS WINNING NUMBERS</p><ul><li>1</li><li>3</li><li>14</li><li>28</li><li>32</li><li>33</li><li>45</li></ul>
            <h4>Tuesday, September 29, 2026</h4><ul><li>6</li><li>10</li><li>13</li><li>22</li><li>23</li><li>43</li><li>49</li><li>Bonus17</li></ul>
            """;
        const string six49 = """
            <script>var x = "Monday, January 01, 2024";</script>
            <h4>Saturday, October 03, 2026</h4><p>CLASSIC DRAW</p>
            <ul><li>1</li><li>4</li><li>7</li><li>23</li><li>28</li><li>37</li><li>Bonus38</li></ul><p>GOLD BALL DRAW</p>
            """;

        LotteryDraw lottoMax = Lottery.ReadWclc(max, LotteryGames.Find("lottomax")!);
        LotteryDraw lotto649 = Lottery.ReadWclc(six49, LotteryGames.Find("lotto649")!);

        Assert.Equal([2, 4, 20, 23, 25, 41, 47], lottoMax.Numbers);
        Assert.Equal(26, lottoMax.Bonus);
        Assert.Equal("2026-10-02", lottoMax.Date);
        Assert.Equal([1, 4, 7, 23, 28, 37], lotto649.Numbers);
        Assert.Equal(38, lotto649.Bonus);
        Assert.Equal("2026-10-03", lotto649.Date);
        Assert.Throws<InvalidOperationException>(() => Lottery.ReadWclc("<p>Down for maintenance</p>", LotteryGames.Find("lottomax")!));
    }

    [Fact]
    public void Traffic_is_asked_about_an_area_tomtom_accepts()
    {
        var city = MapMath.Bounds(43.65, -79.38, 10, 1280, 720);
        Assert.True(MapMath.AreaKm2(city) > 10_000);

        var asked = MapMath.Within(city, 9_000);

        Assert.InRange(MapMath.AreaKm2(asked), 8_900, 9_001);
        Assert.Equal((city.West + city.East) / 2, (asked.West + asked.East) / 2, 6);
        // Already small enough: left as it is.
        Assert.Equal(asked, MapMath.Within(asked, 9_100));
    }

    [Theory]
    [InlineData(0, true, "sun")]
    [InlineData(0, false, "moon")]
    [InlineData(63, true, "rain")]
    [InlineData(75, true, "snow")]
    [InlineData(95, true, "storm")]
    [InlineData(45, true, "fog")]
    public void Weather_codes_become_icons(int code, bool day, string icon) => Assert.Equal(icon, Weather.Icon(code, day));

    [Fact]
    public void The_map_covers_the_picture_around_the_place()
    {
        (double west, double south, double east, double north) = MapMath.Bounds(43.65, -79.38, 10, 1280, 720);

        Assert.True(west < -79.38 && east > -79.38 && south < 43.65 && north > 43.65);
        // 1280 px at zoom 10 is 1280 / (256 × 1024) of the world's 360°.
        Assert.Equal(1280 / (256.0 * 1024) * 360, east - west, 6);
    }

    // --- The store, the library and the stream -------------------------------------------------------------------

    [Fact]
    public void Saved_shows_get_unique_ids_and_tidy_values()
    {
        var store = new NewsStore(_folder);
        store.Save(
        [
            new NewsShow { Name = "Local News", Symbols = ["aapl", "bad symbol!", "^gspc"], Leagues = ["basketball/nba", "nope"], Lotteries = ["powerball", "lotto max"],
                           Feeds = [new NewsFeed { Url = " https://example.com/rss " }, new NewsFeed { Url = "https://example.com/rss" }], MapZoom = 30, StoryCount = 0 },
            new NewsShow { Name = "Local News" }
        ]);

        var reloaded = new NewsStore(_folder);
        Assert.Equal(["local-news", "local-news-2"], reloaded.Shows.Select(show => show.Id));
        NewsShow first = reloaded.Shows[0];
        Assert.Equal(["AAPL", "^GSPC"], first.Symbols);
        Assert.Equal(["basketball/nba"], first.Leagues);
        Assert.Equal(["powerball"], first.Lotteries);
        Assert.Equal("https://example.com/rss", Assert.Single(first.Feeds).Url);
        Assert.Equal(14, first.MapZoom);
        Assert.Equal(1, first.StoryCount);
    }

    [Fact]
    public void Each_show_is_a_virtual_show_with_an_episode_per_length_that_tunes_in_live()
    {
        var show = new NewsShow { Id = "local", Name = "Local News", Lengths = [5, 2, 0, 5, 60] };

        MediaItem[] items = [.. show.ValidLengths.Select(minutes => NewsLibrary.Item(show, minutes, "http://localhost:5252/plugins/news/live/local.ts"))];

        Assert.Equal([2, 5, 60], items.Select(item => (int)item.Duration.TotalMinutes));
        Assert.All(items, item => Assert.Equal("Local News", item.SeriesName));
        Assert.All(items, item => Assert.Equal("true", item.Attributes["ersatztv.live"]));
        Assert.Equal("news:local:5", items[1].Id);
    }

    [Fact]
    public void A_show_on_air_draws_its_page_and_plays_the_plugins_own_sound()
    {
        var settings = new NewsSettings(new FakeContext(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StreamAddress"] = "http://192.168.1.20:5252/"
        }).Build(), _folder));

        LiveStreamSpec spec = settings.Spec(new NewsShow { Id = "local", Name = "Local News" });

        Assert.Equal("http://localhost:5252/plugins/news/view/local", spec.PageUrl);
        Assert.Equal(LiveAudio.Stream, spec.Audio);
        Assert.Equal("http://localhost:5252/plugins/news/sound/local.ts", spec.AudioStreamUrl);
        Assert.Equal("http://192.168.1.20:5252/plugins/news/live/local.ts", settings.LiveUrl("local"));
        Assert.Equal(TimeSpan.FromMinutes(15), settings.RefreshEvery);
        Assert.Null(settings.TomTomKey);
    }

    private sealed class FakeContext(IConfiguration configuration, string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "news", Name = "News shows" };
        public string? PluginDirectory => null;
        public string StaticFilesPath => "/plugins/news/";
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => configuration;
        public IConfiguration AppConfiguration => new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
    }
}
