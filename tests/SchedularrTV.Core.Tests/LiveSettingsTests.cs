using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service.Hosting;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class LiveSettingsTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"live-settings-{Guid.NewGuid():N}");

    public LiveSettingsTests() => Directory.CreateDirectory(_data);

    public void Dispose() => Directory.Delete(_data, recursive: true);

    public sealed class FakeOptions
    {
        public string Server { get; set; } = "default";
    }

    [Fact]
    public void Saved_settings_reach_options_without_a_restart()
    {
        string settings = Path.Combine(_data, "settings.json");
        File.WriteAllText(settings, """{ "Fake": { "Server": "first" } }""");
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile(settings, optional: true, reloadOnChange: false).Build();

        var services = new ServiceCollection();
        services.Configure<FakeOptions>(configuration.GetSection("Fake"));
        services.Replace(ServiceDescriptor.Singleton(typeof(IOptions<>), typeof(LiveOptions<>)));
        using ServiceProvider provider = services.BuildServiceProvider();
        IOptions<FakeOptions> options = provider.GetRequiredService<IOptions<FakeOptions>>();
        Assert.Equal("first", options.Value.Server);

        var runtime = new AppRuntime(_data, _data, null!, configuration);
        int saved = 0;
        runtime.SettingsSaved += () => saved++;
        JsonObject file = runtime.Settings.Load();
        file["Fake"] = new JsonObject { ["Server"] = "second" };
        runtime.SaveSettings(file);

        Assert.Equal("second", options.Value.Server);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void A_builder_with_live_features_picks_up_features_switched_on_or_off_between_builds()
    {
        List<PlannerFeature> running = [PlannerFeature.Promos];
        var builder = new ScheduleBuilder(new SchedularrTV.Core.Media.CompositeMediaLibrary([]), new InMemoryPlaybackStateStore(), null, () => [.. running]);

        Assert.Equal([PlannerFeature.Promos], builder.Features);

        running.Add(PlannerFeature.ArchiveFollow);
        Assert.Equal([PlannerFeature.Promos, PlannerFeature.ArchiveFollow], builder.Features);

        running.Clear();
        Assert.Empty(builder.Features);
    }

    [Fact]
    public async Task A_combined_library_asks_for_its_sources_on_each_use()
    {
        List<SchedularrTV.Core.Media.IMediaLibrary> sources = [];
        var library = new SchedularrTV.Core.Media.CompositeMediaLibrary(() => [.. sources]);
        var item = new SchedularrTV.Core.Media.MediaItem { Id = "a", Title = "A" };

        Assert.Empty(await library.SearchAsync(new SchedularrTV.Core.Query.AllExpression()));

        sources.Add(new OneItem(item));
        Assert.Same(item, Assert.Single(await library.SearchAsync(new SchedularrTV.Core.Query.AllExpression())));
    }

    private sealed class OneItem(SchedularrTV.Core.Media.MediaItem item) : SchedularrTV.Core.Media.IMediaLibrary
    {
        public ValueTask<IReadOnlyList<SchedularrTV.Core.Media.MediaItem>> SearchAsync(
            SchedularrTV.Core.Query.QueryExpression expression, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SchedularrTV.Core.Media.MediaItem>>([item]);

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
