using SchedularrTV.Core.Media;
using SchedularrTV.Core.Query;
using SchedularrTV.Plugins;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public class LibraryBrowserChangeTests
{
    /// <summary>A library whose shows can be renamed between reads, as a plugin's are when it saves.</summary>
    private sealed class ChangingLibrary : IMediaLibrary
    {
        public string ShowName { get; set; } = "Toronto Weather";

        public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<MediaItem>>([TestMedia.Episode(ShowName, 1, 1, TimeSpan.FromMinutes(5))]);

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_plugin_saying_its_library_changed_shows_the_change_at_once_and_tells_open_pages()
    {
        var source = new ChangingLibrary();
        var browser = new LibraryBrowser(source, TimeProvider.System);
        Assert.Equal("Toronto Weather", Assert.Single(await browser.GetShowsAsync()).Name);

        var reread = new TaskCompletionSource();
        browser.Changed += () =>
        {
            if (!browser.Updating)
            {
                reread.TrySetResult();
            }
        };

        source.ShowName = "Toronto Radar";
        ((IMediaLibraryNotifier)browser).LibraryChanged();
        Assert.True(browser.Updating);

        await reread.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(browser.Updating);
        Assert.Equal("Toronto Radar", Assert.Single(await browser.GetShowsAsync()).Name);
    }

    [Fact]
    public async Task Without_a_change_the_library_stays_cached()
    {
        var source = new ChangingLibrary();
        var browser = new LibraryBrowser(source, TimeProvider.System);
        await browser.GetShowsAsync();

        source.ShowName = "Renamed";

        Assert.Equal("Toronto Weather", Assert.Single(await browser.GetShowsAsync()).Name);
    }
}
