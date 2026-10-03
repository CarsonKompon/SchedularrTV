using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.LiveCapture;
using SchedularrTV.Plugins;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.WebChannels;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class WebChannelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "web-channels-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly PageAddresses Browser = new(WebPresets.DefaultWeatherStarSite, "http://localhost:5252");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static WebChannel Toronto(string preset = WebPresets.WeatherStar) => new()
    {
        Id = "toronto",
        Name = "Toronto Weather",
        Preset = preset,
        Location = "Toronto, Ontario",
        Latitude = 43.70011,
        Longitude = -79.4163,
        TimeZone = "America/Toronto",
        Lengths = [5, 3, 60, 3, 0, 2000]
    };

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    // --- WeatherStar -----------------------------------------------------------------------------

    [Fact]
    public void The_weatherstar_page_is_kiosk_mode_for_the_place_and_metric_units()
    {
        Dictionary<string, string> query = Query(WebPresets.WeatherStarUrl(Toronto(), Browser));

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
    public void Imperial_weatherstar_asks_for_fahrenheit_mph_miles_and_inches()
    {
        WebChannel washington = Toronto();
        washington.Location = "Washington, District of Columbia";
        washington.Imperial = true;
        washington.TwelveHourClock = true;
        washington.Widescreen = false;
        washington.Speed = 1.5;

        Dictionary<string, string> query = Query(WebPresets.WeatherStarUrl(washington, Browser with { WeatherStarSite = "https://example.test/ws4kp/?old=1" }));

        Assert.DoesNotContain("old", query.Keys);
        Assert.Equal("2", query["settings-temperatureUnits-select"]);
        Assert.Equal("4", query["settings-windUnits-select"]);
        Assert.Equal("2", query["settings-distanceUnits-select"]);
        Assert.Equal("2", query["settings-pressureUnits-select"]);
        Assert.Equal("1", query["settings-hoursFormat-select"]);
        Assert.Equal("false", query["settings-wide-checkbox"]);
        Assert.Equal("1.50", query["settings-speed-select"]);
    }

    // --- The other presets -----------------------------------------------------------------------

    [Fact]
    public void The_clock_is_the_plugins_own_page_with_its_settings_in_the_address()
    {
        WebChannel clock = Toronto(WebPresets.Clock);
        clock.Imperial = true;
        clock.TwelveHourClock = true;
        clock.ClockStyle = "analog";
        clock.Message = "Back soon & thanks";

        string url = WebPresets.ClockUrl(clock, Browser);
        Dictionary<string, string> query = Query(url);

        Assert.StartsWith("http://localhost:5252/plugins/web-channels/clock.html?", url);
        Assert.Equal("America/Toronto", query["tz"]);
        Assert.Equal("analog", query["style"]);
        Assert.Equal("1", query["h12"]);
        Assert.Equal("f", query["units"]);
        Assert.Equal("43.7001", query["lat"]);
        Assert.Equal("Back soon & thanks", query["msg"]);
    }

    [Fact]
    public void A_clock_preview_is_relative_to_the_app_and_without_weather_has_no_place()
    {
        WebChannel clock = Toronto(WebPresets.Clock);
        clock.ShowWeather = false;

        string url = WebPresets.ClockUrl(clock, Browser with { LocalBase = null });

        Assert.StartsWith("plugins/web-channels/clock.html?", url);
        Assert.DoesNotContain("lat=", url);
    }

    [Fact]
    public void The_radar_is_windys_embed_centred_on_the_place_with_its_layer_and_zoom()
    {
        WebChannel radar = Toronto(WebPresets.Radar);
        radar.Imperial = true;
        radar.Zoom = 99;

        Dictionary<string, string> query = Query(WebPresets.WindyUrl(radar, Browser));

        Assert.Equal("43.7001", query["lat"]);
        Assert.Equal("-79.4163", query["lon"]);
        Assert.Equal("radar", query["overlay"]);
        Assert.Equal("11", query["zoom"]);
        Assert.Equal("mph", query["metricWind"]);
        Assert.Equal("°F", query["metricTemp"]);

        radar.Layer = "nonsense";
        radar.Zoom = 0;
        Assert.Equal("radar", Query(WebPresets.WindyUrl(radar, Browser))["overlay"]);
        Assert.Equal("7", Query(WebPresets.WindyUrl(radar, Browser))["zoom"]);
    }

    [Fact]
    public void The_globe_faces_the_place_or_the_atlantic()
    {
        WebChannel globe = Toronto(WebPresets.Globe);
        globe.Layer = "ocean";

        Assert.Equal("https://earth.nullschool.net/#current/ocean/surface/currents/orthographic=-79.4163,43.7001,640", WebPresets.GlobeUrl(globe, Browser));

        globe.Latitude = 0;
        globe.Longitude = 0;
        globe.Layer = null;
        Assert.Equal("https://earth.nullschool.net/#current/wind/surface/level/orthographic=-40,20,640", WebPresets.GlobeUrl(globe, Browser));
    }

    [Fact]
    public void Custom_pages_fill_in_the_place()
    {
        WebChannel custom = Toronto(WebPresets.Custom);
        custom.Url = " https://example.test/map?lat={lat}&lon={LON}&q={location} ";

        Assert.Equal("https://example.test/map?lat=43.7001&lon=-79.4163&q=Toronto%2C%20Ontario", WebPresets.CustomUrl(custom, Browser));
        Assert.True(WebPresets.IsWebAddress(custom.Url));
        Assert.False(WebPresets.IsWebAddress("file:///C:/secret.html"));
        Assert.False(WebPresets.IsWebAddress(null));
    }

    [Fact]
    public void An_unknown_preset_is_a_custom_page()
    {
        Assert.Equal(WebPresets.Custom, WebPresets.Find("teletext").Id);
    }

    [Theory]
    [InlineData("Toronto Weather", "toronto-weather")]
    [InlineData("  Washington, D.C. ", "washington-d-c")]
    [InlineData("☀", "channel")]
    public void Channel_ids_are_safe_slugs(string name, string expected) => Assert.Equal(expected, WebChannel.Slug(name));

    [Fact]
    public void Lengths_are_whole_minutes_up_to_a_day_once_each_shortest_first()
    {
        Assert.Equal([3, 5, 60], Toronto().ValidLengths);
    }

    // --- Channels file ---------------------------------------------------------------------------

    [Fact]
    public void Saving_gives_every_channel_a_unique_id_and_survives_a_reload()
    {
        var store = new ChannelStore(_folder);
        int changes = 0;
        store.Changed += () => changes++;

        store.Save([new WebChannel { Name = "Toronto Weather" }, new WebChannel { Name = "Toronto Weather", MusicVolume = 4, MusicFolder = "  ", Preset = "nope" }]);

        Assert.Equal(1, changes);
        Assert.Equal(["toronto-weather", "toronto-weather-2"], store.Channels.Select(channel => channel.Id));
        Assert.Equal(1, store.Channels[1].MusicVolume);
        Assert.Null(store.Channels[1].MusicFolder);
        Assert.Equal(WebPresets.Custom, store.Channels[1].Preset);

        var reloaded = new ChannelStore(_folder);
        Assert.Equal(["toronto-weather", "toronto-weather-2"], reloaded.Channels.Select(channel => channel.Id));
        Assert.NotNull(reloaded.Find("TORONTO-WEATHER"));
        string json = File.ReadAllText(Path.Combine(_folder, "channels.json"));
        Assert.DoesNotContain("valid_lengths", json, StringComparison.Ordinal);
        Assert.DoesNotContain("preset_info", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_channels_file_reads_as_no_channels()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "channels.json"), "{ not json");

        Assert.Empty(new ChannelStore(_folder).Channels);
    }

    [Fact]
    public void The_old_weatherstar_stations_are_taken_over_once()
    {
        string appData = Path.Combine(_folder, "app");
        string legacy = Path.Combine(appData, "plugin-data", "ws4kp");
        string own = Path.Combine(appData, "plugin-data", "web-channels");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "stations.json"), """
            [{ "id": "toronto", "name": "Toronto Weather", "location": "Toronto, Ontario", "latitude": 43.7, "longitude": -79.4,
               "imperial": false, "widescreen": true, "twelve_hour_clock": true, "scan_lines": false, "speed": 1.25,
               "lengths": [3, 5], "music_folder": null, "music_volume": 0.4 }]
            """);

        WebChannel station = Assert.Single(new ChannelStore(own, appData).Channels);

        Assert.Equal("toronto", station.Id);
        Assert.Equal(WebPresets.WeatherStar, station.Preset);
        Assert.True(station.TwelveHourClock);
        Assert.Equal(1.25, station.Speed);
        Assert.Equal([3, 5], station.Lengths);
        Assert.True(File.Exists(Path.Combine(own, "channels.json")));

        // Once there are channels of its own, the old stations aren't read again.
        new ChannelStore(own, appData).Save([]);
        Assert.Empty(new ChannelStore(own, appData).Channels);
    }

    // --- Settings --------------------------------------------------------------------------------

    [Fact]
    public void Settings_fall_back_to_the_old_weatherstar_plugins()
    {
        IConfiguration app = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Plugins:ws4kp:WeatherStarUrl"] = "http://localhost:8080/",
                ["Plugins:ws4kp:FrameRate"] = "20",
                ["Server:Port"] = "6000"
            })
            .Build();
        IConfiguration own = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FrameRate"] = "12" })
            .Build();

        var settings = new WebChannelSettings(new FakeContext(own, _folder, app));

        Assert.Equal("http://localhost:8080/", settings.WeatherStarSite);
        Assert.Equal(12, settings.CaptureFrameRate);
        Assert.Equal("http://localhost:6000/plugins/web-channels/live/toronto.ts", settings.LiveUrl("toronto"));
        Assert.StartsWith("http://localhost:6000/plugins/web-channels/clock.html?", settings.PageUrl(Toronto(WebPresets.Clock)));
    }

    // --- The library -----------------------------------------------------------------------------

    private WebChannelLibrary Library(params WebChannel[] channels)
    {
        var store = new ChannelStore(_folder);
        store.Save(channels);
        IConfiguration settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StreamAddress"] = "http://scheduler.lan:5252/" })
            .Build();
        return new WebChannelLibrary(store, new WebChannelSettings(new FakeContext(settings, _folder)));
    }

    [Fact]
    public void Each_channel_is_a_show_with_an_episode_per_length_pointing_at_its_live_stream()
    {
        MediaItem[] items = [.. Library(Toronto()).Items()];

        Assert.Equal(["web-channels:toronto:3", "web-channels:toronto:5", "web-channels:toronto:60"], items.Select(item => item.Id));
        MediaItem five = items[1];
        Assert.Equal(MediaKind.Episode, five.Kind);
        Assert.Equal("Toronto Weather", five.SeriesName);
        Assert.Equal(TimeSpan.FromMinutes(5), five.Duration);
        Assert.Equal("Local Forecast (5 min)", five.Title);
        Assert.Equal("Local Forecast (1 hour)", items[2].Title);
        Assert.Equal(["Weather", "WeatherStar", WebChannelLibrary.Tag], five.Tags);
        Assert.Equal("http://scheduler.lan:5252/plugins/web-channels/live/toronto.ts", five.Path);
        Assert.Equal("true", five.Attributes[PlayoutPublisher.LiveAttribute]);
        using JsonDocument hint = JsonDocument.Parse(five.Attributes[PlayoutPublisher.ProbeHintAttribute]);
        Assert.Equal("mpegts", hint.RootElement.GetProperty("format_name").GetString());
    }

    [Fact]
    public void Each_preset_titles_its_episodes_its_own_way_and_custom_pages_need_an_address()
    {
        WebChannel clock = Toronto(WebPresets.Clock);
        clock.Id = "clock";
        WebChannel globe = Toronto(WebPresets.Globe);
        globe.Id = "globe";
        WebChannel broken = Toronto(WebPresets.Custom);
        broken.Id = "broken";
        broken.Url = "not a page";

        MediaItem[] items = [.. Library(clock, globe, broken).Items()];

        Assert.Equal("Time & Temperature (3 min)", items.First(item => item.SeriesId == "web-channels:clock").Title);
        Assert.Equal("Earth Winds (3 min)", items.First(item => item.SeriesId == "web-channels:globe").Title);
        Assert.DoesNotContain(items, item => item.SeriesId == "web-channels:broken");
    }

    [Fact]
    public async Task Searches_match_channels_by_show_name_and_tag()
    {
        WebChannel washington = Toronto();
        washington.Id = "washington";
        washington.Name = "Washington Weather";
        WebChannel clock = Toronto(WebPresets.Clock);
        clock.Id = "clock";
        clock.Name = "The Time";
        WebChannelLibrary library = Library(Toronto(), washington, clock);

        IReadOnlyList<MediaItem> byShow = await library.SearchAsync(QueryParser.Parse("show:\"Washington Weather\""));
        IReadOnlyList<MediaItem> byTag = await library.SearchAsync(QueryParser.Parse("tag:Weather"));

        Assert.All(byShow, item => Assert.Equal("Washington Weather", item.SeriesName));
        Assert.Equal(3, byShow.Count);
        Assert.Equal(6, byTag.Count);
    }

    // --- Streaming -------------------------------------------------------------------------------

    [Fact]
    public void A_channel_plays_its_music_or_silence()
    {
        var settings = new WebChannelSettings(new FakeContext(new ConfigurationBuilder().Build(), _folder));
        WebChannel withMusic = Toronto();
        withMusic.MusicFolder = "D:/Music";

        Assert.Equal(LiveAudio.Music, settings.Spec(withMusic).Audio);
        Assert.Equal(LiveAudio.Silence, settings.Spec(Toronto()).Audio);
        Assert.Contains("ws4kp-international", settings.Spec(Toronto()).PageUrl);
    }

    // --- Publishing to ErsatzTV ------------------------------------------------------------------

    [Fact]
    public void Live_items_are_published_as_live_sources_without_in_or_out_points()
    {
        DateTimeOffset start = new(2026, 10, 2, 18, 0, 0, TimeSpan.FromHours(-4));
        const string url = "http://localhost:5252/plugins/web-channels/live/toronto.ts";
        MediaItem forecast = WebChannelLibrary.Item(Toronto(), 60, url);
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
        Assert.Equal(url, source.GetProperty("args").EnumerateArray().Last().GetString());
        Assert.True(source.GetProperty("is_live").GetBoolean());
        Assert.False(source.TryGetProperty("in_point_ms", out _));
        Assert.False(source.TryGetProperty("out_point_ms", out _));
        Assert.Equal("h264", source.GetProperty("probe_hint").GetProperty("video")[0].GetProperty("codec").GetString());
    }

    private sealed class FakeContext(IConfiguration configuration, string folder, IConfiguration? app = null) : IPluginContext
    {
        public PluginManifest Manifest { get; } = new() { Id = "web-channels", Name = "Web page channels" };
        public string? PluginDirectory => null;
        public string StaticFilesPath => "/plugins/web-channels/";
        public string DataDirectory => folder;
        public string AppDataDirectory => folder;
        public IConfiguration Configuration => configuration;
        public IConfiguration AppConfiguration => app ?? new ConfigurationBuilder().Build();
        public bool IsLoaded(string pluginId) => false;
    }
}
