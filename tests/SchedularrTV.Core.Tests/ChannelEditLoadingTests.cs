using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SchedularrTV.Core.Configuration;
using SchedularrTV.Core.Media;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Query;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Service;
using SchedularrTV.Service.Components.Pages;
using SchedularrTV.Service.Components.Shared;
using SchedularrTV.Service.Editor;
using Xunit;

namespace SchedularrTV.Core.Tests;

public sealed class ChannelEditLoadingTests
{
    [Theory]
    [InlineData("collection")]
    [InlineData("query")]
    [InlineData("include")]
    public async Task Selector_source_is_visible_before_library_loading_finishes(string mode)
    {
        var library = new PausedLibrary();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SchedulerServiceOptions>>(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(Path.GetTempPath(), $"missing-selector-test-{Guid.NewGuid():N}.yaml")
        }));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMediaLibrary>(library);
        services.AddSingleton<SchedulerConfigProvider>();
        services.AddSingleton<ConfigEditor>();
        services.AddSingleton<LibraryBrowser>();
        services.AddHttpClient();
        services.AddSingleton<IOptions<SchedularrTV.Providers.Jellyfin.JellyfinOptions>>(
            Options.Create(new SchedularrTV.Providers.Jellyfin.JellyfinOptions()));
        services.AddSingleton<SchedularrTV.Providers.Jellyfin.JellyfinClient>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<ConfigEditor>().Working.GlobalCollections["Shorts"] = new();
        var selector = new ContentSelector
        {
            Collection = mode == "collection" ? "Shorts" : null,
            Query = mode == "query" ? "kind == 'Episode'" : null,
            Include = mode == "include" ? [new ContentInclude { Show = "Short show" }] : []
        };
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent<SelectorEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(SelectorEditor.Selector)] = selector }));
            try
            {
                Assert.False(rendered.QuiescenceTask.IsCompleted);
                string html = rendered.ToHtmlString();
                Assert.Contains($"<select value=\"{mode}\"", html);
                Assert.Contains(mode == "collection" ? "<select value=\"Shorts\"" : mode == "query" ? "kind ==" : "Short show", html);
                Assert.Equal(mode == "collection" ? "Shorts" : null, selector.Collection);
            }
            finally
            {
                library.Complete();
                await rendered.QuiescenceTask;
            }
        });
    }

    [Theory]
    [InlineData("existing", true)]
    [InlineData("missing", false)]
    public async Task Channel_lookup_does_not_wait_for_the_library_scan(string channelId, bool exists)
    {
        var library = new PausedLibrary();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<SchedulerServiceOptions>>(Options.Create(new SchedulerServiceOptions
        {
            ConfigPath = Path.Combine(Path.GetTempPath(), $"missing-channel-test-{Guid.NewGuid():N}.yaml"),
            WatchConfig = false,
            AutoRebuild = false
        }));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMediaLibrary>(library);
        services.AddSingleton<IPlaybackStateStore, InMemoryPlaybackStateStore>();
        services.AddSingleton<SchedulerConfigProvider>();
        services.AddSingleton<ConfigEditor>();
        services.AddSingleton<LibraryBrowser>();
        services.AddSingleton<LogoStore>();
        services.AddSingleton<ScheduleBuilder>();
        services.AddSingleton<ScheduleCoordinator>();
        services.AddSingleton<RotationResetter>();
        // The General tab shows whether a 24/7 channel is playing (never started here, so it has nothing to say).
        services.AddHttpClient();
        services.AddSingleton<SchedularrTV.Core.Publishing.AlwaysOnChannels>();
        services.AddSingleton<AlwaysOnKeeper>();
        // The Archive Follow plugin's channel tab, as the plugin registers it.
        services.AddSingleton(new SchedularrTV.Plugins.Editor.PluginEditorSection
        {
            PluginId = BuiltInPluginIds.ArchiveFollow,
            Title = "Follow",
            Surface = SchedularrTV.Plugins.Editor.EditorSurface.Channel,
            Component = typeof(SchedularrTV.Service.Components.Features.ArchiveFollowChannelTab),
            Order = 20
        });
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton<IJSRuntime, UnusedJavaScript>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<ConfigEditor>().Working.Channels.Add(new ChannelConfig
        {
            Id = "existing", Name = "Configured Channel", Number = 47
        });
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ChannelEdit.ChannelId)] = channelId });
            var rendered = renderer.BeginRenderingComponent<ChannelEdit>(parameters);
            try
            {
                // Rendered while the library is still paused: nothing on the page waits for the scan.
                string html = rendered.ToHtmlString();
                if (exists)
                {
                    Assert.Contains("Configured Channel", html);
                    Assert.Contains("Follow", html);
                    Assert.DoesNotContain("Channel not found", html);
                }
                else
                {
                    Assert.Contains("Channel not found", html);
                }
            }
            finally
            {
                library.Complete();
                await rendered.QuiescenceTask;
            }
        });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Follow_options_render_before_library_load_and_coverage_needs_no_full_scan(bool weekday, bool month)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"follow-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "2002", "09"));
        File.WriteAllText(Path.Combine(directory, "2002", "09", "2002-09-02.json"), "{}");
        try
        {
            var library = new PausedLibrary();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IMediaLibrary>(library);
            services.AddSingleton<LibraryBrowser>();
            services.AddSingleton<ArchiveBrowser>();
            services.AddSingleton<IJSRuntime, UnusedJavaScript>();
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var channel = new ChannelConfig
                {
                    Id = "follow", Name = "Follow", Follow = new()
                    {
                        Directory = directory, StartYear = 2002, EndYear = 2002,
                        FollowWeekday = weekday, FollowMonth = month
                    }
                };
                var rendered = renderer.BeginRenderingComponent<FollowEditor>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(FollowEditor.Channel)] = channel }));
                try
                {
                    Assert.False(rendered.QuiescenceTask.IsCompleted);
                    string html = rendered.ToHtmlString();
                    Assert.Contains("Follow weekday", html);
                    Assert.Equal(weekday, html.Contains("Follow month", StringComparison.Ordinal));
                    Assert.Contains("Counting archive dates", html);
                }
                finally
                {
                    library.Complete();
                    await rendered.QuiescenceTask;
                }
                string completed = rendered.ToHtmlString();
                Assert.Contains("Average matches per day", completed);
                Assert.Contains("Days with 0 possibilities", completed);
                Assert.Contains("Days with only 1 possibility", completed);
                Assert.Contains("1 archive dates", completed);
                Assert.DoesNotContain("Counting archive dates", completed);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PausedLibrary : IMediaLibrary
    {
        private readonly TaskCompletionSource<IReadOnlyList<MediaItem>> _items = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<MediaItem>> SearchAsync(QueryExpression expression, CancellationToken cancellationToken = default) =>
            new(_items.Task.WaitAsync(cancellationToken));

        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void Complete() => _items.TrySetResult([]);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/channels/existing");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class UnusedJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}