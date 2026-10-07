using System.Text.Json.Serialization;

namespace SchedularrTV.Providers.Jellyfin;

internal sealed class JellyfinItemsResponse
{
    [JsonPropertyName("Items")]
    public List<JellyfinItem> Items { get; set; } = [];

    [JsonPropertyName("TotalRecordCount")]
    public int TotalRecordCount { get; set; }
}

internal sealed class JellyfinItem
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }

    [JsonPropertyName("SeriesId")]
    public string? SeriesId { get; set; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; set; }

    [JsonPropertyName("SeasonId")]
    public string? SeasonId { get; set; }

    [JsonPropertyName("IndexNumber")]
    public int? IndexNumber { get; set; }

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    [JsonPropertyName("PremiereDate")]
    public DateTimeOffset? PremiereDate { get; set; }

    [JsonPropertyName("ProductionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("Tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("Genres")]
    public List<string>? Genres { get; set; }

    /// <summary>The content rating (TV-Y, TV-PG, PG-13…), returned on every item without asking.</summary>
    [JsonPropertyName("OfficialRating")]
    public string? OfficialRating { get; set; }

    [JsonPropertyName("Path")]
    public string? Path { get; set; }

    [JsonPropertyName("Chapters")]
    public List<JellyfinChapter>? Chapters { get; set; }

    /// <summary>Networks, on a Series item. Episodes carry only the primary one, as SeriesStudio.</summary>
    [JsonPropertyName("Studios")]
    public List<JellyfinNamedItem>? Studios { get; set; }

    /// <summary>
    /// Greater than one when this item is part of a merged version group. Cheap to request, unlike
    /// <see cref="MediaSources"/>, so it is used to decide which items need the expensive lookup.
    /// </summary>
    [JsonPropertyName("MediaSourceCount")]
    public int? MediaSourceCount { get; set; }

    /// <summary>
    /// Every version in this item's group, including itself. Each source's <c>Id</c> is the item id
    /// of that version, which is what links the group together.
    /// </summary>
    [JsonPropertyName("MediaSources")]
    public List<JellyfinMediaSource>? MediaSources { get; set; }
}

internal sealed class JellyfinMediaSource
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

internal sealed class JellyfinNamedItem
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

/// <summary>Series-level metadata that episodes do not carry themselves, or carry staler.</summary>
internal sealed record JellyfinSeriesInfo(string? Name, List<string> Networks, int? Year, string? Rating = null, List<string>? Genres = null);

/// <summary>Which merged version group an item belongs to, and what this version is called.</summary>
internal sealed record JellyfinVersion(string GroupId, string? Name);

/// <summary>A configured Jellyfin library and the folders it maps to.</summary>
internal sealed class JellyfinVirtualFolder
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Locations")]
    public List<string> Locations { get; set; } = [];
}

internal sealed class JellyfinUser
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;
}

internal sealed class JellyfinScheduledTask
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Key")]
    public string? Key { get; set; }

    [JsonPropertyName("State")]
    public string? State { get; set; }

    [JsonPropertyName("CurrentProgressPercentage")]
    public double? CurrentProgressPercentage { get; set; }
}

/// <summary>Jellyfin scanning its own folders for new or changed files.</summary>
public sealed record JellyfinServerScan(string Name, double? Percent);

internal sealed class JellyfinChapter
{
    [JsonPropertyName("StartPositionTicks")]
    public long StartPositionTicks { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }
}

internal sealed class JellyfinMediaSegments
{
    [JsonPropertyName("Items")]
    public List<JellyfinMediaSegment>? Items { get; set; }
}

internal sealed class JellyfinMediaSegment
{
    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("StartTicks")]
    public long StartTicks { get; set; }

    [JsonPropertyName("EndTicks")]
    public long EndTicks { get; set; }
}
