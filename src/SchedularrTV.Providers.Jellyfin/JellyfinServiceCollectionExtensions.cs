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
        services.AddSingleton<IArtworkSource, JellyfinArtworkSource>();

        // Everything else Jellyfin offers, through the generic contracts the app asks every source for.
        services.AddSingleton<IMediaSourceInfo>(sp => sp.GetRequiredService<JellyfinMediaLibrary>());
        services.AddSingleton<JellyfinExtras>();
        services.AddSingleton<IExtrasSource>(sp => sp.GetRequiredService<JellyfinExtras>());
        services.AddSingleton<JellyfinCredits>();
        services.AddSingleton<ICreditsSource>(sp => sp.GetRequiredService<JellyfinCredits>());
        services.AddSingleton<JellyfinLiveViewers>();
        services.AddSingleton<ILiveViewerSource>(sp => sp.GetRequiredService<JellyfinLiveViewers>());
        services.AddSingleton<JellyfinMusicLibrary>();
        services.AddSingleton<IMusicLibrary>(sp => sp.GetRequiredService<JellyfinMusicLibrary>());

        return services;
    }
}
