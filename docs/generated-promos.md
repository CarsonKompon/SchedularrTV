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
3. Add the matching show layer, text, images, and background.
4. Set the voice and optional **Background music**.
5. Drag or resize layers and inspect transitions on the timeline.
6. Select **Render preview video**.
7. Select **Save changes**.

Hold **Alt** while dragging to bypass snapping. **Browse** opens a file dialog on the scheduler's computer; from another device, enter a server-readable path.

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

**Up Next** layers can show additional programmes through **offset**, **shows ahead**, and start-time settings. Coming Up and Eventually use the built schedule when available; unresolved targets retain stand-in commercials.

## Variable images

Both canvas templates and external generators support show-dependent images:

1. Open **Variable images** in the template and select **+ Variable image**.
2. Choose the context: **the show this promo promotes**, **currently watching / before the break**, or **up next**.
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

## Voices

| Provider | Setup |
| --- | --- |
| **TikTok** · `tiktok` | Enable the voice plugin and choose a listed voice |
| **MiniMax** · `minimax` | Enter the API key in the plugin's **Settings**; usage is paid |
| **Piper** · `piper` | Install the local engine and voice models |

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
