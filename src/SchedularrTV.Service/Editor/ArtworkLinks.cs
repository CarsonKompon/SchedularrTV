using SchedularrTV.Core.Media;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Builds the app's own artwork URLs. Images are proxied through the service so the browser never
/// needs the media server's address or API key.
/// </summary>
public static class ArtworkLinks
{
    public static IReadOnlySet<string> ImageTypes => ArtworkTypes.All;

    /// <summary>
    /// The running media sources' pictures (Jellyfin's, local folders', any library plugin's), set by the host once it's
    /// built; read on each use, so a source switched on or off applies at once.
    /// </summary>
    public static Func<IEnumerable<IArtworkSource>>? Sources { get; set; }

    /// <summary>
    /// Ids that can have artwork: those a running source owns (a media server's items, local TV shows, episodes and
    /// movies). Commercials (<c>fs:</c>) and name fallbacks have none.
    /// </summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 256 && (Sources?.Invoke() ?? []).Any(source => source.Owns(id));

    public static string? Url(string? id, int width, string type = "Primary") =>
        IsValidId(id) ? $"api/library/artwork/{Uri.EscapeDataString(id!)}?w={width}&type={type}" : null;

    /// <summary>The series poster for an episode, the item's own image otherwise.</summary>
    public static string? PosterId(MediaItem? item) =>
        item is null ? null : IsValidId(item.SeriesId) ? item.SeriesId : item.Id;
}
