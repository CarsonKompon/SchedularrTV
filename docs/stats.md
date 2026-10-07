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
