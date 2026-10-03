namespace SchedularrTV.Service.Editor;

public enum LibrarySortColumn
{
    Default,
    Year,
    Runtime,
    Episodes,
    AverageEpisode
}

public sealed class LibraryTableSort
{
    public LibrarySortColumn Column { get; private set; }
    public bool Descending { get; private set; }

    public void Toggle(LibrarySortColumn column)
    {
        Descending = Column == column && !Descending;
        Column = column;
    }

    public void Set(LibrarySortColumn column, bool descending)
    {
        Column = column;
        Descending = descending;
    }

    public IEnumerable<LibraryShow> Apply(IEnumerable<LibraryShow> shows) => Column switch
    {
        LibrarySortColumn.Year => Order(shows, show => show.Year),
        LibrarySortColumn.Runtime => Order(shows, show => show.TotalRuntime.Ticks),
        LibrarySortColumn.Episodes => Order(shows, show => show.EpisodeCount),
        LibrarySortColumn.AverageEpisode => Order(shows, show => show.AverageEpisodeDuration?.Ticks),
        _ => shows
    };

    public IEnumerable<LibraryMovie> Apply(IEnumerable<LibraryMovie> movies) => Column switch
    {
        LibrarySortColumn.Year => Order(movies, movie => movie.Year),
        LibrarySortColumn.Runtime => Order(movies, movie => movie.Duration.Ticks),
        _ => movies
    };

    private IEnumerable<TItem> Order<TItem>(IEnumerable<TItem> items, Func<TItem, long?> key)
    {
        var knownFirst = items.OrderBy(item => key(item) is null);
        return Descending ? knownFirst.ThenByDescending(key) : knownFirst.ThenBy(key);
    }
}