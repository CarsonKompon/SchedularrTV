using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Radio;

/// <summary>
/// The stations as a media source. Each station is a show (its name), and each allowed length an episode of it, so a
/// station can fill a whole channel or play between programmes like any other show.
/// </summary>
public sealed class RadioLibrary(RadioStore store, RadioSettings settings) : IMediaLibrary
{
    public const string Tag = "Radio";

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MediaItem>>([.. Items().Where(item => QueryEvaluator.Matches(item, expression))]);

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<MediaItem> Items()
    {
        foreach (RadioStation station in store.Stations)
        {
            string url = settings.LiveUrl(station.Id);
            foreach (int minutes in station.ValidLengths)
            {
                yield return Item(station, minutes, url);
            }
        }
    }

    public static MediaItem Item(RadioStation station, int minutes, string url) => new()
    {
        Id = $"radio:{station.Id}:{minutes}",
        Title = $"{(station.Source == RadioSource.Internet ? "Live" : "Music")} ({Describe(minutes)})",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(minutes),
        SeriesId = $"radio:{station.Id}",
        SeriesName = station.Name,
        SeasonNumber = 1,
        EpisodeNumber = minutes,
        Overview = station.Source == RadioSource.Internet ? "Live radio, with the songs as they play." : "Music, with the songs as they play.",
        Tags = [Tag, "Music"],
        Path = url,
        Attributes = new Dictionary<string, string>
        {
            ["ersatztv.live"] = "true",
            ["ersatztv.probe_hint"] = LiveStream.ProbeHint,
            ["radio.station"] = station.Id
        }.ToFrozenDictionary()
    };

    private static string Describe(int minutes) =>
        minutes % 60 == 0 ? minutes == 60 ? "1 hour" : $"{minutes / 60} hours" : $"{minutes} min";
}
