using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Service;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Plugins;
using SchedularrTV.Plugins.Playout;
using SchedularrTV.Service.Plugins;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class PluginSystemTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"plugins-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private sealed class FakePlugin(string id, params PluginDependency[] dependencies) : IBuiltInPlugin
    {
        public List<string> Calls { get; } = [];

        public PluginManifest Manifest { get; } = new() { Id = id, Name = id, ApiVersion = PluginApi.Version.ToString(), Dependencies = [.. dependencies] };

        public Action<IServiceCollection>? Register { get; init; }

        public void ConfigureServices(IServiceCollection services, IPluginContext context)
        {
            Calls.Add(id);
            Register?.Invoke(services);
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))).Build();

    [Fact]
    public void A_missing_dependency_keeps_a_plugin_from_loading_but_a_switched_off_one_only_makes_it_wait()
    {
        var needsMissing = new FakePlugin("needs.missing", new PluginDependency { Id = "nowhere" });
        var needsOff = new FakePlugin("needs.off", new PluginDependency { Id = "off" });
        var off = new FakePlugin("off");
        var optional = new FakePlugin("optional.only", new PluginDependency { Id = "nowhere", Optional = true });

        PluginRegistry registry = PluginLoader.Load(_data, Config(("PluginStates:off", "false")), [needsMissing, needsOff, off, optional]);

        Assert.Equal(PluginStatus.MissingDependency, registry.Find("needs.missing")!.Status);
        Assert.Contains("isn't installed", registry.Find("needs.missing")!.Problem);

        // Switched off, but loaded: its switch applies while running.
        Assert.True(registry.IsLoaded("off"));
        Assert.False(registry.IsActive("off"));
        Assert.True(registry.IsLoaded("needs.off"));
        Assert.False(registry.IsActive("needs.off"));
        Assert.Contains("Waiting for off", registry.Find("needs.off")!.Blocked);
        Assert.True(registry.IsActive("optional.only"));

        Assert.True(registry.SetEnabled("off", true));
        Assert.True(registry.IsActive("off"));
        Assert.True(registry.IsActive("needs.off"));
        Assert.Null(registry.Find("needs.off")!.Blocked);
    }

    [Fact]
    public async Task Switching_a_plugin_off_while_running_drops_its_contributions_media_and_background_services()
    {
        var host = new DashboardCard(typeof(object), 1);
        var card = new DashboardCard(typeof(string), 2);
        var media = new FakeLibrary();
        var plugin = new FakePlugin("switchable")
        {
            Register = services =>
            {
                services.AddSingleton(card);
                services.AddKeyedSingleton<SchedularrTV.Core.Media.IMediaLibrary>("switchable", media);
                services.AddSingleton<FakeWorker>();
                services.AddHostedService(sp => sp.GetRequiredService<FakeWorker>());
            }
        };

        PluginRegistry registry = PluginLoader.Load(_data, Config(("PluginStates:switchable", "false")), [plugin]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(host);
        PluginLoader.ConfigureServices(registry, services, _data, Config());
        Func<IServiceProvider, IReadOnlyList<SchedularrTV.Core.Media.IMediaLibrary>> sources = PluginServiceGate.MediaSources(registry, services);
        PluginServiceGate.Gate(registry, services);
        using ServiceProvider provider = services.BuildServiceProvider();

        IEnumerable<DashboardCard> cards = provider.GetServices<DashboardCard>();
        IHostedService hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        FakeWorker worker = provider.GetRequiredService<FakeWorker>();

        Assert.Equal([host], cards);
        Assert.Empty(sources(provider));
        Assert.False(worker.Running);

        registry.SetEnabled("switchable", true);
        SpinWait.SpinUntil(() => worker.Running, TimeSpan.FromSeconds(5));

        // The same list object follows the switch: consumers that kept it see the change.
        Assert.Equal([host, card], cards);
        Assert.Same(media, Assert.Single(sources(provider)));
        Assert.True(worker.Running);

        registry.SetEnabled("switchable", false);
        SpinWait.SpinUntil(() => !worker.Running, TimeSpan.FromSeconds(5));

        Assert.Equal([host], cards);
        Assert.False(worker.Running);
        await hosted.StopAsync(CancellationToken.None);
    }

    private sealed class FakeLibrary : SchedularrTV.Core.Media.IMediaLibrary
    {
        public ValueTask<IReadOnlyList<SchedularrTV.Core.Media.MediaItem>> SearchAsync(
            SchedularrTV.Core.Query.QueryExpression expression, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SchedularrTV.Core.Media.MediaItem>>([]);

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeWorker : IHostedService
    {
        public volatile bool Running;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Running = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Running = false;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Plugins_register_after_their_dependencies_and_a_failing_one_leaves_nothing_behind()
    {
        List<string> order = [];
        var promos = new FakePlugin("promos") { Register = services => { order.Add("promos"); services.AddSingleton("promos"); } };
        var generated = new FakePlugin("generated", new PluginDependency { Id = "promos" })
        {
            Register = services => { order.Add("generated"); services.AddSingleton(new object()); throw new InvalidOperationException("boom"); }
        };

        // Declared dependent-first on purpose.
        PluginRegistry registry = PluginLoader.Load(_data, Config(), [generated, promos]);
        var services = new ServiceCollection();
        PluginLoader.ConfigureServices(registry, services, _data, Config());

        Assert.Equal(new[] { "promos", "generated" }, order);
        Assert.Equal(PluginStatus.Failed, registry.Find("generated")!.Status);
        Assert.Contains("boom", registry.Find("generated")!.Problem);
        Assert.Single(services, service => service.ServiceType == typeof(string));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(object));
    }

    [Fact]
    public void Dependency_loops_are_reported()
    {
        var a = new FakePlugin("loop.a", new PluginDependency { Id = "loop.b" });
        var b = new FakePlugin("loop.b", new PluginDependency { Id = "loop.a" });

        PluginRegistry registry = PluginLoader.Load(_data, Config(), [a, b]);

        Assert.All(new[] { "loop.a", "loop.b" }, id => Assert.Equal(PluginStatus.Failed, registry.Find(id)!.Status));
    }

    [Fact]
    public void Installed_plugins_with_bad_manifests_or_assemblies_are_reported_not_loaded()
    {
        string plugins = PluginLoader.UserDirectory(_data);
        Directory.CreateDirectory(Path.Combine(plugins, "no-manifest"));
        Directory.CreateDirectory(Path.Combine(plugins, "bad.id"));
        File.WriteAllText(Path.Combine(plugins, "bad.id", "plugin.json"), """{ "id": "Bad Id!", "entryAssembly": "x.dll" }""");
        Directory.CreateDirectory(Path.Combine(plugins, "missing.dll"));
        File.WriteAllText(Path.Combine(plugins, "missing.dll", "plugin.json"), """{ "id": "missing.dll", "name": "Missing", "apiVersion": "1.0", "entryAssembly": "Missing.dll" }""");
        Directory.CreateDirectory(Path.Combine(plugins, "future"));
        File.WriteAllText(Path.Combine(plugins, "future", "plugin.json"), """{ "id": "future", "apiVersion": "2.0", "entryAssembly": "Future.dll" }""");

        PluginRegistry registry = PluginLoader.Load(_data, Config(), []);

        Assert.Equal(PluginStatus.Failed, registry.Find("no-manifest")!.Status);
        Assert.Contains("must be", registry.Plugins.Single(plugin => plugin.Directory!.EndsWith("bad.id")).Problem);
        Assert.Equal(PluginStatus.Failed, registry.Find("missing.dll")!.Status);
        Assert.Contains("Couldn't load", registry.Find("missing.dll")!.Problem);
        Assert.Equal(PluginStatus.Incompatible, registry.Find("future")!.Status);
    }

    [Fact]
    public void Plugins_installed_in_the_data_folder_by_an_earlier_version_move_to_the_apps_plugins_folder()
    {
        string legacy = PluginLoader.UserDirectory(_data);
        string app = Path.Combine(_data, "app", "plugins");
        foreach (string id in new[] { "moved", "kept" })
        {
            Directory.CreateDirectory(Path.Combine(legacy, id));
            File.WriteAllText(Path.Combine(legacy, id, "plugin.json"), $$"""{ "id": "{{id}}", "apiVersion": "1.0", "entryAssembly": "{{id}}.dll" }""");
        }

        // A plugin of the same name already in the app's folder wins; the old copy is left alone.
        Directory.CreateDirectory(Path.Combine(app, "kept"));
        File.WriteAllText(Path.Combine(app, "kept", "plugin.json"), """{ "id": "kept", "version": "2.0.0", "apiVersion": "1.0", "entryAssembly": "kept.dll" }""");

        PluginRegistry registry = PluginLoader.Load(_data, Config(), [], app);

        Assert.Equal(app, registry.UserDirectory);
        Assert.True(File.Exists(Path.Combine(app, "moved", "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(legacy, "moved")));
        Assert.Equal("2.0.0", registry.Find("kept")!.Manifest.Version);
        Assert.True(Directory.Exists(Path.Combine(legacy, "kept")));
        Assert.StartsWith(app, registry.Find("moved")!.Directory);
    }

    [Fact]
    public void The_plugins_folder_can_be_chosen_on_the_command_line_and_is_the_data_folders_for_a_source_checkout()
    {
        string chosen = Path.Combine(_data, "elsewhere");
        Assert.Equal(Path.GetFullPath(chosen), PluginLoader.ResolveInstallDirectory([PluginLoader.PluginsDirectoryArgument, chosen], _data, runningFromSource: false));
        Assert.Equal(PluginLoader.UserDirectory(_data), PluginLoader.ResolveInstallDirectory([], _data, runningFromSource: true));

        string checkout = Path.Combine(_data, "repo");
        Assert.Equal(Path.Combine(checkout, SourcePlugins.InstallFolder), PluginLoader.ResolveInstallDirectory([], _data, runningFromSource: true, checkout));
    }

    [Fact]
    public void From_a_checkout_the_repositorys_plugins_load_and_the_data_folders_are_left_alone()
    {
        string legacy = PluginLoader.UserDirectory(_data);
        Directory.CreateDirectory(Path.Combine(legacy, "old"));
        File.WriteAllText(Path.Combine(legacy, "old", "plugin.json"), """{ "id": "old", "apiVersion": "1.0", "entryAssembly": "old.dll" }""");
        string built = Path.Combine(_data, "repo", "plugins", "Fresh", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(built);
        File.WriteAllText(Path.Combine(built, "plugin.json"), """{ "id": "fresh", "apiVersion": "1.0", "entryAssembly": "Fresh.dll" }""");
        string installed = Path.Combine(_data, "repo", SourcePlugins.InstallFolder);
        File.WriteAllText(Path.Combine(_data, "repo", SourcePlugins.SolutionFileName), "<Solution />");

        PluginRegistry registry = PluginLoader.Load(_data, Config(), [], installed, [built]);

        Assert.Equal(PluginSource.Bundled, registry.Find("fresh")!.Source);
        Assert.Null(registry.Find("old"));
        Assert.True(Directory.Exists(Path.Combine(legacy, "old")));
        Assert.Equal(Path.Combine(_data, "repo"), SourcePlugins.Root(built));

        // The tray runs the server from a folder inside its own build output.
        string sep = Path.DirectorySeparatorChar.ToString();
        Assert.Equal(("Debug", "net10.0"), SourcePlugins.BuildOf(string.Join(sep, "repo", "src", "SchedularrTV.Tray", "bin", "Debug", "net10.0", "server")));
        Assert.Equal(("Release", "net10.0"), SourcePlugins.BuildOf(string.Join(sep, "repo", "src", "SchedularrTV.Service", "bin", "Release", "net10.0")));
    }

    [Fact]
    public void Plugin_sections_in_the_schedule_round_trip_and_bind_with_block_then_channel_then_global_precedence()
    {
        const string yaml = """
            settings:
              plugins:
                acme.bug:
                  image: global.png
                  opacity: 50
            channels:
            - id: ch1
              name: One
              number: 1
              base_rotation: { query: "kind == 'Episode'" }
              plugins:
                acme.bug:
                  image: channel.png
              blocks:
              - name: Late
                time: 22:00
                end_time: 23:00
                shows: [{ query: "kind == 'Episode'" }]
                plugins:
                  Acme.Bug:
                    image: block.png
            """;

        SchedulerConfig config = SchedulerConfigLoader.Parse(SchedulerConfigLoader.Serialize(SchedulerConfigLoader.Parse(yaml)));
        ChannelConfig channel = config.Channels[0];
        var schedule = new ChannelSchedule { ChannelId = "ch1", ChannelName = "One", Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch, Entries = [] };
        var context = new PlayoutContext(config, channel, schedule);
        ScheduleEntry late = new() { Start = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromMinutes(1), Kind = ScheduleEntryKind.Program, BlockName = "Late" };
        ScheduleEntry other = late with { BlockName = null };

        Assert.Equal("block.png", context.Settings<BugSettings>("acme.bug", late)!.Image);
        Assert.Equal("channel.png", context.Settings<BugSettings>("acme.bug", other)!.Image);
        Assert.Equal(50, PluginSettings.Read<BugSettings>(config.Settings.Plugins, "acme.bug")!.Opacity);
        Assert.Null(context.Settings<BugSettings>("someone.else"));
        Assert.True(SchedulerConfigLoader.Validate(config).IsValid);
    }

    public sealed class BugSettings
    {
        public string? Image { get; set; }

        public int Opacity { get; set; } = 100;
    }
}
