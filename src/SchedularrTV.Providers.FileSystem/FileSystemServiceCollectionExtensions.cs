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

        // What the folders offer besides items and pictures, through the generic contracts.
        services.AddSingleton<IMediaSourceInfo>(sp => sp.GetRequiredService<FileSystemMediaLibrary>());
        services.AddSingleton<IExtrasSource>(sp => sp.GetRequiredService<FileSystemMediaLibrary>());
        services.AddSingleton<LocalMusicLibrary>();
        services.AddSingleton<IMusicLibrary>(sp => sp.GetRequiredService<LocalMusicLibrary>());

        return services;
    }
}
