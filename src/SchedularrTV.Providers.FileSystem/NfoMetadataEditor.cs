using System.Text;
using Microsoft.Extensions.Logging;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.FileSystem;

/// <summary>
/// Writes edited show and movie metadata into the .nfo files of local libraries (see <see cref="IMetadataEditor"/>):
/// a show's <c>tvshow.nfo</c>, a movie's <c>movie.nfo</c> or <c>&lt;file name&gt;.nfo</c>. A copy of the file as it was
/// is kept beside it as <c>.nfo.bak</c>.
/// </summary>
public sealed class NfoMetadataEditor(FileSystemMediaLibrary library, ILogger<NfoMetadataEditor> logger) : IMetadataEditor
{
    public string Name => "Local folders";

    public string WriteLabel => "Update NFO file";

    public MetadataFields Supported => MetadataFields.All;

    public bool Owns(string id) =>
        id.StartsWith(LocalLibraryIds.SeriesPrefix, StringComparison.Ordinal) || id.StartsWith(LocalLibraryIds.ItemPrefix, StringComparison.Ordinal);

    public async ValueTask<EditableMetadata?> ReadAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default)
    {
        if (await TargetAsync(id, kind, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return null;
        }

        if (File.Exists(target.Path) && NfoReader.ReadOne(target.Path, Root(kind)) is { } nfo)
        {
            return new EditableMetadata
            {
                Title = nfo.Title, SortTitle = nfo.SortTitle, Year = nfo.Year, Overview = nfo.Plot, Rating = nfo.Rating,
                Genres = nfo.Genres, Studios = nfo.Studios, Tags = nfo.Tags
            }.Normalized();
        }

        // No file yet: what the library made of the names, so the form starts from what's shown.
        return target.Fallback;
    }

    public async ValueTask<string?> DescribeTargetAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken = default)
    {
        if (await TargetAsync(id, kind, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return null;
        }

        return File.Exists(target.Path)
            ? $"This rewrites {target.Path}. A copy of the file as it is now is kept as {Path.GetFileName(target.Path)}.bak beside it. "
              + "Media servers and players that read this folder pick the change up on their next scan."
            : $"This creates {target.Path}. Media servers and players that read this folder pick it up on their next scan.";
    }

    public async ValueTask WriteAsync(string id, MetadataItemKind kind, EditableMetadata metadata, MetadataFields fields,
        CancellationToken cancellationToken = default)
    {
        if (await TargetAsync(id, kind, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            throw new MetadataWriteException("This item is no longer in your local folders. Rescan the library and try again.");
        }

        try
        {
            string? existing = File.Exists(target.Path) ? await File.ReadAllTextAsync(target.Path, cancellationToken).ConfigureAwait(false) : null;
            string updated = NfoWriter.Update(existing, Root(kind), metadata, fields);

            if (existing is not null)
            {
                File.Copy(target.Path, target.Path + ".bak", overwrite: true);
            }

            // Written beside it and swapped in, so a failure part way never leaves half a file.
            string temp = target.Path + ".tmp";
            await File.WriteAllTextAsync(temp, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, target.Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't write {Path}.", target.Path);
            throw new MetadataWriteException($"Couldn't write {target.Path}: {ex.Message}", ex);
        }

        // The library reads the folders again in the background, so lists and schedules show the change.
        _ = Task.Run(async () =>
        {
            try
            {
                await library.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Rescanning local folders after a metadata change failed.");
            }
        }, CancellationToken.None);
    }

    private static string Root(MetadataItemKind kind) => kind == MetadataItemKind.Show ? "tvshow" : "movie";

    private sealed record Target(string Path, EditableMetadata Fallback);

    /// <summary>The file a show's or movie's metadata lives in (existing or to be created), and the library's view of it.</summary>
    private async ValueTask<Target?> TargetAsync(string id, MetadataItemKind kind, CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaItem> items = await library.ItemsAsync(cancellationToken).ConfigureAwait(false);
        if (kind == MetadataItemKind.Show)
        {
            if (await library.ShowFolderAsync(id, cancellationToken).ConfigureAwait(false) is not { } folder)
            {
                return null;
            }

            MediaItem? episode = items.FirstOrDefault(item => item.SeriesId == id);
            return new Target(System.IO.Path.Combine(folder, "tvshow.nfo"), new EditableMetadata
            {
                Title = episode?.SeriesName, Year = episode?.SeriesYear, Rating = episode?.Rating,
                Genres = episode?.Genres ?? [], Studios = episode?.Networks ?? []
            }.Normalized());
        }

        if (items.FirstOrDefault(item => item.Id == id && item.Kind == MediaKind.Movie) is not { Path: { Length: > 0 } file } movie)
        {
            return null;
        }

        // Its own .nfo, else the folder's movie.nfo (which the scanner also reads), else a new one named for the file.
        string beside = System.IO.Path.ChangeExtension(file, ".nfo");
        string folderNfo = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? string.Empty, "movie.nfo");
        string path = File.Exists(beside) ? beside : File.Exists(folderNfo) ? folderNfo : beside;
        return new Target(path, new EditableMetadata
        {
            Title = movie.Title, Year = movie.PremiereDate?.Year, Overview = movie.Overview, Rating = movie.Rating,
            Genres = movie.Genres, Studios = movie.Networks
        }.Normalized());
    }
}
