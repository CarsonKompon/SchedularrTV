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

    /// <summary>Whether any of the design's pictures is found with a picture search.</summary>
    public static bool HasAutomatic(Overlay overlay) =>
        overlay.Kind != OverlayKinds.File && overlay.Layers.Any(layer => layer is { Kind: OverlayLayerKinds.Image, AutomaticImage: not null });

    /// <summary>The tokens an automatic image's query can use, for the editor's help text.</summary>
    public const string AutomaticTokenHelp =
        "Tokens: {title} the show on now, {episode}, {season_episode}, {year}, {next} the next show, {channel}. A token with nothing to fill it (no next show) leaves the picture out.";

    /// <summary>
    /// A copy of the design whose automatic images point at their pictures, as <paramref name="find"/> has them (null
    /// leaves a picture out). The saved overlay isn't changed.
    /// </summary>
    public static Overlay WithAutomatic(Overlay overlay, IReadOnlyDictionary<string, string> tokens,
        Func<SchedularrTV.Service.Images.AutomaticImageRequest, string?> find)
    {
        if (!HasAutomatic(overlay))
        {
            return overlay;
        }

        Overlay copy = overlay.Copy();
        foreach (OverlayLayer layer in copy.Layers.Where(layer => layer is { Kind: OverlayLayerKinds.Image, AutomaticImage: not null }))
        {
            layer.Path = Request(layer, tokens) is { } request ? find(request) : null;
        }

        return copy;
    }

    /// <summary>What an automatic image searches for with these tokens; null when its query names something they don't have.</summary>
    public static SchedularrTV.Service.Images.AutomaticImageRequest? Request(OverlayLayer layer, IReadOnlyDictionary<string, string> tokens) =>
        layer.AutomaticImage is { } config && FillQuery(config.Query, tokens) is { } query
            ? SchedularrTV.Service.Images.AutomaticImageRequest.For(config, query)
            : null;

    /// <summary>The query with its tokens filled in; null when a token it uses is empty here (no next show, say).</summary>
    public static string? FillQuery(string? query, IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        bool missing = false;
        string filled = System.Text.RegularExpressions.Regex.Replace(query, @"\{([a-z_][a-z_0-9]*)\}", match =>
        {
            string? value = tokens.FirstOrDefault(token => string.Equals(token.Key, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is null)
            {
                return match.Value;
            }

            missing |= string.IsNullOrWhiteSpace(value);
            return value;
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return missing || string.IsNullOrWhiteSpace(filled)
            ? null
            : string.Join(' ', filled.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

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
