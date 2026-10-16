using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Images.Klipy;

/// <summary>Klipy GIF and sticker search for variable image layers.</summary>
public sealed class KlipyImagesPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        // The API key is part of Klipy's URL path; don't include request URLs in HTTP logs.
        services.AddHttpClient(KlipyImageSearch.ProviderId, http => http.Timeout = TimeSpan.FromSeconds(60))
            .RemoveAllLoggers();
        services.AddSingleton(provider => new KlipyImageSearch(
            provider.GetRequiredService<IHttpClientFactory>(),
            () => !string.IsNullOrWhiteSpace(context.Configuration["ApiKey"])
                ? context.Configuration["ApiKey"] : context.AppConfiguration[KlipyImageSearch.KeyVariable],
            () => !string.Equals(context.Configuration["SafeSearch"], "false", StringComparison.OrdinalIgnoreCase),
            () => context.Configuration["Country"]));
        services.AddSingleton<IImageSearchProvider>(provider => provider.GetRequiredService<KlipyImageSearch>());
    }
}
