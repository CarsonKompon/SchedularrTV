using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Plugins;

namespace SchedularrTV.Providers.ErsatzTv;

/// <summary>ErsatzTV Next as the output: lineup, playouts and guide, plus optionally running ErsatzTV itself.</summary>
public sealed class ErsatzTvPlugin : IBuiltInPlugin
{
    public const string PluginId = "ersatztv";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "ErsatzTV Next",
        Version = typeof(ErsatzTvPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        Description = "Writes channel lineups, playouts and guides for ErsatzTV Next, and can start and stop it with the scheduler.",
        Author = "SchedularrTV",
        ApiVersion = PluginApi.Version.ToString(),
        // The only output so far, and the source of "is anyone watching" for generated promos.
        Required = true,
        SettingsLink = "settings/app"
    };

    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        IConfiguration configuration = context.AppConfiguration;
        services.Configure<ErsatzTvOptions>(configuration.GetSection(ErsatzTvOptions.SectionName));

        // Guide artwork is served by the media server, so it defaults to wherever that is.
        services.PostConfigure<ErsatzTvOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.ArtworkBaseUrl))
            {
                options.ArtworkBaseUrl = configuration["Jellyfin:BaseUrl"];
            }

            // Local shows' and movies' pictures are served by the scheduler itself.
            if (string.IsNullOrWhiteSpace(options.LocalArtworkBaseUrl))
            {
                string port = int.TryParse(configuration["Server:Port"], out int configured) ? configured.ToString(System.Globalization.CultureInfo.InvariantCulture) : "5252";
                options.LocalArtworkBaseUrl = $"http://localhost:{port}";
            }
        });

        services.AddErsatzTvPublishing();
    }
}
