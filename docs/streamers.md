# Streamer channels

**Streamer channels** combines Twitch and YouTube broadcasters into priority-ordered channels. It can show chat, other-live notices, replays, and predicted start times.

## Create a channel

1. Install **Streamer channels** and restart.
2. Verify a supported Chromium browser and **ffmpeg**.
3. Open **Streamer channels** and download **yt-dlp** if unavailable.
4. Select **Add a streamer channel**.
5. Set the channel's name, unique number, group, and optional logo.
6. Add Twitch names or YouTube handles, IDs, or URLs.
7. Order streamers with the arrows, highest priority first.
8. Configure chat, offline behavior, and notifications, then save.

The channel appears directly in the lineup; it does not need a base rotation. **Channels → Edit** returns to its plugin editor.

## Set chat and notifications

| Control | Choices |
| --- | --- |
| **Chat placement** | Overlay, side panel, or custom box |
| **Chat look** | Classic, Bubbles, Minimal, custom HTML |
| **Message timing** | Visible count, fade delay, backing opacity |
| **Third-party emotes** | BetterTTV, FrankerFaceZ, 7TV |
| **Other-live notices** | Popup card, scrolling text, or both |

Custom chat templates support **`{author}`**, **`{message}`**, **`{color}`**, **`{platform}`**, and **`{time}`**. Live notices support **`{name}`**, **`{title}`**, **`{game}`**, **`{platform}`**, and **`{viewers}`**.

## Choose offline behavior

| Mode | Output |
| --- | --- |
| **Countdown** | Next expected stream and optional music |
| **Replay with a card** | Recent recording with periodic replay/countdown notices |
| **Framed replay** | Recording beside persistent replay and countdown information |

Replays use the newest accessible recording of at least ten minutes among the selected streamers. Playback position survives restarts, recordings loop, and a live streamer takes priority; unavailable recordings fall back to another replay or the countdown.

## Understand predictions

Predictions combine announced streams, weekly habits, daily habits, and typical gaps between streams. The countdown labels confidence as **Scheduled**, **Usually live**, or **Rough guess**.

History is retained for **10 weeks** in **`plugin-data/streamers/history.json`**. **Time zone for predictions** defaults to the scheduler computer's zone; a new streamer may lack enough history.

## Configure performance

| Setting | Default or purpose |
| --- | --- |
| **yt-dlp**, **Browser**, **ffmpeg** | Executable overrides |
| **Stream address** | Scheduler URL reachable by ErsatzTV |
| **Check who's live every** | 60 seconds with standard defaults |
| **Highest stream quality** | 720p with standard defaults |
| **Capture frames per second** | Chat and overlay capture rate |
| **Decode streams on the graphics chip** | Optional hardware decoding |
| **Keep a channel running after its last viewer** | 60 seconds |

Select hardware encoding under **App settings → Hardware and video encoding → Live page channels**. Lower stream quality and capture rate for constrained systems.

Status polling requires two offline checks before switching away. Changing the active streamer restarts the outgoing stream; players briefly reconnect.

## Troubleshooting

- Update **yt-dlp** when a stream remains stuck opening
- Verify the **Twitch name** or **YouTube handle** rather than the display name
- Check stream restrictions when public playback cannot open
- Verify YouTube chat is enabled when **chat is missing**
- Try another configured browser when **chat has a black backing**

The plugin uses unofficial public-site integrations without accounts; upstream changes can require an update. Restricted streams requiring sign-in are not opened anonymously.
