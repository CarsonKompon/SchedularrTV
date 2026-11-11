using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// Promo clips that go together (<see cref="AdPromoPair"/>): the partner of a clip that opens a pair, and which clips may
/// only play as one's partner.
/// </summary>
internal static class PromoPairing
{
    /// <summary>The file of the clip that answers <paramref name="clip"/>, when it opens a pair of this kind.</summary>
    public static string? PartnerOf(PromoConfig config, AdPromoPairKind kind, MediaItem? clip) =>
        clip?.Path is { } path
            ? config.Pairs.FirstOrDefault(pair => pair.Kind == kind && AdBreakPaths.Same(pair.First, path))?.Second
            : null;

    /// <summary>Whether <paramref name="clip"/> answers a pair of this kind, and so only plays after its partner.</summary>
    public static bool IsSecond(PromoConfig config, AdPromoPairKind kind, MediaItem clip) =>
        clip.Path is { } path && config.Pairs.Any(pair => pair.Kind == kind && AdBreakPaths.Same(pair.Second, path));

    /// <summary>Whether <paramref name="clip"/> opens a pair of this kind.</summary>
    public static bool IsFirst(PromoConfig config, AdPromoPairKind kind, MediaItem clip) =>
        clip.Path is { } path && config.Pairs.Any(pair => pair.Kind == kind && AdBreakPaths.Same(pair.First, path));

    /// <summary>The library's clip at <paramref name="path"/> (however its folders are written); null when it isn't there.</summary>
    public static async ValueTask<MediaItem?> FindAsync(ScheduleBuildSession session, string path, CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaItem> found = await session.SearchAsync(
                new FieldExpression("path", ComparisonOperator.Matches, [$"^{AdBreakPaths.Pattern(path)}$"]), cancellationToken)
            .ConfigureAwait(false);
        return found.FirstOrDefault(item => item.Duration > TimeSpan.Zero);
    }
}

/// <summary>
/// The answer owed to a Now Playing that opened a pair: its partner plays as the This Has Been once that show (one of
/// <see cref="ShowIds"/>) has finished. Kept for the length of a build.
/// </summary>
internal sealed class PendingSignOff
{
    public string? Partner { get; set; }

    public IReadOnlyList<string> ShowIds { get; set; } = [];
}
