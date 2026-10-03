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

/// <summary>One of the things a map preset can show.</summary>
public sealed record LayerOption(string Value, string Label);

/// <summary>A kind of web page channel: which page it shows, made from a channel's settings, and how it's described.</summary>
public sealed record WebPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public PlaceNeed Place { get; init; } = PlaceNeed.None;

    /// <summary>Sent to ErsatzTV as the network; null for none.</summary>
    public string? Network { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Map presets' zoom: the default, and the range allowed.</summary>
    public int DefaultZoom { get; init; }

    public int MinZoom { get; init; }

    public int MaxZoom { get; init; }

    /// <summary>What a map preset can show; the first is the default.</summary>
    public IReadOnlyList<LayerOption> Layers { get; init; } = [];

    /// <summary>A name for a new channel of this kind, from the place's town (or null).</summary>
    public required Func<string?, string> DefaultName { get; init; }

    /// <summary>The episode title before its length, e.g. "Local Forecast".</summary>
    public required Func<WebChannel, string> EpisodeTitle { get; init; }

    public required Func<WebChannel, string> Overview { get; init; }

    public required Func<WebChannel, PageAddresses, string> PageUrl { get; init; }

    public bool HasZoom => MaxZoom > MinZoom;

    /// <summary>The channel's zoom, or the preset's default, kept in range.</summary>
    public int ZoomFor(WebChannel channel) => HasZoom ? Math.Clamp(channel.Zoom == 0 ? DefaultZoom : channel.Zoom, MinZoom, MaxZoom) : 0;

    /// <summary>The channel's layer if this preset has it, else the preset's first.</summary>
    public string? LayerFor(WebChannel channel) =>
        Layers.FirstOrDefault(layer => string.Equals(layer.Value, channel.Layer, StringComparison.OrdinalIgnoreCase))?.Value ?? Layers.FirstOrDefault()?.Value;
}

/// <summary>The presets: the pages that make good television, and any page you like.</summary>
public static class WebPresets
{
    public const string WeatherStar = "weatherstar";
    public const string Clock = "clock";
    public const string Radar = "radar";
    public const string Globe = "globe";
    public const string Custom = "custom";

    public const string DefaultWeatherStarSite = "https://mwood77.github.io/ws4kp-international/";

    public static IReadOnlyList<WebPreset> All { get; } =
    [
        new()
        {
            Id = WeatherStar,
            Name = "WeatherStar 4000+",
            Description = "Retro local forecasts in the style of 1990s cable weather, from WeatherStar 4000+ International.",
            Place = PlaceNeed.Required,
            Network = "WeatherStar 4000+",
            Tags = ["Weather", "WeatherStar"],
            DefaultName = town => town is null ? "Weather" : $"{town} Weather",
            EpisodeTitle = _ => "Local Forecast",
            Overview = channel => $"Live weather for {Where(channel)} from the WeatherStar 4000+.",
            PageUrl = WeatherStarUrl
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
            Id = Radar,
            Name = "Weather radar (Windy)",
            Description = "Windy's live animated weather map: radar, wind, rain, temperature or clouds, centred on the place.",
            Place = PlaceNeed.Required,
            Network = "Windy",
            Tags = ["Weather", "Radar"],
            DefaultZoom = 7,
            MinZoom = 3,
            MaxZoom = 11,
            Layers =
            [
                new("radar", "Radar"),
                new("wind", "Wind"),
                new("rain", "Rain and thunder"),
                new("temp", "Temperature"),
                new("clouds", "Clouds")
            ],
            DefaultName = town => town is null ? "Weather Radar" : $"{town} Radar",
            EpisodeTitle = channel => Find(Radar).LayerFor(channel) == "radar" ? "Weather Radar" : "Weather Map",
            Overview = channel => $"Windy's live weather map around {Where(channel)}.",
            PageUrl = WindyUrl
        },
        new()
        {
            Id = Globe,
            Name = "Earth wind map (nullschool)",
            Description = "The earth.nullschool.net globe: the whole planet's winds or ocean currents, animated. Lovely ambient filler with music.",
            Place = PlaceNeed.Optional,
            Network = "earth.nullschool.net",
            Tags = ["Weather", "Ambient"],
            DefaultZoom = 3,
            MinZoom = 1,
            MaxZoom = 8,
            Layers =
            [
                new("wind", "Wind"),
                new("wind-temp", "Wind over temperature"),
                new("ocean", "Ocean currents")
            ],
            DefaultName = _ => "Earth Winds",
            EpisodeTitle = channel => Find(Globe).LayerFor(channel) == "ocean" ? "Ocean Currents" : "Earth Winds",
            Overview = channel => channel.HasPlace ? $"The world's winds, live, over {Where(channel)}." : "The world's winds, live.",
            PageUrl = GlobeUrl
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

    /// <summary>The preset with this id; an unknown id is treated as a custom page.</summary>
    public static WebPreset Find(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[^1];

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

    /// <summary>Windy's public embed, centred (and pinned) on the place.</summary>
    public static string WindyUrl(WebChannel channel, PageAddresses addresses)
    {
        string layer = Find(Radar).LayerFor(channel)!;
        string lat = Coordinate(channel.Latitude);
        string lon = Coordinate(channel.Longitude);
        return Build("https://embed.windy.com/embed2.html",
        [
            ("lat", lat),
            ("lon", lon),
            ("detailLat", lat),
            ("detailLon", lon),
            ("width", LiveStream.Width.ToString(CultureInfo.InvariantCulture)),
            ("height", LiveStream.Height.ToString(CultureInfo.InvariantCulture)),
            ("zoom", Find(Radar).ZoomFor(channel).ToString(CultureInfo.InvariantCulture)),
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

    /// <summary>The earth.nullschool.net globe, turned to face the place (or the Atlantic).</summary>
    public static string GlobeUrl(WebChannel channel, PageAddresses addresses)
    {
        WebPreset preset = Find(Globe);
        string lon = channel.HasPlace ? Coordinate(channel.Longitude) : "-40";
        string lat = channel.HasPlace ? Coordinate(channel.Latitude) : "20";
        int scale = (int)Math.Round(250 * Math.Pow(1.6, preset.ZoomFor(channel) - 1));
        string view = preset.LayerFor(channel) switch
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
