using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.TvGuide;

/// <summary>
/// The guides as a media source. Each guide is a show (its name), and each allowed length an episode of it, so a
/// guide can fill a whole channel or be scheduled between programmes like any other show.
/// </summary>
public sealed class GuideLibrary(GuideStore store, GuideSettings settings) : IMediaLibrary
{
    public const string Tag = "Guide";

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MediaItem>>([.. Items().Where(item => QueryEvaluator.Matches(item, expression))]);

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public IEnumerable<MediaItem> Items()
    {
        foreach (GuideStation guide in store.Guides)
        {
            string url = settings.LiveUrl(guide.Id);
            foreach (int minutes in guide.ValidLengths)
            {
                yield return Item(guide, minutes, url);
            }
        }
    }

    public static MediaItem Item(GuideStation guide, int minutes, string url) => new()
    {
        Id = $"tv-guide:{guide.Id}:{minutes}",
        Title = $"Listings ({Describe(minutes)})",
        Kind = MediaKind.Episode,
        Duration = TimeSpan.FromMinutes(minutes),
        SeriesId = $"tv-guide:{guide.Id}",
        SeriesName = guide.Name,
        SeasonNumber = 1,
        EpisodeNumber = minutes,
        Overview = "What's on, scrolling by.",
        Tags = [Tag],
        Path = url,
        Attributes = new Dictionary<string, string>
        {
            ["ersatztv.live"] = "true",
            ["ersatztv.probe_hint"] = LiveStream.ProbeHint,
            ["tv-guide.guide"] = guide.Id
        }.ToFrozenDictionary()
    };

    private static string Describe(int minutes) =>
        minutes % 60 == 0 ? minutes == 60 ? "1 hour" : $"{minutes / 60} hours" : $"{minutes} min";
}
