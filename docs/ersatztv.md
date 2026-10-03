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

## What is written

`ConfigRoot` (the **Lineup folder**, `etv/config` in the data folder by default) receives:

| Path | Contents | Owner |
| --- | --- | --- |
| `lineup.json` | Channel list, generated from the schedule so `tvg_id` can't drift from the guide files (`ErsatzTv:WriteLineup`, on by default). | Generated every rebuild; hand edits are overwritten. |
| `channels/{number}/playout/{start}_{finish}.json` | Playout document ([schema](https://github.com/ErsatzTV/next/blob/main/schema/playout.json)). The compact ISO-8601 file name is how Next finds the window for the current time. | Generated every rebuild. |
| `xmltv/{tvg_id}.xml` | Guide fragment. Next builds the `<channel>` elements from the lineup and copies the `<programme>` elements from these. | Generated every rebuild. |
| `channels/{number}/channel.json` | Per-channel encoding settings. | Created once, then left alone: **edit this** for resolution and hardware encoding. |
| `hls/` | ErsatzTV's segment scratch space. | ErsatzTV. |

Next then serves `/channels.m3u`, `/channel/{n}.m3u8` and `/xmltv.xml`.

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
