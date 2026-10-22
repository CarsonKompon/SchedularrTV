using SchedularrTV.Core.Configuration;
using SchedularrTV.Service.Editor;

namespace SchedularrTV.Overlays;

/// <summary>Overlays as template packages (zips with their pictures and videos), for sharing between channels and computers.</summary>
public static class OverlayPackages
{
    public const string Kind = "overlay";

    /// <summary>Where an overlay's chosen and imported files are kept, under the data folder's assets.</summary>
    public const string AssetCategory = "overlays";

    /// <summary>Swaps every file the overlay uses through <paramref name="map"/>.</summary>
    public static async Task MapPathsAsync(Overlay overlay, Func<string?, Task<string?>> map)
    {
        overlay.File = await map(overlay.File).ConfigureAwait(false);
        foreach (var image in overlay.VariableImages)
        {
            image.DefaultPath = await map(image.DefaultPath).ConfigureAwait(false);
            foreach (var show in image.Shows) show.Path = await map(show.Path).ConfigureAwait(false);
        }
        foreach (OverlayLayer layer in overlay.Layers)
        {
            layer.Path = await map(layer.Path).ConfigureAwait(false);
        }

        foreach (OverlayVariant variant in overlay.Variants)
        {
            variant.File = await map(variant.File).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a copy of the overlay and its files into the package; returns its name. The shared images its variable
    /// images use (from <paramref name="sharedLibrary"/>) go in with it, so the package works on its own.
    /// </summary>
    /// <param name="automaticImages">This computer's automatic pictures: those already found for its variable images go in too.</param>
    public static async Task<string> ExportAsync(Overlay overlay, TemplatePackageWriter writer, IEnumerable<GeneratedSharedImage>? sharedLibrary = null,
        SchedularrTV.Service.Images.AutomaticImages? automaticImages = null)
    {
        Overlay copy = overlay.Copy();
        List<GeneratedSharedImage> shared = SharedImagePackages.Collect(copy.VariableImages, sharedLibrary);
        copy.SharedImages = shared.Count == 0 ? null : shared;
        List<GeneratedFoundPicture> found = SharedImagePackages.FoundPictures(
            shared.Cast<IGeneratedImageChoices>().Concat(copy.VariableImages.Where(image => !image.IsShared)), automaticImages, writer.Asset);
        copy.FoundPictures = found.Count == 0 ? null : found;
        await MapPathsAsync(copy, path => Task.FromResult(writer.Asset(path))).ConfigureAwait(false);
        foreach (GeneratedSharedImage image in shared)
        {
            await SharedImagePackages.MapPathsAsync(image, path => Task.FromResult(writer.Asset(path))).ConfigureAwait(false);
        }

        writer.Finish(Kind, overlay.Name, copy);
        return overlay.Name;
    }

    /// <summary>
    /// The package's overlay, its files kept in the data folder. Its shared images are merged into
    /// <paramref name="sharedLibrary"/> (matched by id: one not there is added, one that is only gains what it lacks);
    /// null turns its shared variable images into ones with their own pictures.
    /// </summary>
    public static async Task<(Overlay Overlay, string Summary)> ImportAsync(TemplatePackageReader reader, TemplateAssets assets,
        List<GeneratedSharedImage>? sharedLibrary = null, CancellationToken cancellationToken = default,
        SchedularrTV.Service.Images.AutomaticImages? automaticImages = null)
    {
        Overlay overlay = reader.Item<Overlay>();
        // Automatic pictures found where it was made: this computer's searches take them, so they show straight away.
        List<GeneratedFoundPicture>? found = overlay.FoundPictures;
        overlay.FoundPictures = null;
        await SharedImagePackages.AdoptFoundAsync(found, reader, automaticImages, cancellationToken).ConfigureAwait(false);
        Func<string?, Task<string?>> keep = path => reader.AssetAsync(path, assets, AssetCategory, cancellationToken);
        List<GeneratedSharedImage> packaged = overlay.SharedImages ?? [];
        overlay.SharedImages = null;
        SharedImagePackages.PrepareImport(overlay.VariableImages, packaged, sharedLibrary);
        await MapPathsAsync(overlay, keep).ConfigureAwait(false);

        string summary = string.Empty;
        if (sharedLibrary is not null)
        {
            (List<string> added, List<string> extended) = await SharedImagePackages.MergeAsync(sharedLibrary, packaged, keep).ConfigureAwait(false);
            OverlayImages.Link(overlay, sharedLibrary);
            summary = string.Join(" ", new[]
            {
                added.Count == 0 ? null : $"Added shared images: {string.Join(", ", added)}.",
                extended.Count == 0 ? null : $"Added missing shows to: {string.Join(", ", extended)}."
            }.Where(part => part is not null));
        }

        return (overlay, summary);
    }
}
