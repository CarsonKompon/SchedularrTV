# Credit squeeze

The Credit squeeze plugin (`credit-squeeze`) does what networks do as a show's end credits roll: the credits shrink
into a box, and the rest of the picture says what's on next. That's the next programme's picture, title, episode and
time, and optionally the two after it. The credits keep their own sound.

It ships in `SchedularrTV-<version>-all-plugins.zip`; install it on the **Plugins** page and restart. It needs ffmpeg.

## Where the credits are

- **Chapters named for them:** a chapter called "Credits", "End Credits", "Outro" or "Ending" in the second half of an
  episode marks where they start. These come with the library, so they're squeezed from the first publish.
- **Jellyfin's media segments:** Jellyfin 10.10 and newer keep an "Outro" segment for episodes when a segment provider
  has found the credits (for example the [Intro Skipper](https://github.com/intro-skipper/intro-skipper) plugin's
  credits detection). The plugin asks Jellyfin for each episode it schedules, in the background, and remembers the
  answers in `plugin-data/credit-squeeze/credits.json`. An episode is squeezed from the next publish after its answer
  is in. Turn this off with **Ask Jellyfin for credits segments**.
- **Shows' own outros:** an end card a show plays after its episode is squeezed as a whole. These are the outro
  bumpers set under *Titles*, or the *Outro* on a collection. When a show's outro is squeezed, its episode's credits
  play as they are, so there's only one squeeze per programme.

- **A set length for the rest:** for episodes with none of the above, **Squeeze the last seconds of episodes without
  known credits** (in the plugin's settings, off by default) squeezes that much of their end anyway. A channel or block
  can set its own under *How often* (empty uses the plugin's, 0 turns it off). Only the part of an episode that plays
  to its end is squeezed.

Credits are left alone when nothing follows them on the channel, when they'd start in the first minute of the part that
plays, when they're shorter than 8 seconds, or when they're longer than the channel's limit (4 minutes by default).

## Setting it up

Channels opt in: nothing is squeezed until it's turned on in a channel's **Credit Squeeze** tab (**Squeeze credits on
this channel**), or for one of its blocks. The tab sets, for the whole channel or for each block (*Same as this
channel*, *Its own settings* or *No squeezes*):

- **Templates:** a channel (or block) can save several looks. *Main* is its own; **+ New from this one** saves a copy of
  the selected look as a new template, to change from there. Each squeeze picks one of the looks at random, by its
  **weight** (the buttons show each one's share), and the same airing always gets the same one. A template can be
  renamed, switched off (**In use**) or deleted, and a weight of 0 keeps a look out. **Layout**, **Play in the panel** and
  **Look** belong to the selected template, and **Render a preview video** uses it. **How often** and shows' own layouts
  are the channel's, whichever look is picked (a show's own layout replaces the picked look's layout, keeping the rest).
- **Layout:**
  - **Style:** *Side by side* is the classic one: the credits shrink against one edge (left, right, top or bottom)
    and the panel slides in from the far edge to fill the rest. As they end, they grow back to fill the picture and
    the panel slides away. (Framed, the background and panel fade up as the credits shrink into their box.) *Framed* puts the credits and the
    panel's picture in two boxes on a background, with the channel's name over the credits (left or right only).
  - **Credits on the** side, and their **share** of the picture (30–80%: of the width, or of the height for top and
    bottom).
  - Each side has its own options:
    - **Fit:** *Letterbox* keeps the video whole, with bars around it (black on the credits' side, the background on
      the up-next side). *Squeeze* stretches it to fill its side exactly. *Crop* fills its side and trims the edges.
    - **Credits side text:** what's ending ("You've been watching" over the show's name, both editable under *Look*)
      and a line of your own. It goes under the credits, or beside them for top and bottom.
    - **Up-next side text:** turn it off and the up-next side is just its video (the promo, else the next programme's
      picture) on the background, with no heading or listings.
  - These are part of the layout, so a show with its own layout can have its own fit and text too.
  - **Layout for:** *Every show*, or *Specific show*, which adds a picker for any show or movie in the library (not
    only what the channel airs now, since blocks and Archive Follow can play others). Shows already given a layout are
    listed under it to jump to. A show the schedule doesn't air is previewed from one of its library episodes. Some shows' credits run in a corner or need more room, so
    a show can have its own layout, either just on this channel (or block) or on every channel. Pick the show, then
    **Give it its own here** or **…on every channel**. **Use it on every channel** / **Only use it here** move it
    between the two, and **Remove** goes back to the channel's layout. A show's layout on a channel wins over its
    layout for every channel.
  - A preview beside the settings shows the layout as you change it. **Render a preview video** makes a 20-second
    squeeze from the channel's current schedule with these settings, whatever the odds. With a show picked, it uses
    that show's outro, else an episode's marked credits, else the last 20 seconds of an episode (so its layout can
    still be judged). With *Every show*, it uses the first credits or outro there is.
  - Under the preview, the tab says what Jellyfin is being asked: how many episodes are waiting, roughly how long
    that will take, and how many have credits marked. Opening the tab asks about the channel's episodes straight
    away, even before the channel's turned on.
- **Play in the panel:** the kinds of promo that may play in the panel's picture: *Up Next* (the next programme's own,
  from the breaks before it), *Now Playing* (its ident), *Coming Up* (later today) and *Eventually* (later in the week).
  One is picked at random from what the schedule airs nearby, library promo clips and generated promos alike. It plays
  once the credits are in place, fading up from black (and back down as it ends), with the credits' sound dipped under
  its own. The next programme's picture is only shown when there's no promo at all. A generated promo not made yet is waited
  for until 20 minutes before the squeeze airs. With none nearby (or a generated one not ready in time), and the
  Generated Promos plugin on, the channel's own templates make one of a ticked kind for the panel, if it has a template
  of that kind. This happens for previews too, so they take a little longer. With none ticked, or none to be had, the
  panel shows the next programme's picture.
  The panel's heading follows the promo in it: *Up next* for the next programme (or no promo), *Coming up* and
  *Eventually* for those promos, which also name the airing they're for (with its day, when it isn't today), and *Next*
  for a Now Playing ident. Each heading can be changed under *Look*.
- **How often:**
  - **Episodes' end credits:** the odds an episode with marked credits gets squeezed.
  - **Shows' own outros:** the odds for outros, with a slider for each show on the channel that has one. A show left
    alone uses the overall setting; **Reset** puts it back.
- **Look:** the heading ("Up next"), the heading and text fonts (any font installed on the server, as in the
  generated promos editor), the credits' caption and your own line, accent and background colours, a background picture or video (a video loops;
  side by side it's behind the panel, framed it's behind both boxes), the credits' volume, the longest credits to
  squeeze, whether the next two programmes are listed too, and whether the credits shrink into place over the first
  second (or start in their box).

The odds are rolled per airing, seeded by the channel and time, so a rebuild gives the same answer.

In the schedule file it's `plugins: credit-squeeze:` on a channel or block:

```yaml
plugins:
  credit-squeeze:
    enabled: true
    style: side_by_side        # or framed
    side: right                # left, right, top, bottom
    split_percent: 60
    credits_fit: letterbox     # letterbox, squeeze, crop
    credits_text: true
    panel_fit: crop
    panel_text: false          # just the video on the up-next side
    credits_caption: You've been watching
    credits_line: Thanks for watching Toons!
    heading_font: Bebas Neue
    text_font: Segoe UI
    shows:                     # shows with their own layout on this channel
      SpongeBob SquarePants:
        style: framed
        side: left
        split_percent: 45
    background_path: D:\Media\Backgrounds\loop.mp4
    promos: [up_next, coming_up]
    credits_chance: 0.75
    assumed_credits_seconds: 45   # episodes without known credits: squeeze their last 45 seconds
    outro_chance: 1
    outros:
      Rugrats: 0.5
      Hey Arnold!: 0
    longest_credits: 4m
    heading: Up next
    coming_up_heading: Coming up
    eventually_heading: Eventually
    now_playing_heading: Next
    accent_color: '#ff5c8a'
    background_color: '#0b0d14'
    credits_volume: 1
    show_later: true
    animate: true
    weight: 2                  # Main's share against the templates
    templates:                 # more looks, one picked at random per squeeze
      - name: Framed
        enabled: true
        weight: 1
        style: framed
        heading: Stay tuned
        accent_color: '#5cc8ff'
        promos: [coming_up]
```

Layouts for every channel go under `settings`. That section only holds shows' layouts; it never turns a channel on:

```yaml
settings:
  plugins:
    credit-squeeze:
      enabled: false
      shows:
        Rugrats: { style: side_by_side, side: bottom, split_percent: 55 }
```

## How it works

At each publish the plugin works out which credits to squeeze. Squeezes already made replace their credits in the
playout; the rest are made in the background, soonest first, once they air within the lead time (4 hours by default).
Until a squeeze is ready its credits (or the show's outro) play as they are, so nothing is ever late or black. A
finished squeeze is published straight away, and plays in place of the credits if it's ready before they air.

By default, squeezes are only made for channels someone is watching (going by ErsatzTV's stream activity), the way
generated promos are. Tuning in starts making that channel's squeezes, and the schedule is published again as each
one is ready. Turn **Only make squeezes for channels someone is watching** off to make them for every squeezing channel.

Each squeeze is a short video: the frame (drawn once, with the next programme's picture from Jellyfin), the credits
played into its box with ffmpeg, and the credits' own sound. Squeezes are kept by content, so identical ones are made
once. Ones no schedule has used for 3 days are deleted.

In the guide, a squeeze is part of the programme whose credits it is.

## Settings

| Setting | Default | |
| --- | --- | --- |
| Save folder | `plugin-data/credit-squeeze/renders` | ErsatzTV must be able to read it |
| Make squeezes when they air within | 240 minutes | |
| Only make squeezes for channels someone is watching | On | |
| Squeeze the last seconds of episodes without known credits | 0 (off) | 0–600; channels and blocks can set their own |
| Keep unused squeezes for | 3 days | |
| Ask Jellyfin for credits segments | On | |
| Squeeze resolution | 720p | or 1080p |
| ffmpeg | App settings' | |

The **Credit Squeeze** tab shows how many squeezes have been made, what's being made, and how many episodes Jellyfin
knows the credits of.
