# Web page shows

**Web page shows** captures a live page and streams it as schedulable programming. Presets cover retro weather and clocks; custom URLs support unattended dashboards and displays.

## Create a show

1. Install **Web page shows** and restart.
2. Verify **Chrome, Edge, Chromium, or Brave** and **ffmpeg** on the scheduler computer.
3. Open **Web shows → Add a show**.
4. Choose a preset and name the show.
5. Select a place or enter a custom URL.
6. Set appearance, lengths in minutes, and sound.
7. Save and select the show in a channel's **Base rotation**, block, or **Breaks** interstitial shows.

Use **`30, 60`** for continuous channels or **`3, 4, 5`** for short weather segments. Each length becomes a virtual episode tagged **Web channel**, plus preset-specific tags.

## Choose a preset

| Preset | Use |
| --- | --- |
| **WeatherStar 4000+** | International forecasts with metric or imperial units; place required |
| **Clock** | Local time, optional weather, message |
| **Weatherscan v2**, **v1**, **Local** | Classic continuous-weather designs |
| **WeatherStar Jr**, **4000 (classic)**, **XL** | Retro local forecasts |
| **IntelliStar** | 2000s forecast graphics |
| **Custom web page** | Unattended page at a supplied URL |

Mist Weather Media presets use weather.com data and primarily target US locations. Without a selected place, supported simulators may use the scheduler's public-IP location.

Some simulators show a configuration screen in the editor preview; the live capture configures and starts them automatically. Check the on-air preview for the actual output.

## Configure sound

| Choice | Behavior |
| --- | --- |
| **Play its own music and narration** | Capture supported audio/video elements in the hidden browser |
| **Music folder** | Shuffle audio files from the folder and subfolders |
| **Music library** | Play all songs or selected playlists, genres, or artists |

If a simulator is silent, disable its own sound and select [background music](background-music.md). Audio generated outside supported page media elements may not be captured.

## Custom pages

Custom URLs accept **`{lat}`**, **`{lon}`**, **`{location}`**, and **`{name}`**. Use a page that starts without clicks or login; the hidden browser starts with a fresh session.

An iframe-blocking site may have a blank editor preview but work on air. Test with the on-air preview or an IPTV player.

## Streaming settings

| Setting | Purpose |
| --- | --- |
| **WeatherStar address** | Override the WeatherStar International site |
| **Browser**, **ffmpeg** | Executable overrides |
| **Stream address** | Scheduler URL reachable by ErsatzTV |
| **Capture frames per second** | Capture smoothness versus CPU cost |
| **Keep a channel running after it goes off air** | Idle timeout; default 90 seconds |
| **Draw animations at the capture rate** | Reduce unnecessary browser redraws |

The endpoint is **`/plugins/web-channels/live/<show>.ts`**. The first request starts capture; every channel airing the same show shares it, and late joins enter the live stream without seeking.

ErsatzTV reads live streams through **curl** by default. Override **`ErsatzTv:LiveStreamCommand`** and **`LiveStreamArguments`** when another stream-reading program is required.

## Coming from the WeatherStar plugin

1. Install **Web page shows** and restart to migrate existing stations.
2. Remove the old **WeatherStar 4000+** plugin and restart.
3. Rebuild the schedule to publish the new stream URLs.

The new plugin imports existing station settings and initially uses the old plugin settings until its own are saved.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| **Dark picture** | Check the on-air preview error, browser installation, and page connectivity |
| **Wrong place** | Use a simpler **Shown as** location, state abbreviation, or ZIP code |
| **Settings screen on air** | Check for a simulator-site change and try another preset |
| **Clock has no weather** | Select a place and verify Open-Meteo access |
| **Remote playback fails** | Set **Stream address** and allow scheduler-port access |
| **Endless loading** | Rebuild to refresh live-source playouts and test the stream directly |
