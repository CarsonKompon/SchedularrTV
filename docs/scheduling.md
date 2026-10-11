# How scheduling works

How SchedularrTV decides what plays when: the layers of a channel's schedule, how ad breaks are filled, and the query language used to pick media.

## Layer precedence

Each channel's schedule is built from layers. The highest layer wins, and any time it doesn't claim falls through to the next layer down.

1. **Block anchors**: time-locked slots inside an active block (`blocks[].anchors[]`).
2. **Channel anchors**: `anchors[]`, or a block with a `time` and no `end_time`.
3. **Block windows**: `time` + `end_time`. A window may cross midnight.
4. **Base rotation**: the continuous fallback that keeps the grid full.
5. **Gap packing**: whatever time is still unclaimed.

When block windows overlap, they resolve by priority: specific-date `100` > anchor slot `50` > windowed block `10`. Lower-priority windows are clipped rather than dropped.

> [!NOTE]
> Enabled [Archive Follow](archive-follow.md) slots override all of these layers, but only during their own windows.

`pad_to_nearest` rounds each programme's end up to a clock boundary. This is what creates regular ad breaks on a channel whose programmes would otherwise run back to back.

## Playback orders

The available orders are:

- `chronological`
- `random`
- `shuffle` (no repeats until the pool is exhausted)
- `season_episode`

Spellings are lenient: `season_episode`, `"Season, Episode"` and `SeasonEpisode` all work.

## Query language

Queries pick media from your library. Some examples:

```text
tag:"Commercials" AND tag:"Adult Swim" AND (tag:"Any Time" OR tag:"January" OR tag:"Winter")
show:'SpongeBob SquarePants'
network:'Teletoon' AND kind == 'Episode'
title CONTAINS 'christmas' OR plot CONTAINS 'xmas'
show_id == 'SpongeBob' AND promo_type IN ['later_today', 'coming_up_at_4pm']
season >= 3 AND episode < 20
promo_type MATCHES 'coming_up_at_\d+pm'
```

- **Fields:** `id`, `title`, `plot`, `tag`, `collection`, `network`, `show`, `show_id`, `kind`, `season`, `episode`, `year`, `duration`, `path`, and any provider attribute by name.
- **Operators:** `:` `==` `!=` `>` `>=` `<` `<=` `CONTAINS` `MATCHES` `IN [...]`.
- **Combining:** use `AND`, `OR`, `NOT` and parentheses. Putting two conditions side by side (juxtaposition) implies `AND`.
- **The `:` operator** is an exact match for set fields (`tag`, `collection`, `network`, `show`) and a substring match for text fields (`title`, `plot`).
- **`MATCHES`** uses non-backtracking regular expressions where possible, with a timeout fallback.

Queries parse into an AST that round-trips back to text, so the editor builds them structurally.

### Scoped search

`filter` narrows a selector. `filter_scope` controls what it searches:

- `filter_scope: collection` (the default) layers the filter on top of the selector's own pool.
- `filter_scope: library` ignores the collection and searches the whole library.

## Gap resolution

Gaps between programmes are filled with interstitial media, split into three tiers with different guarantees:

| Tier | YAML | Guarantee |
| --- | --- | --- |
| 1. Interstitial shows | `interstitials.shows` | Plays in full. Collapses larger gaps between programmes. |
| 2. Commercials | `interstitials.commercials` | **Strict fit.** Only selected when the full runtime fits the gap; never trimmed, never overruns. |
| 3. Filler | `interstitials.filler` | Non-guaranteed. Bumpers, IDs and music videos that may be **trimmed** to land exactly on the boundary. |

```yaml
interstitials:
  max_shows_per_slot: 2
  show_chance: 70
  shows:
    query: "collection:'Shorts & Pilots'"
    order: shuffle

  commercials:
    query: "tag:'Commercials' AND (tag:'Adult Swim' OR tag:'Any Channel')"
    strict_fit: true

  filler:
    query: "tag:'Bumpers' OR tag:'StationID' OR tag:'MusicVideo'"
    trimmable: true
    fallback: true
```

### How a break is packed

A break is filled in this order:

1. **Macro-fill**: tier 1 shorts that fit the remaining gap, in their configured order.
2. **Lookahead promos**: upcoming anchors, matched on `show_id` + `promo_type` by distance:
   - `coming_up` / `later`: less than 3h away.
   - `later_today` / `tonight`: less than 12h away.
   - Clock variants such as `coming_up_at_4pm`.
3. **Commercials**: a subset-sum knapsack over tier 2. Item weights round *up* into buckets, so a commercial is never chosen unless it fits whole.
4. **Filler**: closes whatever the knapsack could not. It prefers an exact-length item; otherwise it trims the shortest item that overshoots, so the least content is lost. `stop_before_end` emits it as a "play to the boundary then stop" hint instead of a hard cut.
5. **Up Next promo**: matches `promo_type IN [up_next, next, coming_up_next]` **or** an `Up Next` tag against the show that follows.
   - It's suppressed when that show is the same series that just played (`skip_when_same_series`).
   - By default it plays in any break of the show before (`up_next.placement: any_break` in the `promos` plugin settings). `before_show` puts it last, straight before the new show.
   - Now Playing always runs last.

Anything still unfilled becomes an explicit `Padding` entry rather than silent dead air. Set `interstitials.filler.fallback: false` to let ErsatzTV handle the residual itself.

### Interstitial options

- **Inheritance:** tiers merge block → channel → `settings.interstitials`, so a block only restates what it changes.
- **`max_shows_per_slot`** caps the total tier-1 shorts across all breaks in one programme timeslot, including stacked episodes. Zero disables shorts. Blank inherits the parent limit; the default is unlimited.
- **`show_chance`** is the percent chance (0–100) that a short is used when one fits. It's checked for each short, so breaks aren't always opened with one; a miss leaves the rest of the break to the adverts. Blank inherits; the default is 70, and 100 always uses one. The roll is seeded by the break, so rebuilding gives the same result.
- **Shorthands:** `commercial_base_tags`, `interstitial_shows` and `auto_temporal_tags` still work, and win at whichever level they are declared.

Gap-filling shorts and commercials can recycle fitting items when the remaining shuffle bag cannot fit the gap, without discarding the unplayed longer items. Per-break repeat limits still apply.

> [!TIP]
> If breaks end up with more padding than expected, see [Diagnosing ad breaks](schedule-performance.md#diagnosing-ad-breaks).

## Mid-roll breaks

`mid_roll` interrupts a programme with breaks, rather than saving all the break time for the end.

```yaml
mid_roll:
  mode: auto           # none | chapters | auto
  target_segment: 15m  # aim for a break roughly this often
  min_segment: 8m      # never leave a part shorter than this
  chapter_tolerance: 4m
  max_break_length: 3m
```

- **Modes:** `chapters` breaks only at chapter markers; a programme without them plays whole. `auto` snaps to a marker within `chapter_tolerance` of the cadence point, and cuts on the cadence otherwise.
- **Cadence, not a break count, sets the frequency.** A fixed count would give a 90-minute film two half-hour stretches; `target_segment` gives it five breaks and a 30-minute episode one.
- **`min_segment`** matters because chapter markers are often seconds apart (title cards). It stops a cut landing six seconds into a film.
- **Mid-rolls only contain commercials and filler.** Interstitial shows and Up Next promos are held back for the break after the programme.
- **`max_break_length`** caps each interruption. Whatever the slot cannot spend inside the programme is pushed to the trailing break, where a short can absorb it.
- **Stacking:** with `stack_to_fill`, episode boundaries count as chapter markers on the combined slot timeline. Stacking reserves `max_break_length` per planned break, including the trailing one, before accepting another episode.

## Season-specific intros and outros

To give different seasons different intros:

1. Open a show's **Intros/outros** editor.
2. On one opening block, tick seasons 1 and 2.
3. On another opening block, tick season 3.

The same works for outros, both show-wide and in collection overrides. **All seasons** clears the restriction.

- In YAML, the field is `seasons: [1, 2]` on the bumper block. Empty or omitted matches all seasons.
- Season `0` selects specials.
- In a stacked slot, the first episode determines the intro and the last episode the outro.
- Multiple matching blocks play in their configured order.

## Temporal tags

With `auto_temporal_tags: true`, commercial queries widen at build time with:

- the month (`January`)
- the meteorological season (`Winter`)
- the exact date (`01-10`)
- optionally, the full date (`2026-01-10`)
- any matching holiday (`Christmas`, `Halloween`, `Thanksgiving`, …)

Holiday windows are either fixed `MM-DD` ranges (which may wrap the year) or "nth weekday of month" rules. You can replace them in `settings.temporal_tags.holidays` or under **Settings → Seasonal tags**.

## Determinism

Every build is reproducible:

- Random and shuffle modes use a xoshiro256\*\* PRNG seeded from a stable FNV-1a hash of the sequence key.
- The PRNG state, rotation cursor and shuffle bag are persisted to `state/playback-state.json` in the data folder.
- Rebuilding the same window twice yields identical schedules, and the next day continues the rotation instead of restarting it.
- Changing `settings.seed` reshuffles everything on purpose.

With `title_order`, each title has its own persisted episode position and shuffle bag and wraps independently. Shorter series keep their turns instead of disappearing until the longest finishes.

### Rebuilds and replanning

- Routine rebuilds keep the unplayed plan and extend it.
- Saving configuration replans the future, but preserves the active grid slot, including its remaining mid-roll segments.
- For channels with a grid, the rolling horizon rounds up to a full slot boundary.

## Performance

See [Schedule build profiling](schedule-performance.md) for reproducible benchmarks.
