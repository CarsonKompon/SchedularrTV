using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

/// <summary>What importing a package did to the shared images.</summary>
public sealed class GeneratedSharedImport
{
    /// <summary>Shared images that weren't there, by name.</summary>
    public List<string> Added { get; } = [];

    /// <summary>Shared images already there that gained overrides, with how many.</summary>
    public List<string> Extended { get; } = [];

    public string Summary =>
        string.Join(" ", new[]
        {
            Added.Count == 0 ? null : $"Added shared images: {string.Join(", ", Added)}.",
            Extended.Count == 0 ? null : $"Added missing shows to: {string.Join(", ", Extended)}."
        }.Where(part => part is not null));
}

/// <summary>
/// Generated promo templates as template packages: a zip with the template, its background, images and songs, and an
/// external generator's whole folder (less what its .gitignore leaves out), so it works as-is on another computer.
/// </summary>
public static class GeneratedPromoPackages
{
    public const string Kind = "generated-promo";

    /// <summary>Where a template's chosen and imported files are kept, under the data folder's assets.</summary>
    public const string AssetCategory = "generated-promos";

    /// <summary>The generator's folder inside the package.</summary>
    private const string ProgramEntry = "program";

    /// <summary>Stands for the generator's folder in its command and arguments, so they follow it to its new place.</summary>
    public const string FolderToken = "{generator_folder}";

    private static RegexOptions PathOptions =>
        RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None);

    /// <summary>
    /// Swaps every file the template uses through <paramref name="map"/>: the background, image layers and songs
    /// (with the songs' own starts and volumes, which are kept by path). A song mapped to null is dropped.
    /// </summary>
    public static async Task MapPathsAsync(GeneratedPromoTemplate template, Func<string?, Task<string?>> map)
    {
        template.Background = await map(template.Background).ConfigureAwait(false);
        if (template.Backgrounds is { } backgrounds)
        {
            List<string> mapped = [];
            foreach (string background in backgrounds)
            {
                if (await map(background).ConfigureAwait(false) is { Length: > 0 } path) mapped.Add(path);
            }

            template.Backgrounds = mapped.Count == 0 ? null : mapped;
        }

        if (template.BackgroundRules is { } rules)
        {
            foreach (GeneratedBackgroundRule rule in rules.ToList())
            {
                if (await map(rule.Background).ConfigureAwait(false) is { Length: > 0 } moved) rule.Background = moved;
                else rules.Remove(rule);
            }
            template.BackgroundRules = rules.Count == 0 ? null : rules;
        }

        // Layer placements per background follow their background to its new path.
        if (template.BackgroundOverrides is { } overrides)
        {
            foreach (GeneratedBackgroundOverride tracked in overrides.ToList())
            {
                if (await map(tracked.Background).ConfigureAwait(false) is { Length: > 0 } moved)
                {
                    tracked.Background = moved;
                }
                else
                {
                    overrides.Remove(tracked);
                }
            }

            template.BackgroundOverrides = overrides.Count == 0 ? null : overrides;
        }

        foreach (GeneratedLayer layer in template.Layers)
        {
            layer.Path = await map(layer.Path).ConfigureAwait(false);
        }

        foreach (GeneratedVariableImage image in template.VariableImages ?? [])
        {
            image.DefaultPath = await map(image.DefaultPath).ConfigureAwait(false);
            foreach (GeneratedShowImage show in image.Shows)
            {
                show.Path = await map(show.Path).ConfigureAwait(false);
            }
        }

        if (template.Music is not { } music)
        {
            return;
        }

        music.Path = await map(music.Path).ConfigureAwait(false);
        List<string> songs = [];
        foreach (string song in music.Paths)
        {
            if (await map(song).ConfigureAwait(false) is { Length: > 0 } mapped)
            {
                songs.Add(mapped);
            }
        }

        music.Paths = songs;
        if (music.SongVolumes is { } volumes)
        {
            Dictionary<string, double> moved = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string song, double volume) in volumes)
            {
                if (await map(song).ConfigureAwait(false) is { Length: > 0 } mapped) moved[mapped] = volume;
            }

            music.SongVolumes = moved.Count == 0 ? null : moved;
        }

        if (music.SongStarts is { } starts)
        {
            Dictionary<string, TimeSpan> moved = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string song, TimeSpan start) in starts)
            {
                if (await map(song).ConfigureAwait(false) is { Length: > 0 } mapped) moved[mapped] = start;
            }

            music.SongStarts = moved.Count == 0 ? null : moved;
        }
    }

    /// <summary>
    /// Writes a copy of the template, its files and its generator's folder into the package; returns its name. The
    /// shared images its variables use (from <paramref name="sharedLibrary"/>) go in with it, so every package works on
    /// its own; each such variable also carries a copy of its images, for versions that don't know shared images.
    /// </summary>
    public static async Task<string> ExportAsync(GeneratedPromoTemplate template, TemplatePackageWriter writer,
        IEnumerable<GeneratedSharedImage>? sharedLibrary = null)
    {
        GeneratedPromoTemplate copy = GeneratedPromoEditing.Copy(template);
        List<GeneratedSharedImage> shared = [];
        foreach (GeneratedVariableImage image in copy.VariableImages ?? [])
        {
            if (image.IsShared && (GeneratedSharedImages.Find(sharedLibrary, image.Shared) ?? image.Linked) is { } source)
            {
                if (!shared.Any(packed => string.Equals(packed.Id, source.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    shared.Add(GeneratedPromoEditing.Copy(source));
                }

                image.DefaultPath = source.DefaultPath;
                image.Shows = GeneratedPromoEditing.Copy(source.Shows);
                image.Automatic = source.Automatic?.Copy();
            }

            image.Linked = null;
        }

        copy.SharedImages = shared.Count == 0 ? null : shared;
        await MapPathsAsync(copy, path => Task.FromResult(writer.Asset(path))).ConfigureAwait(false);
        foreach (GeneratedSharedImage image in shared)
        {
            await MapPathsAsync(image, path => Task.FromResult(writer.Asset(path))).ConfigureAwait(false);
        }

        // The canvas editor's text is drawn with installed fonts, which the importing computer may not have.
        if (copy.Generator is null)
        {
            writer.AddFonts(FontsUsed(copy));
        }

        string? runs = null;
        if (copy.Generator is { } generator)
        {
            if (generator.WorkingDirectory is { Length: > 0 } folder && Directory.Exists(folder.Trim()))
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
                writer.AddFolder(full, ProgramEntry);
                generator.WorkingDirectory = TemplatePackages.Prefix + ProgramEntry;
                Relocate(generator, value => ReplaceFolder(value, full, FolderToken));
            }

            runs = string.IsNullOrWhiteSpace(generator.SetupCommand)
                ? generator.Command
                : $"{generator.Command} (set up first with: {generator.SetupCommand.Trim()})";
        }

        writer.Finish(Kind, template.Name, copy, runs);
        return template.Name;
    }

    /// <summary>A default image's and overrides' files swapped through <paramref name="map"/>.</summary>
    private static async Task MapPathsAsync(IGeneratedImageChoices images, Func<string?, Task<string?>> map)
    {
        images.DefaultPath = await map(images.DefaultPath).ConfigureAwait(false);
        foreach (GeneratedShowImage show in images.Shows)
        {
            show.Path = await map(show.Path).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The package's template, its files kept in the data folder and its generator's folder unpacked there, named so it
    /// doesn't clash with <paramref name="existingNames"/>.
    /// </summary>
    /// <param name="sharedLibrary">
    /// The shared images to merge the package's into, matched by id (never by name): one not there is added whole; one
    /// that is keeps everything it has and only gains the show and block overrides (and a default) it lacks. Only the
    /// files of what's added are taken from the package. Null turns the template's shared variables into its own.
    /// </param>
    public static async Task<GeneratedPromoTemplate> ImportAsync(TemplatePackageReader reader, TemplateAssets assets, IEnumerable<string> existingNames,
        List<GeneratedSharedImage>? sharedLibrary = null, GeneratedSharedImport? report = null, CancellationToken cancellationToken = default)
    {
        GeneratedPromoTemplate template = reader.Item<GeneratedPromoTemplate>();
        Func<string?, Task<string?>> keep = path => reader.AssetAsync(path, assets, AssetCategory, cancellationToken);
        List<GeneratedSharedImage> packaged = template.SharedImages ?? [];
        template.SharedImages = null;

        // A variable whose shared image will be there afterwards drops the copy of its images it carries (whose files
        // then aren't kept); any other keeps that copy as its own.
        foreach (GeneratedVariableImage image in template.VariableImages ?? [])
        {
            image.Linked = null;
            if (!image.IsShared)
            {
                continue;
            }

            if (sharedLibrary is not null
                && (GeneratedSharedImages.Find(packaged, image.Shared) is not null || GeneratedSharedImages.Find(sharedLibrary, image.Shared) is not null))
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

        await MapPathsAsync(template, keep).ConfigureAwait(false);
        if (sharedLibrary is not null)
        {
            await MergeSharedAsync(sharedLibrary, packaged, keep, report).ConfigureAwait(false);
            GeneratedSharedImages.Link(template, sharedLibrary);
        }
        await reader.KeepFontsAsync(assets, cancellationToken).ConfigureAwait(false);
        template.Name = GeneratedPromoEditing.UniqueName(existingNames, string.IsNullOrWhiteSpace(template.Name) ? reader.Manifest.Name : template.Name);

        if (template.Generator is { } generator && generator.WorkingDirectory == TemplatePackages.Prefix + ProgramEntry)
        {
            string folder = assets.NewProgramFolder(template.Name);
            try
            {
                reader.ExtractFolder(ProgramEntry, folder);
            }
            catch
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left half-unpacked; harmless.
                }

                throw;
            }

            generator.WorkingDirectory = folder;
            Relocate(generator, value => value.Replace(FolderToken, folder, StringComparison.Ordinal));
        }

        return template;
    }

    /// <summary>
    /// Adds the shared images <paramref name="library"/> doesn't have (by id), and the overrides (and default) it lacks
    /// to those it does; what's already there is never changed. Files come through <paramref name="keep"/> only for
    /// what's added.
    /// </summary>
    public static async Task MergeSharedAsync(List<GeneratedSharedImage> library, IEnumerable<GeneratedSharedImage> incoming,
        Func<string?, Task<string?>> keep, GeneratedSharedImport? report = null)
    {
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
                image.Name = GeneratedPromoEditing.UniqueName(library.Select(existing => existing.Name),
                    string.IsNullOrWhiteSpace(image.Name) ? "Shared image" : image.Name.Trim());
                image.Shows = [.. image.Shows.Where(show => !string.IsNullOrWhiteSpace(show.Show))];
                await MapPathsAsync(image, keep).ConfigureAwait(false);
                library.Add(image);
                report?.Added.Add(image.Name);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mine.DefaultPath) && !string.IsNullOrWhiteSpace(image.DefaultPath))
            {
                mine.DefaultPath = await keep(image.DefaultPath).ConfigureAwait(false);
            }

            // An automatic picture it lacks is added too; one it has is kept.
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
                report?.Extended.Add($"{mine.Name} (+{added})");
            }
        }
    }

    /// <summary>The font families the template's text layers are drawn with.</summary>
    public static IReadOnlyList<string> FontsUsed(GeneratedPromoTemplate template) =>
    [
        .. template.Layers
            .Where(layer => layer.Kind is GeneratedLayerKind.Text or GeneratedLayerKind.UpNext || GeneratedPromoKinds.IsShowLayer(layer.Kind))
            .Select(layer => layer.FontFamily?.Trim() ?? string.Empty)
            .Where(family => family.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    private static void Relocate(GeneratedScriptConfig generator, Func<string, string> change)
    {
        generator.Command = change(generator.Command);
        generator.SetupCommand = generator.SetupCommand is null ? null : change(generator.SetupCommand);
        generator.Arguments = [.. generator.Arguments.Select(change)];
        generator.PerShow = [.. generator.PerShow.Select(change)];
        generator.ClipArguments = [.. generator.ClipArguments.Select(change)];
    }

    /// <summary>The folder's path (not a longer one starting the same) swapped for <paramref name="token"/>.</summary>
    private static string ReplaceFolder(string value, string folder, string token) =>
        Regex.Replace(value, Regex.Escape(folder) + @"(?=$|[\\/""'\s])", token.Replace("$", "$$"), PathOptions);
}
