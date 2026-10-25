# Scheduling and queries

The scheduler combines fixed starts, programming windows, and a fallback rotation into a continuous timeline. Queries select media; break rules fill the remaining time.

## Layer precedence

| Priority | Layer |
| --- | --- |
| Highest | Enabled **Archive Follow** slots |
| 2 | **Block anchors** inside an active block |
| 3 | **Channel anchors**, including blocks with no end time |
| 4 | **Block windows** with start and end times |
| 5 | **Base rotation** |
| Lowest | **Gap packing** |

Overlapping windows use priority: specific-date rules default to `100`, anchor slots to `50`, and windows to `10`. Lower-priority windows are clipped.

**`pad_to_nearest`** rounds programme endings to grid boundaries. **`max_break`** can shorten grid-created gaps; **`stack_to_fill`** adds whole episodes of the same show before filling the remainder.

## Playback orders

| Order | Behavior |
| --- | --- |
| **`chronological`** | Premiere-date ordering |
| **`season_episode`** | Season and episode ordering |
| **`shuffle`** | Shuffled pool without repeats until exhausted |
| **`random`** | Random selection with possible repeats |
| **`drop`** | New episodes drop on a schedule; between drops only episodes that already dropped play, as repeats |

**`title_order`** rotates titles separately, while **`order`** controls episodes within each title. Each title retains its own position and wraps independently.

### Drop new episodes on a schedule

With **`order: drop`**, each show releases episodes on a cadence, like a network airing a new episode every Friday. From each drop day, the show's first airings (at its normal times in the rotation) are the new episodes; until the next drop, only episodes that have already dropped play, as repeats. One show can instead set an air **`time`**: its new episode then takes the first slot at or after that time, ahead of whatever else was due.

```yaml
global_collections:
  saturday-morning:
    order: shuffle                 # most shows shuffle as usual
    include:
      - show: Bluey
        order: drop
        drop: { days: [monday], time: "20:00", count: 1 }   # new episodes air at 8pm Mondays
      - show: Bob's Burgers
        order: drop
        drop: { every: 3d, count: 2, release_order: air_date }
      - show: Rugrats
  cartoon-premieres:
    order: drop                    # every show here drops on Fridays (the default), at its normal airings
```

| Field | Meaning |
| --- | --- |
| **`days`** | Weekdays drops happen, in the channel's time zone; Friday when neither days nor `every` are set |
| **`every`** | Interval between drops instead of weekdays (`3d`, `12h`) |
| **`time`** | For one show: when its new episodes air, ahead of whatever else is due. Left out, they take the show's normal airings |
| **`count`** | Episodes per drop; `1` by default |
| **`release_order`** | `episode_order` (season, episode) or `air_date` |
| **`repeats`** | How dropped episodes repeat between drops: `shuffle` (default), `random`, `season_episode` or `chronological` |
| **`start`** | First drop date, `yyyy-MM-dd`. Without it, drops begin with the first build, and the latest drop time counts, so one episode is out straight away |
| **`already_out`** | Episodes treated as already released (repeats only) before the first drop |
| **`season_break`** | A break between seasons (`3w`, `10d`). After a season's last episode drops, the next season's first drops on the first drop day at least this long after it; a drop never runs on into the next season. Left out, seasons follow straight on |
| **`at_end`** | Once the last episode has dropped: `stop` (default; everything keeps repeating) or `restart` (after the season break, start dropping again from the first episode) |

```yaml
      - show: Bluey
        order: drop
        drop: { days: [friday], season_break: 4w, at_end: restart }   # a month off between seasons, then round again
```

The channel page's **Drops** tab lists every show on drops on that channel: which episode is out, whether it's on a season break or finished, and when the next drop is. **Reset…** puts a show back to any episode: pick how many episodes are already out, and whether the next one drops now or at the next drop time. The channel is rebuilt from the next slot so the guide follows at once. A reset lasts until the show's first-drop date (`start`) is changed, or the channel is started over.

Drops apply to each show on its own. A collection's **`drop`** settings are the default for every show in it on `drop`; a title's own **`drop`** overrides them. In a collection without **`title_order`**, shows not on `drop` keep the collection's order together, and shows come up in proportion to how many episodes each has out. With **`title_order`**, drop shows take their turns like any other title; only a show with an air `time` jumps the queue for its new episode.

Whether an episode is out is worked out from the time it would air, so rebuilding the schedule never releases one early. The first airing of a dropped episode is marked as a **new episode**: generated promos, promo clips in a `New` folder, and overlay conditions can all react to it.

## Query language

```text
kind == 'Movie'
show:'Clone High' AND season >= 1
network:'Teletoon' AND kind == 'Episode'
genre:'Comedy' AND year >= 2000
tag:'Commercials' AND (tag:'Adult Swim' OR tag:'Any Channel')
title CONTAINS 'christmas' OR plot CONTAINS 'xmas'
show_id == 'Rugrats' AND promo_type IN ['up_next', 'later_today']
promo_type MATCHES 'coming_up_at_\d+pm'
```

| Element | Supported values or behavior |
| --- | --- |
| **Fields** | `id`, `title`, `plot`, `tag`, `collection`, `genre`, `network`, `show`, `show_id`, `kind`, `season`, `episode`, `year`, `duration`, `path`, `rating`, `promo_type`, provider attributes |
| **Comparison** | `:`, `==`, `!=`, `>`, `>=`, `<`, `<=` |
| **Text and membership** | `CONTAINS`, `MATCHES`, `IN [...]` |
| **Logic** | `AND`, `OR`, `NOT`, parentheses; adjacent conditions imply `AND` |
| **`:`** | Exact set membership for tags, collections, networks, and shows; substring matching for title and plot |
| **`MATCHES`** | Regular expression with bounded execution |

Test expressions through [**`POST /api/library/query/parse`**](rest-api.md#search-the-library). **`GET /api/metadata`** returns the editor's query vocabulary.

### Scoped search

**`filter_scope: collection`** narrows the selector's current pool. **`filter_scope: library`** ignores that pool and searches the entire library.

## Gap resolution

| Tier | YAML | Guarantee |
| --- | --- | --- |
| **Interstitial shows** | `interstitials.shows` | Whole shorts that fit |
| **Commercials** | `interstitials.commercials` | Whole clips; no trimming or overrun |
| **Filler** | `interstitials.filler` | Residual coverage, with trimming when enabled |

Trailing breaks combine shorts, lookahead promos, commercials, filler, and eligible closing promos. **Up Next** placement is configurable; **Now Playing** runs last.

Commercial packing searches for clips that fit the remaining duration. Filler prefers an exact fit, then the shortest eligible overshoot; **`stop_before_end`** supplies a stop-at-boundary hint.

Unfilled time becomes explicit **Padding**, rendered as black video and silence by the ErsatzTV output. See [Diagnosing ad breaks](schedule-performance.md#diagnosing-ad-breaks).

### Inheritance and repetition

Break tiers inherit from **block → channel → `settings.interstitials`**. A scope only needs to declare the fields it changes.

**`max_shows_per_slot`** caps shorts across the entire programme slot, including stacked episodes. **`show_chance`** controls the seeded chance of adding each fitting short; `100` always attempts one and `0` disables the chance.

Shorts and commercials may reuse fitting clips when the remaining shuffle bag cannot fit the gap. Per-break repeat limits still apply, and unplayed longer clips remain in the bag.

## Mid-roll breaks

| Mode | Placement |
| --- | --- |
| **`none`** | No mid-rolls |
| **`chapters`** | Chapter boundaries only; files without chapters play whole |
| **`auto`** | Timed cadence, snapped to nearby chapters when available |

**`target_segment`** controls cadence, **`min_segment`** prevents tiny programme parts, and **`chapter_tolerance`** limits snapping distance. **`max_break_length`** caps interruptions; remaining break time moves after the programme.

Stacked episode boundaries can serve as break points. Interstitial shorts remain in the trailing break; promo eligibility follows the configured promo type and placement.

See [YAML breaks](yaml.md#breaks) for an example and [Auto Chapters](auto-chapters.md) for detected boundaries.

## Season-specific intros and outros

1. Open the show's **Intros/outros** editor.
2. Assign seasons to each opening or closing bumper block.
3. Select **All seasons** to clear a restriction.
4. Select **Save changes**.

The YAML field is **`seasons: [1, 2]`** on a bumper block; season `0` selects specials. Stacked slots use the first episode's season for intros and the last episode's season for outros.

## Temporal tags

With **`auto_temporal_tags: true`**, eligible commercial tags include:

| Match | Example |
| --- | --- |
| **Any time** | `Any Time` |
| **Month** | `January` |
| **Season** | `Winter` |
| **Month and day** | `01-10` |
| **Full date**, when enabled | `2026-01-10` |
| **Holiday window** | `Christmas` |

Configure matching under **Channel defaults → Seasonal tags and holidays**, or **`settings.temporal_tags`**. Holiday windows support fixed date ranges, including year-wrapping ranges, and nth-weekday rules.

## Determinism

Rotation cursors, random-generator state, and shuffle bags persist in **`state/playback-state.json`**. Stable sequence keys and **`settings.seed`** make repeated builds reproducible.

Routine rebuilds extend the unplayed plan. Configuration changes replan affected channels while preserving the active grid slot, including remaining mid-roll segments.

Changing **`settings.seed`** reshuffles rotations. Resetting a channel clears its playback positions; neither action is required for a routine rebuild.
