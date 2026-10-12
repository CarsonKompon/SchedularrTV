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

To alternate shows while keeping episodes sequential, choose a title-rotation mode and season/episode order. Referenced collections can supply both settings through **Inherit from collection**.

### Tune individual titles

| Title control | Effect |
| --- | --- |
| **Order** | Override the collection's episode order for one title |
| **Weight** | Adjust turns per rotation; `2` plays twice as often as `1` |
| **Seasons…** | Restrict a show to selected seasons or individual episodes |
| **Collection from your library** | Add an existing media-library collection when the source provides one |

Leave season and episode picks empty to include the whole show. [Viewer-aware scheduling](stats.md#enable-viewer-aware-scheduling) can further adjust title weights from viewing history.

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

## Add intros and outros

1. Open a show's **Intros/outros** editor from **TV Shows** or a collection.
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
