# SchedularrTV

**SchedularrTV** builds continuous TV schedules from Jellyfin and local media, with programming blocks, commercials, promos, and an electronic programme guide. **ErsatzTV Next** streams the resulting channels to IPTV players.

## Start here

1. Download and extract the build for your system from [Releases](https://github.com/CarsonKompon/SchedularrTV/releases).
2. Install [**ErsatzTV Next**](https://github.com/ErsatzTV/next) and [**ffmpeg**](https://ffmpeg.org/download.html).
3. Run **SchedularrTV** and open <http://localhost:5252>.
4. Connect **Jellyfin** or add local media folders in the setup guide.
5. Select the **ErsatzTV Next** executable and verify **ffmpeg** and **ffprobe**.
6. Create a channel through **Channels → New channel → Channel Creation Wizard**.
7. Add the playlist and guide below to your IPTV player.

| Player setting | Default URL |
| --- | --- |
| **M3U playlist** | `http://localhost:8409/channels.m3u` |
| **XMLTV guide** | `http://localhost:8409/xmltv.xml` |

For another device, replace `localhost` with the ErsatzTV computer's address. Release builds include the .NET runtime; Jellyfin is optional when using local folders.

## Web UI

| Task | Guide |
| --- | --- |
| Install, update, or move the app | [Installation](docs/installation.md) |
| Find a Web UI task or advanced feature | [Use the Web UI](docs/web-ui.md) |
| Create channels and tune playback | [Channels](docs/web-ui-channels.md) |
| Select titles, order episodes, and add intros | [Collections and media selection](docs/web-ui-collections.md) |
| Schedule windows, fixed starts, and marathons | [Blocks and anchors](docs/web-ui-scheduling.md) |
| Configure commercials, mid-rolls, and announcements | [Breaks and promos](docs/web-ui-breaks.md) |
| Change defaults, monitor playback, or restore backups | [Settings and maintenance](docs/web-ui-maintenance.md) |
| Add shows, movies, music, and metadata | [Local media](docs/local-library.md) |
| Import an existing lineup | [Import channels](docs/import.md) |
| Inspect viewers and adjust scheduling weights | [Channel stats](docs/stats.md) |
| Diagnose playback, missing content, or high CPU | [Troubleshooting](docs/troubleshooting.md) |

## Plugins

| Task | Guide |
| --- | --- |
| Install, configure, update, or remove plugins | [Manage plugins](docs/plugins.md) |
| Generate promos and voice-overs | [Generated promos](docs/generated-promos.md) |
| Add logos, lower thirds, and captions | [Overlays](docs/overlays.md) |
| Write captions and voice-over scripts with a local model | [Local AI writer](docs/local-ai.md) |
| Add music or internet radio | [Radio shows](docs/radio.md) · [Background music](docs/background-music.md) |
| Schedule news, weather, clocks, or listings | [News](docs/news.md) · [Web shows](docs/web-channels.md) · [TV Guide](docs/tv-guide.md) |
| Carry external channels | [IPTV](docs/iptv.md) · [Streamers](docs/streamers.md) |
| Add credit panels or detect break points | [Credit squeeze](docs/credit-squeeze.md) · [Auto Chapters](docs/auto-chapters.md) |
| Recreate historical programming | [Archive Follow](docs/archive-follow.md) |

## Advanced

| Task | Guide |
| --- | --- |
| Edit the schedule format | [YAML reference](docs/yaml.md) |
| Configure paths, ports, and credentials | [App configuration](docs/configuration.md) |
| Understand selection, precedence, and break packing | [Scheduling and queries](docs/scheduling.md) |
| Configure output, encoding, and remote playout | [ErsatzTV integration](docs/ersatztv.md) |
| Automate validation, previews, and rebuilds | [REST API](docs/rest-api.md) |
| Measure build times and inspect padding | [Build profiling](docs/schedule-performance.md) |
| Extend the application | [Plugin development](docs/plugin-development.md) |
| Build, test, or package the project | [Development](docs/development.md) |

The same guides are available under **Help → Documentation** in the app.

## Run from source

Install the **.NET 10 SDK**, then run **`dotnet run`** from the repository root:

```sh
dotnet run --project src/SchedularrTV.Tray
```

Use **`src/SchedularrTV.Service`** for the server alone. See [Development](docs/development.md) for tests and packaging.
