# IPTV passthrough

Carry channels from an IPTV provider, or free channels from iptv-org, alongside your own channels in the same lineup and guide.

## What it is

The IPTV plugin (`iptv`) carries channels from an IPTV provider (an M3U playlist and its XMLTV guide) alongside your own channels.

- They go into the same ErsatzTV lineup, so Jellyfin sees them on the same tuner and in the same guide, with the provider's own listings.
- Nothing changes on the Jellyfin side.

## Requirements

- ErsatzTV Next with `WriteLineup` on (the default).
- ffmpeg.

The plugin is optional: install `SchedularrTV-<version>-plugin-iptv.zip` (or the all-plugins zip) from the **Plugins** page.

## Adding a provider

Open **IPTV channels** in the sidebar and add a provider:

1. Give it a name and its **playlist** address. This is the M3U link your provider gives you (for Xtream Codes providers, `http://host/get.php?username=…&password=…&type=m3u_plus&output=ts`), or a `.m3u` file on this computer.
2. Set the **guide** address. It's optional when the playlist names one (`url-tvg` in its first line). Otherwise give its XMLTV link (`http://host/xmltv.php?username=…&password=…`) or file; `.xml.gz` is fine.
3. Choose **Load channels**, then tick the ones to carry. You can filter by name or group, and carry or drop everything shown, or tick **Carry every channel in the playlist**.
4. Choose the **first channel number**. Carried channels are numbered from there, skipping numbers already given. They keep their number from then on, even when the provider reorders the playlist.
5. Save. The playlist and guide are read, and the channels reach ErsatzTV in a moment (it restarts to pick up the new channel list).

### Pass video and audio through

Leave **Pass video and audio through** on unless a provider's streams cause trouble in your players.

- **On:** the provider's video and audio are handed on as they are, which costs almost nothing. ErsatzTV still re-encodes anything it can't pass on.
- **Off:** each channel is re-encoded to its `channel.json` settings, like your own channels.

## Free channels and guides from iptv-org

[iptv-org](https://github.com/iptv-org/iptv) collects publicly available channels from around the world, and [iptv-org/epg](https://github.com/iptv-org/epg) grabs their listings from TV listings sites.

### Adding channels from iptv-org

**Add channels from iptv-org** (or the box at the top of any provider's editor) picks one of iptv-org's playlists instead of an address: a country, a category (News, Kids…), a language, or everything. Then load the channels and tick the ones to carry as usual.

> [!NOTE]
> iptv-org's streams come and go, so expect some not to play.

### Making the guide with iptv-org/epg

**Make the guide with iptv-org/epg** makes the guide for the carried channels. It's on by default for iptv-org playlists, and available for any provider.

For each carried channel, the plugin looks up the listings sites iptv-org knows for it in iptv-org's database. It matches by the playlist's `tvg-id` (`CNN.us`, or `CNN.us@East` for a feed), or else the channel's name. It then runs iptv-org/epg's grabber for those channels and reads the result in like any other guide.

- **Setup.** The first time, the plugin sets the grabber up in its data folder (`plugin-data/iptv/iptv-org`).
  - It uses the Node.js installed on this computer (20 or newer), or else downloads a copy just for this plugin.
  - It also downloads iptv-org/epg and installs its packages. That's a few hundred MB and a few minutes; the IPTV channels page shows progress.
  - iptv-org/epg is downloaded again weekly, as listings sites change.
- **Grabbing.** The guide is grabbed every 12 hours, and straight away when the carried channels change. **Grab now** does it on demand. The provider shows how many channels got listings.
- **Missing listings.** A channel whose site gave nothing is tried on another site next time. Channels iptv-org has no listings site for show their name, unless a guide address is also set: then that guide fills them in. A guide named in the playlist itself isn't used.

## Channel numbers

A channel whose number is already used by one of your own channels is left out, and the IPTV channels page marks it **Number taken**. Your own channels always win.

To fix it, change the provider's first number or your channel's number.

> [!TIP]
> Give each provider its own block of numbers. The page suggests 1000, 2000, and so on.

## How it works

- **The guide** is the provider's. Each listing is copied into ErsatzTV's guide as the provider wrote it (title, description, artwork, categories, episode numbers), only moved onto the channel's guide id (`iptv.<provider>.<number>`).
  - Channels are matched to the guide by `tvg-id`, then by name.
  - A channel with no listings appears under its own name.
- **The picture.** When a channel is watched, ErsatzTV asks the scheduler for it (`http://<scheduler>:5252/plugins/iptv/live/<provider>/<number>.ts`). The scheduler relays the provider's stream through ffmpeg, copying it into MPEG-TS.
  - HLS (`.m3u8`) and MPEG-TS streams both work, with the user agent and referrer the playlist asks for.
  - Everyone watching a channel shares one connection to the provider.
- **Holding streams open.** ErsatzTV reconnects at each programme change, so a stream is held open for a moment after its last viewer leaves (30 seconds by default). That also matters for providers that allow only one connection at a time.
- **Refreshing.** Playlists and guides are read again every 6 hours (see the settings), when you save, and on **Refresh now**. The channels are published again each time, and on every schedule rebuild.
- **Not planned.** Carried channels aren't planned: they're the provider's schedule, as it is. They're listed on the **Channels** page and the dashboard (marked *IPTV*), where **Edit** brings you back to the IPTV channels page.

## Settings

| Setting | What it does |
| --- | --- |
| **ffmpeg** | The ffmpeg that relays streams. Found automatically when empty. |
| **Stream address** | Where ErsatzTV fetches the relayed streams from. Set it when ErsatzTV runs on another computer or in a container, e.g. `http://192.168.1.20:5252`. |
| **Refresh playlists and guides every** | Hours between reads (1–48, default 6). |
| **Keep a stream open after its last viewer** | Seconds (default 30). |
| **iptv-org guides: grab every** | Hours between grabs (2–168, default 12). |
| **iptv-org guides: days of listings** | 1–14 (default 2). More takes longer to grab. |
| **iptv-org guides: requests at once** | 1–20 (default 3). Higher is faster, but some listings sites block busy callers. |
| **Node.js** | A Node.js of your own for the grabber. Empty uses the one installed, or downloads one. |

## Troubleshooting

### The channels don't appear in Jellyfin

Check they're in `http://localhost:8409/channels.m3u`.

- If they are, refresh Jellyfin's guide (**Dashboard → Scheduled Tasks → Refresh Guide**).
- If not, look for **Number taken** on the IPTV page, and for a playlist problem under the provider.

### No listings

The guide doesn't list the channel under its `tvg-id` or name. Check the guide address, or that the provider's guide covers the channel.

### The iptv-org guide says it couldn't be made

The message under the provider has the grabber's last words. Most often the setup couldn't download Node.js or iptv-org/epg (a firewall), or `npm install` failed.

- Set an installed Node.js in the settings, or
- Delete `plugin-data/iptv/iptv-org` to start the setup afresh.

### Few iptv-org listings

Listings sites change or block grabbers. iptv-org fixes them upstream, and the weekly re-download picks the fixes up. Lower **requests at once** if many channels come back empty.

### A channel won't play

The scheduler's log has ffmpeg's output for it ("IPTV" lines). Try the stream address from the playlist in VLC. If the provider wants a particular user agent, set it on the provider.

### Plays in VLC but not in Jellyfin

Turn passthrough off for that provider, so ErsatzTV re-encodes its streams.
