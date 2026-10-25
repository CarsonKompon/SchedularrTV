# Auto Chapters

**Auto Chapters** detects fades to black and optional silence in files without chapter markers. Detected boundaries improve mid-roll placement without modifying source files.

## Enable detection

1. Install **Auto Chapters** and restart.
2. Verify **ffmpeg** and access to the media files.
3. Enable mid-rolls on the channel's **Breaks** tab.
4. Open **Auto chapters** to inspect queued files and detected boundaries.
5. Review upcoming break coverage before increasing analysis scope.

Files airing within the next two days are queued first, with watched channels prioritized. Existing chapters take precedence over detected markers.

## Manage analysis

| Control | Effect |
| --- | --- |
| **Analyse the whole library** | Queue episodes and movies without chapters |
| **Stop waiting ones** | Clear queued work; upcoming programmes can return on the next publish |
| **Try failed ones again** | Retry unreadable files |
| **Start over** | Forget detected results for reanalysis |

Each file is decoded at reduced resolution. Results persist in **`plugin-data/auto-chapters/chapters.json`**; changed file size or modification time triggers reanalysis.

## Tune detection

| Setting | Default | Use |
| --- | --- | --- |
| **Use the markers found for mid-roll breaks** | On | Disable to inspect results without applying them |
| **Only count fades to black where the sound drops out too** | On | Disable for programmes retaining music across breaks |
| **Shortest fade to black** | 0.4 seconds | Ignore shorter black intervals |
| **How dark counts as black** | 0.1 | Raise for dark-gray fades; lower to reject dark scenes |
| **Analyse the whole library in the background** | Off | Extend analysis beyond upcoming content |
| **Programmes analysed at once** | 1 | Toggle the plugin off/on after changing |

Fades in the first and last **45 seconds** are ignored. Fades within **20 seconds** merge into one candidate.

## Keep analysis light

Analysis runs at the lowest process priority, so it only uses processor time the channels on air don't want. These settings decide how much it takes:

| Setting | Default | Use |
| --- | --- | --- |
| **Decoder threads** | 2 | Processor threads per analysis; **0** uses all of them (fastest, heaviest) |
| **Fastest analysis speed** | 20× real time | Caps how fast a file is read: about 90 seconds for a half-hour episode. Lower it (5–10×) to spread analysis thinner; **0** removes the cap. Needs ffmpeg 5.1 or later |
| **Decode only what's needed** | On | Skips frames black detection doesn't need and the deblocking filter, for well under half the decoding |
| **Hardware decoding** | **match** | Moves decoding to the graphics or video chip. **match** uses the card chosen for encoding under **App settings → Video encoding** (NVIDIA → `cuda`, Intel → `qsv`, AMD → `d3d11va` on Windows or `vaapi` on Linux, Apple → `videotoolbox`), and the processor when none is; a Raspberry Pi uses `auto`. Pick one directly to use the card without encoding on it |
| **Keep frames on the graphics card** | On | With `cuda`, `qsv` or `vaapi`, frames are shrunk on the card and only a 320×180 picture is copied back, instead of every full-size frame |

Anything the card can't handle falls back on its own: frames come back full size if this ffmpeg has no scaler for the card (after three failures in a row it stops trying until restart), and a file the card can't decode is read in software. Black and silence detection themselves always run on the processor, on that small picture and the sound. While hardware decoding is in use, the graphics card's **Decoder** column on the **Performance** page shows the load.

Check the result under **Auto Chapters – Chapter detection** on the **Performance** page.

## Understand schedule changes

New plans use detected markers like chapters. Already-planned breaks can move to the nearest fade within **five minutes**, preserving the programme's outer boundaries; an airing already in progress is left unchanged.

If no suitable fade exists, **Auto** mid-roll mode retains cadence-based breaks. The **Mid-roll breaks coming up** summary distinguishes detected fades, embedded chapters, pending analysis, and unmatched cadence breaks.

## Review markers

The **Review queue** tab plays each analysed programme in turn, oldest analysis first, with its break points on the scrub bar.

- Click a marker (on the bar or in the thumbnails below) to jump to it; drag it along the bar to move it
- Nudge the selected marker one frame with **,** / **.** (or **Alt**+arrows), one second with **Shift** added
- Step the playhead one frame with **←** / **→**, jump between markers with **[** / **]**
- **M** adds a marker at the playhead, **Delete** removes the selected one, **Reset** puts back what analysis found

**Approve** keeps the markers (edited or not) for mid-roll breaks and moves on to the next programme. The arrow beside it switches the button to **Approve and apply to video file**, which also writes the markers into the file as chapters without re-encoding: MKV and WebM in place with `mkvpropedit` when MKVToolNix is installed, otherwise (and for MP4, M4V and MOV) by copying the streams into a new file that's checked before it replaces the original. Other formats (AVI, TS, MPEG…) can't hold chapters; those are approved and left as they are, with a notice saying so. **Skip** leaves a programme waiting at the back of the queue, and **Re-open** under **Recently reviewed** puts one back.

Files a browser can play (H.264, VP9 or AV1 with AAC, Opus or MP3 sound) play as they are. Anything else (AC-3 sound, MPEG-2, older formats) plays as a converted 540p preview, and stepping while paused shows exact frames from the file.

## Resolve unreadable files

- Verify the scheduler account can read the reported path
- Configure **`ErsatzTv:PathMappings`** for container or remote-library paths
- Select **Try failed ones again** after correcting access
- *This ffmpeg couldn't decode the video*: turn off **Decode only what's needed** or **Hardware decoding** in the plugin's settings (a file that refuses frame skipping is already retried without it). Hover the message for ffmpeg's own words

See [Path mappings](ersatztv.md#path-mappings-and-guide-options).
