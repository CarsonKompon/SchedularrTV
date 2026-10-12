# Import channels

Import ErsatzTV or Tunarr schedules from a database copy. The source application can remain running.

## Run the import

1. Open the Web UI on the scheduler's computer.
2. Select **Channels → New channel → Import from ErsatzTV or Tunarr**.
3. Choose the source database or its data folder.
4. Review the detected channels and unmatched programmes.
5. Select the channels to import and complete the import.
6. Review imported programming and save any remaining unsaved changes.

The importer backs up the schedule before saving. If the editor already has unsaved changes, imported channels remain staged with those changes.

## Locate the database

| Source | File | Common locations |
| --- | --- | --- |
| **ErsatzTV legacy** | `ersatztv.sqlite3` | `%LOCALAPPDATA%\ersatztv`, `~/.local/share/ersatztv`, Docker mount for `/root/.local/share/ersatztv` |
| **Tunarr** | `db.db`, with `channel-lineups` beside it | `%APPDATA%\tunarr`, `~/.local/share/tunarr`, Docker mount for `/config/tunarr` |

The import reads legacy ErsatzTV databases; [ErsatzTV Next](ersatztv.md) is the playback engine for the resulting channels.

## Review the translation

| Source configuration | Imported result |
| --- | --- |
| ErsatzTV fixed-start items | Blocks ending at the next fixed start |
| ErsatzTV untimed items | Base rotation |
| ErsatzTV alternate schedules and block templates | Blocks with matching days and times |
| ErsatzTV collections and multi-collections | Shared collections |
| ErsatzTV fallback filler | Channel filler |
| Tunarr time slots | Timed blocks |
| Tunarr random slots | Weighted base rotation |
| Tunarr plain programme list | Show and movie rotation |
| Tunarr custom shows | Shared collections |
| Tunarr filler lists | Channel filler |
| Channel icons | Channel logos |

Matching uses media IDs, file paths, then show/season/episode or movie title/year. A channel number already in use moves to the next free number.

## Resolve unsupported items

- Recreate **ErsatzTV sequential or scripted schedules** manually
- Rebuild **smart collections and searches** with scheduler queries
- Recreate **ErsatzTV playlists, music, and other-video programming** with supported sources
- Replace **Tunarr redirects** with explicit programming
- Review **More** for unmatched media and import notes
- Configure **commercials and promos** in the imported channel
