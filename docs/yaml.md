# YAML reference

**`config/schedule.yaml`** defines global defaults, reusable collections, per-title bumpers, and channels. The Web UI writes the same format.

## Edit and validate

1. Open **YAML**.
2. Select **Load current edits** or **Load from disk**.
3. Edit the document.
4. Select **Apply to editor** and resolve validation errors.
5. Select **Save changes**.

**Apply to editor** stages changes; it does not write the file. For scripts, use [**`POST /api/config/validate`**](rest-api.md#validate-and-save-yaml) before saving.

## Minimal channel

```yaml
settings:
  time_zone: UTC
  horizon: 48h
  rebuild_interval: 1h
  seed: schedularrtv

channels:
  - id: movies
    name: Movie Channel
    number: 1
    base_rotation:
      query: "kind == 'Movie'"
      order: shuffle
```

This channel plays matching movies back to back. It needs matching media in an enabled library.

## Document structure

| Section | Purpose |
| --- | --- |
| **`settings`** | Global time zone, horizon, seed, excluded libraries, break defaults, and plugin settings |
| **`global_collections`** | Named selectors reused by channels and blocks |
| **`titles`** | Per-show intro and outro defaults, keyed by series name |
| **`channels`** | Channel definitions |

Keys use **snake_case**. Durations use units such as **`500ms`**, **`10s`**, **`30m`**, and **`48h`**; quote clock times such as **`"20:00"`** and date strings such as **`"12-25"`**.

## Channel fields

| Field | Purpose |
| --- | --- |
| **`id`** | Unique channel identifier used by state and API routes |
| **`name`** | Display name |
| **`number`** | Unique positive channel number |
| **`group`**, **`logo`** | Playlist grouping and logo URL |
| **`time_zone`** | Override of the global time zone |
| **`base_rotation`** | Selector for unclaimed time |
| **`pad_to_nearest`** | Grid interval; omitted for back-to-back programming |
| **`max_break`** | Cap on grid-created trailing breaks; may relax grid alignment |
| **`stack_to_fill`** | Additional whole episodes of the same show within a slot |
| **`schedule_start`** | Explicit grid anchor as a date-time with offset |
| **`auto_temporal_tags`** | Seasonal commercial matching |
| **`interstitials`**, **`mid_roll`** | Break content and placement |
| **`anchors`**, **`blocks`** | Fixed starts and programming windows |
| **`always_on`** | Continuous playback rather than on-demand streaming |
| **`always_on_counts_as_watched`** | Watched-only plugin behavior for a continuous channel; defaults to true |
| **`encoding`** | Per-channel ErsatzTV hardware, height, and bitrate overrides |
| **`viewer_aware`** | Audience-weighted scheduling |
| **`plugins`** | Plugin-owned settings keyed by plugin ID |

**`tvg_id`** is derived as `schedularrtv.<number>`; do not author it.

## Collections and selectors

```yaml
global_collections:
  cartoons:
    query: "kind == 'Episode' AND genre:'Animation'"
    title_order: shuffle
    order: season_episode
    exclude_seasons: [0]

channels:
  - id: cartoons
    name: Cartoons
    number: 2
    pad_to_nearest: 30m
    stack_to_fill: true
    base_rotation:
      collection: cartoons
```

| Selector field | Behavior |
| --- | --- |
| **`collection`** | Reference to a key in `global_collections` |
| **`query`** | Inline library query |
| **`include`** | Explicit show, movie, season, or episode picks; the visual editor generates these |
| **`filter`** | Additional query |
| **`filter_scope: collection`** | Filter within the selected pool; default |
| **`filter_scope: library`** | Search the full library instead of the selected pool |
| **`order`** | `chronological`, `random`, `shuffle`, `season_episode`, or `drop` |
| **`drop`** | Drop cadence for `order: drop` (days or every, time, count, release order, repeats); see [Drop new episodes](scheduling.md#drop-new-episodes-on-a-schedule) |
| **`title_order`** | Separate title rotation from episode ordering |
| **`min_duration`**, **`max_duration`** | Runtime limits |
| **`exclude_seasons`** | Seasons excluded unless explicitly selected |
| **`versions`** | Alternate-version selection |
| **`state_key`** | Explicit identity for persisted playback state |

An omitted **`order`** inherits from a referenced collection, otherwise defaults to shuffle. Each **`include`** entry can set its own **`order`**, **`weight`** and **`drop`**, so one show can drop new episodes inside a shuffled collection. See [Query language](scheduling.md#query-language) for fields and operators.

## Blocks and anchors

Add this fragment inside a channel:

```yaml
anchors:
  - name: Noon show
    time: "12:00"
    count: 2
    show:
      query: "show:'Rugrats'"
      order: season_episode

blocks:
  - name: Saturday night
    time: "20:00"
    end_time: "23:00"
    days: [Saturday]
    shows:
      - collection: cartoons

  - name: Christmas
    time: "00:00"
    end_time: "23:59"
    schedule:
      date: "12-25"
      repeat: yearly
    shows:
      - query: "title CONTAINS 'christmas'"
        order: shuffle
```

**`time`** without **`end_time`** creates an anchor slot. A window can cross midnight, and **`blocks[].anchors`** adds fixed starts inside it; see [Layer precedence](scheduling.md#layer-precedence).

## Holidays

A channel's **`holidays`** narrow what its blocks and base rotation play to holiday episodes on certain days of the year (see [Play holiday episodes for a season](web-ui-scheduling.md#play-holiday-episodes-for-a-season)):

```yaml
holidays:
  - name: Christmas
    dates: week_of          # the Sunday-to-Saturday week holding `date`
    date: "12-25"
    weeks_before: 1
    query: "plot:'christmas' OR plot:'xmas'"
    minimum_shows: 3        # fewer: the rest is filled in
    fallback: winter_specials   # a collection; omit for the usual episodes
  - name: May Day
    dates: range            # the default
    start: "05-01"
    end: "05-05"
    weeks_after: 1
    query: "plot CONTAINS 'may day'"
    global: true            # search the whole library
    order: season_episode   # holiday episodes shuffle unless set
    enabled: false
```

The first enabled holiday whose days hold a programme's start applies to it.

Top-level **`global_holidays`** take the same fields and apply to every channel, after the channel's own (a channel holiday on the same days wins). A channel sets **`ignore_global_holidays: true`** to leave them off:

```yaml
global_holidays:
  - name: Halloween
    start: "10-01"
    end: "10-31"
    query: "plot:'halloween'"

channels:
  - id: news
    ignore_global_holidays: true
```

## Breaks

Add this fragment inside a channel, or put **`interstitials`** under **`settings`** for defaults:

```yaml
interstitials:
  max_shows_per_slot: 1
  show_chance: 70
  shows:
    query: "collection:'Shorts'"
    max_duration: 5m
    order: shuffle
  commercials:
    query: "tag:'Commercials'"
    strict_fit: true
    max_item_duration: 3m
    max_repeats_per_break: 1
  filler:
    query: "tag:'Bumpers' OR tag:'StationID'"
    trimmable: true
    fallback: true

mid_roll:
  mode: auto
  target_segment: 15m
  min_segment: 8m
  chapter_tolerance: 4m
  max_break_length: 3m
```

Break tiers inherit from **block → channel → global defaults**. **`show_chance`** uses a percentage from `0` to `100`; **`max_shows_per_slot: 0`** disables shorts.

**`mid_roll.mode`** accepts `none`, `chapters`, or `auto`. See [Mid-roll breaks](scheduling.md#mid-roll-breaks) for timing behavior.

## Plugin sections

Plugin settings use the plugin ID at the applicable scope:

```yaml
settings:
  plugins:
    promos:
      enabled: true
      lookahead: 12h
      max_per_break: 2

channels:
  - id: movies
    name: Movies
    number: 1
    base_rotation:
      query: "kind == 'Movie'"
    plugins:
      credit-squeeze:
        enabled: true
        style: side_by_side
        side: right
        split_percent: 60
```

Each plugin defines its own fields and override rules. Application-level plugin settings belong in **`settings.json`**, under **`Plugins:<id>`**; disabling a plugin keeps its schedule sections.

## Reference and recovery

The [example schedule](../src/SchedularrTV.Service/config/schedule.example.yaml) combines collections, anchors, blocks, commercials, and promos. The [configuration models](../src/SchedularrTV.Core/Configuration) define additional fields.

**Backups** restores earlier schedule versions. Changing **`settings.seed`** reshuffles deterministic rotations; **Start this channel over** resets one channel's playback positions.
