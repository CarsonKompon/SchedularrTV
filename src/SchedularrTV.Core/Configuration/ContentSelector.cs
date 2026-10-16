using SchedularrTV.Core.Ordering;

namespace SchedularrTV.Core.Configuration;

/// <summary>Which media a rule draws from. Exactly one of <see cref="Collection"/> or <see cref="Query"/> is required.</summary>
public sealed class ContentSelector
{
    /// <summary>Key into <see cref="SchedulerConfig.GlobalCollections"/>.</summary>
    public string? Collection { get; set; }

    /// <summary>Inline query, e.g. <c>show:'SpongeBob SquarePants'</c>.</summary>
    public string? Query { get; set; }

    /// <summary>
    /// Explicitly picked shows, movies and seasons, OR-ed together and unioned with
    /// <see cref="Query"/>. This is what the visual editor builds when you add titles to a
    /// collection.
    /// </summary>
    public List<ContentInclude> Include { get; set; } = [];

    /// <summary>Extra predicate AND-ed onto the resolved pool (spec 2.2 smart search).</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// <see cref="FilterScope.Collection"/> narrows the selector's own pool;
    /// <see cref="FilterScope.Library"/> ignores the pool and searches the whole library.
    /// </summary>
    public FilterScope FilterScope { get; set; } = FilterScope.Collection;

    /// <summary>Null inherits the order of the referenced global collection, else defaults to Shuffle.</summary>
    public PlaybackOrder? Order { get; set; }

    /// <summary>
    /// Drop cadence for every show here that uses <see cref="PlaybackOrder.Drop"/>, unless a title sets its
    /// own on its include. Null inherits from the referenced global collection, else one episode a week.
    /// </summary>
    public DropConfig? Drop { get; set; }

    /// <summary>
    /// How whole titles take turns. Without this the pool is flat, so <c>chronological</c> sorts every
    /// episode of every show by air date and the oldest series monopolises the channel. Setting it
    /// makes <see cref="Order"/> apply <em>within</em> each title while titles rotate.
    /// </summary>
    public TitleOrder? TitleOrder { get; set; }

    /// <summary>Optional runtime bounds; useful to keep interstitials genuinely short.</summary>
    public TimeSpan? MinDuration { get; set; }

    public TimeSpan? MaxDuration { get; set; }

    /// <summary>
    /// Seasons dropped from every title here, typically <c>[0]</c> to keep specials and behind-the-scenes
    /// clips out. Applied to titles that do not name their seasons, so new seasons are picked up
    /// automatically; a title that explicitly lists seasons is left exactly as written.
    /// </summary>
    public List<int> ExcludeSeasons { get; set; } = [];

    /// <summary>
    /// Which alternate version to play when a title exists in several (e.g. web vs DVD rip).
    /// Null inherits from the referenced global collection. Regardless of this setting a version
    /// group only ever contributes one item, so alternates never play as if they were extra episodes.
    /// </summary>
    public VersionSelection? Versions { get; set; }

    /// <summary>Stable identity for playback-state persistence. Defaults to a hash of the selector.</summary>
    public string? StateKey { get; set; }
}

public enum FilterScope
{
    /// <summary>Apply the filter on top of the selector's collection/query.</summary>
    Collection,

    /// <summary>Apply the filter against the entire media library, ignoring the collection.</summary>
    Library
}
