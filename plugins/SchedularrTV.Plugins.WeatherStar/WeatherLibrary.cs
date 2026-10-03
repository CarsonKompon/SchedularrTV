using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.WeatherStar;

/// <summary>
/// The stations as a media source. Each station is a show (its name), and each allowed length an episode of it,
/// so weather can go anywhere a show can: a channel's base rotation for a 24/7 weather channel, a block, or a
/// channel's interstitial shows for short forecasts between programmes.
/// </summary>
public sealed class WeatherLibrary(StationStore stations, StreamAddress address) : IMediaLibrary
{
    public const string Tag = "Weather";

    /// <summary>What ErsatzTV is told about the stream, so it needn't open it twice to find out.</summary>
    public static string ProbeHint => $$"""
        {"format_name":"mpegts","video":[{"stream_index":0,"codec":"h264","width":{{StationStream.Width}},"height":{{StationStream.Height}},"pix_fmt":"yuv420p","frame_rate":"{{StationStream.OutputFrameRate}}","profile":"high"}],"audio":[{"stream_index":1,"codec":"aac","channels":2}]}
        """;

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MediaItem>>([.. Items().Where(item => QueryEvaluator.Matches(item, expression))]);

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<MediaItem> Items()
    {
        foreach (WeatherStation station in stations.Stations)
        {
            string url = address.LiveUrl(station.Id);
            foreach (int minutes in station.ValidLengths)
            {
                yield return Item(station, minutes, url);
            }
        }
    }

    public static MediaItem Item(WeatherStation station, int minutes, string url) => new()
    {
        Id = $"ws4kp:{station.Id}:{minutes}",
        Title = $"Local Forecast ({Describe(minutes)})",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(minutes),
        SeriesId = $"ws4kp:{station.Id}",
        SeriesName = station.Name,
        SeasonNumber = 1,
        EpisodeNumber = minutes,
        Overview = $"Live weather for {(string.IsNullOrWhiteSpace(station.Location) ? station.Name : station.Location)} from the WeatherStar 4000+.",
        Tags = [Tag, "WeatherStar"],
        Networks = ["WeatherStar 4000+"],
        Path = url,
        Attributes = new Dictionary<string, string>
        {
            ["ersatztv.live"] = "true",
            ["ersatztv.probe_hint"] = ProbeHint.Trim(),
            ["ws4kp.station"] = station.Id
        }.ToFrozenDictionary()
    };

    private static string Describe(int minutes) =>
        minutes % 60 == 0 ? minutes == 60 ? "1 hour" : $"{minutes / 60} hours" : $"{minutes} min";
}
