# Overlays

Put screen bugs, lower thirds and other graphics over your channels, such as a logo in the corner, the show's name a few seconds in, or "Up next" before the end. You design them in a visual editor, choose when each one shows, and ErsatzTV Next draws them over the picture as it streams.

## Requirements

- The Overlays plugin (`overlays`). It's optional: install `SchedularrTV-<version>-plugin-overlays.zip` (or the all-plugins zip) from the **Plugins** page.
- The plugin is built against the scheduler itself, so install the one from the same release as your scheduler.
- A current ErsatzTV Next (one with graphics layers in its playouts).

## Setting them up

Open a channel and its **Overlays** tab.

- **Settings for** picks the whole channel or one of its blocks.
- **Overlays shown** is the channel's own, or the same as another channel's. For a block, it can also be the same as this channel's (the default) or none.
- **Add an overlay** starts from a ready-made one:
  - *Screen bug (a logo file)*: a picture placed in a corner.
  - *Screen bug (the channel's name)*: the name, designed as text.
  - *Lower third*: the show and episode across the bottom.
  - *Coming up next*: what's on next, near the end.
  - *Blank design*: an empty canvas.
- **Copy in another channel's** adds copies of another channel's overlays, which are yours to change from then on.

Each overlay has a switch to turn it off without deleting it. The order of the list is the order they're drawn in: lower in the list draws on top.

### Keeping two the same

Choose **Same as another channel** to show that channel's overlays, live.

For example, set the *Adult Swim* block on your Cartoon Network channel to the same as your standalone Adult Swim channel. Change the bug on Adult Swim and both change. `{channel}` then reads as the channel they come from (Adult Swim).

A link to a missing channel, or two channels linked to each other, shows nothing, and the tab says why.

## Designing

A **design** is drawn on a frame-sized canvas over a stand-in picture. The canvas is 1920 × 1080 by default; match it to your channels' shape.

1. Add **text**, **pictures** and **colour blocks**.
2. Drag a box to move it, or a handle to resize it. Boxes snap to the centre lines; hold Alt to place them freely.
3. Set each box's colour and opacity, and for text its font, size, alignment, bold and shadow.

Long text shrinks to fit its box.

### Text tokens

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

### AI-written text

With the [Local AI writer](local-ai.md) plugin, a text box can be **AI-written** for each programme. Say what you want ("a cheeky one-liner about the episode") and a free model on your computer writes it from the show, the episode, its description and what's next.

Captions are written in the background for the next day and a half of programmes. Until one is ready (or if the AI's writing doesn't pass its checks), the box shows its usual text.

### Videos in a design

A design can have **video** layers too: for example, an animated lower-third bar that wipes in, holds and wipes out, with your text on top of it.

- Use a video with an alpha channel, such as ProRes 4444 in a `.mov` or VP9 in a `.webm`, so the programme shows through it.
- The canvas shows a still from part-way through the video.
- ErsatzTV sizes the video by its box's width and keeps its shape, so use **Fit box to its shape** (or **Fill the canvas** for a full-frame video).
- When you pick the first video, the overlay's **Shows for** is set to the video's length. If you change it, **Show for …s, its length** puts it back.

#### Timing text around a video

Videos don't fade: their own animation brings them in and out. To keep text off the screen while the video animates, time each layer within the overlay:

- **Comes in after** (seconds) holds a layer back.
- **Goes before the end** (seconds) takes it away early.

The layer fades in and out as the overlay does. For a video that takes 1.5 s to animate in and 1 s to animate out, set the text to come in after 1.5 and go 1 before the end. A chart under the timing strip shows when each layer is seen during one appearance.

```yaml
- name: Animated lower third
  when: start
  offset_seconds: 10
  duration_seconds: 8
  fade_seconds: 0.5
  layers:
    - { kind: video, path: D:\Media\Overlays\lower-third.mov, x: 96, y: 810, width: 960, height: 180 }
    - { kind: text, text: "{title}", x: 140, y: 850, width: 860, height: 70, in_seconds: 1.5, out_seconds: 1 }
```

#### How videos are sent to ErsatzTV

Each video goes to ErsatzTV as its own graphics layer. The drawn layers between videos are drawn into separate pictures. Layers that come and go together share a picture, and the drawing order (bottom to top) is kept.

> [!TIP]
> Test a new video on a channel first. The video should play from its first frame each time the overlay appears, but that depends on how your ErsatzTV version plays graphics videos. If yours plays it on a loop instead, use the "once" timings (near the start, near the end, part-way through) rather than "every few minutes".

### File overlays

A **file** overlay is a picture or video file: a PNG with transparency, or a video with an alpha channel for an animated bug.

- It's placed at one of nine positions, with its width and margins as percents of the picture.
- *Keep inside the picture* keeps it off the black bars of letterboxed and 4:3 shows.

## When they show

| Shows | Settings |
| --- | --- |
| All the way through each programme | (fades in and out around each ad break) |
| Once, near the start | How far in it starts, how long it shows |
| Once, near the end | How long before the end it finishes, how long it shows |
| Once, part-way through | How far through (as a percent), how long it shows |
| Every few minutes through the programme | How often, when the first one is, how long each shows |
| Every few minutes by the clock | How often (e.g. every 30 minutes, on the hour and half hour), offset, how long |

Every overlay also has:

- a **fade** (in and out, the same length each), and
- a choice of **programmes only** or **everything, breaks too**.

Times are measured through the whole programme. A programme split by mid-roll breaks still gets its lower third 10 seconds in and its "Up next" before the very end, on whichever part those fall in.

A strip under the canvas shows when the overlay would appear on a 22-minute programme.

## Different pictures for different times

A file overlay can show other pictures in place of its own when their conditions hold. Add them under **Other pictures** in its editor. Each one has a picture and any mix of conditions, all of which must hold:

- **Rated**: the programme's content rating from Jellyfin (TV-Y, TV-PG, PG-13…), the episode's own or else its series'.
- **Matches a library query**: the usual query language, e.g. `show:'Rugrats' OR tag:'Halloween'` (`rating` works there too).
- **During a holiday**, **in a season**, **in a month**, or **between two dates** (`MM-DD`, wrapping the new year if need be).
- **On a day** of the week, and **between two times** (which may cross midnight).

They're checked in order as each programme starts, in the channel's time zone, and the first that holds wins. If none hold, the overlay's own picture shows.

> [!TIP]
> Leave the overlay's own picture empty and nothing shows when no condition holds. That makes a rating badge: the **Rating badge** preset comes with one entry per TV rating, so you only choose the pictures.

In YAML:

```yaml
- name: Screen bug
  kind: file
  file: D:\Media\Logos\bug.png
  variants:
    - { name: Halloween, file: D:\Media\Logos\bug-pumpkin.png, holidays: [Halloween] }
    - { name: Saturday mornings, file: D:\Media\Logos\bug-toons.png, days: [Saturday], time_from: "06:00", time_to: "12:00" }
- name: Rating
  kind: file
  when: start
  variants:
    - { file: D:\Media\Ratings\tv-y.png, ratings: [TV-Y] }
    - { file: D:\Media\Ratings\tv-pg.png, ratings: [TV-PG] }
```

## How it reaches ErsatzTV

When schedules are published, the plugin adds a graphics layer to every playout item an overlay shows on:

- **Designs** become transparent PNGs drawn for that programme (and kept, so each show's lower third is drawn once).
- **Files** are sent as they are.

Each comes with its position, opacity and timing, and ErsatzTV composites them while it encodes. Changing overlays and saving the schedule republishes straight away, without rebuilding it.

Drawn overlays are kept in the plugin's data folder (`plugin-data/overlays/rendered`). Ones no publish has used for a month are cleared out.

> [!IMPORTANT]
> ErsatzTV reads drawn overlays from that folder, so it has to be able to see it. If ErsatzTV runs in a container or on another computer, set **Folder for drawn overlays** in the plugin's settings to a folder you share with it, at the same path. File overlays' paths must work for ErsatzTV too.

> [!NOTE]
> Items with graphics are always re-encoded by ErsatzTV, even on channels set to pass video through.
