using System.Text;
using System.Text.Json.Serialization;

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

    // --- Place (WeatherStar, radar and globe centre on it; the clock takes its time zone and weather) -----------

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

    /// <summary>Radar and globe: how far in; 0 for the preset's own default.</summary>
    public int Zoom { get; set; }

    /// <summary>Radar and globe: what the map shows (radar, wind, temperature…); null for the preset's default.</summary>
    public string? Layer { get; set; }

    /// <summary>Clock: "digital" or "analog".</summary>
    public string ClockStyle { get; set; } = "digital";

    /// <summary>Clock: current conditions for the place under the time.</summary>
    public bool ShowWeather { get; set; } = true;

    /// <summary>Clock: a line of text scrolled along the bottom; empty for none.</summary>
    public string? Message { get; set; }

    // --- Scheduling and sound ------------------------------------------------------------------------------------

    /// <summary>The lengths, in minutes, this channel can be scheduled for; each becomes an "episode".</summary>
    public List<int> Lengths { get; set; } = [3, 4, 5, 10, 15, 30, 60];

    /// <summary>A folder of songs shuffled under the page; none plays silence.</summary>
    public string? MusicFolder { get; set; }

    /// <summary>0 to 1.</summary>
    public double MusicVolume { get; set; } = 0.6;

    [JsonIgnore]
    public bool HasPlace => Latitude != 0 || Longitude != 0;

    [JsonIgnore]
    public WebPreset PresetInfo => WebPresets.Find(Preset);

    /// <summary>Lengths that can be scheduled: whole minutes from 1 to 24 hours, once each, shortest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> ValidLengths => [.. Lengths.Where(minutes => minutes is > 0 and <= 1440).Distinct().Order()];

    public WebChannel Copy()
    {
        var copy = (WebChannel)MemberwiseClone();
        copy.Lengths = [.. Lengths];
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
