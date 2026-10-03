using SchedularrTV.Core.Media;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class LibraryTableSortTests
{
    [Fact]
    public void Numeric_columns_toggle_direction_and_keep_unknown_values_last()
    {
        var shows = new[]
        {
            Show("A", 2005, 100, 22), Show("B", 1999, 9, 45), Show("C", null, 20, null)
        };
        var sort = new LibraryTableSort();
        Assert.Equal(["A", "B", "C"], sort.Apply(shows).Select(show => show.Name));
        sort.Toggle(LibrarySortColumn.Year);
        Assert.Equal(["B", "A", "C"], sort.Apply(shows).Select(show => show.Name));
        sort.Toggle(LibrarySortColumn.Year);
        Assert.Equal(["A", "B", "C"], sort.Apply(shows).Select(show => show.Name));
        sort.Toggle(LibrarySortColumn.Episodes);
        Assert.Equal(["B", "C", "A"], sort.Apply(shows).Select(show => show.Name));
        sort.Toggle(LibrarySortColumn.Runtime);
        Assert.Equal(["C", "B", "A"], sort.Apply(shows).Select(show => show.Name));
        sort.Toggle(LibrarySortColumn.AverageEpisode);
        Assert.Equal(["A", "B", "C"], sort.Apply(shows).Select(show => show.Name));
        sort.Toggle(LibrarySortColumn.AverageEpisode);
        Assert.Equal(["B", "A", "C"], sort.Apply(shows).Select(show => show.Name));
    }

    [Fact]
    public void Movies_sort_numerically_and_preserve_tie_order()
    {
        var movies = new[] { new LibraryMovie("A", 2000, TimeSpan.FromMinutes(100)),
            new LibraryMovie("B", null, TimeSpan.FromMinutes(90)), new LibraryMovie("C", 1990, TimeSpan.FromMinutes(100)) };
        var sort = new LibraryTableSort();
        sort.Toggle(LibrarySortColumn.Runtime);
        Assert.Equal(["B", "A", "C"], sort.Apply(movies).Select(movie => movie.Title));
        sort.Toggle(LibrarySortColumn.Runtime);
        Assert.Equal(["A", "C", "B"], sort.Apply(movies).Select(movie => movie.Title));
        sort.Toggle(LibrarySortColumn.Year);
        Assert.Equal(["C", "A", "B"], sort.Apply(movies).Select(movie => movie.Title));
        sort.Toggle(LibrarySortColumn.Year);
        Assert.Equal(["A", "C", "B"], sort.Apply(movies).Select(movie => movie.Title));
    }

    [Fact]
    public async Task Average_uses_known_episode_durations_and_does_not_count_versions_twice()
    {
        MediaItem Version(string id, int minutes) => new()
        {
            Id = id, Title = "First episode", SeriesName = "Show", Kind = MediaKind.Episode,
            SeasonNumber = 1, EpisodeNumber = 1, Duration = TimeSpan.FromMinutes(minutes), VersionGroupId = "episode-one"
        };
        var library = new LibraryBrowser(new FakeMediaLibrary([
            Version("a-original", 10), Version("z-alternate", 90),
            TestMedia.Episode("Show", 1, 2, TimeSpan.FromMinutes(20)),
            TestMedia.Episode("Show", 1, 3, TimeSpan.Zero), TestMedia.Episode("Unknown", 1, 1, TimeSpan.Zero)]), TimeProvider.System);
        var shows = await library.GetShowsAsync();
        LibraryShow show = Assert.Single(shows, show => show.Name == "Show");
        Assert.Equal(3, show.EpisodeCount);
        Assert.Equal(TimeSpan.FromMinutes(15), show.AverageEpisodeDuration);
        Assert.Equal("15m 00s", show.AverageEpisodeRuntime);
        Assert.Null(Assert.Single(shows, show => show.Name == "Unknown").AverageEpisodeDuration);
    }

    private static LibraryShow Show(string name, int? year, int episodes, int? average) =>
        new(name, name, year, episodes, [], TimeSpan.FromMinutes(episodes * (average ?? 0)), [], 0, [])
        { AverageEpisodeDuration = average is { } minutes ? TimeSpan.FromMinutes(minutes) : null };
}