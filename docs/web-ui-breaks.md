# Configure breaks and promos

An **ad break** is a reusable set of folders from your commercials libraries: which folders hold ads, which hold bumpers, which hold filler, and which hold promos. Channels then pick one or more ad breaks on their **Ad breaks** tab, for example an "Any channel" set plus a "Kids channel" set. Their clips are pooled.

## Create an ad break

1. Add clip folders under **App settings → Commercials library** and select **Save**.
2. Open **Programming → Ad Breaks** and select **Create**.
3. Name it and pick the folder holding all of its commercials, bumpers, filler and promos. Everything in that folder plays as an ad unless a folder inside it is given another job.
4. On the next page, untick any of the suggested folders that aren't what they look like (`Bumpers` as bumpers, `Up Next` as Up Next promos, a `Promos` folder sorted by show as all promo types, and so on). Seasonal folders are switched on when the folder has month, season, holiday or date folders.
5. Select **Create ad break**. It opens on its own page, showing just its folder, where each folder can be given a job (**Change folder** picks another):

| Choice | Behavior |
| --- | --- |
| **Ads** | Whole clips packed to fit the break, never cut off |
| **Bumpers** | Station idents; mixed in with the ads, or at the start and/or end of each break (**Settings → Bumpers go**) |
| **Block start** | One full clip opens a block using this collection, before its first programme |
| **Block end** | One full clip caps the block, after its programming and final break |
| **Filler** | Closes whatever the ads can't, cut short (or looped, under **Advanced**) instead of leaving a black screen |
| **Skip** | Leaves a folder out, inside a folder that is used |
| **Promos: all types** | Promos sorted by show and type in either order (`Promos/Looney Tunes/Back To The Show/`, `Promos/Up Next/Top Cat/`); each type is found by its folder's name |
| **Promo: … only** | A folder of one type: Up Next, Now Playing, This Has Been, You're Watching, Back To The Show, Coming Up, Later Today or Eventually |

Folders inside a chosen folder follow along unless you pick otherwise. Folders named like a job (`Bumpers`, `Filler`, `Up Next`, `Now Playing`…) keep being offered as one-click suggestions; **Add all** takes them all.

6. Select **Save changes**.

## Open and close a block

Assign folders the **Block start** and **Block end** jobs, then select the ad break collection under **Schedule → [block] → Override channel settings → Ad breaks**. Blocks with no collection selected inherit the channel's collections.

One clip is chosen from each role's pooled folders per block occurrence. For a timed block, the opening clip starts at the block boundary and the closing clip ends at the block's end; its time is reserved before scheduling programmes and ads. A start-only (anchor-slot) block plays the opener before its programme and the closer immediately afterwards. Ordinary base rotation and individual mid-roll breaks don't trigger these clips.

These folders are excluded from ordinary ads, even when nested under an Ads folder. Seasonal folder rules and nested **Skip** folders still apply. Folders named `Block Start`, `Block Intro`, `Block End`, or `Block Outro` are offered as suggestions.

Clips play whole; if no eligible clip fits, that boundary clip is omitted. Fixed anchors can overrun reserved closing time, in which case the closer is skipped rather than cutting programming. A build starting or ending partway through a block doesn't treat that cut as a new block boundary.

## Preview an ad break

The top of each ad break shows an example half-hour slot: a 22-minute show split by two mid-rolls, then the break before the next show. Each break is filled from the ad break's real clips in the order a playout plays them: the opening promo (You're Watching or This Has Been), an opening bumper, Coming Up and Later Today, the ads with Up Next and Eventually mixed in, filler, a closing bumper, then Back To The Show or Up Next and Now Playing. **Shuffle** picks other clips.

Block start/end clips are outside this per-show preview; inspect a built block in the schedule to see them.

## Match seasonal commercials

Turn on **Seasonal folders** in the ad break's settings. Clips inside a folder named for a month, season, holiday or date (`December`, `Winter`, `Christmas`, `12-25`, `2026-12-25`) then only play at that time; everything else (including an `Any Time` folder) plays all year. The tree shows greyed-out example folders under each ads, bumpers or filler folder. Holidays come from **Channel defaults → Seasonal tags and holidays**.

## Set up promos

Pick a promo choice for a folder in the tree, then adjust it under **Promos**:

| Option | Folder layout |
| --- | --- |
| **Show folders** | `Up Next/Top Cat/clip.mp4` plays only for Top Cat |
| **Other clips suit any show** | Clips loose in the folder, or in `Any Show/`, play for any show |
| *(always)* `Any Movie/` | `Now Playing/Any Movie/clip.mp4` (or `Promos/Any Movie/Now Playing/`) plays for any movie, ahead of the `Any Show` clips, and never for a show |
| **Start time folders** | `Up Next/Top Cat/8pm/` plays only for an 8pm start (also `8:30pm`, `830pm`, `20:30`) |
| **Weekday folders** | `Eventually/Top Cat/Saturday/` plays only for a Saturday airing; `Weekdays/` covers Monday to Friday and `Weekends/` Saturday and Sunday, so `Eventually/Top Cat/Weekdays/4pm/` plays for a 4pm airing on any weekday |
| **New episode folders** | `Up Next/Top Cat/New/` plays only before a new episode (a [Drop](scheduling.md#drop-new-episodes-on-a-schedule) premiere), and wins over the others then; it works for Up Next, Now Playing, This Has Been, You're Watching and Back to the Show |

With seasonal folders on, a promo under a month, season, holiday or date folder (`Promos/Christmas/Top Cat/Up Next/`) only plays then, and that folder isn't taken for a show. The tree shows greyed-out examples of the show, time and day folders each promo looks for. Clips made for the show win over clips for any show. When a movie is on, its own folder (named as the library titles it, such as `Now Playing/Good Burger/`) wins, then `Any Movie`, then `Any Show`; `Any Movie` clips never play for a show. The **"Any movie" folder name** under the ad break's **Advanced** settings renames it, as **"Any show" folder name** does for `Any Show`. Generated promos can still fill a spot no ad break has a folder for.

## Use ad breaks on a channel

1. Open the channel's **General → Scheduling grid** and set **Start programmes on a boundary of**, such as `30m`.
2. Open **Ad breaks** and add one or more ad breaks.
3. Add interstitial shows and mid-rolls on the same tab if wanted.
4. Select **Save changes**.

A block can use its own ad breaks: open it under **Schedule** and pick them under **Override channel settings → Ad breaks**; left empty, it uses the channel's. A channel that picks none uses **Channel defaults → Default ad breaks**; with no defaults either, its breaks are empty (black).

## Fine tuning

Ad lengths, repeats per break, tolerance, strict fit, trimming and looping are under the ad break's **Settings → Advanced**.

## Upgrading from tag-based breaks

Before ad breaks, each channel picked commercials by folder tag (brands, clip folders, filler, seasonal tags) and had its own promo settings. The first time an older schedule loads, those settings are moved into ad breaks and the channels are pointed at them, so breaks fill exactly as before:

- Channels that used the global defaults share an ad break called **Default**.
- A channel with settings of its own gets an ad break named after it; a block with its own gets one named "Channel – Block".
- The original file is kept beside the schedule as `.before-ad-breaks.bak`, and a copy is under **Backups**.

Each moved ad break keeps the old settings as **Tag rules**, which you can still edit on its page. Its clips are pooled with any folders you add. Once your folders cover the same clips, select **Remove tag rules**. Folder names supply tags; see [Commercials on disk](configuration.md#commercials-on-disk).

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
      - { path: 'D:\Media\Commercials\Any Channel\Block Start', role: BlockStart }
      - { path: 'D:\Media\Commercials\Any Channel\Block End', role: BlockEnd }
      - { path: 'D:\Media\Commercials\Any Channel\Filler', role: Filler }
    promos:
      - { type: UpNext, path: 'D:\Media\Commercials\Any Channel\Up Next', per_show: true, times: true }
channels:
  - id: toons
    ad_breaks: [Any channel, Kids channel]
```

See [Diagnosing ad breaks](schedule-performance.md#diagnosing-ad-breaks) when padding remains despite available clips.
