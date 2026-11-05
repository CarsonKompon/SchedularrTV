using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>A schedule build shares its library searches between the channels it plans.</summary>
public sealed class SearchScopeTests
{
    private sealed class CountingLibrary : IMediaLibrary
    {
        private int _searches;

        public int Searches => Volatile.Read(ref _searches);

        public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searches);
            return ValueTask.FromResult(QueryEvaluator.Filter([TestMedia.Episode("Alpha", 1, 1, TimeSpan.FromMinutes(22))], expression));
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static QueryExpression Query(string text) => QueryParser.Parse(text);

    [Fact]
    public async Task Inside_a_scope_each_query_is_searched_once_however_many_ask()
    {
        var source = new CountingLibrary();
        var library = new CompositeMediaLibrary([source]);

        using (library.BeginSearchScope())
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await library.SearchAsync(Query("show == 'Alpha'"));
                await library.SearchAsync(Query("kind == 'Episode'"));
            })));
        }

        Assert.Equal(2, source.Searches);
    }

    [Fact]
    public async Task Outside_a_scope_and_after_it_ends_every_search_reaches_the_library()
    {
        var source = new CountingLibrary();
        var library = new CompositeMediaLibrary([source]);

        await library.SearchAsync(Query("show == 'Alpha'"));
        await library.SearchAsync(Query("show == 'Alpha'"));
        Assert.Equal(2, source.Searches);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task lingering;
        using (library.BeginSearchScope())
        {
            await library.SearchAsync(Query("show == 'Alpha'"));
            Assert.Equal(3, source.Searches);

            // Work started inside the scope that only searches once it has ended.
            lingering = Task.Run(async () =>
            {
                await gate.Task;
                await library.SearchAsync(Query("show == 'Alpha'"));
            });
        }

        gate.SetResult();
        await lingering;
        Assert.Equal(4, source.Searches);

        await library.SearchAsync(Query("show == 'Alpha'"));
        Assert.Equal(5, source.Searches);
    }
}
