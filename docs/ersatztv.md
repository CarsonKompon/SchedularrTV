# ErsatzTV Next

[ErsatzTV Next](https://github.com/ErsatzTV/next) does the transcoding and streaming. It has no
management API; it is file-driven, so SchedularrTV writes its playouts, guide and lineup as files.

## Running it with SchedularrTV

Under **App settings → ErsatzTV Next**, tick *Start and stop ErsatzTV Next together with the scheduler*
and choose the `ersatztv` program. SchedularrTV then:

- starts it once the first playouts are written,
- restarts it if it stops, or when the channel list changes (Next reads its channels once at startup),
- stops it, with its whole ffmpeg process tree, when the scheduler stops.

ErsatzTV's output goes into the scheduler's log. The page shows whether it is running, plus the playlist
(`http://localhost:8409/channels.m3u`) and guide (`http://localhost:8409/xmltv.xml`) addresses for your
IPTV player.

The equivalent `settings.json`:

```jsonc
"ErsatzTv": {
  "ManageProcess": true,
  "ExecutablePath": "/opt/ersatztv/ersatztv",
  "ConfigRoot": "/srv/ersatztv/config",
  "Lineup": { "Port": 8409 }
}
```

## The lineup folder

ErsatzTV Next has no config folder of its own: it's started with the path of a `lineup.json`
(`ersatztv config/lineup.json`). SchedularrTV writes that file, the playouts and the guide into its
**Lineup folder**, `etv/config` in the data folder (`%LOCALAPPDATA%\SchedularrTV\etv\config` on Windows)
unless you choose another. When SchedularrTV manages ErsatzTV, it starts it with that lineup.

If you start ErsatzTV yourself with a lineup of your own, the Lineup folder must be the folder that
lineup is in, or ErsatzTV never sees the playouts and guide. Choosing the ErsatzTV program in App settings
picks up a lineup already beside it (such as `config/lineup.json`, where `ersatztv add-lineup` puts one).

## Checking the guide

**Check guide** (App settings → ErsatzTV Next) asks ErsatzTV for its playlist and guide the way Jellyfin
does, and lists how many listings each channel has. An empty guide there usually means ErsatzTV is running
with a different lineup; listings there but not in Jellyfin mean Jellyfin hasn't fetched them yet. In
Jellyfin, check the XMLTV guide source (Dashboard → Live TV) and run **Dashboard → Scheduled Tasks →
Refresh Guide**.

## Watching a channel

The play button on each channel on the **Channels** page opens a live preview of what ErsatzTV is sending for
that channel. The guide can look fine while channels don't play; this checks the picture itself. If the channel
doesn't start, the preview says why (no such channel in ErsatzTV's lineup, ErsatzTV not answering, or ErsatzTV's
own error, with more in the scheduler's log).

The stream is relayed through the scheduler (`/api/ersatztv/…`), so it works from any computer that can open
SchedularrTV, without opening ErsatzTV's port. Browsers that can't play HLS themselves (most besides Safari) use
[hls.js](https://github.com/video-dev/hls.js), loaded from cdn.jsdelivr.net when a preview first opens. While a
preview is open it counts as a viewer, like Jellyfin would.

## What is written

`ConfigRoot` (the **Lineup folder**, `etv/config` in the data folder by default) receives:

| Path | Contents | Owner |
| --- | --- | --- |
| `lineup.json` | Channel list, generated from the schedule so `tvg_id` can't drift from the guide files (`ErsatzTv:WriteLineup`, on by default). | Generated every rebuild; hand edits are overwritten. |
| `channels/{number}/playout/{start}_{finish}.json` | Playout document ([schema](https://github.com/ErsatzTV/next/blob/main/schema/playout.json)). The compact ISO-8601 file name is how Next finds the window for the current time. | Generated every rebuild. |
| `xmltv/{tvg_id}.xml` | Guide fragment. Next builds the `<channel>` elements from the lineup and copies the `<programme>` elements from these. | Generated every rebuild. |
| `channels/{number}/channel.json` | Per-channel encoding settings. | Created once, then left alone: **edit this** for resolution and hardware encoding. The only change ever made to it is its schema `version` (see below). |
| `hls/` | ErsatzTV's segment scratch space. | ErsatzTV. |

Next then serves `/channels.m3u`, `/channel/{n}.m3u8` and `/xmltv.xml`.

## Channel config versions

ErsatzTV Next versions its `channel.json` files, and current builds refuse one without a `version`
("missing schema version (read as 0.0.0) is no longer supported"). When that happens the guide still
works but no channel plays: Jellyfin shows the listings and then fails to tune.

On every rebuild, SchedularrTV brings each channel's `channel.json` up to the version it writes
(`https://ersatztv.org/channel/version/0.1.0`), keeping the original as `channel.json.bak`. Nothing else in the
file changes, except that a `"format": null` (which used to mean "copy") becomes `"mode": "copy"`, the way 0.1.0
says it. Files that already have a 0.1 or later version are left alone. If you run ErsatzTV with a lineup of
your own (`WriteLineup` off), add the `version` line to its channel files yourself.

## Details

- Trimmed filler carries `out_point_ms`, so the asset stops on the boundary instead of running long.
- `Padding` entries become a black, silent `lavfi` item rather than a hole in the timeline.
- `ErsatzTv:PathMappings` rewrites Jellyfin's media paths to what ErsatzTV sees. It's needed whenever the
  two run in different containers or on different machines:

  ```jsonc
  "ErsatzTv": { "PathMappings": [ { "From": "/media", "To": "D:\\Media" } ] }
  ```

- `ErsatzTv:GuideMode` defaults to `Programs`, which stretches each programme over the ad break that
  follows it. `All` lists every commercial and bumper, which is only useful for debugging.
- `ErsatzTv:ArtworkBaseUrl` is where guide artwork is fetched from; it defaults to the Jellyfin address
  and must be reachable by your players.
- **Only render for channels someone is watching** (Settings → Generated promos) uses ErsatzTV's stream
  activity, so paid voices are only spent on channels with a viewer.
