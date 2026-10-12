# Auto Chapters

**Auto Chapters** detects fades to black and optional silence in files without chapter markers. Detected boundaries improve mid-roll placement without modifying source files.

## Enable detection

1. Install **Auto Chapters** and restart.
2. Verify **ffmpeg** and access to the media files.
3. Enable mid-rolls on the channel's **Breaks** tab.
4. Open **Auto chapters** to inspect queued files and detected boundaries.
5. Review upcoming break coverage before increasing analysis scope.

Files airing within the next two days are queued first, with watched channels prioritized. Existing chapters take precedence over detected markers.

## Manage analysis

| Control | Effect |
| --- | --- |
| **Analyse the whole library** | Queue episodes and movies without chapters |
| **Stop waiting ones** | Clear queued work; upcoming programmes can return on the next publish |
| **Try failed ones again** | Retry unreadable files |
| **Start over** | Forget detected results for reanalysis |

Each file is decoded at reduced resolution. Results persist in **`plugin-data/auto-chapters/chapters.json`**; changed file size or modification time triggers reanalysis.

## Tune detection

| Setting | Default | Use |
| --- | --- | --- |
| **Use the markers found for mid-roll breaks** | On | Disable to inspect results without applying them |
| **Only count fades to black where the sound drops out too** | On | Disable for programmes retaining music across breaks |
| **Shortest fade to black** | 0.4 seconds | Ignore shorter black intervals |
| **How dark counts as black** | 0.1 | Raise for dark-gray fades; lower to reject dark scenes |
| **Analyse the whole library in the background** | Off | Extend analysis beyond upcoming content |
| **Programmes analysed at once** | 1 | Toggle the plugin off/on after changing |

Fades in the first and last **45 seconds** are ignored. Fades within **20 seconds** merge into one candidate.

## Understand schedule changes

New plans use detected markers like chapters. Already-planned breaks can move to the nearest fade within **five minutes**, preserving the programme's outer boundaries; an airing already in progress is left unchanged.

If no suitable fade exists, **Auto** mid-roll mode retains cadence-based breaks. The **Mid-roll breaks coming up** summary distinguishes detected fades, embedded chapters, pending analysis, and unmatched cadence breaks.

## Resolve unreadable files

- Verify the scheduler account can read the reported path
- Configure **`ErsatzTv:PathMappings`** for container or remote-library paths
- Select **Try failed ones again** after correcting access

See [Path mappings](ersatztv.md#path-mappings-and-guide-options).
