# Credit squeeze

**Credit squeeze** shrinks end credits beside a next-programme panel, promo, or commercials. It renders replacement clips ahead of airing and leaves original credits in place until each render is ready.

## Enable a channel

1. Install **Credit squeeze**, restart, and verify **ffmpeg**.
2. Open the channel's **Credit Squeeze** tab.
3. Enable **Squeeze credits on this channel**.
4. Choose the layout, panel content, and frequency.
5. Select **Render a preview video**.
6. Select **Save changes**.

Blocks can use **Same as this channel**, **Its own settings**, or **No squeezes**.

## Locate credits

| Source | Behavior |
| --- | --- |
| **Credits chapters** | Chapters named Credits, End Credits, Outro, or Ending in the second half of the episode |
| **Jellyfin Outro segments** | Background lookup against Jellyfin 10.10+ with detected media segments |
| **Show outros** | Configured outro bumpers squeezed as a whole, instead of episode credits |
| **Assumed ending length** | Optional final-seconds fallback for episodes without known credits |

Squeezes require following programming. Credits shorter than **8 seconds**, earlier than the first minute of the playing segment, or longer than the configured maximum are skipped.

## Choose the layout

| Control | Choices |
| --- | --- |
| **Style** | Side by side or Framed |
| **Credits on the** | Left/right; side-by-side also supports top/bottom |
| **Share** | 30–80% of width or height |
| **Fit** | Letterbox, Squeeze, Crop |
| **Credits side text** | Programme caption and custom line |
| **Up-next side text** | Listings and heading, or video only |

**Layout for → Specific show** creates show-specific layouts. **Give it its own here** limits an override to the channel/block; **…on every channel** creates a global show layout, with local overrides taking precedence.

## Save multiple looks

1. Select **+ New from this one**.
2. Name and edit the copied template.
3. Set its **weight** and **In use** switch.
4. Preview the selected template and save.

Each airing chooses a stable weighted template. A weight of `0` excludes a look; show-specific layouts replace the chosen template's layout while retaining its other settings.

## Play promos or commercials in the panel

**Play in the panel** supports **A promo**, **Commercials**, or **A promo, then commercials for the rest**. Eligible promo kinds are **Up Next**, **Now Playing**, **Coming Up**, and **Eventually**.

The plugin uses nearby library/generated promos and can request a render from available channel templates. Without a usable promo, the panel shows the next programme's picture.

A promo longer than the credits can expand to full screen and consume time from the following break. It is cut if necessary to preserve the next programme's start.

## Control frequency and rendering

**How often** sets separate probabilities for episode credits and show outros. Choices are seeded per airing, so rebuilding preserves them.

| Plugin setting | Default |
| --- | --- |
| **Save folder** | `plugin-data/credit-squeeze/renders` |
| **Make squeezes when they air within** | 240 minutes |
| **Only make squeezes for channels someone is watching** | On |
| **Squeeze the last seconds of episodes without known credits** | 0, disabled |
| **Keep unused squeezes for** | 3 days |
| **Ask Jellyfin for credits segments** | On |
| **Squeeze resolution** | 720p |

ErsatzTV must read the **Save folder**. The channel tab reports rendering progress and known credit boundaries; tuning in starts watched-only rendering.

## In the schedule file

Place this fragment inside a channel or block:

```yaml
plugins:
  credit-squeeze:
    enabled: true
    style: side_by_side
    side: right
    split_percent: 60
    credits_fit: letterbox
    panel: promo_then_commercials
    promos: [up_next, coming_up]
    credits_chance: 0.75
    outro_chance: 1
    assumed_credits_seconds: 0
    longest_credits: 4m
    heading: Up next
    show_later: true
    animate: true
```

Global show-layout overrides belong under **`settings.plugins.credit-squeeze.shows`**. They do not enable squeezing on a channel.
