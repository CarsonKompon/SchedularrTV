using Microsoft.Extensions.DependencyInjection;
using SchedularrTV.Core.Media;

namespace SchedularrTV.Providers.FileSystem;

public static class FileSystemServiceCollectionExtensions
{
    public const string LibraryKey = "filesystem";

    public static IServiceCollection AddFileSystemMediaLibrary(this IServiceCollection services)
    {
        services.AddSingleton<FileSystemMediaLibrary>();
        services.AddKeyedSingleton<IMediaLibrary>(LibraryKey, (sp, _) => sp.GetRequiredService<FileSystemMediaLibrary>());

        // The posters, stills and fanart beside local TV shows and movies.
        services.AddSingleton<LocalArtworkSource>();
        services.AddSingleton<IArtworkSource>(sp => sp.GetRequiredService<LocalArtworkSource>());

        return services;
    }
}
