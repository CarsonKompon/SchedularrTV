using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SchedularrTV.WebChannels;

/// <summary>Whether a preset needs a place.</summary>
public enum PlaceNeed
{
    None,
    Optional,
    Required
}

/// <summary>
/// Where pages are: the WeatherStar site to use, and the scheduler's own address for pages the plugin serves
/// (<c>http://localhost:5252</c> for the hidden browser; null for an address relative to the app, for previews).
/// </summary>
public sealed record PageAddresses(string WeatherStarSite, string? LocalBase);

/// <summary>One choice of a preset's setting.</summary>
public sealed record LayerOption(string Value, string Label);

/// <summary>A kind of web page channel: which page it shows, made from a channel's settings, and how it's described.</summary>
public sealed record WebPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public PlaceNeed Place { get; init; } = PlaceNeed.None;

    /// <summary>Said under the place, for presets that take it in their own way.</summary>
    public string? PlaceHint { get; init; }

    /// <summary>Sent to ErsatzTV as the network; null for none.</summary>
    public string? Network { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>A name for a new channel of this kind, from the place's town (or null).</summary>
    public required Func<string?, string> DefaultName { get; init; }

    /// <summary>The episode title before its length, e.g. "Local Forecast".</summary>
    public required Func<WebChannel, string> EpisodeTitle { get; init; }

    public required Func<WebChannel, string> Overview { get; init; }

    public required Func<WebChannel, PageAddresses, string> PageUrl { get; init; }

    /// <summary>
    /// Scripts run at the start of every load of the page, for pages that have to be set up or started by script (a
    /// settings screen to get past, a start button to press); none for a page that plays by itself.
    /// </summary>
    public Func<WebChannel, IReadOnlyList<string>> Scripts { get; init; } = _ => [];

    /// <summary>The page plays its own music, which is aired unless the channel says otherwise.</summary>
    public bool OwnSound { get; init; }

    /// <summary>Whether the page's own sound can be aired (a page with its own music, or any custom page).</summary>
    public bool CanPlayPageSound => OwnSound || Id == WebPresets.Custom;

    /// <summary>Whether the preview in the app can't show what airs (the page needs its start-up script, which only the hidden browser runs).</summary>
    public bool PreviewNeedsScript { get; init; }
}

/// <summary>The presets: the pages that make good television, and any page you like.</summary>
public static class WebPresets
{
    public const string WeatherStar = "weatherstar";
    public const string Clock = "clock";
    public const string WeatherscanV2 = "weatherscan-v2";
    public const string WeatherscanV1 = "weatherscan-v1";
    public const string WeatherscanLocal = "weatherscan-local";
    public const string WeatherStarJr = "weatherstar-jr";
    public const string WeatherStar4000 = "weatherstar-4000";
    public const string WeatherStarXl = "weatherstar-xl";
    public const string IntelliStar = "intellistar";
    public const string Custom = "custom";

    /// <summary>The weather radar (Windy) and wind globe (nullschool) presets of earlier versions, now custom pages.</summary>
    public const string LegacyRadar = "radar";
    public const string LegacyGlobe = "globe";

    public const string DefaultWeatherStarSite = "https://mwood77.github.io/ws4kp-international/";

    private const string SimPlaceHint =
        "Shown by the simulator as it finds it from the name above (a town, \"City, ST\" or a ZIP code works best). With no place, it uses where this computer is, from its internet address.";

    private const string SimNote = " Forecasts come from weather.com, in °F and mph; it's built for US places, but others mostly work.";

    // --- Choices for the simulators' settings ----------------------------------------------------------------------

    /// <summary>Weatherscan Local's segments ("packages"), by the names it gives them.</summary>
    public static readonly IReadOnlyList<LayerOption> LocalSegments =
    [
        new("forecast", "Local forecast"),
        new("extralocal", "Extra cities"),
        new("minicore", "Mini forecast"),
        new("spanish", "Spanish forecast"),
        new("golf", "Golf"),
        new("health", "Health"),
        new("airport", "Airports")
    ];

    public static readonly IReadOnlyList<string> DefaultLocalSegments = ["forecast", "extralocal", "minicore"];

    /// <summary>WeatherStar Jr and 4000's bottom line.</summary>
    public static readonly IReadOnlyList<LayerOption> ClassicBars =
    [
        new("observations", "Current conditions"),
        new("ad", "Your message"),
        new("both", "Both, in turn"),
        new("none", "Nothing")
    ];

    /// <summary>WeatherStar XL's bottom line.</summary>
    public static readonly IReadOnlyList<LayerOption> XlBars =
    [
        new("both", "Current conditions and your message (M flavor only)"),
        new("observations", "Current conditions"),
        new("crawl", "Your message")
    ];

    /// <summary>IntelliStar's bottom line.</summary>
    public static readonly IReadOnlyList<LayerOption> IntelliStarBars =
    [
        new("both", "Current conditions and your message"),
        new("observations", "Current conditions")
    ];

    public static readonly IReadOnlyList<LayerOption> XlFlavors =
    [
        new("m", "M: 2 minutes"),
        new("k", "K: 90 seconds"),
        new("d", "D: 1 minute")
    ];

    public static readonly IReadOnlyList<LayerOption> IntelliStarFlavors =
    [
        new("120", "2 minutes"),
        new("90", "90 seconds"),
        new("60", "1 minute")
    ];

    public static readonly IReadOnlyList<LayerOption> XlGraphics =
    [
        new("", "Either, picked by the simulator"),
        new("v2", "Version 2"),
        new("v3", "Version 3")
    ];

    public static readonly IReadOnlyList<LayerOption> IntelliStarGraphics =
    [
        new("2010", "2010"),
        new("2009", "2009"),
        new("2008", "2008"),
        new("2007", "2007")
    ];

    /// <summary>The channel's choice if it's one of <paramref name="options"/>, else the first.</summary>
    public static string Choice(IReadOnlyList<LayerOption> options, string? value) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))?.Value ?? options[0].Value;

    public static IReadOnlyList<WebPreset> All { get; } =
    [
        new()
        {
            Id = WeatherStar,
            Name = "WeatherStar 4000+",
            Description = "Retro local forecasts in the style of 1990s cable weather, from WeatherStar 4000+ International. Works anywhere, in metric or imperial.",
            Place = PlaceNeed.Required,
            Network = "WeatherStar 4000+",
            Tags = ["Weather", "WeatherStar"],
            DefaultName = town => town is null ? "Weather" : $"{town} Weather",
            EpisodeTitle = _ => "Local Forecast",
            Overview = channel => $"Live weather for {Where(channel)} from the WeatherStar 4000+.",
            PageUrl = WeatherStarUrl
        },
        Simulator(WeatherscanV2, "Weatherscan v2", "Weatherscan",
            "The last design of Weatherscan, the round-the-clock local weather channel: forecasts, radar, travel, health, golf and more, with its music.",
            "Local Weather", channel => $"Weatherscan for {Where(channel)}.", WeatherscanUrl("https://v2.weatherscan.net/")),
        Simulator(WeatherscanV1, "Weatherscan v1", "Weatherscan",
            "The first IntelliStar design of Weatherscan, with its music.",
            "Local Weather", channel => $"Weatherscan for {Where(channel)}.", WeatherscanUrl("https://v1.weatherscan.net/")),
        Simulator(WeatherscanLocal, "Weatherscan Local", "Weatherscan",
            "The first Weatherscan, from 1998: choose its segments, the cable company's name and a scrolling message.",
            "Local Weather", channel => $"Weatherscan Local for {Where(channel)}.", (_, _) => "https://local.weatherscan.net/") with
        {
            Scripts = channel => [LocalScript(channel)],
            PreviewNeedsScript = true
        },
        Simulator(WeatherStarJr, "WeatherStar Jr", "WeatherStar",
            "The local forecast as the WeatherStar Jr showed it on cable, with its music.",
            "Local Forecast", channel => $"The local forecast for {Where(channel)} on the WeatherStar Jr.", WeatherStarDevUrl("https://jr.weatherstar.dev/")) with
        {
            Scripts = channel => [ClassicScript(channel, startMusic: false)]
        },
        Simulator(WeatherStar4000, "WeatherStar 4000 (classic)", "WeatherStar",
            "The original 1990s WeatherStar 4000 local forecast, with its music. For anywhere outside the US, or metric units, choose WeatherStar 4000+.",
            "Local Forecast", channel => $"The local forecast for {Where(channel)} on the WeatherStar 4000.", WeatherStarDevUrl("https://4000.weatherstar.dev/")) with
        {
            Scripts = channel => [ClassicScript(channel, startMusic: channel.PlaysPageSound)]
        },
        Simulator(WeatherStarXl, "WeatherStar XL", "WeatherStar",
            "The local forecast as the WeatherStar XL showed it in the late 1990s and 2000s, with its music and narration.",
            "Local Forecast", channel => $"The local forecast for {Where(channel)} on the WeatherStar XL.", (_, _) => "https://xl.weatherstar.dev/") with
        {
            Scripts = channel => [XlScript(channel)],
            PreviewNeedsScript = true
        },
        Simulator(IntelliStar, "IntelliStar", "IntelliStar",
            "The local forecast as the IntelliStar showed it in the 2000s, with its music and narration.",
            "Local Forecast", channel => $"The local forecast for {Where(channel)} on the IntelliStar.", (_, _) => "https://i1.weatherstar.dev/") with
        {
            Scripts = channel => [IntelliStarScript(channel)],
            PreviewNeedsScript = true
        },
        new()
        {
            Id = Clock,
            Name = "Clock",
            Description = "Time and temperature: a big clock for the place's time zone, its current conditions and a scrolling message. Served by the plugin itself.",
            Place = PlaceNeed.Optional,
            Network = "SchedularrTV",
            Tags = ["Clock"],
            DefaultName = town => town is null ? "The Time" : $"{town} Time",
            EpisodeTitle = channel => channel.HasPlace && channel.ShowWeather ? "Time & Temperature" : "The Time",
            Overview = channel => channel.HasPlace ? $"The time and weather in {Where(channel)}." : "The time.",
            PageUrl = ClockUrl
        },
        new()
        {
            Id = Custom,
            Name = "Custom web page",
            Description = "Any address. It's shown at 1280×720 with no way to click, so pick a page that plays by itself (a dashboard, a live map, a kiosk view).",
            Place = PlaceNeed.Optional,
            Tags = ["Web"],
            DefaultName = _ => "Web Channel",
            EpisodeTitle = _ => "Live",
            Overview = channel => $"Live from {Host(channel.Url)}.",
            PageUrl = CustomUrl
        }
    ];

    /// <summary>The weather simulators share most of what they are; Mist Weather Media hosts them all.</summary>
    private static WebPreset Simulator(string id, string name, string network, string description, string episode, Func<WebChannel, string> overview,
        Func<WebChannel, PageAddresses, string> url) => new()
    {
        Id = id,
        Name = name,
        Description = description + SimNote,
        Place = PlaceNeed.Optional,
        PlaceHint = SimPlaceHint,
        Network = network,
        Tags = ["Weather", network],
        OwnSound = true,
        DefaultName = town => town is null ? name : $"{town} {name}",
        EpisodeTitle = _ => episode,
        Overview = overview,
        PageUrl = url
    };

    /// <summary>The preset with this id; an unknown id is treated as a custom page.</summary>
    public static WebPreset Find(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[^1];

    /// <summary>
    /// Turns a channel of a preset that's gone (the weather radar and wind globe) into a custom page showing what it
    /// showed. True when it changed anything.
    /// </summary>
    public static bool Migrate(WebChannel channel)
    {
        string? url = channel.Preset?.ToLowerInvariant() switch
        {
            LegacyRadar => LegacyWindyUrl(channel),
            LegacyGlobe => LegacyGlobeUrl(channel),
            _ => null
        };
        if (url is null)
        {
            return false;
        }

        channel.Preset = Custom;
        channel.Url = url;
        channel.Zoom = 0;
        channel.Layer = null;
        return true;
    }

    private static string Where(WebChannel channel) => string.IsNullOrWhiteSpace(channel.Location) ? channel.Name : channel.Location;

    private static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "the web";

    private static string Coordinate(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Build(string address, IEnumerable<(string Key, string Value)> query)
    {
        var url = new StringBuilder(address);
        char separator = address.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        foreach ((string key, string value) in query)
        {
            url.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return url.ToString();
    }

    /// <summary>
    /// The WeatherStar page for a channel, set up for broadcast: kiosk mode (no controls, playing straight away), the
    /// channel's place and units, and Webamp hidden.
    /// </summary>
    public static string WeatherStarUrl(WebChannel channel, PageAddresses addresses)
    {
        string site = addresses.WeatherStarSite;
        site = site.Contains('?', StringComparison.Ordinal) ? site[..site.IndexOf('?', StringComparison.Ordinal)] : site;
        string latLon = JsonSerializer.Serialize(new { lat = Math.Round(channel.Latitude, 4), lon = Math.Round(channel.Longitude, 4) });
        static string Flag(bool value) => value ? "true" : "false";

        // WeatherStar reads each setting from settings-<name>-<checkbox|select>; the select values are its own option
        // numbers, and the speed is written the way it writes its own (0.50 Very Fast … 1.00 Normal … 1.50 Very Slow).
        return Build(site,
        [
            ("latLonQuery", string.IsNullOrWhiteSpace(channel.Location) ? channel.Name : channel.Location),
            ("latLon", latLon),
            ("kiosk", "true"),
            ("settings-kiosk-checkbox", "true"),
            ("settings-wide-checkbox", Flag(channel.Widescreen)),
            ("settings-hideWebamp-checkbox", "true"),
            ("settings-scanLines-checkbox", Flag(channel.ScanLines)),
            ("settings-temperatureUnits-select", channel.Imperial ? "2" : "1"),
            ("settings-windUnits-select", channel.Imperial ? "4" : "2"),
            ("settings-distanceUnits-select", channel.Imperial ? "2" : "1"),
            ("settings-pressureUnits-select", channel.Imperial ? "2" : "1"),
            ("settings-hoursFormat-select", channel.TwelveHourClock ? "1" : "2"),
            ("settings-speed-select", Math.Clamp(channel.Speed, 0.5, 1.5).ToString("0.00", CultureInfo.InvariantCulture))
        ]);
    }

    /// <summary>What the simulators search weather.com for: the place's name, else its coordinates; null for "where this computer is".</summary>
    public static string? SimulatorQuery(WebChannel channel) =>
        !string.IsNullOrWhiteSpace(channel.Location) ? channel.Location.Trim()
        : channel.HasPlace ? $"{Coordinate(channel.Latitude)},{Coordinate(channel.Longitude)}"
        : null;

    /// <summary>
    /// Weatherscan v1 and v2 take the place as the whole of the address's query (<c>?Denver, CO</c>), and start by
    /// themselves; with none, they find the place from this computer's internet address.
    /// </summary>
    private static Func<WebChannel, PageAddresses, string> WeatherscanUrl(string site) =>
        // Commas are left as they are: the simulators hand the query on to weather.com's search as it's written.
        (channel, _) => SimulatorQuery(channel) is { } query ? site + "?" + Uri.EscapeDataString(query).Replace("%2C", ",", StringComparison.Ordinal) : site;

    /// <summary>WeatherStar Jr and 4000 read the place the same way, and start by themselves too.</summary>
    private static Func<WebChannel, PageAddresses, string> WeatherStarDevUrl(string site) => WeatherscanUrl(site);

    /// <summary>A C# string as a JavaScript string literal.</summary>
    private static string Js(string? value) => JsonSerializer.Serialize(value ?? string.Empty);

    /// <summary>
    /// What every simulator script starts with: dialogs that would stop the page (a "new version" notice) are skipped,
    /// and <c>whenReady(test, then)</c> waits for the page's own scripts to have set up what the script needs. Each
    /// script runs once per page load, so a page that reloads itself is set up again each time.
    /// </summary>
    private const string ScriptStart = """
        (() => {
          if (window.__schedularrSim) return;
          window.__schedularrSim = true;
          window.alert = () => {};
          window.confirm = () => true;
          const whenReady = (test, then, every = 200, tries = 900) => {
            const timer = setInterval(() => {
              let ready = false;
              try { ready = !!test(); } catch (e) { }
              if (ready) { clearInterval(timer); try { then(); } catch (e) { console.warn('SchedularrTV:', e); } }
              else if (--tries <= 0) clearInterval(timer);
            }, every);
          };
        """;

    private const string ScriptEnd = "\n})();";

    /// <summary>
    /// Weatherscan Local opens on its setup screens: the place (by coordinates, else found from the internet address),
    /// the segments, the cable company's name, the message and the narration are filled in, and it's started.
    /// </summary>
    public static string LocalScript(WebChannel channel)
    {
        string[] chosen = [.. (channel.Segments.Count > 0 ? channel.Segments : DefaultLocalSegments)
            .Where(segment => LocalSegments.Any(option => option.Value == segment)).Distinct()];
        if (chosen.Length == 0)
        {
            chosen = [.. DefaultLocalSegments];
        }

        string packages = string.Join(", ", chosen.Select(segment => segment switch
        {
            "forecast" => "forecastPackage",
            "extralocal" => "extraLocalPackage",
            "minicore" => "miniCorePackage",
            "spanish" => "spanishForecastPackage",
            "golf" => "golfPackage",
            "health" => "healthPackage",
            _ => "airportPackage"
        }));
        string place = channel.HasPlace
            ? $"locationSettings.mainCity = {{ autoFind: false, displayname: \"\", type: 'geocode', val: {Js($"{Coordinate(channel.Latitude)},{Coordinate(channel.Longitude)}")} }};"
            : "";
        string affiliate = string.IsNullOrWhiteSpace(channel.Affiliate) ? "" : $"apperanceSettings.providerName = {Js(channel.Affiliate.Trim())};";
        string message = string.IsNullOrWhiteSpace(channel.Message) ? "" : $"apperanceSettings.enableCrawl = true; apperanceSettings.adMessage = [{Js(channel.Message.Trim())}];";
        return ScriptStart + $$"""
              whenReady(() => typeof startButton === 'function' && typeof locationSettings === 'object' && typeof slideSettings === 'object'
                  && typeof forecastPackage !== 'undefined', () => {
                {{place}}
                {{affiliate}}
                {{message}}
                if (typeof audioSettings === 'object') audioSettings.narrations = {{(channel.Narration ? "true" : "false")}};
                slideSettings.order[0].slideLineup.push({{packages}});
                startButton();
              });
            """ + ScriptEnd;
    }

    /// <summary>
    /// WeatherStar Jr and 4000 start by themselves; this sets their bottom line and message before they do, and starts
    /// the 4000's music (its page leaves it off).
    /// </summary>
    public static string ClassicScript(WebChannel channel, bool startMusic)
    {
        string message = string.IsNullOrWhiteSpace(channel.Message) ? "" : $"apperanceSettings.marqueeAd = [{Js(channel.Message.Trim())}];";
        string music = startMusic
            ? """
                whenReady(() => typeof audioPlayer === 'object' && audioPlayer.playlist && audioPlayer.playlist.length > 0,
                  () => setTimeout(() => audioPlayer.startPlaying(audioPlayer.playlist, true), 6000));
              """
            : "";
        return ScriptStart + $$"""
              whenReady(() => typeof apperanceSettings === 'object', () => {
                apperanceSettings.marqueeType = {{Js(Choice(ClassicBars, channel.BottomBar))}};
                {{message}}
              }, 50);
              {{music}}
            """ + ScriptEnd;
    }

    /// <summary>
    /// WeatherStar XL opens on its settings menu, and goes back to it after each forecast (it reloads itself): the place
    /// is looked up, the flavor, bottom line, shape, graphics, message and narration set, and it's started, every time.
    /// </summary>
    public static string XlScript(WebChannel channel)
    {
        string? query = SimulatorQuery(channel);
        string flavor = Choice(XlFlavors, channel.Flavor);
        string bar = Choice(XlBars, channel.BottomBar);
        // Both together only fits the longest flavor.
        if (bar == "both" && flavor != "m")
        {
            bar = "observations";
        }

        string graphics = Choice(XlGraphics, channel.Graphics);
        string message = string.IsNullOrWhiteSpace(channel.Message) ? "" : $"if (typeof appearanceSettings === 'object') appearanceSettings.marqueeAd = [{Js(channel.Message.Trim())}];";
        string lookUp = query is null ? "" : $"mainquery = {Js(query)}; locationJS();";
        return ScriptStart + $$"""
              whenReady(() => typeof locationJS === 'function' && typeof startButton === 'function' && typeof setFlavor === 'function', () => {
                {{lookUp}}
                // The place it found first (from the internet address) is replaced by the one asked for, which takes a moment.
                setTimeout(() => whenReady(() => locationConfig.mainCity.lat !== undefined && locationConfig.mainCity.displayname !== undefined, () => {
                  setFlavor({{Js(flavor)}});
                  setLDLType({{Js(bar)}});
                  setAspectRatio({{(channel.Widescreen ? "3/2" : "4/3")}});
                  {{(graphics.Length > 0 ? $"versionSettings(null, {Js(graphics)});" : "")}}
                  {{message}}
                  if (typeof audioSettings === 'object') audioSettings.narrations = {{(channel.Narration ? "true" : "false")}};
                  startButton();
                }), {{(query is null ? 500 : 3000)}});
              });
            """ + ScriptEnd;
    }

    /// <summary>
    /// The IntelliStar opens on its settings menu too, and reloads itself after each forecast: once it has its first
    /// data the place is looked up again (when one's given), then the flavor, graphics year, bottom line, message and
    /// narration are set and it's started.
    /// </summary>
    public static string IntelliStarScript(WebChannel channel)
    {
        string? query = SimulatorQuery(channel);
        string message = string.IsNullOrWhiteSpace(channel.Message) ? "" : $"if (typeof appearanceSettings === 'object') appearanceSettings.marqueeAd = [{Js(channel.Message.Trim())}];";
        string lookUp = query is null
            ? "begin();"
            : $"mainquery = {Js(query)}; grabLocation().then(() => setTimeout(onLocationInit, 1000)); setTimeout(() => whenReady(ready, begin), 4000);";
        return ScriptStart + $$"""
              const ready = () => { const button = document.getElementById('startbutton'); return button && getComputedStyle(button).pointerEvents !== 'none'; };
              const begin = () => {
                flavorChanger({{Js(Choice(IntelliStarFlavors, channel.Flavor))}});
                versionsChanger({{Choice(IntelliStarGraphics, channel.Graphics)}});
                ldlChanger({{Js(Choice(IntelliStarBars, channel.BottomBar))}});
                {{message}}
                if (typeof audioSettings === 'object') audioSettings.narrations = {{(channel.Narration ? "true" : "false")}};
                startProgram();
              };
              whenReady(() => typeof grabLocation === 'function' && typeof startProgram === 'function' && ready(), () => { {{lookUp}} });
            """ + ScriptEnd;
    }

    /// <summary>The plugin's own clock page; everything it shows is in the address, so a preview needs nothing saved.</summary>
    public static string ClockUrl(WebChannel channel, PageAddresses addresses)
    {
        string page = (addresses.LocalBase is { } local ? local.TrimEnd('/') : string.Empty) + WebChannelSettings.RoutePrefix + "/clock.html";
        if (addresses.LocalBase is null)
        {
            page = page.TrimStart('/');
        }

        List<(string, string)> query =
        [
            ("name", channel.Name),
            ("place", channel.Location),
            ("style", channel.ClockStyle == "analog" ? "analog" : "digital"),
            ("h12", channel.TwelveHourClock ? "1" : "0"),
            ("units", channel.Imperial ? "f" : "c")
        ];
        if (!string.IsNullOrWhiteSpace(channel.TimeZone))
        {
            query.Add(("tz", channel.TimeZone));
        }

        if (channel.HasPlace && channel.ShowWeather)
        {
            query.Add(("lat", Coordinate(channel.Latitude)));
            query.Add(("lon", Coordinate(channel.Longitude)));
        }

        if (!string.IsNullOrWhiteSpace(channel.Message))
        {
            query.Add(("msg", channel.Message));
        }

        return Build(page, query);
    }

    /// <summary>What the old weather radar preset showed: Windy's public embed, centred (and pinned) on the place.</summary>
    public static string LegacyWindyUrl(WebChannel channel)
    {
        string[] layers = ["radar", "wind", "rain", "temp", "clouds"];
        string layer = layers.FirstOrDefault(value => string.Equals(value, channel.Layer, StringComparison.OrdinalIgnoreCase)) ?? layers[0];
        string lat = Coordinate(channel.Latitude);
        string lon = Coordinate(channel.Longitude);
        int zoom = Math.Clamp(channel.Zoom == 0 ? 7 : channel.Zoom, 3, 11);
        return Build("https://embed.windy.com/embed2.html",
        [
            ("lat", lat),
            ("lon", lon),
            ("detailLat", lat),
            ("detailLon", lon),
            ("width", LiveStream.Width.ToString(CultureInfo.InvariantCulture)),
            ("height", LiveStream.Height.ToString(CultureInfo.InvariantCulture)),
            ("zoom", zoom.ToString(CultureInfo.InvariantCulture)),
            ("level", "surface"),
            ("overlay", layer),
            ("product", layer == "radar" ? "radar" : "ecmwf"),
            ("menu", string.Empty),
            ("message", "true"),
            ("marker", "true"),
            ("calendar", "now"),
            ("pressure", string.Empty),
            ("type", "map"),
            ("location", "coordinates"),
            ("detail", string.Empty),
            ("metricWind", channel.Imperial ? "mph" : "km/h"),
            ("metricTemp", channel.Imperial ? "°F" : "°C"),
            ("radarRange", "-1")
        ]);
    }

    /// <summary>What the old wind globe preset showed: the earth.nullschool.net globe, turned to face the place (or the Atlantic).</summary>
    public static string LegacyGlobeUrl(WebChannel channel)
    {
        string lon = channel.HasPlace ? Coordinate(channel.Longitude) : "-40";
        string lat = channel.HasPlace ? Coordinate(channel.Latitude) : "20";
        int zoom = Math.Clamp(channel.Zoom == 0 ? 3 : channel.Zoom, 1, 8);
        int scale = (int)Math.Round(250 * Math.Pow(1.6, zoom - 1));
        string view = channel.Layer?.ToLowerInvariant() switch
        {
            "ocean" => "ocean/surface/currents",
            "wind-temp" => "wind/surface/level/overlay=temp",
            _ => "wind/surface/level"
        };

        return $"https://earth.nullschool.net/#current/{view}/orthographic={lon},{lat},{scale.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The channel's own address, with {lat}, {lon}, {location} and {name} filled in.</summary>
    public static string CustomUrl(WebChannel channel, PageAddresses addresses)
    {
        if (string.IsNullOrWhiteSpace(channel.Url))
        {
            return "about:blank";
        }

        return channel.Url.Trim()
            .Replace("{lat}", Coordinate(channel.Latitude), StringComparison.OrdinalIgnoreCase)
            .Replace("{lon}", Coordinate(channel.Longitude), StringComparison.OrdinalIgnoreCase)
            .Replace("{location}", Uri.EscapeDataString(channel.Location), StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", Uri.EscapeDataString(channel.Name), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a custom address can be shown: an absolute http or https address.</summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url?.Trim()
            .Replace("{lat}", "0", StringComparison.OrdinalIgnoreCase).Replace("{lon}", "0", StringComparison.OrdinalIgnoreCase)
            .Replace("{location}", "x", StringComparison.OrdinalIgnoreCase).Replace("{name}", "x", StringComparison.OrdinalIgnoreCase),
            UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
