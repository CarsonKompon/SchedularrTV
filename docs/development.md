# Development

SchedularrTV targets **.NET 10** with an ASP.NET Core/Blazor service and an Avalonia tray launcher. The service owns scheduling, the Web UI, plugins, and the REST API.

## Run from source

Install the **.NET 10 SDK**, **ffmpeg**, and **ffprobe**, then run from the repository root:

```sh
dotnet run --project src/SchedularrTV.Service
```

For the tray launcher, use **`dotnet run --project src/SchedularrTV.Tray`**. Debug builds copy the service beside the tray; the default UI address is **http://localhost:5252**.

A folder containing **`config/schedule.yaml`** can serve as a checkout-local data folder. Use **`--data-dir`** for an explicit location.

## Plugins in a source checkout

Debug plugin builds copy output into the ignored **`.plugins/`** directory. Startup builds missing or stale plugins with **`dotnet build --no-dependencies`** and loads their output.

Set **`SCHEDULARRTV_BUILD_PLUGINS=false`** to skip automatic builds. Plugins installed through the UI also use the checkout's **`.plugins/`** directory.

See [Plugin development](plugin-development.md) for extension contracts and packaging.

## Test

Run **`dotnet test`**:

```sh
dotnet test
```

Rendering and probing tests need real **ffmpeg** and **ffprobe** executables on **PATH**.

### Snapshot tests

**EngineSnapshotTests** compares full schedule output with **`tests/SchedularrTV.Core.Tests/Snapshots/`**.

- Inspect **`*.received.txt`** when output differs
- Set **`UPDATE_SNAPSHOTS=1`** only for intentional output changes
- Review updated snapshots before committing

Missing snapshots are recorded locally; CI treats them as failures.

## Project map

| Project or directory | Responsibility |
| --- | --- |
| **`src/SchedularrTV.Core`** | Configuration, queries, ordering, temporal rules, archive reading, schedule engine |
| **`src/SchedularrTV.Plugins.Abstractions`** | Manifests and extension contracts |
| **`src/SchedularrTV.Providers.Jellyfin`** | Jellyfin media source |
| **`src/SchedularrTV.Providers.FileSystem`** | Local video, music, metadata, artwork, and clips |
| **`src/SchedularrTV.Providers.ErsatzTv`** | Playout, XMLTV, lineup, process supervision |
| **`src/SchedularrTV.Service`** | Background work, Blazor UI, REST endpoints, plugin loading |
| **`src/SchedularrTV.Tray`** | Launcher and tray lifecycle |
| **`src/Shared`** | Shared hosting and path code |
| **`plugins/`** | Optional feature and voice plugins |
| **`samples/SchedularrTV.Plugins.Sample`** | External plugin example |
| **`tests/`** | Automated tests and engine snapshots |
| **`tools/`** | Profiling, break diagnostics, promo previews |

## Edit documentation

Guides live in **`docs/*.md`** and are embedded in the service. **`Docs/DocsLibrary.cs`** assigns sidebar sections and rewrites relative Markdown links for the in-app browser.

- Start each guide with one **H1** and an introduction of at most three sentences
- Organize tasks with **action headings** and ordered steps
- Start every bullet with an **action verb** and keep it to one fragment
- Bold **UI labels**, **key terms**, and inline **commands**
- Use relative **`.md` links** between guides
- Register new guide slugs in **`DocsLibrary.Layout`**

## Package releases

**`scripts/package.ps1`** requires PowerShell **7.4+** and the .NET 10 SDK. Output goes to **`publish/dist`**.

```powershell
./scripts/package.ps1
./scripts/package.ps1 -Targets win-x64 -NoPlugins
./scripts/package.ps1 -PluginsOnly
./scripts/package.ps1 -Test
./scripts/package.ps1 -Version 1.2.0 -Release
```

| Artifact | Contents |
| --- | --- |
| **Platform archive** | Self-contained tray at root and service under `server/` |
| **`plugin-<id>.zip`** | One framework-dependent plugin |
| **`all-plugins.zip`** | All optional plugins |
| **macOS archive** | App bundle with ad-hoc signing |

Supported targets are **win-x64**, **linux-x64**, **linux-arm64**, **osx-arm64**, and **osx-x64**. ARM64 Linux uses ReadyToRun.

The [build workflow](../.github/workflows/build.yml) tests on Windows and publishes artifacts. Pushes to **`main`** replace the nightly release; **`v*`** tags produce numbered releases.

See [Build profiling](schedule-performance.md) for schedule benchmarks and [Generated promos](generated-promos.md#command-line-preview) for render previews.
