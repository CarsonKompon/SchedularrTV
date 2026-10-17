using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Scheduling;

/// <summary>
/// What a <see cref="ContentSelector"/> matches, worked out the same way a schedule build does, for editors that need
/// a channel's or block's shows without building it.
/// </summary>
public static class ContentSelectorQuery
{
    public const int MaxCollectionDepth = 8;

    /// <summary>
    /// The query a selector stands for: its global collection, query and included titles unioned, then narrowed by its
    /// filter (or replaced by it, for a library-scoped filter).
    /// </summary>
    /// <exception cref="SchedulerConfigurationException">An unknown or cyclic collection, or a selector naming nothing.</exception>
    public static QueryExpression Expression(SchedulerConfig config, ContentSelector selector, int depth = 0)
    {
        if (depth > MaxCollectionDepth)
        {
            throw new SchedulerConfigurationException($"Global collection reference cycle detected at '{selector.Collection}'.");
        }

        List<QueryExpression> pool = [];

        if (!string.IsNullOrWhiteSpace(selector.Collection))
        {
            if (!config.GlobalCollections.TryGetValue(selector.Collection, out ContentSelector? global))
            {
                throw new SchedulerConfigurationException($"Unknown global collection '{selector.Collection}'.");
            }

            pool.Add(Expression(config, global, depth + 1));
        }

        if (!string.IsNullOrWhiteSpace(selector.Query))
        {
            QueryExpression parsed = QueryParser.Parse(selector.Query);

            // A raw query names no titles, so the exclusion simply narrows the whole thing.
            pool.Add(selector.ExcludeSeasons.Count > 0
                ? QueryExpression.And([parsed, ContentIncludeCompiler.ExcludingSeasons(selector.ExcludeSeasons)])
                : parsed);
        }

        if (selector.Include.Count > 0)
        {
            pool.Add(ContentIncludeCompiler.Compile(selector.Include, selector.ExcludeSeasons));
        }

        if (pool.Count == 0)
        {
            throw new SchedulerConfigurationException("A content selector requires 'collection', 'query' or 'include'.");
        }

        // Several sources union together: a collection is everything it names plus anything it matches.
        QueryExpression baseExpression = QueryExpression.Or(pool);

        if (!string.IsNullOrWhiteSpace(selector.Filter))
        {
            QueryExpression filter = QueryParser.Parse(selector.Filter);

            // Library scope deliberately discards the collection so smart searches can reach
            // outside the block's own shows (spec 2.2 scoped layering).
            baseExpression = selector.FilterScope == FilterScope.Library
                ? filter
                : QueryExpression.And([baseExpression, filter]);
        }

        return baseExpression;
    }

    /// <summary>Everything a channel programmes: its base rotation, and every block's and anchor's shows.</summary>
    public static IEnumerable<ContentSelector> Of(ChannelConfig channel)
    {
        if (channel.BaseRotation is { } rotation)
        {
            yield return rotation;
        }

        foreach (ContentSelector selector in channel.Anchors.SelectMany(anchor => anchor.AllSelectors()))
        {
            yield return selector;
        }

        foreach (BlockConfig block in channel.Blocks)
        {
            foreach (ContentSelector selector in Of(block))
            {
                yield return selector;
            }
        }
    }

    /// <summary>Everything a block programmes: its shows and its anchors'.</summary>
    public static IEnumerable<ContentSelector> Of(BlockConfig block) =>
        block.Shows.Concat(block.Anchors.SelectMany(anchor => anchor.AllSelectors()));

    /// <summary>
    /// The show (series) and movie titles <paramref name="selectors"/> match among <paramref name="items"/>, sorted.
    /// Selectors that can't be resolved (a missing collection, a query with a typo) are skipped.
    /// </summary>
    public static IReadOnlyList<string> Titles(SchedulerConfig config, IEnumerable<ContentSelector> selectors, IReadOnlyList<MediaItem> items)
    {
        (IReadOnlyList<string> shows, IReadOnlyList<string> movies) = ShowsAndMovies(config, selectors, items);
        return [.. shows.Concat(movies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A film rather than an episode of a show: a movie, or anything that isn't an episode and has no series.
    /// </summary>
    public static bool IsMovie(MediaItem item) =>
        item.Kind == MediaKind.Movie || (item.Kind != MediaKind.Episode && string.IsNullOrWhiteSpace(item.SeriesName));

    /// <summary>The title a show is known by (its series), or a movie's own.</summary>
    public static string TitleOf(MediaItem item) => (string.IsNullOrWhiteSpace(item.SeriesName) ? item.Title : item.SeriesName)?.Trim() ?? string.Empty;

    /// <summary>
    /// The show (series) titles and the movie titles <paramref name="selectors"/> match among <paramref name="items"/>,
    /// each sorted. Selectors that can't be resolved (a missing collection, a query with a typo) are skipped.
    /// </summary>
    public static (IReadOnlyList<string> Shows, IReadOnlyList<string> Movies) ShowsAndMovies(SchedulerConfig config,
        IEnumerable<ContentSelector> selectors, IReadOnlyList<MediaItem> items)
    {
        HashSet<string> shows = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> movies = new(StringComparer.OrdinalIgnoreCase);
        foreach (ContentSelector selector in selectors.Distinct())
        {
            QueryExpression expression;
            try
            {
                expression = Expression(config, selector);
            }
            catch (Exception ex) when (ex is SchedulerConfigurationException or QueryParseException)
            {
                continue;
            }

            foreach (MediaItem item in QueryEvaluator.Filter(items, expression))
            {
                if ((selector.MinDuration is { } min && item.Duration < min) || (selector.MaxDuration is { } max && item.Duration > max))
                {
                    continue;
                }

                string title = TitleOf(item);
                if (title.Length > 0)
                {
                    (IsMovie(item) ? movies : shows).Add(title);
                }
            }
        }

        return (shows.Order(StringComparer.OrdinalIgnoreCase).ToList(), movies.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
