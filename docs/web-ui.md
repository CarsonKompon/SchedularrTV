# Use the Web UI

Create channels, assign programming, and inspect playback at **http://localhost:5252**. Schedule edits take effect after **Save changes**.

## Connect your media and player

1. Open **App settings**.
2. Enter the **Jellyfin** address and API key, or add folders under **Local TV and movies**.
3. Select the **ErsatzTV Next** executable and enable **Start and stop ErsatzTV Next together with the scheduler**.
4. Verify the **ffmpeg** and **ffprobe** paths.
5. Select **Save**.

**App settings → Run setup guide** reopens onboarding. Media paths must be readable by the scheduler; ErsatzTV also needs access to files it plays.

## Create a channel

1. Open **Channels → New channel → Channel Creation Wizard**.
2. Choose the channel's programming, time slots, commercials, and optional block.
3. Complete the wizard and review the channel editor.
4. Select **Save changes** if the unsaved-changes bar appears.
5. Open **Dashboard** to inspect the guide.
6. Select the channel's play button on **Channels** to test the stream.

**New empty channel** opens a blank editor. Give each channel a unique **ID** and **Number**, then configure **General → Base rotation** to fill time outside blocks and anchors.

## Build a reusable collection

1. Open **Collections**.
2. Enter **New collection name** and select **Create**.
3. Expand the collection and edit **Contents**, or add titles from **TV Shows** and **Movies**.
4. Set the title and episode ordering in the selector.
5. Select the collection in a channel's **Base rotation** or a block's programming.
6. Select **Save changes**.

Collections are shared: changing one affects every selector that references it.

## Schedule blocks and fixed starts

1. Open a channel's **Blocks & anchors** tab.
2. Add a block with its name, start time, recurrence, and programming.
3. Set an end time for a programming window, including windows that cross midnight.
4. Add a **Channel anchor** for a fixed start outside a block, or an anchor inside a block.
5. Review **Day at a glance** and select **Save changes**.

A block with a start time and no end time behaves as an anchor slot. **Base rotation** fills unclaimed time; [Scheduling](scheduling.md#layer-precedence) explains overlapping rules.

## Add commercials and filler

1. Add clip folders under **App settings → Commercials library** and select **Save**.
2. Open **General → Scheduling grid** on the channel.
3. Set **Start programmes on a boundary of**, such as `30m`.
4. Open **Breaks** and choose the commercial brands, clip folders, filler, and optional interstitial shows.
5. Check the folder-layout preview for missing folders.
6. Select **Save changes**.

**Commercials** must fit in full; **filler** can be trimmed when enabled. A blank scheduling grid gives back-to-back rotation programming without grid-created breaks.

### Insert mid-roll breaks

1. Open **Breaks → Enable mid-rolls**.
2. Choose **Chapters** for existing chapter boundaries or **Auto** for timed breaks with chapter snapping.
3. Set the target segment length, minimum segment length, and maximum break length.
4. Select **Save changes**.

[Auto Chapters](auto-chapters.md) can detect break points in files without chapters.

### Match seasonal commercials

1. Enable **General → Match seasonal commercial tags** on the channel.
2. Configure months, seasons, dates, and holidays under **Channel settings → Seasonal tags**.
3. Tag clip folders with matching names such as `Any Time`, `Winter`, or `Christmas`.
4. Select **Save changes**.

## Add intros, outros, and promos

1. Open a show's **Intros/outros** editor from **TV Shows** or a collection.
2. Choose extras or browse for clips on disk.
3. Restrict each bumper block to specific seasons when needed.
4. Open the channel's **Promos** tab to configure library promo clips.
5. Select **Save changes**.

Season `0` means specials; an empty season restriction applies to every season. [Generated promos](generated-promos.md) adds rendered templates and voice-overs.

## Copy settings to another channel

1. Open the destination channel.
2. Select **Copy From** and choose the source.
3. Select **Copy settings**.
4. Review the result and select **Save changes**.

Copying replaces scheduling settings while keeping the destination's name, number, ID, logo, and playback history. Collections remain shared references.

## Change global defaults

| Page | Settings |
| --- | --- |
| **Channel settings** | Time zone, planning horizon, rebuild interval, shuffle seed, seasonal tags, default breaks, plugin defaults |
| **App settings** | Media connections, folders, executable paths, ports, hardware encoding |
| **Plugins** | Plugin switches and application-level plugin settings |

**Channel settings** uses **Save changes**; **App settings** and plugin settings dialogs use their own **Save** buttons. Port and network-access changes require a restart.

## Inspect and refresh playback

| Task | Action |
| --- | --- |
| Read the published schedule | Open **Dashboard → Guide** |
| Test actual video | Open **Channels** and select the play button |
| Find new media | Select **TV Shows → Rescan** |
| Re-read local folders and replan | Select **Channel settings → Rescan and rebuild** |
| Restart episode positions | Select **General → Start this channel over** |
| Keep a channel encoding continuously | Enable **General → Play around the clock (24/7)** |
| Inspect CPU, memory, and active channels | Open **Performance** |
| Inspect audience history | Open the channel's **Stats** tab |

**Start this channel over** resets playback positions and rebuilds. **24/7** consumes encoding resources even without viewers.

## Save, discard, or restore

**Save changes** validates and writes the schedule. **Discard** reloads the saved schedule.

1. Open **Backups**.
2. Select a version and review its changes.
3. Select **Restore**, then **Restore this version?**.

Restoring replaces the schedule and backs up the version it replaces. Schedule backups do not restore app settings, media files, or viewing history.

For direct editing, open **YAML**, change the document, select **Apply to editor**, then **Save changes**. See [YAML reference](yaml.md).
