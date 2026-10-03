# TV Guide channel

The TV Guide channel plugin (`tv-guide`) makes a scrolling listings channel out of your own schedule, the way
cable systems did: the 1990s **Prevue Channel** (a blue scrolling list with a channel box and the programmes
after it) or the 2000s **TV Guide Channel** (a glossy grid with half-hour columns). Above the listings, a top
panel shows information, round-the-clock commercials, or another channel's picture, with music or a
channel's sound playing.

It's optional: install `SchedularrTV-<version>-plugin-tv-guide.zip` (or the all-plugins zip) from the
**Plugins** page and restart once. Like Generated promos, it's built against the scheduler itself, so install
the one from the same release as your scheduler.

## What you need

- **Chrome, Edge, Chromium or Brave** on the scheduler's computer: the guide is a web page drawn by one running
  hidden. It's found automatically, or choose it in the plugin's settings (its gear on the Plugins page).
- **ffmpeg**, the same one as the rest of SchedularrTV (and ffprobe beside it, to tell which commercials have
  sound).

## Making a guide

Open **Guide channels** in the sidebar and choose **Add a guide channel**. Everything you change shows at once
in the live preview beside the form, with your real listings.

| Section | Options |
| --- | --- |
| Guide | Show name, look (Prevue 1990s or TV Guide Channel 2000s), lengths it can be scheduled for |
| Listings | Time shown (1 to 3 hours), scroll speed, row height, text size, channel logos, 24-hour clock, which channels are listed |
| Colours | Background, rows, text and highlights, or the theme's own |
| Top panel | Information (clock, date, a message and a rotating "Now on…"), commercials round the clock, or a channel's picture (one channel, or a different one at random every few minutes); its height |
| Sound | Music from a folder (shuffled), the top panel's sound (the commercials, or the channel shown), a channel's sound (the old cable "barker" channel), or nothing; volume |

Untick the guide's own channel under **Channels listed** so it doesn't list itself.

- **Commercials** are library items matching a query (`tag:"Commercials"` by default). MP4 and WebM files
  play, shuffled and looping. Choose **The top panel's sound** to hear them (choosing commercials does this for
  you when the guide was playing music); otherwise the guide's music plays over them.
- **A channel's picture** is that channel's live stream from ErsatzTV, laid into the window as the guide is
  streamed. The preview shows where it goes. If the channel can't be played when the guide starts, the
  window stays empty and the station page says so.
- **Cycle through channels at random** shows a different channel every few minutes (1 to 30), from the
  channels you tick, or every listed channel when none are ticked. It never shows the same channel twice in a
  row, and asks ErsatzTV for the next one shortly before its turn so the switch is quick. With **The top
  panel's sound** (or *A channel's sound* left on "The one in the window") you hear whichever channel is up.

On air, commercials and cycling channels come from the guide's own window feed
(`/plugins/tv-guide/feed/<guide>.ts`): each commercial or channel turn is played by ffmpeg, sized to the window,
one after another. Between two of them the picture holds for a moment.

## Scheduling it

Each guide appears in the library as a show of that name (tagged `Guide`), with one "episode" per length.

- **A 24/7 guide channel:** make the guide a channel's base rotation, with lengths like `30, 60`.
- **Between programmes:** add it to a channel's interstitial shows (Breaks tab) with short lengths
  (`2, 5`), the way cable channels filled time.

The listings come from the schedules as last published, and refresh every minute.

## How it plays

When ErsatzTV reaches a guide, it opens the guide's live stream from the scheduler
(`http://<scheduler>:5252/plugins/tv-guide/live/<guide>.ts`), read as a live source, so it's joined wherever it
is. The first viewer starts it: a hidden browser shows the guide page, and ffmpeg encodes it at 720p with the
chosen sound and any channel picture laid in. Every channel airing the same guide shares one encoder, which
stops 90 seconds after the last viewer leaves. Saving a guide that's on air starts it again with the new
settings.

**ErsatzTV on another computer?** Set **Stream address** in the plugin's settings to an address that computer
can reach, e.g. `http://192.168.1.20:5252`.

## Settings

| Setting | What it does |
| --- | --- |
| Browser | Chrome, Edge or Chromium to draw with. Found automatically when empty. |
| ffmpeg | Empty uses the one from App settings. |
| Stream address | Where ErsatzTV fetches the guide from. |
| Capture frames per second | How often the page is captured (5 to 30, default 24). Higher scrolls more smoothly but uses more CPU. |
| Keep a guide running after it goes off air | Seconds a guide stays on after it's no longer watched (default 90). |
