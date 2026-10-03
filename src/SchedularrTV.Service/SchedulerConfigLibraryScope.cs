using SchedularrTV.Core.Media;

namespace SchedularrTV.Service;

/// <summary>
/// Points the media providers at the library exclusions saved in <c>schedule.yaml</c>.
/// </summary>
/// <remarks>
/// Reads the current config on every access rather than caching it, so unticking a library takes
/// effect on the next scan instead of the next restart.
/// </remarks>
public sealed class SchedulerConfigLibraryScope(SchedulerConfigProvider provider) : ILibraryScope
{
    public IReadOnlyList<string> ExcludedLibraries => provider.Current.Settings.ExcludedLibraries;
}
