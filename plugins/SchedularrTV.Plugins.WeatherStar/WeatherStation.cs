using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SchedularrTV.WeatherStar;

/// <summary>One weather "show": a place, how it's shown, and the lengths it can be scheduled for.</summary>
public sealed class WeatherStation
{
    /// <summary>Stable id, used in item ids and the stream address; never changes once created.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The show's title, e.g. "Toronto Weather".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The place as WeatherStar shows it, e.g. "Toronto, Ontario".</summary>
    public string Location { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>Fahrenheit, mph, miles and inHg instead of Celsius, km/h, km and hPa.</summary>
    public bool Imperial { get; set; }

    public bool Widescreen { get; set; } = true;

    public bool TwelveHourClock { get; set; }

    public bool ScanLines { get; set; }

    /// <summary>WeatherStar's page speed: 0.5 very fast to 1.5 very slow.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>The lengths, in minutes, this station can be scheduled for; each becomes an "episode".</summary>
    public List<int> Lengths { get; set; } = [3, 4, 5, 10, 15, 30, 60];

    /// <summary>A folder of songs shuffled under the forecast; none plays silence.</summary>
    public string? MusicFolder { get; set; }

    /// <summary>0 to 1.</summary>
    public double MusicVolume { get; set; } = 0.6;

    public WeatherStation Copy()
    {
        var copy = (WeatherStation)MemberwiseClone();
        copy.Lengths = [.. Lengths];
        return copy;
    }

    /// <summary>Lengths that can be scheduled: whole minutes from 1 to 24 hours, once each, shortest first.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<int> ValidLengths => [.. Lengths.Where(minutes => minutes is > 0 and <= 1440).Distinct().Order()];

    /// <summary>
    /// The WeatherStar page for this station, set up for broadcast: kiosk mode (no controls, playing straight away),
    /// the station's place and units, and Webamp hidden.
    /// </summary>
    public string PageUrl(string siteAddress)
    {
        string site = siteAddress.Contains('?', StringComparison.Ordinal) ? siteAddress[..siteAddress.IndexOf('?', StringComparison.Ordinal)] : siteAddress;
        string latLon = JsonSerializer.Serialize(new { lat = Math.Round(Latitude, 4), lon = Math.Round(Longitude, 4) });
        static string Flag(bool value) => value ? "true" : "false";
        // Written the way WeatherStar writes its own option values (0.50 Very Fast … 1.00 Normal … 1.50 Very Slow); it parseFloats them.
        static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        // WeatherStar reads each setting from settings-<name>-<checkbox|select>; the select values are its own option numbers.
        (string Key, string Value)[] query =
        [
            ("latLonQuery", string.IsNullOrWhiteSpace(Location) ? Name : Location),
            ("latLon", latLon),
            ("kiosk", "true"),
            ("settings-kiosk-checkbox", "true"),
            ("settings-wide-checkbox", Flag(Widescreen)),
            ("settings-hideWebamp-checkbox", "true"),
            ("settings-scanLines-checkbox", Flag(ScanLines)),
            ("settings-temperatureUnits-select", Imperial ? "2" : "1"),
            ("settings-windUnits-select", Imperial ? "4" : "2"),
            ("settings-distanceUnits-select", Imperial ? "2" : "1"),
            ("settings-pressureUnits-select", Imperial ? "2" : "1"),
            ("settings-hoursFormat-select", TwelveHourClock ? "1" : "2"),
            ("settings-speed-select", Number(Math.Clamp(Speed, 0.5, 1.5)))
        ];

        var url = new StringBuilder(site);
        char separator = '?';
        foreach ((string key, string value) in query)
        {
            url.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return url.ToString();
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
        return result.Length == 0 ? "station" : result.Length > 40 ? result[..40].TrimEnd('-') : result;
    }
}
