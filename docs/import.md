# Importing from ErsatzTV or Tunarr

Bring your channels over from ErsatzTV or Tunarr as SchedularrTV channels. The other app's database is read from a copy, so it can keep running and nothing in it is changed.

## Running the import

1. Go to **Channels → New channel → Import from ErsatzTV or Tunarr**. It's also offered at the end of the setup guide.
2. Point it at the other app's database (see [Where to find the database](#where-to-find-the-database)).

> [!NOTE]
> The import is only offered in a browser on the computer running the scheduler.

The channels are added and saved, and the schedule is backed up first (under **Backups**). If you already had unsaved changes, the channels are added as unsaved changes instead.

### Where to find the database

**ErsatzTV**: point it at `ersatztv.sqlite3`, or its folder. Usually in:

- `%LOCALAPPDATA%\ersatztv`
- `~/.local/share/ersatztv`
- Docker: the folder mapped to `/root/.local/share/ersatztv`

**Tunarr**: point it at `db.db`, or its data folder (with `channel-lineups` beside it). Usually in:

- `%APPDATA%\tunarr`
- `~/.local/share/tunarr`
- Docker: the folder mapped to `/config/tunarr`

## Finding the programmes

Every programme is looked up in your Jellyfin library, in this order:

1. By Jellyfin's id (when the other app used the same Jellyfin).
2. By its file, also when it's under another folder (as with Docker mounts).
3. By show, season and episode, or a film's title and year.

So a library the other app read from Plex works too, as long as Jellyfin has the same media.

What can't be found is counted on each channel and listed under **More**.

## What becomes what

### From ErsatzTV

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

### From Tunarr

| Tunarr | SchedularrTV |
| --- | --- |
| A time-slot schedule | Blocks from each slot's time to the next (weekly ones on their day) |
| A random-slot schedule | The base rotation, weighted like the slots |
| A plain list of programmes | The base rotation: each show (season and episode order) and the films, as often as they came up |
| Custom shows | Shared collections |
| Filler lists | The channel's filler |
| Channel icons | The channel's logo |

## What isn't brought over

Anything not brought over is noted on the channel.

From ErsatzTV:

- sequential and scripted (YAML) schedules
- smart collections and searches (they're in ErsatzTV's own search syntax: make them again as collection queries)
- playlists
- music and other videos

From Tunarr:

- redirects to other channels
- smart collections

Also:

- Commercials and promos follow your usual settings.
- A channel number already in use here moves to the next free one.
