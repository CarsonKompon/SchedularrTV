using System.Text.RegularExpressions;
using SchedularrTV.Core.Configuration;

namespace SchedularrTV.GeneratedPromos;

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

    /// <summary>Writes a copy of the template, its files and its generator's folder into the package; returns its name.</summary>
    public static async Task<string> ExportAsync(GeneratedPromoTemplate template, TemplatePackageWriter writer)
    {
        GeneratedPromoTemplate copy = GeneratedPromoEditing.Copy(template);
        await MapPathsAsync(copy, path => Task.FromResult(writer.Asset(path))).ConfigureAwait(false);

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

    /// <summary>
    /// The package's template, its files kept in the data folder and its generator's folder unpacked there, named so it
    /// doesn't clash with <paramref name="existingNames"/>.
    /// </summary>
    public static async Task<GeneratedPromoTemplate> ImportAsync(TemplatePackageReader reader, TemplateAssets assets, IEnumerable<string> existingNames,
        CancellationToken cancellationToken = default)
    {
        GeneratedPromoTemplate template = reader.Item<GeneratedPromoTemplate>();
        await MapPathsAsync(template, path => reader.AssetAsync(path, assets, AssetCategory, cancellationToken)).ConfigureAwait(false);
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
