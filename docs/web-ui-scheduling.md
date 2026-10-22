# Schedule blocks and anchors

**Blocks** reserve programming windows; **anchors** pin programmes to clock times. Configure both under a channel's **Blocks & anchors** tab.

## Add a programming window

1. Open **Blocks & anchors** and add a block.
2. Set **Name**, **Starts**, and **Ends**.
3. Choose **Recurrence** and any required days.
4. Select **Add a pool** under **What plays**.
5. Choose a collection, query, or picked titles.
6. Review **Day at a glance** and select **Save changes**.

An end time earlier than the start crosses midnight, such as **21:00–03:00**. A blank **Ends** creates an anchor slot instead of a window.

## Pin a show to a fixed start

1. Select **Add anchor** under **Channel anchors** or **Anchors inside this block**.
2. Set **Name**, **Time**, and **Episodes back to back**.
3. Choose days, or leave them empty for every day the scope is active.
4. Configure **What plays**.
5. Select **Save changes**.

A channel anchor applies independently of a block; a block anchor applies within its active block. Base rotation fills unclaimed time.

## Schedule a holiday or marathon

1. Add a block with its start and end times.
2. Set **Recurrence → A specific date**.
3. Choose **Repeat** and enter **Date**.
4. Select themed programming, optionally using a selector's **Advanced → Filter**.
5. Set **Priority** if another dated block competes for the time.
6. Select **Save changes**.

| **Repeat** | Date format | Example |
| --- | --- | --- |
| **Every year** | `MM-DD` | `12-25` |
| **Every month** | `DD` | `15` |
| **Once** | `yyyy-MM-dd` | `2026-12-25` |

For a weekly marathon, use **Recurrence → Certain days** instead. A show-specific pool keeps the marathon within one title; **Order** determines episode order.

## Play holiday episodes for a season

**Holidays**, at the bottom of **Blocks & anchors**, change *which episodes* play on certain days of the year without changing *which shows* play. A block (or the base rotation outside blocks) keeps its shows, but plays only their episodes that match the holiday's query: Christmas episodes in Christmas week, Halloween episodes through October.

1. Select **Add a holiday**.
2. Under **Days**, choose either:
    - **The same days every year**, with **From** and **To** as `MM-DD` (`05-01` to `05-05`). A **To** before **From** runs into the new year (`12-26` to `01-02`).
    - **The week of a day**, with a date as `MM-DD` (`12-25`). This is always the whole Sunday-to-Saturday week holding that day that year, whatever day of the week it falls on.
3. Optionally add **Weeks before** and **Weeks after**. The editor shows this year's and next year's days.
4. Enter **Holiday episodes**, a [query](scheduling.md#query-language) that picks them out, such as `plot:'christmas' OR plot:'xmas'`. `plot:` finds the words anywhere in an episode's description.
5. Tick **Search the whole library** to look for holiday episodes everywhere instead of only in what the block plays.
6. Set **Minimum shows** and **Fill in from**, described below.
7. Select **Save changes**.

**Minimum shows** is how many different shows the holiday needs. With fewer, each show that has holiday episodes still gets its share of the slots (one show of a minimum of three plays a third of them, spread evenly). The rest come from **Fill in from**: a collection, or by default the usual episodes of what the block plays. The same happens when a holiday episode can't fit before the next block or anchor.

Notes:

- Holiday episodes play in their own order (shuffled unless set otherwise), so each show's usual run picks up where it left off once the holiday ends.
- Days are in the channel's time zone, and a programme counts by when it starts.
- When holidays overlap, the first one in the list applies; use **Move up** and **Move down** to order them.
- Anchors and Archive Follow slots aren't changed.
- **On** turns a holiday off without deleting it.

## Override the channel inside a block

| Control | Use |
| --- | --- |
| **Pad programmes to / Longest break** | Change grid spacing and break cap |
| **Fill a slot with more episodes of the same show** | Enable or disable stacking for this block |
| **Commercial brand tags** | Change the block's advertising brands |
| **Match seasonal / holiday commercial tags** | Apply seasonal commercial matching |
| **Block-specific interstitials and mid-rolls** | Replace inherited break settings |
| Plugin sections or **Settings for** in plugin tabs | Give the block its own promos, graphics, or credit treatment |

Unset overrides inherit from the channel. For interstitials and mid-rolls, select **Inherit from channel** to remove the override; stacking also offers an explicit inheritance choice.

See [Breaks and promos](web-ui-breaks.md) for break controls and the relevant plugin guide for its inheritance rules.

## Resolve overlaps

**Archive Follow** slots take precedence, followed by block anchors, channel anchors, block windows, base rotation, and gap packing. Overlapping windows use priorities; lower-priority windows are clipped.

Check **Day at a glance** for configured timing and **Dashboard → Guide** for the published result. See [Layer precedence](scheduling.md#layer-precedence) for default priorities.

## Follow historical programming

Enable **Archive Follow**, then map archive labels under the channel's **Follow** tab. Mark a block **Follow Only** when it should supply mapped settings without running as ordinary programming.

Use **Archive schedules** to inspect or edit historical days. See [Archive Follow](archive-follow.md) for date selection, missing-media fallbacks, and episode assignments.
