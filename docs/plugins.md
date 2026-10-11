# Plugins

Plugins extend the scheduler without changes to the main code, adding new media sources, outputs, voices, pages and settings. This page covers installing and managing plugins, then how to write your own.

## What plugins can do

Plugins can add new media sources, outputs, voices, changes to what's sent to the playout engine, extra pages and settings. They're ordinary .NET assemblies loaded at startup.

### Built-in plugins

These come with the app:

- Jellyfin
- Local media folders
- ErsatzTV Next
- Promos
- Archive Follow (off until you switch it on)

### Optional plugins

These are installed separately:

- **Generated promos** (needs Promos), plus the **TikTok**, **MiniMax** and **Piper** voices for it.
- [Web page shows](web-channels.md): live pages such as WeatherStar and Weatherscan forecasts, and a clock.
- [IPTV passthrough](iptv.md): channels from an M3U playlist and its guide.
- [Streamer channels](streamers.md): Twitch and YouTube streamers, with chat and a countdown to the next stream.
- [Radio shows](radio.md): music with cover art, lyrics and a visualizer.
- [News shows](news.md): local news breaks from your feeds, with weather, markets, scores and more, optionally read out.
- [Overlays](overlays.md): screen bugs and lower thirds.
- [Local AI writer](local-ai.md): natural, AI-written voice-overs and captions.
- [TV Guide channel](tv-guide.md): scrolling listings in the style of the Prevue Channel or TV Guide Channel.
- [Credit squeeze](credit-squeeze.md): end credits shrunk beside what's on next.
- [Auto Chapters](auto-chapters.md): mid-roll break points found where programmes fade to black.

Each [release](https://github.com/CarsonKompon/tv_scheduler/releases) has a `SchedularrTV-<version>-all-plugins.zip` with all of them, and a `SchedularrTV-<version>-plugin-<id>.zip` for each one. Install them from the **Plugins** page.

> [!IMPORTANT]
> Generated promos is built against the scheduler itself, so install the one from the same release as your scheduler.

## Installing

There are two ways to install a plugin:

- **From the Plugins page:** choose a plugin's `.zip`, or a zip of several plugin folders (like the all-plugins zip) to install them all. This is only offered to a browser on the computer running the scheduler, because a plugin runs with the same access to that computer as the scheduler.
- **By hand:** unzip the plugin into its own folder in the app's `plugins` folder, for example `SchedularrTV\plugins\acme.screen-bug\` beside `SchedularrTV.exe`.

Either way, restart the scheduler (**Restart now** on the Plugins page) to load it.

### Updating and removing

- **To update**, install the newer zip, or replace the plugin's folder, and restart.
- **To remove** an installed plugin, remove it and restart.

## Switching plugins on and off

Every plugin has a switch on the **Plugins** page, and a gear for its settings when it has any.

Apart from installing, updating and removing, everything applies straight away:

- Switching a plugin on or off takes effect at once. Its media, outputs, voices, editor tabs, pages and background work come and go.
- Plugins that need it follow it, showing **Waiting** while it's off.
- Every channel is replanned.
- Saved plugin settings are in use as soon as they're saved.

A switched-off plugin's settings stay in the schedule (under `plugins:`, see [Per-channel settings](#per-channel-settings)) but are ignored, and its tabs leave the editor.

Every installed plugin is loaded at startup, switched on or not. That's what lets the switch work without a restart.

## Where plugins are stored

The `plugins` folder sits in the app's own folder: beside the tray app, or beside the server when it runs on its own. The **Plugins** page shows the folder in use.

This means updating SchedularrTV by unzipping a new version over the old one keeps your plugins. Their settings are in `settings.json` and their private files in `plugin-data/` in the data folder, so those survive too.

These cases use `<data folder>/plugins/` instead:

- the macOS app, where replacing `SchedularrTV.app` would replace everything inside it;
- an app folder the scheduler can't write to.

Other options:

- Start with `--plugins-dir <folder>` to use another folder.
- Plugins installed in `<data folder>/plugins/` by an earlier version are moved into the app's folder on the next start.
- Run from a source checkout, every plugin in the repository is loaded from its build output instead, and installed ones go in the checkout's `.plugins/` folder. See [Development](development.md#plugins-in-a-source-checkout).

## Writing a plugin

A plugin is a class library targeting `net10.0` that references `SchedularrTV.Plugins.Abstractions`, plus a `plugin.json`. See `samples/SchedularrTV.Plugins.Sample` for a complete example with settings, a page and a playout processor.

### Project setup

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

To package it:

1. Run `dotnet publish -c Release -o out/<id>`.
2. Zip the `out/<id>` folder.

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

#### Field notes

- **`apiVersion`**: the plugin loads when its major version matches the scheduler's and its minor version is no newer. The current version is shown in `PluginApi.Version`.
- **`dependencies`**:
  - A required dependency must be installed, enabled, working and at least `minVersion`, or the plugin doesn't load.
  - An optional one is used when present; check with `context.IsLoaded(id)`.
  - Dependencies register before the plugins that need them. Loops are refused.
- **`settings`**: types are `Text`, `Secret`, `Number`, `Boolean`, `File`, `Folder` and `Choice` (with `options`). Values are saved in `settings.json` under `Plugins:<id>` and read through `context.Configuration`.
- **`channelSettings`, `blockSettings`, `scheduleSettings`** (Plugin API 1.1): generated editor forms for the plugin's YAML sections (see [Per-channel settings](#per-channel-settings)), with the same field types as `settings`. Keys are written as given, so use snake_case. Unknown keys set by hand are kept.
- **`settingsComponent`**: the full type name of a component in the entry assembly. It's shown in the plugin's settings dialog below the `settings` fields (while the plugin is loaded), for things fields can't do, like downloads. It gets a `[Parameter] PluginSettingsEditor Editor` to read and set values; the Plugins page saves them.
- **`pages`**: routable components in the entry assembly, each under `/plugins/<id>`. They're added to the sidebar and use the app's layout and styles.
- **`lowPowerNotice`** (Plugin API 1.8): shown on the plugin's card and in its settings on a Raspberry Pi or other low-power computer: what's heavy there and how to keep it light.
- **`lowPowerNote`, `lowPowerPlaceholder`, `lowPowerDefault`** (on a setting, Plugin API 1.8): on a low-power computer, a warning under the field, its placeholder, and the value a `default` field starts at, for when the plugin's own default differs there.

### What a plugin can add

Everything goes through `ConfigureServices`, so a plugin can register any service, options or hosted service. These helpers fill the scheduler's extension points:

| Extension point | Register with | Interface |
| --- | --- | --- |
| Media source (Plex, Emby, a web API…) | `services.AddMediaSource<T>(key)` | `IMediaLibrary` |
| A media source's name, set-up state, loading progress, server activity and libraries (shown on the dashboard, library pages, setup checks and **Settings → Libraries**) | `services.AddMediaSourceInfo<T>()` | `IMediaSourceInfo` |
| A media source's pictures (posters, stills, fanart) for the ids it owns | `services.AddArtworkSource<T>()` | `IArtworkSource` |
| A show's extras, offered as intros and outros | `services.AddExtrasSource<T>()` | `IExtrasSource` |
| Where items' end credits start (for Credit squeeze), beyond chapters named Credits | `services.AddCreditsSource<T>()` | `ICreditsSource` |
| Who's watching a media server's live TV (for channel stats) | `services.AddLiveViewerSource<T>()` | `ILiveViewerSource` |
| A music library radio channels and background music can play from | `services.AddMusicLibrary<T>()` | `IMusicLibrary` |
| Extra details on library items (chapter markers found in the file, say) | `services.AddMediaEnricher<T>()` (API 1.2) | `IMediaEnricher` |
| Output (another playout engine, M3U files…) | `services.AddPlayoutOutput<T>()` | `IPlayoutPublisher` |
| Voice for generated promos | `services.AddSpeechProvider<T>()` | `ITextToSpeechProvider` |
| Language model that writes voice-overs and captions (see [Local AI writer](local-ai.md)) | `services.AddTextGenerator<T>()` (API 1.5) | `ITextGenerator` |
| Changes to what's sent to outputs | `services.AddPlayoutProcessor<T>()` | `IPlayoutProcessor` |
| Whole channels with their own schedule, published alongside the scheduler's (IPTV, say) | `services.AddExternalChannels<T>()` (API 1.3) | `IExternalChannelSource` |
| A promo made now from a channel's generated promo templates (for a credit squeeze's panel, say) | Ask for `IPromoMaker` with `GetService` (API 1.7) | `IPromoMaker` |
| Who's watching which channel | `services.AddChannelViewers<T>()` | `IChannelViewers` |
| Tell the library pages a media source changed (a show added or renamed), so they update at once | Inject `IMediaLibraryNotifier` (API 1.4) and call `LibraryChanged()` | |
| HTTP endpoints | Implement `IPluginEndpoints` on the plugin class | |

Notes on some of these:

- **External channels:** inject `IScheduleRepublisher` to publish again when they change. They're listed on the Channels page and dashboard. Implement `EditPath` (API 1.6) to give them an **Edit** button that opens your page.
- **`IPromoMaker`:** it's there when the Generated Promos plugin is.

### Plugin context

`IPluginContext` gives the plugin:

- its manifest;
- its own settings (`Configuration`);
- the whole app configuration (`AppConfiguration`);
- its folder;
- a private writable `DataDirectory` that survives updates;
- `IsLoaded(id)` for optional dependencies.

### Library sources

Nothing in the scheduler depends on a particular library. Jellyfin and local folders are built-in plugins that use the same contracts (in `SchedularrTV.Core.Media`) a library plugin would, so a new source works everywhere they do as soon as it implements them.

`IMediaLibrary` alone is enough for its shows and movies to be scheduled. Each of the others is optional and adds one thing:

- **`IMediaSourceInfo`**: its name, `IsConfigured`, `Loading` progress (with `LoadingChanged`), `GetActivityAsync` (what its own server is busy with, such as a library scan) and `GetLibrariesAsync` (libraries that can be left out by name; they're read back through `ILibraryScope`). Usually the same class as the `IMediaLibrary`.
- **`IArtworkSource`**: pictures for the ids it `Owns`. Artwork links and the `/api/library/artwork` endpoint ask whichever running source owns an id.
- **`IExtrasSource`**: a show's extras for the Intros/outros dialog.
- **`ICreditsSource`**: where credits start, asked in the background by Credit squeeze for items it owns.
- **`ILiveViewerSource`**: live TV sessions, matched to channels by number, then name, for channel stats.
- **`IMusicLibrary`**: songs, playlists, genres, artists, lyrics, a stream ffmpeg can read (a path, or an address with headers) and covers, for radio channels and [background music](background-music.md). Its `Id` is saved with each station and each piece of background music that uses it.

Throw `MediaSourceConfigurationException` for a problem the user has to fix (a missing address or key): the scheduler logs its message without a stack trace.

Every one of these lists follows the plugin's switch, so a source that's switched off drops out of all of them at once.

### Background music in your plugin

Anything that plays music under something should offer every music library, not just a folder. The pieces are in `SchedularrTV.Core.Media` and `SchedularrTV.Plugins`:

- **`BackgroundMusic`**: the setting to save (a folder, or a library's every song, playlist, genres or artists). Pass it through `BackgroundMusic.Tidy(music, oldFolder)` when loading, which also carries over a plain folder setting from an earlier version.
- **`<MusicPicker Value="x.Music" ValueChanged="v => x.Music = v" />`**: the picker every built-in page uses (none, a folder, or any installed library, with its playlists, genres and artists).
- **`MusicBed.Start(ffmpeg, music, libraries, logger)`**: endless 48 kHz stereo PCM of the songs, shuffled and looped, each decoded by its own ffmpeg. Read it 20 ms at a time to mix it (`Read`), or `Serve` it at a local `tcp://` address for an ffmpeg to read with `-f s16le -ar 48000 -ac 2 -i`.
- **`MusicFiles.PickFileAsync` / `LocalFileAsync`**: one song as a file (copied once into a cache folder when the library serves it rather than keeps it as a file), for renders that need one.
- **Live page channels** (`SchedularrTV.LiveCapture`) take it as `LiveStreamSpec.Music`; the hub hands the stream the installed libraries itself.

### Playout processors

An `IPlayoutProcessor` receives each channel's schedule just before it's published and returns the version the outputs get. It can replace or insert entries in breaks, or attach `Annotations` to entries.

Processors run in `Order`, then name order. Rules:

- **Be a pure function of the input.** Processors run on the schedule as planned every time it's published, never on their own earlier output, and what they return isn't fed back into planning. The same airing must come out the same way on every publish.
- **Keep each entry's start and duration**, because outputs expect a gap-free timeline. To put something new in a break, replace break entries with items of the same total length.
- **Errors are contained.** A processor that throws is skipped for that channel and logged; the publish goes ahead.

Annotations are string pairs keyed `<plugin id>.<name>`. Outputs ignore keys they don't know, so a screen-bug plugin can tag programmes with an image, and an output (or a later version of the ErsatzTV plugin) that supports overlays can draw it.

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

To read them:

- **In a processor**, `context.Settings<MySettings>("acme.screen-bug", entry)` binds the block's section, then the channel's, then the global one, with the same snake_case naming as the rest of the file.
- **Outside a processor**, `PluginSettings.Read<T>(sections, id)` does the same for any `Plugins` dictionary.

The built-in features use the same sections:

- `promos`: `base_tags` and `override` on a channel or block; the promo settings themselves under `settings`.
- `generated-promos`: templates on a channel or block; its other settings are the plugin's own, on the Plugins page.
- `archive-follow`: on a channel; `follow_only` on a block.

They're read into their own typed settings, so they don't show up in `ChannelConfig.Plugins`.

### Editor sections

Fields declared in `channelSettings`, `blockSettings` or `scheduleSettings` get a generated form. For anything richer, register your own component instead:

```csharp
services.AddChannelEditorTab<ScreenBugTab>(context, "Screen bug", order: 60, icon: "image");
services.AddBlockEditorSection<ScreenBugBlock>(context, "Screen bug");
services.AddScheduleSettingsSection<ScreenBugDefaults>(context, "Screen bug");
```

The component takes a `[Parameter] PluginEditorContext Context`, which provides:

- the whole `Config`;
- the `Channel` and `Block` being edited (null where they don't apply);
- `Read<T>()` and `Write(section)` for the plugin's own section at that level;
- `NotifyChanged()` to mark the schedule as edited.

The scheduler's channel tabs are ordered General 0, Blocks 10, Follow 20, Breaks 30, Promos 40 and Generated Promos 50.

### Low-power computers (Plugin API 1.8)

- `SchedularrTV.Core.Platform.HardwareProfile.IsLowPower` says whether the app is using its Raspberry Pi defaults (detected, or chosen under **App settings → Hardware and video encoding**). Read it each time, since it can change while running.
- Use `HardwareProfile.Pick(standard, lowPower)` for defaults, never to override a value the user set.
- In your own pages, `<LowPowerNotice Title="…" Advice="…" />` (namespace `SchedularrTV.Plugins`) shows a warning only on such a computer. `Inline="true"` makes it a hint beside a field.

### Video encoding

Plugins that encode video should take their encoder from the app's settings:

- `LiveEncodingSettings.Current` for live streams.
- `RenderEncodingSettings.Current(preset)` for clips made ahead of time, whose `VideoEncoderChoice.VideoArguments(...)` gives the `-c:v …` arguments for the chosen encoder.

### Static files

Files in a `wwwroot` folder inside the plugin are served at `/plugins/<id>/`.

> [!NOTE]
> Only the plugin's `wwwroot` folder is served. Razor class library static web assets such as `_content/...` and scoped CSS aren't.

### Loading and isolation

Each plugin loads in its own assembly load context.

- **Shared assemblies:** assemblies the scheduler already has (the plugin API, `SchedularrTV.Core`, ASP.NET Core, `Microsoft.Extensions.*`, YamlDotNet, SkiaSharp…) always come from the scheduler, matched by name, so shared types match. A plugin built against a newer version of one of these still gets the scheduler's.
- **Dependencies:** a plugin also sees the assemblies of the plugins it declares as dependencies, so it can use their types.
- **Private assemblies:** anything else it brings is private to it, so two plugins can use different versions of the same library.
- **No file locks:** plugin files aren't locked while the scheduler runs (they're read into memory), so `Assembly.Location` is empty inside a plugin. Use `context.PluginDirectory` instead.

A plugin that fails to load or throws in `ConfigureServices` is marked failed on the Plugins page with the reason, and none of its registrations are kept.

## Roadmap

Plugin API 1.7 covers sources, media enrichers, outputs, voices, text writers, publish-time playout changes, external channels, promos made on request, settings, pages, endpoints and schedule editor sections.

Promos, generated promos and Archive Follow use extension points inside the planner that are internal for now, so they aren't available to installed plugins yet.

Planned next:

- Opening those planner extension points to installed plugins once they've settled.
- A NuGet package for the plugin API.
