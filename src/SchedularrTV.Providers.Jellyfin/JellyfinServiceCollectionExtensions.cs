using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.Jellyfin;

public static class JellyfinServiceCollectionExtensions
{
    public const string LibraryKey = "jellyfin";

    public static IServiceCollection AddJellyfinMediaLibrary(this IServiceCollection services)
    {
        services.AddHttpClient(JellyfinClient.HttpClientName, (provider, client) =>
        {
            JellyfinOptions options = provider.GetRequiredService<IOptions<JellyfinOptions>>().Value;
            client.Timeout = options.RequestTimeout;
        });

        services.AddSingleton<JellyfinClient>();
        services.AddSingleton<JellyfinMediaLibrary>();

        // Keyed so the composite library can collect every source without depending on itself.
        services.AddKeyedSingleton<IMediaLibrary>(LibraryKey, (sp, _) => sp.GetRequiredService<JellyfinMediaLibrary>());

        return services;
    }
}
