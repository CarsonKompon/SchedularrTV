namespace SchedularrTV.Core.Media;

/// <summary>
/// Adds to items as they come out of the combined library, before the scheduler sees them: for example chapter
/// markers found by analysing the file when the media server has none. Must be fast (it runs on every search), so
/// do any slow work elsewhere and look results up here. Return the item itself when there's nothing to add.
/// </summary>
public interface IMediaEnricher
{
    MediaItem Enrich(MediaItem item);
}
