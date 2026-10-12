# Radio shows

**Radio shows** combines library music or internet radio with cover art, lyrics, and a visualizer. Stations become schedulable library shows.

## Requirements

| Component | Purpose |
| --- | --- |
| **Radio shows** plugin | Station configuration and streaming |
| **Chrome, Edge, Chromium, or Brave** | Hidden page rendering |
| **ffmpeg** | Audio and video encoding |
| **Music library or internet stream** | Audio source |

Use the plugin package from the scheduler's release. Add local songs through [Music folders](local-library.md#music-folders); Jellyfin lyrics require Jellyfin 10.9 or later.

## Create a station

1. Open **Radio shows → Add a station**.
2. Name the show and set its schedulable lengths in minutes.
3. Choose a music library or an internet station.
4. Select songs by playlist, genre, or artist, or enter the station URL.
5. Choose the look, visualizer, background, and volume.
6. Review the preview and save.
7. Select the station in a channel's **Base rotation** or **Breaks** interstitial shows.

Use lengths such as **`30, 60`** for continuous channels and **`3, 5`** for short breaks. Stations carry **Radio** and **Music** tags.

## Choose the presentation

| Look | Layout |
| --- | --- |
| **Now Playing** | Cover, song details, lyrics, bottom visualizer |
| **Karaoke** | Large centered lyrics with the current line highlighted |
| **Vinyl** | Spinning record with cover label |
| **Retro FM** | Segmented tuner display |
| **Minimal** | Full-screen cover background with compact text |

The editor controls lettering, text size, colors, clock, scan lines, messages, and visualizer style. **Lyrics timing** corrects early or late lyric display.

## Lyrics and internet stations

Library songs use provider artwork and lyrics, including local **`.lrc`** files. Optional online lookup uses **LRCLIB**; internet-station covers use the **iTunes Search API**.

Internet sources accept direct streams and **`.m3u`/`.pls`** playlists. Stations exposing **Artist - Title** metadata support song information; HLS stations play without titles, and timed lyrics start from the detected title change.

## Configure streaming

| Plugin setting | Use |
| --- | --- |
| **Browser**, **ffmpeg** | Override detected executables |
| **Stream address** | Scheduler address reachable by ErsatzTV |
| **Capture frames per second** | Page capture rate; lower values reduce CPU |
| **Keep a station running after it goes off air** | Idle timeout; default 90 seconds |
| **Draw animations at the capture rate** | Reduce browser work; disable for animation problems |

The live endpoint is **`/plugins/radio/live/<station>.ts`**. Viewers share one running station; live encoding defaults come from **App settings → Hardware and video encoding**.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| **No songs match** | Check library connectivity and selected filters |
| **No lyrics** | Check local/provider lyrics and online lookup settings |
| **Station name never changes** | Verify the source sends track metadata |
| **Remote playback fails** | Set **Stream address** to a reachable scheduler URL |
