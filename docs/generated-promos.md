# Generated promos

A channel can make its own Up Next, Now Playing, This Has Been, Coming Up and Eventually promos, with a voice-over and music. Use them when your library has no promo clips, or mix them in with real ones.

## How it works

- Each template has a fixed `duration`, so breaks are planned without waiting for a render.
- After each build, the upcoming programmes are filled in and the promos are rendered in the background, soonest airing first.
- Until a promo's file exists, its slot plays ordinary commercials of the same length. Once the promo is ready, it is attached and the schedule is republished.

## Requirements

- **ffmpeg** and **ffprobe** for rendering (see [Installation](installation.md#requirements)).
- The Generated promos plugin and its voices. They are optional: install `SchedularrTV-<version>-all-plugins.zip` from the same release as your scheduler on the **Plugins** page, then restart.
- The built-in **Promos** plugin, which Generated promos needs.

## The editor

A channel's **Generated Promos** tab edits templates visually. **Settings for** switches between the channel and each of its blocks. The canvas is drawn by the renderer itself, so it matches the output.

- **Moving and resizing:** drag boxes to move them. They snap to the centre lines; hold Alt to place them freely. Use the handles to resize, and scrub the timeline to check transitions.
- **Choosing files:** songs, backgrounds and images are chosen with **Browse**, which opens your system's own file dialog (you can pick several songs at once). The files belong to the scheduler, so the dialog opens on the computer running SchedularrTV and is only offered in a browser on that computer. From another computer, type the path.
- **Inspector:** covers per-layer settings: Up Next offset, show count and start times, text, fonts, colour, transparency, timing and animation.
- **Songs from:** chooses song files, or a [music library](background-music.md) (Jellyfin's, local music folders, or a plugin's), using every song, a playlist, some genres or some artists. From a library, each promo gets one of its songs, and the same one every time that promo is rendered. Songs a library serves rather than keeps as files (Jellyfin's) are copied once into the promos folder's `music` folder for rendering.
- **Song start points:** each song file has its own start point. Its **▶ Play** button plays exactly the stretch a promo of the template's length would use, with the fades applied.
- **Render preview video:** makes the full promo with voice and music, using the channel's real upcoming shows once its schedule is built.

## Promo types

| `type` | What it fills | Required layer | Default read-out |
| --- | --- | --- | --- |
| `up_next` | Up Next spot in a break of the show before | `up_next` (offset, shows_ahead, include_time) | Coming up next... SpongeBob. Followed by Rugrats at 12:30, and Hey Arnold at 1! |
| `now_playing` | Ident right before the next show | `now_playing` | You're watching SpongeBob! |
| `this_has_been` | Sign-off after a slot ends (not between stacked episodes of the same show) | `this_has_been` | This has been SpongeBob! |
| `coming_up` | Lookahead promo for a later anchor or block opening, never the show that follows | `coming_up` | Coming up tonight at 8:00, Rocket Power! |
| `eventually` | Trailer mixed among the ads for an airing later in the week | `eventually` | Don't miss Rocket Power, Saturday at 4:00! |
| `youre_watching` | Opens a mid-show (mid-roll) break, naming the show it interrupts | `youre_watching` | You're watching SpongeBob! |
| `back_to_the_show` | Closes a mid-show break, just before the show carries on | `back_to_the_show` | Now, back to SpongeBob! |

### Show layers

Show layers take `show: { title, episode_title, day, time }`. Each ticked part is its own line. The day reads Today, Tonight (from 5pm), Tomorrow or the weekday.

Coming Up and Eventually name what the built schedule airs at that time. When the schedule doesn't reach that far, they fall back to the show predicted at build time.

### Channels without anchors or block openings

Channels run by their rotation, or by long blocks of several shows (with no anchor or block opening ahead), get Coming Up and Eventually promos too:

- **Coming Up** picks a time 45 minutes to 3 hours past the break and names the first show the schedule airs from then, skipping the shows either side of the promo.
- **Eventually** advertises a block of several shows only within the build horizon. A rotation-run channel gets a time more than 12 hours out, since the schedule has to be built that far to name the show.

If nothing can be named by render time, the slot plays stand-in commercials.

### How read-outs are worded

- Up Next read-outs link the later shows in varied ways ("Then…", "Followed by… and…") and say whole hours as "at 5".
- Each promo picks one opener and one closer from its lists. The choice is fixed per airing, so re-renders sound the same.

## Voices

| `provider` | Voices | Setup |
| --- | --- | --- |
| `tiktok` | Built-in English list (`US_MALE_1`, `en_us_006`...) | None |
| `minimax` | Every system, cloned and designed voice on your account, e.g. `English_expressive_narrator` | API key in the MiniMax voices plugin's settings |
| `piper` | Every model in the voices folder, e.g. `en_US-ryan-high`; multi-speaker models as `en_GB-semaine-medium#spike` | Free and offline (see below) |

Each voice plugin's settings are under its gear on the **Plugins** page.

### Piper

Piper is free and works offline. It needs the `piper` program from the [Piper releases](https://github.com/rhasspy/piper/releases) and `.onnx` + `.onnx.json` voices from [piper-voices](https://huggingface.co/rhasspy/piper-voices).

The Piper voices plugin's settings can download both for you:

- **Download Piper** fetches the program for your computer.
- **Browse voices to download** lists the voice catalogue by language and quality, with a download button for each. You can hear samples at [rhasspy.github.io/piper-samples](https://rhasspy.github.io/piper-samples/).

Downloaded voices can be used straight away.

By default, Piper looks in `SchedularrTV/piper/piper/` under the local application data folder, with voices in `SchedularrTV/piper/voices/`. Both can be changed in the same settings. To add a voice you got elsewhere, drop its `.onnx` model and `.onnx.json` into the voices folder.

### MiniMax

MiniMax is pay-as-you-go.

- **Model** defaults to `speech-2.8-turbo`.
- **Language hint** defaults to `auto`.
- The editor lists English voices first, with a language filter and search.
- Changing the model makes new voice-overs; cached ones are left in place.

### When text-to-speech fails or runs long

- If text-to-speech fails, a silent version plays until it is retried.
- A voice-over longer than the promo is sped up, at most 1.35x.

### AI-written voice-overs and captions

With the [Local AI writer](local-ai.md) plugin, **Have AI word it naturally** in the **Voice-over** section has a free model on your computer write each read-out from the promo's shows ("followed by even more SpongeBob!"). It can optionally joke about the episode, using its description.

A **Text** layer can be AI-written the same way.

The usual wording is the fallback whenever the AI's writing leaves out a show or makes up a time, or when no model is set up. Both take a `writer:` section in the YAML; see [Local AI writer](local-ai.md#in-the-schedule-file).

## Blocks

Blocks use the channel's generated promos unless they set their own. A block can:

- have its own `generated-promos` section,
- switch them off with `enabled: false`, or
- match another channel with `from: <channel id>`, which always uses that channel's current templates (and its name for `{channel}`).

Channels can use `from` too.

```yaml
blocks:
- name: Adult Swim
  time: 21:00
  end_time: 06:00
  plugins:
    generated-promos:
      from: adultswim
```

Generated promos needs the Promos plugin. Switching either off on the **Plugins** page keeps the templates in the schedule but stops them airing.

## Plugin settings

All of these are in the plugin's settings (its gear on the **Plugins** page):

| Setting | Default | Notes |
| --- | --- | --- |
| **Save folder** | `state/generated-promos` in the data folder | Where renders and voice-overs are cached (see [Caching](#caching)). The settings show the folder in use. |
| **Start rendering when a promo airs within** | 60 minutes | `0` renders the whole playout ahead. Promos further out wait, so a rebuild doesn't leave unused renders. |
| **Only render for channels someone is watching** | On | Uses ErsatzTV's stream activity, so paid voices are only spent on watched channels. Other channels air stand-ins. |
| **Keep unused videos for** | 7 days | `0` = forever. Videos a schedule still uses are never removed. |
| **Keep voice-overs for** | 7 days | `0` = forever. Counted from last use, so a line that keeps airing is never requested again. |

> [!NOTE]
> Earlier versions kept these in the schedule file (`path`, `lead_time`, `watched_only`, `keep_videos` and `keep_audio` under `settings.plugins.generated-promos`). Those values still apply until the plugin's settings are saved. Opening the settings fills in any that differ from the defaults, so saving keeps them.

## Caching

Renders and voice-overs are cached by content in the **Save folder**, so identical promos are made only once.

- Renders are filed as `<channel>/<template>/` or, for templates a block defines, `<channel>/<block>/<template>/`.
- Borrowed templates render into the channel that owns them.
- Editor previews go to `_previews/`.
- Changing the folder doesn't move existing files.

Output is H.264 (`medium`, CRF 22, `animation` tune) with 128k AAC.

## Template YAML

Templates belong to a channel (or a block), in its `plugins:` section under the plugin's id, `generated-promos`:

```yaml
plugins:
  generated-promos:
    templates:
    - name: Classic Up Next
      type: up_next
      usage: mixed          # fallback: only when no clip exists
      mix_chance: 0.5
      duration: 10s
      background: /media/Promos/Backgrounds/nick.mp4   # optional image or looping video
      background_clip:      # optional; plays the promoted episode itself behind the promo
        start_at: 3m        # omit to pick a spot in the middle, different for each airing
        include_audio: true
        volume: 0.6
        duck_under_voice: true
      voice:
        provider: tiktok
        voice: US_MALE_1
        pre_messages: [Coming up next..., Stay tuned for]   # one is picked per promo; omit for the type's usual opener
        post_messages: [Only on {channel}!, '']            # '' = say nothing that time
      music:                # optional; one song is picked per promo, each loops if shorter than it
        paths:
        - /media/Promos/Music/theme.mp3
        - /media/Promos/Music/theme-2.mp3
        volume: 0.35        # the master volume
        song_volumes:       # a song's own volume on top of it (1 = the master volume, up to 2); songs not listed play at 1
          /media/Promos/Music/theme-2.mp3: 0.7
        song_starts:        # where each song starts; songs not listed start at start_at (default 0)
          /media/Promos/Music/theme.mp3: 12s
          /media/Promos/Music/theme-2.mp3: 41.5s
        fade_in: 300ms
        fade_out: 1s
        duck_under_voice: true
        duck_under_clip: true   # dips while the episode clip's sound plays (needs background_clip with include_audio)
      layers:
      - { name: Panel, kind: rect, x: 110, y: 250, width: 1220, height: 580, color: '#96000000', corner_radius: 28 }
      - { name: Next, kind: up_next, x: 180, y: 420, width: 1060, height: 170, font_size: 132, transition_in: zoom }
      - name: Later
        kind: up_next
        x: 180
        y: 650
        width: 1060
        height: 60
        font_size: 54
        up_next: { offset: 1, shows_ahead: 1, include_time: true }
```

> [!NOTE]
> Files from before version 1.1 had this as `generated_promos:` directly on the channel. They still load and are rewritten in the new layout (the original is kept as `schedule.yaml.before-plugins.bak`).

### Layers

- Layers draw in order. Kinds are `up_next`, `text`, `rect` and `image`.
- Each layer has its own `start`/`end`, `transition_in`/`transition_out` (`fade`, `slide_left|right|up|down`, `zoom`) and `opacity`.
- `{channel}` is replaced in text and voice messages.
- Every template needs the text block for its type (see [Promo types](#promo-types)).

### Episode clip background

`background_clip` uses the file of the episode the promo is for: the first show on Up Next, or the promoted show otherwise. It can include the episode's sound, which ducks under the voice like music does.

`background` is used instead when that file can't be read, such as a Coming Up promo for a show the schedule hasn't reached yet.

### Muting the episode clip with a layer

Any layer can be marked `mutes_clip: true` (**Mutes the episode clip** in the editor). The episode's sound then follows that layer, turned down as much as the layer is opaque.

- A black box that fades in fades the episode out (and back in as it fades away).
- A box that cuts in cuts the sound.
- Several such layers combine.

## External generators

A template can hand its video to an external program instead of the canvas (**Video made with** in the editor). The voice-over and music are mixed on top.

- The program's video is scaled over the template's background, so a transparent `.mov`/`.webm` shows the background or episode clip through it.
- If the video is short it is held on its last frame; if long, it is cut.
- Its own sound can be kept and ducked.

The program runs directly (never through a shell) and must either write `{output}` or print the finished file's path as its last line.

- `{shows}` repeats `per_show` for each listed show.
- `{clip_arguments}` adds `clip_arguments` only when an episode clip is available.
- A program whose arguments use `{clip}`, `{clip_start}` or `{clip_arguments}` is handed the episode clip and plays it itself, so the clip is never laid underneath.

Placeholders: `{title}`, `{episode}`, `{time}` (8:30 PM), `{time_or_next}`, `{day}`, `{when}`, `{index}`, `{channel}`, `{type}`, `{duration}`, `{width}`, `{height}`, `{aspect}`, `{fps}`, `{clip}`, `{clip_start}`, `{output}`, `{output_dir}`.

Renders are cached like any other promo, so the program only runs for new content.

```yaml
- name: Next 3
  type: up_next
  duration: 7s
  generator:
    command: node
    working_directory: /media/Promo Generators/Up Next
    shows: 3
    arguments: [tools/bumper.js, next3, '{shows}', --aspect, '{aspect}', --duration, '{duration}', --out, '{output}']
    per_show: [--show, '{title}', --at, '{time_or_next}']
- name: Next 1
  type: up_next
  duration: 5s
  background_clip: { include_audio: true }   # laid under the transparent .mov
  generator:
    command: node
    working_directory: /media/Promo Generators/Up Next
    output_extension: .mov
    keep_audio: false
    arguments: [tools/bumper.js, next1, --show, '{title}', --duration, '{duration}', --aspect, '{aspect}', --out, '{output}']
```

> [!WARNING]
> The editor can run any program the scheduler's account can, so keep the web UI on a trusted network.

## Command-line preview

Preview against your real schedule without saving anything:

```sh
dotnet run --file tools/generate-promo.cs -- ch1 --voice US_MALE_1 --limit 3
```
