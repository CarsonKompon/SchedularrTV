using SchedularrTV.Service.Editor;

namespace SchedularrTV.CreditSqueeze;

/// <summary>Credit squeeze looks as template packages (zips with their background), for sharing between channels and computers.</summary>
public static class SqueezePackages
{
    public const string Kind = "credit-squeeze-look";

    /// <summary>Where a look's chosen and imported files are kept, under the data folder's assets.</summary>
    public const string AssetCategory = "credit-squeeze";

    /// <summary>Writes a copy of the look and its background into the package; returns its name.</summary>
    public static Task<string> ExportAsync(SqueezeLook look, string name, TemplatePackageWriter writer)
    {
        SqueezeTemplate copy = SqueezeTemplate.Of(look, name);
        if (look is SqueezeTemplate template)
        {
            copy.Enabled = template.Enabled;
        }

        copy.BackgroundPath = writer.Asset(copy.BackgroundPath);
        writer.Finish(Kind, name, copy);
        return Task.FromResult(name);
    }

    /// <summary>The package's look as a template, its background kept in the data folder.</summary>
    public static async Task<SqueezeTemplate> ImportAsync(TemplatePackageReader reader, TemplateAssets assets, CancellationToken cancellationToken = default)
    {
        SqueezeTemplate template = reader.Item<SqueezeTemplate>();
        template.BackgroundPath = await reader.AssetAsync(template.BackgroundPath, assets, AssetCategory, cancellationToken).ConfigureAwait(false);
        return template;
    }
}
