using YamlDotNet.Serialization;

namespace SchedularrTV.Core.Configuration;

/// <summary>
/// Chooses between alternate versions of the same episode or movie (e.g. a web rip and a DVD rip
/// merged into one item in Jellyfin). These rules only ever apply <em>within</em> a version group,
/// so a title that exists in a single version is never filtered out by them.
/// </summary>
public sealed class VersionSelection
{
    /// <summary>Version names to pick, best first. Matched as a case-insensitive substring.</summary>
    public List<string> Prefer { get; set; } = [];

    /// <summary>
    /// Version names to avoid, matched as a case-insensitive substring. Ignored for a given title
    /// when it would exclude every version, so blocking a version never loses an episode outright.
    /// </summary>
    public List<string> Exclude { get; set; } = [];

    [YamlIgnore]
    public bool IsEmpty => Prefer.Count == 0 && Exclude.Count == 0;
}
