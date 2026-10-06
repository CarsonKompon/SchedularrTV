using SchedularrTV.Core.Media;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Providers.Jellyfin;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Builds the app's own artwork URLs. Images are proxied through the service so the browser never
/// needs the media server's address or API key.
/// </summary>
public static class ArtworkLinks
{
    public static IReadOnlySet<string> ImageTypes => ArtworkTypes.All;

    /// <summary>
    /// Ids that can have artwork: Jellyfin's, and local TV shows, episodes and movies (pictures beside the files).
    /// Commercials (<c>fs:</c>) and name fallbacks have none.
    /// </summary>
    public static bool IsValidId(string? id) => JellyfinArtworkSource.IsJellyfinId(id) || LocalLibraryIds.IsLocalId(id);

    public static string? Url(string? id, int width, string type = "Primary") =>
        IsValidId(id) ? $"api/library/artwork/{Uri.EscapeDataString(id!)}?w={width}&type={type}" : null;

    /// <summary>The series poster for an episode, the item's own image otherwise.</summary>
    public static string? PosterId(MediaItem? item) =>
        item is null ? null : IsValidId(item.SeriesId) ? item.SeriesId : item.Id;
}
