# Generated promos

A channel can render its own promos (Up Next, Now Playing, This Has Been, Coming Up and Eventually)
when the library has none, or mix them in with real clips. Templates have a fixed `duration`, so breaks
are planned without waiting for a render. After each build the upcoming programmes are filled in and
the promo is rendered in the background, soonest airing first. Until the file exists the slot plays
ordinary commercials of the same length; the promo is attached and republished once it's ready.

Rendering needs ffmpeg and ffprobe (see [Installation](installation.md#requirements)).

Generated promos is an optional plugin, as are the voices for it: install `SchedularrTV-<version>-all-plugins.zip`
from the same release as your scheduler on the **Plugins** page and restart. It needs the built-in Promos
plugin.

## The editor

A channel's **Generated Promos** tab edits templates visually; **Settings for** switches between the
channel and each block. The canvas is drawn by the renderer itself, so it matches the output.

- Drag boxes to move them (they snap to the centre lines; hold Alt to place freely), use the handles to
  resize, and scrub the timeline to check transitions.
- Songs, backgrounds and images are chosen with **Browse**, which lists the scheduler machine's folders.
- The inspector covers per-layer settings: Up Next offset, show count and start times, text, fonts,
  colour, transparency, timing and animation.
- Each song has its own start point; its **▶ Play** button plays exactly the stretch a promo of the
  template's length would use, with the fades applied.
- **Render preview video** makes the full promo with voice and music, using the channel's real upcoming
  shows once its schedule is built.

## Template YAML

Templates belong to a channel (or a block), in its `plugins:` section under the plugin's id,
`generated-promos`:

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
        volume: 0.35
        song_starts:        # where each song starts; songs not listed start at start_at (default 0)
          /media/Promos/Music/theme.mp3: 12s
          /media/Promos/Music/theme-2.mp3: 41.5s
        fade_in: 300ms
        fade_out: 1s
        duck_under_voice: true
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

Files from before version 1.1 had this as `generated_promos:` directly on the channel; they still load and are
rewritten in the new layout (the original is kept as `schedule.yaml.before-plugins.bak`).

Layers draw in order (`up_next`, `text`, `rect`, `image`), each with its own `start`/`end`,
`transition_in`/`transition_out` (`fade`, `slide_left|right|up|down`, `zoom`) and `opacity`.
`{channel}` is replaced in text and voice messages. Every template needs the text block for its type.

`background_clip` uses the file of the episode the promo is for (the first show on Up Next, the promoted
show otherwise), optionally with its sound, which ducks under the voice like music does. `background` is
used instead when that file can't be read, such as a Coming Up promo for a show the schedule hasn't
reached yet.

## Promo types

| `type` | Fills | Required layer | Default read-out |
| --- | --- | --- | --- |
| `up_next` | Up Next spot in a break of the show before | `up_next` (offset, shows_ahead, include_time) | Coming up next... SpongeBob. Followed by Rugrats at 12:30, and Hey Arnold at 1! |
| `now_playing` | Ident right before the next show | `now_playing` | You're watching SpongeBob! |
| `this_has_been` | Sign-off after a slot ends (not between stacked episodes of the same show) | `this_has_been` | This has been SpongeBob! |
| `coming_up` | Lookahead promo for a later anchor or block opening, never the show that follows | `coming_up` | Coming up tonight at 8:00, Rocket Power! |
| `eventually` | Trailer mixed among the ads for an airing later in the week | `eventually` | Don't miss Rocket Power, Saturday at 4:00! |
| `youre_watching` | Opens a mid-show (mid-roll) break, naming the show it interrupts | `youre_watching` | You're watching SpongeBob! |
| `back_to_the_show` | Closes a mid-show break, just before the show carries on | `back_to_the_show` | Now, back to SpongeBob! |

Show layers take `show: { title, episode_title, day, time }`; each ticked part is its own line, and the
day reads Today, Tonight (from 5pm), Tomorrow or the weekday. Coming Up and Eventually name what the
built schedule airs at that time, falling back to the show predicted at build time when the schedule
doesn't reach that far.

Up Next read-outs link the later shows in varied ways ("Then…", "Followed by… and…") and say whole hours
as "at 5". Each promo picks one opener and one closer from its lists; the choice is fixed per airing, so
re-renders sound the same.

## Blocks

Blocks use the channel's generated promos unless they set their own. A block can have its own
`generated-promos` section, switch them off with `enabled: false`, or match another channel with
`from: <channel id>`, which always uses that channel's current templates (and its name for
`{channel}`). Channels can use `from` too.

```yaml
blocks:
- name: Adult Swim
  time: 21:00
  end_time: 06:00
  plugins:
    generated-promos:
      from: adultswim
```

Generated promos is a built-in plugin that needs the Promos plugin; switching either off on the **Plugins**
page keeps the templates in the schedule but stops them airing.

## External generators

A template can hand its video to an external program instead of the canvas (**Video made with** in the
editor). The voice-over and music are mixed on top. The program's video is scaled over the template's
background, so a transparent `.mov`/`.webm` shows the background or episode clip through it; it is held
on its last frame if short and cut if long, and its own sound can be kept and ducked.

The program runs directly (never through a shell) and must either write `{output}` or print the finished
file's path as its last line. `{shows}` repeats `per_show` for each listed show, and `{clip_arguments}`
adds `clip_arguments` only when an episode clip is available. A program whose arguments use `{clip}`,
`{clip_start}` or `{clip_arguments}` is handed the episode clip and plays it itself, so it is never laid
underneath.

Placeholders: `{title}`, `{episode}`, `{time}` (8:30 PM), `{time_or_next}`, `{day}`, `{when}`, `{index}`,
`{channel}`, `{type}`, `{duration}`, `{width}`, `{height}`, `{aspect}`, `{fps}`, `{clip}`, `{clip_start}`,
`{output}`, `{output_dir}`. Renders are cached like any other promo, so the program only runs for new
content.

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

The editor can run any program the scheduler's account can, so keep the web UI on a trusted network.

## Voices

| `provider` | Voices | Setup |
| --- | --- | --- |
| `tiktok` | Built-in English list (`US_MALE_1`, `en_us_006`...) | None |
| `minimax` | Every system, cloned and designed voice on your account, e.g. `English_expressive_narrator` | API key in the MiniMax voices plugin's settings (its gear on the **Plugins** page) |
| `piper` | Every model in the voices folder, e.g. `en_US-ryan-high`; multi-speaker models as `en_GB-semaine-medium#spike` | Free and offline: the `piper` program from the [Piper releases](https://github.com/rhasspy/piper/releases) and `.onnx` + `.onnx.json` voices from [piper-voices](https://huggingface.co/rhasspy/piper-voices) |

The Piper voices plugin's settings (its gear on the **Plugins** page) download both for you:
**Download Piper** fetches the program for your computer, and **Browse voices to download** lists the
voice catalogue by language and quality, with a download button for each (samples are at
[rhasspy.github.io/piper-samples](https://rhasspy.github.io/piper-samples/)). Downloaded voices can be used
straight away.

Piper looks in `SchedularrTV/piper/piper/` under the local application data folder by default, with voices
in `SchedularrTV/piper/voices/`; both can be changed in the same settings. A voice you get elsewhere is
another `.onnx` model, with its `.onnx.json`, dropped into the voices folder.

MiniMax is pay-as-you-go. Its **Model** setting defaults to `speech-2.8-turbo` and **Language hint** to
`auto`. The editor lists English voices first, with a language filter and search. Changing the model
makes new voice-overs; cached ones are left in place.

If text-to-speech fails, a silent version plays until it is retried. A voice-over longer than the promo
is sped up, at most 1.35x.

## Caching

Renders and voice-overs are cached by content in the folder set under **Settings → Generated promos**
(`path` under `settings.plugins.generated-promos`, a full path), or `state/generated-promos` in the data folder when
unset, so identical promos are made once. Renders are filed as `<channel>/<template>/` or, for templates a
block defines, `<channel>/<block>/<template>/`; borrowed templates render into the channel that owns
them. Editor previews go to `_previews/`. Changing the folder doesn't move existing files.

**Keep unused videos for** and **Keep voice-overs for** (`keep_videos` / `keep_audio` under
`settings.plugins.generated-promos`, 7 days by default, `0` = forever) decide when cached files are deleted:
videos once no schedule has used them for that long, voice-overs once they haven't been reused for that
long, so a line that keeps airing is never requested again.

Output is H.264 (`medium`, CRF 22, `animation` tune) with 128k AAC.

## Command-line preview

Preview against your real schedule without saving anything:

```sh
dotnet run --file tools/generate-promo.cs -- ch1 --voice US_MALE_1 --limit 3
```
