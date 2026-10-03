namespace SchedularrTV.Plugins;

/// <summary>
/// Tells the scheduler that a plugin's media source changed: a show added, renamed or removed (a web channel saved,
/// say). The library pages (TV Shows, Movies, the pickers) are read again at once and update while open, instead of
/// waiting out their few-minute cache. Provided by the host (plugin API 1.4); inject it.
/// </summary>
/// <remarks>
/// Schedules aren't rebuilt for this: channels that air the changed items pick them up at their next rebuild.
/// </remarks>
public interface IMediaLibraryNotifier
{
    void LibraryChanged();
}
