# Streamer channels

The Streamer channels plugin (`streamers`) makes a channel out of your favourite Twitch and YouTube streamers. Streamers are
listed in priority order:

- When someone is live, the channel shows them. If several are live, the highest in the list wins, and the others get a
  pop-up or ticker now and then ("Another streamer is also live: …").
- Their chat can be shown over the stream or in a panel beside it, laid out and styled as you like.
- While nobody is live, the channel shows a countdown to the soonest expected stream, predicted from each streamer's past
  streams, and when everyone else is expected.

Each streamer channel is a channel of its own in ErsatzTV (and Jellyfin's guide), alongside your others. It's listed on the
**Channels** page and the dashboard (marked *Streamers*), where Edit opens it on the Streamers page.

No API keys or accounts are needed: the plugin reads Twitch and YouTube the way their own sites do. That's unofficial, so a
change on their side can break something until the plugin is updated.

## Requirements

- **Chrome, Edge or Chromium** on the scheduler's computer, run hidden to draw the countdown, chat and pop-ups (as for
  [Radio shows](radio.md)).
- **yt-dlp**, which opens the streams. The **Streamers** page downloads it for you (from yt-dlp's GitHub releases), or
  installs on the PATH are found automatically. Use **Update** now and then: Twitch and YouTube change often, and new
  yt-dlp versions keep up.
- ffmpeg, as for the rest of the scheduler.

The plugin ships in `SchedularrTV-<version>-all-plugins.zip`; install it on the **Plugins** page and restart.

## Making a channel

Open **Streamers** in the sidebar and choose **Add a streamer channel**:

- **Logo**: drop an image on it (or paste an image address) for the guide and channel lists. Without one, the top
  streamer's profile picture is used.
- **Name, number and group**: the channel in ErsatzTV. A number another channel already has leaves this one out
  (the page says *Number is taken*).
- **Streamers**: Twitch or YouTube, and who. For Twitch, a name or a link (`xqc`, `https://twitch.tv/xqc`). For YouTube, a handle,
  channel id or link (`@LinusTechTips`, `UC…`, `https://youtube.com/@LinusTechTips`). Order them with the arrows: the first is
  the highest priority.
- **Chat**: over the stream (right, left, or a bottom corner), in a panel beside it (the stream shrinks to fit), or in a box of
  your own. Looks: Classic, Bubbles, Minimal (outlined text), or your own HTML template per message, with `{author}`,
  `{message}`, `{color}`, `{platform}` and `{time}`. Also set the text size, how many messages show, how long before each fades,
  and the backing's opacity. Twitch emotes, YouTube emoji and Super Chats are shown, and in Twitch chat so are
  BetterTTV, FrankerFaceZ and 7TV emotes (their global sets and the streamer's own), read from those services' public
  APIs with no key or login. Switch that off with **BetterTTV, FrankerFaceZ and 7TV emotes**.
- **When others are live too**: a pop-up card (with their picture, title, game and viewers), scrolling text, or both. Set
  how often, for how long, in which corner, and the wording (`{name}`, `{title}`, `{game}`, `{platform}`, `{viewers}`).
  Someone who has just gone live is mentioned within seconds.
- **While nobody is live**, the channel plays one of:
  - **The countdown** (the default).
  - **A replay with a card now and then.** The recording of whoever streamed last fills the picture. Every few
    minutes a card says it's a replay of their stream from when, and counts down to the next live stream.
  - **A framed replay.** The replay plays in a frame marked *REPLAY*, with its title and date, and the countdown (and
    everyone's next stream) beside it the whole time. Set its size (always 16:9) and position, or snap it left, centre or
    right. When there isn't room for the countdown beside it, the next stream moves into the title bar above the replay,
    to the right of its title; with no room above either, it goes in a narrow full-height column beside the replay, and
    only with no room at all into a card over its corner.

  A replay is the newest saved stream among the channel's streamers: a Twitch past broadcast, or a YouTube stream's
  recording, at least ten minutes long. It plays from where it has got to (a restart doesn't send it back to the
  beginning), starts over when it ends, and switches to a newer recording as soon as there is one. Someone going live
  takes over straight away. A recording that can't be opened (deleted, or subscriber-only) is skipped for the next
  newest. With nothing to replay, the countdown shows. The rest of this section's settings:
- **While nobody is live**: the heading, an optional message, colours, whether everyone else's next stream is listed, and
  optional music from a folder.

The preview beside the form shows the result with made-up streamers; switch between **Someone's live** and **Nobody's live**.

## How it works

- **Checks.** Every streamer is checked every minute (**Check who's live every**). Someone counts as offline after two
  checks in a row say so, so a hiccup doesn't switch the channel away.
- **Opening a stream.** When the channel's streamer changes, yt-dlp finds the stream's address, and the channel's stream
  restarts on it. Players reconnect for a moment. YouTube's addresses expire, so a long YouTube stream is reopened every
  four hours.
- **On air.** The stream is laid under a hidden browser page that draws the chat and pop-ups (with a see-through
  background), and the result is encoded once for ErsatzTV at 720p. Like other live channels, it only runs while someone is
  watching, plus **Keep a channel running after its last viewer**.
- **The guide** shows who's live (with their stream title), then who's expected when, as "Name (expected)" or "Name
  (scheduled)", and "Offline" between.

## Predictions

Each streamer's past streams come from:

- **Twitch:** their saved past broadcasts, and the start of their last broadcast.
- **YouTube:** the recent streams on their channel's Live tab.
- **Both:** every stream the plugin sees them do.

History is kept for 10 weeks in `plugin-data/streamers/history.json`. Recent weeks count more.

The prediction for a streamer is, in order:

1. An **announced stream**: YouTube's scheduled (upcoming) stream, or the next segment of their Twitch schedule. If their
   usual habit (below) comes more than three hours before it, that's shown first; a habit closer to the announced time is
   taken to be the same stream, at the announced time.
2. A **weekly habit**: they usually stream on this weekday around this time (days run from 4am, so a stream at 12:30am
   counts with the evening before). A slot they've already streamed in today isn't predicted again.
3. A **daily habit**, for streamers who stream most days.
4. A **rough guess** from their usual gap between streams.

The countdown shows how sure it is ("Scheduled", "Usually live", "Rough guess"); a start just missed reads "Any minute now"
for 45 minutes. Predictions are worked out in the scheduler computer's time zone, or the plugin's **Time zone for
predictions** setting. A new streamer may show "not enough past streams" until a few streams have been seen.

## Settings

The plugin's gear on the **Plugins** page:

| Setting | Default | |
| --- | --- | --- |
| yt-dlp | Downloaded copy, else found | A yt-dlp of your own |
| Browser | Found automatically | Chrome, Edge or Chromium |
| ffmpeg | App settings' | |
| Stream address | `http://localhost:5252` | How ErsatzTV reaches the scheduler, if not on this machine |
| Check who's live every | 60 s | 20 to 900 |
| Highest stream quality | 720 | The tallest picture asked of a stream |
| Time zone for predictions | This computer's | e.g. `America/New_York` |
| Capture frames per second | 15 | How smoothly the chat and pop-ups move |
| Decode streams on the graphics chip | off | `auto`, or `cuda` (NVIDIA), `qsv` (Intel), `d3d11va` (Windows), `vaapi` (Linux), `videotoolbox` (Mac) |
| Keep a channel running after its last viewer | 60 s | |
| Draw animations at the capture rate | On | The chat, pop-ups and ticker are moved on only as often as they're captured |

### Lightening the load

A live or replayed stream is the heaviest kind of channel: the stream is decoded, the chat and pop-ups are laid over it,
and the result is encoded again. The biggest savings, in order:

- **Encode on the graphics chip**: under **App settings → Hardware and video encoding → Live page channels**, choose your
  card's encoder (NVIDIA, Intel Quick Sync, AMD…). This helps every live page channel, not just streamers.
- **Decode on the graphics chip**: the plugin's **Decode streams on the graphics chip** setting (`auto` is a good start).
  A stream the chip can't decode is decoded in software instead.
- **Highest stream quality** and **Capture frames per second**: lower is lighter.

The plugin already asks for a stream's 30 frames a second copy over a 60 one of the same height where there's a choice
(the channel keeps at most 30), and the hidden browser only draws as many frames as are captured.

## Troubleshooting

- **The channel shows "… is live: opening their stream" and stays there.** yt-dlp couldn't open it; the reason is under the
  channel on the Streamers page. Update yt-dlp first. Subscriber-only, age-restricted or region-locked streams can't be
  opened without signing in.
- **A streamer always says "Couldn't reach …" or never goes live.** Check the name or handle (open their page in a browser).
  YouTube handles are case-insensitive, but must be the handle (`@name`), not the display name.
- **No chat.** Twitch chat is read anonymously and always works for public channels. YouTube chat needs the stream's chat
  to be on (it's off for some streams, and for members-only chat).
- **The chat is drawn over a black box rather than the stream.** The browser didn't draw a see-through page. Try another
  browser (Chrome rather than Edge, say) in the plugin's settings.
