# News shows

**News shows** turns RSS or Atom feeds into scheduled bulletins with optional weather, markets, sports, maps, and lottery results. Shows can use voice plugins and background music.

## Set up a show

1. Install **News shows** and restart.
2. Verify **Chrome, Edge, Chromium, or Brave** and **ffmpeg** on the scheduler computer.
3. Open **News shows → Add a news show**.
4. Set the name, schedulable lengths, and look.
5. Add RSS or Atom feeds under **Stories**.
6. Select data segments and a location under **Place**.
7. Configure **Sound**, review preview errors, and save.
8. Select the show in a channel's **Base rotation**, block, or **Breaks** interstitial shows.

Use **`30, 60`** minute lengths for continuous news and **`2, 3, 5`** for bulletins. Library entries carry the **News** tag.

## Choose the look

| Look | Presentation |
| --- | --- |
| **Teletext** | Numbered pages, colored bars, mosaic weather symbols |
| **News desk** | Story picture, lower third, ticker, side panel |
| **24-hour cable news** | Headline banners, crawl, markets strip |
| **80s/90s local news** | Retro titles, grid, scan-line effects |

Colors, fonts, text size, ticker, side panel, clock, and corner label are configurable. The preview uses real data and lists fetch failures.

## Configure segments

| Segment | Configuration | Source |
| --- | --- | --- |
| **Stories** | Feed URLs, story count, age limit, duration | RSS/Atom providers |
| **Weather** | Place | Open-Meteo |
| **Markets** | Symbols such as `^GSPC`, `AAPL`, `BTC-USD` | Yahoo Finance |
| **Sports** | League or ESPN `sport/league` | ESPN scoreboards |
| **Map** | Style, zoom, radar, marker | OpenStreetMap, CARTO, Esri, RainViewer |
| **Traffic** | TomTom key in plugin settings | TomTom |
| **Lottery** | Supported US or Canadian games | New York State and WCLC |

Traffic requires a key; other bundled data integrations do not. Availability depends on the upstream source.

## Add narration and music

1. Enable and configure a [voice plugin](generated-promos.md#voices).
2. Choose headline-only, summary, or fuller read-outs under **Sound**.
3. Select the voice and optional data-segment narration.
4. Set opening and closing lines.
5. Choose [background music](background-music.md) and its volume.

Narrated segments last for their speech duration. Without speech, configured segment durations apply; music ducks under the voice.

## Refresh and streaming settings

The rundown loops through an intro, stories, data segments, and outro. Refreshes replace it at the end of a loop; unchanged stories reuse voice-overs.

| Setting | Default or purpose |
| --- | --- |
| **Fetch the news again every** | 15 minutes while in use; longer intervals reduce paid speech requests |
| **TomTom API key** | Traffic data |
| **Browser**, **ffmpeg** | Executable overrides |
| **Stream address** | Scheduler URL reachable by ErsatzTV |
| **Capture frames per second** | Page capture rate |
| **Keep a show running after it goes off air** | 90 seconds |

The endpoint is **`/plugins/news/live/<show>.ts`**. A show restarted after its idle timeout begins at the top of its rundown.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| **403/404 from a feed** | Replace a moved or blocked feed URL |
| **Fetch timeout** | Check connectivity and wait for the next refresh |
| **Missing markets or map** | Check upstream availability from the scheduler computer |
| **No narration** | Check voice-plugin status and pending speech generation |
| **Remote stream unavailable** | Correct **Stream address** and network access |

Failed data requests can reuse a successful response from the preceding six hours.
