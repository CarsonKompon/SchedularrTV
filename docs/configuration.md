# Configuration

SchedularrTV has two kinds of settings:

| What | Where | Edited in |
| --- | --- | --- |
| Channels, blocks, breaks, promos: what plays and when | `<data folder>/config/schedule.yaml` | The web UI (Channels, Collections, Settings) or the **YAML** page. Hot-reloads on save. |
| Connections, keys and programs | `<data folder>/settings.json` | **App settings**. Applied as soon as they're saved, except the web UI port and network access, which take a restart. |

`config/schedule.example.yaml` in the repository is a full-featured reference schedule. A new install
starts from an empty schedule with only global settings.

## App settings

| Section | Settings | Configuration keys |
| --- | --- | --- |
| This computer | Web UI port (default 5252), allow other devices, start at sign-in | `Server:Port`, `Server:AllowRemote` |
| Jellyfin | Server address, API key, optional user ID | `Jellyfin:BaseUrl`, `Jellyfin:ApiKey`, `Jellyfin:UserId` |
| ErsatzTV Next | Manage the process, program, lineup folder, streaming port | `ErsatzTv:ManageProcess`, `ErsatzTv:ExecutablePath`, `ErsatzTv:ConfigRoot`, `ErsatzTv:Lineup:Port` |
| Voices (optional plugins) | MiniMax API key, Piper program and voices folder, from each plugin's gear on the Plugins page | `Plugins:tts-minimax:ApiKey`, `Plugins:tts-piper:ExecutablePath`, `Plugins:tts-piper:VoicesPath` (the older `MiniMax:` and `Piper:` sections still work) |
| ffmpeg | ffmpeg and ffprobe programs | `GeneratedPromos:FfmpegPath`, `GeneratedPromos:FfprobePath`, `FileSystemLibrary:FfprobePath` |
| Commercials library | Folders, tags, promo folder name | `FileSystemLibrary:Folders` |

`settings.json` uses the same layout as `appsettings.json`, so anything in the table can also come from
`appsettings.json`, user secrets, environment variables (`Jellyfin__ApiKey`) or a `.env` file
(`MINIMAX_API_KEY`). `settings.json` is applied last and wins.

Other options without a UI, such as `ErsatzTv:PathMappings`, `ErsatzTv:GuideMode`,
`Jellyfin:AttributeTagPrefixes` and `Scheduler:ApiKey`, can be set in `settings.json` by hand.

Relative paths are resolved against the data folder.

## Adding a channel

Channels are normally added in the web UI. In YAML, append to `channels:`. `number` must be unique: it
becomes the ErsatzTV channel folder, and `tvg_id` binds the guide to the stream.

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

The scheduler creates `channels/2/channel.json` and its playout folder, and rewrites `lineup.json`.
ErsatzTV Next reads its channel list once at startup; when SchedularrTV manages it, it is restarted
automatically. Schedule changes to an existing channel are picked up live.

## Copying channel settings

Open the destination channel's editor, choose **Copy From**, select a source channel and confirm
**Copy settings**. This replaces its scheduling settings (collection references, blocks, anchors,
Follow mappings, breaks and promos) while keeping the destination's name, number, ID and logo.
Settings are independent copies; global collections remain shared. **Save** applies the changes and
**Discard** undoes them. Playback history is not copied or reset.

## Commercials on disk

Commercials rarely belong in a media server. The file system library treats a folder tree as a tag
hierarchy, so **organising the files is tagging them**:

```
Commercials/Adult Swim/Bumpers/Any Time/clip.mp4
            <Channel>  <Type>  <When>
```

becomes an item tagged `Commercials`, `Adult Swim`, `Bumpers`, `Any Time`, reachable with the same
[query language](scheduling.md#query-language) as everything else:

```
tag:"Commercials" AND (tag:"Adult Swim" OR tag:"Any Channel")
  AND tag:"Bumpers" AND (tag:"Any Time" OR tag:"January" OR tag:"Winter")
```

Add folders under **App settings → Commercials library**, or in `settings.json`:

```jsonc
"FileSystemLibrary": {
  "Folders": [
    { "Path": "/media/Commercials", "Tags": [ "Commercials" ], "TagFromFolders": true, "PromoFolder": "Promos" }
  ]
}
```

Beneath a folder named by `PromoFolder`, the levels become promo metadata:
`<promo folder>/<show>/<promo type>`. A folder named `key=value` becomes an attribute rather than a tag.

Durations come from ffprobe and are cached by size and modified time, so a 10k-file library is probed
once and rescans are instant. Jellyfin and the file system are merged into one library, so the
scheduling engine never knows there are two sources.

## Jellyfin metadata conventions

Jellyfin has no custom fields, so promo and targeting metadata rides on tags using a `prefix:value`
convention (configurable with `Jellyfin:AttributeTagPrefixes`):

| Tag | Becomes |
| --- | --- |
| `promo_type:later_today` | attribute `promo_type = later_today` |
| `show_id:SpongeBob` | attribute `show_id = SpongeBob` |
| `trimmable:false` | attribute `trimmable = false` (per-item override of the filler tier default) |
| `stop_before_end:true` | attribute `stop_before_end = true` |

Everything else stays a plain tag (`Commercials`, `Adult Swim`, `Any Time`, `January`, `01-10`, …).
Collection membership is read from BoxSets, and a series' collections are inherited by its episodes.

Episodes stored directly in a `Specials` folder are treated as season 0 even if Jellyfin assigns them
to a regular season, so **Skip specials and extras** applies to them.

**Settings → Libraries** chooses which Jellyfin libraries are used. Untick one to hide its content
everywhere, which fixes a show appearing twice because a DVR or cache library holds its own copy.
