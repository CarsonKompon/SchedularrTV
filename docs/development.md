# Development

How to build, run, test and package SchedularrTV from source, and how the solution is laid out.

## Overview

SchedularrTV is a .NET 10 solution. The engine is timezone-, recurrence- and state-heavy, which is where a strong type system and `TimeZoneInfo`/`DateTimeOffset` pay off.

## Running from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and ffmpeg on the PATH.

```sh
dotnet run --project src/SchedularrTV.Service     # server only, http://localhost:5252
dotnet run --project src/SchedularrTV.Tray        # tray; a Debug build copies the server in beside it
```

When `config/schedule.yaml` sits beside the program (as it does when running the server from its project folder), that folder is used as the data folder, so a checkout works on its own files. `settings.json` written by App settings is ignored by git.

### Plugins in a source checkout

Run from a source checkout, the scheduler loads every plugin under `plugins/` from the checkout's `.plugins/` folder (ignored by git), one folder per project.

- A Debug build of a plugin (building the solution, say) copies it there itself.
- At startup, any plugin that's missing or older than its sources (or the scheduler it's built against) is built first with `dotnet build --no-dependencies`, with progress in the console.
- Anything built but not yet in `.plugins/` is copied over; up-to-date ones start at once.
- Set `SCHEDULARRTV_BUILD_PLUGINS=false` to skip building and load whatever was built last.
- Nothing is loaded from (or moved out of) the per-user data folder's `plugins`. A plugin installed from the Plugins page goes in `.plugins/` too.

## Tests

```sh
dotnet test
```

Some tests render promos and probe clips with the real ffmpeg and ffprobe, so both must be on the PATH.

### Snapshot tests

`EngineSnapshotTests` compare the engine's complete output for a set of scenarios with recorded snapshots in `tests/SchedularrTV.Core.Tests/Snapshots/`.

- A missing snapshot is recorded on the first local run (CI fails instead).
- A difference writes `<name>.received.txt` beside the snapshot.
- When a change to the output is intended, run with `UPDATE_SNAPSHOTS=1` and commit the updated snapshots.

## Projects

| Project | Purpose |
| --- | --- |
| `SchedularrTV.Core` | Domain model, query language, temporal tags, ordering, archive reader, timeline and gap-packing engine. |
| `SchedularrTV.Plugins.Abstractions` | The plugin API: `IPlugin`, manifests and the extension points. See [Plugins](plugins.md). |
| `SchedularrTV.Providers.Jellyfin` | Built-in plugin: Jellyfin library snapshot. |
| `SchedularrTV.Providers.FileSystem` | Built-in plugin: folder-backed library for commercials and bumpers, tagged by folder path. |
| `SchedularrTV.Providers.ErsatzTv` | Built-in plugin: playout JSON, XMLTV fragments, lineup generation and process supervision. |
| `plugins/SchedularrTV.Plugins.GeneratedPromos` | Optional plugin: generated promos (renderer, editor, render queue). Builds against `SchedularrTV.Service`. |
| `plugins/SchedularrTV.Plugins.TikTok`, `.MiniMax`, `.Piper` | Optional plugins: voices for generated promos. |
| `samples/SchedularrTV.Plugins.Sample` | Example external plugin with settings, a page and a playout processor. |
| `SchedularrTV.Service` | ASP.NET Core server: background rebuilds, the Blazor web UI and the REST API. Runs on its own or under the tray. |
| `SchedularrTV.Tray` | Cross-platform tray / menu-bar app (Avalonia). Runs the server as a child process from a `server` folder beside it. |
| `src/Shared` | Source files compiled into both the server and the tray (data folder, settings file, autostart). |
| `SchedularrTV.Core.Tests` | xUnit suite covering recurrence, ordering determinism, gap packing and end-to-end builds. |

The tray and server are separate processes so that:

- their SkiaSharp versions never clash;
- a tray crash can't take the scheduler down;
- the server can run headless.

## Diagnostics

- [Schedule build profiling](schedule-performance.md): `tools/profile-schedule.cs`.
- [Diagnosing ad breaks](schedule-performance.md#diagnosing-ad-breaks): `tools/diagnose-breaks.cs`.
- `tools/generate-promo.cs` previews generated promos from the command line.

## Releases

`.github/workflows/build.yml` runs the tests on Windows, then publishes self-contained builds for `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`. `linux-arm64` (for the Raspberry Pi) is cross-published from x64 with ReadyToRun, so it starts quickly on slow CPUs.

- Every push to `main` replaces the **nightly** pre-release.
- Pushing a tag such as `v1.2.0` creates a release with that version.

### What each build contains

- The tray is published to the top of the folder and the server into `server/`.
- The projects under `plugins/` are published once (framework-dependent) into a `SchedularrTV-<version>-plugin-<id>.zip` each, plus `SchedularrTV-<version>-all-plugins.zip` with all of them, stamped with the release's version.
- `<version>` is the tag (`v1.2.0`) for a release and `nightly` otherwise. Every download starts with `SchedularrTV-<version>-`.
- macOS builds are wrapped in a menu-bar-only `SchedularrTV.app` with ad-hoc signatures. Nothing is notarised or code-signed.

### Building the downloads locally

`scripts/package.ps1` builds the same files as CI, named the same way, into `publish/dist`. It needs PowerShell 7.4+ and the .NET 10 SDK, and every system can be built from Windows, macOS or Linux.

Started from Windows PowerShell 5.1, it runs itself again in PowerShell 7 (`pwsh`), or says how to install it (`winget install --id Microsoft.PowerShell`).

```powershell
./scripts/package.ps1                                  # everything, as the nightly
./scripts/package.ps1 -Targets win-x64 -NoPlugins      # just the Windows download
./scripts/package.ps1 -PluginsOnly                     # just the plugin zips
./scripts/package.ps1 -Test                            # run the tests first, as CI does
./scripts/package.ps1 -Version 1.2.0 -Release          # stamped and named as release v1.2.0
```

- A local nightly is numbered like CI's (just past the latest `v*` tag) with run number 0 (use `-RunNumber` to change it), so the app never takes it for a newer nightly.
- The Linux tarballs and the macOS and plugin zips carry Unix permissions, so the programs are executable when unpacked, even when built on Windows.
- Built on a Mac, the `.app` is signed with `codesign` and zipped with `ditto` exactly as CI does. Elsewhere it relies on the ad-hoc signature the .NET SDK gives the programs it publishes.
