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

- Add **text**, **pictures**, **color blocks**, or **video layers**
- Drag boxes and resize handles on the canvas
- Hold **Alt** to bypass center-line snapping
- Set fonts, colors, opacity, alignment, and shadows
- Match the canvas shape to the output channel

Long text shrinks to its box. [Local AI writer](local-ai.md) can generate captions while retaining the typed text as fallback.

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

Use **Variable images → + Variable image** to create an image slot with a **default image** and **per-show overrides**, just like Generated Promos. Show titles match without regard to case.

- **Currently watching / before the break** uses the programme being shown. During an ad break it uses the preceding programme, when the overlay is set to appear during breaks.
- **Up next** selects the next show, two shows ahead, or up to ten shows ahead. Continuation segments, intros/outros, and stacked episodes are skipped; consecutive listings of the same show count once.

For a canvas design, add/select a **Picture** layer and choose the variable under **Image source**. The image keeps the layer's position, size, opacity, and timing. For a file-based screenbug, choose the variable under **File source**; placement, sizing, margins, and fades work as usual.

Unassigned shows and unavailable schedule contexts use the default image. With no default, that image is hidden. Matching **Other pictures** variants (such as holiday or rating badges) take priority over a screenbug's variable image.

Use **Preview show contexts** to enter a current show and upcoming titles and see their images on the canvas. These fields only affect the preview; the live overlay uses the actual schedule. Variables and all their default/per-show files are included in exported overlay packages and deep-copied when copying overlays.

Example inside an overlay's YAML:

```yaml
kind: file
variable_image: variable_image_01
variable_images:
  - name: variable_image_01
    context: currently_watching
    default_path: 'D:\Media\Logos\default.png'
    shows:
      - show: SpongeBob SquarePants
        path: 'D:\Media\Logos\spongebob.png'
```

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
