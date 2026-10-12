# TV Guide channel

**TV Guide shows** renders scrolling listings from the published schedule. A top panel can display information, commercials, or another channel's video.

## Create a guide

1. Install **TV Guide shows** from the scheduler's release and restart.
2. Verify **Chrome, Edge, Chromium, or Brave**, **ffmpeg**, and **ffprobe**.
3. Open **Guide shows → Add a guide**.
4. Set the name, look, and schedulable lengths.
5. Choose listed channels, time span, scroll speed, and text size.
6. Configure **Top panel** and **Sound**.
7. Save and select the guide in a channel's **Base rotation** or **Breaks** interstitial shows.

Use **`30, 60`** minute lengths for continuous listings or **`2, 5`** for short segments. Exclude the guide's own channel under **Channels listed** to avoid self-listing.

## Choose a look

| Look | Presentation |
| --- | --- |
| **Prevue (1990s)** | Blue scrolling list |
| **TV Guide Channel (2000s)** | Glossy grid with half-hour columns |
| **Cable box guide (2000s)** | Cream listings, channel badges, promo panel |
| **Listings channel (1999)** | Blue promo panel, yellow times, colored programme rows |

Fonts, colors, logos, row height, and 24-hour time are configurable. Listings refresh every minute from the latest published schedules.

## Configure the top panel

| Mode | Configuration |
| --- | --- |
| **Information** | Clock, date, message, current-programme information |
| **Commercials** | Library query; default `tag:"Commercials"` |
| **A channel's picture** | Fixed channel or random cycling selection |

**Cycle through channels at random** changes channels every **1–30 minutes**, without repeating the same channel consecutively. A channel window starts another ErsatzTV stream and adds its encoding cost.

## Configure sound

Choose **music**, **the top panel's sound**, **a channel's sound**, or **nothing**. With cycling channels, the window's sound follows the selected channel.

See [Background music](background-music.md) for folder and library choices.

## Streaming settings

| Setting | Purpose |
| --- | --- |
| **Browser**, **ffmpeg** | Executable overrides |
| **Stream address** | Scheduler URL reachable by ErsatzTV |
| **Capture frames per second** | Scroll smoothness versus CPU cost |
| **Keep a guide running after it goes off air** | Idle timeout; default 90 seconds |
| **Draw animations at the capture rate** | Match browser work to capture frequency |
| **Decode the video window on the graphics chip** | Hardware decoding for window content |

The endpoint is **`/plugins/tv-guide/live/<guide>.ts`**. Viewers share one encoder; saving an on-air guide restarts it with the new settings.

## Troubleshooting

- Verify published schedules when **listings are empty**
- Test the selected ErsatzTV channel when the **video window is blank**
- Select **The top panel's sound** to hear window video
- Set a reachable **Stream address** when ErsatzTV runs elsewhere
- Lower capture rate or enable hardware decoding for **high CPU usage**
