# Configure breaks and promos

**Breaks** selects commercials, short shows, filler, and mid-roll placement. **Promos** selects announcements for current or upcoming programming.

## Add commercials and filler

1. Add clip folders under **App settings → Commercials library** and select **Save**.
2. Open the channel's **General → Scheduling grid**.
3. Set **Start programmes on a boundary of**, such as `30m`.
4. Open **Breaks** and choose commercial brands, clip folders, and filler.
5. Check the folder-layout preview for missing folders.
6. Select **Save changes**.

| Break content | Behavior |
| --- | --- |
| **Interstitial shows** | Whole short programmes that fit |
| **Commercials** | Whole clips without trimming or overruns |
| **Filler** | Remaining time, with trimming when enabled |

Folder names supply tags; see [Commercials on disk](configuration.md#commercials-on-disk). Unfilled time can become black, silent **Padding**.

## Add short shows between programmes

Select interstitial shows on **Breaks**, then limit their length, chance, and count per programme slot. Use short content from the library or virtual [weather](web-channels.md), [news](news.md), [radio](radio.md), and [guide](tv-guide.md) shows.

Shorts play in the trailing break rather than interrupting the programme. Their configured lengths must fit the available gap.

## Insert mid-rolls

1. Open **Breaks → Enable mid-rolls**.
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

## Match seasonal commercials

1. Enable **General → Match seasonal commercial tags** on the channel.
2. Configure **Channel settings → Seasonal tags**.
3. Tag clip folders with names such as **Any Time**, **Winter**, or **Christmas**.
4. Select **Save changes**.

Matching supports month, season, month/day, optional full date, and holiday windows. Blocks can override seasonal matching and commercial brands.

## Add library promos

1. Enable the built-in **Promos** plugin.
2. Arrange clips beneath the configured promo folder by show and promo type.
3. Open the channel's **Promos** tab.
4. Choose brands and configure the enabled promo spots.
5. Check the folder-layout preview and select **Save changes**.

**Up Next** names following programming; **Coming Up** and **Eventually** advertise later airings. Promo settings control eligibility, placement, and suppression of repeated same-show announcements.

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

## Give a block different breaks

Open the block's **Block-specific interstitials and mid-rolls**, then select **Override interstitials** or **Override mid-rolls**. Configure block promos through its plugin section or **Settings for** where offered.

See [Diagnosing ad breaks](schedule-performance.md#diagnosing-ad-breaks) when padding remains despite available clips.
