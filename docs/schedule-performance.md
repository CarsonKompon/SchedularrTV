# Schedule build profiling

Tools for measuring how long schedule builds take and for checking how ad breaks are filled. They run
against a copy of your configuration and library, never the live files.

## How builds are kept fast

- `ScheduleBuildSession` reuses raw results for identical queries within one channel build. Playback
  sequences, version choices and duration filtering keep their own behaviour; the cache doesn't persist
  across builds.
- `QueryEvaluator.Filter` prepares boolean predicates and equality sets once per search instead of
  interpreting the expression for every item.
- Channel builds run sequentially. Loading the Jellyfin and file system libraries is usually the largest
  startup cost.

### Selective rebuilds on save

Configuration-triggered rebuilds compare each channel with its last successful build inputs. Unchanged
channels keep their existing schedules and rotation state; they issue no media queries and aren't
replanned. New, changed, failed or expired channels are built. A repeated unchanged save or watcher
notification builds and publishes nothing.

Global collection changes affect only channels that reference those collections, including references
through other collections and block or interstitial selectors. Global settings and per-show defaults
invalidate every channel. Removed channels are dropped from the published lineup without rebuilding the
rest. Manual full rebuilds remain full rebuilds, and timer runs still extend every channel's horizon.

## Profiling a build

Run from the repository root with the .NET 10 SDK and your normal configuration available to the server:

```powershell
dotnet run --file tools/profile-schedule.cs -- capture
dotnet run --file tools/profile-schedule.cs -- build
dotnet run --file tools/profile-schedule.cs -- build "$env:TEMP/schedularrtv-profile" 120
dotnet run --file tools/profile-schedule.cs -- changes "$env:TEMP/schedularrtv-profile" 12
```

- `capture` copies the configuration, playback state and probe cache into a snapshot folder
  (`%TEMP%/schedularrtv-profile` by default) and records the library. Pass a new absolute folder after
  `capture` to take a fresh snapshot; existing snapshots aren't overwritten. Credentials are never written
  out, but the snapshot still contains your configuration and media paths, so don't commit or share it.
- `build` runs entirely against the captured snapshot and writes a state copy and per-channel
  `results.json` under a timestamped run folder. The optional hours argument changes only the build
  window.
- `changes` measures an initial build, a save that changes one channel, and a save with no changes.

Capture doesn't start hosted services, disables process management and removes publishers, so nothing
is published. Archive files aren't snapshotted, so avoid changing them between runs.

For CPU sampling, install `dotnet-trace` and launch the built profiler under
`dotnet-trace collect --profile dotnet-sampled-thread-time --show-child-io -- dotnet <profiler-dll> build <snapshot-folder> 12`
(the file-based build prints the DLL path), then inspect it with `dotnet-trace report <trace> topN -n 15`.

## Diagnosing ad breaks

`tools/diagnose-breaks.cs` reports, per channel, how each tier of each break was filled and how much
padding was left. It reads the live server on port 5252 and the captured snapshot, and never changes
anything:

```powershell
dotnet run --file tools/diagnose-breaks.cs                  # every channel
dotnet run --file tools/diagnose-breaks.cs -- ch1 ch2       # chosen channels
dotnet run --file tools/diagnose-breaks.cs -- --replay ch1  # rebuild in memory and compare
```

Common causes of padding:

- A block's `commercial_base_tags` narrowing the pool to only a handful of adverts.
- A filler tier whose brand and folder picks match no clips, so residuals too short for a whole advert
  can't be closed.
- Long videos (compilations, full programmes) filed among the adverts; `max_item_duration` on the
  commercials tier keeps them out.
