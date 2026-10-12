using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

/// <summary>
/// Re-reading the library (every cache window, or after a settings change) must never leave the pages, tag pickers and
/// autocompletes empty while it runs: what was read before is served until the new read is in.
/// </summary>
public class LibraryRereadTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task An_old_snapshot_is_served_while_it_is_read_again()
    {
        var clock = new Clock();
        var release = new TaskCompletionSource();
        int reads = 0;
        var snapshot = new RefreshingSnapshot<string>(async (_, _) =>
        {
            int read = Interlocked.Increment(ref reads);
            if (read > 1)
            {
                await release.Task;
            }

            return $"read {read}";
        }, "test", clock);

        Assert.Equal("read 1", await snapshot.GetAsync("key", Window));

        var refreshed = new TaskCompletionSource();
        snapshot.RefreshedInBackground += () => refreshed.TrySetResult();
        clock.Now += Window + TimeSpan.FromMinutes(1);

        // Old, so it's read again, but nobody waits for that.
        Assert.Equal("read 1", await snapshot.GetAsync("key", Window).AsTask().WaitAsync(Wait));
        Assert.True(snapshot.Refreshing);
        Assert.Equal("read 1", await snapshot.GetAsync("key", Window).AsTask().WaitAsync(Wait));

        release.SetResult();
        await refreshed.Task.WaitAsync(Wait);

        Assert.Equal("read 2", await snapshot.GetAsync("key", Window));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_failed_re_read_keeps_the_snapshot_read_before()
    {
        var clock = new Clock();
        bool down = false;
        var snapshot = new RefreshingSnapshot<string>((_, _) =>
            down ? throw new IOException("The server is down.") : Task.FromResult("library"), "test", clock);

        Assert.Equal("library", await snapshot.GetAsync("key", Window));

        down = true;
        clock.Now += Window + TimeSpan.FromMinutes(1);
        Assert.Equal("library", await snapshot.GetAsync("key", Window));
        await WaitUntil(() => !snapshot.Refreshing);

        Assert.Equal("library", await snapshot.GetAsync("key", Window));
    }

    [Fact]
    public async Task A_snapshot_read_from_somewhere_else_is_never_served()
    {
        var snapshot = new RefreshingSnapshot<string>((key, _) => Task.FromResult($"from {key}"), "test", new Clock());

        Assert.Equal("from server A", await snapshot.GetAsync("server A", Window));
        Assert.Equal("from server B", await snapshot.GetAsync("server B", Window));
    }

    [Fact]
    public async Task An_explicit_rescan_waits_for_the_new_read()
    {
        int reads = 0;
        var snapshot = new RefreshingSnapshot<string>((_, _) => Task.FromResult($"read {Interlocked.Increment(ref reads)}"), "test", new Clock());

        Assert.Equal("read 1", await snapshot.GetAsync("key", Window));
        Assert.Equal("read 2", await snapshot.RefreshAsync("key"));
        Assert.Equal("read 2", await snapshot.GetAsync("key", Window));
    }

    /// <summary>A library that's slow to read after the first time, the way a big Jellyfin library is.</summary>
    private sealed class SlowLibrary : IMediaLibrary
    {
        private int _reads;

        public string ShowName { get; set; } = "Toronto Weather";

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) > 1)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return [TestMedia.Episode(ShowName, 1, 1, TimeSpan.FromMinutes(5))];
        }

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_library_pages_keep_their_shows_while_an_old_library_is_read_again()
    {
        var clock = new Clock();
        var library = new SlowLibrary();
        var browser = new LibraryBrowser(library, clock);
        Assert.Equal("Toronto Weather", Assert.Single(await browser.GetShowsAsync()).Name);

        var done = new TaskCompletionSource();
        browser.Changed += () =>
        {
            if (!browser.Updating)
            {
                done.TrySetResult();
            }
        };

        library.ShowName = "Toronto Radar";
        clock.Now += TimeSpan.FromMinutes(6);

        // The re-read is under way and stuck, yet the page still gets its shows (and their commercial folders, pickers…).
        Assert.Equal("Toronto Weather", Assert.Single(await browser.GetShowsAsync().WaitAsync(Wait)).Name);
        Assert.Equal("Toronto Weather", Assert.Single(await browser.GetShowsAsync().WaitAsync(Wait)).Name);

        library.Release.SetResult();
        await done.Task.WaitAsync(Wait);

        Assert.Equal("Toronto Radar", Assert.Single(await browser.GetShowsAsync()).Name);
    }

    [Fact]
    public async Task Settings_saved_keep_the_shows_on_screen_until_the_new_library_is_in()
    {
        var library = new SlowLibrary();
        var browser = new LibraryBrowser(library, new Clock());
        await browser.GetShowsAsync();

        var done = new TaskCompletionSource();
        browser.Changed += () =>
        {
            if (!browser.Updating)
            {
                done.TrySetResult();
            }
        };

        library.ShowName = "Toronto Radar";
        browser.Invalidate();
        Assert.True(browser.Updating);
        Assert.Equal("Toronto Weather", Assert.Single(await browser.GetShowsAsync().WaitAsync(Wait)).Name);

        library.Release.SetResult();
        await done.Task.WaitAsync(Wait);

        Assert.False(browser.Updating);
        Assert.Equal("Toronto Radar", Assert.Single(await browser.GetShowsAsync()).Name);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
