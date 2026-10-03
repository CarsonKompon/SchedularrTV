using System.Collections;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Publishing;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Plugins.Speech;
using PluginEditing = SchedularrTV.Plugins.Editor;

namespace SchedularrTV.Service.Plugins;

/// <summary>
/// Makes plugin switches apply while running. Every loaded plugin registers its services at startup, switched on
/// or not; this then gates what it added by its switch:
/// <list type="bullet">
/// <item>Its contributions (outputs, playout processors, voices, planner features, editor sections, dashboard cards,
/// schedule finishers, viewer counts) are left out of every list of them while it's off, checked each time a list is
/// read.</item>
/// <item>Its media sources are left out of the combined library.</item>
/// <item>Its background services are started when it's switched on and stopped when it's switched off.</item>
/// </list>
/// Its endpoints and pages are gated where they're mapped (see <c>SchedulerHost</c> and <c>Routes.razor</c>).
/// </summary>
public static class PluginServiceGate
{
    /// <summary>The kinds of thing several plugins add to, which are read as a list.</summary>
    public static readonly IReadOnlyList<Type> ContributionTypes =
    [
        typeof(IPlayoutPublisher),
        typeof(IMediaEnricher),
        typeof(IPlayoutProcessor),
        typeof(IChannelViewers),
        typeof(ITextToSpeechProvider),
        typeof(PlannerFeature),
        typeof(IScheduleFinisher),
        typeof(DashboardCard),
        typeof(PluginEditing.PluginEditorSection)
    ];

    /// <summary>
    /// Records what a plugin's <c>ConfigureServices</c> added (everything in <paramref name="services"/> not in
    /// <paramref name="before"/>), and wraps its background services so they follow its switch.
    /// </summary>
    public static void Claim(PluginRegistry registry, string pluginId, IServiceCollection services, IReadOnlyCollection<ServiceDescriptor> before)
    {
        HashSet<ServiceDescriptor> existing = new(before, ReferenceEqualityComparer.Instance);
        for (int index = 0; index < services.Count; index++)
        {
            ServiceDescriptor added = services[index];
            if (existing.Contains(added))
            {
                continue;
            }

            if (added.ServiceType == typeof(IHostedService) && !added.IsKeyedService)
            {
                // Only a service built here from its type is ours to dispose; one from a factory (usually a singleton
                // resolved from the container) or an instance is disposed by the container or its owner.
                services[index] = ServiceDescriptor.Singleton<IHostedService>(sp => new PluginHostedService(
                    pluginId, registry, () => Create(added, sp), sp.GetRequiredService<ILogger<PluginHostedService>>(),
                    disposeInner: added.ImplementationType is not null));
                continue;
            }

            registry.ServiceOwners[added] = pluginId;
        }
    }

    /// <summary>
    /// Replaces each contribution list with one that leaves out switched-off plugins' entries. Call once every plugin
    /// has registered.
    /// </summary>
    /// <remarks>
    /// Each entry is re-registered under a private key (keeping its lifetime), and <c>IEnumerable&lt;T&gt;</c> itself
    /// is registered to resolve the entries whose plugin is running. The container uses an explicit
    /// <c>IEnumerable&lt;T&gt;</c> registration in place of building the list, so this covers constructor injection,
    /// <c>GetServices</c> and <c>@inject</c> alike. A single <c>T</c> still resolves to the last one registered.
    /// </remarks>
    public static void Gate(PluginRegistry registry, IServiceCollection services)
    {
        foreach (Type type in ContributionTypes)
        {
            typeof(PluginServiceGate)
                .GetMethod(nameof(GateType), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type)
                .Invoke(null, [registry, services]);
        }
    }

    private static void GateType<T>(PluginRegistry registry, IServiceCollection services) where T : class
    {
        List<ServiceDescriptor> entries = [.. services.Where(service => service.ServiceType == typeof(T) && !service.IsKeyedService)];
        if (!entries.Any(registry.ServiceOwners.ContainsKey))
        {
            return;
        }

        List<(object Key, string? Owner)> keyed = [];
        foreach (ServiceDescriptor entry in entries)
        {
            var key = new ContributionKey(typeof(T), keyed.Count);
            services.Remove(entry);
            services.Add(Rekey(entry, key));
            keyed.Add((key, registry.ServiceOwners.GetValueOrDefault(entry)));
        }

        services.AddTransient<IEnumerable<T>>(sp => new LiveContributions<T>(sp, keyed, registry));
        // Same lifetime as the entry it forwards to: a transient forwarder would have each scope dispose a disposable
        // singleton when the scope ends.
        object last = keyed[^1].Key;
        services.Add(new ServiceDescriptor(typeof(T), sp => sp.GetRequiredKeyedService(typeof(T), last), entries[^1].Lifetime));
    }

    /// <summary>The running media sources, asked for on each use.</summary>
    public static Func<IServiceProvider, IReadOnlyList<IMediaLibrary>> MediaSources(PluginRegistry registry, IServiceCollection services)
    {
        List<(object? Key, string? Owner)> sources = [.. services
            .Where(service => service.ServiceType == typeof(IMediaLibrary) && service.IsKeyedService)
            .Select(service => (service.ServiceKey, registry.ServiceOwners.GetValueOrDefault(service)))];

        return sp => [.. sources
            .Where(source => source.Owner is null || registry.IsActive(source.Owner))
            .Select(source => sp.GetRequiredKeyedService<IMediaLibrary>(source.Key))];
    }

    private static ServiceDescriptor Rekey(ServiceDescriptor entry, object key) =>
        entry.ImplementationInstance is { } instance ? new ServiceDescriptor(entry.ServiceType, key, instance)
        : entry.ImplementationFactory is { } factory ? new ServiceDescriptor(entry.ServiceType, key, (sp, _) => factory(sp), entry.Lifetime)
        : new ServiceDescriptor(entry.ServiceType, key, entry.ImplementationType!, entry.Lifetime);

    private static object Create(ServiceDescriptor descriptor, IServiceProvider services) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(services)
        ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);

    private sealed record ContributionKey(Type Type, int Index);

    /// <summary>A contribution list that's filtered by the plugins' switches every time it's read.</summary>
    private sealed class LiveContributions<T>(IServiceProvider services, IReadOnlyList<(object Key, string? Owner)> entries, PluginRegistry registry)
        : IEnumerable<T> where T : class
    {
        public IEnumerator<T> GetEnumerator()
        {
            foreach ((object key, string? owner) in entries)
            {
                if (owner is null || registry.IsActive(owner))
                {
                    yield return (T)services.GetRequiredKeyedService(typeof(T), key);
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A plugin's background service, running only while the plugin is switched on.</summary>
public sealed class PluginHostedService(string pluginId, PluginRegistry registry, Func<object> create, ILogger<PluginHostedService> logger,
    bool disposeInner = true)
    : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IHostedService? _inner;
    private bool _running;
    private bool _hostRunning;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _hostRunning = true;
        registry.Changed += OnChanged;
        await SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _hostRunning = false;
        registry.Changed -= OnChanged;
        await SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnChanged() => _ = SyncAsync(CancellationToken.None);

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wanted = _hostRunning && registry.IsActive(pluginId);
            if (wanted && !_running)
            {
                _inner ??= (IHostedService)create();
                await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
                _running = true;
                logger.LogDebug("Started {Service} for plugin {Plugin}.", _inner.GetType().Name, pluginId);
            }
            else if (!wanted && _running && _inner is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await _inner.StopAsync(timeout.Token).ConfigureAwait(false);
                _running = false;
                logger.LogDebug("Stopped {Service} for plugin {Plugin}.", _inner.GetType().Name, pluginId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Plugin {Plugin}'s background service couldn't be {Action}.", pluginId, _running ? "stopped" : "started");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        registry.Changed -= OnChanged;
        if (disposeInner)
        {
            (_inner as IDisposable)?.Dispose();
        }
        _gate.Dispose();
    }
}
