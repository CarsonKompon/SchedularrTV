# Manage plugins

Plugins add media sources, generated content, graphics, voices, and output integrations. Manage them from **Plugins** in a browser on the scheduler's computer.

## Install

1. Download **`SchedularrTV-<version>-plugin-<id>.zip`** or **`SchedularrTV-<version>-all-plugins.zip`** from [Releases](https://github.com/CarsonKompon/SchedularrTV/releases).
2. Open **Plugins → Install a plugin**.
3. Choose the ZIP.
4. Select **Restart now**.
5. Open the plugin's **Settings** gear and configure its requirements.

Use plugin packages from the same scheduler release, especially plugins built against the service. Plugins execute with the scheduler account's system access.

## Enable and configure

1. Turn on the plugin's switch.
2. Enable any required dependencies listed on its card.
3. Open **Settings**, make changes, and select **Save**.
4. Open the plugin's sidebar page or channel editor tab.

Switches and saved settings normally apply immediately. Disabling a plugin removes its active sources, outputs, pages, and tabs while preserving its saved configuration; dependent plugins display **Waiting**.

Required built-in plugins display **Always on** and cannot be disabled.

| Change | Restart |
| --- | --- |
| Install, update, or remove files | Required |
| Enable or disable a loaded plugin | Not required |
| Save plugin settings | Normally not required; follow field-specific notes |

## Choose a plugin

| Plugin | Location after enabling | Purpose |
| --- | --- | --- |
| **Jellyfin** · built in | **App settings** | Media server library |
| **Local media folders** · built in | **App settings** | Local shows, movies, music, and clips |
| **ErsatzTV Next** · built in | **App settings** | Streaming output |
| **Promos** · built in | Channel **Promos** tab | Library promo selection |
| [**Archive Follow**](archive-follow.md) · built in, initially off | Channel **Follow** tab; **Archive schedules** | Historical schedules |
| [**Generated promos**](generated-promos.md) | Channel **Generated Promos** tab | Rendered promo templates; requires **Promos** |
| [**TikTok, MiniMax, Piper voices**](generated-promos.md#voices) | Plugin **Settings**; voice pickers | Text-to-speech |
| [**Overlays**](overlays.md) | Channel **Overlays** tab | Bugs, lower thirds, captions |
| [**Local AI writer**](local-ai.md) | Plugin **Settings**; text editors | Generated scripts and captions |
| [**Radio shows**](radio.md) | **Radio shows** | Library music and internet radio |
| [**News shows**](news.md) | **News shows** | Headlines and data segments |
| [**Web page shows**](web-channels.md) | **Web shows** | Weather, clocks, custom pages |
| [**TV Guide shows**](tv-guide.md) | **Guide shows** | Scrolling listings |
| [**IPTV passthrough**](iptv.md) | **IPTV channels** | External M3U/XMLTV channels |
| [**Streamer channels**](streamers.md) | **Streamer channels** | Twitch and YouTube |
| [**Credit squeeze**](credit-squeeze.md) | Channel **Credit Squeeze** tab | End-credit panels |
| [**Auto Chapters**](auto-chapters.md) | **Auto chapters** | Detected mid-roll boundaries |

[**Background music**](background-music.md) is a shared feature of several plugins, not a separate package.

## Schedule plugin content

**Web**, **radio**, **news**, and **guide** shows become library titles with one virtual episode per configured length. Select them in a channel's **Base rotation**, a block, or **Breaks** interstitial shows.

**IPTV** and **streamer** plugins publish complete external channels. Their **Edit** buttons return to the plugin page rather than the standard schedule editor.

## Update or remove

- Install the newer **ZIP** and restart to update
- Select **Remove**, confirm **Remove?**, and restart to uninstall
- Disable the plugin's **switch** for a reversible pause

## Where plugins are stored

The **Plugins** page displays the active folder. Manual installation uses one subfolder per plugin, containing **`plugin.json`** and its assemblies.

| Environment | Default plugin folder |
| --- | --- |
| Writable application folder | `plugins/` beside the launcher or standalone server |
| macOS app or unwritable application folder | `<data folder>/plugins/` |
| Source checkout | `.plugins/` in the checkout |
| Explicit override | **`--plugins-dir <folder>`** |

Application-level settings live in **`settings.json`** under **`Plugins:<id>`**; private plugin files live in **`plugin-data/`**. Schedule-level settings use **`plugins:`** sections in YAML.

## Resolve loading problems

| Status or symptom | Action |
| --- | --- |
| **Waiting** | Enable or repair the required dependency |
| **Failed** | Read the card's error and install a compatible package |
| **Restart required** | Restart after file changes |
| Missing management controls | Open the page on the scheduler's computer |
| Missing channel tab | Enable the plugin and its dependencies |

See [Plugin development](plugin-development.md) for manifests, extension points, and packaging.
