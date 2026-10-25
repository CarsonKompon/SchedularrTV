# Use the Web UI

Open **http://localhost:5252** to configure channels and inspect playback. Choose a task below for its steps and advanced controls.

## Build your lineup

| Task | Guide | Advanced controls |
| --- | --- | --- |
| Create or copy a channel | [Channels](web-ui-channels.md) | Grid alignment, episode stacking, encoding, continuous playback |
| Choose and order media | [Collections and media selection](web-ui-collections.md) | Title rotation, filters, versions, specials, intros/outros |
| Set programming times | [Blocks and anchors](web-ui-scheduling.md) | Overnight windows, date-based marathons, priorities, block overrides |
| Fill breaks and announce shows | [Breaks and promos](web-ui-breaks.md) | Mid-roll limits, seasonal matching, filler, generated promos |
| Connect, monitor, and recover | [Settings and maintenance](web-ui-maintenance.md) | Global defaults, rescanning, rotation resets, backups, YAML |

For first-time setup, start with [Installation](installation.md). Existing lineups can use [Import channels](import.md); folder naming and metadata are covered in [Local media](local-library.md).

## Extend your channels

| Goal | Starting point |
| --- | --- |
| Favor shows people watch | Open **Stats** and enable [viewer-aware scheduling](stats.md#enable-viewer-aware-scheduling) |
| Replay historical schedules | Enable [Archive Follow](archive-follow.md) and configure the channel's **Follow** tab |
| Generate announcements | Open **Generated Promos** after installing [Generated promos](generated-promos.md) |
| Add logos or captions | Open **Overlays** after installing [Overlays](overlays.md) |
| Write scripts with a local model | Configure [Local AI writer](local-ai.md) and enable it in supported text or voice editors |
| Add next-show panels to credits | Enable [Credit squeeze](credit-squeeze.md) on the channel |
| Detect natural break points | Enable [Auto Chapters](auto-chapters.md) for files without chapters |
| Schedule live virtual shows | Create [radio](radio.md), [news](news.md), [web](web-channels.md), or [guide](tv-guide.md) shows, then select them as programming |
| Carry external channels | Configure [IPTV](iptv.md) or [streamers](streamers.md) on their plugin pages |
| Add music under live pages or promos | Choose a source in the feature's [music picker](background-music.md) |

Install optional features through [Plugins](plugins.md). Plugin pages and channel tabs appear while their plugins and dependencies are enabled.

## Save and inspect changes

**Save changes** validates and writes schedule edits; **Discard** reloads the saved schedule. **App settings** has its own **Save changes** bar; plugin dialogs have a **Save** button.

After saving, inspect **Dashboard → Guide** and test the channel's play button on **Channels**. See [Settings and maintenance](web-ui-maintenance.md) for recovery and [Troubleshooting](troubleshooting.md) for failures.

## Use advanced tools

| Task | Reference |
| --- | --- |
| Edit schedule structure | [YAML](yaml.md) |
| Understand scheduling precedence and queries | [Scheduling and queries](scheduling.md) |
| Configure remote playback and path mappings | [ErsatzTV integration](ersatztv.md) |
| Automate validation, previews, and rebuilds | [REST API](rest-api.md) |
| Diagnose build times and padding | [Build profiling](schedule-performance.md) |
