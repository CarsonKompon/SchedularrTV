# Channel stats

**Stats** records viewing history and can use it to adjust show frequency. History starts when recording begins; earlier viewing is unavailable.

## Inspect viewing

1. Open a channel's **Stats** tab.
2. Select a period from **24 hours** through **Everything**.
3. Review audience totals, viewing patterns, shows, and break behavior.

| Metric | Meaning |
| --- | --- |
| **Watch time** | Time with at least one viewer |
| **Viewer time** | Sum across viewers; two viewers for an hour equals two hours |
| **Airtime watched** | Share of airtime seen by someone |
| **Peak viewers** | Largest simultaneous audience |
| **Most watched** | Shows ranked by viewer time |
| **Least watched** | Watched share for shows with at least 30 minutes aired |
| **Programmes vs breaks** | Viewing split and sessions ending during breaks |

## Connect viewer sources

| Source | Setup | Counting |
| --- | --- | --- |
| **Jellyfin** | **App settings → Jellyfin** | Users and devices watching Live TV |
| **Plex** | **Stats → Where viewers come from** | Live TV users through the configured address and token |
| **Direct players** | **App settings → ErsatzTV Next** | One aggregate direct viewer per active channel |

The recorder samples once a minute. When Jellyfin or Plex viewers are known, they replace the aggregate direct count; sessions match by channel number, then name.

Select **Check now** to test sources. A **24/7** keepalive never counts as a direct viewer.

## Enable viewer-aware scheduling

1. Enable **viewer-aware scheduling** on the channel's **Stats** tab.
2. Choose the strength and history thresholds.
3. Select **Save changes**.

The next build adjusts turns among block shows and rotating title pools. Anchors stay fixed, and no show is removed entirely.

| Strength | Maximum increase | Minimum frequency |
| --- | --- | --- |
| **Gentle** | 1.5× | 67% |
| **Balanced** | 2.5× | 40% |
| **Strong** | 4× | 20% |

Default eligibility requires **7 days** of history, **1 average daily viewer**, and **10 watched hours**, evaluated over **30 days**. A show needs **60 aired minutes** before its own viewing figures determine its weight.

## Manage history

**Where viewers come from** applies across channels.

| Setting | Configuration | Default |
| --- | --- | --- |
| **Plex address** | `Stats:PlexUrl` | Unset |
| **Plex token** | `Stats:PlexToken` | Unset |
| **Keep history for** | `Stats:KeepDays` | 365 days; range 7–3650 |
| **Record viewing** | `Stats:Enabled` | On |

**Clear this channel's history** deletes that channel's statistics. History is stored in **`stats/viewing.db`** inside the data folder.
