# Create and tune channels

**Channels** defines channel identity, fallback programming, and playback settings. Blocks, anchors, and breaks build on this foundation.

## Create a channel

1. Open **Channels → New channel → Guided setup**.
2. Choose programming, time slots, commercials, and an optional block.
3. Complete the wizard and review the channel editor.
4. Select **Save changes** if the unsaved-changes bar appears.
5. Check **Dashboard → Guide**.
6. Select the channel's play button on **Channels** to test video.

**Empty channel** opens a blank editor. Assign a unique **ID** and **Number**, a display **Name**, and optional group and logo.

## Renumber channels

Select **Channels → Renumber** to change every channel's number in one list. Type new numbers directly, or use **Number in list order from … counting by …** to renumber the whole lineup in one step (numbers held by plugin channels are skipped). Duplicate numbers are flagged before you can apply. Select **Apply**, then **Save changes**. Plugin channels are shown for reference; change their numbers on the plugin's page.

## Set fallback programming

1. Open **General → Default programming**.
2. Select **Choose what plays** if none exists.
3. Choose **Collection**, **Search rule (advanced)**, or **Picked titles** under **Source**.
4. Set **Order** and **How titles take turns**.
5. Select **Save changes**.

Base rotation fills time outside blocks and anchors. See [Collections and media selection](web-ui-collections.md) for ordering, filters, and version preferences.

## Align programme starts

Open **General → Scheduling grid**.

| Control | Effect |
| --- | --- |
| **Programme start interval** | Round programme endings to an interval such as `30m`; the remainder becomes break time |
| **Maximum ad break** | Cap grid-created breaks, potentially relaxing alignment |
| **Fill a slot with more episodes of the same show** | Add whole episodes before filling the remaining time |
| **Grid anchor** | Measure boundaries from a chosen date and time instead of local midnight |
| **Match seasonal commercial tags** | Select commercials matching configured seasons, dates, and holidays |

A blank grid gives back-to-back rotation programming without grid-created breaks. Use [Breaks and promos](web-ui-breaks.md) to fill gaps and [Blocks and anchors](web-ui-scheduling.md) to reserve fixed times.

## Copy another channel's settings

1. Open the destination channel.
2. Select **Copy settings** and choose the source.
3. Select **Copy settings**.
4. Review the copied programming and select **Save changes**.

Copying replaces scheduling settings while retaining the destination's identity, logo, and playback history. Collection references remain shared; other copied scheduling settings are independent.

## Set encoding and continuous playback

| Location | Action |
| --- | --- |
| **Playback → Encoding** | Override hardware, picture size, or bitrate for this channel |
| **App settings → Video encoding → ErsatzTV channels** | Set defaults inherited by channels |
| **Playback → Play around the clock (24/7)** | Keep encoding continuously for faster tuning |
| Watched-status choice under **Playback** | Decide whether continuous playback triggers watched-only features |

Continuous playback consumes encoding resources without viewers. Inspect **Performance** after enabling it; see [ErsatzTV integration](ersatztv.md#keep-a-channel-running) for keepalive and viewer-count behavior.

## Add advanced channel features

| Tab | Use |
| --- | --- |
| **Stats** | Inspect viewers and adjust show frequency through [viewer-aware scheduling](stats.md) |
| **Follow** | Match historical dates, blocks, and episodes with [Archive Follow](archive-follow.md) |
| **Generated Promos** | Design [rendered announcements](generated-promos.md) |
| **Overlays** | Add [logos and timed graphics](overlays.md) |
| **Credit Squeeze** | Add [end-credit panels](credit-squeeze.md) |

Plugin tabs require their plugins to be enabled. Complete external channels from **IPTV** and **Streamers** use their plugin editors instead of this channel editor.

For resets and recovery, see [Settings and maintenance](web-ui-maintenance.md).
