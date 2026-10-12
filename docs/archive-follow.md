# Archive Follow

**Archive Follow** recreates historical schedules using matching media from your library. Missing dates and unmatched programming fall back to the channel's normal rules.

## Configure a channel

1. Enable the built-in **Archive Follow** plugin on **Plugins**.
2. Open a channel's **Follow** tab.
3. Enter the archive directory and inclusive year range.
4. Select **Scan archive**.
5. Map archive blocks and shows.
6. Review unmatched episodes and assign replacements where needed.
7. Select **Save changes**.

## Archive layout

```text
archive/
  _block_index.json
  _manifest.json
  2002/
    01/
      2002-01-01.json
```

Daily files contain a **`schedule`** array. For example:

```json
{
  "schedule": [
    {
      "time": "08:00",
      "end_time": "08:30",
      "show": "Clone High",
      "episode": "Escape to Beer Mountain",
      "blocks": ["Morning Cartoons"]
    }
  ]
}
```

Times use **24-hour `HH:mm`**. Missing end times use the next row's start, or midnight for the last row; **`_block_index.json`** is keyed by block label, and the manifest is informational.

## Choose archive dates

By default, each channel-local date selects an available archive with the same month and day from the configured years. **Follow weekday** switches to weekday matching; **Follow month** optionally restricts those candidates to the same month.

Library coverage ranks complete episode matches above complete show matches, then partial show coverage. Calendar weights favor the same month and exact month/day; lower-ranked days retain a chance of selection.

Selection is seeded by channel, playback date, and **`settings.seed`**. **Archive date possibilities** reports eligible files for the displayed year, not media-match quality.

## Mapping blocks

1. Select archive labels under **Blocks**.
2. Assign them to a channel block.
3. Mark a settings-only block **Follow Only** when it should never run independently.

Mapped blocks supply ads, promos, bumpers, and mid-roll settings at archived times, regardless of their usual hours. Highest block priority wins when multiple mappings apply; unmapped labels use normal active-block or channel settings.

## Mapping shows

Unambiguous normalized titles match automatically, followed by conservative fuzzy matching. Batch assignments can map several archive names to one library show.

| Control | Effect |
| --- | --- |
| **Unmap** | Disable the archive show's automatic assignment |
| **Auto match** | Remove the manual override |
| Manual assignment | Use the chosen library show |

## Matching episodes

Matching uses titles within the assigned show, then supported episode-number labels such as **`EP07`** or **`S02E03`**. Slash-separated archive segments can map to separate library episodes when they fit.

**Episodes** lists incomplete matches. A valid manual episode assignment takes precedence, including when the archive show is unmapped; deleted media IDs revert to automatic matching.

| Missing match | Fallback |
| --- | --- |
| Episode | Seeded random fitting episode from the mapped show |
| Show or fitting episode | Mapped block rotation, then channel base rotation |
| Archive date or interval | Normal scheduling |

## Edit archive days

1. Open **Archive schedules**.
2. Choose a channel's archive or another readable folder.
3. Select a date, or choose **Start this day** for a missing date.
4. Edit programme times, shows, episodes, and block labels.
5. Select **Save day**.

Existing non-schedule fields are preserved. New block labels enter the block index; following channels use changes on their next rebuild.

## Inspect the guide

The dashboard shows **Following YYYY-MM-DD** bands and original block labels. Enable **Archived schedule** beside the channel to compare original listings with the actual playout.

| Color | Meaning |
| --- | --- |
| **Blue** | Normal scheduling |
| **Purple** | Shorts, intros, outros |
| **Pink** | Anchors |
| **Yellow** | Matched or assigned archive episodes |
| **Orange** | Same-show replacements or extra episodes filling a slot |

Valid archive slots take precedence over ordinary scheduling and retain archived end boundaries. Stacking, bumpers, mid-rolls, and gap packing still apply.

## YAML

Place this fragment inside a channel:

```yaml
plugins:
  archive-follow:
    enabled: true
    directory: /media/archives/network
    start_year: 1998
    end_year: 2004
    blocks:
      "Toonami '00": Toonami
    shows:
      The Bugs and Daffy Show: Looney Tunes
      An unwanted show: null
    episodes:
      - show: The Bugs and Daffy Show
        episode: EP07
        media_id: replace-with-library-item-id
```

The example requires a channel block named **Toonami** and a valid library item ID. A **Follow Only** block uses **`plugins: { archive-follow: { follow_only: true } }`**; disabling the plugin preserves assignments and returns the channel to normal scheduling.
