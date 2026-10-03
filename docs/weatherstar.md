# WeatherStar weather

The WeatherStar plugin (`ws4kp`) puts live, retro-styled local weather on your channels, drawn by
[WeatherStar 4000+ International](https://github.com/mwood77/ws4kp-international). Use it for a 24/7
weather channel, or for short local forecasts between shows, the way cable channels used to.

It's optional: install `SchedularrTV-<version>-plugin-ws4kp.zip` (or the all-plugins zip) from the **Plugins** page.

## What you need

- **Chrome, Edge, Chromium or Brave** on the scheduler's computer. The plugin runs one hidden to draw the
  forecast. It's found automatically; otherwise choose it in the plugin's settings (its gear on the
  Plugins page).
- **ffmpeg.** The plugin uses the same one as generated promos or the one beside your ffprobe, or
  whatever you set in its settings.
- Internet access for the forecast data. WeatherStar International uses Open-Meteo, so it works
  anywhere in the world.

## Stations

Open **Weather stations** in the sidebar and add a station:

1. Search for a place and pick it. This fills in the latitude and longitude.
2. Name the show, e.g. "Toronto Weather".
3. Choose metric or imperial units, widescreen, a 12-hour clock, scan lines and the page speed.
4. List the **lengths** it can be scheduled for, in minutes, e.g. `3, 4, 5` for short forecasts, or
   `30, 60` for a weather channel.
5. Optionally pick a **music folder**. Its songs are shuffled under the forecast at the volume you set.
   MP3, M4A, AAC, OGG, Opus, FLAC and WAV files are used, including those in subfolders.

The preview shows the page as it will look. While a station is on air, the preview shows what's going out.

Each station appears in the library as a show of that name (tagged `Weather`). Each length is an "episode",
"Local Forecast (5 min)" for example, so the scheduler picks one that fits the time it has.

## Recipes

**A 24/7 Toronto weather channel.** Make a station for Toronto with lengths `30, 60`. Then make a
channel whose base rotation is the show *Toronto Weather*. The forecast runs around the clock and stays
current, because it's live.

**Short Washington forecasts between shows.** Make a station for Washington, imperial, with lengths
`3, 4, 5`. On the channel's **Breaks** tab, add *Washington Weather* to the interstitial shows, one per
slot. A forecast then plays between programmes as if it were an interstitial show. You can also put it
in a block: for example, a five-minute "Weather on the 8s" block every hour.

New or renamed stations reach the schedule on its next rebuild.

## How it plays

When ErsatzTV reaches a forecast, it opens the station's live stream from the scheduler:
`http://<scheduler>:5252/plugins/ws4kp/live/<station>.ts`. The first request starts the station: a
hidden browser opens the WeatherStar page and ffmpeg encodes its picture with the music into 720p H.264
and AAC. Every channel airing that station shares one encoder. A station stops 90 seconds after the last
viewer leaves, so back-to-back forecasts don't restart it (see the settings below).

Forecasts are live, so they're never seeked into: a forecast that starts late, or that you tune into
halfway, just joins the stream wherever it is. ErsatzTV reads the stream through `curl` (built into
Windows 10 and later, macOS and most Linux systems), which marks it as live. If the computer running
ErsatzTV has no curl, set `ErsatzTv:LiveStreamCommand` (and `ErsatzTv:LiveStreamArguments`) in
`settings.json` to another program that writes a URL's contents to its output.

To check a station on its own, open its stream address (shown on the Weather stations page) in VLC.

**ErsatzTV on another computer?** Set **Stream address** in the plugin's settings to an address that
computer can reach, e.g. `http://192.168.1.20:5252`. The default is `localhost`, which only works when
both run on the same computer.

## Settings

| Setting | What it does |
| --- | --- |
| WeatherStar address | The WeatherStar International site. Defaults to the public one; point it at your own copy if you host one. |
| Browser | Chrome, Edge or Chromium to draw with. Found automatically when empty. |
| ffmpeg | The ffmpeg to encode with. Found automatically when empty. |
| Stream address | Where ErsatzTV fetches the streams from. |
| Capture frames per second | How many frames per second are taken from the page (5–30, default 15). The stream is always 30 fps. |
| Keep a station running after it goes off air | Seconds a station stays on after it's no longer watched (default 90). |

## Troubleshooting

- **A plain blue picture.** That's the stand-in shown until the browser draws the page. While the station
  is on air, the Weather stations page shows what's going out under **Preview**, and says under the station
  why nothing is drawn (after 20 seconds). The scheduler's log has a line when the browser starts and when
  the forecast first appears. Check that a browser is installed and that the WeatherStar address opens.
- **No sound.** The music folder doesn't exist on the scheduler's computer, or has no music files in it.
- **ErsatzTV can't open the stream.** Check the stream address, and that the scheduler's port is
  reachable from the ErsatzTV computer through its firewall.
- **The channel loads forever.** Rebuild the schedule (Dashboard → Rebuild) so ErsatzTV gets the
  forecasts as live sources, and check the stream plays in VLC.
