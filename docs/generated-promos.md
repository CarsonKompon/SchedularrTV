# Generated promos

**Generated promos** renders scheduled announcements from visual templates, voice-overs, and music. Until a render is ready, its reserved slot uses stand-in commercials.

## Set up

1. Install **Generated promos** from the same release as the scheduler.
2. Enable the built-in **Promos** plugin.
3. Verify **ffmpeg** and **ffprobe** under **App settings**.
4. Install and configure a voice plugin if speech is required.
5. Open a channel's **Generated Promos** tab.

## Create a template

1. Choose the channel or block under **Settings for**.
2. Add a template and set its type and duration.
3. Add the matching show layer (or variable images), text, images, and background.
4. Set the voice and optional **Background music**.
5. Drag or resize layers, scrub the timeline to check transitions, and add keyframes to animate layers.
6. Select **Render preview video**.
7. Select **Save changes**.

Hold **Alt** while dragging to bypass snapping. **Browse** opens a file dialog on the scheduler's computer; from another device, enter a server-readable path.

Drag along the timeline under the canvas to scrub: the preview redraws as you drag, a whole frame at a time, showing the background video's exact frame for that moment, the same one the render uses. The first time a background video is previewed, the frames the promo uses are extracted once in the background; until then each frame is fetched as you land on it, so scrubbing is quicker after a few seconds.

The **« ‹ › »** buttons step a frame or a second. After clicking the timeline or dragging a box, these keys work:

| Key | Does |
| --- | --- |
| **←** / **→** (or **,** / **.**) | Previous / next frame |
| **Shift+←** / **Shift+→** | Back / forward a second |
| **Home** / **End** | First / last frame |
| **[** / **]** | Selected layer's previous / next keyframe |
| **K** | Add a keyframe at the playhead |
| **Delete** | Remove the keyframe at the playhead |

### Counting upcoming programmes

**Count upcoming programmes by** is a template-wide setting shared by **Up Next text, variable images, TTS, external generator listings, and background-rule lookahead**:

- **Timeslot / episode (follow the channel's slots)** — the default. Each scheduled timeslot counts once. If the channel packs multiple episodes into one timeslot, those episodes stay together. If packing is disabled, each episode counts as its own slot, even when consecutive episodes have the same show title.
- **Show (group consecutive slots of the same show)** — combines consecutive slots with the same show title into one entry. A show returning after a different show is a new entry.

Mid-roll segments, intros, outros, and adverts never count as additional upcoming programmes. A listing's end covers its last programme segment/stacked episode, excluding the trailing break.

| Schedule | Timeslot / episode (default) | Group by show |
| --- | --- | --- |
| One slot containing two Regular Show episodes, then a Gumball slot | Regular Show → Gumball | Regular Show → Gumball |
| Two separate Regular Show episode slots, then Gumball | Regular Show → Regular Show → Gumball | Regular Show → Gumball |

In every mode, the second Up Next text entry, variable-image `next2`, and TTS `{up_next_2}` refer to the same listing. Repeat wording such as “more Regular Show” only changes the spoken wording, not which listing was chosen.

In YAML, set `upcoming_grouping: timeslot` (default) or `upcoming_grouping: show` directly on the template. The former voice-only `use_individual_episodes` option is no longer used; old files containing it still load and use the shared setting, defaulting to timeslots/episodes.

### Text style

Text, show and Up Next layers shrink their text to fit the box. The **Align in its box** pad sets both directions at once: left, centre or right, and top, middle or bottom. **Letters** can make every letter uppercase or lowercase, including show titles and AI-written text. In YAML these are `align`, `vertical_align` (`top`, `middle`, `bottom`; top when left out) and `text_case` (`upper` or `lower`; as written when left out).

### Backgrounds

Add one or more background images or videos. With several, each promo picks one at random; the pick is fixed by what the promo is, so re-rendering it looks the same. **Preview** on a background shows it on the canvas instead of the one the sample shows would get. In YAML, the first is `background` and the rest are `backgrounds`:

#### Choosing backgrounds by upcoming shows

Under each background, expand **Background selection**, enable **Choose based on upcoming shows**, then enter one or more show names, one per line. This section starts collapsed even when rules are enabled; its summary shows whether show matching is on and any daily time range. A match against **any** name triggers the rule, using the same loose title matching as variable images (case, punctuation and small spelling differences are tolerated).

- **Prefer on match:** the background keeps normal chance when no show matches, and gets the configured **Relative chance when matching** when one does. Normal weight is 1: weight 5 against one ordinary background gives a 5-in-6 chance, not a guaranteed choice.
- **Only when matching:** the background is completely excluded unless a show matches. Other eligible backgrounds can still be chosen. To make a background the sole possible choice for particular shows, ensure the other backgrounds' rules exclude them in that situation.
- **Number of upcoming shows:** 1 checks only the next entry; up to 10 checks that many entries using the template's shared timeslot/episode or show grouping.
- **Time from the promo's start:** checks shows starting within the next 1–1440 minutes, including the boundary. This can reach beyond ten shows. It uses the available schedule, not predictions beyond it.

For **Now Playing** templates, these rules match the show the promo identifies, even if it started earlier. The editor labels this **Choose based on the Now Playing show** and hides lookahead controls, which aren't used for this type. Both preference weights and **Only when matching** apply to that show.

Other promo types inspect upcoming shows, even for a promo advertising a different show. Rules don't add shows to the promo's text or voice-over. Unrestricted backgrounds remain eligible. If every background is excluded, the background colour is used. An episode clip still takes precedence when configured and available. Choices are repeatable for a given promo, and changes affecting the selected background update its render cache key.

**Preview** and **Edit** on a particular background deliberately bypass its rule so it can be inspected and laid out. Return to the automatic preview to see the rule-based choice. Rules follow renamed/replaced background files and template package exports/imports.

```yaml
backgrounds:
  - backgrounds/general.png
  - backgrounds/cartoons.png
background_rules:
  - background: backgrounds/cartoons.png
    shows: [Rugrats, SpongeBob SquarePants]
    next_shows: 3
    only_when_matching: true
    match_weight: 5
    # Alternatively use within: '01:30:00' to check the next 90 minutes.
```

#### Daily background time ranges

In a background's **Background selection** section, enable **Only choose during a daily time range** and set **From** and **Until**. This works for images and videos, either on its own or combined with show matching.

- Uses the promo's scheduled start in the **channel's timezone**, including its daylight-saving changes, rather than the computer's clock or when the render runs.
- Start is inclusive and end is exclusive: **06:00–12:00** permits morning promos but not one starting at noon.
- An earlier end crosses midnight: **22:00–06:00** permits late-evening and overnight promos.
- Start and end must differ; disable the time-range switch to allow any time.
- Outside the range, a background is excluded even if its show matches. Ordinary unrestricted backgrounds remain eligible, and the background colour is used if none is eligible.

For a morning-only background without a show condition:

```yaml
background_rules:
  - background: backgrounds/morning.mp4
    match_shows: false
    time_range:
      start: '06:00:00'
      end: '12:00:00'
```

For a combined rule, leave out `match_shows: false`, add the show settings as above, and add `time_range` to the same rule. Explicit **Preview/Edit** still lets you inspect backgrounds outside their eligibility hours. Time ranges travel with the other rules when backgrounds are replaced or templates exported/imported.

#### Colours and shapes

Text and shape layers have **Pick from screen** beside their colour selector. Click it, then click a pixel anywhere on your screen; **Escape** cancels. Picking keeps the layer's colour transparency and respects the background currently being edited. Screen picking requires a browser with the EyeDropper API (such as Chrome or Edge), on HTTPS or localhost; an explanation appears when it is unavailable.

**+ Shape** adds a rectangle that can be changed to an **Ellipse**, **Triangle**, **Diamond**, or **Star**. Use equal width and height for a circle. **Rotation (degrees)** turns the shape clockwise about its centre. **Soft edges (pixels)** feathers its outline (0 is sharp, up to 200); rectangles also retain **Corner radius**. These settings affect both the preview and rendered video, and apply across backgrounds. Position, size and opacity can still be animated with keyframes; rotation and softness are static settings.

Existing rectangle templates keep their appearance. In YAML, shapes retain `kind: rect` for compatibility and add `shape: ellipse` (or `rectangle`, `triangle`, `diamond`, `star`), `rotation: 30`, and `soft_edges: 8` as needed. Leaving these out gives the original sharp, unrotated rectangle.

#### Placing layers for one background

Layers can be placed and animated differently over each background, to follow what's in a particular video, while everything else (text, variable images, voice, music, timing) stays shared:

1. Select **Edit** next to the background. The canvas switches to it and a banner shows which background you're placing for.
2. Move, resize, change opacity or keyframe layers as usual. The first change to a layer gives that background its own copy of the layer's position, size, opacity and keyframes (⌖ marks it); layers you don't touch keep following the template.
3. Change a layer's **Colour** to use a different one over this background, such as darker text over a light picture. A colour alone doesn't copy the layer's position, so it still moves with the template.
4. Change **Appears at**, **Leaves at**, **Enter**, **Exit** or **Transition length** to time the layer differently over this background. Like colour, timing is kept on its own; **Use the template's** next to the timing heading resets just the timing.
5. **Use the template's** at the top of the layer settings drops everything that background sets for the layer: position, keyframes, timing and colour. **Done** goes back to editing the template's own layout.

The **Edit** button shows how many layers a background places its own way. Swapping a background's file keeps its placements; removing it drops them. Over an episode clip, layers use the template's layout. In YAML, placements are under `background_overrides`, each naming its `background` and listing `layers` by the layer's `id`, `colors` (each a `layer` id and a `color`), and `timings` (each a `layer` id with `start`, `end`, `transition_in`, `transition_out` and `transition_duration`).

A background video plays its own sound by default, looping with the picture. Under the background list, untick **Play the background video's sound** to mute it, or set its volume and whether it dips while the voice speaks. Videos without a sound track play silently. The episode clip has its own sound settings and isn't affected. **Lower the music while the episode clip's (or background video's) sound plays** also follows a background video's sound.

```yaml
background_sound: # Optional; left out, a video's sound plays at full volume and dips under the voice
  enabled: true
  volume: 0.6
  duck_under_voice: true
background: 'D:\Media\Promos\Backgrounds\blue.mp4'
backgrounds:
  - 'D:\Media\Promos\Backgrounds\red.mp4'
  - 'D:\Media\Promos\Backgrounds\stars.png'
```

### Keyframes

Select a layer and choose **◆ Animate with keyframes** under the timeline. Its position, size and opacity then come from keyframes, shown as diamonds on the timeline while the layer is selected:

- Move the playhead and drag or resize the box (or change its numbers) to set a keyframe at that time; one is added if there isn't one there yet. **◆ Add keyframe** adds one without changing anything.
- Drag a diamond to retime it; keyframes snap to whole frames. Clicking one jumps to it; **◀ / ▶** (or **[ / ]**) step between them, **K** adds one and **Delete** removes the one under the playhead.
- **Then** sets how the layer moves on to the next keyframe: **Linear**, **Ease in**, **Ease out**, **Ease in and out**, or **Hold, then jump**.
- **Stop animating** removes every keyframe and leaves the layer where it is at the playhead.

The layer holds its first keyframe's pose before it and its last one's after it. Keyframe times are from the start of the promo, and the layer's own appear/leave times and enter/exit transitions still apply on top. In YAML:

```yaml
keyframes:
  - at: 0.5s
    x: -400
    y: 420
    width: 1060
    height: 170
    opacity: 1
    easing: ease_out # linear, ease_in, ease_out, ease_in_out or hold
  - at: 1.2s
    x: 180
    y: 420
    width: 1060
    height: 170
    opacity: 1
    easing: linear
```

## Choose a promo type

| Type | Placement | Matching layer |
| --- | --- | --- |
| **Up Next** | Before the next show, according to promo placement settings | `up_next` |
| **Now Playing** | Immediately before a show | `now_playing` |
| **This Has Been** | After a programme slot | `this_has_been` |
| **Coming Up** | Announcement for later programming | `coming_up` |
| **Eventually** | Announcement for a later airing | `eventually` |
| **You're Watching** | Start of a mid-roll break | `youre_watching` |
| **Back to the Show** | End of a mid-roll break | `back_to_the_show` |

The matching layer writes the show's name and is optional: a template can show the shows with [variable images](#variable-images) alone, for example title cards for what's on now, next and after that.

**Up Next** layers can show additional programmes through **offset**, **shows ahead**, and start-time settings. **Before the shows** and **After the shows** add text inside the same layer, joined onto the first and last show with spaces kept: `prefix: 'Up next: '` and `suffix: ' on {channel}'` give "Up next: Rugrats on Nick". A new line in either puts it on a line of its own. Coming Up and Eventually use the built schedule when available; unresolved targets retain stand-in commercials.

Both prefix and suffix also accept **`{start_time}`** and **`{end_time}`**. These use the first selected show's start and last selected show's end, respecting **offset** and **shows ahead**, in the channel's local time (the same `h:mm` format as **Show start times**). For example, `prefix: '{start_time}–{end_time}: '` gives `5:00–5:30: Rugrats`. Scheduled end times include later segments and stacked episodes, excluding the trailing ad break; when only a runtime is available, the end is estimated from it. If neither is known, `{end_time}` becomes blank. Token names are case-insensitive and work in either field, including across newlines.

## Variable images

Both canvas templates and external generators support show-dependent images:

1. Open **Variable images** in the template and select **+ Variable image**.
2. Choose its usual show: **the show this promo promotes**, **currently watching / before the break**, or **up next**. Each use can pick a different one (see below).
3. For **up next**, choose the next show, two shows ahead, or up to ten shows ahead. Mid-roll continuations don't count as a new upcoming show.
4. Pick a **Default image**, then add **Show overrides** with the series/show title and its image. Titles match without regard to case.
5. On the canvas, add an **Image** layer and select the variable under **Image source**. Position, resize, and animate it as usual.

The default is used when the show has no assigned image or the schedule doesn't reach the requested show. Without a default, the canvas layer is hidden and the placeholder resolves to an empty string. Configured files must be readable on the scheduler's computer. Previews use the channel's current schedule, or sample contexts before a schedule exists.

For an external script/tool, use the displayed placeholder in an argument, for example:

```text
render-promo.js
--image
{variable_image_01}
--output
{output}
```

The scheduler passes the resolved **image file path** as that argument; the script loads the image from it. This also works in **For each show** and **When there's an episode clip** arguments. Image variables retain their configured context in these groups. Placeholders are substituted in arguments, not by rewriting the script's source file.

Variables belong to the template, and their default and per-show image files travel with template exports. YAML example inside a template:

```yaml
variable_images:
  - name: variable_image_01
    context: up_next
    offset: 1 # Two shows ahead; 0 means next
    default_path: 'D:\Media\Promos\default.png'
    shows:
      - show: SpongeBob SquarePants
        path: 'D:\Media\Promos\spongebob.png'
```

An image layer references it with `variable_image: variable_image_01` instead of `path`. Other contexts are `promoted_show` and `currently_watching`.

### Choosing the show where it's used

A variable's context is only its **usual show**. Each use can pick its own, so one variable with all your show images can fill several slots at once, for example what's on now, what's next and what comes after that:

- On an image layer, pick the variable under **Image source**, then choose **For which show**. In YAML, add `variable_image_show: next2`.
- In program arguments, add the show after a colon: `{variable_image_01:watching}`, `{variable_image_01:next}`, `{variable_image_01:next2}`.

| Selector | Show |
| --- | --- |
| `promoted` | The show the promo is for |
| `watching` | What's on now, or the show before the break |
| `next` | The next show |
| `next2` … `next10` | That many shows ahead |

On an **Up Next** template, the automatic voice-over reads out every upcoming show the template shows, by Up Next text or by image layer: an image of the show after next makes it mention both. Advanced voice-over templates instead read only the script you supply.

Without a selector, `{variable_image_01}` uses the variable's usual show, so existing templates work as before. An unknown selector is reported when the schedule is checked.

## Voices

Spoken show names omit parenthesised four-digit release years: **Total Drama Island (2023)** is read as **Total Drama Island**. Colons and dashes (including hyphens, en dashes and em dashes) in show titles become spaces to avoid awkward TTS pauses: **Avatar: The Last Airbender** becomes **Avatar The Last Airbender**, and **Spider-Man** becomes **Spider Man**. This applies to automatic reads, advanced show-title tokens (including `:raw`), and AI voice-over title inputs. On-screen titles and show matching retain the original name. Other qualifiers such as `(US)` and numbers that are part of a title such as **The 100** are kept. Punctuation you write in your script and clock times such as `5:30` are preserved.

| Provider | Setup |
| --- | --- |
| **TikTok** · `tiktok` | Enable the voice plugin and choose a listed voice |
| **MiniMax** · `minimax` | Enter the API key in the plugin's **Settings**; usage is paid |
| **Piper** · `piper` | Install the local engine and voice models |

### Advanced voice-over templates

Under **Voice-over**, enable **Advanced voice-over templates (write the complete script)**. Write one complete script, or add several of your own variations. A single eligible template is picked deterministically for each promo, so re-rendering it uses the same wording. There are no hidden linking phrases or AI rewriting in this mode; switching it off restores the automatic wording and retains your authored templates.

For example:

```text
Now, it's {up_next}. Crazy right? Then after that it's {up_next_2}!
```

| Token | Meaning |
| --- | --- |
| `{up_next}` / `{up_next_1}` | First upcoming show, matching the variable-image `next` selector |
| `{up_next_2}` … `{up_next_10}` | That upcoming show, matching variable-image `next2` … `next10` |
| `{up_next_2:raw}` | The title without repeat-only text (works on any upcoming token) |
| `{up_next_2:episode}` | That episode's title |
| `{up_next_2:start_time}` / `{up_next_2:end_time}` | Its channel-local time, in `h:mm` format |
| `{show}` / `{episode}` | Show / episode that this promo identifies or promotes |
| `{start_time}` / `{end_time}` | That promoted show's channel-local times |
| `{channel}` | Channel name |

Token names are case-insensitive. An unknown episode title or end time becomes empty; an end time uses the scheduled end when known, otherwise the start plus runtime.

**Before a repeated show title** defaults to `more ` (with a trailing space). **After a repeated show title** defaults to empty. These apply to a later upcoming title if the same show appears earlier in the selected listings. With the default **Timeslot / episode** grouping, two consecutive Rugrats episodes in **separate slots** make the example read:

> Now, it's Rugrats. Crazy right? Then after that it's more Rugrats!

You can instead leave the prefix empty and use ` again` as the suffix, or clear both for unchanged titles. Spaces are preserved. Use `:raw` to bypass repeat text at a particular token. Matching ignores case and normalized title punctuation.

Advanced voice tokens always use **the same sequence as text and variable images**, controlled by the template's **Count upcoming programmes by** setting. For a Now Playing promo showing a Regular Show slot followed by a Gumball slot, `{up_next}` says Regular Show and `{up_next_2}` says Gumball, even if the Regular Show slot contains multiple episodes. There is no independent TTS counting option. `{show}` remains the promo's subject for Now Playing, Coming Up, and other promo types.

**Include an opener before the script** and **Include a closer after the script** are independent, optional switches, both off initially. When enabled they use your existing opener/closer lists (with the same tokens available). Empty lists add nothing—no default opener is inserted. The selected script and any enabled affixes appear in **Template read-out** before rendering.

A script needing a listing beyond the available schedule is skipped. Add a shorter template such as `Now, it's {up_next}!` as a fallback. If none of your templates is eligible, the promo has no voice-over and the editor explains why. Optional opener/closer variants that require unavailable listings are also skipped.

```yaml
upcoming_grouping: timeslot
voice:
  provider: tiktok
  voice: en_us_006
  advanced:
    enabled: true
    templates:
      - "Now, it's {up_next}. Crazy right? Then after that it's {up_next_2}!"
      - "Now, it's {up_next}!"
    repeat_prefix: 'more '
    repeat_suffix: ''
    include_pre_messages: false
    include_post_messages: false
```

### Piper

1. Open **Plugins → Piper voices → Settings**.
2. Select **Download Piper**.
3. Open **Browse voices to download** and download a voice.
4. Save and select the voice in the template.

Manual voices require both **`.onnx`** and **`.onnx.json`** in the configured voices folder. Piper runs offline after download.

### MiniMax

The plugin lists account voices, including cloned and designed voices. Model changes generate new voice-overs while retaining existing cached files.

Failed speech requests use a silent version until retried. Overlong speech can be accelerated up to **1.35×**.

## Music and episode backgrounds

**Songs from** accepts files or a music library. Each airing selects a stable song; file-based selections support individual start points, volume, and preview playback.

**Episode clip backgrounds** use the promoted programme's file. Configure its start, audio level, and ducking; the regular background is the fallback when the clip cannot be read.

**Mutes the episode clip** on a layer reduces clip audio according to that layer's opacity. See [Background music](background-music.md) and [Local AI writer](local-ai.md) for shared options.

## Share templates

Blocks inherit channel templates unless they define their own, disable them, or reference another channel. This fragment goes inside a channel or block:

```yaml
plugins:
  generated-promos:
    from: adultswim
```

Replace **`adultswim`** with an existing channel ID. The reference uses that channel's current templates and name; **`enabled: false`** disables generated promos at that scope.

## Control rendering and storage

| Plugin setting | Default | Effect |
| --- | --- | --- |
| **Save folder** | `state/generated-promos` | Render and speech cache |
| **Start rendering when a promo airs within** | 60 minutes | `0` renders the full horizon |
| **Only render for channels someone is watching** | On | Unwatched channels retain stand-ins |
| **Keep unused videos for** | 7 days | `0` keeps unused renders indefinitely |
| **Keep voice-overs for** | 7 days | Retention measured from last use |

Rendering runs in airing order. Completed clips trigger republication; identical content reuses cached files. Changing **Save folder** does not move old files.

## Template YAML

Place this fragment inside a channel or block:

```yaml
plugins:
  generated-promos:
    templates:
      - name: Classic Up Next
        type: up_next
        usage: mixed
        mix_chance: 0.5
        duration: 10s
        voice:
          provider: piper
          voice: en_US-ryan-high
          pre_messages: ['Coming up next...', 'Stay tuned for']
          post_messages: ['Only on {channel}!', '']
        layers:
          - name: Panel
            kind: rect
            x: 110
            y: 250
            width: 1220
            height: 580
            color: '#96000000'
          - name: Next
            kind: up_next
            x: 180
            y: 420
            width: 1060
            height: 170
            font_size: 132
            transition_in: zoom
```

**`usage: fallback`** limits generated promos to missing library clips. **`mixed`** uses **`mix_chance`**; the selected voice model must exist.

Layers draw in list order and support **start/end**, **opacity**, and **transition_in/transition_out**. Text and voice messages accept **`{channel}`**.

## External generators

Choose **Video made with** to render through an external executable. The program runs directly, without a shell, and must write **`{output}`** or print the completed path as its last output line.

```yaml
generator:
  command: node
  working_directory: /media/promo-generator
  shows: 3
  arguments: [render.js, '{shows}', --duration, '{duration}', --out, '{output}']
  per_show: [--show, '{title}', --at, '{time_or_next}']
```

| Placeholder group | Tokens |
| --- | --- |
| **Programme** | `{title}`, `{episode}`, `{time}`, `{time_or_next}`, `{day}`, `{when}`, `{index}` |
| **Template** | `{channel}`, `{type}`, `{duration}`, `{width}`, `{height}`, `{aspect}`, `{fps}` |
| **Files** | `{clip}`, `{clip_start}`, `{output}`, `{output_dir}` |
| **Argument expansion** | `{shows}`, `{clip_arguments}` |

Transparent output can reveal the template background. Short output holds its final frame; long output is cut to duration. Voice and music mix over the result.

**`setup_command`** (the editor's **First-time setup**) runs once in **`working_directory`** before the generator is first used there, through the system shell (`cmd` on Windows, `sh` elsewhere), for steps such as `npm install`. It runs again if the folder or command changes, or from **Run it again** in the editor. A failed setup fails the render and is retried on the next one.

```yaml
generator:
  command: node
  working_directory: /media/promo-generator
  setup_command: npm install
```

## Files kept in the data folder

Pictures, videos and songs chosen for a template (and for overlays and Credit Squeeze looks) are copied into the data folder under **`assets/`**, and the template uses the copy, so it keeps working if the original is moved or deleted. Choosing the same file again reuses the copy.

## Sharing templates

**Export** saves the selected template as a `.zip` with every file it uses. For an external generator, the whole **`working_directory`** goes in too, leaving out whatever its `.gitignore` files exclude (and the `.git` folder), so `node_modules` and build output stay behind and **`setup_command`** restores them. Paths to that folder in the command and arguments are rewritten to follow it.

A template made with the canvas editor also carries the font files of every family its text layers use (each weight and style found among the computer's installed fonts). On import they're kept under **`assets/fonts/`**, and the renderer uses them for any family that isn't installed there, so text looks the same without installing anything. Check a font's licence allows sharing before passing a package on.

**Import…** adds a template from such a `.zip`. Its files are kept under **`assets/`** and a generator's folder is unpacked to **`assets/programs/`**; a package that runs a program shows the command and asks before importing. Overlays and Credit Squeeze looks export and import the same way.

## Command-line preview

Run **`dotnet run`** from a source checkout:

```sh
dotnet run --file tools/generate-promo.cs -- ch1 --voice US_MALE_1 --limit 3
```

The command previews against the configured schedule without saving it.
