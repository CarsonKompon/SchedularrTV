using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Media;

/// <summary>
/// Presents several media sources as one library, so a single query can span a Jellyfin server and
/// on-disk commercial folders without the scheduling engine knowing either exists. Items pass through
/// any <see cref="IMediaEnricher"/>s on the way out.
/// </summary>
public sealed class CompositeMediaLibrary : IMediaLibrary
{
    private readonly Func<IReadOnlyList<IMediaLibrary>> _sources;
    private readonly Func<IEnumerable<IMediaEnricher>> _enrichers;

    public CompositeMediaLibrary(IEnumerable<IMediaLibrary> sources)
    {
        IReadOnlyList<IMediaLibrary> fixedSources = sources.ToList();
        _sources = () => fixedSources;
        _enrichers = () => [];
    }

    /// <summary>
    /// Asks for the sources (and enrichers) on every use, so media sources and the plugins that enrich them can be
    /// switched on and off while running.
    /// </summary>
    public CompositeMediaLibrary(Func<IReadOnlyList<IMediaLibrary>> sources, Func<IEnumerable<IMediaEnricher>>? enrichers = null)
    {
        _sources = sources;
        _enrichers = enrichers ?? (() => []);
    }

    public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IMediaLibrary> sources = _sources();
        IReadOnlyList<MediaItem> results;
        if (sources.Count == 1)
        {
            results = await sources[0].SearchAsync(expression, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            List<MediaItem> merged = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (IMediaLibrary source in sources)
            {
                foreach (MediaItem item in await source.SearchAsync(expression, cancellationToken).ConfigureAwait(false))
                {
                    if (seen.Add(item.Id))
                    {
                        merged.Add(item);
                    }
                }
            }

            results = merged;
        }

        return Enrich(results);
    }

    private IReadOnlyList<MediaItem> Enrich(IReadOnlyList<MediaItem> items)
    {
        IMediaEnricher[] enrichers = [.. _enrichers()];
        if (enrichers.Length == 0)
        {
            return items;
        }

        MediaItem[] enriched = new MediaItem[items.Count];
        for (int index = 0; index < items.Count; index++)
        {
            MediaItem item = items[index];
            foreach (IMediaEnricher enricher in enrichers)
            {
                item = enricher.Enrich(item);
            }

            enriched[index] = item;
        }

        return enriched;
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        foreach (IMediaLibrary source in _sources())
        {
            await source.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
