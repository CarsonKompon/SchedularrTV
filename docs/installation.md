# Installation

Download SchedularrTV for your system, start it, and follow the setup guide to connect Jellyfin, ErsatzTV Next and ffmpeg.

## Requirements

- **Jellyfin**, for the programming library.
- **[ErsatzTV Next](https://github.com/ErsatzTV/next)**, which streams the channels.
- **ffmpeg and ffprobe**, for generated promos and for reading clip lengths in commercial folders.

To install ffmpeg, use [ffmpeg.org](https://ffmpeg.org/download.html), `winget install ffmpeg`, `brew install ffmpeg` or your package manager.

SchedularrTV finds ffmpeg and ffprobe automatically if they are beside the app, on the PATH, or in the usual Homebrew, winget, Scoop and Chocolatey folders. Otherwise, choose them in **App settings**.

> [!NOTE]
> .NET does not need to be installed. Every download is self-contained.

## Download

Get the file for your system from the [Releases](https://github.com/CarsonKompon/tv_scheduler/releases) page.

- **Numbered releases** come from `v*` tags.
- **Nightly** is rebuilt from `main` on every push. It is an untested snapshot.

File names start with the release's tag (`SchedularrTV-v1.0.0-…`), or `SchedularrTV-nightly-…` for the nightly build.

| System | File |
| --- | --- |
| Windows | `SchedularrTV-<version>-win-x64.zip` |
| macOS (Apple silicon) | `SchedularrTV-<version>-osx-arm64.zip` |
| macOS (Intel) | `SchedularrTV-<version>-osx-x64.zip` |
| Linux | `SchedularrTV-<version>-linux-x64.tar.gz` |
| Linux on ARM (Raspberry Pi 4/5) | `SchedularrTV-<version>-linux-arm64.tar.gz` |

## Starting it

### Windows

Unzip anywhere and run `SchedularrTV.exe`.

SmartScreen may warn you because the build isn't signed. Choose **More info → Run anyway**.

### macOS

Unzip and move `SchedularrTV.app` to Applications. This is the same for Apple silicon and Intel.

The app isn't notarised. The first time, open it, then go to **System Settings → Privacy & Security** and choose **Open Anyway**. Alternatively, run:

```sh
xattr -dr com.apple.quarantine /Applications/SchedularrTV.app
```

### Linux

Extract the archive and run the app:

```sh
tar xzf SchedularrTV-<version>-linux-x64.tar.gz
./SchedularrTV/SchedularrTV
```

The ARM download (`linux-arm64`) works the same way, but needs a 64-bit OS. See [Raspberry Pi and other low-power devices](#raspberry-pi-and-other-low-power-devices).

The tray icon needs a desktop with StatusNotifier/AppIndicator support (KDE, Cinnamon, XFCE, and GNOME with the AppIndicator extension). Without one, run `./SchedularrTV/SchedularrTV --no-tray` in a terminal. With no display at all, it runs without the tray automatically.

### The tray icon and web UI

SchedularrTV runs in the notification area (the menu bar on macOS) and serves its web UI at `http://localhost:5252`.

The tray menu can:

- open the UI
- start, stop and restart the server
- open the data folder and the server log
- turn on **Start when I sign in**

## First run

The first time the web UI opens, a setup guide walks you through:

1. **Jellyfin**: the server address and an API key (Jellyfin → Dashboard → API Keys), with **Test connection**.
2. **ErsatzTV Next**: the `ersatztv` program, so SchedularrTV starts and stops it for you. See [ErsatzTV Next](ersatztv.md).
3. **ffmpeg**: checks both programs are found.
4. **Commercials** folders and **extras** (start at sign-in). Both are optional.

Finishing saves the settings, and they are used straight away. The scheduler only restarts if you changed its port or network access.

### Creating your first channel

Choose **Create a channel** to go straight into the **Channel Creation Wizard**. It asks:

- what the channel plays
- how its time slots and ad breaks work
- which commercials to use
- optionally, whether to add a programming block

You can run the wizard again any time from **Channels → New channel**, or start from an empty channel instead.

### Watching your channels

Point your IPTV player at ErsatzTV's playlist and guide:

- Playlist: `http://localhost:8409/channels.m3u`
- Guide: `http://localhost:8409/xmltv.xml`

### Optional plugins

Generated promos, their voices and web page channels (WeatherStar and friends) are optional plugins. To add them:

1. Download `SchedularrTV-<version>-all-plugins.zip` from the same release.
2. Install it from the **Plugins** page.
3. Restart SchedularrTV.

### Reopening the setup guide

The guide reappears while Jellyfin isn't connected. **Skip for now** hides it until your next visit.

**App settings → Run setup guide** reopens it. Everything it sets can also be changed directly in **App settings**.

## Updates

When a newer release is published, a dot appears beside **Settings** in the sidebar, and the settings pages show a link to download it.

- SchedularrTV checks the GitHub repository in `Updates:Repository` every six hours. This is set in `appsettings.json`, or `settings.json` in the data folder.
- Set `Updates:Enabled` to `false` to stop the check.
- Nightly builds are never offered as updates. A nightly is numbered just past the latest release (`1.0.1-nightly.N` after `v1.0.0`), so it's told when the next release comes out.

### Installing an update

1. Stop SchedularrTV.
2. Unzip the new download over the app folder, replacing its files.

The `plugins` folder isn't part of the download, so your installed plugins stay. The data folder is not touched.

On macOS, replace `SchedularrTV.app`. Plugins there live in the data folder, so they stay too.

### Updating plugins

Update a plugin the same way: replace its folder in `plugins`, or install its newer zip from the **Plugins** page.

> [!IMPORTANT]
> Generated promos is built against the scheduler, so update it with each new release.

## Data folder

Everything SchedularrTV writes lives in one per-user folder: the schedule, playback state, rendered promos, `settings.json` and `logs/server.log`.

Installed plugins are the exception. They go in a `plugins` folder in the app's own folder (see [Plugins](plugins.md#installing)).

| System | Data folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\SchedularrTV` |
| macOS | `~/Library/Application Support/SchedularrTV` |
| Linux | `~/.local/share/schedularrtv` (or `$XDG_DATA_HOME/schedularrtv`) |

To use a different folder, start with `--data-dir <folder>`, or set `SCHEDULARRTV_DATA`.

> [!WARNING]
> API keys entered in App settings are stored as plain text in `settings.json`. Anyone who can open the data folder can read them.

## Headless (servers, systemd)

`server/SchedularrTV.Service` inside the download is the whole scheduler without the tray. Run it directly, or run `SchedularrTV --no-tray` to have the launcher supervise it in the foreground.

A systemd user unit:

```ini
# ~/.config/systemd/user/schedularrtv.service
[Unit]
Description=SchedularrTV

[Service]
ExecStart=/opt/SchedularrTV/server/SchedularrTV.Service
Restart=on-failure

[Install]
WantedBy=default.target
```

```sh
systemctl --user enable --now schedularrtv
loginctl enable-linger $USER   # keep it running without a login session
```

To reach the web UI from another machine, turn on **Allow other devices on the network** in App settings.

> [!WARNING]
> The web UI has no password, so only allow other devices on a network you trust.

## Raspberry Pi and other low-power devices

### Setting up a Pi

Use `SchedularrTV-<version>-linux-arm64.tar.gz` on a 64-bit Raspberry Pi OS (or another 64-bit Linux) on a Raspberry Pi 4 or 5.

- 4 GB of memory or more is recommended.
- 2 GB works for the scheduler with few extras.

Install ffmpeg and ICU. For the web page, radio, TV guide and streamer channels, also install Chromium (Chrome and Edge have no ARM Linux builds):

```sh
sudo apt install ffmpeg libicu72 chromium
```

`libicu72` is Bookworm's; use whichever `libicu` version your release has. On older Raspberry Pi OS releases, use `chromium-browser`.

Run it headless with the [systemd unit above](#headless-servers-systemd).

The arm64 download uses workstation garbage collection and is precompiled (ReadyToRun), so it starts quickly.

### The low-power profile

SchedularrTV recognises a Raspberry Pi, or another small 64-bit ARM Linux board (four cores or fewer, or about 4 GB of memory or less), and uses its **low-power profile**.

**App settings → Hardware and video encoding** shows what was detected, and can force the profile on or off.

The profile only changes defaults. Anything you've set yourself is kept. It changes these:

| What | Standard | Low-power |
| --- | --- | --- |
| New channels' `channel.json` | 1080p, 6 Mbps, loudness normalisation | 720p, 3 Mbps, no loudness normalisation |
| Live page channels (web, radio, guide, streamers) | 720p30, x264 `veryfast`, 3.5 Mbps | 540p30, x264 `ultrafast`, 2.5 Mbps |
| Capture frames per second (web / radio / guide / streamers) | 15 / 20 / 24 / 15 | 8 / 10 / 12 / 8 |
| Streamers: highest stream quality, check interval | 720p, 60 s | 480p, 120 s |
| Generated promos and credit squeezes | x264 `medium` / `veryfast`, every thread, 2 promos at once | x264 `superfast`, 2 threads, 1 promo at once |
| Auto Chapters decoding | every thread, software | 2 threads, `-hwaccel auto` (software if that fails) |
| Commercial folder ffprobes at once | 8 | 3 |
| Jellyfin library and commercial folders re-read after | 30 minutes | 2 hours |
| New radio stations' background | blurred cover | plain colour |
| Local AI default model | Qwen3.5 2B | Qwen3.5 0.8B under 6 GB of memory, else 2B |

The profile also shows warnings in the pages and plugin settings that are known to be heavy on a Pi, with what to choose instead.

### Getting more out of a Pi

What limits a Pi is video encoding, not scheduling. ErsatzTV re-encodes every channel being watched (in software on a Pi 5, which has no H.264 encoder), so expect about one 720p stream at a time.

To get more out of it:

- Pass channels through where the files are already H.264/AAC (IPTV passthrough does this). Avoid overlays on those channels, since items with overlays are always re-encoded.
- On a Pi 4, try the **Raspberry Pi 4 hardware (h264_v4l2m2m)** encoder for live page channels and rendered clips.
- Keep the data folder, and ErsatzTV's `hls` folder in the lineup folder, on a USB SSD or tmpfs rather than the SD card.
- Run ErsatzTV Next on a stronger computer and keep SchedularrTV on the Pi. `ErsatzTv:PathMappings` maps media paths between them.
