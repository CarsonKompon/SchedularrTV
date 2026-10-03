# Auto Chapters

Mid-roll breaks snap to a programme's chapter markers, which is where its original ad breaks were. Many
files have none, so breaks fall on a fixed interval instead, sometimes mid-sentence. The **Auto Chapters**
plugin finds those moments in the picture: broadcast programmes fade to black (and the sound drops out)
going into and out of every break. It records them as chapter markers for files that have none, and the
scheduler uses them like real ones.

It's optional: install `SchedularrTV-<version>-plugin-auto-chapters.zip` (or the all-plugins zip) from the
**Plugins** page and restart once. It needs ffmpeg, the same one as the rest of SchedularrTV.

## How it works

1. **Programmes about to air come first.** Each time the schedule is published, programmes airing in the
   next two days that have no chapter markers are queued, soonest first. **Analyse the whole library**
   on the **Auto chapters** page, or the plugin's **Analyse the whole library in the background** setting,
   queues every other episode and movie too.
2. **Each file is analysed once,** in the background at low priority. ffmpeg decodes the file at a small
   size and reports where the picture is black (`blackdetect`) and the sound is silent (`silencedetect`).
   A fade to black that's silent too becomes a marker, at its middle. A short episode takes a minute or
   two; a large library takes a long time, which is why analysing everything is off by default.
3. **The markers are used like real chapters.** Mid-roll breaks snap to them, in either mid-roll mode,
   for airings planned from then on. Files with chapter markers of their own keep them. If none are found
   (no fades to black), breaks still fall on the interval in **Auto** mode.
4. **Airings already planned are fixed up too.** A programme is usually planned (with its breaks on the
   fixed interval, every 13 minutes or so) before it's been analysed, and planned airings are kept. So once
   its fades are found, the next time the schedule goes out each of its breaks is slid onto the nearest fade
   within 5 minutes: the part before gets shorter and the part after longer by the same amount, so where the
   programme starts and ends (and everything around it) doesn't move. A programme already on air is left
   alone until it ends.

Results are kept in `plugin-data/auto-chapters/chapters.json` in the data folder, so nothing is analysed
twice. A file that's replaced (a different size or date) is analysed again.

Fades in the first and last 45 seconds are ignored: they're titles and credits, not breaks. Fades within 20
seconds of each other count as one: a fade out, a card and a fade back in.

## The Auto chapters page

It shows:

- **Mid-roll breaks coming up**: of the programmes with breaks in the next two days, how many are split at
  their fades, at their own chapters, still on the fixed interval waiting to be analysed, or on the interval
  because no fade was found (or none near enough). This is the quickest way to see it working.
- **Library analysed**: how much of the library (episodes and movies without their own chapters) has been
  analysed and kept, as a percentage.
- How many programmes have break points, how many have none, what's waiting and what's being analysed now,
  plus the latest results with the times found.

From there you can:

- **Analyse the whole library**: queue every episode and movie without chapters.
- **Stop waiting ones**: empty the queue (programmes coming up are queued again at the next publish).
- **Try failed ones again**: forget files that couldn't be read.
- **Start over**: forget everything found, after changing how fades are detected.

## Settings

| Setting | Default | |
| --- | --- | --- |
| Use the markers found for mid-roll breaks | On | Off analyses without changing where breaks go, to check results first. |
| Only count fades to black where the sound drops out too | On | Keeps dark scenes out. Turn off for programmes that keep music playing through their breaks. |
| Shortest fade to black | 0.4 s | Black shorter than this is ignored. |
| How dark counts as black | 0.1 | Raise it if fades only reach dark grey; lower it if dark scenes are mistaken for breaks. |
| Analyse the whole library in the background | Off | On queues everything without chapters, every six hours for anything new. |
| Programmes analysed at once | 1 | Each one decodes a whole video. Takes effect when the plugin is switched off and on. |
| ffmpeg | | Empty uses the one from App settings. |

## Files on another computer

The file has to be readable from the computer running SchedularrTV. When Jellyfin reports paths as it sees
them (another machine or a container), the plugin applies ErsatzTV's path mappings
(`ErsatzTv:PathMappings`) to find them. A file it still can't find is listed as couldn't be read.
