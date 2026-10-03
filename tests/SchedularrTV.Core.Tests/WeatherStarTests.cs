using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.WeatherStar;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class WeatherStarTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ws4kp-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static WeatherStation Toronto() => new()
    {
        Id = "toronto",
        Name = "Toronto Weather",
        Location = "Toronto, Ontario",
        Latitude = 43.70011,
        Longitude = -79.4163,
        Lengths = [5, 3, 60, 3, 0, 2000]
    };

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    // --- The WeatherStar page --------------------------------------------------------------------

    [Fact]
    public void The_page_url_is_kiosk_mode_for_the_station_place_and_metric_units()
    {
        Dictionary<string, string> query = Query(Toronto().PageUrl(WeatherStarSettings.DefaultSite));

        Assert.Equal("true", query["kiosk"]);
        Assert.Equal("Toronto, Ontario", query["latLonQuery"]);
        using JsonDocument latLon = JsonDocument.Parse(query["latLon"]);
        Assert.Equal(43.7001, latLon.RootElement.GetProperty("lat").GetDouble());
        Assert.Equal(-79.4163, latLon.RootElement.GetProperty("lon").GetDouble());
        Assert.Equal("true", query["settings-hideWebamp-checkbox"]);
        Assert.Equal("true", query["settings-wide-checkbox"]);
        Assert.Equal("1", query["settings-temperatureUnits-select"]);
        Assert.Equal("2", query["settings-windUnits-select"]);
        Assert.Equal("2", query["settings-hoursFormat-select"]);
        Assert.Equal("1.00", query["settings-speed-select"]);
    }

    [Fact]
    public void Imperial_stations_ask_for_fahrenheit_mph_miles_and_inches()
    {
        WeatherStation washington = Toronto();
        washington.Location = "Washington, District of Columbia";
        washington.Imperial = true;
        washington.TwelveHourClock = true;
        washington.Widescreen = false;
        washington.Speed = 1.5;

        Dictionary<string, string> query = Query(washington.PageUrl("https://example.test/ws4kp/?old=1"));

        Assert.DoesNotContain("old", query.Keys);
        Assert.Equal("2", query["settings-temperatureUnits-select"]);
        Assert.Equal("4", query["settings-windUnits-select"]);
        Assert.Equal("2", query["settings-distanceUnits-select"]);
        Assert.Equal("2", query["settings-pressureUnits-select"]);
        Assert.Equal("1", query["settings-hoursFormat-select"]);
        Assert.Equal("false", query["settings-wide-checkbox"]);
        Assert.Equal("1.50", query["settings-speed-select"]);
    }

    [Theory]
    [InlineData("Toronto Weather", "toronto-weather")]
    [InlineData("  Washington, D.C. ", "washington-d-c")]
    [InlineData("☀", "station")]
    public void Station_ids_are_safe_slugs(string name, string expected) => Assert.Equal(expected, WeatherStation.Slug(name));

    [Fact]
    public void Lengths_are_whole_minutes_up_to_a_day_once_each_shortest_first()
    {
        Assert.Equal([3, 5, 60], Toronto().ValidLengths);
    }

    // --- Stations file ---------------------------------------------------------------------------

    [Fact]
    public void Saving_gives_every_station_a_unique_id_and_survives_a_reload()
    {
        var store = new StationStore(_folder);
        int changes = 0;
        store.Changed += () => changes++;

        store.Save([new WeatherStation { Name = "Toronto Weather" }, new WeatherStation { Name = "Toronto Weather", MusicVolume = 4, MusicFolder = "  " }]);

        Assert.Equal(1, changes);
        Assert.Equal(["toronto-weather", "toronto-weather-2"], store.Stations.Select(station => station.Id));
        Assert.Equal(1, store.Stations[1].MusicVolume);
        Assert.Null(store.Stations[1].MusicFolder);

        var reloaded = new StationStore(_folder);
        Assert.Equal(["toronto-weather", "toronto-weather-2"], reloaded.Stations.Select(station => station.Id));
        Assert.NotNull(reloaded.Find("TORONTO-WEATHER"));
        Assert.DoesNotContain("valid_lengths", File.ReadAllText(Path.Combine(_folder, "stations.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_stations_file_reads_as_no_stations()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "stations.json"), "{ not json");

        Assert.Empty(new StationStore(_folder).Stations);
    }

    // --- The library -----------------------------------------------------------------------------

    private WeatherLibrary Library(params WeatherStation[] stations)
    {
        var store = new StationStore(_folder);
        store.Save(stations);
        IConfiguration settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StreamAddress"] = "http://scheduler.lan:5252/" })
            .Build();
        return new WeatherLibrary(store, new StreamAddress(new WeatherStarSettings(new FakeContext(settings, _folder))));
    }

    [Fact]
    public void Each_station_is_a_show_with_an_episode_per_length_pointing_at_its_live_stream()
    {
        MediaItem[] items = [.. Library(Toronto()).Items()];

        Assert.Equal(["ws4kp:toronto:3", "ws4kp:toronto:5", "ws4kp:toronto:60"], items.Select(item => item.Id));
        MediaItem five = items[1];
        Assert.Equal(MediaKind.Episode, five.Kind);
        Assert.Equal("Toronto Weather", five.SeriesName);
        Assert.Equal(TimeSpan.FromMinutes(5), five.Duration);
        Assert.Equal("Local Forecast (5 min)", five.Title);
        Assert.Equal("Local Forecast (1 hour)", items[2].Title);
        Assert.Equal("http://scheduler.lan:5252/plugins/ws4kp/live/toronto.ts", five.Path);
        Assert.Equal("true", five.Attributes[PlayoutPublisher.LiveAttribute]);
        using JsonDocument hint = JsonDocument.Parse(five.Attributes[PlayoutPublisher.ProbeHintAttribute]);
        Assert.Equal("mpegts", hint.RootElement.GetProperty("format_name").GetString());
    }

    [Fact]
    public async Task Searches_match_stations_by_show_name_and_tag()
    {
        WeatherStation washington = Toronto();
        washington.Id = "washington";
        washington.Name = "Washington Weather";
        WeatherLibrary library = Library(Toronto(), washington);

        IReadOnlyList<MediaItem> byShow = await library.SearchAsync(QueryParser.Parse("show:\"Washington Weather\""));
        IReadOnlyList<MediaItem> byTag = await library.SearchAsync(QueryParser.Parse("tag:Weather"));

        Assert.All(byShow, item => Assert.Equal("Washington Weather", item.SeriesName));
        Assert.Equal(3, byShow.Count);
        Assert.Equal(6, byTag.Count);
    }

    // --- Streaming -------------------------------------------------------------------------------

    [Fact]
    public void The_stream_mixes_music_from_a_looping_playlist_or_plays_silence()
    {
        IReadOnlyList<string> withMusic = StationStream.Arguments(15, "music.txt", 0.6);
        IReadOnlyList<string> silent = StationStream.Arguments(15, null, 0.6);

        Assert.Contains("-stream_loop", withMusic);
        Assert.Contains("volume=0.6", withMusic);
        Assert.Contains("mpegts", withMusic);
        Assert.Equal("pipe:1", withMusic[^1]);
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", silent);
        Assert.DoesNotContain("-af", silent);
    }

    [Fact]
    public void Playlist_paths_escape_quotes()
    {
        Assert.Equal("/music/it'\\''s fine.mp3", StationStream.ConcatPath("/music/it's fine.mp3"));
    }

    // --- Publishing to ErsatzTV ------------------------------------------------------------------

    [Fact]
    public void Live_items_are_published_as_live_sources_without_in_or_out_points()
    {
        DateTimeOffset start = new(2026, 10, 2, 18, 0, 0, TimeSpan.FromHours(-4));
        MediaItem forecast = WeatherLibrary.Item(Toronto(), 60, "http://localhost:5252/plugins/ws4kp/live/toronto.ts");
        var schedule = new ChannelSchedule
        {
            ChannelId = "weather",
            ChannelName = "Weather",
            ChannelNumber = 9,
            ChannelTvgId = "schedularrtv.9",
            Start = start,
            End = start.AddMinutes(45),
            Entries =
            [
                new ScheduleEntry
                {
                    Start = start,
                    Duration = TimeSpan.FromMinutes(45),
                    Kind = ScheduleEntryKind.Program,
                    Item = forecast,
                    IsTrimmed = true,
                    SourceDuration = TimeSpan.FromMinutes(60),
                    Source = "base rotation"
                }
            ]
        };

        string json = JsonSerializer.Serialize(PlayoutPublisher.Build(schedule, new ErsatzTvOptions()), PlayoutPublisher.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement source = document.RootElement.GetProperty("items")[0].GetProperty("source");

        // Read through a program marked live, so ErsatzTV joins the stream where it is instead of reading up to the
        // scheduled offset (which an endless real-time stream never reaches).
        Assert.Equal("script", source.GetProperty("source_type").GetString());
        Assert.Equal("curl", source.GetProperty("command").GetString());
        Assert.Equal("http://localhost:5252/plugins/ws4kp/live/toronto.ts", source.GetProperty("args").EnumerateArray().Last().GetString());
        Assert.True(source.GetProperty("is_live").GetBoolean());
        Assert.False(source.TryGetProperty("in_point_ms", out _));
        Assert.False(source.TryGetProperty("out_point_ms", out _));
        Assert.Equal("h264", source.GetProperty("probe_hint").GetProperty("video")[0].GetProperty("codec").GetString());
    }

    private sealed class FakeContext(IConfiguration configuration, string folder) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "ws4kp", Name = "WeatherStar 4000+" };
        public string? PluginDirectory => null;
        public string StaticFilesPath => "/plugins/ws4kp/";
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => configuration;
        public IConfiguration AppConfiguration => new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
    }
}
