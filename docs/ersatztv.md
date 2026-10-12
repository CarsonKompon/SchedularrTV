# ErsatzTV integration

**ErsatzTV Next** transcodes and streams schedules published by SchedularrTV. Integration uses lineup, playout, and XMLTV files.

## Manage the process

1. Open **App settings → ErsatzTV Next**.
2. Enable **Start and stop ErsatzTV Next together with the scheduler**.
3. Select the executable and **Lineup folder**.
4. Save and wait for the first schedule publication.
5. Select **Check guide** and test a channel's play button.

The managed process starts after playouts exist, restarts after failure or lineup changes, and stops with the scheduler. Its output goes to **`logs/server.log`**.

## Use an unmanaged process

Set **Lineup folder** to the directory containing the lineup ErsatzTV actually opens. Start ErsatzTV with **`ersatztv <lineup>`**:

```sh
ersatztv /srv/schedularrtv/etv/config/lineup.json
```

ErsatzTV reads the channel list at startup; restart it after channel-list or encoding-overlay changes. Existing-channel playout updates are read live.

## Watch and check the guide

| Resource | Default URL |
| --- | --- |
| **Playlist** | `http://localhost:8409/channels.m3u` |
| **Guide** | `http://localhost:8409/xmltv.xml` |
| **Channel stream** | `http://localhost:8409/channel/{number}.m3u8` |

The **Channels** play button proxies a live preview through SchedularrTV. It counts as a viewer; non-native HLS playback loads **hls.js** from jsDelivr.

**Check guide** tests the playlist and XMLTV response. If listings exist there but not in Jellyfin, verify Jellyfin's guide source and run **Dashboard → Scheduled Tasks → Refresh Guide**.

## Keep a channel running

Enable **General → Play around the clock (24/7)**, or set **`always_on: true`** on the channel. The scheduler requests playlists every 20 seconds to keep ErsatzTV encoding without downloading video.

**`always_on_counts_as_watched`** defaults to `true` for watched-only rendering features; set it to `false` to exclude the keepalive from those decisions. Keepalives never count as direct viewers in statistics.

Continuous playback consumes resources without viewers. Only schedule-defined channels support this setting; external IPTV and streamer channels remain on demand.

## Encoding and hardware acceleration

**App settings → Hardware and video encoding → ErsatzTV channels** controls the default encoder, picture size, and bitrate. Channel **General → Encoding** overrides individual fields.

```yaml
encoding:
  hardware: none
  height: 720
  bitrate_kbps: 3000
```

The default standard profile uses software encoding at **1080p, 6000 kbps**; low-power defaults use **720p, 3000 kbps**. Available hardware choices depend on ErsatzTV and the installed GPU.

SchedularrTV writes generated encoding overlays referenced by the lineup. They override matching hardware, height, and bitrate values in **`channel.json`**; other hand-edited channel settings remain effective.

Changing these overlays restarts managed ErsatzTV and briefly interrupts viewers. IPTV preserves source dimensions where known; streamer output follows the live-page encoding dimensions.

## Path mappings and guide options

Merge required options into **`settings.json`**:

```json
{
  "ErsatzTv": {
    "PathMappings": [
      { "From": "/media", "To": "D:\\Media" }
    ],
    "GuideMode": "Programs",
    "LocalArtworkBaseUrl": "http://192.168.1.20:5252"
  }
}
```

| Option | Purpose |
| --- | --- |
| **`PathMappings`** | Translate library media paths to paths visible to ErsatzTV |
| **`GuideMode: Programs`** | Extend programme listings over trailing breaks; default |
| **`GuideMode: All`** | List individual commercials and bumpers for diagnostics |
| **`ArtworkBaseUrl`** | Player-reachable Jellyfin artwork address |
| **`LocalArtworkBaseUrl`** | Player-reachable scheduler address for local artwork |
| **`WriteLineup`** | Generate the lineup; default true |

Live-source plugins also need their own **Stream address** when ErsatzTV runs on another computer or in a container. Generated graphics and rendered clips must be readable by ErsatzTV.

## Output ownership

The default output root is **`<data folder>/etv/config`**.

| Path | Ownership |
| --- | --- |
| **`lineup.json`** | Generated channel list; manual edits overwritten |
| **`channels/{number}/playout/{start}_{finish}.json`** | Generated timeline |
| **`xmltv/{tvg_id}.xml`** | Generated programme fragment |
| **`channels/{number}/channel.json`** | Created once; schema migration can update it |
| **`overlays/`** | Generated encoding overrides |
| **`hls/`** | ErsatzTV stream segments |

Trimmed filler uses source out-points. **Padding** becomes black video and silence; supported **`ersatztv.graphics`** annotations become graphics layers.

## Channel config versions

Older channel files can cause **missing schema version** errors while guide generation still works. Publication migrates generated channel files to **`https://ersatztv.org/channel/version/0.1.0`**, retaining **`channel.json.bak`**.

The migration also changes legacy **`"format": null`** to **`"mode": "copy"`**. With **`WriteLineup: false`**, maintain your own lineup and channel schema versions.

## Performance

Open **Performance** to inspect **SchedularrTV**, **ErsatzTV**, and **Helpers**, with CPU and memory samples every five seconds over fifteen minutes. Supported NVIDIA metrics use **nvidia-smi**.

**`GET /api/performance`** exposes the same current measurements and advice. See [Troubleshooting](troubleshooting.md#reduce-cpu-usage) for encoding adjustments.
