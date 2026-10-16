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
    public static Overlay Resolve(Overlay overlay, string? current, IReadOnlyList<string> upcoming,
        IEnumerable<GeneratedSharedImage>? library = null, IAutomaticImageLookup? automatic = null) =>
        ResolveFor(overlay, current is null ? null : new OverlayImageSubject(current), [.. upcoming.Select(title => new OverlayImageSubject(title))],
            library, automatic);

    /// <summary>Points the overlay's variable images that name a shared image at it in <paramref name="library"/> (or at nothing, when it's gone).</summary>
    public static void Link(Overlay overlay, IEnumerable<GeneratedSharedImage>? library)
    {
        foreach (GeneratedVariableImage image in overlay.VariableImages)
        {
            image.Linked = image.IsShared ? GeneratedSharedImages.Find(library, image.Shared) : null;
        }
    }

    /// <summary>Whether any of the overlay's variable images finds pictures automatically (once linked).</summary>
    public static bool HasAutomatic(Overlay overlay) => overlay.VariableImages.Any(image => image.Choices.Automatic is not null);

    /// <summary>A use may override the variable's usual show without changing other layers that use it.</summary>
    public static (GeneratedImageContext Context, int Offset) ContextFor(GeneratedVariableImage image, string? selector) =>
        GeneratedImageSelectors.TryParse(selector, out GeneratedImageContext context, out int offset)
            ? (context, offset) : (image.Context, Math.Clamp(image.Offset, 0, GeneratedImageSelectors.MaxAhead - 1));

    /// <summary>
    /// A copy of the overlay with its variable images' pictures in place for these shows: each variable's shared image
    /// (from <paramref name="library"/>) picks one by show or block, else finds one automatically with
    /// <paramref name="automatic"/>, else uses its default.
    /// </summary>
    public static Overlay ResolveFor(Overlay overlay, OverlayImageSubject? current, IReadOnlyList<OverlayImageSubject> upcoming,
        IEnumerable<GeneratedSharedImage>? library = null, IAutomaticImageLookup? automatic = null)
    {
        if (overlay.VariableImages.Count == 0 && string.IsNullOrWhiteSpace(overlay.VariableImage)
            && !overlay.Layers.Any(layer => !string.IsNullOrWhiteSpace(layer.VariableImage))) return overlay;

        Dictionary<string, GeneratedVariableImage> images = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedVariableImage original in overlay.VariableImages)
        {
            GeneratedVariableImage image = original;
            if (library is not null && image.IsShared)
            {
                image = original.Copy();
                image.Linked = GeneratedSharedImages.Find(library, image.Shared);
            }

            images[image.Name] = image;
        }

        Dictionary<(string Name, GeneratedImageContext Context, int Offset), string?> paths = [];
        string? PathFor(string name, string? selector)
        {
            if (!images.TryGetValue(name, out GeneratedVariableImage? image)) return null;
            var (context, offset) = ContextFor(image, selector);
            var key = (image.Name, context, offset);
            if (paths.TryGetValue(key, out string? cached)) return cached;
            OverlayImageSubject? show = context == GeneratedImageContext.UpNext ? upcoming.ElementAtOrDefault(offset) : current;
            string path = image.PathFor(show?.Title, show?.Block, automatic);
            return paths[key] = path.Length == 0 ? null : path;
        }

        Overlay copy = overlay.Copy();
        if (!string.IsNullOrWhiteSpace(copy.VariableImage)) copy.File = PathFor(copy.VariableImage, copy.VariableImageShow);
        foreach (OverlayLayer layer in copy.Layers.Where(layer => layer.Kind == OverlayLayerKinds.Image && !string.IsNullOrWhiteSpace(layer.VariableImage)))
            layer.Path = PathFor(layer.VariableImage!, layer.VariableImageShow);
        return copy;
    }
}
