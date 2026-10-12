# Build profiling

Profile schedule builds against a captured configuration and library. These tools require the **.NET 10 SDK** and a source checkout.

## Capture and profile

Run **`tools/profile-schedule.cs`** from the repository root:

```powershell
dotnet run --file tools/profile-schedule.cs -- capture
dotnet run --file tools/profile-schedule.cs -- build
dotnet run --file tools/profile-schedule.cs -- build "$env:TEMP/schedularrtv-profile" 120
dotnet run --file tools/profile-schedule.cs -- changes "$env:TEMP/schedularrtv-profile" 12
```

| Command | Result |
| --- | --- |
| **`capture [absolute-folder]`** | Snapshot of configuration, playback state, probe cache, and library; existing snapshots retained |
| **`build [folder] [hours]`** | Build from snapshot with per-channel `results.json` in a timestamped run folder |
| **`changes [folder] [hours]`** | Compare initial build, one-channel change, and unchanged save |

The default snapshot folder is the system temporary directory's **`schedularrtv-profile`**. Capture disables publication and process management; archive files remain external and should stay unchanged between comparisons.

Snapshots omit credentials but include configuration and media paths.

## Sample CPU usage

1. Install **dotnet-trace**.
2. Locate the profiler DLL printed by the file-based build.
3. Collect a sampled trace.
4. Inspect the most expensive methods.

```sh
dotnet-trace collect --profile dotnet-sampled-thread-time --show-child-io -- dotnet <profiler-dll> build <snapshot-folder> 12
dotnet-trace report <trace> topN -n 15
```

## Diagnosing ad breaks

**`tools/diagnose-breaks.cs`** reads the live server on port **5252** and the captured snapshot. It reports tier usage and residual padding without changing the live schedule.

```powershell
dotnet run --file tools/diagnose-breaks.cs
dotnet run --file tools/diagnose-breaks.cs -- ch1 ch2
dotnet run --file tools/diagnose-breaks.cs -- --replay ch1
```

| Finding | Adjustment |
| --- | --- |
| **Small commercial pool** | Broaden block brands or `commercial_base_tags` |
| **No short filler matches** | Correct filler brand and folder selections |
| **Long compilations among ads** | Set `interstitials.commercials.max_item_duration` |
| **Unexpectedly large grid gap** | Review grid interval, stacking, and break cap |

## Understand selective rebuilds

Configuration saves compare channels against their last successful build inputs.

| Change | Rebuilt channels |
| --- | --- |
| **Channel settings** | Changed channel |
| **Global collection** | Channels referencing it, including nested references |
| **Global settings or per-show defaults** | All channels |
| **Channel removal** | Lineup updated; remaining unchanged channels retained |
| **Unchanged save** | No build or publication |

New, failed, or expired channels rebuild as needed. Timer runs extend all channel horizons; manual full rebuilds remain full rebuilds.

Query results are reused within a channel build, while rotation and filtering retain their own state. Use **Performance** for live encoding load rather than schedule-build timing.
