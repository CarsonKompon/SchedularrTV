using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>Optional provider-backed HTTP fallback for media unavailable to local playout.</summary>
public interface IPlaybackUrlResolver
{
    string? Resolve(MediaItem item, ErsatzTvOptions options);
}
