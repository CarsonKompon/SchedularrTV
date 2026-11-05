# Collections and media selection

**Collections** shares programming across channels and blocks. The same media selector also supports direct title picks and library queries.

## Build a reusable collection

1. Open **Collections**.
2. Enter **New collection name** and select **Create**.
3. Expand the collection and edit **Contents**, or add titles from **TV Shows** and **Movies**.
4. Choose the source and ordering.
5. Select the collection in a channel's **Base rotation** or a block's programming.
6. Select **Save changes**.

Changing a collection affects every selector referencing it. Use a separate collection for independent programming.

To rename or delete a collection, use the pencil and bin buttons on its row, or right-click it. **Rename** opens a popup, and everything that uses the collection (channels' rotations, blocks, anchors, holidays' fill-ins, other collections) switches to the new name. Like other edits, it's kept once you **Save changes**.

On **TV Shows** and **Movies**, right-click a title (or one of several selected) for **Add to collection** and **Remove from collection**; shows also have **Intros/outros**.

## Choose a source

| **Source** | Use |
| --- | --- |
| **Collection** | Reuse an existing collection and inherit its selection settings |
| **Picked titles** | Choose shows, movies, seasons, or episodes explicitly |
| **Query** | Select media by tags, genre, network, dates, or other metadata |

For example, **`kind == 'Movie' AND year < 1990`** selects older movies. Switching source mode replaces the previous mode's selection.

## Separate title rotation from episode order

| Control | Choice | Result |
| --- | --- | --- |
| **How titles take turns** | **Rotate titles — in the order listed** | Give each title its turn in the chosen sequence |
| **How titles take turns** | **Rotate titles — shuffled** | Shuffle title turns separately from their episodes |
| **How titles take turns** | **All episodes in one pool (no rotation)** | Order every matching item together |
| **Order** | Season/episode or chronological | Advance through each title when title rotation is enabled |
| **Order** | Shuffle | Avoid repeats until the pool is exhausted |
| **Order** | Random | Allow repeated selections |
| **Order** | Drop | Release new episodes on a schedule; repeat the ones already out in between |

To alternate shows while keeping episodes sequential, choose a title-rotation mode and season/episode order. Referenced collections can supply both settings through **Inherit from collection**.

### Drop new episodes

Choose **Drop** as the collection's **Order** to give every show in it the same release schedule, or choose it for one title in **Tune individual titles** and open **Drops…** to give that show its own.

| Drop control | Effect |
| --- | --- |
| **Drop on** | Weekdays new episodes come out (Friday to start with); untick them all to use **Or every** (`3d`, `12h`) |
| **At (time of day)** | Individual shows only, optional: when their new episodes air, ahead of whatever else is due. Empty uses the show's normal airings |
| **Episodes per drop** | How many come out at once |
| **Next episode by** | Episode order, or air date |
| **Between drops, repeats play** | Shuffled, at random, or in order |
| **First drop** | The first drop's date; empty starts with the next build, with one episode out straight away |
| **Already out before the first drop** | Episodes that count as released (repeats only) from the start |

From each drop day, the show's first airings at its normal times are the new episodes (or, with an air time, the first slot at or after it). A new episode's first airing counts as a **new episode** for promos and overlays. See [Drop new episodes on a schedule](scheduling.md#drop-new-episodes-on-a-schedule).

### Tune individual titles

| Title control | Effect |
| --- | --- |
| **Order** | Override the collection's episode order for one title |
| **Weight** | Adjust turns per rotation; `2` plays twice as often as `1` |
| **Drops…** | For a title on **Drop**: use the collection's schedule or give it its own |
| **Seasons…** | Restrict a show to selected seasons or individual episodes |
| **Timeslots…** | Limit when a show or movie may be scheduled: tick days and, optionally, a start and end time |
| **Collection from your library** | Add an existing media-library collection when the source provides one |

Leave season and episode picks empty to include the whole show.

**Timeslots** are the only times a title is considered when the schedule is planned, in the channel's time zone. They don't guarantee it airs then; the collection's other titles fill the rest of the day. **Simple** sets one start and end time for every ticked day. **Per day** gives each day its own times. Leave the times empty for the whole day. An end earlier than the start runs past midnight (10pm to 2am). **Any time** removes the limit. If at some moment nothing in the block is allowed to play, the limits are set aside rather than leaving the time to adverts. Fixed starts (anchors) and holiday specials ignore timeslots.

In a collection that plays every episode in one order (no title rotation), a show outside its slot is skipped over, not held back. Use a **Title order** if each show should keep its own place. [Viewer-aware scheduling](stats.md#enable-viewer-aware-scheduling) can further adjust title weights from viewing history.

## Restrict content and versions

Open the selector's **Advanced** section.

| Control | Effect |
| --- | --- |
| **Filter (extra condition)** | Apply another query, such as **`plot CONTAINS 'christmas'`** |
| **Filter scope → Narrow this selection** | Keep matches within the selected collection or titles |
| **Filter scope → Search the whole library** | Search beyond the original selection |
| **Minimum duration / Maximum duration** | Restrict programme runtimes |
| **Avoid these versions** | Exclude alternate releases unless that would remove every version of an episode |
| **Prefer these versions** | Favor a release when alternatives exist |

Version controls appear when the library has version labels. Only one alternate version contributes to programming; see [Query language](scheduling.md#query-language) for filter syntax.

## Skip specials

Enable **Skip specials and extras in every title** for a directly defined selection. A referenced collection supplies this setting instead.

The option excludes season `0` without fixing the list of regular seasons, allowing newly added seasons to appear. Explicit season picks remain as selected; movies are unaffected.

## Browse a show's seasons and episodes

Click **View show** on **TV Shows** to open its detail page. Clicking the show card or list row checks or unchecks it for bulk collection selection. Expand a season on the detail page to see its episodes, select a target collection, then use **Add season to collection** or **Add episode**. **Add whole show** includes future seasons too. These actions add to the collection without narrowing existing picks.

### Show and season aliases

The show detail page has a **Show aliases** editor, and each expanded season has its own alias editor. Add alternate names such as `Billy and Mandy` for `The Grim Adventures of Billy & Mandy`, or `Total Drama Island` for season 1 of `Total Drama`.

All aliases match show folders for promos in **Ad Breaks**. A season alias matches only when that season is being promoted. Aliases are also included when searching the TV Shows page.

Check **Preferred name** to use an alias for generated promo speech, Up Next text, and variable-image matching. Each show and season can have one preferred alias. The season's preference takes precedence over the show's; otherwise the original library title is used. Adding an alias without selecting it as preferred only adds a matching name. Use the editor's usual **Save** action to persist changes.

## Show and movie pages

Open a show from **TV Shows**, or a movie from **Movies** (**View show** / **View movie**), for its page. Like a channel's page it has tabs:

| Tab | Shows | Movies | What it's for |
| --- | --- | --- | --- |
| **Overview** | ✓ | ✓ | Its metadata, editable (below). Shows also have their aliases and the screen bug setting here. |
| **Seasons & episodes** | ✓ | | Season aliases, screen bug per season and episode, adding seasons or episodes to a collection. |
| **Intros & outros** | ✓ | | The show's intro and outro blocks, and **Edit intros & outros**. |
| **Collections** | ✓ | ✓ | The collections that list it, with what each takes; add it to another, or take it out. |
| **History** | ✓ | ✓ | Its last metadata changes, each revertible. |

### Edit metadata

Change the title, sort title, year, rating, networks (studios for movies), overview, genres or tags on **Overview**, then choose the button named for where the change goes:

- **Push to Jellyfin** for Jellyfin items: the item is changed on your Jellyfin server, for every app that uses it. The changed fields are locked there, so Jellyfin's next metadata refresh doesn't download them again.
- **Update NFO file** for local folders: the show's `tvshow.nfo`, or the movie's `.nfo`, is updated, or created when there isn't one. Everything else in the file is kept, and the file as it was is kept beside it as `.nfo.bak`. The folders are rescanned afterwards.

Nothing is written until you confirm on the **Are you sure?** step, which says exactly what will change and lists each field's value now and after. Metadata edits don't go through **Save changes**; they're written straight to the source.

Renaming a show moves its settings (aliases, intros and outros, screen bug) and its collection entries to the new name; renaming a movie moves its collection entries. Those moves are schedule changes, so **Save changes** afterwards.

Other sources can be edited when their plugin supports it; otherwise the metadata is shown read-only.

### Edit history and revert

**History** keeps the last 20 metadata changes written from the page (kept in the data folder's `metadata-history` folder, so they survive restarts), each with every changed field's value before and after. **Revert** puts that change's fields back as they were before it, after an **Are you sure?** step. Only the fields that still differ at the source are written, and the revert is itself kept in the history, so it can be undone.

## Add intros and outros

1. Open a show from **TV Shows**, then choose **Edit intros & outros** on its **Intros & outros** tab. Collection-specific overrides are also available from a collection.
2. Choose extras or **Browse files on disk**.
3. Set opening or closing placement and clip order.
4. Restrict bumper blocks to specific seasons when needed.
5. Select **Save changes**.

| Advanced option | Use |
| --- | --- |
| **Options → Only play in** | Restrict seasons; no selections means every season |
| **When there are several clips, play** | Control selection within a bumper block |
| **Any episode** beside a clip | Pin the clip to particular episodes |
| **Skip this block when the episode's file name contains** | Avoid adding an intro or outro already present in a particular release |
| Collection-specific bumpers | Override the show's defaults for that collection |

Stacked slots use the first episode's season for intros and the last episode's season for outros. See [Local media](local-library.md#intros-outros-and-excluded-files) for supported extras.
