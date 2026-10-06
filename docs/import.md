# Importing from ErsatzTV or Tunarr

**Channels → New channel → Import from ErsatzTV or Tunarr** (also offered at the end of the setup guide) brings another
app's channels over as SchedularrTV channels. It reads the other app's database from a copy, so the app can keep running
and nothing of it is changed. It's only offered in a browser on the computer running the scheduler.

| From | Point it at | Usual place |
| --- | --- | --- |
| ErsatzTV | `ersatztv.sqlite3`, or its folder | `%LOCALAPPDATA%\ersatztv`, `~/.local/share/ersatztv`, Docker: the folder mapped to `/root/.local/share/ersatztv` |
| Tunarr | `db.db`, or its data folder (with `channel-lineups` beside it) | `%APPDATA%\tunarr`, `~/.local/share/tunarr`, Docker: the folder mapped to `/config/tunarr` |

## Finding the programmes

Every programme is looked up in your Jellyfin library: by Jellyfin's id (when the other app used the same Jellyfin), then
by its file (also when it's under another folder, as with Docker mounts), then by show, season and episode, or a film's
title and year. So a library the other app read from Plex works too, as long as Jellyfin has the same media. What can't be
found is counted on each channel and listed under **More**.

## What becomes what

| ErsatzTV | SchedularrTV |
| --- | --- |
| A classic schedule's items at a fixed start time | A block from that time to the next one, playing that item and the ones after it without a time |
| Items without a start time (flood, one, multiple, duration) | The base rotation (a "multiple" of 3 comes up three times as often) |
| Alternate schedules for some days of the week | Blocks on those days |
| A block schedule's templates | Each block at its start time, for its length, on the template's days |
| Shows, seasons, single episodes and films | Shows (or some seasons), episodes by number, films by id |
| Collections and multi-collections | Shared collections (under Collections) |
| A channel's fallback filler preset | The channel's filler |
| Channel logos | Copied in with the scheduler's logos |

| Tunarr | SchedularrTV |
| --- | --- |
| A time-slot schedule | Blocks from each slot's time to the next (weekly ones on their day) |
| A random-slot schedule | The base rotation, weighted like the slots |
| A plain list of programmes | The base rotation: each show (season and episode order) and the films, as often as they came up |
| Custom shows | Shared collections |
| Filler lists | The channel's filler |
| Channel icons | The channel's logo |

Not brought over (and said so on the channel): ErsatzTV's sequential and scripted (YAML) schedules, smart collections and
searches (they're in ErsatzTV's own search syntax: make them again as collection queries), playlists, music and other
videos; Tunarr's redirects to other channels and smart collections. Commercials and promos follow your usual settings,
and a channel number already in use here moves to the next free one.

The channels are added and saved (the schedule is backed up first, under **Backups**), or added as unsaved changes when
you already had some.
