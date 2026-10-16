using Microsoft.Extensions.DependencyInjection;

namespace SchedularrTV.Images.SerpApi;

/// <summary>Google Images results through SerpApi, for automatic image layers.</summary>
public sealed class SerpApiImagesPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddHttpClient(SerpApiImageSearch.ProviderId, http => http.Timeout = TimeSpan.FromSeconds(60));

        // Read on each search, so a key saved on the Plugins page applies straight away.
        services.AddSingleton(provider => new SerpApiImageSearch(
            provider.GetRequiredService<IHttpClientFactory>(),
            () => context.Configuration["ApiKey"] is { Length: > 0 } key ? key : context.AppConfiguration[SerpApiImageSearch.KeyVariable],
            () => !string.Equals(context.Configuration["SafeSearch"], "false", StringComparison.OrdinalIgnoreCase),
            () => context.Configuration["Country"]));
        services.AddSingleton<IImageSearchProvider>(provider => provider.GetRequiredService<SerpApiImageSearch>());
    }
}
