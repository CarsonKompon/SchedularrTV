using System.Text.RegularExpressions;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Builds the app's own artwork URLs. Images are proxied through the service so the browser never
/// needs the media server's address or API key.
/// </summary>
public static partial class ArtworkLinks
{
    public static readonly IReadOnlySet<string> ImageTypes =
        new HashSet<string>(["Primary", "Thumb", "Backdrop"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Jellyfin ids only: file-system items (<c>fs:</c>) and name fallbacks have no artwork.</summary>
    public static bool IsValidId(string? id) => id is not null && JellyfinId().IsMatch(id);

    public static string? Url(string? id, int width, string type = "Primary") =>
        IsValidId(id) ? $"api/library/artwork/{id}?w={width}&type={type}" : null;

    /// <summary>The series poster for an episode, the item's own image otherwise.</summary>
    public static string? PosterId(MediaItem? item) =>
        item is null ? null : IsValidId(item.SeriesId) ? item.SeriesId : item.Id;

    [GeneratedRegex("^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex JellyfinId();
}
