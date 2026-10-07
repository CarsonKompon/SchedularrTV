# News shows

The news plugin (`news`) makes local news breaks: headlines from your own **RSS or Atom feeds**, with the **weather**,
the **markets**, **sports scores**, a **local map** (rain radar, and traffic with a TomTom key) and **lottery results**
between the stories. It comes in four looks: **teletext**, a **news desk**, **24-hour cable news** and an **80s/90s
local station**. You can have it **read out** by any voice plugin you have installed, over your own music.

Each news show is a "virtual show": instead of video files it adds empty episodes of the lengths you allow (2, 3, 5,
10, 30, 60 minutes…) to your library. When the schedule reaches one, it tunes into the show's live broadcast. Use the
long ones for a 24/7 news channel, or drop the short ones into other channels as news breaks.

It's optional: install `SchedularrTV-<version>-plugin-news.zip` (or the all-plugins zip) from the **Plugins** page.

## What you need

- **Chrome, Edge, Chromium or Brave** on the scheduler's computer. The news page is drawn by one running hidden.
- **ffmpeg**, the same one as the rest of SchedularrTV.
- Internet access, for the feeds and data.
- To read it out: a voice plugin switched on ([Piper](generated-promos.md) works offline; TikTok and MiniMax are online).

## Making a show

Open **News shows** in the sidebar and choose **Add a news show**. The live preview beside the form uses the real
news and runs through the segments on the show's own timing. Anything that couldn't be fetched is listed along its top.

| Section | Options |
| --- | --- |
| Show | Name, lengths it can be scheduled for, look, station colour, ticker, side panel, story pictures, a ticker message |
| Stories | Any number of RSS or Atom feeds (each with an optional name to show), one-click BBC, NPR, CBC, Guardian and Al Jazeera feeds, and Google News for your town; how many stories each time round; how old a story can be; seconds per story when it isn't read out |
| Segments | Weather, markets, sports scores, map, lottery: each one you tick comes between the stories |
| Place | The town for the weather, the map and the clock (searched by name) |
| Markets | Yahoo Finance symbols: `^GSPC` (S&P 500), `^DJI`, `^IXIC`, `^GSPTSE`, `^FTSE`, shares like `AAPL`, `BTC-USD`, currencies like `CADUSD=X` |
| Sports | NFL, NBA, MLB, NHL, WNBA, college football and basketball, Premier League, MLS, LaLiga, Champions League, CFL, or any ESPN `sport/league` |
| Map | Zoom (region to streets) and rain radar; traffic when a TomTom key is set |
| Lottery | Powerball, Mega Millions, NY Lotto |
| Sound | What's read (nothing, headlines, headlines and a short summary, or the whole summary); the voice; whether the data segments are read too; the opening and closing lines; a music folder and its volume (it dips under the voice) |

### The looks

- **Teletext:** Ceefax-style pages, with page numbers (101 onwards for the news, 302 sport, 401 weather, 430 travel,
  555 lottery), coloured title bars, a clock in the header and a Fastext row. Weather symbols are drawn in mosaic
  blocks, and the map is turned into teletext colours.
- **News desk:** a modern broadcast. Big story pictures with a lower third, a corner bug, a clock, a white ticker and a
  side panel cycling through the weather, the markets and scores.
- **24-hour cable news:** BREAKING / LATEST banners, an all-caps headline bar, an "Up next" panel, a yellow crawl and a
  markets strip along the bottom.
- **80s/90s local news:** gold bevelled logo, serif titles, a neon grid, VHS lines and a "▶ PLAY" clock.

## Where the data comes from

All free, with no keys, except traffic:

| Segment | Source |
| --- | --- |
| Stories | Your feeds (RSS 2.0, RSS 1.0 and Atom; pictures from enclosures, Media RSS or the first image) |
| Weather | [Open-Meteo](https://open-meteo.com) (current conditions and five days), and its place search |
| Markets | Yahoo Finance's chart data (the day's prices, drawn as a sparkline) |
| Sports | ESPN's public scoreboards (live games first, then finished, then still to come) |
| Lottery | New York State's open data (Powerball, Mega Millions, NY Lotto) |
| Map | [OpenStreetMap](https://www.openstreetmap.org) tiles, kept for a week; [RainViewer](https://www.rainviewer.com) radar |
| Traffic | [TomTom](https://developer.tomtom.com) traffic flow and incidents, with a free key in the plugin's settings |

Map tiles are fetched through the scheduler, so the TomTom key never reaches the page.

## Scheduling it

Each show appears in the library as a show of that name (tagged `News`), with one "episode" per length.

- **A 24/7 news channel:** make the show a channel's base rotation, with lengths like `30, 60`.
- **News breaks:** add it to a channel's interstitial shows (Breaks tab) with short lengths (`2, 3, 5`).

## How it plays

Each show has a **rundown**: an intro, the stories with the data segments spread between them, and an outro. It loops
for as long as the show is on. The rundown is written again every 15 minutes while the show is in use (**Fetch the news
again every**). Then the new one takes over when the current loop finishes, so a story is never cut off. A break
that goes on air after the show has been idle starts from the top.

When it's read out, each segment's words are voiced when the rundown is written, and each segment lasts as long as
its voice-over. Voice-overs are kept while a story stays in the news, so a refresh only voices the new stories. When it
isn't read out, stories and segments last as long as the show says.

The picture and the sound follow the same clock. A hidden browser draws the page, which asks every second or two where
the rundown is. The plugin mixes the voice-overs over the music, a little ahead of the clock. ffmpeg encodes both at
720p. A show stops 90 seconds after the last viewer leaves.

**ErsatzTV on another computer?** Set **Stream address** in the plugin's settings to an address that computer can
reach, e.g. `http://192.168.1.20:5252`.

## Settings

| Setting | What it does |
| --- | --- |
| Fetch the news again every | Minutes between refreshes while a show is in use (5 to 240, default 15). With a paid voice, keep this longer. |
| TomTom API key | For traffic on the map. Without one the map shows the radar. |
| Browser | Chrome, Edge or Chromium to draw with. Found automatically when empty. |
| ffmpeg | Empty uses the one from App settings. |
| Stream address | Where ErsatzTV fetches the show from. |
| Capture frames per second | How often the page is captured (5 to 30, default 15; 8 on a Raspberry Pi). |
| Keep a show running after it goes off air | Seconds (default 90). A break after this starts from the top. |
| Draw animations at the capture rate | On by default. Turn off if the ticker or a transition stops or stutters. |

## Troubleshooting

- **"Couldn't get … : the server answered 403/404".** The feed moved or blocks automated readers; try its other feed
  address, or Google News for the topic.
- **No markets.** Yahoo Finance sometimes refuses requests for a while; the segment is left out until it answers again.
- **No voice.** The voice plugin is switched off or not set up (the editor says which), or the voice-overs are still
  being made after a refresh; segments run for their standard length in the meantime.
- **The map is blank.** OpenStreetMap or RainViewer couldn't be reached from the scheduler's computer.
