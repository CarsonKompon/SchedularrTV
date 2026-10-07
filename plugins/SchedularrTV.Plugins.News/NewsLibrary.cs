using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.News;

/// <summary>
/// The news shows as a media source. Each show is a "virtual show" (its name), and each allowed length an empty
/// "episode" of it that tunes into the live broadcast when it airs: a channel's base rotation or a block for a 24/7 news
/// channel, or a channel's interstitial shows for news breaks between programmes.
/// </summary>
public sealed class NewsLibrary(NewsStore store, NewsSettings settings) : IMediaLibrary
{
    public const string SourceId = "news";

    /// <summary>Every news show carries these tags.</summary>
    public static readonly string[] Tags = ["News", "News break"];

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MediaItem>>([.. Items().Where(item => QueryEvaluator.Matches(item, expression))]);

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<MediaItem> Items()
    {
        foreach (NewsShow show in store.Shows)
        {
            string url = settings.LiveUrl(show.Id);
            foreach (int minutes in show.ValidLengths)
            {
                yield return Item(show, minutes, url);
            }
        }
    }

    public static MediaItem Item(NewsShow show, int minutes, string url) => new()
    {
        Id = $"{SourceId}:{show.Id}:{minutes}",
        Title = $"{show.Name} ({Describe(minutes)})",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(minutes),
        SeriesId = $"{SourceId}:{show.Id}",
        SeriesName = show.Name,
        SeasonNumber = 1,
        EpisodeNumber = minutes,
        Overview = Overview(show),
        Tags = Tags,
        Path = url,
        Attributes = new Dictionary<string, string>
        {
            ["ersatztv.live"] = "true",
            ["ersatztv.probe_hint"] = LiveStream.ProbeHint,
            ["news.show"] = show.Id
        }.ToFrozenDictionary()
    };

    private static string Overview(NewsShow show)
    {
        string desks = show.Desks.Count == 0 ? "" : $" with {string.Join(", ", show.Desks.Select(desk => desk.ToString().ToLowerInvariant()))}";
        string place = show.Location.Length > 0 ? $" for {show.Location}" : "";
        return $"The latest headlines{desks}{place}.";
    }

    private static string Describe(int minutes) =>
        minutes % 60 == 0 ? minutes == 60 ? "1 hour" : $"{minutes / 60} hours" : $"{minutes} min";
}
