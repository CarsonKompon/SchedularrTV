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
        foreach (OverlayLayer layer in overlay.Layers)
        {
            layer.Path = await map(layer.Path).ConfigureAwait(false);
        }

        foreach (OverlayVariant variant in overlay.Variants)
        {
            variant.File = await map(variant.File).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a copy of the overlay and its files into the package; returns its name.</summary>
    public static async Task<string> ExportAsync(Overlay overlay, TemplatePackageWriter writer)
    {
        Overlay copy = overlay.Copy();
        await MapPathsAsync(copy, path => Task.FromResult(writer.Asset(path))).ConfigureAwait(false);
        writer.Finish(Kind, overlay.Name, copy);
        return overlay.Name;
    }

    /// <summary>The package's overlay, its files kept in the data folder.</summary>
    public static async Task<Overlay> ImportAsync(TemplatePackageReader reader, TemplateAssets assets, CancellationToken cancellationToken = default)
    {
        Overlay overlay = reader.Item<Overlay>();
        await MapPathsAsync(overlay, path => reader.AssetAsync(path, assets, AssetCategory, cancellationToken)).ConfigureAwait(false);
        return overlay;
    }
}
