using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Plugins.Speech;

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

    /// <summary>Tells the scheduler which channels are being watched, e.g. from a playout engine's activity.</summary>
    public static IServiceCollection AddChannelViewers<TViewers>(this IServiceCollection services)
        where TViewers : class, IChannelViewers
    {
        services.AddSingleton<IChannelViewers, TViewers>();
        return services;
    }
}
