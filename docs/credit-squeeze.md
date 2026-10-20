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

Choose **Design the squeeze with → Preset layout** for the controls below, or **Canvas Editor** to build a layered design.

| Control | Choices |
| --- | --- |
| **Style** | Side by side or Framed |
| **Credits on the** | Left/right; side-by-side also supports top/bottom |
| **Share** | 30–80% of width or height |
| **Fit** | Letterbox, Squeeze, Crop |
| **Credits side text** | Programme caption and custom line |
| **Up-next side text** | Listings and heading, or video only |

**Layout for → Specific show** creates show-specific layouts. **Give it its own here** limits an override to the channel/block; **…on every channel** creates a global show layout, with local overrides taking precedence.

## Design a squeeze on the canvas

1. Select **Canvas Editor** in the chosen look.
2. Choose an **aspect ratio**, then **Create canvas**. The starting canvas contains only the **Source credits video**, filling the entire screen.
3. Move and resize the source layer. Its default **Stretch / squeeze to bounds** mode compresses the picture to the layer's exact width and height. **Fit** preserves its shape; **Fill** crops the edges.
4. Add text, shapes, images, or video layers. A video layer can play the **Source credits video**, the **Promo / commercials panel**, or a file. Put artwork behind the source with **Backward**, or above it with **Forward**.
5. For motion, resize the source to its destination and select **Animate from full screen to this box**. Or add keyframes at the playhead and edit their bounds. Keyframe times are seconds from the start of the credits; the last pose holds for the rest of the credits.
6. Render a preview, then save the channel. The preview uses the canvas currently selected in the editor.

Each look can have **16:9**, **4:3**, **16:10**, **1:1**, **21:9**, and **9:16** canvases. At render time the show's display aspect ratio (including anamorphic pixels) selects the closest canvas. A single canvas is always used, even if the show or channel output has a different shape. Unknown source shapes are treated as 16:9.

Before squeezing, the source is fitted to the canvas shape using the channel's **encoding scaling mode**, inheriting the app-wide setting when unset: fit keeps the whole picture with black bars; crop fills the picture. Those bars are part of the source layer and resize with it. The canvas keeps its authored aspect ratio; the playout encoder handles conversion to the channel's final output shape. For example, a sole 4:3 canvas still renders for a 16:9 channel.

The editor shows labelled stand-in videos, with a selectable stand-in source ratio to check bars. **Render a preview video** uses actual credits and panel content. Text supports `{show}`, `{next}`, `{time}`, `{channel}`, and `{heading}`.

Canvas designs run for the credits' duration and use their own layer timing/keyframes; the preset's automatic shrink/return and full-screen promo overrun do not apply. Show-specific preset-layout overrides apply to preset looks. Canvas designs and their image/video files are included when copying or exporting a look.

### Credits Text: keep the names while replacing the video

**Credits Text** is a movable, resizable text layer populated by OCR of the original source credits. It works even if the source-video layer ends, animates away, or is removed entirely, so a bumper or promo can replace the picture while the names stay on screen.

1. Install **Tesseract OCR** and its language data on the machine running SchedularrTV. In the **Credit squeeze plugin settings**, set **Tesseract OCR executable**, or leave it empty when `tesseract` is on PATH. On Debian/Ubuntu the English packages are `tesseract-ocr` and `tesseract-ocr-eng`; on Windows select the installed `tesseract.exe`.
2. Set **Credits OCR languages** to the installed language code(s), such as `eng` or `eng+fra`. **Read a credits frame every** defaults to **1 second**; keep that for scrolling or fast-changing credits.
3. In a squeeze canvas, click **Credits Text**. Place its box, choose font, size, colour and alignment, and set **Source lines per page** (default **8**). Long lines can wrap to the box; the font shrinks when needed to keep a page inside it.
4. Set the layer's **Starts** and **Ends** times if needed. All extracted lines are divided into pages that share that active interval equally. For example, three pages on a layer active from 2–20 seconds start at 2, 8 and 14 seconds. Leaving Ends blank uses the end of the squeeze. Layer keyframes animate the text box independently; changing pages does not restart its animation.
5. End or animate away the original video and place the promo/bumper as desired. OCR still reads the original, unsqueezed source segment, not the composite or the replacement clip.
6. **Render a preview video**, then expand **Review OCR credits text** to inspect the recognized lines and low-confidence count. Credits Text previews read the chosen credits segment rather than shortening it to the usual 20 seconds. The canvas itself shows clearly labelled sample lines over a 60-second demo.

OCR preserves recognized wording, accents and punctuation; it does not generate copy or rewrite names. Repeated cards and overlapping scrolling lines are joined before pagination. OCR is not guaranteed to be an exact transcription: stylized fonts, poor footage, clipped lines, rapid rolls and incorrect credits markers can lead to mistakes or omissions. Check the preview; high OCR confidence is not a guarantee of correctness. Names shown only between sampled frames can be missed.

Extraction runs only for canvases containing Credits Text. Successful transcripts are cached under the squeeze save folder's `_credits-text` directory by source file/version, source start, duration, OCR language and sampling settings. Changing layout or lines per page reuses the transcript; changing the source or OCR settings reads it again. The saved layer settings travel with copied/exported templates, while episode transcripts remain in the local cache.

If Tesseract or its language data is unavailable, the scan times out, or no text is readable, the squeeze reports an error instead of silently rendering blank credits. On air the original credits remain in place until a replacement is ready.

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
