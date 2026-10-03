using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchedularrTV.WebChannels;

public sealed record Place(string Label, double Latitude, double Longitude, string? TimeZone);

/// <summary>Finds places by name with Open-Meteo's free geocoding (the same data WeatherStar International forecasts from).</summary>
public sealed class Geocoder(IHttpClientFactory httpClients)
{
    public const string ClientName = "web-channels-geocoding";

    public async Task<IReadOnlyList<Place>> SearchAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
        {
            return [];
        }

        using HttpClient http = httpClients.CreateClient(ClientName);
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language=en&format=json";
        await using Stream body = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        Response? response = await JsonSerializer.DeserializeAsync<Response>(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. (response?.Results ?? []).Select(result => new Place(Label(result), result.Latitude, result.Longitude, result.Timezone))];
    }

    // "Toronto, Ontario, Canada": WeatherStar shows the name from its own lookup; this label only has to be clear.
    private static string Label(Result result) =>
        string.Join(", ", new[] { result.Name, result.Admin1, result.Country }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());

    private sealed class Response
    {
        [JsonPropertyName("results")]
        public List<Result>? Results { get; set; }
    }

    private sealed class Result
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }

        [JsonPropertyName("admin1")]
        public string? Admin1 { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }
    }
}
