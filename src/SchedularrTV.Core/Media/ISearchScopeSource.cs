namespace SchedularrTV.Core.Media;

/// <summary>
/// A library that can remember its search results for a while: within a scope, asking the same query again (another
/// channel's ads, bumpers and promos, say) returns the first answer instead of filtering the whole library again.
/// </summary>
public interface ISearchScopeSource
{
    /// <summary>
    /// Remembers searches made from this flow of work (and everything it awaits or starts) until disposed. Work outside
    /// it (the web pages, a preview) searches as usual. Call from the method that awaits the work, not from inside it.
    /// </summary>
    IDisposable BeginSearchScope();
}
