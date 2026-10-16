using SchedularrTV.Core.Configuration;

namespace SchedularrTV.Service.Editor;

/// <summary>
/// Shared images in template packages (generated promo templates, overlays): carried with what uses them, so every
/// package works on its own, and merged into the schedule's own on import, matched by id (never by name).
/// </summary>
public static class SharedImagePackages
{
    /// <summary>
    /// The shared images <paramref name="variables"/> use, copied from <paramref name="library"/> to go in a package.
    /// Each such variable is also given a copy of its pictures, for versions that don't know shared images, and is
    /// unlinked (the package names shared images by id).
    /// </summary>
    public static List<GeneratedSharedImage> Collect(IEnumerable<GeneratedVariableImage> variables, IEnumerable<GeneratedSharedImage>? library)
    {
        List<GeneratedSharedImage> shared = [];
        foreach (GeneratedVariableImage image in variables)
        {
            if (image.IsShared && (GeneratedSharedImages.Find(library, image.Shared) ?? image.Linked) is { } source)
            {
                if (!shared.Any(packed => string.Equals(packed.Id, source.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    shared.Add(Copy(source));
                }

                image.DefaultPath = source.DefaultPath;
                image.Shows = [.. source.Shows.Select(CopyShow)];
                image.Automatic = source.Automatic?.Copy();
            }

            image.Linked = null;
        }

        return shared;
    }

    /// <summary>
    /// Readies a package's variables for import: one whose shared image will be there afterwards (in the package or
    /// already in <paramref name="library"/>) drops the copy of its pictures it carries; any other keeps that copy as its
    /// own. Null <paramref name="library"/> turns every shared variable into one with its own pictures.
    /// </summary>
    public static void PrepareImport(IEnumerable<GeneratedVariableImage> variables, IEnumerable<GeneratedSharedImage> packaged,
        IEnumerable<GeneratedSharedImage>? library)
    {
        foreach (GeneratedVariableImage image in variables)
        {
            image.Linked = null;
            if (!image.IsShared)
            {
                continue;
            }

            if (library is not null
                && (GeneratedSharedImages.Find(packaged, image.Shared) is not null || GeneratedSharedImages.Find(library, image.Shared) is not null))
            {
                image.DefaultPath = null;
                image.Shows = [];
                image.Automatic = null;
            }
            else
            {
                image.Shared = null;
            }
        }
    }

    /// <summary>A default image's and overrides' files swapped through <paramref name="map"/>.</summary>
    public static async Task MapPathsAsync(IGeneratedImageChoices images, Func<string?, Task<string?>> map)
    {
        images.DefaultPath = await map(images.DefaultPath).ConfigureAwait(false);
        foreach (GeneratedShowImage show in images.Shows)
        {
            show.Path = await map(show.Path).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds the shared images <paramref name="library"/> doesn't have (by id), and the overrides, default and automatic
    /// picture it lacks to those it does; what's already there is never changed. Files come through
    /// <paramref name="keep"/> only for what's added. Returns the names added, and those extended (with how many).
    /// </summary>
    public static async Task<(List<string> Added, List<string> Extended)> MergeAsync(List<GeneratedSharedImage> library,
        IEnumerable<GeneratedSharedImage> incoming, Func<string?, Task<string?>> keep)
    {
        List<string> addedNames = [], extendedNames = [];
        foreach (GeneratedSharedImage image in incoming)
        {
            if (string.IsNullOrWhiteSpace(image.Id))
            {
                continue;
            }

            image.Id = image.Id.Trim();
            image.Shows ??= [];
            if (GeneratedSharedImages.Find(library, image.Id) is not { } mine)
            {
                // Names are only labels; one already taken gets a number so the two can be told apart.
                image.Name = UniqueName(library.Select(existing => existing.Name), string.IsNullOrWhiteSpace(image.Name) ? "Shared image" : image.Name.Trim());
                image.Shows = [.. image.Shows.Where(show => !string.IsNullOrWhiteSpace(show.Show))];
                await MapPathsAsync(image, keep).ConfigureAwait(false);
                library.Add(image);
                addedNames.Add(image.Name);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mine.DefaultPath) && !string.IsNullOrWhiteSpace(image.DefaultPath))
            {
                mine.DefaultPath = await keep(image.DefaultPath).ConfigureAwait(false);
            }

            mine.Automatic ??= image.Automatic;
            HashSet<string> have = new(mine.Shows.Where(show => !string.IsNullOrWhiteSpace(show.Show)).Select(GeneratedImageChoices.Key),
                StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (GeneratedShowImage show in image.Shows)
            {
                if (string.IsNullOrWhiteSpace(show.Show) || !have.Add(GeneratedImageChoices.Key(show)))
                {
                    continue;
                }

                show.Path = await keep(show.Path).ConfigureAwait(false);
                mine.Shows.Add(show);
                added++;
            }

            if (added > 0)
            {
                extendedNames.Add($"{mine.Name} (+{added})");
            }
        }

        return (addedNames, extendedNames);
    }

    private static GeneratedSharedImage Copy(GeneratedSharedImage image) => new()
    {
        Id = image.Id, Name = image.Name, DefaultPath = image.DefaultPath, Automatic = image.Automatic?.Copy(), Shows = [.. image.Shows.Select(CopyShow)]
    };

    private static GeneratedShowImage CopyShow(GeneratedShowImage show) => new() { Show = show.Show, Block = show.Block, Path = show.Path };

    private static string UniqueName(IEnumerable<string> existing, string name)
    {
        HashSet<string> taken = new(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int index = 2; ; index++)
        {
            if (!taken.Contains($"{name} {index}")) return $"{name} {index}";
        }
    }
}
