# Settings and maintenance

Use **App settings** for connections and executable paths, **Channel settings** for scheduling defaults, and **Backups** for schedule recovery.

## Connect media and playback

1. Open **App settings**.
2. Connect **Jellyfin** or add folders under **Local TV and movies**.
3. Select the **ErsatzTV Next** executable and enable **Start and stop ErsatzTV Next together with the scheduler**.
4. Verify **ffmpeg** and **ffprobe**.
5. Select **Save**.

**Run setup guide** reopens onboarding. The scheduler needs access to media files; ErsatzTV also needs readable paths for playback.

| Advanced setting | Use |
| --- | --- |
| **Jellyfin → Libraries** | Exclude duplicate DVR or cache libraries |
| **Allow other devices on the network** | Enable remote Web UI access; restart required |
| **Web UI port** | Change the listening port; restart required |
| **Hardware and video encoding** | Set separate defaults for ErsatzTV channels, live pages, and rendered clips |

The Web UI has no login; enable remote access only on a trusted network. See [App configuration](configuration.md) for manual settings and [ErsatzTV integration](ersatztv.md#path-mappings-and-guide-options) for remote paths and artwork URLs.

## Change global scheduling defaults

Open **Channel settings**, edit the required fields, and select **Save changes**.

| Control | Effect |
| --- | --- |
| **Time zone** | Default zone for channel scheduling |
| **Plan ahead** | Published schedule horizon |
| **Rebuild every** | Automatic extension interval |
| **Shuffle seed** | Reshuffle deterministic rotations when changed |
| **Seasonal tags** | Month, season, date, and holiday matching |
| Break and plugin sections | Defaults inherited by channels and blocks |

A longer planning horizon provides more guide coverage but adds build work. Channel and block overrides take precedence where supported.

## Inspect playback and resources

| Task | Action |
| --- | --- |
| Read the published schedule | Open **Dashboard → Guide** |
| Test actual video | Select the channel's play button on **Channels** |
| Inspect the playlist and listings | Select **App settings → ErsatzTV Next → Check guide** |
| Inspect CPU, memory, and active channels | Open **Performance** |
| Review viewing history | Open the channel's **Stats** tab |

A live preview counts as a viewer and can trigger watched-only rendering. **Performance** separates the scheduler, ErsatzTV, and helper processes; see [Troubleshooting](troubleshooting.md) for common failures.

## Refresh media or reset playback

| Control | Effect |
| --- | --- |
| **TV Shows → Rescan** | Refresh library content |
| **Channel settings → Rescan and rebuild** | Re-read local folders and replan playouts |
| Channel **General → Start this channel over** | Reset that channel's playback positions and rebuild |

Resetting playback is not required to discover new media. Routine rebuilds extend existing plans; saving changed programming replans affected channels while preserving the active grid slot.

## Save or discard schedule edits

**Save changes** validates and writes the schedule. **Discard** reloads the saved version, abandoning staged schedule changes.

**App settings** and plugin dialogs use their own **Save** buttons. Plugin-owned pages can also have separate save bars; use the save control on the page being edited.

## Restore a schedule version

1. Open **Backups**.
2. Select a version and inspect its changes.
3. Select **Restore**, then **Restore this version?**.
4. Review the guide and test playback.

Restoring replaces the schedule and backs up the version it replaces. Schedule backups do not restore app settings, media, plugin files, or viewing history; see [Data folder](installation.md#data-folder) for full backups.

## Edit YAML or automate tasks

1. Open **YAML**.
2. Select **Load current edits** or **Load from disk**.
3. Edit the document and select **Apply to editor**.
4. Resolve validation errors and select **Save changes**.

**Apply to editor** stages changes without saving. See [YAML reference](yaml.md) for fields and [REST API](rest-api.md) for scripted validation, previews, rebuilds, and restores.
