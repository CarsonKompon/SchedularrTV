# Plugins

Plugins extend the scheduler without changes to the main code: new media sources, outputs, voices,
changes to what's sent to the playout engine, extra pages and settings. They're ordinary .NET assemblies
loaded at startup.

Jellyfin, local media folders, ErsatzTV Next, Promos and Archive Follow (off until you switch it on) are
built-in plugins. Generated promos (which needs Promos), the TikTok, MiniMax and Piper voices for it, and
[Web page channels](web-channels.md) (live pages such as WeatherStar forecasts, a clock and weather radar), [IPTV passthrough](iptv.md) (channels from an
M3U playlist and its guide), [Streamer channels](streamers.md) (Twitch and YouTube streamers, with chat and a countdown to the next
stream), [Radio channels](radio.md) (music with cover art, lyrics and a visualizer), [Overlays](overlays.md)
(screen bugs and lower thirds), the [Local AI writer](local-ai.md) (natural, AI-written voice-overs and captions), the [TV Guide channel](tv-guide.md)
(scrolling listings in the style of the Prevue Channel or TV Guide Channel), [Credit squeeze](credit-squeeze.md) (end credits
shrunk beside what's on next) and [Auto Chapters](auto-chapters.md)
(mid-roll break points found where programmes fade to black) are optional: each release has a
`SchedularrTV-<version>-all-plugins.zip` with all of them (and a
`SchedularrTV-<version>-plugin-<id>.zip` for each), installed from the **Plugins** page. Generated promos is built
against the scheduler itself, so install the one from the same release as your scheduler.

Every plugin has a switch on the **Plugins** page, and a gear for its settings when it has any. A
switched-off feature's settings stay in the schedule (under `plugins:`, see below) but are ignored, and
its tabs leave the editor.

## Installing

- **From the Plugins page:** choose a plugin's `.zip`, or a zip of several plugin folders (like the extra
  plugins zip) to install them all. This is only offered to a browser on the computer running the
  scheduler, because a plugin runs with the same access to that computer as the scheduler.
- **By hand:** unzip the plugin into its own folder in the app's `plugins` folder, for example
  `SchedularrTV\plugins\acme.screen-bug\` beside `SchedularrTV.exe`.

Either way, restart the scheduler (**Restart now** on the Plugins page) to load it. Updating works the
same way: install the newer zip, or replace the plugin's folder, and restart. Removing an installed plugin
also takes a restart.

Everything else applies straight away. Switching a plugin on or off takes effect at once: its media,
outputs, voices, editor tabs, pages and background work come and go, plugins that need it follow it
(showing **Waiting** while it's off), and every channel is replanned. Saved plugin settings are in use as
soon as they're saved. Every installed plugin is loaded at startup, switched on or not, which is what lets
the switch work without a restart.

The `plugins` folder sits in the app's own folder (beside the tray app; beside the server when it runs on
its own), so updating SchedularrTV by unzipping a new version over the old one keeps your plugins. Their
settings are in `settings.json` and their private files in `plugin-data/` in the data folder, so those
survive too. The Plugins page shows the folder in use. Exceptions, which use `<data folder>/plugins/`
instead:

- the macOS app, where replacing `SchedularrTV.app` would replace everything inside it;
- an app folder the scheduler can't write to.

Run from a source checkout, every plugin in the repository is loaded from its build output instead (see
[development.md](development.md)), and installed ones go in the checkout's `.plugins/` folder.

Start with `--plugins-dir <folder>` to use another folder. Plugins installed in `<data folder>/plugins/` by
an earlier version are moved into the app's folder on the next start.

## Writing a plugin

A plugin is a class library targeting `net10.0` that references `SchedularrTV.Plugins.Abstractions`, plus a
`plugin.json`. See `samples/SchedularrTV.Plugins.Sample` for a complete example with settings, a page and a
playout processor.

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">   <!-- Microsoft.NET.Sdk if it has no pages or components -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="path/to/SchedularrTV.Plugins.Abstractions.csproj" Private="false" />
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

```csharp
public sealed class ScreenBugPlugin : IPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context)
    {
        services.AddPlayoutProcessor<ScreenBugProcessor>();
    }
}
```

Package it with `dotnet publish -c Release -o out/<id>` and zip the `out/<id>` folder.

### plugin.json

```jsonc
{
  "id": "acme.screen-bug",          // lowercase letters, digits, dots and dashes; never change it
  "name": "Screen bug",
  "version": "1.2.0",
  "description": "Puts the channel logo in the corner.",
  "author": "Acme",
  "website": "https://example.com",
  "apiVersion": "1.0",              // the plugin API version it was built against
  "entryAssembly": "Acme.ScreenBug.dll",
  "entryType": null,                // only needed if the assembly has more than one IPlugin class
  "enabledByDefault": true,
  "dependencies": [
    { "id": "acme.promos", "minVersion": "1.1.0" },
    { "id": "tts-minimax", "optional": true }
  ],
  "settings": [
    { "key": "Opacity", "label": "Opacity (%)", "type": "Number", "default": "80" },
    { "key": "ApiKey", "label": "API key", "type": "Secret" }
  ],
  "channelSettings": [              // fields of plugins.<id> on each channel: a tab on the channel page
    { "key": "image", "label": "Logo image", "type": "File" },
    { "key": "corner", "label": "Corner", "type": "Choice", "options": ["top-left", "top-right"] }
  ],
  "blockSettings": [                // the same per block, in the block editor
    { "key": "image", "label": "Logo image", "type": "File" }
  ],
  "scheduleSettings": [],           // the same under settings:, on the Schedule settings page
  "settingsComponent": null,        // a Razor component shown below the generated settings fields
  "pages": [
    { "path": "/plugins/acme.screen-bug", "title": "Screen bug", "icon": "image" }
  ]
}
```

| Field | Notes |
| --- | --- |
| `apiVersion` | Loads when its major version matches the scheduler's and its minor version is no newer. The current version is shown in `PluginApi.Version`. |
| `dependencies` | A required dependency must be installed, enabled, working and at least `minVersion`, or the plugin doesn't load. An optional one is used when present; check with `context.IsLoaded(id)`. Dependencies register before the plugins that need them. Loops are refused. |
| `settings` | Types: `Text`, `Secret`, `Number`, `Boolean`, `File`, `Folder`, `Choice` (with `options`). Values are saved in `settings.json` under `Plugins:<id>` and read through `context.Configuration`. |
| `channelSettings`, `blockSettings`, `scheduleSettings` | Plugin API 1.1. Generated editor forms for the plugin's YAML sections (below), with the same field types as `settings`. Keys are written as given, so use snake_case. Unknown keys set by hand are kept. |
| `settingsComponent` | Full type name of a component in the entry assembly, shown in the plugin's settings dialog below the `settings` fields (while the plugin is loaded), for things fields can't do, like downloads. It gets a `[Parameter] PluginSettingsEditor Editor` to read and set values; the Plugins page saves them. |
| `pages` | Routable components in the entry assembly, each under `/plugins/<id>`. They're added to the sidebar and use the app's layout and styles. |

Files in a `wwwroot` folder inside the plugin are served at `/plugins/<id>/`.

### What a plugin can add

Everything goes through `ConfigureServices`, so a plugin can register any service, options or hosted
service. These helpers fill the scheduler's extension points:

| Extension point | Register with | Interface |
| --- | --- | --- |
| Media source (Plex, Emby, a web API…) | `services.AddMediaSource<T>(key)` | `IMediaLibrary` |
| Extra details on library items (chapter markers found in the file, say) | `services.AddMediaEnricher<T>()` (API 1.2) | `IMediaEnricher` |
| Output (another playout engine, M3U files…) | `services.AddPlayoutOutput<T>()` | `IPlayoutPublisher` |
| Voice for generated promos | `services.AddSpeechProvider<T>()` | `ITextToSpeechProvider` |
| Language model that writes voice-overs and captions (see [local-ai.md](local-ai.md)) | `services.AddTextGenerator<T>()` (API 1.5) | `ITextGenerator` |
| Changes to what's sent to outputs | `services.AddPlayoutProcessor<T>()` | `IPlayoutProcessor` |
| Whole channels with their own schedule, published alongside the scheduler's (IPTV, say) | `services.AddExternalChannels<T>()` (API 1.3); inject `IScheduleRepublisher` to publish again when they change. They're listed on the Channels page and dashboard; implement `EditPath` (API 1.6) to give them an Edit button that opens your page | `IExternalChannelSource` |
| A promo made now from a channel's generated promo templates (for a credit squeeze's panel, say) | Ask for `IPromoMaker` with `GetService` (API 1.7); it's there when the Generated Promos plugin is | `IPromoMaker` |
| Who's watching which channel | `services.AddChannelViewers<T>()` | `IChannelViewers` |
| Tell the library pages a media source changed (a show added or renamed), so they update at once | inject `IMediaLibraryNotifier` (API 1.4) and call `LibraryChanged()` | |
| HTTP endpoints | implement `IPluginEndpoints` on the plugin class | |

`IPluginContext` gives the plugin its manifest, its own settings (`Configuration`), the whole app
configuration (`AppConfiguration`), its folder, a private writable `DataDirectory` that survives updates,
and `IsLoaded(id)` for optional dependencies.

### Playout processors

An `IPlayoutProcessor` receives each channel's schedule just before it's published and returns the
version the outputs get: it can replace or insert entries in breaks, or attach `Annotations` to entries.
Processors run in `Order`, then name order. Rules:

- They run on the schedule as planned every time it's published, never on their own earlier output, and
  what they return isn't fed back into planning. Be a pure function of the input: the same airing must
  come out the same way on every publish.
- Keep each entry's start and duration (outputs expect a gap-free timeline). To put something new in a
  break, replace break entries with items of the same total length.
- One that throws is skipped for that channel and logged; the publish goes ahead.

Annotations are string pairs keyed `<plugin id>.<name>`. Outputs ignore keys they don't know, so a
screen-bug plugin can tag programmes with an image and an output (or a later version of the ErsatzTV
plugin) that supports overlays can draw it.

### Per-channel settings

Plugins can have their own sections in the schedule YAML, globally, per channel and per block:

```yaml
settings:
  plugins:
    acme.screen-bug: { image: D:\Logos\default.png }
channels:
  - id: ch1
    plugins:
      acme.screen-bug: { image: D:\Logos\ch1.png, corner: top-right }
    blocks:
      - name: Late Night
        plugins:
          acme.screen-bug: { image: D:\Logos\late.png }
```

In a processor, `context.Settings<MySettings>("acme.screen-bug", entry)` binds the block's section, then
the channel's, then the global one, with the same snake_case naming as the rest of the file. Outside a
processor, `PluginSettings.Read<T>(sections, id)` does the same for any `Plugins` dictionary.

The built-in features use the same sections: `promos` (`base_tags` and `override` on a channel or block, the
promo settings themselves under `settings`), `generated-promos` (templates on a channel or block; its other settings are the
plugin's own, on the Plugins page) and `archive-follow` (on a
channel; `follow_only` on a block). They're read into their own typed settings, so they don't show up in
`ChannelConfig.Plugins`.

### Editor sections

Fields declared in `channelSettings`, `blockSettings` or `scheduleSettings` get a generated form. For
anything richer, register your own component instead:

```csharp
services.AddChannelEditorTab<ScreenBugTab>(context, "Screen bug", order: 60, icon: "image");
services.AddBlockEditorSection<ScreenBugBlock>(context, "Screen bug");
services.AddScheduleSettingsSection<ScreenBugDefaults>(context, "Screen bug");
```

The component takes a `[Parameter] PluginEditorContext Context`: the whole `Config`, the `Channel` and
`Block` being edited (null where they don't apply), `Read<T>()` and `Write(section)` for the plugin's own
section at that level, and `NotifyChanged()` to mark the schedule as edited. The scheduler's channel tabs
are ordered General 0, Blocks 10, Follow 20, Breaks 30, Promos 40 and Generated Promos 50.

### Loading and isolation

Each plugin loads in its own assembly load context. Assemblies the scheduler already has (the plugin
API, `SchedularrTV.Core`, ASP.NET Core, `Microsoft.Extensions.*`, YamlDotNet, SkiaSharp…) always come from
the scheduler, matched by name, so shared types match; a plugin built against a newer version of one of
these still gets the scheduler's. A plugin also sees the assemblies of the plugins it declares as
dependencies, so it can use their types. Anything else it brings is private to it, so two plugins can
use different versions of the same library. Plugin files aren't locked while the scheduler runs (they're
read into memory), so `Assembly.Location` is empty inside a plugin; use `context.PluginDirectory`.

Only the plugin's `wwwroot` folder is served (at `/plugins/<id>/`); Razor class library static web
assets such as `_content/...` and scoped CSS aren't.

A plugin that fails to load or throws in `ConfigureServices` is marked failed on the Plugins page with the
reason, and none of its registrations are kept.

## Roadmap

Plugin API 1.7 covers sources, media enrichers, outputs, voices, text writers, publish-time playout changes, external channels, promos made on request, settings, pages, endpoints
and schedule editor sections. Promos, generated promos and Archive Follow use extension points inside the
planner that are internal for now (see [engine-extension-points.md](engine-extension-points.md)).
Planned next:

- Opening those planner extension points to installed plugins once they've settled.
- A NuGet package for the plugin API.
