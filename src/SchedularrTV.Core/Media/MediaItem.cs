using System.Collections.Frozen;

namespace SchedularrTV.Core.Media;

/// <summary>
/// Source-agnostic representation of a playable item. Everything the scheduling engine reasons about
/// is projected into this shape so the engine can be unit tested without a live Jellyfin server.
/// </summary>
public sealed class MediaItem
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Unknown;

    public TimeSpan Duration { get; init; }

    public string? SeriesId { get; init; }

    public string? SeriesName { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public DateTimeOffset? PremiereDate { get; init; }

    public string? Overview { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Collections { get; init; } = [];

    /// <summary>Networks the show originally aired on, e.g. <c>["Teletoon", "Cartoon Network"]</c>.</summary>
    public IReadOnlyList<string> Networks { get; init; } = [];

    /// <summary>Year the series began, which is not the same as this episode's air year.</summary>
    public int? SeriesYear { get; init; }

    /// <summary>Chapter start offsets, used to place mid-roll breaks at natural act boundaries.</summary>
    public IReadOnlyList<TimeSpan> Chapters { get; init; } = [];

    /// <summary>
    /// Free-form provider metadata (e.g. <c>promo_type</c>, <c>show_id</c>) used by promo targeting.
    /// Populated from Jellyfin custom fields / tag conventions.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>Absolute or provider-relative path handed to ErsatzTV for playback.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// Identifies the set of alternate versions of one episode/movie (e.g. a web rip and a DVD rip
    /// merged in Jellyfin). Every member of the set carries the same value; null when the item has
    /// no alternates. Members are separate provider items, so without this they would each schedule.
    /// </summary>
    public string? VersionGroupId { get; init; }

    /// <summary>
    /// Provider name for this particular version, normally the file stem
    /// (e.g. <c>S01E01 The Beginning - DVD Release</c>). Null when the item has no alternates.
    /// </summary>
    public string? VersionName { get; init; }

    /// <summary>Per-item override for whether filler may be cut short. Null defers to the tier default.</summary>
    public bool? Trimmable => ReadFlag("trimmable");

    /// <summary>Per-item override for "play until the boundary then stop" rather than a hard cut.</summary>
    public bool? StopBeforeEnd => ReadFlag("stop_before_end");

    /// <summary>Stable ordering key so equal-sorting items never shuffle between deterministic runs.</summary>
    public string SortKey => $"{SeriesName}|{SeasonNumber:D4}|{EpisodeNumber:D5}|{Title}|{Id}";

    public override string ToString() => $"{Title} ({Duration:hh\\:mm\\:ss})";

    /// <summary>A copy with other chapter markers, and optionally extra attributes (for example where the markers came from).</summary>
    public MediaItem WithChapters(IReadOnlyList<TimeSpan> chapters, IReadOnlyDictionary<string, string>? extraAttributes = null)
    {
        IReadOnlyDictionary<string, string> attributes = Attributes;
        if (extraAttributes is { Count: > 0 })
        {
            Dictionary<string, string> merged = new(Attributes);
            foreach ((string key, string value) in extraAttributes)
            {
                merged[key] = value;
            }

            attributes = merged.ToFrozenDictionary();
        }

        return new MediaItem
        {
            Id = Id,
            Title = Title,
            Kind = Kind,
            Duration = Duration,
            SeriesId = SeriesId,
            SeriesName = SeriesName,
            SeasonNumber = SeasonNumber,
            EpisodeNumber = EpisodeNumber,
            PremiereDate = PremiereDate,
            Overview = Overview,
            Tags = Tags,
            Collections = Collections,
            Networks = Networks,
            SeriesYear = SeriesYear,
            Chapters = chapters,
            Attributes = attributes,
            Path = Path,
            VersionGroupId = VersionGroupId,
            VersionName = VersionName
        };
    }

    private bool? ReadFlag(string name) =>
        Attributes.TryGetValue(name, out string? value) && bool.TryParse(value, out bool parsed) ? parsed : null;
}
