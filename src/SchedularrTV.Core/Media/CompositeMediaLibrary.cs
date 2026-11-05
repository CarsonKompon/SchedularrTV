using SchedularrTV.Core.Query;

namespace SchedularrTV.Core.Media;

/// <summary>
/// Presents several media sources as one library, so a single query can span a Jellyfin server and
/// on-disk commercial folders without the scheduling engine knowing either exists. Items pass through
/// any <see cref="IMediaEnricher"/>s on the way out.
/// </summary>
public sealed class CompositeMediaLibrary : IMediaLibrary, ISearchScopeSource
{
    private readonly Func<IReadOnlyList<IMediaLibrary>> _sources;
    private readonly Func<IEnumerable<IMediaEnricher>> _enrichers;

    // Searches remembered by query, for the flow of work inside a scope (a whole schedule build, every channel of it).
    private readonly AsyncLocal<SearchScope?> _scope = new();

    private sealed class SearchScope
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<MediaItem>>>> Results =
            new(StringComparer.Ordinal);

        // Work started inside the scope that outlives it (a render handed off to the background) still carries it, so
        // an ended scope has to say so rather than keep serving what it remembered.
        public volatile bool Ended;
    }

    public IDisposable BeginSearchScope()
    {
        if (_scope.Value is { Ended: false })
        {
            return new Ender(() => { }); // already inside one: it goes on remembering
        }

        var scope = new SearchScope();
        _scope.Value = scope;
        return new Ender(() =>
        {
            scope.Ended = true;
            scope.Results.Clear();
            _scope.Value = null;
        });
    }

    private sealed class Ender(Action end) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                end();
            }
        }
    }

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
        if (_scope.Value is not { Ended: false } scope)
        {
            return await SearchSourcesAsync(expression, cancellationToken).ConfigureAwait(false);
        }

        // One search per query however many channels ask at once. Not cancelled by the first asker's token, as others
        // may be waiting on it; a failure isn't remembered, so the next asker tries again.
        string key = expression.ToQueryString();
        Lazy<Task<IReadOnlyList<MediaItem>>> shared = scope.Results.GetOrAdd(key,
            _ => new Lazy<Task<IReadOnlyList<MediaItem>>>(() => SearchSourcesAsync(expression, CancellationToken.None).AsTask()));
        try
        {
            return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && shared.Value.IsFaulted)
        {
            scope.Results.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<MediaItem>>>>(key, shared));
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<MediaItem>> SearchSourcesAsync(QueryExpression expression, CancellationToken cancellationToken)
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
