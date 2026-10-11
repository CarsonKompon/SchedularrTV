# Configuration

SchedularrTV keeps what plays in a schedule file and its connections and programs in an app settings file. This page explains both, plus how commercials on disk and Jellyfin tags are read.

## Where settings live

SchedularrTV has two kinds of settings.

### The schedule

Channels, blocks, breaks and promos: what plays and when.

- **File:** `<data folder>/config/schedule.yaml`
- **Edited in:** the web UI (Channels, Collections, Settings) or the **YAML** page.
- Hot-reloads on save.

`config/schedule.example.yaml` in the repository is a full-featured reference schedule. A new install starts from an empty schedule with only global settings.

### App settings

Connections, keys and programs.

- **File:** `<data folder>/settings.json`
- **Edited in:** **App settings**.
- Applied as soon as they're saved, except the web UI port and network access, which take a restart.

## Adding a channel

Channels are normally added in the web UI. In YAML, append to `channels:`:

```yaml
  - id: "ch2"
    name: "Movie Channel"
    number: 2
    tvg_id: "schedularrtv.2"
    group: "SchedularrTV"
    base_rotation:
      query: "kind == 'Movie'"
      order: shuffle
```

- `number` must be unique. It becomes the ErsatzTV channel folder.
- `tvg_id` binds the guide to the stream.

The scheduler creates `channels/2/channel.json` and its playout folder, and rewrites `lineup.json`.

ErsatzTV Next reads its channel list once at startup. When SchedularrTV manages it, it is restarted automatically. Schedule changes to an existing channel are picked up live.

## Copying channel settings

1. Open the destination channel's editor.
2. Choose **Copy From** and select a source channel.
3. Confirm **Copy settings**.
4. Choose **Save** to apply the changes, or **Discard** to undo them.

This replaces the destination's scheduling settings (collection references, blocks, anchors, Follow mappings, breaks and promos). It keeps the destination's name, number, ID and logo.

- Settings are independent copies; global collections remain shared.
- Playback history is not copied or reset.

## Commercials on disk

Commercials rarely belong in a media server. The file system library treats a folder tree as a tag hierarchy, so **organising the files is tagging them**:

```text
Commercials/Adult Swim/Bumpers/Any Time/clip.mp4
            <Channel>  <Type>  <When>
```

This file becomes an item tagged `Commercials`, `Adult Swim`, `Bumpers`, `Any Time`. You can reach it with the same [query language](scheduling.md#query-language) as everything else:

```text
tag:"Commercials" AND (tag:"Adult Swim" OR tag:"Any Channel")
  AND tag:"Bumpers" AND (tag:"Any Time" OR tag:"January" OR tag:"Winter")
```

### Adding commercial folders

Add folders under **App settings → Commercials library**, or in `settings.json`:

```jsonc
"FileSystemLibrary": {
  "Folders": [
    { "Path": "/media/Commercials", "Tags": [ "Commercials" ], "TagFromFolders": true, "PromoFolder": "Promos" }
  ]
}
```

> [!TIP]
> The bottom of a channel's **Breaks** and **Promos** tabs shows the folder layout that channel looks for with its current settings (brands, clip folders, time-of-year folders, promo folder, shows and promo types). It marks which folders are already in your library and which aren't yet, and updates as you change the settings.

### Promo folders and attributes

- Beneath a folder named by `PromoFolder`, the levels become promo metadata: `<promo folder>/<show>/<promo type>`.
- A folder named `key=value` becomes an attribute rather than a tag.

### Clip lengths

Durations come from ffprobe and are cached by size and modified time, so a 10k-file library is probed once and rescans are instant.

Jellyfin and the file system are merged into one library, so the scheduling engine never knows there are two sources.

## Jellyfin metadata conventions

Jellyfin has no custom fields, so promo and targeting metadata rides on tags using a `prefix:value` convention. The prefixes are configurable with `Jellyfin:AttributeTagPrefixes`.

| Tag | Becomes |
| --- | --- |
| `promo_type:later_today` | attribute `promo_type = later_today` |
| `show_id:SpongeBob` | attribute `show_id = SpongeBob` |
| `trimmable:false` | attribute `trimmable = false` (per-item override of the filler tier default) |
| `stop_before_end:true` | attribute `stop_before_end = true` |

Everything else stays a plain tag (`Commercials`, `Adult Swim`, `Any Time`, `January`, `01-10`, …).

- Collection membership is read from BoxSets, and a series' collections are inherited by its episodes.
- Episodes stored directly in a `Specials` folder are treated as season 0, even if Jellyfin assigns them to a regular season. This means **Skip specials and extras** applies to them.

### Choosing Jellyfin libraries

**Settings → Libraries** chooses which Jellyfin libraries are used. Untick one to hide its content everywhere. This fixes a show appearing twice because a DVR or cache library holds its own copy.

## App settings reference

Each section of **App settings**, and the configuration keys it writes:

### This computer

Web UI port (default 5252), allow other devices, start at sign-in.

- `Server:Port`
- `Server:AllowRemote`

### Jellyfin

Server address, API key, optional user ID.

- `Jellyfin:BaseUrl`
- `Jellyfin:ApiKey`
- `Jellyfin:UserId`

### ErsatzTV Next

Manage the process, program, lineup folder, streaming port. See [ErsatzTV Next](ersatztv.md).

- `ErsatzTv:ManageProcess`
- `ErsatzTv:ExecutablePath`
- `ErsatzTv:ConfigRoot`
- `ErsatzTv:Lineup:Port`

### Voices (optional plugins)

MiniMax API key, Piper program and voices folder. Set these from each plugin's gear on the **Plugins** page.

- `Plugins:tts-minimax:ApiKey`
- `Plugins:tts-piper:ExecutablePath`
- `Plugins:tts-piper:VoicesPath`

The older `MiniMax:` and `Piper:` sections still work.

### ffmpeg

ffmpeg and ffprobe programs.

- `GeneratedPromos:FfmpegPath`
- `GeneratedPromos:FfprobePath`
- `FileSystemLibrary:FfprobePath`

### Commercials library

Folders, tags, promo folder name.

- `FileSystemLibrary:Folders`

### Hardware and video encoding

Blank uses this computer's default.

- **Hardware profile:** `Auto`, `LowPower` or `Standard` (see [Raspberry Pi](installation.md#raspberry-pi-and-other-low-power-devices)). Key: `Hardware:Profile`.
- **Live page channels:** encoder, x264 preset, height (360/480/540/720), frame rate (15/24/25/30), bitrate and threads. Keys: `LiveEncoding:Encoder`, `LiveEncoding:Preset`, `LiveEncoding:Height`, `LiveEncoding:FrameRate`, `LiveEncoding:BitrateKbps`, `LiveEncoding:Threads`.
- **Rendered clips** (generated promos, credit squeezes): encoder, preset, threads and hardware bitrate. Keys: `Rendering:Encoder`, `Rendering:Preset`, `Rendering:Threads`, `Rendering:HardwareBitrateKbps`.

## Other ways to set app settings

`settings.json` uses the same layout as `appsettings.json`. So anything above can also come from:

- `appsettings.json`
- user secrets
- environment variables (`Jellyfin__ApiKey`)
- a `.env` file (`MINIMAX_API_KEY`)

`settings.json` is applied last and wins.

Other options without a UI, such as `ErsatzTv:PathMappings`, `ErsatzTv:GuideMode`, `Jellyfin:AttributeTagPrefixes` and `Scheduler:ApiKey`, can be set in `settings.json` by hand.

Relative paths are resolved against the data folder.
