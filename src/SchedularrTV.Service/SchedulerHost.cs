using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using SchedularrTV.Core.Media;
using SchedularrTV.Hosting;
using SchedularrTV.Service.Hosting;
using SchedularrTV.Core.Ordering;
using SchedularrTV.Core.Scheduling;
using SchedularrTV.Providers.ErsatzTv;
using SchedularrTV.Providers.FileSystem;
using SchedularrTV.Service.Components;
using SchedularrTV.Service.Editor;
using SchedularrTV.Service.Plugins;

namespace SchedularrTV.Service;

/// <summary>
/// Builds the scheduler host. Shared so the console entry point and the tray application start an
/// identical server rather than drifting apart.
/// </summary>
public static class SchedulerHost
{
    public static WebApplication Create(
        string[] args,
        Action<WebApplicationBuilder>? configure = null,
        string? contentRoot = null)
    {
        // A tray or service host is launched from an arbitrary working directory, so the content
        // root has to be stated explicitly or relative config paths resolve somewhere unexpected.
        // The application name is pinned to this assembly because the tray is the entry assembly when
        // it hosts us, and the static web asset manifests are named after *this* project.
        // Started from a shell in the project folder (dotnet run) the working directory is the content
        // root; started from anywhere else (the tray, a login item, systemd) the app's own folder is.
        contentRoot ??= File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
            ? Directory.GetCurrentDirectory()
            : AppContext.BaseDirectory;

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
            ApplicationName = typeof(SchedulerHost).Assembly.GetName().Name
        });

        // In a non-published build the wwwroot/_framework files only exist in obj/, reachable through
        // the generated runtime manifest. The framework only loads that manifest in Development, so
        // ask for it explicitly; in a published layout the files are real and this is a no-op.
        builder.WebHost.UseStaticWebAssets();

        // User secrets and a .env further up the folder tree are a developer's settings for a source checkout.
        // A downloaded build must never read them: they're per-machine, so a developer's Jellyfin key and
        // absolute schedule path would otherwise leak into every build run on that machine (skipping first-run
        // setup and writing the schedule into the repo). The framework only loads secrets in Development, and
        // `dotnet run` here runs as Production, hence the source-checkout test.
        bool fromSource = builder.Environment.IsDevelopment() || RunningFromSource(contentRoot);
        if (fromSource)
        {
            builder.Configuration.AddUserSecrets(typeof(SchedulerHost).Assembly, optional: true);
        }

        // A local .env file is the lowest-priority source; real environment variables and secrets win. An
        // installed build only reads one beside the program (or the content root), never from parent folders.
        string? dotEnv = fromSource
            ? DotEnvFile.Find(builder.Environment.ContentRootPath, AppContext.BaseDirectory)
            : new[] { builder.Environment.ContentRootPath, AppContext.BaseDirectory }
                .Select(folder => Path.Combine(folder, ".env"))
                .FirstOrDefault(File.Exists);
        if (dotEnv is not null)
        {
            builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
            {
                InitialData = DotEnvFile.Parse(File.ReadLines(dotEnv)).Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))
            });
        }

        // Everything the app writes lives in the data folder, and settings entered in the app (settings.json
        // there) are added last so they win over appsettings, secrets and the environment.
        string dataDirectory = AppPaths.ResolveDataDirectory(args, builder.Environment.ContentRootPath);
        Directory.CreateDirectory(dataDirectory);
        var settingsFile = new AppSettingsFile(AppPaths.SettingsPath(dataDirectory));
        builder.Configuration.AddJsonFile(settingsFile.Path, optional: true, reloadOnChange: false);
        System.Text.Json.Nodes.JsonObject userSettings = settingsFile.Load();

        // Raspberry Pi (low-power) defaults and warnings follow detection unless App settings says otherwise. Read live,
        // so changing it applies without a restart; set before plugins load, since some read it as they start.
        // The live encoding settings are read through the same reader by the scheduler and every plugin.
        IConfiguration platformConfiguration = builder.Configuration;
        SchedularrTV.Core.Platform.PlatformSettings.Use(key => platformConfiguration[key]);

        // An explicit --urls or ASPNETCORE_URLS still wins; otherwise the port chosen in the app is used.
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls(AppPaths.BindUrl(AppSettingsFile.Port(userSettings), AppSettingsFile.AllowRemote(userSettings)));
        }

        builder.Services.AddSingleton(sp => new AppRuntime(dataDirectory, builder.Environment.ContentRootPath, sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddHostedService<ParentProcessWatcher>();

        // Looks for newer releases on GitHub; the repository is Updates:Repository (see UpdateOptions).
        builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));
        builder.Services.AddSingleton<UpdateChecker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());
        builder.Services.AddTransient<AppSettingsForm>();
        builder.Services.AddScoped<OnboardingState>();

        builder.Services.Configure<SchedulerServiceOptions>(builder.Configuration.GetSection(SchedulerServiceOptions.SectionName));
        RootPaths(builder.Services, dataDirectory, settingsFile);

        // Media sources, outputs and voices are plugins, built in or installed. Each active one registers its
        // own services here; disabled ones register nothing.
        // The host's own HTTP client factory (App settings tests Jellyfin with it) mustn't depend on a plugin adding one.
        builder.Services.AddHttpClient();

        // The Channels page's preview: ErsatzTV takes up to 30 seconds to have a channel ready before it answers.
        builder.Services.AddHttpClient(ErsatzTvPreviewProxy.ClientName, http => http.Timeout = TimeSpan.FromSeconds(45));

        // Jellyfin, ErsatzTV and local media options are bound by their plugins, which are loaded even while
        // switched off (binding them here too would bind every list in them twice).
        // Installed plugins live in a plugins folder beside the app, so updating the app keeps them.
        // From a checkout, every plugin in the repository is loaded from its build output (built first when stale),
        // and nothing is loaded from (or moved out of) the per-user data folder.
        string? sourceRoot = fromSource ? SourcePlugins.Root(contentRoot, AppContext.BaseDirectory) : null;
        PluginRegistry plugins = PluginLoader.Load(dataDirectory, builder.Configuration, BuiltInPlugins.All,
            PluginLoader.ResolveInstallDirectory(args, dataDirectory, fromSource, sourceRoot),
            sourceRoot is null ? null : SourcePlugins.Prepare(sourceRoot));
        PluginLoader.ConfigureServices(plugins, builder.Services, dataDirectory, builder.Configuration);
        PluginLoader.AddGeneratedEditorSections(plugins, builder.Services);
        builder.Services.AddSingleton(plugins);
        builder.Services.AddSingleton<PluginManager>();

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SchedulerConfigProvider>();
        builder.Services.AddSingleton<ILibraryScope, SchedulerConfigLibraryScope>();

        // Channels set to play around the clock are held open in ErsatzTV, and the viewer sources are told which they are.
        builder.Services.AddSingleton<SchedularrTV.Core.Publishing.AlwaysOnChannels>();
        builder.Services.AddSingleton<AlwaysOnKeeper>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AlwaysOnKeeper>());

        // What SchedularrTV, ErsatzTV and their helpers are costing the computer, for the Performance page.
        builder.Services.AddSingleton<SchedularrTV.Service.Performance.ResourceMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedularrTV.Service.Performance.ResourceMonitor>());

        // The guides in docs/, built into the app and rendered for the Documentation pages.
        builder.Services.AddSingleton<SchedularrTV.Service.Docs.DocsLibrary>();

        // Every running media source plugin's library merged into one, so the engine never knows there are several.
        // Asked for on each search, so a source switched off on the Plugins page drops out at once.
        Func<IServiceProvider, IReadOnlyList<IMediaLibrary>> mediaSources = PluginServiceGate.MediaSources(plugins, builder.Services);
        // Plugins' enrichers (Auto Chapters' detected markers, say) are applied to everything it returns.
        builder.Services.AddSingleton<IMediaLibrary>(sp => new CompositeMediaLibrary(() => mediaSources(sp), () => sp.GetServices<IMediaEnricher>()));

        builder.Services.AddSingleton<IPlaybackStateStore>(sp =>
        {
            SchedulerServiceOptions options = sp.GetRequiredService<IOptions<SchedulerServiceOptions>>().Value;
            return new JsonPlaybackStateStore(Path.GetFullPath(options.StatePath));
        });

        // Only the planner features of running plugins take part (Promos, Generated promos, Archive Follow), checked
        // before each build so switching one on or off applies to the next build.
        builder.Services.AddSingleton(sp => new ScheduleBuilder(
            sp.GetRequiredService<IMediaLibrary>(),
            sp.GetRequiredService<IPlaybackStateStore>(),
            sp.GetRequiredService<ILoggerFactory>(),
            () => [.. sp.GetServices<PlannerFeature>()])
        {
            // Viewer-aware scheduling reads the viewing history (see Stats below).
            ViewerPreference = sp.GetRequiredService<SchedularrTV.Service.Stats.ViewerPreferences>()
        });

        builder.Services.AddSingleton<ScheduleCoordinator>();
        builder.Services.AddSingleton<SchedularrTV.Plugins.Playout.IScheduleRepublisher, ScheduleRepublisher>();
        // Also resolvable on its own, so settings and plugin switches can ask for a rebuild.
        builder.Services.AddSingleton<ScheduleRebuildService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduleRebuildService>());

        // Viewing stats: who's watching each channel (Jellyfin, Plex, ErsatzTV direct), noted every minute.
        builder.Services.Configure<SchedularrTV.Service.Stats.StatsOptions>(builder.Configuration.GetSection(SchedularrTV.Service.Stats.StatsOptions.SectionName));
        builder.Services.AddSingleton(new SchedularrTV.Service.Stats.ViewingStore(Path.Combine(dataDirectory, "stats", "viewing.db")));
        builder.Services.AddSingleton<SchedularrTV.Service.Stats.ViewerPreferences>();
        builder.Services.AddSingleton<SchedularrTV.Service.Stats.AudienceReader>();
        // Plex can only say who's watching (it isn't a library source here); media server plugins add their own.
        builder.Services.AddSingleton<SchedularrTV.Service.Stats.PlexLiveViewers>();
        builder.Services.AddSingleton<ILiveViewerSource>(sp => sp.GetRequiredService<SchedularrTV.Service.Stats.PlexLiveViewers>());
        builder.Services.AddSingleton<SchedularrTV.Service.Stats.ViewingRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedularrTV.Service.Stats.ViewingRecorder>());
        builder.Services.AddHttpClient(SchedularrTV.Service.Stats.AudienceReader.ClientName, http => http.Timeout = TimeSpan.FromSeconds(10));

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        // Singleton, not scoped: a scoped editor is per-circuit, so refreshing the browser would
        // silently throw away unsaved edits. This is a single-user localhost tool, so one shared
        // working copy across tabs is what the user expects.
        builder.Services.AddSingleton<ConfigEditor>();
        builder.Services.AddSingleton<RotationResetter>();
        builder.Services.AddSingleton<LibraryBrowser>();
        builder.Services.AddHostedService<LibraryWarmup>();
        // Plugins say when their media sources change, so the library pages show it at once.
        builder.Services.AddSingleton<SchedularrTV.Plugins.IMediaLibraryNotifier>(sp => sp.GetRequiredService<LibraryBrowser>());
        builder.Services.AddSingleton<LogoStore>();
        // Installed fonts, for the font pickers of the live page channels (news, radio, guide).
        builder.Services.AddSingleton<SchedularrTV.Plugins.Editor.IFontCatalog, SystemFontCatalog>();
        builder.Services.AddSingleton<SchedularrTV.Service.Import.ChannelImporter>();

        // Language models from the writer plugins (Local AI) word voice-overs and captions; what they write is kept here.
        builder.Services.AddSingleton<SchedularrTV.Service.Writing.TextGeneratorCatalog>();
        builder.Services.AddSingleton(sp => new SchedularrTV.Service.Writing.AiTextWriter(
            sp.GetRequiredService<SchedularrTV.Service.Writing.TextGeneratorCatalog>(),
            Path.Combine(dataDirectory, "ai-text"),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SchedularrTV.Service.Writing.AiTextWriter>>()));

        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
            .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
            .AllowAnyHeader()
            .AllowAnyMethod()));

        configure?.Invoke(builder);

        // Plugins' contributions follow their switches from here on (everything is registered by now).
        PluginServiceGate.Gate(plugins, builder.Services);

        // Options follow settings.json as it's saved, for the host and every plugin alike.
        builder.Services.Replace(ServiceDescriptor.Singleton(typeof(IOptions<>), typeof(LiveOptions<>)));

        WebApplication app = builder.Build();

        // Artwork links follow whichever media sources are running (Jellyfin, local folders, library plugins).
        SchedularrTV.Service.Editor.ArtworkLinks.Sources = () => app.Services.GetServices<IArtworkSource>();

        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hardware").LogInformation(
            "Running on {Hardware}; {Profile} defaults ({Mode}).", SchedularrTV.Core.Platform.HardwareProfile.Detected.Summary,
            SchedularrTV.Core.Platform.HardwareProfile.IsLowPower ? "low-power" : "standard", SchedularrTV.Core.Platform.HardwareProfile.Mode);

        // The library pickers re-read what's available when a media source is switched or reconfigured.
        LibraryBrowser browser = app.Services.GetRequiredService<LibraryBrowser>();
        plugins.Changed += browser.Invalidate;
        app.Services.GetRequiredService<AppRuntime>().SettingsSaved += browser.Invalidate;

        app.UseCors();
        app.UseAntiforgery();

        // The manifest is named after this assembly, which ApplicationName above is pinned to, so the
        // default lookup works from either host. Unlike UseStaticFiles this also serves framework
        // assets such as blazor.web.js outside of the Development environment.
        app.MapStaticAssets();

        ServeLogos(app);

        app.MapSchedulerApi();
        app.MapErsatzTvPreview();
        MapPlugins(app, plugins, dataDirectory);
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies([.. plugins.PageAssemblies]);

        return app;
    }

    /// <summary>
    /// Whether this is a build inside a source checkout (dotnet run, an IDE, or the tray started from its own
    /// bin folder) rather than a published download: the solution file sits a few folders above it.
    /// </summary>
    internal static bool RunningFromSource(string contentRoot) => SourcePlugins.Root(contentRoot, AppContext.BaseDirectory) is not null;

    /// <summary>
    /// Relative paths in the options are resolved against the data folder rather than whatever the working
    /// directory happens to be, and bare tool names are looked up where desktop apps can't see PATH.
    /// </summary>
    private static void RootPaths(IServiceCollection services, string data, AppSettingsFile settingsFile)
    {
        services.PostConfigure<SchedulerServiceOptions>(options =>
        {
            options.ConfigPath = AppPaths.Rooted(data, options.ConfigPath);
            options.StatePath = AppPaths.Rooted(data, options.StatePath);
        });
        services.PostConfigure<FileSystemLibraryOptions>(options =>
        {
            options.CachePath = AppPaths.Rooted(data, options.CachePath);
            options.FfprobePath = ToolLocator.Resolve(options.FfprobePath, "ffprobe", data);

            // A list saved in the app replaces the configured one outright; merging by index would let
            // folders removed in the app reappear from appsettings or user secrets.
            // Read from the file each time the options are rebuilt (after every save), not once at startup.
            if (AppSettingsFile.Get(settingsFile.Load(), $"{FileSystemLibraryOptions.SectionName}:Folders") is System.Text.Json.Nodes.JsonArray folders)
            {
                options.Folders = ReadFolders(folders);
            }
        });
        services.PostConfigure<ErsatzTvOptions>(options =>
        {
            options.ConfigRoot = AppPaths.Rooted(data, options.ConfigRoot);
        });
    }

    /// <summary>
    /// The folders saved in the app, one by one: a folder this version can't read (a kind added by a newer version,
    /// such as music, when an older copy shares the data folder) is left out rather than failing them all, which
    /// would stop every channel from building.
    /// </summary>
    internal static List<MediaFolderOptions> ReadFolders(System.Text.Json.Nodes.JsonArray folders)
    {
        List<MediaFolderOptions> read = [];
        foreach (System.Text.Json.Nodes.JsonNode? folder in folders)
        {
            try
            {
                if (folder?.Deserialize<MediaFolderOptions>(AppSettingsJson.Options) is { } options)
                {
                    read.Add(options);
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return read;
    }

    /// <summary>Marks an endpoint as a plugin's, so it answers only while the plugin is switched on.</summary>
    private sealed record PluginEndpointOwner(string PluginId);

    /// <summary>
    /// Serves each loaded plugin's <c>wwwroot</c> at <c>/plugins/&lt;id&gt;</c> and maps its endpoints, all answering
    /// only while the plugin is switched on, and logs what happened to every plugin at startup (loading runs before
    /// logging exists).
    /// </summary>
    private static void MapPlugins(WebApplication app, PluginRegistry plugins, string dataDirectory)
    {
        // Routing runs first, so the chosen endpoint is known here: a switched-off plugin's endpoints are not found.
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<PluginEndpointOwner>() is { } owner && !plugins.IsActive(owner.PluginId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Plugins");
        foreach (PluginDescriptor plugin in plugins.Plugins)
        {
            if (!plugin.IsLoaded)
            {
                if (plugin.Status != PluginStatus.Disabled)
                {
                    logger.LogWarning("Plugin {Plugin} isn't loaded: {Problem}", plugin.Id, plugin.Problem);
                }

                continue;
            }

            logger.LogInformation("Plugin {Plugin} {Version} loaded ({Source}, {State}).", plugin.Id, plugin.Manifest.Version, plugin.Source,
                plugin.IsActive ? "on" : "off");
            string id = plugin.Id;
            if (plugin.Directory is { } directory && Path.Combine(directory, "wwwroot") is var wwwroot && Directory.Exists(wwwroot))
            {
                app.UseWhen(_ => plugins.IsActive(id), branch => branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(wwwroot),
                    RequestPath = $"/plugins/{id}"
                }));
            }

            if (plugin.Instance is IPluginEndpoints endpoints)
            {
                try
                {
                    RouteGroupBuilder group = app.MapGroup(string.Empty);
                    group.WithMetadata(new PluginEndpointOwner(id));
                    endpoints.MapEndpoints(group, new PluginContext(plugin, dataDirectory, app.Configuration, plugins.IsLoaded));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Plugin {Plugin} failed to add its endpoints.", plugin.Id);
                }
            }
        }
    }

    /// <summary>
    /// Publishes uploaded channel logos, which players fetch by URL from the M3U and guide.
    /// </summary>
    private static void ServeLogos(WebApplication app)
    {
        string root = app.Services.GetRequiredService<LogoStore>().Root;
        Directory.CreateDirectory(root);

        // Only image types are mapped, so anything else that reaches the folder is not served.
        FileExtensionContentTypeProvider images = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp"
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = LogoStore.RequestPath,
            ContentTypeProvider = images,
            ServeUnknownFileTypes = false
        });
    }
}
