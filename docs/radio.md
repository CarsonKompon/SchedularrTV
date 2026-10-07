# Radio shows

The radio plugin (`radio`) makes music into a channel: songs from your **Jellyfin music library**, or an **internet
radio station**, shown with the song's cover, its lyrics and a visualizer that moves with the sound. Use one as a 24/7
music channel, or as music between programmes.

It's optional: install `SchedularrTV-<version>-plugin-radio.zip` (or the all-plugins zip) from the **Plugins** page.
Like the TV Guide channel, it's built against the scheduler itself, so install the one from the same release.

## What you need

- **Chrome, Edge, Chromium or Brave** on the scheduler's computer: the station's picture is a web page drawn by one
  running hidden.
- **ffmpeg**, the same one as the rest of SchedularrTV.
- For library music, Jellyfin set up under App settings. Lyrics from Jellyfin need Jellyfin 10.9 or later.

## Making a station

Open **Radio shows** in the sidebar and choose **Add a station**. The live preview beside the form shows a song from
your library with your settings as you change them.

| Section | Options |
| --- | --- |
| Station | Show name; music from the Jellyfin library or an internet station; lengths it can be scheduled for |
| Music (Jellyfin) | Every song, a playlist, some genres or some artists; shuffled or in order |
| Station (internet) | The stream's address, or an `.m3u` / `.pls` playlist that names it |
| Look | Now Playing, Karaoke, Vinyl, Retro FM or Minimal; the visualizer (bars, waveform, spectrum, oscilloscope or none), its size (50–150%), how strongly it moves, and its colour; background (the cover blurred, a moving gradient, or a colour); accent colour; lettering (the look's own, any installed font, as in the canvas editors, or a web font: Inter, Oswald, Merriweather, Roboto Slab, Nunito, JetBrains Mono or a segmented display), text size and text colour; cover, lyrics, up next, clock and scan lines on or off; a message along the bottom; lyrics timing |
| Sound | Volume |

### The looks

- **Now Playing:** a big cover on the left, the song and its lyrics on the right, the visualizer along the bottom.
- **Karaoke:** the lyrics, big and centred, the current line lit up, over the blurred cover.
- **Vinyl:** a spinning record with the cover as its label, the song underneath, the current lyric line above.
- **Retro FM:** a hi-fi tuner's glowing 14-segment display ([DSEG14](https://github.com/keshikan/DSEG), fetched
  when the page draws) with its unlit segments faintly showing, the song in capitals scrolling along it when it's too
  long, and the visualizer as its meter.
- **Minimal:** the cover filling the screen, softly, with the song in a corner.

## Lyrics and covers

Library songs use their Jellyfin cover and Jellyfin's lyrics; timed lyrics (from `.lrc` files or embedded) follow the
song line by line, untimed ones scroll through it. When a song has no lyrics, and *Look up covers and lyrics online* is
on, they're looked up on [LRCLIB](https://lrclib.net). If lyrics run early or late, move **Lyrics timing**.

Internet stations that send the song playing (Shoutcast and Icecast do, as "Artist - Title") show it, with a cover from
the iTunes Search API and lyrics from LRCLIB. A station's songs have no known start, so timed lyrics follow from when
the title changed, which is usually close. HLS (`.m3u8`) stations play without titles.

## Scheduling it

Each station appears in the library as a show of that name (tagged `Radio` and `Music`), with one "episode" per length.

- **A 24/7 music channel:** make the station a channel's base rotation, with lengths like `30, 60`.
- **Between programmes:** add it to a channel's interstitial shows (Breaks tab) with short lengths (`3, 5`).

## How it plays

When ErsatzTV reaches a station, it opens its live stream from the scheduler
(`http://<scheduler>:5252/plugins/radio/live/<station>.ts`). The first viewer starts it: the plugin plays the songs
one after another (from Jellyfin, read as they are, with the API key kept out of the address) or relays the station; a
hidden browser draws the page, which asks every second what's playing; and ffmpeg encodes it at 720p with the sound,
drawing the visualizer from the sound itself, so it's always in step. A station stops 90 seconds after the last viewer
leaves.

**ErsatzTV on another computer?** Set **Stream address** in the plugin's settings to an address that computer can
reach, e.g. `http://192.168.1.20:5252`.

## Settings

| Setting | What it does |
| --- | --- |
| Browser | Chrome, Edge or Chromium to draw with. Found automatically when empty. |
| ffmpeg | Empty uses the one from App settings. |
| Stream address | Where ErsatzTV fetches the station from. |
| Capture frames per second | How often the page is captured (5 to 30, default 20). The visualizer is always 30 fps. |
| Keep a station running after it goes off air | Seconds (default 90). |
| Draw animations at the capture rate | On by default: the hidden browser moves the page's animations on only as often as it's captured, rather than redrawing 60 times a second. Turn off if an animation stops or stutters. |

## Troubleshooting

- **Silence, and "No songs match this station's music".** The playlist, genres or artists chosen have no songs; or
  Jellyfin isn't reachable.
- **No lyrics.** The song has none in Jellyfin and none on LRCLIB (or looking online is off).
- **An internet station stays on its name.** It doesn't send titles (or it's HLS).
