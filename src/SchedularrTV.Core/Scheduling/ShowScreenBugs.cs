using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Whether a recording already carries a burned-in screen bug, as set on the TV Shows page for the whole show, a season
/// or one episode. The most specific setting wins: episode, then season, then show.
/// </summary>
public static class ShowScreenBugs
{
    public static bool HasScreenBug(SchedulerConfig config, MediaItem? item) =>
        item is { SeriesName: { Length: > 0 } series }
        && HasScreenBug(config, series, item.SeasonNumber, item.EpisodeNumber);

    public static bool HasScreenBug(SchedulerConfig config, string series, int? seasonNumber, int? episodeNumber)
    {
        if (!config.Titles.TryGetValue(series, out TitleDefaults? title))
        {
            return false;
        }

        bool? value = title.ScreenBug;
        if (seasonNumber is { } number && title.Seasons.TryGetValue(number, out SeasonDefaults? season))
        {
            value = season.ScreenBug ?? value;
            if (episodeNumber is { } episode && season.Episodes.TryGetValue(episode, out EpisodeDefaults? settings))
            {
                value = settings.ScreenBug ?? value;
            }
        }

        return value == true;
    }
}
