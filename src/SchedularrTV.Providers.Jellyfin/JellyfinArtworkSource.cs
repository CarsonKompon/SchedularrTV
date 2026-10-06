using System.Text.RegularExpressions;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.Jellyfin;

/// <summary>Jellyfin's own images (posters, stills, backdrops), resized by the server.</summary>
public sealed partial class JellyfinArtworkSource(JellyfinClient client) : IArtworkSource
{
    /// <summary>A Jellyfin item id: 32 hex digits, or the same as a dashed GUID.</summary>
    public static bool IsJellyfinId(string? id) => id is not null && JellyfinId().IsMatch(id);

    public bool Owns(string id) => IsJellyfinId(id);

    public async ValueTask<ArtworkImage?> GetImageAsync(string id, string type, int width, CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.GetImageAsync(id, type, width, cancellationToken).ConfigureAwait(false) is { } image
                ? new ArtworkImage(image.Content, image.ContentType)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    [GeneratedRegex("^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex JellyfinId();
}
