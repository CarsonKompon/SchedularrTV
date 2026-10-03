using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Media;

/// <summary>
/// Abstraction over the media provider (Jellyfin in production, in-memory in tests).
/// Implementations are expected to cache aggressively: a single 24h schedule build issues
/// hundreds of query evaluations against the same library snapshot.
/// </summary>
public interface IMediaLibrary
{
    /// <summary>Returns every item matching <paramref name="expression"/>, unordered.</summary>
    ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default);

    /// <summary>Forces a refresh of any provider-side cache.</summary>
    ValueTask RefreshAsync(CancellationToken cancellationToken = default);
}
