using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Scheduling;

namespace SchedularrTV.Overlays;

/// <summary>Resolves show-dependent images before rendering, without changing the saved overlay.</summary>
public static class OverlayImages
{
    public static (string? Current, IReadOnlyList<string> Upcoming) Subjects(IReadOnlyList<ScheduleEntry> ordered, int index)
    {
        ScheduleEntry? current = null;
        for (int previous = index; previous >= 0; previous--)
        {
            if (IsProgramme(ordered[previous])) { current = ordered[previous]; break; }
        }

        List<string> upcoming = [];
        string? lastId = current?.Item?.Id;
        for (int next = index + 1; next < ordered.Count && upcoming.Count < 10; next++)
        {
            ScheduleEntry candidate = ordered[next];
            if (!IsProgramme(candidate) || candidate.SegmentNumber > 1 || candidate.ContinuesSlot || candidate.Item!.Id == lastId) continue;
            string title = Title(candidate.Item)!;
            lastId = candidate.Item.Id;
            if (upcoming.Count == 0 || !string.Equals(upcoming[^1], title, StringComparison.OrdinalIgnoreCase)) upcoming.Add(title);
        }
        return (Title(current?.Item), upcoming);
    }

    private static bool IsProgramme(ScheduleEntry entry) => entry.Kind == ScheduleEntryKind.Program && entry.Item is not null
        && !entry.IsSlotIntro && !entry.IsSlotOutro;

    private static string? Title(MediaItem? item) => string.IsNullOrWhiteSpace(item?.SeriesName) ? item?.Title : item.SeriesName;

    public static Overlay Resolve(Overlay overlay, string? current, IReadOnlyList<string> upcoming)
    {
        if (overlay.VariableImages.Count == 0 && string.IsNullOrWhiteSpace(overlay.VariableImage)
            && !overlay.Layers.Any(layer => !string.IsNullOrWhiteSpace(layer.VariableImage))) return overlay;

        Dictionary<string, string?> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedVariableImage image in overlay.VariableImages)
        {
            string? show = image.Context == GeneratedImageContext.UpNext ? upcoming.ElementAtOrDefault(Math.Clamp(image.Offset, 0, 9)) : current;
            string? path = string.IsNullOrWhiteSpace(show) ? null : image.Shows.FirstOrDefault(candidate =>
                string.Equals(candidate.Show.Trim(), show.Trim(), StringComparison.OrdinalIgnoreCase))?.Path;
            paths[image.Name] = string.IsNullOrWhiteSpace(path) ? image.DefaultPath : path;
        }

        Overlay copy = overlay.Copy();
        if (!string.IsNullOrWhiteSpace(copy.VariableImage)) copy.File = paths.GetValueOrDefault(copy.VariableImage);
        foreach (OverlayLayer layer in copy.Layers.Where(layer => layer.Kind == OverlayLayerKinds.Image && !string.IsNullOrWhiteSpace(layer.VariableImage)))
            layer.Path = paths.GetValueOrDefault(layer.VariableImage!);
        return copy;
    }
}
