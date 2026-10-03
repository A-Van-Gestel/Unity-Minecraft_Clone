<objective>
Profiler analysis from the shell. All queries live in one repo script,
`Tools/UnityCli/Profiler/ProfilerQueries.cs`, compiled in memory by `run_script` on each call (no
domain reload, never imported by Unity). Works on frames recorded live in play mode **or** loaded
from a saved `.data` capture.
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
| `FrameTopTime`      | `[frame, top, targetFrameMs, threadIndex]`                   | Samples by total time, with self ms, calls, GC and path                 |
| `FrameSelfTime`     | `[frame, top, threadIndex]`                                  | Self time per sample name across the frame                              |

</calling>

<threads>

**Thread indices are per-frame.** Only index `0` (main thread) is stable. Range queries therefore
take a thread **name** exactly as `Threads` prints it (`"Main Thread"`, `"Job / Worker 0"`), and
single-frame queries take the index from `Threads` **for that frame**.

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
- `RawFrameDataView.GetSampleMetadataAsLong(i, 4)` is not GC bytes; use `columnGcMemory`.
- **`OverallGc` attributes allocations only as far as the nearest profiler sample.** It credits each `GC.Alloc`
  to its parent sample and never reads the managed call stacks the Profiler records with **Call Stacks** on.
  Without deep profiling, that parent is a coarse marker. Per-method attribution needs a new entry built on
  `HierarchyFrameDataView.GetItemMergedSampleCallstack` / `ResolveItemCallstack` (or
  `RawFrameDataView.GetSampleCallstack` + `ResolveMethodInfo`), which exist in 6000.6.

</pitfalls>
