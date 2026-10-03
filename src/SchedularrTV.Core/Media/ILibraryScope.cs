namespace SchedularrTV.Core.Media;

/// <summary>
/// Which of a provider's libraries the scheduler is allowed to ingest.
/// </summary>
/// <remarks>
/// Lets a provider honour a user setting that lives in the scheduler config without depending on
/// the config or the host that owns it.
/// </remarks>
public interface ILibraryScope
{
    /// <summary>Provider library names to skip entirely, e.g. a DVR or streaming-cache library.</summary>
    IReadOnlyList<string> ExcludedLibraries { get; }
}
