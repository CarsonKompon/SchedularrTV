# Overlays

The Overlays plugin (`overlays`) puts screen bugs, lower thirds and other graphics over your channels: a channel logo
in the corner, the show's name a few seconds in, "Up next" before the end. You design them in a visual editor, say when
each one shows, and ErsatzTV Next's graphics engine draws them over the picture as it streams.

It's optional: install `SchedularrTV-<version>-plugin-overlays.zip` (or the all-plugins zip) from the **Plugins** page.
It's built against the scheduler itself, so install the one from the same release. It needs a current ErsatzTV Next
(one with graphics layers in its playouts).

## Setting them up

Open a channel and its **Overlays** tab.

- **Settings for** picks the whole channel or one of its blocks.
- **Overlays shown** is the channel's own, or the same as another channel's. For a block, it can also be the same as
  this channel's (the default) or none.
- **Add an overlay** starts from a ready-made one:
  - *Screen bug (a logo file)*: a picture placed in a corner.
  - *Screen bug (the channel's name)*: the name, designed as text.
  - *Lower third*: the show and episode across the bottom.
  - *Coming up next*: what's on next, near the end.
  - *Blank design*: an empty canvas.
- **Copy in another channel's** adds copies of another channel's overlays, yours to change from then on.

Each overlay has a switch to turn it off without deleting it, and the order of the list is the order they're drawn in:
lower in the list draws on top.

### Keeping two the same

Choose **Same as another channel** to show that channel's overlays, live. For example, set the *Adult Swim* block on
your Cartoon Network channel to the same as your standalone Adult Swim channel: change the bug on Adult Swim and both
change. `{channel}` then reads as the channel they come from (Adult Swim). A link to a missing channel, or two
channels linked to each other, shows nothing and the tab says why.

## Designing

A **design** is drawn on a frame-sized canvas (1920 × 1080 by default; match your channels' shape) over a stand-in
picture. Add **text**, **pictures** and **colour blocks**, then drag a box to move it or a handle to resize it. Boxes
snap to the centre lines; hold Alt to place them freely. Each box has its own colour, opacity and, for text, font, size,
alignment, bold and shadow. Long text shrinks to fit its box.

Text can hold tokens, filled in for each programme:

| Token | Reads as |
| --- | --- |
| `{title}` | The show's name (or the film's) |
| `{episode}` | The episode's title |
| `{season_episode}` | e.g. S02E05 |
| `{year}` | The year it first aired |
| `{channel}` | The channel's name (the one it's linked to, when it's linked) |
| `{next}` | What's on next |
| `{next_time}` | When that starts |

With the [Local AI writer](local-ai.md) plugin, a text box can be **AI-written** for each programme: say what you
want ("a cheeky one-liner about the episode") and a free model on your computer writes it from the show, episode,
its description and what's next. Captions are written in the background for the next day and a half of
programmes; until one is ready (or if the AI's writing doesn't pass its checks) the box shows its usual text.

A **file** overlay is a picture or video file (a PNG with transparency, or a video with an alpha channel for an
animated bug), placed at one of nine positions with its width and margins as percents of the picture. *Keep inside
the picture* keeps it off the black bars of letterboxed and 4:3 shows.

## When they show

| Shows | Settings |
| --- | --- |
| All the way through each programme | (fades in and out around each ad break) |
| Once, near the start | how far in it starts, how long it shows |
| Once, near the end | how long before the end it finishes, how long it shows |
| Once, part-way through | how far through (as a percent), how long it shows |
| Every few minutes through the programme | how often, when the first one is, how long each shows |
| Every few minutes by the clock | how often (e.g. every 30 minutes, on the hour and half hour), offset, how long |

Every overlay has a **fade** (in and out, the same length each) and is on **programmes only** or **everything,
breaks too**. Times are measured through the whole programme, so one split by mid-roll breaks still gets its lower
third 10 seconds in and its "Up next" before the very end, on whichever part those fall in. A strip under the canvas
shows when it would appear on a 22-minute programme.

## How it reaches ErsatzTV

When schedules are published, the plugin adds a graphics layer to every playout item an overlay shows on: designs as
transparent PNGs drawn for that programme (and kept, so each show's lower third is drawn once), files as they are,
with their position, opacity and timing. ErsatzTV composites them while it encodes. Changing overlays and saving the
schedule republishes straight away, without rebuilding it.

Drawn overlays are kept in the plugin's data folder (`plugin-data/overlays/rendered`), and ones no publish has used
for a month are cleared out. ErsatzTV reads them from there, so it has to be able to see that folder: if it runs in a
container or on another computer, set **Folder for drawn overlays** in the plugin's settings to a folder you share with
it, at the same path. File overlays' paths must work for ErsatzTV too.

Items with graphics are always re-encoded by ErsatzTV, even on channels set to pass video through.
