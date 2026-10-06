namespace SchedularrTV.Core.Media;

/// <summary>An image ready to send: its bytes and content type.</summary>
public sealed record ArtworkImage(byte[] Content, string ContentType);

/// <summary>
/// Where a media source's pictures come from (a media server, or image files beside the videos). The app asks each
/// source in turn for the one that <see cref="Owns"/> an item or series id.
/// </summary>
public interface IArtworkSource
{
    /// <summary>Whether <paramref name="id"/> (an item's or a series' id) is one of this source's.</summary>
    bool Owns(string id);

    /// <summary>
    /// The image of a type for an item or series, about <paramref name="width"/> pixels wide; null when there's none.
    /// <list type="bullet">
    /// <item><see cref="ArtworkTypes.Primary"/>: a series' or movie's poster, an episode's still.</item>
    /// <item><see cref="ArtworkTypes.Thumb"/>: a landscape picture (an episode's still, a show's thumb).</item>
    /// <item><see cref="ArtworkTypes.Backdrop"/>: the background art (fanart).</item>
    /// </list>
    /// </summary>
    ValueTask<ArtworkImage?> GetImageAsync(string id, string type, int width, CancellationToken cancellationToken = default);
}

public static class ArtworkTypes
{
    public const string Primary = "Primary";
    public const string Thumb = "Thumb";
    public const string Backdrop = "Backdrop";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([Primary, Thumb, Backdrop], StringComparer.OrdinalIgnoreCase);

    /// <summary>The first source that owns <paramref name="id"/> and has an image of the type, or null.</summary>
    public static async ValueTask<ArtworkImage?> FindAsync(IEnumerable<IArtworkSource> sources, string id, string type, int width,
        CancellationToken cancellationToken = default)
    {
        foreach (IArtworkSource source in sources)
        {
            if (source.Owns(id) && await source.GetImageAsync(id, type, width, cancellationToken).ConfigureAwait(false) is { } image)
            {
                return image;
            }
        }

        return null;
    }
}
