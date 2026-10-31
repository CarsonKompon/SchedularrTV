using SchedularrTV.Core.Media;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Editing a show's or movie's metadata from its library page: finds the source's <see cref="IMetadataEditor"/> (any
/// plugin's), writes through it, and records each change in <see cref="MetadataHistory"/> so it can be reverted. Knows
/// nothing about any particular source.
/// </summary>
public sealed class LibraryMetadata(IEnumerable<IMetadataEditor> editors, MetadataHistory history)
{
    /// <summary>The editor for the item's source, or null when its source can't be edited.</summary>
    public IMetadataEditor? EditorFor(string? id) => MetadataEditors.For(editors, id);

    public IReadOnlyList<MetadataHistoryEntry> History(string id) => history.Read(id);

    /// <summary>Writes the fields of <paramref name="edited"/> that differ from <paramref name="current"/>, and records the change.</summary>
    public async Task<MetadataHistoryEntry?> WriteAsync(IMetadataEditor editor, string id, MetadataItemKind kind,
        EditableMetadata current, EditableMetadata edited, CancellationToken cancellationToken = default)
    {
        MetadataFields fields = current.Differences(edited) & editor.Supported;
        if (fields == MetadataFields.None)
        {
            return null;
        }

        await editor.WriteAsync(id, kind, edited, fields, cancellationToken).ConfigureAwait(false);
        return history.Record(id, editor.Name, current.Normalized(), Merge(current, edited, fields).Normalized(), fields);
    }

    /// <summary>
    /// Puts back what <paramref name="entry"/> changed: its fields as they were before it, read against the source as it
    /// is now (so only what still differs is written). Recorded as a change of its own, which can be reverted too.
    /// </summary>
    public async Task<MetadataHistoryEntry?> RevertAsync(IMetadataEditor editor, string id, MetadataItemKind kind, MetadataHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        EditableMetadata current = await editor.ReadAsync(id, kind, cancellationToken).ConfigureAwait(false)
                                   ?? throw new MetadataWriteException("The item can't be found at its source any more.");
        EditableMetadata restored = Merge(current, entry.Before, entry.Fields);
        MetadataFields fields = current.Differences(restored) & editor.Supported;
        if (fields == MetadataFields.None)
        {
            return null;
        }

        await editor.WriteAsync(id, kind, restored, fields, cancellationToken).ConfigureAwait(false);
        return history.Record(id, editor.Name, current, restored.Normalized(), fields, $"Reverted the change from {entry.At.LocalDateTime:d MMM, h:mm tt}");
    }

    /// <summary><paramref name="baseline"/> with <paramref name="fields"/> taken from <paramref name="from"/>.</summary>
    public static EditableMetadata Merge(EditableMetadata baseline, EditableMetadata from, MetadataFields fields) => baseline with
    {
        Title = fields.HasFlag(MetadataFields.Title) ? from.Title : baseline.Title,
        SortTitle = fields.HasFlag(MetadataFields.SortTitle) ? from.SortTitle : baseline.SortTitle,
        Year = fields.HasFlag(MetadataFields.Year) ? from.Year : baseline.Year,
        Overview = fields.HasFlag(MetadataFields.Overview) ? from.Overview : baseline.Overview,
        Rating = fields.HasFlag(MetadataFields.Rating) ? from.Rating : baseline.Rating,
        Genres = fields.HasFlag(MetadataFields.Genres) ? from.Genres : baseline.Genres,
        Studios = fields.HasFlag(MetadataFields.Studios) ? from.Studios : baseline.Studios,
        Tags = fields.HasFlag(MetadataFields.Tags) ? from.Tags : baseline.Tags
    };

    /// <summary>The changed fields' names, for the history list: "Title, Genres".</summary>
    public static string Describe(MetadataFields fields) =>
        string.Join(", ", Enum.GetValues<MetadataFields>()
            .Where(field => field is not MetadataFields.None and not MetadataFields.All && fields.HasFlag(field))
            .Select(Label));

    public static string Label(MetadataFields field) => field switch
    {
        MetadataFields.SortTitle => "Sort title",
        MetadataFields.Studios => "Networks / studios",
        _ => field.ToString()
    };

    /// <summary>A field's value as text, for showing before and after.</summary>
    public static string Value(EditableMetadata metadata, MetadataFields field) => field switch
    {
        MetadataFields.Title => metadata.Title ?? "—",
        MetadataFields.SortTitle => metadata.SortTitle ?? "—",
        MetadataFields.Year => metadata.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—",
        MetadataFields.Overview => metadata.Overview ?? "—",
        MetadataFields.Rating => metadata.Rating ?? "—",
        MetadataFields.Genres => metadata.Genres.Count > 0 ? string.Join(", ", metadata.Genres) : "—",
        MetadataFields.Studios => metadata.Studios.Count > 0 ? string.Join(", ", metadata.Studios) : "—",
        MetadataFields.Tags => metadata.Tags.Count > 0 ? string.Join(", ", metadata.Tags) : "—",
        _ => "—"
    };
}
