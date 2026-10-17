# Installation

Release downloads include the .NET runtime. Playback requires **ErsatzTV Next** and media from **Jellyfin**, local folders, or a media-source plugin.

## Requirements

| Component | Purpose |
| --- | --- |
| [**ErsatzTV Next**](https://github.com/ErsatzTV/next) | Transcoding, streams, playlist, and guide |
| [**ffmpeg** and **ffprobe**](https://ffmpeg.org/download.html) | Media probing, generated video, and live plugin streams |
| **Jellyfin** or local media folders | Shows and movies |
| **Chrome, Edge, Chromium, or Brave** | Browser-rendered plugins only |

Install **ffmpeg** with **`winget install ffmpeg`**, **`brew install ffmpeg`**, or your distribution's package manager. The app searches beside its executable, on **PATH**, and in common package-manager locations; custom paths belong in **App settings → ffmpeg**.

## Download

Choose an archive from [Releases](https://github.com/CarsonKompon/SchedularrTV/releases).

| System | Archive |
| --- | --- |
| Windows x64 | `SchedularrTV-<version>-build-win-x64.zip` |
| macOS Apple silicon | `SchedularrTV-<version>-build-osx-arm64.zip` |
| macOS Intel | `SchedularrTV-<version>-build-osx-x64.zip` |
| Linux x64 | `SchedularrTV-<version>-build-linux-x64.tar.gz` |
| Linux ARM64 | `SchedularrTV-<version>-build-linux-arm64.tar.gz` |

**`<version>`** is a release tag such as `v1.0.0`, or `nightly`. Nightly builds track `main`.

## Start the app

### Windows

1. Extract the ZIP.
2. Run **`SchedularrTV.exe`**.
3. Select **More info → Run anyway** if SmartScreen blocks the unsigned build.

### macOS

1. Extract the ZIP and move **`SchedularrTV.app`** to **Applications**.
2. Open the app.
3. Select **System Settings → Privacy & Security → Open Anyway** if macOS blocks it.

### Linux

Extract the archive, then run **`SchedularrTV`**:

```sh
tar xzf SchedularrTV-<version>-build-linux-x64.tar.gz
./SchedularrTV/SchedularrTV
```

The tray needs **StatusNotifier/AppIndicator** support. Use **`--no-tray`** without a compatible desktop; a system without a display starts headless automatically.

## Complete setup

1. Open <http://localhost:5252>.
2. Connect Jellyfin or add local TV and movie folders.
3. Choose the ErsatzTV executable and verify ffmpeg and ffprobe.
4. Add optional commercial folders.
5. Complete the setup guide and create a channel.

See [Create and tune channels](web-ui-channels.md) for your first channel, or [Use the Web UI](web-ui.md) for other tasks. **App settings → Run setup guide** reopens setup.

| IPTV player setting | Default URL |
| --- | --- |
| **Playlist** | `http://localhost:8409/channels.m3u` |
| **Guide** | `http://localhost:8409/xmltv.xml` |

Replace `localhost` with the ErsatzTV computer's address for players on other devices.

## Update

1. Stop SchedularrTV.
2. Extract the new release over the application folder, or replace **`SchedularrTV.app`** on macOS.
3. Update optional plugins from the same release.
4. Start SchedularrTV.

The data folder survives application updates. **Plugins** shows the installation folder; macOS stores plugins outside the app bundle.

Release checks run every six hours by default. Set **`Updates:Enabled`** to `false` in app configuration to disable them; nightly builds are not offered as updates.

## Data folder

| System | Default location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\SchedularrTV` |
| macOS | `~/Library/Application Support/SchedularrTV` |
| Linux | `$XDG_DATA_HOME/schedularrtv`, or `~/.local/share/schedularrtv` |

Start with **`--data-dir <folder>`** or set **`SCHEDULARRTV_DATA`** to override the location. Relative configuration paths resolve against this folder.

| Path | Contents |
| --- | --- |
| `config/schedule.yaml` | Schedule and channel settings |
| `settings.json` | App settings and plain-text credentials |
| `state/` | Playback state and media caches |
| `plugin-data/` | Plugin-owned data |
| `stats/viewing.db` | Viewing history |
| `logs/server.log` | Server and managed ErsatzTV output |

For a full backup, stop the app and copy the data folder plus the plugin folder shown on **Plugins**. **Backups** in the Web UI covers schedule versions only.

## Headless servers and systemd

Run **`server/SchedularrTV.Service`** directly, or **`SchedularrTV --no-tray`** to retain launcher supervision.

Create a user unit at **`~/.config/systemd/user/schedularrtv.service`**:

```ini
[Unit]
Description=SchedularrTV

[Service]
ExecStart=/opt/SchedularrTV/server/SchedularrTV.Service
Restart=on-failure

[Install]
WantedBy=default.target
```

Enable it with **`systemctl`**:

```sh
systemctl --user enable --now schedularrtv
loginctl enable-linger "$USER"
```

Enable **App settings → Allow other devices on the network** for remote access, then restart. The Web UI has no login; use a trusted network.

## Raspberry Pi and other low-power devices

Use the **linux-arm64** build on a 64-bit OS. Install **ffmpeg**, your distribution's **ICU** package, and **Chromium** for browser-rendered plugins; Raspberry Pi OS Bookworm uses:

```sh
sudo apt install ffmpeg libicu72 chromium
```

**App settings → Hardware and video encoding** shows the detected hardware profile. **LowPower** lowers default picture sizes, bitrates, capture rates, and rendering concurrency; explicit settings take precedence.

- Prefer **4 GB RAM** or more for plugins
- Lower **Live page channels** resolution and capture rates for browser-heavy channels
- Select a supported **hardware encoder** for live pages and rendered clips
- Store the data folder and ErsatzTV **`hls/`** scratch space on an SSD
- Move ErsatzTV to a stronger computer when concurrent streams exceed the Pi's capacity

See [ErsatzTV integration](ersatztv.md#path-mappings-and-guide-options) for remote media paths.
