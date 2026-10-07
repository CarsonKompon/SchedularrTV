namespace SchedularrTV.Core.Media;

public enum MediaKind
{
    Unknown = 0,
    Episode,
    Movie,

    /// <summary>Short-form filler content: commercials, bumpers, promos, idents.</summary>
    Filler,

    /// <summary>Songs, for radio channels (an <see cref="IMusicLibrary"/>); never scheduled as items themselves.</summary>
    Music
}
