<objective>
Profiler analysis from the shell. All queries live in one repo script,
`Tools/UnityCli/Profiler/ProfilerQueries.cs`, compiled in memory by `run_script` on each call (no
domain reload, never imported by Unity). Works on frames recorded live in play mode **or** loaded
from a saved `.data` capture. Recording control (arm, auto-stop, save) lives in a second script,
`Tools/UnityCli/Profiler/ProfilerCapture.cs`, because it changes the Profiler's state.
</objective>

<calling>

```bash
unity command run_script --file Tools/UnityCli/Profiler/ProfilerQueries.cs \
    --entry UnityCli.ProfilerQueries.<Entry> --args '<json array>' --result-only
```

**Every argument is required** (no C# defaults). Frame `-1` means first/last available. Loading a
large capture can take longer than 30 s: add `--timeout_ms 240000 --timeout 260`.

| Entry               | `--args`                                                     | Returns                                                                 |
|---------------------|--------------------------------------------------------------|-------------------------------------------------------------------------|
| `Status`            | `[]`                                                         | Frame range, recording flag, last frame's CPU ms                        |
| `Load`              | `["ProfilerCaptures/<name>.data"]`                           | Loads a capture (replaces current frames)                               |
| `Clear`             | `[]`                                                         | Drops all frames — leave the Profiler as you found it                   |
| `Threads`           | `[frame]`                                                    | Thread index → name map for that frame                                  |
| `FrameRangeSummary` | `[first, last, targetFrameMs, top, "threadName"]`            | Frame-time min/avg/p95/max, over-budget count, worst frames, top avg self-time |
| `OverallGc`         | `[first, last, top, "threadName"]`                           | Managed allocation per allocating sample (`GC.Alloc` credited to its parent) |
| `GcCallstacks`      | `[first, last, top, stackDepth, "threadFilter", "windowMarker", "tsvPath"]` | Managed allocation per resolved call site and stack, every matching thread; header with the `no callstack` share and GC.Alloc + other = thread-total check |
| `FrameTopTime`      | `[frame, top, targetFrameMs, threadIndex]`                   | Samples by total time, with self ms, calls, GC and path                 |
| `FrameSelfTime`     | `[frame, top, threadIndex]`                                  | Self time per sample name across the frame                              |

`ProfilerCapture.cs` (`--entry UnityCli.ProfilerCapture.<Entry>`):

| Entry         | `--args`                                                  | Does                                                                                   |
|---------------|-----------------------------------------------------------|----------------------------------------------------------------------------------------|
| `ConnectToPlayer` | `["WindowsPlayer"]`                                   | Points the Profiler at the first discovered connection matching the text (`connected …` / `not found …`) |
| `ArmAutoStop` | `["marker", graceFrames, "ProfilerCaptures/<name>.data"]` | `Arm`, then an `EditorApplication.update` hook stops recording and saves `graceFrames` after the marker's last frame |
| `Poll`        | `["", 0, ""]`                                             | Progress (`waiting …` / `SAVED …`); **reinstalls the hook** when a domain reload dropped it |
| `Disarm`      | `[]`                                                      | Abandons an armed capture: removes the hook, stops recording, restores the call-stack mode |
| `Arm`         | `[]`                                                      | Clears frames, sets GC.Alloc call stacks (`ProfilerDriver.memoryRecordMode`), starts recording — no auto-stop |

**Capturing one benchmark phase** (in a built player, the end-to-end recipe — build, launch, connect, read the
report — is the `perf-benchmark` skill's `references/player-capture.md`):

1. Target: Play mode is `ProfilerDriver.connectedProfiler = -1`. A Development player (no autoconnect) needs
   `ConnectToPlayer ["WindowsPlayer"]` once it is running, repeated until `connected`; it must be a real
   Development build.
2. `ArmAutoStop` with the phase marker (e.g. `Benchmark.Generation.200mps`) — before or after entering Play mode.
3. Start the run **muted** — a player with `-mc-mute` (`perf-benchmark` "Unattended runs"), Play mode with
   `unity command eval "Launch.LaunchSession.MuteForSession(); return 0;"` once playing — and keep a background shell loop polling (`until … grep -q SAVED; do sleep 5; done`). The poll
   does not stop the capture — the hook does, within a few frames, because a player rendering hundreds of fps
   after the phase overruns the 2000-frame buffer between two 5 s polls. Entering Play mode reloads the domain and
   drops the hook; the next `Poll` reinstalls it, so the loop must be running.
4. The save restores the call-stack mode in force before the first arm (recording stays off); `Disarm` does the
   same for a run you abandon.

Each poll compiles and runs on the Editor's main thread, so in a **Play-mode** capture its allocations land
in the recorded frames (Roslyn `Binder.*` / `UnityPipeline` stacks); a connected player is unaffected.

</calling>

<threads>

**Thread indices are per-frame.** Only index `0` (main thread) is stable. Range queries therefore
take a thread **name** exactly as `Threads` prints it (`"Main Thread"`, `"Job / Worker 0"`), and
single-frame queries take the index from `Threads` **for that frame**.

**Names are not unique.** .NET ThreadPool threads all record as `Scripting Threads / #0` (29 of them in an
IL2CPP capture), and a by-name query reads only the first. `GcCallstacks` therefore matches a label
**substring** on every index (`"*"` = all threads); use it for anything off the main thread. In
Editor Play mode the same threads record as `Scripting Threads / Thread Pool Worker`.

</threads>

<workflow>

1. `Status` — frames present? If not, record in play mode (confirm with the user first) or
   `Load` a capture from `ProfilerCaptures/`.
2. `FrameRangeSummary` over the whole range — sustained cost vs. spikes; note the worst frames.
3. `OverallGc` — who allocates, in how many frames.
4. `FrameTopTime` / `FrameSelfTime` on a worst frame — where the time goes.
5. Job threads: `Threads` on that frame, then `FrameSelfTime` with the worker's index.
6. `Clear` if you loaded a capture.

**Bottom-up and cross-thread questions** have no entry. Write a short `eval` against the same API
(`UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView`, `HierarchyFrameDataView` with
`columnSelfTime` / `columnGcMemory`, `GetItemChildren`, `GetItemPath`), or add an entry point to
`ProfilerQueries.cs`. Always `Dispose()` the view (use `using`).

</workflow>

<pitfalls>

- **`eval` formats numbers with the Editor's locale** (a decimal comma here). Format with
  `CultureInfo.InvariantCulture` when numbers are compared or parsed.
- **Frame 0 is startup**; skip it for steady-state analysis.
- `HierarchyFrameDataView.GetItemCallsCount()` does not exist; read `columnCalls`.
- `RawFrameDataView.GetSampleMetadataAsLong(i, 4)` is not GC bytes. A `GC.Alloc` sample carries one metadata
  value, index `0`, its byte count (sums exactly to the hierarchy's `columnGcMemory`).
- **`HierarchyFrameDataView.GetItemMergedSampleCallstack` returns empty stacks for almost every sample** (99.9 % of
  bytes in a 6000.6 Play-mode capture) even when they were recorded; `RawFrameDataView.GetSampleCallstack` returns
  them. `GcCallstacks` reads raw samples for that reason.
- **A recorded stack starts with ~9 native frames** (`StackWalker`, `profiling::gc_allocation`,
  `mono_gc_alloc_vector`) that `ResolveMethodInfo` leaves unnamed; managed frames resolve as
  `Assembly.dll!Namespace::Class.Method()` (`::Class` for the global namespace). `GcCallstacks` skips the
  unnamed frames and charges a byte to the first `Assembly-CSharp*` frame.
- **Some samples carry GC bytes with no `GC.Alloc` child** (`Application.Preload Assets`,
  `Application.LoadLevelAsync Integrate` on a scene load). `GcCallstacks` lists them under "outside GC.Alloc".
- **`OverallGc` attributes allocations only as far as the nearest profiler sample.** It credits each `GC.Alloc`
  to its parent sample and never reads the managed call stacks the Profiler records with **Call Stacks** on.
  Without deep profiling, that parent is a coarse marker, and ThreadPool allocations have **no** parent at all.
  For per-method attribution use `GcCallstacks`. It needs a capture recorded with GC.Alloc call stacks on
  (`ProfilerCapture.Arm`, or the toolbar's **Call Stacks → GC.Alloc**); without them every byte lands in
  `<no callstack> under <parent>`, so check the header's `no callstack` share before reading the ranking.
- **An ad-hoc Play-mode capture needs no marker:** `Arm`, let it run a few seconds (or drive the case between
  frames), then `Disarm`. Recording stops but the frames stay, so `GcCallstacks` over `[-1,-1,…]` reads them;
  pass a `tsvPath` to grep the full stack table rather than the capped ranking. Expect one
  `<no callstack> under <sample>` row per capture holding exactly one frame's allocations of that sample:
  the arming frame, recorded before call stacks switched on. It is not an unattributed allocator.
- **Selecting a benchmark phase:** the generation pass records one sample per frame named
  `Benchmark.Generation.<speed>mps` (e.g. `Benchmark.Generation.200mps`); pass it as `GcCallstacks`'s
  `windowMarker`. The Profiler keeps at most 2000 frames (Preferences → Analysis → Profiler → Frame Count), and
  the benchmark runs on for minutes after the phase, so a recording stopped by hand at the end of the run holds
  only the results screen. Use `ProfilerCapture.ArmAutoStop`, whose hook stops shortly after the phase.

</pitfalls>
