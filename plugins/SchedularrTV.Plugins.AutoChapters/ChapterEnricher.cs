using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace SchedularrTV.AutoChapters;

/// <summary>
/// Gives programmes that have no chapter markers of their own the ones found in their files, so mid-roll breaks go
/// at the fades to black instead of on a fixed interval. Items with real chapters are left alone.
/// </summary>
public sealed class ChapterEnricher(ChapterStore store, AutoChaptersSettings settings) : IMediaEnricher
{
    public const string SourceAttribute = "chapters_source";
    public const string SourceValue = "auto";

    private static readonly IReadOnlyDictionary<string, string> Marked =
        new Dictionary<string, string> { [SourceAttribute] = SourceValue }.ToFrozenDictionary();

    // The same library item comes back on every search; handing out the same copy each time keeps the items the
    // scheduler compares stable. Started afresh whenever new markers are stored.
    private ConditionalWeakTable<MediaItem, MediaItem> _copies = new();
    private int _copiesVersion = -1;

    public MediaItem Enrich(MediaItem item)
    {
        // Read before the record: a record saved in between then bumps the version past this one, so a copy made from
        // the older record isn't kept under the newer version.
        int version = store.Version;
        if (item.Chapters.Count > 0 || item.Kind is not (MediaKind.Episode or MediaKind.Movie) || !settings.UseForMidRolls
            || store.Find(item.Path) is not { Error: null, Markers.Count: > 0 } record)
        {
            return item;
        }

        if (version != _copiesVersion)
        {
            _copies = new ConditionalWeakTable<MediaItem, MediaItem>();
            _copiesVersion = version;
        }

        return _copies.GetValue(item, original => original.WithChapters(record.MarkerTimes, Marked));
    }

    /// <summary>Whether an item needs analysing: an episode or movie with a file and no chapter markers of its own.</summary>
    public static bool Wants(MediaItem item) =>
        item.Kind is MediaKind.Episode or MediaKind.Movie
        && !string.IsNullOrWhiteSpace(item.Path)
        && (item.Chapters.Count == 0 || (item.Attributes.TryGetValue(SourceAttribute, out string? source) && source == SourceValue));
}
