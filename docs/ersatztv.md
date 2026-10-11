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

## 24/7 channels

ErsatzTV starts a channel when a player asks for it and stops it 90 seconds after the last request. Ticking
**Play around the clock (24/7)** on a channel's General tab (`always_on: true` in the YAML) keeps it playing all
the time instead: SchedularrTV asks ErsatzTV for the channel's playlists every 20 seconds, the way a player would,
without downloading the video. Tuning in is then instant and lands mid-programme, but ErsatzTV transcodes the
channel non-stop, so check the **Performance** page for what it costs.

Keeping a channel open looks the same to ErsatzTV as someone watching, so its heartbeat can no longer tell real
viewers apart. Each 24/7 channel says how the watched-only features (generated promos, credit squeezing, chapter
scanning) should treat it: always watched (the default) or never watched
(`always_on_counts_as_watched: false`). A 24/7 channel is never counted as a direct viewer in the viewing stats;
viewers through Jellyfin or Plex still are.

```yaml
channels:
- id: news
  name: News
  number: 3
  always_on: true
  always_on_counts_as_watched: false   # optional; leave out to count it as watched
```

Only the channels defined in the schedule can be set to 24/7; IPTV and streamer channels from plugins start on
demand as before.

## Encoding and hardware acceleration

**App settings › Hardware and video encoding › ErsatzTV channels** sets the defaults for how ErsatzTV encodes every
channel: on the processor or a graphics card (NVIDIA `cuda`, Intel `qsv`, AMD `amf`, Linux `vaapi`, Mac
`videotoolbox`, Rockchip `rkmpp`, `vulkan`), the picture size and the bitrate. Until they're changed they're the
processor, 1080p and 6000 kbps (720p and 3000 kbps on a Raspberry Pi), as a new `channel.json` has them. A channel can
override any of them on its General tab (`encoding:` in the YAML); what it leaves out takes the default.

Channels made from one source keep that source's picture size instead of the default, and use the default encoder
and bitrate:

- **IPTV** channels take the size of the provider's stream, which the relay reads from ffmpeg the first time the
  channel plays and remembers (`iptv-pictures.json` in the data folder). Until then they use the default size. A
  passed-through (copied) stream isn't re-encoded at all; the size only matters for anything that can't be copied.
- **Streamer** channels take the size their picture is made at (App settings, Live page channels).

```yaml
channels:
- id: news
  name: News
  number: 3
  encoding:
    hardware: none      # this one on the processor, whatever the app-wide setting
    height: 720
    bitrate_kbps: 3000
```

`channel.json` is never rewritten. The settings go into an overlay in `overlays/` (named after its content) that the
lineup lists for each channel, which ErsatzTV merges over `channel.json`, so these settings win over any acceleration,
size or bitrate written there by hand (its other settings, such as audio, still apply). A changed setting changes the lineup, so
ErsatzTV restarts to pick it up and anyone watching drops for a few seconds. ErsatzTV checks for the hardware when a
channel starts and falls back to the processor if it isn't there, saying so in its log.

## Performance

**System → Performance** shows what SchedularrTV, ErsatzTV and the processes they start take of the computer,
every 5 seconds over the last 15 minutes:

- **SchedularrTV**: the app itself (and the tray, when it started the app).
- **ErsatzTV**: the server, its channel workers and an ffmpeg transcoder for each channel that's playing. Found
  from the process SchedularrTV started, or any running `ersatztv` program when it's started some other way.
- **Helpers**: what SchedularrTV starts itself, such as the hidden browsers and ffmpeg behind live page channels,
  IPTV relays and promo or credit renders.

It also shows the whole computer's CPU and memory, NVIDIA graphics cards' load (including the video encoder) when
`nvidia-smi` is installed, and suggestions drawn from the last minute: how much CPU each playing channel costs and
how many more fit, how many channels are encoded on the processor, and when the
CPU, memory or video encoder is nearly full. The same numbers are at `GET /api/performance`.

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

- An entry's `ersatztv.graphics` annotation (a JSON array in ErsatzTV's `graphics` format, as the Overlays plugin
  writes it) becomes the item's graphics layers.
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
