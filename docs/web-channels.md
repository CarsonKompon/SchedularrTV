# Web page channels

The web page channels plugin (`web-channels`) puts live web pages on your channels. A hidden browser shows the page
and its picture is streamed to ErsatzTV with your music, so what airs is always current. Use one for a 24/7 channel,
or for short segments between shows, the way cable channels used to.

Presets set up the pages that make good television:

| Preset | What it shows | Place |
| --- | --- | --- |
| **WeatherStar 4000+** | Retro local forecasts from [WeatherStar 4000+ International](https://github.com/mwood77/ws4kp-international), in the style of 1990s cable weather. | Required |
| **Clock** | Time and temperature: a big digital clock or a clock face for the place's time zone, its current conditions and an optional scrolling message. Served by the plugin itself. | Optional (time zone and weather) |
| **Weather radar (Windy)** | [Windy](https://www.windy.com)'s animated map: radar, wind, rain and thunder, temperature or clouds, centred on the place. | Required |
| **Earth wind map (nullschool)** | The [earth.nullschool.net](https://earth.nullschool.net) globe of the world's winds (or wind over temperature, or ocean currents). Lovely ambient filler with music. | Optional (where the globe faces) |
| **Custom web page** | Any address. `{lat}`, `{lon}`, `{location}` and `{name}` in it are filled in from the channel. | Optional |

It's optional: install `SchedularrTV-<version>-plugin-web-channels.zip` (or the all-plugins zip) from the **Plugins** page.

## Coming from the WeatherStar plugin

This plugin replaces the WeatherStar plugin (`ws4kp`). The first time it starts it takes over your weather stations as
WeatherStar channels (same names and settings), and uses the WeatherStar plugin's settings until you set its own.
Then:

1. Remove the **WeatherStar 4000+** plugin on the Plugins page and restart.
2. Rebuild the schedule (Dashboard → Rebuild), so ErsatzTV gets the new stream addresses
   (`/plugins/web-channels/live/…` instead of `/plugins/ws4kp/live/…`).

## What you need

- **Chrome, Edge, Chromium or Brave** on the scheduler's computer. The plugin runs one hidden to draw the pages. It's
  found automatically; otherwise choose it in the plugin's settings (its gear on the Plugins page).
- **ffmpeg.** The plugin uses the same one as generated promos or the one beside your ffprobe, or whatever you set in
  its settings.
- Internet access for every preset except a clock without weather.

## Channels

Open **Web channels** in the sidebar and add a channel:

1. Choose what it **shows** (a preset) and name the show.
2. For presets with a place, search for one and pick it. This fills in the latitude, longitude and (for the clock)
   the time zone. A custom page takes its address instead.
3. Set its look: units, clock style, map layer and zoom, and so on, depending on the preset.
4. List the **lengths** it can be scheduled for, in minutes, e.g. `3, 4, 5` for short segments, or `30, 60` for a
   channel of its own.
5. Optionally pick a **music folder**. Its songs are shuffled under the page at the volume you set. MP3, M4A, AAC,
   OGG, Opus, FLAC and WAV files are used, including those in subfolders. A page's own sound isn't captured.

The preview shows the page as it will look. While a channel is on air, the preview shows what's going out.

Each channel appears in the library as a show of that name, tagged `Web channel` and with its preset's tags
(`Weather` for WeatherStar, radar and the globe; `Clock` for clocks). Each length is an "episode", such as "Local
Forecast (5 min)", "Time & Temperature (5 min)" or "Weather Radar (30 min)", so the scheduler picks one that fits.

## Recipes

**A 24/7 Toronto weather channel.** Make a WeatherStar channel for Toronto with lengths `30, 60` and make it a channel's
base rotation. Add a Toronto radar channel to the same rotation to alternate them.

**Short Washington forecasts between shows.** Make a WeatherStar channel for Washington, imperial, with lengths
`3, 4, 5`. On a channel's **Breaks** tab, add *Washington Weather* to the interstitial shows, one per slot. You can
also put it in a block, such as a five-minute "Weather on the 8s" every hour.

**Time and temperature filler.** A clock for your town with lengths `1, 2, 3` and a scrolling message makes a tidy
interstitial for odd gaps.

**An overnight ambient channel.** The wind globe with a folder of quiet music and lengths `60`.

New or renamed channels reach the schedule on its next rebuild.

## Custom pages

The page is shown at 1280×720 in a browser no one can click, so pick one that plays by itself: a dashboard, a live map,
a kiosk or "TV mode" view. Pages that need a login won't work, since the hidden browser starts fresh each time. Some
sites refuse to be shown inside another page, so the editor's preview may stay blank even though the channel works.
Check what's going out with the on-air preview, or the stream in VLC.

## How it plays

When ErsatzTV reaches a segment, it opens the channel's live stream from the scheduler:
`http://<scheduler>:5252/plugins/web-channels/live/<channel>.ts`. The first request starts the channel: a hidden
browser opens the page and ffmpeg encodes its picture with the music into 720p H.264 and AAC. Every ErsatzTV channel
airing it shares one encoder. A channel stops 90 seconds after the last viewer leaves, so back-to-back segments don't
restart it (see the settings below).

Segments are live, so they're never seeked into: one that starts late, or that you tune into halfway, joins the stream
wherever it is. ErsatzTV reads the stream through `curl` (built into Windows 10 and later, macOS and most Linux
systems), which marks it as live. If the computer running ErsatzTV has no curl, set `ErsatzTv:LiveStreamCommand` (and
`ErsatzTv:LiveStreamArguments`) in `settings.json` to another program that writes a URL's contents to its output.

**ErsatzTV on another computer?** Set **Stream address** in the plugin's settings to an address that computer can reach,
e.g. `http://192.168.1.20:5252`. The default is `localhost`, which only works when both run on the same computer.

## Settings

| Setting | What it does |
| --- | --- |
| WeatherStar address | The WeatherStar International site for the WeatherStar preset. Defaults to the public one; point it at your own copy if you host one. |
| Browser | Chrome, Edge or Chromium to draw with. Found automatically when empty. |
| ffmpeg | The ffmpeg to encode with. Found automatically when empty. |
| Stream address | Where ErsatzTV fetches the streams from. |
| Capture frames per second | How many frames per second are taken from the page (5–30, default 15). Animated maps look smoother higher. The stream is always 30 fps. |
| Keep a channel running after it goes off air | Seconds a channel stays on after it's no longer watched (default 90). |

## Troubleshooting

- **A plain dark picture.** That's the stand-in shown until the browser draws the page. While the channel is on air,
  the Web channels page shows what's going out under **Preview**, and says under the channel why nothing is drawn
  (after 20 seconds). Check that a browser is installed and that the page opens in a browser on the scheduler's computer.
- **The radar is empty.** Windy's radar only covers some parts of the world; choose *Rain and thunder* instead.
- **The clock shows no weather.** It needs a place, and internet access to Open-Meteo.
- **No sound.** The music folder doesn't exist on the scheduler's computer, or has no music files in it.
- **ErsatzTV can't open the stream.** Check the stream address, and that the scheduler's port is reachable from the
  ErsatzTV computer through its firewall.
- **The channel loads forever.** Rebuild the schedule (Dashboard → Rebuild) so ErsatzTV gets the segments as live
  sources, and check the stream plays in VLC.
