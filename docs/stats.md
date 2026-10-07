# Channel stats

Every channel has a **Stats** tab showing who watches it, when, and what.

## How viewing is counted

Once a minute, SchedularrTV checks what each channel is airing and who is watching it, from every source it can see:

| Source | What it sees | Set up |
| --- | --- | --- |
| **Jellyfin** | Each user watching a channel through Jellyfin Live TV, and on which device. | The Jellyfin connection in App settings. |
| **Plex** | Each user watching a channel through Plex Live TV. | Plex's address and token, on any channel's Stats tab. |
| **Direct** | A player pulling the channel straight from ErsatzTV (VLC, an IPTV app). | ErsatzTV, set up in App settings. |

ErsatzTV can only say that a channel is being watched, not by how many, so a direct player counts as one viewer
("Direct viewer"). When someone from Jellyfin or Plex is on the channel, they're counted instead, since Jellyfin and
Plex pull the channel through ErsatzTV too.

Sessions are matched to channels by channel number first, then by name.

History begins when SchedularrTV starts recording; there's no record from before.

## What the tab shows

- **Watch time**: minutes someone was watching, and viewer time (two people for an hour is two hours of viewer time).
- **Airtime watched**: the share of what aired that someone saw.
- **Peak viewers**: the most watching at once, when, and what was on.
- **Viewers** and **sessions**: who watched, how long they usually stay, and the longest stretch.
- **Watch time by day** (by week over periods longer than 90 days).
- **Last 24 hours**: average viewers each quarter hour.
- **When people watch**: a day-by-hour heat map of average viewers.
- **Most watched** shows by viewer time, and **least watched** by the share of their airtime watched (shows that aired
  at least 30 minutes). A note counts the shows nobody watched at all.
- **Top episodes & films**.
- **Programmes vs breaks**: how much of each was watched, and how many sessions ended during a break.
- **Who watched**, and how watching splits between Jellyfin, Plex and direct players.

Choose the period at the top: 24 hours, 7, 30 or 90 days, a year, or everything.

## Viewer-aware scheduling

Switch it on from a channel's Stats tab (it's saved with the channel, as `viewer_aware`). Shows people watch on the
channel come round more often; shows they skip are rested.

It works wherever the channel already chooses between shows:

- **Among a block's shows.** A block with several shows normally gives each one turn in order. With viewer-aware
  scheduling, the turns are weighted, still spread out, so a favourite isn't played back to back.
- **Among the titles of a pool that rotates titles.** Each title's weight, including any weight you set, is scaled
  by its factor.

Anchored programmes stay where they are, and a show is never dropped entirely.

**How shows are judged.** Each show's factor compares the share of its airtime someone watched with the channel's
average share. A show watched twice as much as average earns more turns; a show nobody watches earns fewer. A show
that has aired only briefly stays near its usual share, and the longer it airs, the more its own figures count. The
strength sets the limits:

| Strength | Favourites, at most | Skipped shows, at least |
| --- | --- | --- |
| Gentle | 1.5× as often | 67% as often |
| Balanced (default) | 2.5× | 40% |
| Strong | 4× | 20% |

**Thresholds.** Nothing changes until the channel passes all of these, over the period it judges on (30 days by
default):

| Threshold | Default |
| --- | --- |
| Days of history | 7 |
| Average daily viewers (different viewers a day) | 1 |
| Hours watched | 10 |
| Minutes a show must air before it's judged | 60 |

The tab shows where the channel stands against each threshold, and which shows are brought forward or resting, with
their factors. Changes apply from the next schedule build after the channel is saved.

## Settings

These live under **Where viewers come from** on any channel's Stats tab and apply to every channel (`Stats` in
`settings.json`):

| Setting | Key | Default |
| --- | --- | --- |
| Plex address | `Stats:PlexUrl` | none |
| Plex token | `Stats:PlexToken` | none |
| Keep history for (days) | `Stats:KeepDays` | 365 (7 to 3650) |
| Record viewing | `Stats:Enabled` | on |

To find the Plex token, open any item in Plex, choose **⋯ → Get Info → View XML**, and copy `X-Plex-Token` from the
address.

**Check now** reads every source straight away and shows any problem next to it. **Clear this channel's history**
forgets the channel's stats.

## Storage

The history is a small SQLite database at `stats/viewing.db` in the data folder. Each airing is one row. Minutes are
stored one by one only when someone was watching, so a channel left playing to nobody costs a row per programme.
