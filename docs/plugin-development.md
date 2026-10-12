# Plugin development

A plugin is a **.NET 10** class library with an **`IPlugin`** entry point and **`plugin.json`** manifest. Start from the [sample plugin](../samples/SchedularrTV.Plugins.Sample), which demonstrates settings, a Razor page, and a playout processor.

## Create the project

1. Create a class library targeting **`net10.0`**.
2. Reference **`SchedularrTV.Plugins.Abstractions`** with **`Private="false"`**.
3. Enable **`EnableDynamicLoading`**.
4. Copy **`plugin.json`** to build and publish output.
5. Implement **`IPlugin.ConfigureServices`**.

Use **`Microsoft.NET.Sdk.Razor`** for components and pages, otherwise **`Microsoft.NET.Sdk`**:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SchedularrTV.Plugins.Abstractions/SchedularrTV.Plugins.Abstractions.csproj"
                      Private="false" />
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest"
          CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

The reference path above assumes a project under **`samples/<project>`**. Register extension services in **`ConfigureServices`** using helpers from **`SchedularrTV.Plugins`**.

## Define the manifest

```json
{
  "id": "acme.screen-bug",
  "name": "Screen bug",
  "version": "1.0.0",
  "apiVersion": "1.8",
  "entryAssembly": "Acme.ScreenBug.dll",
  "enabledByDefault": true,
  "dependencies": [],
  "settings": [
    { "key": "Opacity", "label": "Opacity (%)", "type": "Number", "default": "80" }
  ],
  "channelSettings": [
    { "key": "image", "label": "Logo image", "type": "File" }
  ],
  "pages": [
    { "path": "/plugins/acme.screen-bug", "title": "Screen bug", "icon": "image" }
  ]
}
```

| Field | Contract |
| --- | --- |
| **`id`** | Stable lowercase identifier using letters, digits, dots, and dashes |
| **`apiVersion`** | Same major as the host and no newer minor; current API is **1.8** |
| **`entryAssembly`** | Assembly filename in the plugin folder |
| **`entryType`** | Required when the entry assembly has multiple `IPlugin` implementations |
| **`dependencies`** | Plugin IDs with optional `minVersion` and `optional` fields |
| **`settings`** | Application settings saved under `Plugins:<id>` |
| **`channelSettings`**, **`blockSettings`**, **`scheduleSettings`** | Generated forms for YAML plugin sections |
| **`settingsComponent`** | Full type name of a custom settings component |
| **`pages`** | Routable components under `/plugins/<id>` |
| **`lowPowerNotice`** | Plugin-wide low-power guidance |

Field types are **Text**, **Secret**, **Number**, **Boolean**, **File**, **Folder**, and **Choice**. Choice fields use **`options`**; YAML section keys are written as declared, so use **snake_case**.

Required dependencies must be available and meet minimum versions. Dependencies register first; cycles are rejected. Use **`context.IsLoaded(id)`** before using an optional dependency.

## Register extension points

| Capability | Registration or interface |
| --- | --- |
| Media source | **`AddMediaSource<T>(key)`** · `IMediaLibrary` |
| Source setup, activity, library lists | **`AddMediaSourceInfo<T>()`** · `IMediaSourceInfo` |
| Artwork | **`AddArtworkSource<T>()`** · `IArtworkSource` |
| Intros and outros | **`AddExtrasSource<T>()`** · `IExtrasSource` |
| Credit boundaries | **`AddCreditsSource<T>()`** · `ICreditsSource` |
| Media-server viewing sessions | **`AddLiveViewerSource<T>()`** · `ILiveViewerSource` |
| Music | **`AddMusicLibrary<T>()`** · `IMusicLibrary` |
| Additional item metadata | **`AddMediaEnricher<T>()`** · `IMediaEnricher` |
| Output | **`AddPlayoutOutput<T>()`** · `IPlayoutPublisher` |
| Speech | **`AddSpeechProvider<T>()`** · `ITextToSpeechProvider` |
| Text generation | **`AddTextGenerator<T>()`** · `ITextGenerator` |
| Publish-time processing | **`AddPlayoutProcessor<T>()`** · `IPlayoutProcessor` |
| Complete external channels | **`AddExternalChannels<T>()`** · `IExternalChannelSource` |
| Watched-channel detection | **`AddChannelViewers<T>()`** · `IChannelViewers` |
| HTTP endpoints | **`IPluginEndpoints`** on the entry class |

Inject **`IMediaLibraryNotifier`** and call **`LibraryChanged()`** after source changes. External channels can request **`IScheduleRepublisher`** and provide **`EditPath`** for their editor link.

**`IPromoMaker`** is available when Generated promos is enabled. Planner-internal extension points used by built-in scheduling features are not a general installed-plugin API.

## Read settings and paths

| Context member | Purpose |
| --- | --- |
| **`Configuration`** | Plugin application settings |
| **`AppConfiguration`** | Whole application configuration |
| **`PluginDirectory`** | Installed files |
| **`DataDirectory`** | Writable data surviving plugin updates |
| **`IsLoaded(id)`** | Optional-dependency check |

Inside a playout processor, **`context.Settings<T>(id, entry)`** resolves settings at block, channel, then global scope. **`PluginSettings.Read<T>(sections, id)`** binds a supplied section dictionary outside a processor.

```yaml
settings:
  plugins:
    acme.screen-bug:
      image: /media/logos/default.png
channels:
  - id: movies
    name: Movies
    number: 1
    base_rotation:
      query: "kind == 'Movie'"
    plugins:
      acme.screen-bug:
        image: /media/logos/movies.png
```

## Add editor components

```csharp
services.AddChannelEditorTab<ScreenBugTab>(context, "Screen bug", order: 60, icon: "image");
services.AddBlockEditorSection<ScreenBugBlock>(context, "Screen bug");
services.AddScheduleSettingsSection<ScreenBugDefaults>(context, "Screen bug");
```

Each component accepts **`[Parameter] PluginEditorContext Context`**. Use **`Read<T>()`**, **`Write(section)`**, and **`NotifyChanged()`** for the plugin's section; **`Config`**, **`Channel`**, and **`Block`** expose the editing scope.

A custom settings-dialog component accepts **`[Parameter] PluginSettingsEditor Editor`**. The Plugins page owns saving its values.

## Process playouts

Processors run in **`Order`**, then name order, immediately before publication. Their output does not feed back into schedule planning.

- Preserve every entry's **start and duration**
- Replace break content with the **same total duration**
- Produce identical output for the **same airing**
- Namespace annotations as **`<plugin-id>.<name>`**

A failing processor is logged and skipped for that channel; publication continues. Outputs ignore unknown annotations, so an annotation only affects video when an output or later processor understands it.

## Reuse music and encoding services

| Service | Use |
| --- | --- |
| **`BackgroundMusic`**, **`MusicPicker`** | Shared folder/library music selection |
| **`BackgroundMusic.Tidy`** | Migration from older folder-only settings |
| **`MusicBed.Start`** | Continuous 48 kHz stereo PCM for live mixing |
| **`MusicFiles.PickFileAsync`**, **`LocalFileAsync`** | Cached local song files for renders |
| **`LiveStreamSpec.Music`** | Music for shared live-page capture |
| **`LiveEncodingSettings.Current`** | Live video encoder settings |
| **`RenderEncodingSettings.Current(preset)`** | Rendered-clip encoder settings |
| **`HardwareProfile.IsLowPower`**, **`Pick`** | Hardware-sensitive defaults |

Read hardware defaults at use time and preserve explicit user choices. Manifest fields support **`lowPowerNote`**, **`lowPowerPlaceholder`**, and **`lowPowerDefault`**.

## Serve files and load assemblies

Files under the installed **`wwwroot/`** are served at **`/plugins/<id>/`**. Razor class-library **`_content/`** assets and scoped CSS are not served through this mechanism.

Each plugin has an assembly load context. Host assemblies are shared by name, declared dependencies expose their assemblies, and other bundled libraries remain private.

Assemblies load from memory without locking their files. **`Assembly.Location`** is empty; use **`context.PluginDirectory`**.

## Package and test

1. Publish the project with **`dotnet publish -c Release -o out/<id>`**.
2. Zip the output with **`plugin.json`** and assemblies together.
3. Install the ZIP through **Plugins** and restart.
4. Verify settings, pages, enable/disable behavior, and output on a test channel.

See [Development](development.md#plugins-in-a-source-checkout) for automatic source-checkout builds.
