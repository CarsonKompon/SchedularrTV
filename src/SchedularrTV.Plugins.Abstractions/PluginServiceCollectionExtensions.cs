using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Plugins.Speech;
using SchedularrTV.Plugins.Writing;

namespace SchedularrTV.Plugins;

/// <summary>The extension points a plugin can fill.</summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// A library the schedule draws from, merged with the others; the first source to report an item id wins.
    /// <paramref name="key"/> must be unique, usually the plugin id.
    /// </summary>
    public static IServiceCollection AddMediaSource<TLibrary>(this IServiceCollection services, string key)
        where TLibrary : class, IMediaLibrary
    {
        services.TryAddSingleton<TLibrary>();
        services.AddKeyedSingleton<IMediaLibrary>(key, (provider, _) => provider.GetRequiredService<TLibrary>());
        return services;
    }

    /// <summary>
    /// Adds to library items before the scheduler sees them, such as chapter markers found in the file; see
    /// <see cref="IMediaEnricher"/>. Enrichers run in registration order.
    /// </summary>
    public static IServiceCollection AddMediaEnricher<TEnricher>(this IServiceCollection services)
        where TEnricher : class, IMediaEnricher
    {
        services.AddSingleton<IMediaEnricher, TEnricher>();
        return services;
    }

    /// <summary>Somewhere finished schedules are written, such as a playout engine. Outputs run in registration order.</summary>
    public static IServiceCollection AddPlayoutOutput<TOutput>(this IServiceCollection services)
        where TOutput : class, IPlayoutPublisher
    {
        services.AddSingleton<IPlayoutPublisher, TOutput>();
        return services;
    }

    /// <summary>A voice for generated promos.</summary>
    public static IServiceCollection AddSpeechProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, ITextToSpeechProvider
    {
        services.AddSingleton<ITextToSpeechProvider, TProvider>();
        return services;
    }

    /// <summary>A language model that writes voice-overs and on-screen text; see <see cref="ITextGenerator"/>.</summary>
    public static IServiceCollection AddTextGenerator<TGenerator>(this IServiceCollection services)
        where TGenerator : class, ITextGenerator
    {
        services.AddSingleton<ITextGenerator, TGenerator>();
        return services;
    }

    /// <summary>A picture search service for automatic image layers; see <see cref="Images.IImageSearchProvider"/>.</summary>
    public static IServiceCollection AddImageSearchProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, Images.IImageSearchProvider
    {
        services.AddSingleton<Images.IImageSearchProvider, TProvider>();
        return services;
    }

    /// <summary>Changes schedules on their way to the outputs; see <see cref="IPlayoutProcessor"/>.</summary>
    public static IServiceCollection AddPlayoutProcessor<TProcessor>(this IServiceCollection services)
        where TProcessor : class, IPlayoutProcessor
    {
        services.AddSingleton<IPlayoutProcessor, TProcessor>();
        return services;
    }

    /// <summary>Whole channels, schedule included, published alongside the scheduler's own; see <see cref="IExternalChannelSource"/>.</summary>
    public static IServiceCollection AddExternalChannels<TSource>(this IServiceCollection services)
        where TSource : class, IExternalChannelSource
    {
        services.TryAddSingleton<TSource>();
        services.AddSingleton<IExternalChannelSource>(provider => provider.GetRequiredService<TSource>());
        return services;
    }

    /// <summary>A media source's pictures (posters, stills, fanart) for the ids it owns; see <see cref="IArtworkSource"/>.</summary>
    public static IServiceCollection AddArtworkSource<TSource>(this IServiceCollection services)
        where TSource : class, IArtworkSource
    {
        services.TryAddSingleton<TSource>();
        services.AddSingleton<IArtworkSource>(provider => provider.GetRequiredService<TSource>());
        return services;
    }

    /// <summary>
    /// Describes a media source to the app (its name, whether it's set up, loading progress, its libraries); see
    /// <see cref="IMediaSourceInfo"/>. Usually the same class as the <see cref="AddMediaSource{TLibrary}"/> library.
    /// </summary>
    public static IServiceCollection AddMediaSourceInfo<TInfo>(this IServiceCollection services)
        where TInfo : class, IMediaSourceInfo
    {
        services.TryAddSingleton<TInfo>();
        services.AddSingleton<IMediaSourceInfo>(provider => provider.GetRequiredService<TInfo>());
        return services;
    }

    /// <summary>Lists its shows' extras for intros and outros; see <see cref="IExtrasSource"/>.</summary>
    public static IServiceCollection AddExtrasSource<TSource>(this IServiceCollection services)
        where TSource : class, IExtrasSource
    {
        services.TryAddSingleton<TSource>();
        services.AddSingleton<IExtrasSource>(provider => provider.GetRequiredService<TSource>());
        return services;
    }

    /// <summary>Says where items' end credits start; see <see cref="ICreditsSource"/>.</summary>
    public static IServiceCollection AddCreditsSource<TSource>(this IServiceCollection services)
        where TSource : class, ICreditsSource
    {
        services.TryAddSingleton<TSource>();
        services.AddSingleton<ICreditsSource>(provider => provider.GetRequiredService<TSource>());
        return services;
    }

    /// <summary>Says who is watching a media server's live TV, for viewing stats; see <see cref="ILiveViewerSource"/>.</summary>
    public static IServiceCollection AddLiveViewerSource<TSource>(this IServiceCollection services)
        where TSource : class, ILiveViewerSource
    {
        services.TryAddSingleton<TSource>();
        services.AddSingleton<ILiveViewerSource>(provider => provider.GetRequiredService<TSource>());
        return services;
    }

    /// <summary>A music library radio channels can play from; see <see cref="IMusicLibrary"/>.</summary>
    public static IServiceCollection AddMusicLibrary<TLibrary>(this IServiceCollection services)
        where TLibrary : class, IMusicLibrary
    {
        services.TryAddSingleton<TLibrary>();
        services.AddSingleton<IMusicLibrary>(provider => provider.GetRequiredService<TLibrary>());
        return services;
    }

    /// <summary>Tells the scheduler which channels are being watched, e.g. from a playout engine's activity.</summary>
    public static IServiceCollection AddChannelViewers<TViewers>(this IServiceCollection services)
        where TViewers : class, IChannelViewers
    {
        services.AddSingleton<IChannelViewers, TViewers>();
        return services;
    }
}
