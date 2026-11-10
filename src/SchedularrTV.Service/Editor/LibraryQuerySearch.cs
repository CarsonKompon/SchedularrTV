using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// A library page's search box read as a query when it is one (<c>genre:'Comedy' AND year &lt; 1995</c>): the library is
/// searched a moment after typing stops, and <see cref="Matches"/> holds the keys of what matched (the shows with a
/// matching episode, say). Plain words stay a name search, left to the page.
/// </summary>
public sealed class LibraryQuerySearch
{
    private int _version;

    /// <summary>The text is a query (its results may still be on their way: <see cref="Matches"/> is null until then).</summary>
    public bool Mode { get; private set; }

    /// <summary>What the query matched, by the key the page gave; null while searching or when not a query.</summary>
    public HashSet<string>? Matches { get; private set; }

    /// <summary>Why text that looks like a query doesn't read as one (it's searched as words instead).</summary>
    public string? Error { get; private set; }

    /// <param name="key">The page's key for an item (a show's id for an episode, a movie's title), or null to leave it out.</param>
    /// <param name="changed">Told when results arrive.</param>
    public async Task UpdateAsync(string text, IMediaLibrary library, Func<MediaItem, string?> key, Action changed,
        IEqualityComparer<string>? comparer = null)
    {
        int version = ++_version;
        if (!QueryFields.IsQuery(text, out QueryExpression expression, out string? error))
        {
            Mode = false;
            Matches = null;
            Error = error;
            return;
        }

        Error = null;
        if (!Mode)
        {
            Matches = null; // a new query: nothing to show until it's searched (an edited one keeps its last results meanwhile)
        }

        Mode = true;
        await Task.Delay(250);
        if (version != _version)
        {
            return; // typed again
        }

        try
        {
            IReadOnlyList<MediaItem> found = await library.SearchAsync(expression);
            if (version != _version)
            {
                return;
            }

            Matches = found.Select(key).OfType<string>().ToHashSet(comparer ?? StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _version)
            {
                return;
            }

            Mode = false;
            Matches = null;
            Error = $"couldn't search the library ({ex.Message})";
        }

        changed();
    }
}
