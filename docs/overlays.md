# Overlays

**Overlays** adds logos, lower thirds, captions, and timed graphics to programmes. ErsatzTV Next composites them during encoding.

## Add an overlay

1. Install **Overlays** from the scheduler's release and restart.
2. Verify ErsatzTV Next supports graphics layers.
3. Open a channel's **Overlays** tab.
4. Choose the channel or block under **Settings for**.
5. Select **Add an overlay** and choose a preset.
6. Edit its design, timing, and programme/break scope.
7. Select **Save changes** and test the live channel.

Presets include logo and text screen bugs, lower thirds, coming-up graphics, rating badges, and a blank design. Lower entries in the list draw above earlier entries.

## Design graphics

- Add **text**, **pictures**, **shapes**, or **video layers**
- Drag boxes and resize handles on the canvas, and turn them with the round handle (Shift for 15° steps)
- Hold **Alt** to bypass center-line snapping
- Set fonts, colors, opacity, alignment, and shadows
- Match the canvas shape to the output channel

Long text shrinks to its box. [Local AI writer](local-ai.md) can generate captions while retaining the typed text as fallback.

The canvas editor works like the [Generated promos](generated-promos.md) one:

- **Layers** lists the top first, with each group's layers indented under it (▸ folds a group). Ctrl (⌘) + click or Shift + click selects several, in the list or on the canvas. **Drag rows** to reorder them; dropping on the middle of a group's row puts them inside it. **Lock** a layer with 🔓 so it can't be picked or moved on the canvas (locking a group locks what's inside). **Group** puts the selected layers in a group without moving them; **Ungroup** takes them back out.
- **+ Shape** adds a rectangle that can be changed to an **Ellipse**, **Triangle**, **Diamond** or **Star**, with **Soft edges** and, for rectangles, **Corner radius**. Older color blocks are rectangles and look as before.
- A shape or picture can be a **mask** instead of artwork: under **Use as**, choose **Add mask — reveal inside** or **Subtract mask — hide inside**, then the **Mask target** beneath it. Picture masks use the picture's transparency. Masks can't target groups, videos or other masks.


- **Animated GIFs** (and animated WebP) play in picture layers, variable images and file overlays. In a design, a GIF plays from when its layer comes in, looping, and makes the design animated: it airs as a transparent video of each appearance, like a keyframed one (a whole-programme overlay plays it for its **Animation length**, then holds). A file overlay's GIF is turned into a transparent video once, in the background, and shows as its still picture until then.

### Timeline and keyframes

Drag along the timeline under the canvas to scrub through one appearance: layers coming in late or going early fade as they will on air. For a whole-programme overlay, the timeline is its **Animation length** (10 seconds unless set under Timing). The **« ‹ › »** buttons and **← / →** (Shift for a second), **Home / End**, **[ / ]**, **K** and **Delete** work as in Generated promos.

Video layers show their frame at the playhead, playing from when the layer comes in (as on air) and holding their last frame after they end. The first time a video is scrubbed, the frames the timeline covers are taken once in the background (kept under the plugin's `stills/frames` folder and cleared after a week unused); until then each frame is fetched as you land on it.

**Frame rate** (under Canvas) sets the timeline's frames, which keyframes land on, and the rate an animated design's video is made at. It's 30 unless set. Choosing the design's first video sets it to that video's rate, and **Match the video's … fps** sets it to another's, so keyframed layers line up frame for frame with the video under them. In YAML it's `fps: 25`.

Select a layer or group and choose **◆ Animate with keyframes**. Its position, size, turn and opacity then come from keyframes: move the playhead and drag, resize or turn the box (or change its numbers) to set one there. Drag a diamond to retime it; **Then** sets the easing to the next one. A group's animation carries everything inside it. Video layers are placed by ErsatzTV, so they can't be animated, turned or masked.

On air, an animated design is sent as a transparent video (VP9 WebM, or ProRes 4444 if ffmpeg lacks VP9) of each appearance, made in the background with the ffmpeg set for Generated promos, for programmes in the next 12 hours; the schedule is published again as each is ready. Until then the design shows standing still. A whole-programme animated overlay plays its animation as each part of the programme starts, then stays as it ends up. In YAML:

```yaml
layers:
  - id: bar
    name: Bar
    kind: rect
    shape: ellipse # rectangle (default), ellipse, triangle, diamond, star
    soft_edges: 6
    x: 80
    y: 830
    width: 940
    height: 140
    keyframes:
      - at: 0s
        x: -960
        y: 830
        width: 940
        height: 140
        opacity: 100
        easing: ease_out
      - at: 0.8s
        x: 80
        y: 830
        width: 940
        height: 140
        opacity: 100
  - name: Cutout
    kind: rect
    parent: group_id # inside a group (kind: group, id: group_id)
    mask:
      mode: subtract # or add
      target: bar
```

Keyframe times are from the start of each appearance and opacity is 0–100, like the layer's own. `rotation` is degrees clockwise, `locked: true` only affects editing, and a whole-programme overlay's animation length is `animation_seconds`.

### Text tokens

| Token | Value |
| --- | --- |
| **`{title}`** | Show or movie title |
| **`{episode}`** | Episode title |
| **`{season_episode}`** | Season and episode, such as `S02E05` |
| **`{year}`** | Premiere year |
| **`{channel}`** | Channel name, or referenced channel's name |
| **`{next}`** | Next programme |
| **`{next_time}`** | Next start time |

### Video and file overlays

Transparent **ProRes 4444 MOV** or **VP9 WebM** supports animated graphics over the programme. **Fit box to its shape** preserves video proportions.

Use **Comes in after** and **Goes before the end** to keep text clear of a video's entrance and exit animation. Video layers use their own animation rather than overlay fades.

A **file overlay** places an existing image or video at a preset position with percentage sizing and margins. **Keep inside the picture** avoids letterbox bars.

## Variable images for overlays and screenbugs

Use **Variable images → + Variable image** to create an image slot, then choose its **Shared image**, or **New shared image…**. Shared images are the same ones [Generated promos](generated-promos.md#shared-images) use, kept once for the whole schedule: **Edit shared images…** sets each one's **default image**, **per-show and per-block overrides**, and an optional [automatic picture](generated-promos.md#automatic-pictures-for-shared-images) found with a picture search (such as `{show} main character png`) for shows without an override. Show titles match loosely. A variable from before shared images keeps its own pictures until **Move to a shared image**.

Automatic pictures for programmes in the next 36 hours are looked for in the background, and the schedule is published again as they're found; until then the shared image's default shows. Exported overlay packages carry the shared images they use, merged by id on import.

- **Currently watching / before the break** uses the programme being shown. During an ad break it uses the preceding programme, when the overlay is set to appear during breaks.
- **Up next** selects the next show, two shows ahead, or up to ten shows ahead. Continuation segments, intros/outros, and stacked episodes are skipped; consecutive listings of the same show count once.

For a canvas design, add/select a **Picture** layer and choose the variable under **Image source**. The image keeps the layer's position, size, opacity, and timing. For a file-based screenbug, choose the variable under **File source**; placement, sizing, margins, and fades work as usual.

Each use has a **For which show** selector: **Its usual one**, **Currently watching / before the break**, **Up next**, **Up next 2**, through **Up next 10**. This lets several layers use the same variable for different shows at once. Leaving it at **Its usual one** uses the variable's **Usual show** setting. The preview and live output both honor this choice. In YAML, set `variable_image_show: watching`, `next`, `next2` … `next10` beside `variable_image` on the image layer or file overlay.

Shows without an override (or automatic picture) and unavailable schedule contexts use the default image. With no default, that image is hidden. Matching **Other pictures** variants (such as holiday or rating badges) take priority over a screenbug's variable image.

Use **Preview show contexts** to enter a current show and upcoming titles and see their images on the canvas; a picture layer showing a variable with an automatic picture says what was found for the preview's show. These fields only affect the preview; the live overlay uses the actual schedule.

Example inside an overlay's YAML (the shared image itself is under `settings.plugins.generated-promos.shared_images`, as for Generated promos):

```yaml
kind: file
variable_image: variable_image_01
variable_images:
  - name: variable_image_01
    context: currently_watching
    shared: 6f1c2b0e9a8d4c7fb3e2a1d0c9b8a7f6
```

Older overlays whose variables carry their own `default_path` and `shows` keep working.

For an upcoming show, use `context: up_next` and `offset: 0` for next or `offset: 1` for two shows ahead. Canvas image layers reference the same slot with `variable_image: variable_image_01` instead of `path`.

## Set timing

| Timing | Controls |
| --- | --- |
| **Whole programme** | Fade around breaks |
| **Near start** | Offset and duration |
| **Near end** | End offset and duration |
| **Part-way through** | Percentage and duration |
| **Every few minutes through the programme** | Interval, first appearance, duration |
| **Every few minutes by the clock** | Clock interval, offset, duration |

Programme timing spans all mid-roll segments. Choose **programmes only** or **everything, breaks too** for scope.

Test animated video on the installed ErsatzTV version; repeated graphics-video timing depends on its playback behavior.

## Share or override designs

**Same as another channel** follows that channel's current overlays. **Copy in another channel's** creates independent copies.

Blocks can inherit the channel, use their own overlays, follow another channel, or show none. Missing or circular references show no overlays and report the problem in the editor.

## Select alternate pictures

1. Open a file overlay's **Other pictures** section.
2. Add a picture and its conditions.
3. Order variants from most specific to least specific.
4. Save the schedule.

Conditions cover ratings, library queries, holidays, seasons, months, date ranges, weekdays, and time ranges. All conditions on a variant must match; the first matching variant wins in the channel's time zone.

## Files and output

Designs render to cached transparent PNGs under **`plugin-data/overlays/rendered`**. Video layers and file overlays retain their source files.

ErsatzTV must read all graphic paths. For remote or containerized playout, set **Folder for drawn overlays** to shared storage at a path both processes can read.

Saving overlay changes republishes graphics. Items with overlays are re-encoded even when the channel otherwise uses passthrough.
