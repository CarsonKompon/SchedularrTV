using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Core.Media;

/// <summary>
/// Reduces each set of alternate versions to the single one that should actually play.
/// </summary>
/// <remarks>
/// Providers expose merged versions as separate items that each describe the whole group, so without
/// this step every extra version schedules as if it were another episode.
/// </remarks>
public static class VersionCollapser
{
    public static IReadOnlyList<MediaItem> Collapse(IReadOnlyList<MediaItem> items, VersionSelection? selection)
    {
        Dictionary<string, List<MediaItem>> groups = new(StringComparer.Ordinal);

        foreach (MediaItem item in items)
        {
            if (item.VersionGroupId is { } group)
            {
                if (!groups.TryGetValue(group, out List<MediaItem>? members))
                {
                    groups[group] = members = [];
                }

                members.Add(item);
            }
        }

        if (groups.Count == 0)
        {
            return items;
        }

        List<MediaItem> collapsed = new(items.Count);
        HashSet<string> emitted = new(StringComparer.Ordinal);

        // Walk the original list so the winner takes the position of its group's first appearance,
        // keeping the pool order the caller already established.
        foreach (MediaItem item in items)
        {
            if (item.VersionGroupId is not { } group)
            {
                collapsed.Add(item);
            }
            else if (emitted.Add(group))
            {
                collapsed.Add(Choose(groups[group], selection));
            }
        }

        return collapsed;
    }

    private static MediaItem Choose(List<MediaItem> versions, VersionSelection? selection)
    {
        if (versions.Count == 1)
        {
            return versions[0];
        }

        List<MediaItem> candidates = versions;

        if (selection is { Exclude.Count: > 0 })
        {
            List<MediaItem> kept = [.. versions.Where(v => !MatchesAny(v.VersionName, selection.Exclude))];

            // Excluding every version would silently drop the episode, so the block is skipped
            // when nothing survives it.
            if (kept.Count > 0)
            {
                candidates = kept;
            }
        }

        if (selection is { Prefer.Count: > 0 })
        {
            foreach (string preferred in selection.Prefer)
            {
                MediaItem? match = candidates
                    .Where(v => Matches(v.VersionName, preferred))
                    .OrderBy(v => v.Id, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (match is not null)
                {
                    return match;
                }
            }
        }

        // Nothing expressed a preference, so fall back to a stable pick rather than provider order,
        // which is not guaranteed to repeat between refreshes.
        return candidates.OrderBy(v => v.Id, StringComparer.Ordinal).First();
    }

    private static bool MatchesAny(string? versionName, List<string> names) =>
        names.Any(n => Matches(versionName, n));

    private static bool Matches(string? versionName, string name) =>
        !string.IsNullOrWhiteSpace(name)
        && versionName is not null
        && versionName.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The release a version name describes: "S01E01 The Beginning - DVD Release [2009-03-15]"
    /// becomes "DVD Release". Null when the name describes no release.
    /// </summary>
    /// <remarks>
    /// Keyed off the final " - " because that is where a release qualifier is conventionally
    /// appended, and a trailing "[...]" is dropped so dated variants of one release share a label
    /// rather than each becoming its own. Providers also merge unrelated files whose metadata
    /// collided, and those names carry no qualifier at all.
    /// </remarks>
    public static string? ReleaseLabel(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName))
        {
            return null;
        }

        string name = WithoutTrailingBrackets(versionName)!;
        int separator = name.LastIndexOf(" - ", StringComparison.Ordinal);

        if (separator < 0)
        {
            return null;
        }

        string label = name[(separator + 3)..].Trim();

        return label.Length > 0 && label.Any(char.IsLetter) ? label : null;
    }

    /// <summary>Drops a trailing "[...]" qualifier, so dated variants of one release match up.</summary>
    public static string? WithoutTrailingBrackets(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string trimmed = name.TrimEnd();

        while (trimmed.EndsWith(']'))
        {
            int open = trimmed.LastIndexOf('[');

            if (open <= 0)
            {
                break;
            }

            trimmed = trimmed[..open].TrimEnd();
        }

        return trimmed;
    }
}
