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

**`title_order`** rotates titles separately, while **`order`** controls episodes within each title. Each title retains its own position and wraps independently.

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

Configure matching under **Channel settings → Seasonal tags**, or **`settings.temporal_tags`**. Holiday windows support fixed date ranges, including year-wrapping ranges, and nth-weekday rules.

## Determinism

Rotation cursors, random-generator state, and shuffle bags persist in **`state/playback-state.json`**. Stable sequence keys and **`settings.seed`** make repeated builds reproducible.

Routine rebuilds extend the unplayed plan. Configuration changes replan affected channels while preserving the active grid slot, including remaining mid-roll segments.

Changing **`settings.seed`** reshuffles rotations. Resetting a channel clears its playback positions; neither action is required for a routine rebuild.
