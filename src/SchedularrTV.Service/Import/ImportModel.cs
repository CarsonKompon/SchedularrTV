namespace SchedularrTV.Service.Import;

/// <summary>Where an import comes from.</summary>
public enum ImportSource
{
    ErsatzTv,
    Tunarr
}

/// <summary>How the other app played a set of programmes, as near as SchedularrTV has it.</summary>
public enum ImportedOrder
{
    /// <summary>Not said: the importer picks (season and episode order for a show, shuffled otherwise).</summary>
    Default,
    Chronological,
    SeasonEpisode,
    Shuffle,
    Random
}

/// <summary>
/// One programme as the other app knows it: its media server's id (when it came from Jellyfin), its file, and enough of
/// its metadata to find it another way (by show, season and episode, or a film's title and year).
/// </summary>
public sealed record ImportedMedia(
    string? JellyfinId,
    string? Path,
    string Title,
    string? Show = null,
    string? ShowJellyfinId = null,
    int? Season = null,
    int? Episode = null,
    bool IsMovie = false,
    int? Year = null,
    TimeSpan Duration = default);

/// <summary>What a slot or rotation plays, from the other app.</summary>
public enum ImportedContentKind
{
    /// <summary>A whole show (optionally some of its seasons).</summary>
    Show,

    /// <summary>A list of particular programmes (a collection, a custom show, single films or episodes).</summary>
    Items,

    /// <summary>Every film.</summary>
    AllMovies,

    /// <summary>Several of the others together (a multi-collection).</summary>
    Group,

    /// <summary>Something that can't be brought over (a smart collection's search, a playlist): noted, then skipped.</summary>
    Unsupported
}

public sealed class ImportedContent
{
    public ImportedContentKind Kind { get; init; }

    /// <summary>What the other app called it (a show's title, a collection's name).</summary>
    public string Name { get; init; } = string.Empty;

    public string? ShowJellyfinId { get; init; }

    public List<int> Seasons { get; init; } = [];

    public List<ImportedMedia> Items { get; init; } = [];

    public List<ImportedContent> Children { get; init; } = [];

    public ImportedOrder Order { get; init; }

    /// <summary>How often it comes up in a rotation, relative to the rest.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>A collection to keep under this name (an ErsatzTV collection, a Tunarr custom show), shared by the channels using it.</summary>
    public string? CollectionName { get; init; }

    /// <summary>For <see cref="ImportedContentKind.Unsupported"/>: why.</summary>
    public string? Problem { get; init; }

    /// <summary>The same content with another weight (a shared collection is used by many items, each its own way).</summary>
    public ImportedContent WithWeight(double weight) => new()
    {
        Kind = Kind, Name = Name, ShowJellyfinId = ShowJellyfinId, Seasons = Seasons, Items = Items, Children = Children, Order = Order,
        CollectionName = CollectionName, Problem = Problem, Weight = weight
    };

    public static ImportedContent ForShow(string title, string? jellyfinId = null, IEnumerable<int>? seasons = null, ImportedOrder order = ImportedOrder.Default) =>
        new() { Kind = ImportedContentKind.Show, Name = title, ShowJellyfinId = jellyfinId, Seasons = [.. seasons ?? []], Order = order };

    public static ImportedContent Unsupported(string name, string problem) => new() { Kind = ImportedContentKind.Unsupported, Name = name, Problem = problem };
}

/// <summary>A time in the day (or week) the other app always played something at, until the next one.</summary>
/// <param name="Days">The days it's on; empty for every day.</param>
/// <param name="Length">How long it runs when the other app said; else until the next slot.</param>
public sealed record ImportedSlot(TimeSpan Start, ImportedContent Content, IReadOnlyList<DayOfWeek> Days, TimeSpan? Length = null);

/// <summary>One of the other app's channels.</summary>
public sealed class ImportedChannel
{
    public required string SourceId { get; init; }

    public required string Name { get; init; }

    /// <summary>Its number; a fractional ErsatzTV number (5.1) is rounded down.</summary>
    public int Number { get; init; }

    public string? Group { get; init; }

    /// <summary>Its logo: a web address, or a file on this computer.</summary>
    public string? Logo { get; init; }

    /// <summary>Played at set times.</summary>
    public List<ImportedSlot> Slots { get; } = [];

    /// <summary>Played round and round (between the slots, or all day without any).</summary>
    public List<ImportedContent> Rotation { get; } = [];

    /// <summary>Played in the breaks (an ErsatzTV filler preset, a Tunarr filler list).</summary>
    public List<ImportedMedia> Filler { get; } = [];

    /// <summary>What didn't come over as it was, in words.</summary>
    public List<string> Notes { get; } = [];
}

/// <summary>Everything read from the other app.</summary>
public sealed class ImportedSchedule
{
    public required ImportSource Source { get; init; }

    public required string From { get; init; }

    public List<ImportedChannel> Channels { get; } = [];

    public List<string> Warnings { get; } = [];
}
