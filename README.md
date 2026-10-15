# Performance Logger

A SwiftlyS2 plugin for CS2 servers that tells you what is slowing your server down.

Made by **Denied** for the Novazombie.com ZM server (`zm.novazombie.com`), and shared for anyone
who wants to find out why their server frame time is high.

## What it measures

Every minute (configurable) it writes a short summary to the log:

- **Frames**: how long each server frame really took. At 64 tick a frame should take 15.6 ms;
  anything slower is a frame that ran late. You get the average, the worst one and when it
  happened, how many were slow, and how many of the slow ones had a garbage collection in them.
- **CPU** (Linux): how busy the game thread is, the whole process, steal time, and the clock of
  the core the game thread is on. A CS2 server simulates on one thread, so when that thread sits
  near 100% the server is CPU bound, however idle the other cores look.
- **GC**: .NET garbage collections, how long they paused the server, how much was allocated, and
  which types are allocated the most. Every collection stops the game thread for a moment.
- **Console**: how many lines were printed to the server console, grouped by pattern, so you can
  see what spams it (the game's damage log, engine warnings, a chatty plugin).
- **Plugin logs**: how many lines each plugin wrote to the SwiftlyS2 log. With the default logger
  every line is also drawn to the console on the game thread, which costs frame time.
- **Entities**: the entity count and how it changes, and every few minutes the most common entity
  classes. A count that keeps climbing usually means something never cleans up after itself.
- **Players**: players and bots, and the map.

When something stands out, a "Worth a look" line says so in plain words.

Example:

```
[PerfLogger] Frames (last 60 s, 64 tick, 15.6 ms each): 3840 frames, average 15.63 ms, worst 31.2 ms at 18:03:12. Slow (over 23.4 ms): 12, 3 of them over twice the budget, 5 during a GC. On time 3810 / up to 2x 25 / up to 4x 5 / worse 0.
[PerfLogger] CPU: game thread busy 72% (on core 11 at 3.79 GHz), whole process 95% of one core (12 cores), steal 0.0%.
[PerfLogger] GC: 9 collections (gen 0: 0, gen 1: 9, gen 2: 0), paused 38.2 ms in total, longest 6.1 ms. Allocated 52.0 MB, heap 410 MB.
[PerfLogger] Console: 312 lines. Most: 220x "…" [# # #] attacked "…" [# # #] with "…" | 41x SV: #/CCSPlayerPawn: ...
[PerfLogger] Plugin log lines: SomePlugin 45 (2 warnings), OtherPlugin 12.
[PerfLogger] Entities: 1840 (+12). Most: prop_physics 220, env_sprite 180.
[PerfLogger] Players: 24 (16 bots) on de_dust2.
```

## Commands

- `perflog`: shows the latest summary.
- `perflog report`: writes the last hour of summaries, plus the installed plugins, to a text file in
  the plugin's data folder.

The server console can always use them; in game they need the `perflogger.admin` permission
(configurable).

## Files

- `perf-YYYY-MM-DD.csv` in the plugin's data folder: one row per summary, easy to graph.
- `report-*.txt`: written by `perflog report`.

## Settings

`config.jsonc` is created on first start, under the `PerformanceLogger` section:

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Turns everything off without unloading. |
| `IntervalSeconds` | `60` | How often a summary is written. |
| `SlowFrameFactor` | `1.5` | A frame is slow at this many times the tick interval. |
| `WriteCsv` | `true` | Write the CSV file. |
| `TrackCpu` | `true` | CPU numbers (Linux only). |
| `TrackGc` | `true` | Garbage collections. |
| `TrackAllocations` | `true` | Most allocated types. |
| `TopAllocationTypes` | `6` | How many types to list. |
| `TrackConsole` | `true` | Console lines and patterns. |
| `TopConsolePatterns` | `6` | How many patterns to list. |
| `TrackPluginLogs` | `true` | Log lines per plugin. |
| `TrackEntities` | `true` | Entity count. |
| `EntityClassesEvery` | `5` | List entity classes every this many summaries. |
| `TopEntityClasses` | `8` | How many classes to list. |
| `AdminPermission` | `perflogger.admin` | Permission for the command in game. |

## Cost

It is meant to run all the time. On the game thread it takes a timestamp per frame, and once a
summary it counts players and takes the entity list (class names are read 128 entities per tick).
Everything else (reading /proc, the log file, grouping console lines, writing files and the log)
happens on a background thread. Its own log lines are written from a background thread too, so
they don't cost frame time.

## Reading the numbers

- **Average close to 15.6 ms, few slow frames**: the server keeps up; the slow ones are spikes.
  Check how many had a GC in them and what the console and plugin log lines say.
- **Game thread near 100%**: the server is CPU bound. Fewer bots, lighter plugins, or a faster
  CPU core (single-core speed is what counts).
- **High steal**: on a VPS, the host is giving your CPU time to others.
- **Lots of console lines**: turn down whatever prints them. Every line costs a little.

## Building

```
dotnet publish -c Release
```

The plugin ends up in `build/publish/PerformanceLogger`, and zipped as `build/PerformanceLogger.zip`.
Copy the `PerformanceLogger` folder into `addons/swiftlys2/plugins/`.

Needs SwiftlyS2 and .NET 10.

## License

MIT: free to use, change and share as you like. See [LICENSE](LICENSE).
