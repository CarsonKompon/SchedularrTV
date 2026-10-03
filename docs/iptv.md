# IPTV passthrough

The IPTV plugin (`iptv`) carries channels from an IPTV provider (an M3U playlist and its XMLTV guide) alongside
your own channels. They go into the same ErsatzTV lineup, so Jellyfin sees them on the same tuner, in the same guide,
with the provider's own listings. Nothing changes on the Jellyfin side.

It's optional: install `SchedularrTV-<version>-plugin-iptv.zip` (or the all-plugins zip) from the **Plugins** page.
It needs ErsatzTV Next with `WriteLineup` on (the default), and ffmpeg.

## Adding a provider

Open **IPTV** in the sidebar and add a provider:

1. Give it a name and its **playlist** address: the M3U link your provider gives you (for Xtream Codes providers,
   `http://host/get.php?username=…&password=…&type=m3u_plus&output=ts`), or a `.m3u` file on this computer.
2. The **guide** address is optional when the playlist names one (`url-tvg` in its first line). Otherwise give its
   XMLTV link (`http://host/xmltv.php?username=…&password=…`) or file; `.xml.gz` is fine.
3. **Load channels**, then tick the ones to carry (filter by name or group, and carry or drop everything shown), or
   tick *Carry every channel in the playlist*.
4. Choose the **first channel number**. Carried channels are numbered from there, skipping numbers already given, and
   keep their number from then on, even when the provider reorders the playlist.
5. Save. The playlist and guide are read, and the channels reach ErsatzTV in a moment (it restarts to pick up the
   new channel list).

Leave **Pass video and audio through** on unless a provider's streams cause trouble in your players: it hands the
provider's video and audio on as they are, which costs almost nothing. ErsatzTV still re-encodes anything it can't
pass on. Off, each channel is re-encoded to its `channel.json` settings, like your own channels.

## How it works

- **The guide** is the provider's: each listing is copied into ErsatzTV's guide as the provider wrote it (title,
  description, artwork, categories, episode numbers), only moved onto the channel's guide id
  (`iptv.<provider>.<number>`). Channels are matched to the guide by `tvg-id`, then by name. A channel with no
  listings appears under its own name.
- **The picture**: when a channel is watched, ErsatzTV asks the scheduler for it
  (`http://<scheduler>:5252/plugins/iptv/live/<provider>/<number>.ts`), and the scheduler relays the provider's
  stream through ffmpeg, copying it into MPEG-TS. HLS (`.m3u8`) and MPEG-TS streams both work, with the user agent and
  referrer the playlist asks for. Everyone watching a channel shares one connection to the provider.
- ErsatzTV reconnects at each programme change, so a stream is held open for a moment after its last viewer leaves
  (30 seconds by default). That also matters for providers that allow only one connection at a time.
- Playlists and guides are read again every 6 hours (see the settings), when you save, and on **Refresh now**. The
  channels are published again each time, and on every schedule rebuild.
- Carried channels aren't on the Channels page and aren't planned: they're the provider's schedule, as it is.

## Channel numbers

A channel whose number is already used by one of your own channels is left out (the IPTV page marks it *Number
taken*); your own channels always win. Change the provider's first number or your channel's number. Give each
provider its own block of numbers (the page suggests 1000, 2000, and so on).

## Settings

| Setting | What it does |
| --- | --- |
| ffmpeg | The ffmpeg that relays streams. Found automatically when empty. |
| Stream address | Where ErsatzTV fetches the relayed streams from. Set it when ErsatzTV runs on another computer or in a container, e.g. `http://192.168.1.20:5252`. |
| Refresh playlists and guides every | Hours between reads (1–48, default 6). |
| Keep a stream open after its last viewer | Seconds (default 30). |

## Troubleshooting

- **The channels don't appear in Jellyfin.** Check they're in `http://localhost:8409/channels.m3u`. If they are,
  refresh Jellyfin's guide (Dashboard → Scheduled Tasks → Refresh Guide). If not, look for *Number taken* on the IPTV
  page, and for a playlist problem under the provider.
- **No listings.** The guide doesn't list the channel under its `tvg-id` or name. Check the guide address, or that
  the provider's guide covers the channel.
- **A channel won't play.** The scheduler's log has ffmpeg's output for it ("IPTV" lines). Try the stream address
  from the playlist in VLC; if the provider wants a particular user agent, set it on the provider.
- **Plays in VLC but not in Jellyfin.** Turn passthrough off for that provider, so ErsatzTV re-encodes its streams.
