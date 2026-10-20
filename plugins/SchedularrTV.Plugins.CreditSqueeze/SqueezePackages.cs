using SchedularrTV.Service.Editor;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Credit squeeze looks, including every canvas, layer, font and media asset, for sharing between channels and computers.</summary>
public static class SqueezePackages
{
    public const string Kind = "credit-squeeze-look";

    /// <summary>Where a look's chosen and imported files are kept, under the data folder's assets.</summary>
    public const string AssetCategory = "credit-squeeze";

    /// <summary>Writes a copy of the complete look and its media/fonts into the package; returns its name.</summary>
    public static Task<string> ExportAsync(SqueezeLook look, string name, TemplatePackageWriter writer)
    {
        SqueezeTemplate copy = SqueezeTemplate.Of(look, name);
        if (look is SqueezeTemplate template)
        {
            copy.Enabled = template.Enabled;
        }

        copy.BackgroundPath = writer.Asset(copy.BackgroundPath);
        foreach (var layer in copy.Canvases.SelectMany(canvas => canvas.Layers))
            layer.Path = writer.Asset(layer.Path);
        writer.AddFonts(copy.Canvases.SelectMany(canvas => canvas.Layers).Where(layer => layer.Kind == SchedularrTV.Core.Configuration.GeneratedLayerKind.Text)
            .Select(layer => layer.FontFamily).Concat([copy.HeadingFont, copy.TextFont]));
        writer.Finish(Kind, name, copy);
        return Task.FromResult(name);
    }

    /// <summary>The package's complete look as a template, with bundled media and fonts kept in the data folder.</summary>
    public static async Task<SqueezeTemplate> ImportAsync(TemplatePackageReader reader, TemplateAssets assets, CancellationToken cancellationToken = default)
    {
        SqueezeTemplate template = reader.Item<SqueezeTemplate>();
        template.BackgroundPath = await reader.AssetAsync(template.BackgroundPath, assets, AssetCategory, cancellationToken).ConfigureAwait(false);
        foreach (var layer in template.Canvases.SelectMany(canvas => canvas.Layers))
            layer.Path = await reader.AssetAsync(layer.Path, assets, AssetCategory, cancellationToken).ConfigureAwait(false);
        await reader.KeepFontsAsync(assets, cancellationToken).ConfigureAwait(false);
        return template;
    }
}
