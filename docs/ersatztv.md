# ErsatzTV Next

ErsatzTV Next does the transcoding and streaming for SchedularrTV's channels. SchedularrTV can start and stop it for you, and writes its playouts, guide and lineup as files.

## How it works

[ErsatzTV Next](https://github.com/ErsatzTV/next) does the transcoding and streaming. It has no management API. It is file-driven, so SchedularrTV writes its playouts, guide and lineup as files.

## Running it with SchedularrTV

1. Open **App settings → ErsatzTV Next**.
2. Tick *Start and stop ErsatzTV Next together with the scheduler*.
3. Choose the `ersatztv` program.

SchedularrTV then:

- starts it once the first playouts are written
- restarts it if it stops, or when the channel list changes (Next reads its channels once at startup)
- stops it, with its whole ffmpeg process tree, when the scheduler stops

ErsatzTV's output goes into the scheduler's log.

The page shows whether ErsatzTV is running, plus the addresses for your IPTV player:

- Playlist: `http://localhost:8409/channels.m3u`
- Guide: `http://localhost:8409/xmltv.xml`

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

ErsatzTV Next has no config folder of its own. It's started with the path of a `lineup.json`:

```sh
ersatztv config/lineup.json
```

SchedularrTV writes that file, the playouts and the guide into its **Lineup folder**. Unless you choose another, this is `etv/config` in the data folder (`%LOCALAPPDATA%\SchedularrTV\etv\config` on Windows). When SchedularrTV manages ErsatzTV, it starts it with that lineup.

### Using your own lineup

If you start ErsatzTV yourself with a lineup of your own, the Lineup folder must be the folder that lineup is in. Otherwise ErsatzTV never sees the playouts and guide.

> [!TIP]
> Choosing the ErsatzTV program in App settings picks up a lineup already beside it, such as `config/lineup.json`, where `ersatztv add-lineup` puts one.

## Watching a channel

The play button on each channel on the **Channels** page opens a live preview of what ErsatzTV is sending for that channel.

The guide can look fine while channels don't play; this checks the picture itself. If the channel doesn't start, the preview says why:

- no such channel in ErsatzTV's lineup
- ErsatzTV not answering
- ErsatzTV's own error, with more in the scheduler's log

The stream is relayed through the scheduler (`/api/ersatztv/…`), so it works from any computer that can open SchedularrTV, without opening ErsatzTV's port.

Browsers that can't play HLS themselves (most besides Safari) use [hls.js](https://github.com/video-dev/hls.js), loaded from cdn.jsdelivr.net when a preview first opens.

> [!NOTE]
> While a preview is open, it counts as a viewer, like Jellyfin would.

## Checking the guide

**Check guide** (**App settings → ErsatzTV Next**) asks ErsatzTV for its playlist and guide the way Jellyfin does, and lists how many listings each channel has.

- **An empty guide there** usually means ErsatzTV is running with a different lineup.
- **Listings there but not in Jellyfin** mean Jellyfin hasn't fetched them yet. In Jellyfin, check the XMLTV guide source (**Dashboard → Live TV**) and run **Dashboard → Scheduled Tasks → Refresh Guide**.

## 24/7 channels

ErsatzTV starts a channel when a player asks for it, and stops it 90 seconds after the last request.

Ticking **Play around the clock (24/7)** on a channel's **General** tab (`always_on: true` in the YAML) keeps it playing all the time instead. SchedularrTV asks ErsatzTV for the channel's playlists every 20 seconds, the way a player would, without downloading the video.

Tuning in is then instant and lands mid-programme. But ErsatzTV transcodes the channel non-stop, so check the **Performance** page for what it costs.

### How 24/7 channels count as watched

Keeping a channel open looks the same to ErsatzTV as someone watching, so its heartbeat can no longer tell real viewers apart.

Each 24/7 channel says how the watched-only features (generated promos, credit squeezing, chapter scanning) should treat it:

- always watched (the default)
- never watched (`always_on_counts_as_watched: false`)

A 24/7 channel is never counted as a direct viewer in the viewing stats. Viewers through Jellyfin or Plex still are.

```yaml
channels:
- id: news
  name: News
  number: 3
  always_on: true
  always_on_counts_as_watched: false   # optional; leave out to count it as watched
```

> [!NOTE]
> Only the channels defined in the schedule can be set to 24/7. IPTV and streamer channels from plugins start on demand as before.

## Encoding and hardware acceleration

**App settings › Hardware and video encoding › ErsatzTV channels** sets the defaults for how ErsatzTV encodes every channel:

- **Where:** on the processor or a graphics card (NVIDIA `cuda`, Intel `qsv`, AMD `amf`, Linux `vaapi`, Mac `videotoolbox`, Rockchip `rkmpp`, `vulkan`)
- **Picture size**
- **Bitrate**

Until they're changed, they're the processor, 1080p and 6000 kbps (720p and 3000 kbps on a Raspberry Pi), as a new `channel.json` has them.

A channel can override any of them on its **General** tab (`encoding:` in the YAML). What it leaves out takes the default.

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

### IPTV and streamer channels

Channels made from one source keep that source's picture size instead of the default, and use the default encoder and bitrate:

- **IPTV** channels take the size of the provider's stream. The relay reads it from ffmpeg the first time the channel plays and remembers it (`iptv-pictures.json` in the data folder). Until then they use the default size. A passed-through (copied) stream isn't re-encoded at all; the size only matters for anything that can't be copied.
- **Streamer** channels take the size their picture is made at (App settings, Live page channels).

### How the settings are applied

`channel.json` is never rewritten. Instead, the settings go into an overlay in `overlays/` (named after its content) that the lineup lists for each channel. ErsatzTV merges it over `channel.json`.

- These settings win over any acceleration, size or bitrate written in `channel.json` by hand. Its other settings, such as audio, still apply.
- A changed setting changes the lineup, so ErsatzTV restarts to pick it up. Anyone watching drops for a few seconds.
- ErsatzTV checks for the hardware when a channel starts, and falls back to the processor if it isn't there, saying so in its log.

## Performance

**System → Performance** shows what SchedularrTV, ErsatzTV and the processes they start take of the computer, every 5 seconds over the last 15 minutes:

- **SchedularrTV**: the app itself (and the tray, when it started the app).
- **ErsatzTV**: the server, its channel workers and an ffmpeg transcoder for each channel that's playing. Found from the process SchedularrTV started, or any running `ersatztv` program when it's started some other way.
- **Helpers**: what SchedularrTV starts itself, such as the hidden browsers and ffmpeg behind live page channels, IPTV relays and promo or credit renders.

It also shows:

- the whole computer's CPU and memory
- NVIDIA graphics cards' load (including the video encoder), when `nvidia-smi` is installed
- suggestions drawn from the last minute: how much CPU each playing channel costs and how many more fit, how many channels are encoded on the processor, and when the CPU, memory or video encoder is nearly full

The same numbers are at `GET /api/performance`.

## Path mappings and guide options

These options are set in `settings.json` by hand.

### `ErsatzTv:PathMappings`

Rewrites Jellyfin's media paths to what ErsatzTV sees. It's needed whenever the two run in different containers or on different machines:

```jsonc
"ErsatzTv": { "PathMappings": [ { "From": "/media", "To": "D:\\Media" } ] }
```

### `ErsatzTv:GuideMode`

- `Programs` (default) stretches each programme over the ad break that follows it.
- `All` lists every commercial and bumper, which is only useful for debugging.

### `ErsatzTv:ArtworkBaseUrl`

Where guide artwork is fetched from. It defaults to the Jellyfin address and must be reachable by your players.

## What is written

`ConfigRoot` (the **Lineup folder**, `etv/config` in the data folder by default) receives the files below. Next then serves `/channels.m3u`, `/channel/{n}.m3u8` and `/xmltv.xml`.

### `lineup.json`

- **Contents:** the channel list, generated from the schedule so `tvg_id` can't drift from the guide files (`ErsatzTv:WriteLineup`, on by default).
- **Owner:** generated every rebuild; hand edits are overwritten.

### `channels/{number}/playout/{start}_{finish}.json`

- **Contents:** the playout document ([schema](https://github.com/ErsatzTV/next/blob/main/schema/playout.json)). The compact ISO-8601 file name is how Next finds the window for the current time.
- **Owner:** generated every rebuild.

### `xmltv/{tvg_id}.xml`

- **Contents:** a guide fragment. Next builds the `<channel>` elements from the lineup and copies the `<programme>` elements from these.
- **Owner:** generated every rebuild.

### `channels/{number}/channel.json`

- **Contents:** per-channel encoding settings.
- **Owner:** created once, then left alone. **Edit this** for resolution and hardware encoding. The only change ever made to it is its schema `version` (see [Channel config versions](#channel-config-versions)).

### `hls/`

- **Contents:** ErsatzTV's segment scratch space.
- **Owner:** ErsatzTV.

## Channel config versions

ErsatzTV Next versions its `channel.json` files, and current builds refuse one without a `version`:

```text
missing schema version (read as 0.0.0) is no longer supported
```

When that happens, the guide still works but no channel plays: Jellyfin shows the listings and then fails to tune.

On every rebuild, SchedularrTV brings each channel's `channel.json` up to the version it writes (`https://ersatztv.org/channel/version/0.1.0`), keeping the original as `channel.json.bak`.

- Nothing else in the file changes, except that a `"format": null` (which used to mean "copy") becomes `"mode": "copy"`, the way 0.1.0 says it.
- Files that already have a 0.1 or later version are left alone.

> [!IMPORTANT]
> If you run ErsatzTV with a lineup of your own (`WriteLineup` off), add the `version` line to its channel files yourself.

## Other details

- An entry's `ersatztv.graphics` annotation (a JSON array in ErsatzTV's `graphics` format, as the Overlays plugin writes it) becomes the item's graphics layers.
- Trimmed filler carries `out_point_ms`, so the asset stops on the boundary instead of running long.
- `Padding` entries become a black, silent `lavfi` item rather than a hole in the timeline.
- **Only render for channels someone is watching** (**Settings → Generated promos**) uses ErsatzTV's stream activity, so paid voices are only spent on channels with a viewer.
