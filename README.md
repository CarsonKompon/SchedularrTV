# SchedularrTV

**Local TV like it used to be**

SchedularrTV builds round-the-clock linear TV schedules from your Jellyfin library, complete with time
slots, programming blocks, ad breaks, bumpers and promos, and hands them to
[ErsatzTV Next](https://github.com/ErsatzTV/next) to stream to any IPTV player with a full programme guide.

## Features

- **Schedules like a real network.** Anchored time slots, recurring and holiday blocks, marathons and a
  fallback rotation that keeps every channel full 24/7.
- **Ad breaks that fit.** Breaks are packed with commercials from folders on disk, tagged by folder
  name, and finished with bumpers and station IDs so programmes start on time. Mid-roll breaks can use
  chapter markers.
- **Seasonal commercials.** Holiday and seasonal ads (Christmas, Halloween, winter) air at the right time
  of year automatically.
- **Promos.** Use your own Up Next and Coming Up clips, or generate them: a visual template editor with
  text-to-speech voice-overs and background music.
- **Archive Follow.** Replay what a real network aired on a matching date in history, using your copies
  of the shows.
- **Repeatable.** Shuffles and rotations are deterministic and remembered, so schedules don't repeat
  themselves and rebuilding doesn't reshuffle what's already planned.
- **Plugins** for new media sources, outputs, voices and playout tweaks, installed from a zip.
- **Web UI** for everything, plus a tray app for Windows, macOS and Linux that can start with your
  computer and run ErsatzTV Next for you.

## Getting started

1. **Download** the build for your system from [Releases](../../releases) and unzip it. No other
   install is needed.

   | Windows | macOS | Linux |
   | --- | --- | --- |
   | `SchedularrTV-<version>-win-x64.zip` | `SchedularrTV-<version>-osx-arm64.zip` (Apple silicon)<br>`SchedularrTV-<version>-osx-x64.zip` (Intel) | `SchedularrTV-<version>-linux-x64.tar.gz` |

   `<version>` is the release's tag (`v1.0.0`), or `nightly` for the nightly build.

2. **Run** `SchedularrTV`. It appears in the notification area or menu bar and opens at
   <http://localhost:5252>. The builds aren't code-signed, so Windows and macOS warn the first time; see
   [Installation](docs/installation.md) for how to open them.
3. **Follow the setup guide** that opens the first time: connect Jellyfin, choose the ErsatzTV Next
   program, and check ffmpeg was found.
4. **Create a channel** with the Channel Creation Wizard at the end of the guide (or later from
   **Channels → New channel**), then add ErsatzTV's playlist
   (`http://localhost:8409/channels.m3u`) and guide (`http://localhost:8409/xmltv.xml`) to your IPTV
   player.

### Requirements

- [Jellyfin](https://jellyfin.org) with your shows and movies
- [ErsatzTV Next](https://github.com/ErsatzTV/next) for streaming
- [ffmpeg](https://ffmpeg.org/download.html) (`winget install ffmpeg`, `brew install ffmpeg`, or your package manager)

## Documentation

| Guide | Covers |
| --- | --- |
| [Installation](docs/installation.md) | Downloads, first run, the data folder, updating, running headless or under systemd |
| [Configuration](docs/configuration.md) | App settings, the schedule file, commercials on disk, Jellyfin tags |
| [How scheduling works](docs/scheduling.md) | Blocks and anchors, ad break packing, mid-rolls, the query language, seasonal tags |
| [Generated promos](docs/generated-promos.md) | Promo templates, voices, music, external generators |
| [WeatherStar weather](docs/weatherstar.md) | Live retro weather channels and forecasts between shows |
| [TV Guide channel](docs/tv-guide.md) | A scrolling Prevue / TV Guide Channel-style listings channel from your own schedule |
| [Auto Chapters](docs/auto-chapters.md) | Mid-roll breaks at the fades to black in files without chapter markers |
| [Archive Follow](docs/archive-follow.md) | Replaying historical schedules |
| [ErsatzTV Next](docs/ersatztv.md) | What is published and how ErsatzTV is managed |
| [Plugins](docs/plugins.md) | Installing plugins and writing your own |
| [REST API](docs/rest-api.md) | Endpoints for scripting |
| [Development](docs/development.md) | Building from source, tests, releases |

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/SchedularrTV.Tray     # or src/SchedularrTV.Service for the server alone
dotnet test
```

See [Development](docs/development.md) for more.
