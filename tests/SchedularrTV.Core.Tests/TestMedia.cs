using System.Collections.Frozen;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Tests;

/// <summary>Deterministic in-memory library so scheduling behaviour can be asserted exactly.</summary>
internal sealed class FakeMediaLibrary(IEnumerable<MediaItem> items) : IMediaLibrary
{
    private readonly List<MediaItem> _items = items.ToList();

    public int SearchCount { get; private set; }

    public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
    {
        SearchCount++;
        return ValueTask.FromResult(QueryEvaluator.Filter(_items, expression));
    }

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

internal static class TestMedia
{
    public static MediaItem Episode(
        string series,
        int season,
        int episode,
        TimeSpan duration,
        string? collection = null,
        string? title = null,
        string? overview = null,
        DateTimeOffset? premiere = null) => new()
        {
            Id = $"{series}-s{season:D2}e{episode:D2}",
            Title = title ?? $"{series} S{season}E{episode}",
            Kind = MediaKind.Episode,
            Duration = duration,
            SeriesId = series,
            SeriesName = series,
            SeasonNumber = season,
            EpisodeNumber = episode,
            PremiereDate = premiere ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays((season * 100) + episode),
            Overview = overview,
            Collections = collection is null ? [] : [collection]
        };

    public static MediaItem Filler(string id, TimeSpan duration, params string[] tags) => new()
    {
        Id = id,
        Title = id,
        Kind = MediaKind.Filler,
        Duration = duration,
        Tags = tags
    };

    /// <summary>Tier 3 filler. <paramref name="trimmable"/> null leaves the decision to the tier default.</summary>
    public static MediaItem Bumper(
        string id,
        TimeSpan duration,
        bool? trimmable = null,
        bool? stopBeforeEnd = null,
        params string[] tags) => new()
        {
            Id = id,
            Title = id,
            Kind = MediaKind.Filler,
            Duration = duration,
            Tags = tags.Length > 0 ? tags : ["Bumpers"],
            Attributes = Attributes(
                ("trimmable", trimmable?.ToString()),
                ("stop_before_end", stopBeforeEnd?.ToString()))
        };

    public static MediaItem Promo(string id, TimeSpan duration, string showId, string? promoType, params string[] tags) => new()
    {
        Id = id,
        Title = id,
        Kind = MediaKind.Filler,
        Duration = duration,
        Tags = tags.Length > 0 ? [.. tags, "Promo"] : ["Promo"],
        Attributes = Attributes(("show_id", showId), ("promo_type", promoType))
    };

    /// <summary>A promo filed under a clock folder, so it only applies at that time of day.</summary>
    public static MediaItem PromoAt(
        string id,
        TimeSpan duration,
        string showId,
        string? promoType,
        string clock,
        params string[] tags) => new()
    {
        Id = id,
        Title = id,
        Kind = MediaKind.Filler,
        Duration = duration,
        Tags = tags.Length > 0 ? [.. tags, "Promo"] : ["Promo"],
        Attributes = Attributes(("show_id", showId), ("promo_type", promoType), ("promo_clock", clock))
    };

    /// <summary>A trailer filed under the day and time the show it advertises airs.</summary>
    public static MediaItem PromoOn(
        string id,
        TimeSpan duration,
        string showId,
        string? day,
        string? clock,
        params string[] tags) => new()
    {
        Id = id,
        Title = id,
        Kind = MediaKind.Filler,
        Duration = duration,
        Tags = tags.Length > 0 ? [.. tags, "Promo"] : ["Promo"],
        Attributes = Attributes(
            ("show_id", showId),
            ("promo_type", "eventually"),
            ("promo_day", day),
            ("promo_clock", clock))
    };

    private static FrozenDictionary<string, string> Attributes(params (string Key, string? Value)[] pairs) =>        pairs
            .Where(p => p.Value is not null)
            .ToFrozenDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);
}
