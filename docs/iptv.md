# IPTV passthrough

**IPTV passthrough** adds external M3U channels and XMLTV listings to the ErsatzTV lineup. Carried channels retain the provider's schedule.

## Add a provider

1. Install **IPTV passthrough** and restart.
2. Verify **ffmpeg** and ErsatzTV lineup generation.
3. Open **IPTV channels** and add a provider.
4. Enter a name and playlist URL or local **`.m3u`** path.
5. Enter an XMLTV URL or path when the playlist does not supply one.
6. Select **Load channels** and choose channels to carry.
7. Assign the **first channel number** and save.

XMLTV accepts **`.xml.gz`**. Managed ErsatzTV restarts when the channel list changes; externally managed ErsatzTV needs a manual restart.

## Select channel numbering and passthrough

Each provider assigns numbers from its starting number and remembers them across playlist reordering. A conflict with a scheduler channel excludes the carried channel and displays **Number taken**.

**Pass video and audio through** avoids unnecessary transcoding where supported. Disable it when streams work in VLC but fail in the target player.

## Add iptv-org channels

1. Select **Add channels from iptv-org**.
2. Choose a country, category, language, or complete playlist.
3. Select **Load channels** and choose the channels to carry.
4. Enable **Make the guide with iptv-org/epg** if required.
5. Save and monitor setup progress.

The guide integration uses Node.js **20+**, downloading a plugin-local copy when needed. Initial setup downloads the grabber and dependencies; guide generation defaults to every **12 hours**, with weekly grabber updates.

**Grab now** refreshes listings on demand. Matching uses **`tvg-id`**, then name; channels without available listings show their channel name.

## Configure refresh and streaming

| Setting | Default or purpose |
| --- | --- |
| **ffmpeg** | Detected executable override |
| **Stream address** | Scheduler URL reachable by ErsatzTV |
| **Refresh playlists and guides every** | 6 hours; range 1–48 |
| **Keep a stream open after its last viewer** | 30 seconds |
| **iptv-org guides: grab every** | 12 hours; range 2–168 |
| **iptv-org guides: days of listings** | 2; range 1–14 |
| **iptv-org guides: requests at once** | 3; range 1–20 |
| **Node.js** | Override the installed or downloaded runtime |

The endpoint is **`/plugins/iptv/live/<provider>/<number>.ts`**. Viewers share one upstream connection per carried channel; the relay supports HLS and MPEG-TS plus playlist user-agent/referrer settings.

**Refresh now** reloads provider data. **Channels → Edit** opens the provider's editor.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| **Channel absent from playlist** | Check provider errors and **Number taken** |
| **Channel absent from Jellyfin only** | Refresh Jellyfin's guide |
| **No listings** | Verify XMLTV coverage and matching `tvg-id` or name |
| **iptv-org setup fails** | Read the provider error and verify Node.js/download access |
| **Few generated listings** | Reduce concurrent requests and check upstream listings-site support |
| **Stream fails** | Test the source URL in VLC and inspect IPTV ffmpeg log output |
| **Remote ErsatzTV cannot connect** | Correct **Stream address** and network access |
