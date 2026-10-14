# Configure breaks and promos

An **ad break** is a reusable set of folders from your commercials libraries: which folders hold ads, which hold bumpers, which hold filler, and which hold promos. Channels then pick one or more ad breaks on their **Ad breaks** tab, for example an "Any channel" set plus a "Kids channel" set. Their clips are pooled.

## Create an ad break

1. Add clip folders under **App settings → Commercials library** and select **Save**.
2. Open **Programming → Ad Breaks** and select **Create**.
3. In the folder tree, pick what each folder holds:

| Choice | Behavior |
| --- | --- |
| **Ads** | Whole clips packed to fit the break, never cut off |
| **Bumpers** | Station idents; mixed in with the ads, or at the start and/or end of each break (**Settings → Bumpers go**) |
| **Filler** | Closes whatever the ads can't, cut short (or looped, under **Advanced**) instead of leaving a black screen |
| **Skip** | Leaves a folder out, inside a folder that is used |
| **Promo: …** | Up Next, Now Playing, This Has Been, You're Watching, Back To The Show, Coming Up, Later Today or Eventually |

Folders inside a chosen folder follow along unless you pick otherwise. Folders named like a job (`Ads`, `Bumpers`, `Filler`, `Up Next`, `Now Playing`…) are offered as one-click suggestions; **Add all** takes them all.

4. Select **Save changes**.

## Match seasonal commercials

Turn on **Seasonal folders** in the ad break's settings. Clips inside a folder named for a month, season, holiday or date (`December`, `Winter`, `Christmas`, `12-25`, `2026-12-25`) then only play at that time; everything else (including an `Any Time` folder) plays all year. The tree shows greyed-out example folders under each ads, bumpers or filler folder. Holidays come from **Channel settings → Seasonal tags**.

## Set up promos

Pick a promo choice for a folder in the tree, then adjust it under **Promos**:

| Option | Folder layout |
| --- | --- |
| **Show folders** | `Up Next/Top Cat/clip.mp4` plays only for Top Cat |
| **Other clips suit any show** | Clips loose in the folder, or in `Any Show/`, play for any show |
| **Start time folders** | `Up Next/Top Cat/8pm/` plays only for an 8pm start (also `8:30pm`, `830pm`, `20:30`) |
| **Weekday folders** | `Eventually/Top Cat/Saturday/` plays only for a Saturday airing |

The tree shows greyed-out examples of the show, time and day folders each promo looks for. Clips made for the show win over clips for any show. Generated promos can still fill a spot no ad break has a folder for.

## Use ad breaks on a channel

1. Open the channel's **General → Scheduling grid** and set **Start programmes on a boundary of**, such as `30m`.
2. Open **Ad breaks** and add one or more ad breaks.
3. Add interstitial shows and mid-rolls on the same tab if wanted.
4. Select **Save changes**.

A block can use its own ad breaks: open it under **Blocks & anchors** and pick them under **Ad breaks**; left empty, it uses the channel's.

## Fine tuning

Ad lengths, repeats per break, tolerance, strict fit, trimming and looping are under the ad break's **Settings → Advanced**. A channel's or block's own values (under **Advanced: tag-based breaks and promos**) override the ad break's; the global ones in **Channel settings** apply where neither sets them.

## Tag-based breaks (without ad breaks)

A channel with no ad breaks uses the tag-based settings under **Ad breaks → Advanced: tag-based breaks and promos**: brands, clip folders and filler chosen by folder tag, seasonal tag matching, and the tag-based promo settings. Folder names supply tags; see [Commercials on disk](configuration.md#commercials-on-disk).

## Add short shows between programmes

Select interstitial shows on **Ad breaks**, then limit their length, chance, and count per programme slot. Use short content from the library or virtual [weather](web-channels.md), [news](news.md), [radio](radio.md), and [guide](tv-guide.md) shows.

Shorts play in the trailing break rather than interrupting the programme. Their configured lengths must fit the available gap.

## Insert mid-rolls

1. Open **Ad breaks → Enable mid-rolls**.
2. Choose **Chapters only** or **Auto** under **Mode**.
3. Set the cadence and limits below.
4. Select **Save changes**.

| Control | Effect |
| --- | --- |
| **Break roughly every** | Target programme-segment length |
| **Never leave a part shorter than** | Prevent tiny segments |
| **Snap to a marker within** | Limit chapter snapping distance |
| **Longest single break** | Cap each interruption |
| **Hard cap on breaks per programme** | Limit total interruptions |
| **Skip breaks shorter than** | Avoid undersized breaks |

**Chapters only** leaves files without markers uninterrupted. **Auto** uses timed cuts when suitable markers are unavailable; [Auto Chapters](auto-chapters.md) detects boundaries in files without embedded chapters.

For opening and closing clips attached to a title, use [Intros and outros](web-ui-collections.md#add-intros-and-outros).

## Generate announcements and credit panels

| Feature | Starting point |
| --- | --- |
| **Generated promos** | Create templates in **Generated Promos**, add matching show layers, preview, and save |
| **Voice-overs** | Install a voice provider and select it in the template |
| **AI wording** | Enable the writer in supported voice or text settings |
| **Credit squeeze** | Enable **Credit Squeeze** and choose panel content and frequency |
| **Background music** | Choose files or a music library in the feature's music controls |

See [Generated promos](generated-promos.md), [Local AI writer](local-ai.md), [Credit squeeze](credit-squeeze.md), and [Background music](background-music.md) for setup. Generated clips may remain unrendered until their lead time and watched-only conditions are met.

## YAML

```yaml
ad_breaks:
  Any channel:
    seasonal: true
    bumper_placement: StartAndEnd
    folders:
      - { path: 'D:\Media\Commercials\Any Channel\Ads', role: Ads }
      - { path: 'D:\Media\Commercials\Any Channel\Ads\Old', role: Skip }
      - { path: 'D:\Media\Commercials\Any Channel\Bumpers', role: Bumpers }
      - { path: 'D:\Media\Commercials\Any Channel\Filler', role: Filler }
    promos:
      - { type: UpNext, path: 'D:\Media\Commercials\Any Channel\Up Next', per_show: true, times: true }
channels:
  - id: toons
    ad_breaks: [Any channel, Kids channel]
```

See [Diagnosing ad breaks](schedule-performance.md#diagnosing-ad-breaks) when padding remains despite available clips.
