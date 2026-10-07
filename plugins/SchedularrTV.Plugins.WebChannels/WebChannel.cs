using System.Text;
using System.Text.Json.Serialization;
using SchedularrTV.Core.Media;

namespace SchedularrTV.WebChannels;

/// <summary>
/// One web page channel: a page (from a preset, or any address), how it's set up, and the lengths it can be scheduled
/// for. Only the settings its preset uses matter; the rest are kept so switching presets back and forth loses nothing.
/// </summary>
public sealed class WebChannel
{
    /// <summary>Stable id, used in item ids and the stream address; never changes once created.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The show's title, e.g. "Toronto Weather".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which <see cref="WebPresets">preset</see> draws the page. Defaults to WeatherStar so the old WeatherStar plugin's
    /// stations (which have no preset) read in as what they were.
    /// </summary>
    public string Preset { get; set; } = WebPresets.WeatherStar;

    /// <summary>The page to show, for the custom preset. {lat}, {lon}, {location} and {name} are filled in.</summary>
    public string? Url { get; set; }

    // --- Place (the weather presets centre on it; the clock takes its time zone and weather) -----------------

    /// <summary>The place as shown, e.g. "Toronto, Ontario".</summary>
    public string Location { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>The place's IANA time zone, e.g. "America/Toronto"; empty for this computer's.</summary>
    public string? TimeZone { get; set; }

    // --- Look ----------------------------------------------------------------------------------------------------

    /// <summary>Fahrenheit, mph, miles and inHg instead of Celsius, km/h, km and hPa.</summary>
    public bool Imperial { get; set; }

    public bool TwelveHourClock { get; set; }

    /// <summary>WeatherStar: 16:9 instead of 4:3.</summary>
    public bool Widescreen { get; set; } = true;

    /// <summary>WeatherStar: CRT scan lines.</summary>
    public bool ScanLines { get; set; }

    /// <summary>WeatherStar's page speed: 0.5 very fast to 1.5 very slow.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>The weather radar and wind globe presets of earlier versions: how far in; kept to turn them into custom pages.</summary>
    public int Zoom { get; set; }

    /// <summary>The weather radar and wind globe presets of earlier versions: what the map showed.</summary>
    public string? Layer { get; set; }

    // --- Weather simulators (Weatherscan, WeatherStar Jr/4000/XL, IntelliStar) ---------------------------------

    /// <summary>
    /// Plays the page's own sound (a simulator's music and narration) instead of <see cref="Music"/>; null for the
    /// preset's own choice (on for the simulators, off for a custom page).
    /// </summary>
    public bool? PageSound { get; set; }

    /// <summary>Weatherscan Local: which segments it shows (its "packages"); empty for its usual ones.</summary>
    public List<string> Segments { get; set; } = [];

    /// <summary>The bar along the bottom (the "LDL"): current conditions, a scrolling message, both or nothing; null for the preset's default.</summary>
    public string? BottomBar { get; set; }

    /// <summary>WeatherStar XL and IntelliStar: the "flavor", i.e. how long each local forecast runs; null for the preset's default.</summary>
    public string? Flavor { get; set; }

    /// <summary>WeatherStar XL and IntelliStar: which year's graphics; null for the simulator's own choice.</summary>
    public string? Graphics { get; set; }

    /// <summary>Weatherscan Local: the cable company's name shown on it.</summary>
    public string? Affiliate { get; set; }

    /// <summary>Simulators: the voice-over narrations between songs.</summary>
    public bool Narration { get; set; } = true;

    /// <summary>Clock: "digital" or "analog".</summary>
    public string ClockStyle { get; set; } = "digital";

    /// <summary>Clock: current conditions for the place under the time.</summary>
    public bool ShowWeather { get; set; } = true;

    /// <summary>Clock: a line of text scrolled along the bottom; empty for none.</summary>
    public string? Message { get; set; }

    // --- Scheduling and sound ------------------------------------------------------------------------------------

    /// <summary>The lengths, in minutes, this channel can be scheduled for; each becomes an "episode".</summary>
    public List<int> Lengths { get; set; } = [3, 4, 5, 10, 15, 30, 60];

    /// <summary>Music shuffled under the page (a folder, or any music library); none plays silence.</summary>
    public BackgroundMusic? Music { get; set; }

    /// <summary>The music folder from earlier versions; moved into <see cref="Music"/> when the channels are read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MusicFolder { get; set; }

    /// <summary>0 to 1.</summary>
    public double MusicVolume { get; set; } = 0.6;

    [JsonIgnore]
    public bool HasPlace => Latitude != 0 || Longitude != 0;

    [JsonIgnore]
    public WebPreset PresetInfo => WebPresets.Find(Preset);

    /// <summary>Whether the page's own sound is played (see <see cref="PageSound"/>).</summary>
    [JsonIgnore]
    public bool PlaysPageSound => PresetInfo.CanPlayPageSound && (PageSound ?? PresetInfo.OwnSound);

    /// <summary>Lengths that can be scheduled: whole minutes from 1 to 24 hours, once each, shortest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> ValidLengths => [.. Lengths.Where(minutes => minutes is > 0 and <= 1440).Distinct().Order()];

    public WebChannel Copy()
    {
        var copy = (WebChannel)MemberwiseClone();
        copy.Lengths = [.. Lengths];
        copy.Segments = [.. Segments];
        copy.Music = Music?.Copy();
        return copy;
    }

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
        return result.Length == 0 ? "channel" : result.Length > 40 ? result[..40].TrimEnd('-') : result;
    }
}
