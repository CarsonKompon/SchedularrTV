using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Overlays;

/// <summary>Resolves show-dependent images before rendering, without changing the saved overlay.</summary>
/// <summary>A programme a variable image can be chosen for: its show title and the schedule block it airs in.</summary>
public sealed record OverlayImageSubject(string Title, string? Block = null);

public static class OverlayImages
{
    /// <summary>Show titles only; see <see cref="Programmes"/> for titles with their blocks.</summary>
    public static (string? Current, IReadOnlyList<string> Upcoming) Subjects(IReadOnlyList<ScheduleEntry> ordered, int index)
    {
        (OverlayImageSubject? current, IReadOnlyList<OverlayImageSubject> upcoming) = Programmes(ordered, index);
        return (current?.Title, [.. upcoming.Select(subject => subject.Title)]);
    }

    public static (OverlayImageSubject? Current, IReadOnlyList<OverlayImageSubject> Upcoming) Programmes(IReadOnlyList<ScheduleEntry> ordered, int index)
    {
        ScheduleEntry? current = null;
        for (int previous = index; previous >= 0; previous--)
        {
            if (IsProgramme(ordered[previous])) { current = ordered[previous]; break; }
        }

        List<OverlayImageSubject> upcoming = [];
        string? lastId = current?.Item?.Id;
        for (int next = index + 1; next < ordered.Count && upcoming.Count < 10; next++)
        {
            ScheduleEntry candidate = ordered[next];
            if (!IsProgramme(candidate) || candidate.SegmentNumber > 1 || candidate.ContinuesSlot || candidate.Item!.Id == lastId) continue;
            string title = Title(candidate.Item)!;
            lastId = candidate.Item.Id;
            if (upcoming.Count == 0 || !string.Equals(upcoming[^1].Title, title, StringComparison.OrdinalIgnoreCase))
                upcoming.Add(new(title, candidate.BlockName));
        }
        return (Title(current?.Item) is { } currentTitle ? new(currentTitle, current!.BlockName) : null, upcoming);
    }

    private static bool IsProgramme(ScheduleEntry entry) => entry.Kind == ScheduleEntryKind.Program && entry.Item is not null
        && !entry.IsSlotIntro && !entry.IsSlotOutro;

    private static string? Title(MediaItem? item) => string.IsNullOrWhiteSpace(item?.SeriesName) ? item?.Title : item.SeriesName;

    /// <summary>Resolves by show title alone (no block matching).</summary>
    public static Overlay Resolve(Overlay overlay, string? current, IReadOnlyList<string> upcoming) =>
        ResolveFor(overlay, current is null ? null : new OverlayImageSubject(current), [.. upcoming.Select(title => new OverlayImageSubject(title))]);

    public static Overlay ResolveFor(Overlay overlay, OverlayImageSubject? current, IReadOnlyList<OverlayImageSubject> upcoming)
    {
        if (overlay.VariableImages.Count == 0 && string.IsNullOrWhiteSpace(overlay.VariableImage)
            && !overlay.Layers.Any(layer => !string.IsNullOrWhiteSpace(layer.VariableImage))) return overlay;

        Dictionary<string, string?> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedVariableImage image in overlay.VariableImages)
        {
            OverlayImageSubject? show = image.Context == GeneratedImageContext.UpNext ? upcoming.ElementAtOrDefault(Math.Clamp(image.Offset, 0, 9)) : current;
            string path = image.PathFor(show?.Title, show?.Block);
            paths[image.Name] = path.Length == 0 ? image.DefaultPath : path;
        }

        Overlay copy = overlay.Copy();
        if (!string.IsNullOrWhiteSpace(copy.VariableImage)) copy.File = paths.GetValueOrDefault(copy.VariableImage);
        foreach (OverlayLayer layer in copy.Layers.Where(layer => layer.Kind == OverlayLayerKinds.Image && !string.IsNullOrWhiteSpace(layer.VariableImage)))
            layer.Path = paths.GetValueOrDefault(layer.VariableImage!);
        return copy;
    }
}
