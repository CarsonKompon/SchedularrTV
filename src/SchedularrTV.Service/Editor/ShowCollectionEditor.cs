using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Service.Editor;

/// <summary>Additive picks: a season or episode never narrows a whole-show entry.</summary>
public static class ShowCollectionEditor
{
    public static void Add(ContentSelector target, LibraryShow show, int? season = null, string? episode = null)
    {
        List<ContentInclude> matches = [.. target.Include.Where(include => string.IsNullOrWhiteSpace(include.ShowId)
            ? string.Equals(include.Show, show.Name, StringComparison.OrdinalIgnoreCase)
            : include.ShowId == show.Id)];
        if (matches.Any(include => include.Seasons.Count == 0 && include.Episodes.Count == 0)) return;
        if (season is null && episode is null)
        {
            // Keep entry-level playback and bumper settings when widening a selection.
            ContentInclude whole = matches.FirstOrDefault() ?? new ContentInclude { Show = show.Name, ShowId = show.Id };
            whole.Seasons.Clear();
            whole.Episodes.Clear();
            if (!target.Include.Contains(whole)) target.Include.Add(whole);
            return;
        }
        if (season is { } number && matches.Any(include => include.Episodes.Count == 0 && include.Seasons.Contains(number))) return;
        if (episode is not null && matches.Any(include => include.Episodes.Contains(episode, StringComparer.OrdinalIgnoreCase)
            && (include.Seasons.Count == 0 || season is { } selectedSeason && include.Seasons.Contains(selectedSeason)))) return;
        // Seasons and episodes on one entry are intersected by the query compiler. Keep separate entries for their union.
        target.Include.Add(new ContentInclude
        {
            Show = show.Name, ShowId = show.Id,
            Seasons = episode is null && season is { } selected ? [selected] : [],
            Episodes = episode is not null ? [episode] : []
        });
    }
}
