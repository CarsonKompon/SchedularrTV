# Archive Follow

Archive Follow replays real historical schedules. On each day, a channel airs what a real network aired on a matching date in the archive, using your library's copies of those shows and episodes.

## Getting started

Archive Follow is a built-in plugin (id `archive-follow`). It's off until you switch it on on the **Plugins** page.

1. Switch on **Archive Follow** on the **Plugins** page.
2. Open a channel's **Follow** tab.
3. Enter an archive directory and an inclusive year range.
4. Choose **Scan archive**.
5. Map the archive's [blocks](#mapping-blocks) and [shows](#mapping-shows) to your channel and library, and review [episodes](#matching-episodes) that didn't match.

## Archive layout

The archive directory must be readable by the scheduler and use this layout:

```text
archive/
  _block_index.json
  _manifest.json
  2002/01/2002-01-01.json
```

- **Daily JSON files** contain a `schedule` array with `time`, `end_time`, `show`, `episode` and `blocks`.
- **Times** are 24-hour `HH:mm`. An omitted end uses the next row's start, or midnight for the last row.
- **The block index** is an object keyed by block label.
- **The manifest** is informational. Day selection checks the files directly, so missing days need no repair.

## Choosing a day

By default, each channel-local calendar date chooses an available archive with the same month and day from the selected years.

The choice is seeded by channel, playback date and `settings.seed`. So it is random across dates, but stable across rebuilds. Choices stay stable while the library and mappings are unchanged. A missing date uses normal scheduling.

### Weekday and month options

- **Follow weekday** selects archive dates with the same weekday instead.
- **Follow month** (shown when weekday following is on, checked by default) limits candidates to that month.

Calendar weights apply to the secondary weight (see [How days are ranked](#how-days-are-ranked)):

- **1x** in other months
- **4x** in the same month
- **16x** for the exact month/day (which must also match the weekday)

### Archive date possibilities

**Archive date possibilities** shows, for the displayed **Calendar year**:

- the average eligible archives per day
- the number of days with zero or exactly one possibility

It counts dated files, not media matches or weights. Changing the displayed year only changes the statistics.

### How days are ranked

Library coverage ranks first:

1. Days with every episode available.
2. Days with every show available, but missing some episodes.
3. Days with partial show coverage.

Within each group, higher fractions of fully matched episode slots, then available show slots, rank first. A multi-episode slot only counts as fully matched when all its parts match. Valid manual episode assignments also count.

Each coverage rank gets a weight band larger than the maximum secondary weight, so calendar bonuses can't give a poorer match a higher weight.

The secondary weight is named-programme duration (minimum one hour) times the calendar multiplier.

Lower-ranked, sparse and empty days keep a chance in the weighted draw.

## Mapping blocks

**Blocks** lists every label in the index.

1. Search, and select several labels.
2. Assign them to an existing channel block.

Unmapped entries appear first. Each group is sorted by the index's `count`, highest first.

- The block's ads, promos, bumpers and mid-roll settings apply at the archived times, regardless of its usual hours.
- When several labels map, highest block priority wins. Ties use archive label order.
- Unassigned labels use the normally active block or channel settings.

### Follow Only blocks

Mark a block **Follow Only** to use it only as a mapped settings block. It has no start or end time and never enters normal programming.

Missing archive dates still use normal programming, not Follow Only blocks.

## Mapping shows

**Shows** scans all daily files in the selected years. Each row counts as one occurrence of its show.

- Unmapped entries appear first, highest counts first.
- Saved mappings absent from the current scan have no count and sort last.

### Automatic show matching

Unambiguous normalised library titles match automatically. Then conservative fuzzy matching follows (88/100 minimum and a five-point lead), so `Squibillies` can match `Squidbillies`.

### Manual show mapping

- Batch assignments support aliases, such as two differently named Bugs Bunny programmes pointing to Looney Tunes.
- **Unmap** disables an archive show's automatic assignment.
- **Auto match** removes that override.

## Matching episodes

### Automatic episode matching

- Episode titles match within the assigned show: exact matches first, then fuzzy matching with an 88/100 minimum and a five-point lead.
- Punctuation and common archive annotations are ignored; conflicting part numbers are rejected.
- Slash-separated segments can play as separate episodes when the library stores them separately and they fit.
- Labels such as `EP07`, `Ep: 03` and `Episode 9` use episode metadata when no title matches, defaulting to season 1. Explicit seasons such as `S02E03` and `Season 2 Episode 3` are supported.

### Assigning episodes by hand

**Episodes** lists show/episode pairs without a complete automatic match, including blank episode labels. Search, then assign a library episode. The **Assigned** view keeps saved choices for review.

- A valid assignment takes precedence over automatic matching, even when the archive show is unmapped.
- Deleted library IDs revert to automatic matching.

### When nothing matches

- With no confident episode match, a random fitting episode of the mapped show is chosen. This uses the channel's seeded Archive Follow sequence (separate from normal rotation).
- If the show is missing, unmapped or has no fitting episode, the mapped block's normal rotation is used, then the channel's base rotation.

## Viewing and editing an archive

**Archive schedules** in the sidebar (shown while the plugin is on) opens an archive folder: any channel's, or any folder you pick. A month calendar marks the days that have a schedule, with how many programmes each has.

- Pick a day to read what aired, with each programme's length as the scheduler will read it.
- Change any row (times, show, episode, block labels), add or remove rows, then **Save day**. Other fields in the file are kept.
- On a day with no schedule, **Start this day** or copy another day. Use this to fill gaps, or to build a channel's schedule entirely by hand.
- Point a new, empty folder at the page to start an archive from scratch.
- New block labels are added to `_block_index.json` (created for a new archive), so they can be mapped.

Channels following the folder pick up changes on their next rebuild. The channel's **Follow** tab links straight to its archive.

## In the guide

The dashboard guide shows coloured block bands, with actual start and end times in their tooltips.

- Follow channels show the original archive block labels, with a `Following YYYY-MM-DD` band above each selected date.
- Unlabelled archive periods show `No archive block`.
- Periods without a selected archive show `Normal scheduling`.

### Programme colours

These are also in the key above the guide:

- **Blue**: normal scheduling
- **Purple**: shorts, intros and outros
- **Pink**: anchors
- **Yellow**: matched or assigned archive episodes
- **Orange**: random same-show replacements, or extra episodes added to fill a slot

### Archived schedule row

Each Follow channel has a toggle beside its name that adds a grey **Archived schedule** row under the channel. It shows what the archive aired on the same timeline, so you can see where the real playout matches and where it differs.

Hover a grey block for the archive date, its original times and its block labels.

## How archive slots are scheduled

- Valid archive slots take precedence over normal anchors and block times.
- Archive programmes keep the archived end boundary, even when `max_break` is shorter.
- Stacking, title bumpers, mid-rolls and gap packing still apply.
- Missing archive intervals keep normal scheduling.

Scans are cancellable and cached in memory; scan again after adding files.

Unreadable files are reported, and an unavailable or malformed selected day falls back with a schedule diagnostic.

## YAML

Archive Follow's settings and assignments are saved in the channel's `plugins:` section:

```yaml
plugins:
  archive-follow:
    enabled: true
    directory: '/media/Archived Schedules/network_schedules'
    start_year: 1998
    end_year: 2004
    blocks:
      "Toonami '00": Toonami
      "Adult Swim (2003)": Adult Swim
    shows:
      The Bugs and Daffy Show: Looney Tunes
      The Bugs Bunny and Tweety Show: Looney Tunes
      An unwanted show: null
    episodes:
      - show: The Bugs and Daffy Show
        episode: EP07
        media_id: jellyfin-media-id
```

A Follow Only block has:

```yaml
plugins: { archive-follow: { follow_only: true } }
```

> [!NOTE]
> Files from before version 1.1 used `follow:` on the channel and `follow_only:` on blocks. They still load, and are rewritten in this layout.

With the plugin switched off on the **Plugins** page, the settings are kept, but the channel schedules its own blocks, with a warning in its diagnostics.
