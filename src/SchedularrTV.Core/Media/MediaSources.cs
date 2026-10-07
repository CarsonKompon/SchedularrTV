namespace SchedularrTV.Core.Media;

// ---------------------------------------------------------------------------------------------------------------
// What a library source (Jellyfin, local folders, or any plugin's) can offer besides its items (IMediaLibrary) and
// pictures (IArtworkSource). Each is optional: a source implements the ones it can, and the app asks every source
// that's switched on, so nothing in it depends on one in particular. Register them with the Add… helpers in
// SchedularrTV.Plugins.PluginServiceCollectionExtensions.
// ---------------------------------------------------------------------------------------------------------------

/// <summary>A source that can't be used as it's set up (no address, a wrong key…); its message says what to fix.</summary>
public class MediaSourceConfigurationException(string message) : Exception(message);

/// <summary>How far a source has got reading its library, for progress bars.</summary>
public sealed record MediaSourceProgress(string Stage, int Items, int? Total, DateTimeOffset StartedAt)
{
    public double? Percent => Total is > 0 ? Math.Min(100, Items * 100.0 / Total.Value) : null;
}

/// <summary>Work a source's own server is doing that will change the library soon, such as Jellyfin's library scan.</summary>
public sealed record MediaSourceActivity(string Description, double? Percent);

/// <summary>
/// A library source as the app describes it: its name, whether it's set up, how far reading it has got, what its own
/// server is busy with, and the libraries it has (so some can be left out).
/// </summary>
public interface IMediaSourceInfo
{
    /// <summary>Its name as shown: "Jellyfin", "Local folders".</summary>
    string Name { get; }

    /// <summary>Whether it's set up enough to be read (an address and key, or at least one folder).</summary>
    bool IsConfigured { get; }

    /// <summary>How far reading its library has got while it's being read; null otherwise.</summary>
    MediaSourceProgress? Loading { get; }

    /// <summary>Raised (from any thread) whenever <see cref="Loading"/> changes.</summary>
    event Action? LoadingChanged;

    /// <summary>What its server is doing that will change the library soon; null when nothing (or it can't say).</summary>
    ValueTask<MediaSourceActivity?> GetActivityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The libraries it reads, for leaving some out by name (see <see cref="ILibraryScope"/>); empty when it has none to
    /// choose from. Throws when its server can't be reached.
    /// </summary>
    ValueTask<IReadOnlyList<MediaSourceLibrary>> GetLibrariesAsync(CancellationToken cancellationToken = default);
}

/// <summary>One of a source's libraries: its name (what <see cref="ILibraryScope.ExcludedLibraries"/> holds) and where it is.</summary>
public sealed record MediaSourceLibrary(string Name, string? Detail = null);

/// <summary>A show's or movie's extra: a theme, trailer, featurette…, for intros and outros.</summary>
public sealed record MediaExtra(string Name, string Path, TimeSpan Duration);

/// <summary>Lists the extras of the shows (and movies) a source owns.</summary>
public interface IExtrasSource
{
    /// <summary>Whether <paramref name="id"/> (a series' or movie's id) is one of this source's.</summary>
    bool Owns(string id);

    ValueTask<IReadOnlyList<MediaExtra>> GetExtrasAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Knows where an item's end credits start (beyond chapters named for them), such as Jellyfin's media segments.</summary>
public interface ICreditsSource
{
    string Name { get; }

    /// <summary>Whether it can be asked now (it's set up).</summary>
    bool IsAvailable { get; }

    /// <summary>Whether <paramref name="itemId"/> is one of this source's.</summary>
    bool Owns(string itemId);

    /// <summary>Where the item's credits start; null when it doesn't know.</summary>
    ValueTask<TimeSpan?> GetCreditsStartAsync(string itemId, CancellationToken cancellationToken = default);
}

/// <summary>Someone watching live TV through a media server, before they're matched to one of the scheduler's channels.</summary>
/// <param name="Number">The channel's number as the server has it.</param>
/// <param name="Via">The server's name: "Jellyfin", "Plex".</param>
public sealed record LiveViewerSession(int? Number, string? ChannelName, string User, string Via, string? Device);

public static class LiveChannelNumbers
{
    /// <summary>The leading whole number in "5", "5.1" or "ch5"; null when there isn't one.</summary>
    public static int? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string digits = string.Concat(text.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit));
        return int.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : null;
    }
}

/// <summary>A media server that can say who is watching its live TV right now (for viewing stats).</summary>
public interface ILiveViewerSource
{
    /// <summary>Its name: "Jellyfin".</summary>
    string Name { get; }

    bool IsConfigured { get; }

    /// <summary>Everyone watching live TV now. Throws when the server can't be reached.</summary>
    Task<IReadOnlyList<LiveViewerSession>> GetSessionsAsync(CancellationToken cancellationToken = default);
}
