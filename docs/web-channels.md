# Web page shows

Put live web pages on your channels, such as retro weather simulators, a clock or any page you like, as a 24/7 channel or as short segments between shows.

## What it is

The web page channels plugin (`web-channels`) puts live web pages on your channels. A hidden browser shows the page and its picture is streamed to ErsatzTV with your music, so what airs is always current.

Use one for a 24/7 channel, or for short segments between shows, the way cable channels used to.

The plugin is optional: install `SchedularrTV-<version>-plugin-web-channels.zip` (or the all-plugins zip) from the **Plugins** page.

## Presets

Presets set up the pages that make good television. The **Place** column says whether the preset needs a place.

| Preset | What it shows | Place |
| --- | --- | --- |
| **WeatherStar 4000+** | Retro local forecasts from [WeatherStar 4000+ International](https://github.com/mwood77/ws4kp-international), in the style of 1990s cable weather. | Required |
| **Clock** | Time and temperature: a big digital clock or a clock face for the place's time zone, its current conditions and an optional scrolling message. Served by the plugin itself. | Optional (time zone and weather) |
| **Weatherscan v2** | [v2.weatherscan.net](https://v2.weatherscan.net/): the last design of the round-the-clock local weather channel, with its music and narration. | Optional |
| **Weatherscan v1** | [v1.weatherscan.net](https://v1.weatherscan.net/): its first IntelliStar design. | Optional |
| **Weatherscan Local** | [local.weatherscan.net](https://local.weatherscan.net/): the first Weatherscan; choose its segments, the cable company's name and a message. | Optional |
| **WeatherStar Jr** | [jr.weatherstar.dev](https://jr.weatherstar.dev/): the local forecast on the WeatherStar Jr. | Optional |
| **WeatherStar 4000 (classic)** | [4000.weatherstar.dev](https://4000.weatherstar.dev/): the original 1990s local forecast, with music. | Optional |
| **WeatherStar XL** | [xl.weatherstar.dev](https://xl.weatherstar.dev/): the late-90s/2000s local forecast; choose its flavor, bottom line and graphics. | Optional |
| **IntelliStar** | [i1.weatherstar.dev](https://i1.weatherstar.dev/): the 2000s local forecast; choose its flavor, graphics year and bottom line. | Optional |
| **Custom web page** | Any address. `{lat}`, `{lon}`, `{location}` and `{name}` in it are filled in from the channel. | Optional |

### About the simulators

The Weatherscan, WeatherStar Jr/4000/XL and IntelliStar presets are [Mist Weather Media](https://mistwx.com/sims/)'s simulators.

- They take their forecasts from weather.com in °F and mph and are built for US places (others mostly work). For metric units or anywhere in the world, use **WeatherStar 4000+**.
- Without a place they show where the scheduler's computer is, from its internet address.
- Some open on a settings screen (Weatherscan Local, WeatherStar XL, IntelliStar). On air they're set up with the channel's settings and started automatically, every time they reload, so the editor's preview shows their settings screen rather than what airs.

> [!NOTE]
> The weather radar (Windy) and wind globe (earth.nullschool.net) presets have been removed. Channels that used them carry on as custom pages showing the same map.

## Requirements

- **Chrome, Edge, Chromium or Brave** on the scheduler's computer. The plugin runs one hidden to draw the pages. It's found automatically; otherwise choose it in the plugin's settings (its gear on the **Plugins** page).
- **ffmpeg.** The plugin uses the same one as generated promos or the one beside your ffprobe, or whatever you set in its settings.
- **Internet access** for every preset except a clock without weather.

## Adding a show

Open **Web shows** in the sidebar and choose **Add a show**:

1. Choose what it **shows** (a preset) and name the show.
2. For presets with a place, search for one and pick it. This fills in the latitude, longitude and (for the clock) the time zone. A custom page takes its address instead.
3. Set its look: units, clock style, segments, flavor, bottom line, message and so on, depending on the preset.
4. List the **lengths** it can be scheduled for, in minutes, e.g. `3, 4, 5` for short segments, or `30, 60` for a channel of its own.
5. Choose its **sound** (see below).

The preview shows the page as it will look. While a channel is on air, the preview shows what's going out.

### Sound

- **The page's own sound.** The weather simulators air their own music and narration; untick **Play its own music and narration** to use yours instead. A custom page can air its own sound too.
- **Music**, shuffled under the page at the volume you set, from either:
  - A folder on disk (MP3, M4A, AAC, OGG, Opus, FLAC, WAV and other audio files, including those in subfolders), or
  - Any [music library](background-music.md) (Jellyfin's, local music folders, a plugin's) with every song, a playlist, some genres or some artists.

A page's own sound is what its audio and video elements play, taken from the hidden browser as it plays (it's never heard on the scheduler's computer). Sound a page makes some other way isn't picked up.

### How shows appear in the library

Each channel appears in the library as a show of that name, tagged `Web channel` and with its preset's tags (`Weather` for the weather presets; `Clock` for clocks).

Each length is an "episode", such as "Local Forecast (5 min)", "Time & Temperature (5 min)" or "Local Weather (30 min)", so the scheduler picks one that fits.

New or renamed channels reach the schedule on its next rebuild.

## Recipes

- **A 24/7 Toronto weather channel.** Make a WeatherStar channel for Toronto with lengths `30, 60` and make it a channel's base rotation.
- **A Weatherscan channel.** Weatherscan v2 for your town with lengths `60`, as a channel's base rotation, airing its own music: the 24/7 local weather channel as it was.
- **Short Washington forecasts between shows.** Make a WeatherStar channel for Washington, imperial, with lengths `3, 4, 5`. On a channel's **Breaks** tab, add *Washington Weather* to the interstitial shows, one per slot. You can also put it in a block, such as a five-minute "Weather on the 8s" every hour.
- **Time and temperature filler.** A clock for your town with lengths `1, 2, 3` and a scrolling message makes a tidy interstitial for odd gaps.
- **Local on the 8s.** WeatherStar XL or the IntelliStar with lengths `1, 2` in a block every ten minutes.

## Custom pages

The page is shown at 1280×720 in a browser no one can click, so pick one that plays by itself: a dashboard, a live map, a kiosk or "TV mode" view.

- Pages that need a login won't work, since the hidden browser starts fresh each time.
- Some sites refuse to be shown inside another page, so the editor's preview may stay blank even though the channel works. Check what's going out with the on-air preview, or the stream in VLC.

## How it plays

When ErsatzTV reaches a segment, it opens the channel's live stream from the scheduler: `http://<scheduler>:5252/plugins/web-channels/live/<channel>.ts`.

- **The first request starts the channel.** A hidden browser opens the page (setting up and starting a simulator that needs it), and ffmpeg encodes its picture with the music, or the page's own sound, into 720p H.264 and AAC.
- **One encoder per show.** Every ErsatzTV channel airing it shares one encoder.
- **Stopping.** A channel stops 90 seconds after the last viewer leaves, so back-to-back segments don't restart it (see the settings below).
- **Live, never seeked.** Segments are live, so they're never seeked into: one that starts late, or that you tune into halfway, joins the stream wherever it is.

ErsatzTV reads the stream through `curl` (built into Windows 10 and later, macOS and most Linux systems), which marks it as live. If the computer running ErsatzTV has no curl, set `ErsatzTv:LiveStreamCommand` (and `ErsatzTv:LiveStreamArguments`) in `settings.json` to another program that writes a URL's contents to its output.

> [!IMPORTANT]
> **ErsatzTV on another computer?** Set **Stream address** in the plugin's settings to an address that computer can reach, e.g. `http://192.168.1.20:5252`. The default is `localhost`, which only works when both run on the same computer.

## Settings

| Setting | What it does |
| --- | --- |
| **WeatherStar address** | The WeatherStar International site for the WeatherStar preset. Defaults to the public one; point it at your own copy if you host one. |
| **Browser** | Chrome, Edge or Chromium to draw with. Found automatically when empty. |
| **ffmpeg** | The ffmpeg to encode with. Found automatically when empty. |
| **Stream address** | Where ErsatzTV fetches the streams from. |
| **Capture frames per second** | How many frames per second are taken from the page (5–30, default 15). Animated maps look smoother higher. The stream is always 30 fps. |
| **Keep a channel running after it goes off air** | Seconds a channel stays on after it's no longer watched (default 90). |
| **Draw animations at the capture rate** | On by default. See below. |

**Draw animations at the capture rate** makes the hidden browser move the page's animations (CSS, and anything drawn with requestAnimationFrame) on only as often as it's captured, rather than redrawing 60 times a second. A page of your own that pauses and resumes its own animations may need this off.

## Coming from the WeatherStar plugin

This plugin replaces the WeatherStar plugin (`ws4kp`). The first time it starts, it takes over your weather stations as WeatherStar channels (same names and settings), and uses the WeatherStar plugin's settings until you set its own.

Then:

1. Remove the **WeatherStar 4000+** plugin on the **Plugins** page and restart.
2. Rebuild the schedule (**Dashboard → Rebuild**), so ErsatzTV gets the new stream addresses (`/plugins/web-channels/live/…` instead of `/plugins/ws4kp/live/…`).

## Troubleshooting

### A plain dark picture

That's the stand-in shown until the browser draws the page. While the channel is on air, the **Web shows** page shows what's going out under **Preview**, and says under the channel why nothing is drawn (after 20 seconds). Check that a browser is installed and that the page opens in a browser on the scheduler's computer.

### A simulator shows the wrong place

It searched weather.com for the place's name. Use a plainer one ("Toronto", "Denver, CO" or a ZIP code) under **Shown as**.

### A simulator stays on its settings screen

It couldn't be started for you, often because the site has changed. Watch it in VLC, and try another preset meanwhile.

### A simulator is silent

Its sound comes from the hidden browser. If it's silent, untick **Play its own music and narration** and choose music instead.

### The clock shows no weather

It needs a place, and internet access to Open-Meteo.

### No sound

- The music folder doesn't exist on the scheduler's computer, or has no music files in it; or
- The music library isn't set up, or has no songs for the playlist, genres or artists chosen.

### ErsatzTV can't open the stream

Check the stream address, and that the scheduler's port is reachable from the ErsatzTV computer through its firewall.

### The channel loads forever

Rebuild the schedule (**Dashboard → Rebuild**) so ErsatzTV gets the segments as live sources, and check the stream plays in VLC.
