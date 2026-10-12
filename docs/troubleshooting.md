# Troubleshooting

Start with the channel's live preview, **App settings → ErsatzTV Next → Check guide**, and **`logs/server.log`**. A populated guide does not confirm that video can play.

## Diagnose common problems

| Symptom | Action |
| --- | --- |
| **Guide works; video fails** | Open the channel's play button, check ErsatzTV errors, and verify media paths from the ErsatzTV computer |
| **New channel missing** | Verify the lineup folder and restart unmanaged ErsatzTV after channel-list changes |
| **Guide empty in ErsatzTV** | Confirm ErsatzTV started with the scheduler's `lineup.json` |
| **Guide present in ErsatzTV but absent in Jellyfin** | Verify Jellyfin's XMLTV source and run **Dashboard → Scheduled Tasks → Refresh Guide** |
| **New media missing** | Select **TV Shows → Rescan** and check folder access and enabled libraries |
| **Duplicate shows** | Exclude duplicate libraries under **App settings → Jellyfin → Libraries** |
| **Black gaps between programmes** | Check commercial and filler queries, runtime limits, and folder tags |
| **Mid-rolls cut dialogue** | Use chapters or [Auto Chapters](auto-chapters.md), then review target cadence and tolerance |
| **Plugin tab missing** | Check plugin status, dependencies, and restart requirements on **Plugins** |
| **Generated promos remain commercials** | Check watched-only rendering, render lead time, voice setup, and queue errors |
| **Live plugin stream fails remotely** | Set the plugin's **Stream address** to a reachable scheduler URL |
| **Guide artwork missing remotely** | Set **`ErsatzTv:LocalArtworkBaseUrl`** or **`ArtworkBaseUrl`** to a player-reachable address |
| **API write returns 403** | Configure **`Scheduler:ApiKey`** and restart |
| **API write returns 401** | Send the matching **`X-Api-Key`** header |

## Reduce CPU usage

1. Open **Performance** while the affected channel plays.
2. Identify whether **SchedularrTV**, **ErsatzTV**, or **Helpers** consumes resources.
3. Select supported hardware encoding under **App settings → Hardware and video encoding**.
4. Lower live-page resolution or the plugin's capture rate.
5. Disable unnecessary **Play around the clock (24/7)** channels.

**ErsatzTV channels**, **Live page channels**, and **Rendered clips** use separate encoding settings. Overlays require ErsatzTV to re-encode even when passthrough is enabled.

## Restore a working schedule

1. Open **Backups**.
2. Select a known-good version and inspect the diff.
3. Select **Restore → Restore this version?**.
4. Test the channel's live preview.

Restoring saves the replaced schedule as another backup. It does not restore app settings, plugin files, or playback history.

## Gather diagnostics

- Capture the error from the affected **channel or plugin page**
- Record the application version, OS, and plugin versions
- Inspect **`logs/server.log`** around the failure time
- Run [**break diagnostics**](schedule-performance.md#diagnosing-ad-breaks) for unexplained padding
- Remove credentials and private media paths before sharing logs or configuration
