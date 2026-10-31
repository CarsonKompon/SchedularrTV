namespace SchedularrTV.Core.Media;

/// <summary>What a metadata edit is about: a whole show (series) or a movie.</summary>
public enum MetadataItemKind
{
    Show,
    Movie
}

/// <summary>The fields a <see cref="IMetadataEditor"/> can write. A source leaves out what it has no place for.</summary>
[Flags]
public enum MetadataFields
{
    None = 0,
    Title = 1,
    SortTitle = 2,
    Year = 4,
    Overview = 8,
    Rating = 16,
    Genres = 32,
    Studios = 64,
    Tags = 128,
    All = Title | SortTitle | Year | Overview | Rating | Genres | Studios | Tags
}

/// <summary>
/// A show's or movie's descriptive metadata as the library page edits it. Empty text and empty lists mean "none".
/// Studios are a show's networks, or a movie's studios.
/// </summary>
public sealed record EditableMetadata
{
    public string? Title { get; init; }
    public string? SortTitle { get; init; }
    public int? Year { get; init; }
    public string? Overview { get; init; }
    public string? Rating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Studios { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The fields that differ from <paramref name="other"/> (text compared trimmed, lists in order, ignoring case).</summary>
    public MetadataFields Differences(EditableMetadata other)
    {
        MetadataFields changed = MetadataFields.None;
        if (!SameText(Title, other.Title)) changed |= MetadataFields.Title;
        if (!SameText(SortTitle, other.SortTitle)) changed |= MetadataFields.SortTitle;
        if (Year != other.Year) changed |= MetadataFields.Year;
        if (!SameText(Overview, other.Overview)) changed |= MetadataFields.Overview;
        if (!SameText(Rating, other.Rating)) changed |= MetadataFields.Rating;
        if (!SameList(Genres, other.Genres)) changed |= MetadataFields.Genres;
        if (!SameList(Studios, other.Studios)) changed |= MetadataFields.Studios;
        if (!SameList(Tags, other.Tags)) changed |= MetadataFields.Tags;
        return changed;
    }

    /// <summary>A copy with text trimmed (blank becomes null) and lists trimmed, without blanks or repeats.</summary>
    public EditableMetadata Normalized() => new()
    {
        Title = Clean(Title),
        SortTitle = Clean(SortTitle),
        Year = Year is >= 1800 and <= 2200 ? Year : null,
        Overview = Clean(Overview),
        Rating = Clean(Rating),
        Genres = CleanList(Genres),
        Studios = CleanList(Studios),
        Tags = CleanList(Tags)
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string>? values) =>
        [.. (values ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool SameText(string? left, string? right) =>
        string.Equals(Clean(left), Clean(right), StringComparison.Ordinal);

    private static bool SameList(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        CleanList(left).SequenceEqual(CleanList(right), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Writes a show's or movie's metadata back to where a media source keeps it: a media server (through its API), or
/// the .nfo files beside local videos. Each source that can edit registers one; the app asks each in turn for the one
/// that <see cref="Owns"/> an id. A source that can't edit (a read-only server, a source without metadata files)
/// simply doesn't register one, and its items' pages show their metadata read-only.
/// </summary>
public interface IMetadataEditor
{
    /// <summary>The source's name, for the page: "Jellyfin", "Local folders"…</summary>
    string Name { get; }

    /// <summary>What the write button says, naming where the change goes: "Push to Jellyfin", "Update NFO file".</summary>
    string WriteLabel { get; }

    /// <summary>The fields it can write; the page only offers these.</summary>
    MetadataFields Supported { get; }

    /// <summary>Whether <paramref name="id"/> (a series' or movie's id, as the library reports it) is one of this source's.</summary>
    bool Owns(string id);

    /// <summary>The metadata as the source has it now (read fresh, not from the library's cache); null when the item can't be found.</summary>
    ValueTask<EditableMetadata?> ReadAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// A sentence saying exactly what <see cref="WriteAsync"/> will change, for the "are you sure?" step: the server
    /// and item, or the file's path (and whether it's created). Null when the item can't be found.
    /// </summary>
    ValueTask<string?> DescribeTargetAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="metadata"/>'s <paramref name="fields"/>, leaving everything else the source has about
    /// the item as it was. Throws <see cref="MetadataWriteException"/> with a message for the page when it can't.
    /// </summary>
    ValueTask WriteAsync(string id, MetadataItemKind kind, EditableMetadata metadata, MetadataFields fields, CancellationToken cancellationToken = default);
}

/// <summary>A metadata write that failed, with a message fit to show.</summary>
public sealed class MetadataWriteException(string message, Exception? inner = null) : Exception(message, inner);

public static class MetadataEditors
{
    /// <summary>The first editor that owns <paramref name="id"/>, or null when its source can't be edited.</summary>
    public static IMetadataEditor? For(IEnumerable<IMetadataEditor> editors, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : editors.FirstOrDefault(editor => editor.Owns(id));
}
