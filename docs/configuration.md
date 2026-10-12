# App configuration

**`settings.json`** stores connections, credentials, executable paths, and server options. **`config/schedule.yaml`** stores programming.

## Where settings live

| File | Editor | Application |
| --- | --- | --- |
| `<data folder>/settings.json` | **App settings**, plugin settings, or a text editor | UI-exposed settings generally apply on save; server port and network access require restart |
| `<data folder>/config/schedule.yaml` | Channel editors or **YAML** | Reloaded on save when **`Scheduler:WatchConfig`** is enabled |

See [Installation](installation.md#data-folder) for default directories. Relative paths resolve against the data folder; credentials in **`settings.json`** are plain text.

See [YAML reference](yaml.md) for the schedule format.

## Set connections and paths

Merge the required sections into **`settings.json`**:

```json
{
  "Server": {
    "Port": 5252,
    "AllowRemote": false
  },
  "Jellyfin": {
    "BaseUrl": "http://localhost:8096",
    "ApiKey": "replace-with-jellyfin-key"
  },
  "ErsatzTv": {
    "ManageProcess": true,
    "ExecutablePath": "/opt/ersatztv/ersatztv",
    "ConfigRoot": "etv/config",
    "Lineup": { "Port": 8409 }
  },
  "FileSystemLibrary": {
    "FfprobePath": "ffprobe",
    "Folders": [
      {
        "Path": "/media/Commercials",
        "Tags": ["Commercials"],
        "TagFromFolders": true,
        "PromoFolder": "Promos"
      }
    ]
  }
}
```

Restart after manual edits to ensure startup-bound options are reloaded. For local TV, movie, and music folders, use **App settings → Local TV and movies** to generate the folder entries.

## App settings reference

| Area | Configuration keys |
| --- | --- |
| **This computer** | `Server:Port`, `Server:AllowRemote` |
| **Jellyfin** | `Jellyfin:BaseUrl`, `Jellyfin:ApiKey`, `Jellyfin:UserId` |
| **ErsatzTV Next** | `ErsatzTv:ManageProcess`, `ErsatzTv:ExecutablePath`, `ErsatzTv:ConfigRoot`, `ErsatzTv:Lineup:Port` |
| **ffmpeg** | `GeneratedPromos:FfmpegPath`, `GeneratedPromos:FfprobePath`, `FileSystemLibrary:FfprobePath` |
| **Local media and commercials** | `FileSystemLibrary:Folders` |
| **Hardware profile** | `Hardware:Profile`: `Auto`, `LowPower`, or `Standard` |
| **ErsatzTV channels** | `ErsatzTv:Encoding:Hardware`, `Height`, `BitrateKbps`, `VaapiDevice`, `VaapiDriver` |
| **Live page channels** | `LiveEncoding:Encoder`, `Preset`, `Height`, `FrameRate`, `BitrateKbps`, `Threads` |
| **Rendered clips** | `Rendering:Encoder`, `Preset`, `Threads`, `HardwareBitrateKbps` |
| **Plugin settings** | `Plugins:<plugin-id>:<setting>` |
| **REST writes** | `Scheduler:ApiKey` |
| **Schedule and state paths** | `Scheduler:ConfigPath`, `Scheduler:StatePath`, `Scheduler:LogoPath` |
| **Automatic scheduling** | `Scheduler:AutoRebuild`, `Scheduler:WatchConfig` |
| **Update checks** | `Updates:Enabled`, `Updates:Repository` |

Keys after the first entry in an encoding row share its section prefix. For example, **`LiveEncoding:Height`** belongs inside the `LiveEncoding` object.

## Commercials on disk

With **Tag from folders** enabled, folder names become tags:

```text
Commercials/
  Adult Swim/
    Bumpers/
      Any Time/
        station-id.mp4
    Promos/
      Clone High/
        Up Next/
          next.mp4
```

The bumper matches:

```text
tag:'Commercials' AND tag:'Adult Swim' AND tag:'Bumpers' AND tag:'Any Time'
```

Under the configured **Promo folder**, the next two folder levels supply the show and promo type. A folder named **`key=value`** supplies an attribute instead of a tag.

Channel **Breaks** and **Promos** tabs show the expected layout and missing folders. **ffprobe** caches runtimes by file size and modification time.

## Jellyfin metadata conventions

| Jellyfin tag | Scheduler attribute |
| --- | --- |
| `promo_type:later_today` | `promo_type = later_today` |
| `show_id:Clone High` | `show_id = Clone High` |
| `trimmable:false` | Per-item trimming override |
| `stop_before_end:true` | Stop-at-boundary hint |

Other tags remain ordinary tags. **`Jellyfin:AttributeTagPrefixes`** controls attribute prefixes; BoxSet membership becomes collections and passes from series to episodes.

Use **App settings → Jellyfin → Libraries** to exclude duplicate DVR or cache libraries. Episodes in a **Specials** folder count as season `0`.

## Other configuration sources

ASP.NET configuration also accepts **`appsettings.json`**, user secrets, environment variables such as **`Jellyfin__ApiKey`**, and supported **`.env`** values. **`settings.json`** is applied last and wins over earlier sources.

For remote playout, artwork URLs, and path mappings, see [ErsatzTV integration](ersatztv.md#path-mappings-and-guide-options).
