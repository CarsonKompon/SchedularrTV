# Channel video sizing

Set defaults in **App settings → ErsatzTV channels**, or override them in a channel's
**General → Encoding** section. The channel wizard offers the same controls.

- **Resolution:** choose 480p, 540p, 720p, 1080p, 1440p or 2160p.
- **Aspect ratio:** choose 16:9, 4:3, 16:10, 1:1, 21:9 or 9:16. The width is
  calculated from the resolution and shape; changing resolution keeps that shape.
- **Custom resolution:** the last resolution option replaces the aspect-ratio
  dropdown with exact width and height controls. Existing explicit dimensions open
  in Custom mode, so their shape is preserved.
- **Fit:** preserve the entire image and add black bars where the shapes differ.
- **Fill:** preserve image proportions, enlarge to cover the frame, and crop the overflow.
- **Frame rate:** defaults to **Match source**. Each video keeps its own rate, so
  the channel can change rate between videos; this does not force every source to
  become variable-frame-rate video. Fixed presets are 23.976, 24, 25, 29.97, 30,
  50, 59.94 and 60 fps. Fractional presets use exact NTSC fractions. **Custom** accepts
  whole numbers or fractions such as `48` or `30000/1001`, from 1 to 240 fps.
  Fixed rates drop or repeat frames as needed. A channel can explicitly select
  Match source even when the app default is fixed. Browser-based live sources
  retain their separate capture frame-rate setting.

480p describes the height, not the shape. The original 480p preset is 854×480 and
therefore remains widescreen. Heights support 240–4320 pixels and widths 160–7680;
odd dimensions are rounded down to even pixels for video encoders.

Channel fields inherit app defaults independently. IPTV and streamer channels
keep their source dimensions unless a channel width or height is explicitly set.
Passthrough channels copy video by default; any explicit channel encoding override
forces video transcoding so the selected settings actually apply. Audio can still
be copied. Saving changes regenerates the encoding overlay and restarts ErsatzTV.

Example channel YAML:

```yaml
encoding:
  height: 480
  aspect_ratio: '4:3' # or width: 640 for custom dimensions
  frame_rate: source # or '30000/1001' for fixed 29.97 fps
  scaling_mode: crop # scale_and_pad for Fit
```

## Diagnosing a green picture

A green picture at 480p has not been reproduced or attributed to a specific encoder.
To investigate, record the channel type, selected hardware encoder, GPU/driver,
ErsatzTV/FFmpeg versions, and source video dimensions and pixel format. Include the
ErsatzTV/FFmpeg log from the failing playback and the channel's generated encoding
overlay (referenced by `lineup.json`).

Compare the same source at the same output dimensions with **Processor (software)**.
Check the player's stream information or ffprobe for the actual output dimensions;
the size of the player on screen does not establish the encoded resolution. If
software works and hardware does not, that narrows the investigation to the hardware
decode/filter/encode path rather than the resolution setting alone.
