using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Core.Scheduling;

/// <summary>Alternate promo identities, without changing the library's canonical series identity.</summary>
public static class ShowAliases
{
    private static TitleDefaults? Settings(SchedulerConfig config, MediaItem item) =>
        item.SeriesName is { } name && config.Titles.TryGetValue(name, out var settings) ? settings : null;

    public static IEnumerable<string> Names(SchedulerConfig config, MediaItem item)
    {
        if (Settings(config, item) is not { } settings) yield break;
        foreach (TitleAlias alias in settings.Aliases)
            if (!string.IsNullOrWhiteSpace(alias.Name)) yield return alias.Name.Trim();
        if (item.SeasonNumber is { } number && settings.Seasons.TryGetValue(number, out var season))
            foreach (TitleAlias alias in season.Aliases)
                if (!string.IsNullOrWhiteSpace(alias.Name)) yield return alias.Name.Trim();
    }

    public static string PreferredName(SchedulerConfig config, MediaItem item)
    {
        return string.IsNullOrWhiteSpace(item.SeriesName) ? item.Title : PreferredName(config, item.SeriesName, item.SeasonNumber);
    }

    public static string PreferredName(SchedulerConfig config, string series, int? seasonNumber = null)
    {
        config.Titles.TryGetValue(series, out TitleDefaults? settings);
        string? preferred = settings is not null && seasonNumber is { } number && settings.Seasons.TryGetValue(number, out var season)
            ? Preferred(season.Aliases) : null;
        return preferred ?? (settings is null ? null : Preferred(settings.Aliases))
            ?? series;
    }

    private static string? Preferred(IEnumerable<TitleAlias> aliases) =>
        aliases.FirstOrDefault(alias => alias.Preferred && !string.IsNullOrWhiteSpace(alias.Name))?.Name.Trim();
}
