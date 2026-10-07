using SchedularrTV.Core.Ordering;
using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// One explicit member of a collection, as opposed to a free-form query.
/// </summary>
/// <remarks>
/// Exists so a visual editor can build collections by picking shows rather than composing query
/// strings, while the YAML stays readable. Exactly one of <see cref="Show"/>, <see cref="Movie"/>,
/// <see cref="Collection"/> or <see cref="Query"/> should be set.
/// </remarks>
public sealed class ContentInclude
{
    /// <summary>Series name, exactly as Jellyfin reports it.</summary>
    public string? Show { get; set; }

    /// <summary>
    /// Provider id of the series. Set alongside <see cref="Show"/>, it pins the entry to one series:
    /// a library can hold several shows with the same name, and a name alone would match all of them.
    /// </summary>
    public string? ShowId { get; set; }

    /// <summary>Movie title, exactly as Jellyfin reports it.</summary>
    public string? Movie { get; set; }

    /// <summary>A library collection's name: a Jellyfin BoxSet, a local movie's <c>set</c>, any source's collection.</summary>
    public string? Collection { get; set; }

    /// <summary>Escape hatch for anything the other fields cannot express.</summary>
    public string? Query { get; set; }

    /// <summary>Limits a <see cref="Show"/> to these seasons. Empty means every season.</summary>
    public List<int> Seasons { get; set; } = [];

    /// <summary>Limits a <see cref="Show"/> to specific episodes, as <c>S01E03</c>.</summary>
    public List<string> Episodes { get; set; } = [];

    /// <summary>
    /// Overrides the collection's playback order for this title alone, so a non-linear show can
    /// shuffle inside a collection that otherwise runs chronologically.
    /// </summary>
    public PlaybackOrder? Order { get; set; }

    /// <summary>
    /// How many turns this title takes per rotation. 2 means it comes up twice as often as a title
    /// with the default weight of 1.
    /// </summary>
    public int? Weight { get; set; }

    /// <summary>
    /// Path of a clip played once at the top of any slot this title fills, for shows whose episodes
    /// are stored without their title sequence. Usually an entry from the show's Extras folder.
    /// </summary>
    /// <remarks>Shorthand for a single-clip intro block; the loader folds it into <see cref="Bumpers"/>.</remarks>
    public string? Intro { get; set; }

    /// <summary>
    /// Path of a clip played once at the end of any slot this title fills, before the break that
    /// follows it. The closing credits counterpart to <see cref="Intro"/>.
    /// </summary>
    /// <remarks>Shorthand for a single-clip outro block; the loader folds it into <see cref="Bumpers"/>.</remarks>
    public string? Outro { get; set; }

    /// <summary>
    /// Clip blocks played around this title's episodes, in the order listed within each position.
    /// </summary>
    public List<BumperBlock> Bumpers { get; set; } = [];

    [YamlIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Show)
        && string.IsNullOrWhiteSpace(ShowId)
        && string.IsNullOrWhiteSpace(Movie)
        && string.IsNullOrWhiteSpace(Collection)
        && string.IsNullOrWhiteSpace(Query);

    /// <summary>Short human-readable summary for list views.</summary>
    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Show) || !string.IsNullOrWhiteSpace(ShowId))
        {
            string label = Show ?? ShowId!;

            if (Episodes.Count > 0)
            {
                return $"{label} ({string.Join(", ", Episodes)})";
            }

            return Seasons.Count > 0
                ? $"{label} (season {string.Join(", ", Seasons)})"
                : label;
        }

        if (!string.IsNullOrWhiteSpace(Movie))
        {
            return Movie;
        }

        return !string.IsNullOrWhiteSpace(Collection) ? $"collection: {Collection}" : Query ?? "(empty)";
    }
}
