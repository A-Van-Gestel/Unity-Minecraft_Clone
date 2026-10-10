# Performance Monitor & Logger Overhaul Design

**Version:** 1.16  
**Date:** 2026-10-06  
**Status:** In progress — PM-0 ✅ complete (2026-10-03, Master answers in §8); PM-1 ✅ complete (2026-10-03, §7.2;
confirmed in an IL2CPP Master build); PM-2 ✅ complete (2026-10-04, §7.3; confirmed in an IL2CPP Master build); PM-3 ✅
complete (2026-10-04, §7.4; confirmed in an IL2CPP Master build); PM-4 ✅ complete (2026-10-05, §7.5; confirmed in an
IL2CPP Master build); PM-8 ✅ complete (2026-10-05, §7.6; confirmed in an IL2CPP Master build); PM-6 ✅ complete
(2026-10-06, §7.7; confirmed in an IL2CPP Master build); PM-5 and PM-7 not started.
PM-0's answers reshaped PM-2/PM-4 (§8).  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> An opt-in, settings-driven in-game performance monitor and diagnostic logger that covers **every
> engine system**, sees **individual frames** (not moving averages), attributes each spike to the
> system that caused it, and exports sessions and hitch snapshots to disk — at near-zero cost when off.
> **The pivotal decision: one engine-wide, zero-allocation, enum-indexed timing/counter store
> (`PerfStore`) fed by `Stopwatch` probes that work in Master IL2CPP, with four tiers selected in
> Settings.** The existing always-on `PerformanceMonitor` ring buffer stays exactly as it is (a
> standing decision); everything new layers on top of it and is opt-in. `WorldFrameProfiler` becomes a
> facade over the store so existing benchmark captures stay comparable, and a category logger with
> per-category levels replaces today's three ad-hoc diagnostic flags and untagged `Debug.Log` calls.

**Audited:** 2026-10-02, at commit `376778fe` (branch `main`).
Read in code: `PerformanceMonitor.cs`, `DebugScreen.cs`, `Helpers/UI/GraphRenderer.cs`,
`Benchmarks/WorldFrameProfiler.cs`, `Benchmarks/PipelineTelemetry.cs`, `Benchmarks/BenchmarkController.cs`,
`Benchmarks/BenchmarkMetricsCollector.cs`, `Benchmarks/BenchmarkEnvironment.cs`, `Helpers/PipelinePassBudget.cs`,
`SettingsManager.cs` (diagnostic flags, `OnSettingChanged`, benchmark whitelist),
`UI/Attributes/SettingFieldAttribute.cs`,
`UI/SettingsUIGenerator.cs`, the `World.Update` frame loop, and a repo-wide `Debug.Log*` census.
`ProjectSettings.asset` stack-trace and frame-timing settings were read in the live Editor via a
read-only `unity command eval`. Unity 6000.6 APIs (`ProfilerRecorder`, `ProfilerRecorderHandle`,
`ProfilerMarker`, `FrameTimingManager`/`FrameTiming`, `GarbageCollector`) were checked with the
`unity-api` MCP.

**Relationship to other documents:**

- [`../Architecture/PERFORMANCE_PROFILER_OVERHAUL.md`](../Architecture/PERFORMANCE_PROFILER_OVERHAUL.md) —
  the shipped `PerformanceMonitor` (v2.1). This design keeps it and builds a second layer on it; its
  §1 finding that `ProfilerRecorder` returned invalid data in Release builds is why PM-0 *probes*
  recorder validity rather than assuming it.
- [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](ENGINE_SCALING_PERFORMANCE_ROADMAP.md) — its `ES-0`
  ("measure first") is delivered by PM-1…PM-3 + PM-6; every `ES-*` verdict is scored on this store.
- [`FLIGHT_PROFILE_CAPTURE.md`](FLIGHT_PROFILE_CAPTURE.md) /
  [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) — the
  `PipelineTelemetry` chunk traces and the `WorldFrameProfiler` slots those captures depend on; their
  numbers must stay comparable across PM-1.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — `DT-4` (HUD allocation
  leftovers) ran ahead of PM-5 as its own item and closed 2026-10-07 (its archived entry in
  `Archived/PERFORMANCE_IMPROVEMENTS_COMPLETED.md`).
- [`../Architecture/DATA_DRIVEN_SETTINGS_UI.md`](../Architecture/DATA_DRIVEN_SETTINGS_UI.md) — the
  `[SettingField]` reflection UI that hosts the tier and log-level controls.

---

## 1. Goals & non-goals

### Goals

1. **See every spike.** Per-frame raw wall/CPU time, worst frame, p99/p99.9, and a hitch detector that
   freezes the frames around a spike — none of which any instrument records today.
2. **Attribute it.** Every system has a timing slot (main thread) or counter (workers, I/O), and an
   explicit **unattributed remainder** makes missing coverage visible instead of silent.
3. **Opt-in, near-free when off.** Four tiers chosen in Settings, applied live. Off costs one static
   read per probe and allocates nothing; the always-on base layer is today's `PerformanceMonitor`.
4. **Works in shipping builds.** Master IL2CPP is the production configuration, so the core must not
   depend on Profiler-only APIs (`ProfilerMarker`, deep profiling).
5. **GC and GPU become measurable.** GC allocation that survives a collection frame, a per-frame
   collection flag, and GPU/render-thread time via `FrameTimingManager`.
6. **One logging system.** Categories × levels in Settings, messages built only when enabled,
   rate-limited, consistently tagged, optionally mirrored to the session file with frame numbers.
7. **Exportable.** Session CSV and hitch dumps under `persistentDataPath`, written off the main thread;
   benchmark reports read the same store, so a capture and a play session speak one format.

### Non-goals (v1)

- Replacing the Unity Profiler or the Memory Profiler package — they stay the deep-dive tools; this is
  the always-available field instrument.
- Uploading telemetry anywhere — all output stays on the local disk.
- Per-voxel or per-BFS-node tracing — counters only at job granularity.
- Job *timeline* visualization (per-worker swimlanes) — a **v2** extension (§7).

---

## 2. Current state

| Area                              | State                                                                                                                                                                                                                                                                                                                                                                                      | Anchor                                      |
|-----------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------|
| `PerformanceMonitor`              | Always on by design: `Stopwatch` dual-hook phase times, wall time, GC alloc from `GC.GetTotalMemory` deltas, native/managed memory; history ring of 200 snapshots (10 s × 20 Hz)                                                                                                                                                                                                           | `PerformanceMonitor.cs:26–377`              |
| Smoothing                         | **Every recorded CPU/wall value is a 30-frame mean**; snapshots copy smoothed values at 20 Hz, so no individual frame is ever stored; the timer reset drops remainders, so real sampling falls below 20 Hz at low FPS                                                                                                                                                                      | `:30, :109–136, :327–339`                   |
| GC allocation                     | 60-frame mean of `max(0, Δheap)` — **a collection frame records 0** and its allocation is lost; a 1 MB burst reads ≈17 KB; worker-thread allocation is charged to the main frame                                                                                                                                                                                                           | `:31, :314–321`                             |
| GC collections                    | Session counts on the HUD only; Boehm reports `MaxGeneration = 0`; no per-frame flag, no duration, not in benchmark reports                                                                                                                                                                                                                                                                | `DebugScreen.cs:605–623`                    |
| Benchmark "Peak/Min"              | Extremes of the smoothed 20 Hz snapshots — not frame peaks; no p99, worst-frame or hitch count                                                                                                                                                                                                                                                                                             | `BenchmarkMetricsCollector.cs:254–283`      |
| `WorldFrameProfiler`              | 10 `Stopwatch` slots inside `World.Update`; last frame only; enabled only by benchmark harnesses; no HUD                                                                                                                                                                                                                                                                                   | `Benchmarks/WorldFrameProfiler.cs:43–244`   |
| Untimed regions in `World.Update` | `AdvanceWorldTime`, `BiomeTracker.Tick`, `ShiftOrigin`, `UpdateTeleportHold`, both `CheckViewDistance` sites (incl. `UnloadChunks`), border toggle, `HandleVisualization`, `DrainGenerationRequests`, `DrainFailedSaveRetries`, `ChunkPool.Update`, the CP-1 scan — and no unattributed remainder                                                                                          | `World.cs:2551–2619, 2637, 2950–2964`       |
| Systems with no timing at all     | Physics (`VoxelRigidbody`, only buried in FixedUpdate), audio directors, clouds, sky/day-night, UI/HUD (incl. `DebugScreen` itself), `VoxelVisualizer`, disk I/O latency/bytes/compression, ThreadPool depth, job worker time, GPU/render thread                                                                                                                                           | grep                                        |
| Pools                             | `ChunkPoolManager` counts on the HUD; **`ChunkJobArrayPool` and `MeshOutputPool` expose no counters**                                                                                                                                                                                                                                                                                      | —                                           |
| Profiler APIs                     | No `ProfilerRecorder`/`FrameTimingManager` use; 3 `ProfilerMarker`s (Profiler-only); **`enableFrameTimingStats: 0`** in `ProjectSettings.asset:162` and live                                                                                                                                                                                                                               | —                                           |
| Instrumentation variant drift     | CP-1/LP-1/MP-1 probes are gated on `UNITY_INCLUDE_INSTRUMENTATION`, but comments say "dev/editor builds only"; the `Windows - Development` profile uses the default Release variant, so the probes are **compiled out of Development builds** while their "(dev)" HUD rows read 0 and the save-diagnostics toggle is shown but inert. Only `Windows - Profiler` (Checked variant) has them | `World.cs:1185, 1190, 3471`; build profiles |
| Logging                           | 327 runtime `Debug.Log*` sites (150 Log / 74 Warning / 103 Error); no central logger or `logMessageReceived` subscriber; tags mixed (system, method, class, backlog-ID, untagged); only 3 sites honor `enableDiagnosticLogs`, ~10 `enableWaterDiagnosticLogs`, save diagnostics double-gated (flag + `UNITY_INCLUDE_INSTRUMENTATION`)                                                      | census                                      |
| Stack traces                      | Player settings: **Error = None, Assert/Warning/Log/Exception = ScriptOnly** — every `Log`/`Warning` in a player walks the managed stack (expensive in IL2CPP), while errors carry none (the reverse of the useful choice)                                                                                                                                                                 | `ProjectSettings.asset:57` (live)           |
| Settings hosting                  | `[SettingField(tab)]` + `DebugOnly`, `SubHeader`, `DisabledWhen`; live apply via `SettingsManager.OnSettingChanged`; the **DebugScreen** tab is player-facing; benchmark cold-launch only copies a whitelist (`OverlayBenchmarkSettingsFromDisk`, removed 2026-10-10)                                                                                                                      | `SettingsManager.cs:1003–1039, 1279–1305`   |

---

## 3. Decisions

### 3.1 Timing source

#### Option A — `ProfilerRecorder` / `ProfilerMarker` everywhere (rejected as the core)

- ✅ Unity-native; markers appear in the Unity Profiler; recorders give built-in counters (draw calls,
  memory) for free.
- ❌ **The shipped v2.1 monitor already replaced recorders because they returned invalid data in
  Release builds**, and `ProfilerMarker` samples are only readable through the Profiler. The production
  configuration is Master IL2CPP; a core that goes dark there is no field instrument.
- *PM-0 (2026-10-03):* in a 6.6 Master player all 49 available counters are valid, so the "invalid in
  Release" finding no longer holds for counters. The rejection stands: there are no marker timings and no
  `GC Allocated In Frame` in Master, so recorders cannot be the core. They are the enrichment source §8 q1 lists.

#### Option B — `Stopwatch.GetTimestamp` probes into an enum-indexed store ✅ **CHOSEN**

The pattern `WorldFrameProfiler` already proves in Master builds: begin/end timestamps into
pre-allocated per-slot accumulators. Recorders are used **only as optional enrichment** for counters
PM-0 proves valid in the target build (memory, render stats), and a probe opened with its slot
(`Begin(PerfSlot)`) *also* emits a `ProfilerMarker` under `ENABLE_PROFILER`, so Unity Profiler captures keep their
names. *PM-1 (2026-10-03):* the slot-less `Begin()` this section originally specified cannot emit one —
`ProfilerMarker` has only `Begin()`/`End()`, so a marker must be opened by name before the region runs. The slot-less
pair survives only as the `WorldFrameProfiler` facade's path (no marker); `World.cs`'s ten facade probes move to the
slotted API when PM-3 re-touches those lines. *PM-3 (2026-10-04):* done; the facade's `Begin`/`Add` stay, used by the
Pipeline Backpressure suite.

### 3.2 Opt-in model

#### Option A — One on/off toggle (rejected)

- ❌ The cost and data volume of "frame stats" and "full session capture" differ by orders of magnitude;
  one switch forces either too little or too much.

#### Option B — Four tiers, one setting ✅ **CHOSEN**

| Tier                    | Adds                                                                                                                         | Intended use                                             |
|-------------------------|------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------|
| **0 — Basic** (default) | Today's always-on `PerformanceMonitor` **unchanged**, plus a raw per-frame wall/CPU ring (8 B/frame) feeding worst-frame/p99 | Every session; costs what today costs                    |
| **1 — Frame**           | GC allocation per frame + collection flag, `FrameTimingManager` GPU/render-thread/present-wait, hitch detector + snapshots   | "Why did that stutter?"                                  |
| **2 — Systems**         | Every timing slot and counter (§4.2), unattributed remainder, job/I-O counters                                               | Attribution sessions, benchmark runs                     |
| **3 — Capture**         | Session file streaming + hitch dumps to disk, log mirroring with frame numbers                                               | Recorded investigations; benchmark mode forces this tier |

The tier is a `Settings` enum on the **DebugScreen** tab, applied live through `OnSettingChanged`.
Benchmark harnesses set it explicitly (as they already force `WorldFrameProfiler.Enabled`) and the
field is added to `OverlayBenchmarkSettingsFromDisk`, so captures stay comparable on cold and menu
launches alike. *2026-10-10:* the whitelist is removed — automated runs read the whole settings file, and every report
lists the values that differ from the defaults (`SettingsDifferenceReport`). Logging levels (§4.6) are independent of the tier. *PM-1 (2026-10-03):* `Settings.perfMonitorTier`
("Monitor Detail") ships all four values, contiguous from 0, because the Settings dropdown maps option index to enum
value and the JSON stores the integer — a tier inserted later would shift saved values. Until PM-2/PM-6 land, Frame
records the same as Basic and Capture the same as Systems; the tooltip says so. `/perf tier` sets it from the console.
*PM-2 (2026-10-04):* per-frame GC allocation and collections record at **every** tier, Basic included — the heap read
already ran every frame, so Basic adds one `GC.CollectionCount` call; Frame adds the frame timings and the hitch
detector. A Basic row is 40 B (12 B at PM-1; the table's "8 B" never matched the code).
*PM-6 (2026-10-06):* Capture now writes the session files (§4.7). Benchmark mode does **not** force Capture: it raises
the tier to **at least Frame** through `PerfStore.TierFloor`, which the report's hitch and GPU columns need, and keeps a
higher tier set in the settings file or by `-mc-set`. Forcing Capture would have written ≈ 0.4 MB/s through every
loading pass and removed the per-run tier choice the PM-3/PM-4 overhead A/Bs relied on; a Basic benchmark arm is no
longer possible. The floor never touches the `Settings` object, so a menu-launched run cannot save it into the file.

### 3.3 Relationship to the existing instruments

#### Option A — Replace `PerformanceMonitor` and `WorldFrameProfiler` (rejected)

- ❌ `PerformanceMonitor`'s always-on history is a recorded standing decision, and every FP/P-9/P-4 capture
  is defined on `WorldFrameProfiler`'s slot semantics — replacing either breaks the time series.

#### Option B — Layer and facade ✅ **CHOSEN**

`PerformanceMonitor` keeps its role and API (and its consumers `DebugScreen`/`BenchmarkMetricsCollector`);
`PerfStore` reads its per-frame raw sample instead of re-timing the phases. `WorldFrameProfiler`'s
`Phase` enum maps 1:1 onto store slots and its API stays as a thin facade, so `PipelineTelemetry.PassMsTotals`
and the benchmark report produce identical numbers (pinned by a baseline before/after PM-1).

---

## 4. Architecture

### 4.1 Data model

```csharp
/// <summary>Every timed region in the engine; values index fixed per-slot arrays.</summary>
public enum PerfSlot : byte
{
    // World.Update (existing WorldFrameProfiler phases map onto these first, same order — shipped in PM-1)
    Tick, Apply, LightMerge, LightStagingDrain, LightFailSafeScan, LightSchedule,
    MeshProcess, MeshSchedule, GenerationProcess, LightQueueProbe,
    // World.Update regions untimed today (PM-3 appends these with their probes)
    WorldTime, BiomeTracker, OriginShift, TeleportHold, ViewDistance, Unload, BorderToggle,
    Visualization, GenerationAdmission, SaveRetryDrain, ChunkPoolPrune, DiskLoadApply,
    // Systems outside World.Update
    Physics, AudioDirectors, Clouds, SkyAndTime, DebugHud, Ui, VoxelVisualizer,
    Count
}

/// <summary>One frame of the Systems-tier ring buffer.</summary>
public struct PerfFrame
{
    public int FrameIndex;
    public float WallMs, CpuMs;                 // raw, from PerformanceMonitor's unsmoothed sample
    public float GpuMs, RenderThreadMs, PresentWaitMs;   // FrameTiming (NaN when unavailable)
    public int GcAllocBytes;                    // survives collection frames (§4.3)
    public byte GcCollections;                  // collections observed this frame
    public float UnattributedMs;                // World.Update total − Σ World slots
}
```

- **Slot timings** live in a native `NativeArray<float>` of `ringLength × PerfSlot.Count` alongside the
  `PerfFrame` ring, so a frame row is `PerfFrame` + its slot column — no per-frame allocation, one
  contiguous block (≈ 2 048 frames × (36 + 32 × 4) B ≈ 340 KB at Systems tier, allocated only when the
  tier is entered and disposed when it is left).
  *PM-1 (2026-10-03):* `PerfFrame` ships with only the fields PM-1 fills (`FrameIndex`, `WallMs`, `CpuMs` —
  12 B/frame); PM-2 adds the GPU and GC fields and PM-3 `UnattributedMs`. The frame rows are allocated on the first
  committed frame at every tier (24 KB); the slot block (10 slots × 4 B × 2 048 ≈ 80 KB today) only at Systems and up.
  *PM-2 (2026-10-04):* the row is 40 B (80 KB of rows). `GcAllocBytes` is meaningful only when a `PerfGcState` byte says
  `Measured` (otherwise `NoBaseline`, `Collected` or `Shrank`); `GcCollections` saturates at 255; the three frame-timing
  fields are NaN until a timing arrives; `EndTimestamp` (on the `FrameTiming` clock, §4.3) bounds each row so a
  late timing finds it. `CommitFrame` takes the raw readings (`PerfFrameReadings`) and computes the deltas itself.
- **Counters** (`PerfCounter` enum: queue depths, in-flight jobs per type, resident chunks, pool
  in-use/peak per pool, I/O ops/bytes, ThreadPool depth) are gauges sampled once per frame into a
  second column set, plus `Interlocked`-updated cumulative counters for worker-thread producers.
  *Moved to PM-3 (2026-10-03),* which brings their first producers; PM-1 built no counter storage.
  *PM-3 (2026-10-04), as built:* `PerfCounter` holds 13 gauges — queue depths, jobs in flight per type, resident and
  active chunks, active sections, and the idle stock of the job-array and mesh-output pools — and 5 per-frame counts
  derived from running totals: section, chunk-data, job-array, mesh-output and save-buffer pool misses. A gauge holds
  until set again; a per-frame count is zeroed by each commit, and its running-total baseline restarts when the tier
  enters Systems or a total goes down. The integer columns exist with the slot columns; `World` samples them at the end
  of `Update` while slots record, after the remainder bracket. Peaks come from window statistics over a gauge. The two
  native pools report idle stock rather than rented buffers, because some rented buffers are disposed by their owners.
- *PM-3 (2026-10-04), slot set as built:* 31 slots — the ten phases, the eleven `World.Update` regions listed above,
  `WorldUnattributed`, and outside `World.Update` `DiskLoadApply`, `Physics`, `Player` (input, camera and the block-cursor
  raycast), `AudioDirectors`, `Clouds`, `Environment` (foliage sway and the world-border wall), `DebugHud`, `Ui` and
  `VoxelVisualizer`. The remainder is a slot rather than a `PerfFrame` field, so a Basic row stays 40 B and the remainder
  joins the hitch records' top-three ranking. `SkyAndTime` was dropped: the day/night cycle runs inside
  `AdvanceWorldTime`, the `WorldTime` slot.
- **Statistics** are exact: nearest-rank percentiles (p50, p99), mean and worst frame, computed on read by
  in-place selection over a scratch copy of the ring (O(n), no sorting, no allocation) — `PerfWindowStats`.
  *PM-1 (2026-10-03):* this replaces the log-scale histograms first specified here, whose bucket width (±5–12 %)
  would add error on top of run-to-run noise in cross-run comparisons. Statistics over spans longer than the
  ring (a whole benchmark phase) need a phase-wide aggregate — PM-6's concern.

### 4.2 Probes

```csharp
long t = PerfStore.Begin(PerfSlot.ViewDistance);   // returns 0 and does nothing below the slot's tier
...
PerfStore.End(PerfSlot.ViewDistance, t);           // a start of 0 is a no-op
```

`Begin` is one static bool read + `Stopwatch.GetTimestamp()`; `End` accumulates into the slot's current
frame cell (several calls per frame sum). Under `ENABLE_PROFILER` the same call pair also emits the
slot's static `ProfilerMarker` (`PerfStore.<slot>`). The frame boundary is `PerfStore.CommitFrame`, called from
`PerformanceMonitor`'s end-of-frame coroutine: it stores the row and zeroes every accumulator, so a frame in which a
region never ran records 0 rather than repeating the last frame that did. No strings, no delegates, no `IDisposable` scopes on hot paths. Coverage
follows §2's gap list: every untimed `World.Update` region, `DiskLoadApply` around the async
`LoadOrGenerateChunkInner` continuation, and one probe per non-World system's `Update`/`LateUpdate`.

**Unattributed remainder:** `World.Update` is bracketed once as a whole; the remainder is
`total − Σ(World slots)` per frame. A remainder that grows is a coverage gap, by construction.

*PM-3 (2026-10-04), as built:* `PerfStore.BeginWorldFrame`/`EndWorldFrame` bracket `World.Update` after the facade's
per-frame clear, and subtract only the slot time recorded **inside** the bracket, so time charged elsewhere in the frame
(the disk continuation, a settings reload) cannot distort the remainder. A negative remainder means two slots overlapped;
it is kept as measured, counted, and reported by `/perf stats`. `CheckViewDistance` is probed inside the method, so all
three callers are charged — `OnSettingsChanged` included — with its span closed before `UnloadChunks`, which has its own
slot; the cloud rebuild it starts stays in `ViewDistance`, and `Clouds` covers `Clouds.Update` only. `DiskLoadApply`
opens after the read's `await` and is skipped while the bracket is open: a read that finishes before its `await`
resumes synchronously inside the `World.Update` slot that started the load, which already counts the time. Only the
`World.Update` slots are disjoint; a slot outside it may contain world work it triggers (a console teleport).
`VoxelRigidbody` is probed in `FixedUpdate` only, and `ChunkLoadAnimation` — one instance per chunk — not at all.

### 4.3 GC, GPU and memory

- **Per-frame allocation that survives collections:** prefer
  `GC.GetAllocatedBytesForCurrentThread()` deltas on the main thread (monotonic, unaffected by
  collection) if PM-0 proves it works under IL2CPP; otherwise keep the heap-delta method but mark the
  frame `GcCollections > 0` and exclude it from allocation statistics rather than recording 0.
- **Collection flag:** `GC.CollectionCount(0)` delta per frame. A collection frame whose wall time
  exceeds the hitch threshold is reported as a **GC-correlated hitch** — the closest available proxy for
  pause duration under Boehm, where no pause event exists.
- **GPU / render thread:** `FrameTimingManager.CaptureFrameTimings()` + `GetLatestTimings()` each frame
  at Frame tier and above; `FrameTiming.gpuFrameTime`, `cpuRenderThreadFrameTime`,
  `cpuMainThreadPresentWaitTime` separate GPU-bound frames from VSync waits — today all three hide in
  "Idle/Other". **Requires `enableFrameTimingStats`** (`ProjectSettings.asset:162`, currently 0). The
  `Windows - Development` and `Windows - Production` profiles inherit Player Settings (`m_Settings: []`);
  only `Windows - Profiler` holds a frozen full copy (its own `enableFrameTimingStats: 0` at line 187), so it
  needs its own edit. *PM-0 (2026-10-03): a Master player with the setting **off** still delivered frame
  timings while the frame-time recorder counters were recording (§8 q4). PM-2 should first confirm that
  recording those counters is enough — reading `FrameTimingManager` with no recorder running settles it.
  If it is, the tier can enable timing on demand and the project setting stays 0.*
- *PM-2 (2026-10-04), as built:* allocation is §4.3's fallback — the heap delta, with a frame in which a collection
  completed flagged `Collected` and left out of allocation statistics, and a frame whose heap shrank with no counted
  collection flagged `Shrank` and counted (0 such frames over ~25 000 in the Editor smoke). The delta is process-wide,
  so worker-thread allocation still lands on the main frame. The store outlives the World scene, so
  `PerformanceMonitor.OnEnable` resets the baseline: the first frame after a trip through the main menu records
  `NoBaseline` rather than a delta spanning the menu. Frame timing: `PerfFrameTimingSource` records the
  `Render`-category counters `GPU Frame Time` and `CPU Main Thread Frame Time` while the tier is at Frame or above, and
  the project setting stays 0. `GetLatestTimings` returns frames a few frames late; each timing is written into the
  row whose interval `[previous end, own end)` holds its `frameStartTimestamp`. **That timestamp is not on the
  `Stopwatch` clock** — in the Editor both tick at 10 MHz, but their epochs differ by ≈ 1 823 s — so rows record their
  end with `ProfilerUnsafeUtility.Timestamp`, which matches it. Rows never matched stay NaN, and `/perf stats` reports
  received vs matched, so a clock mismatch in another player shows as "n/a" instead of a wrong value. Every timing
  `GetLatestTimings` returns is written again on each read (a later read of a frame replaces an earlier one), but
  counted only on its first; a GPU time of 0 is stored as NaN — the platform reporting none — and `/perf stats` then
  says the timings arrive without GPU time.
- **Memory:** existing `Profiler.GetTotal*MemoryLong` + managed heap, plus pool counters for
  `ChunkJobArrayPool` / `MeshOutputPool` / `ChunkPoolManager` and per-frame mesh-upload bytes;
  `ProfilerRecorder` memory/render counters only where PM-0 found them `Valid` in the target build.

### 4.4 Worker threads and I/O

- **Job latency:** schedule timestamp stored with each job record; completion observed on the main
  thread → schedule-to-complete latency per job type (frame-granular, but enough to see queueing).
- **Job execute time:** each job writes begin/end timestamps into a `long` pair in its output struct
  (Burst-side timestamp source verified in PM-0 — e.g. `ProfilerUnsafeUtility.Timestamp`); the main
  thread sums them → per-type worker ms/frame and **worker utilization** = Σ execute ÷ (workers × wall).
- **Disk I/O:** `ChunkStorageManager`/`RegionFile`/`ChunkSerializer` record read/write latency, payload
  bytes, compression time and ratio with `Interlocked` adds into cumulative counters; ThreadPool
  in-flight count likewise.
- *PM-4 (2026-10-05), as built:* jobs do not write a begin/end pair. Each job given a `JobBusyTimer` adds its execute
  time and one link to a record shared by its whole chain, with `Interlocked` writes, because the parallel terrain job's
  256 columns and every link of a chain write the same record. Records come from a `PerfJobTimingPool` owned by
  `WorldJobManager` and freed only after every job has completed. Five job types are timed: generation, lighting,
  meshing, the fluid tick (one job per chunk) and the fluid sound scan. The scan can outlive a world teardown, so it keeps
  its own one-record pool. Latency runs from schedule until the main thread has
  consumed the result, so a job held back by a per-frame budget counts as waiting. Each job type has per-frame counts
  (completed, latency µs, busy µs) and a per-job sample ring of the newest 1 024 jobs. Utilization is computed over the
  held window only. Disk I/O records read, deserialize, serialize (compression included — the two run interleaved
  through one stream) and write time, payload bytes, and load hits versus misses (`StorageIoStats`). The compression
  *ratio* is deferred: only compressed bytes are known. "ThreadPool depth" is the engine's own count of background
  loads and saves in flight, plus the wait between submission and a pool thread starting each one.

### 4.5 Hitch detection and snapshots

A frame is a hitch when `WallMs > max(absoluteMs, k × rolling median)` (defaults 33 ms and ×2.5,
settings-file tunable). On a hitch the store freezes the last *N* frames (default 120) and keeps
recording *M* more (default 30), then copies that window into a hitch record: frame rows, slot columns,
counters, GC flag, the top three slots by cost, and the log lines emitted in the window (§4.6). The HUD
lists recent hitches; at Capture tier each record is queued for export. Records are pooled; at most
*H* (default 16) are retained.

*PM-2 (2026-10-04), as built* (`PerfHitchDetector`): the median is the p50 of the last 240 frames, refreshed every 60,
so until the first refresh only the absolute floor applies; a hitch is strictly `>` the threshold. Hitches inside an
open window join it (counted, the slowest becomes the worst row) without extending it, including one on the closing
frame. The newest 16 records are held, a new one overwriting the oldest. Slot columns and the top three slots — the
costliest non-zero slots of the worst frame — exist only for records closed at Systems; dropping to Frame keeps the
ranking but frees the per-row slot times, and leaving Frame discards every record and an open window. Records carry
no counters or log lines yet: those arrive with PM-3 and PM-7. *PM-3 (2026-10-04):* records closed at Systems carry
every row's counters, in a block allocated and freed with the slot block; `/perf hitches` prints the worst frame's
non-zero counters. The thresholds are the settings-file fields
`perfHitchMinMs` / `perfHitchMedianFactor`, read when the tier is applied; `/perf hitches` lists the records.

### 4.6 Logger

```csharp
public enum LogCategory : byte { Startup, Pipeline, Generation, Lighting, Meshing, Storage,
                                 Fluids, Physics, Rendering, Audio, Ui, Commands, Count }
public enum LogLevel : byte { Off, Error, Warning, Info, Verbose }
```

- `EngineLog.Info(LogCategory, string format, T1 a, …)` generic overloads check the category level
  **before** formatting — no string is built, no argument boxed, when disabled. Formatting goes through
  the existing `StringBuilderFormat` helpers (MT-3). `EngineLog.Error` is always on.
- **One tag scheme:** `[Category]` derived from the enum (replacing method-, class- and backlog-ID tags;
  backlog IDs never appear in player-facing text).
- **Rate limiting:** per-call-site latch/rate (static key per site) so a per-chunk log cannot flood.
- **Sinks:** Unity's logger (Player.log/console) and, at Capture tier, the session file with frame index,
  so log lines align with the frame rows they explain.
- **Settings:** a per-category level array on the DebugScreen tab (or a Diagnostics sub-header), with a
  "Verbose all" preset; it **replaces** `enableDiagnosticLogs`, `enableWaterDiagnosticLogs` and
  `enableSaveSystemDiagnosticLogs` (JSON keys migrate by reading the old bools once; `JsonUtility`
  ignores removed keys).
- **Stack-trace policy:** Production profile sets **Log/Warning = None, Error/Exception = ScriptOnly**
  (inverting today's setting); Development keeps ScriptOnly for Warning.
- **Burst:** jobs keep `FixedString` logging on abort paths only (the BFS-cap diagnostic is the model).

### 4.7 Export and HUD

- **Session file** (Capture tier): one CSV row per frame (or every *n*th frame, setting) under
  `persistentDataPath/PerfLogs/`, plus one file per hitch record; written by a single background writer
  from pooled buffers, size-capped with rotation. Reuses `BenchmarkEnvironment`'s path handling
  (including Android MediaStore).
- **Benchmark reports** read percentiles, hitch counts and GC flags from the store, adding the
  worst-frame / p99 / hitch / collection columns they lack today (old columns kept for continuity).
- **HUD:** a Systems panel sorted by slot cost (avg / p99 / max), the unattributed remainder, GPU vs CPU
  split, a hitch list, and graphs overlaying raw max on the smoothed line. `DT-4` already removed the
  existing HUD's allocations ahead of this pass; new panels keep to that, checked with `Minecraft Clone/Benchmarks/
  Debug HUD Allocations`. In the Editor a changed TMP text always allocates its string (TMP syncs it under
  `#if UNITY_EDITOR`), so a panel is allocation-free in the Editor only while its text is unchanged.
- *PM-6 (2026-10-06), as built:*
  - **Final rows.** A row stops changing at `PerfStore.RowFinalAge` (17) frames old: frame timings arrive late and
    `SampleFrameTiming` writes rows up to 16 frames old after each commit. Each commit hands that one row to the phase
    recorder and the session exporter, so both see every frame exactly once, timings included.
  - **Phase statistics.** `PerfPhaseRecorder` routes rows to phases by frame index (`[first, end)`), gathers the six
    frame fields in native lists — no managed allocation as a long phase grows — and computes exact p50/p99/worst/mean
    in place (`PerfWindowStats`' native overload) when the first row past a phase's end arrives. Frame fields only; no
    per-slot phase statistics (decision 4, §7.7). `BenchmarkMetricsCollector` owns one per run, counts hitch frames per
    phase from the detector, and fills `PhaseMetrics.FrameStats` when recording stops; `BenchmarkController` waits
    `RowFinalAge` + 1 frames after the last phase so its final frames count.
  - **Report.** A "Performance Monitor" block (tier, and the hitch thresholds as the detector applies them), raw worst frame / hitch frames / collections in
    the overall summary, and a "Frame Health (every frame)" table per group: frames, wall p50/p99/worst, CPU p50/p99, GC
    p99, collections, hitches, GPU p99. The averaged columns stay. The hitch thresholds joined
    `OverlayBenchmarkSettingsFromDisk` (removed 2026-10-10). *ES-0 (2026-10-10):* the table also prints each phase's `First frame` / `End
    frame` (`PerfPhaseSummary.FirstFrame` / `EndFrame`, culture-invariant), the key `Tools/Python/summarize_perf_session.py`
    selects a session file's rows by to report every slot, counter, job type, disk I/O, tick part and hitch record per
    phase, cross-checked against the row it came from.
  - **Session files** (Capture, while a world's `PerformanceMonitor` is enabled): `persistentDataPath/PerfLogs/
    PerfSession_<yyyy-MM-dd_HH-mm-ss-fff>.csv`, one row per frame — 9 frame columns, then every slot (`<Slot>_ms`) and
    every counter — ASCII, invariant formatting, an empty cell where a frame has no value. A new part (`_part02`, …)
    every 64 MB; at 1 GB the session stops writing and warns; no file is ever deleted (settings-file fields
    `perfCapturePartMb`, `perfCaptureSessionMb`). Each closed hitch record becomes
    `<session>_hitch<NNN>_frame<F>.csv`: a `# hitch …` summary line (frame, threshold, worst, hitch frames,
    GC-correlated, top slots), the session header plus a `mark` column, and the window's rows with `hitch` / `worst`
    marks.
  - **Writer.** `PerfSessionExporter` copies rows on the main thread into 16 pooled 256-row blocks (a full pool drops
    rows or a hitch window and counts them rather than allocating) and formats them on a dedicated below-normal background thread — not
    the ThreadPool, where chunk loads and saves queue. Rows format without allocating, because the store's allocation
    figure is process-wide; opening a file allocates. A write failure stops the session's export and is reported once;
    leaving Capture, a world unload, quit and an Editor reload close the session after writing its newest rows, waiting
    at most 2 s for the writer. `/perf stats` prints the session file, frames, megabytes, hitch files and anything not
    written.
  - **Not as specified:** the files stay under `persistentDataPath` on Android too (`BenchmarkEnvironment`'s MediaStore
    route takes a whole file, not a stream — pull them with adb); log mirroring into the session file is PM-7's.

### 4.8 Ownership and threading

- All slot/column writes are main-thread only; worker producers use `Interlocked` cumulative counters
  sampled on the main thread. The ring and hitch records are owned by `PerfStore`; the export writer
  receives pooled copies, never the live ring.
- Static state follows the house rules: `[NoAutoStaticsCleanup] // reset in DomainReset` on every member,
  reset inside the class's single `[RuntimeInitializeOnLoadMethod]`; native buffers disposed on tier
  exit and on application quit.

### 4.9 Coverage map

*As of 2026-10-05 (PM-8), derived from the code — the probe sites, the per-frame scripts and the job structs — not from the
sections above.* Every phase that adds, moves or removes a probe updates this section in the same commit. Before relying
on it for a coverage analysis, re-derive it with the commands at the end: the tables are a snapshot, the commands are
the check. Whether a system has had a performance *analysis* pass is a different question, tracked as `AC-1`…`AC-10` in
[`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) § Audit coverage gaps.

**Frame level.**

| Measure                                  | Tier                                         | Source                                                          | Not covered                                                                                                |
|------------------------------------------|----------------------------------------------|-----------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| Wall and CPU time per frame              | every tier                                   | `PerformanceMonitor`'s brackets → `CommitFrame`                 | time outside the brackets (wall − CPU − present wait)                                                      |
| Managed allocation and collections       | every tier                                   | heap delta + `GC.CollectionCount`                               | process-wide: worker allocation lands on the main frame; a collection frame has no figure                  |
| GPU, render-thread and present-wait time | Frame                                        | `FrameTiming` via the frame-time recorders                      | whole frame only — no per-pass GPU time                                                                    |
| Hitch records                            | Frame (slot times and counters at Systems)   | `PerfHitchDetector`                                             | —                                                                                                          |
| `World.Update` remainder                 | Systems                                      | `PerfSlot.WorldUnattributed`                                    | `World.Update` only — no whole-frame remainder                                                             |
| Memory                                   | always-on history only                       | `PerformanceMonitor` (`Profiler.GetTotal*Memory`, managed heap) | not in `PerfStore`'s frame rows or hitch records                                                           |
| Whole-phase statistics (PM-6)            | every tier; benchmarks run at Frame or above | `PerfPhaseRecorder`, fed each final row                         | frame fields only — slots and counters per phase are in the session file                                   |
| Session and hitch files (PM-6)           | Capture                                      | `PerfSessionExporter`                                           | rows and hitch records lost to a full block pool, the size cap or a write failure are counted, not written |
| Time to stable, once per launch (ES-0)   | every tier                                   | `StartupTimeline` (owned by `World`)                            | the startup coroutine's frames have no slot; the Editor's jitter never meets the settle rule               |

**Main thread — slots (Systems tier, or forced).** Inside `World.Update` the slots are disjoint, so with the remainder
they sum to the bracket:

| Slot(s)                                                                                     | Covers                                                                                                                                                                                  |
|---------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Tick`                                                                                      | `ProcessTickUpdates`: active-chunk snapshot, per-chunk fluid prepare, scheduling, the `Complete()` wait, and the drain (grass tick, fluid replay) — split by the `Tick*` counters below |
| `Apply`                                                                                     | `ApplyModifications`                                                                                                                                                                    |
| `LightMerge`, `LightStagingDrain`, `LightFailSafeScan`, `LightSchedule`, `LightQueueProbe`  | the lighting passes; `LightQueueProbe` exists only in instrumented builds                                                                                                               |
| `MeshProcess`, `MeshSchedule`                                                               | completed-mesh apply and upload; mesh-queue drain                                                                                                                                       |
| `GenerationProcess`, `GenerationAdmission`                                                  | completed-generation pass; admission of queued requests                                                                                                                                 |
| `WorldTime`, `BiomeTracker`, `OriginShift`, `TeleportHold`, `BorderToggle`, `Visualization` | the smaller `World.Update` regions, each probed around its call                                                                                                                         |
| `ViewDistance`, `Unload`                                                                    | the crossing rebuild (all three callers, including the cloud rebuild it starts) and the unload pass                                                                                     |
| `SaveRetryDrain`, `ChunkPoolPrune`                                                          | one pending failed-save retry; the pools' prune (`ChunkPoolManager.Update`, a plain method)                                                                                             |
| `WorldUnattributed`                                                                         | the bracket minus the slots recorded inside it                                                                                                                                          |

Outside `World.Update` (a slot here may contain world work it triggers, e.g. a console teleport):

| Slot              | Probed in                                                                                                           | Not covered                                                              |
|-------------------|---------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------|
| `DiskLoadApply`   | the disk-load continuation in `World.cs`                                                                            | skipped while the `World.Update` bracket is open (already counted there) |
| `Physics`         | `VoxelRigidbody.FixedUpdate`                                                                                        | its `Update` (step-smoothing ease) and `LateUpdate` (debug bounds)       |
| `Player`          | `Player`, `PlayerInteraction`                                                                                       | —                                                                        |
| `AudioDirectors`  | `SoundManager`, `AmbienceDirector`, `MusicScheduler`, `FluidEmitterDirector`, `PlayerFootsteps`                     | —                                                                        |
| `Clouds`          | `Clouds.Update`                                                                                                     | the crossing rebuild is in `ViewDistance`                                |
| `Environment`     | `FoliageSway`, `BorderWallRenderer`                                                                                 | —                                                                        |
| `DebugHud`        | `DebugScreen.Update`                                                                                                | —                                                                        |
| `Ui`              | `ConsoleUI`, `Toolbar`, `DragAndDropHandler`, `TooltipManager`, `WorldUIManager`, `SafeAreaFitter`, `TouchControls` | uGUI's own layout and canvas rebuilds                                    |
| `VoxelVisualizer` | `VoxelVisualizer.LateUpdate`                                                                                        | —                                                                        |

**Worker jobs (Systems tier).**

| Job                                                                                                                | Type           | Timed                                                                              |
|--------------------------------------------------------------------------------------------------------------------|----------------|------------------------------------------------------------------------------------|
| `StandardWormCarverJob`, `StandardChunkGenerationJob` (per column), `CaveIsolationFilterJob`, `ActiveVoxelScanJob` | Generation     | ✅ one record per chain                                                            |
| `LegacyChunkGenerationJob`                                                                                         | Generation     | latency only — the job is frozen                                                   |
| `NeighborhoodLightingJob`                                                                                          | Lighting       | ✅                                                                                 |
| `MeshGenerationJob`, `MeshPostProcessJob`                                                                          | Meshing        | ✅ one record per chain                                                            |
| `FluidTickJob`                                                                                                     | Fluids         | ✅ one record per chunk                                                            |
| `FluidEmitterScanJob`                                                                                              | FluidSoundScan | ✅ the scanner's own one-record pool                                               |
| `CloudPatternJob`                                                                                                  | —              | ❌ rare rebuild, completed where it is scheduled, inside `Clouds` / `ViewDistance` |
| `VoxelVisualizerJob`                                                                                               | —              | ❌ debug visualizer                                                                |
| `TimestampProbeJob`                                                                                                | —              | n/a — the engine API probe's own measurement                                       |

Unity's own jobs (rendering, animation, uGUI) are invisible, so utilization is the engine's share of the workers.

**Background I/O (Systems tier).** `ChunkStorageManager` only: loads (hits, misses, read and deserialize time, payload
bytes), saves on every path (serialize and write time, payload bytes and their uncompressed size), the ThreadPool wait and the background operations
it covers, and the operations in flight. Region files touched outside it — the migration step, the world list — and the
non-chunk files (`level.dat`, pending modifications, pending lighting) are not counted.

**Counters (Systems tier).** 57, sampled in `World.SamplePerfCounters`:
- *Gauges (14):* the pipeline queues and jobs in flight (`GenerationQueue`, `GenerationInFlight`, `LightReady`,
  `LightWaiting`, `LightInFlight`, `MeshQueue`, `MeshInFlight`, `ModificationQueue`), residency (`ResidentChunks`,
  `ActiveChunks`, `ActiveSections`), the native pools' idle stock (`JobArraysPooled`, `MeshOutputsPooled`) and `IoInFlight`.
- *Per-frame counts (43):* pool misses (section, chunk data, job array, mesh output, save buffer); completed / latency /
  busy for each of the five job types, and `UntimedJobs`; the twelve disk I/O counts; the behavior tick's six timed parts
  (`TickListUs`, `TickPrepareUs`, `TickScheduleUs`, `TickWaitUs`, `TickGrassUs`, `TickFluidReplayUs`, from
  `PerfTickTotals`), its counts (`TickFluidChunks`, `TickSnapshotKb`, `TickGrassVoxels`) and `FluidTickerPoolMisses`.
  The tick's drain bookkeeping and ticker returns are timed by no part: `/perf stats` shows them as the average "Other".

**Known gaps.**

| Gap                                                                                                                                                                                         | Consequence                                                                                              | Owner                                                          |
|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|----------------------------------------------------------------|
| Other composite slots (`LightMerge`, `Unload`)                                                                                                                                              | a hitch they lead cannot say which part                                                                  | PM-8's pattern (§7.6), if their hitches warrant it — not filed |
| Per-chunk fluid prepare distribution                                                                                                                                                        | the prepare is a per-frame sum, so a few heavy chunks and many light ones read alike                     | not filed — PM-8 decision 2                                    |
| Unity's own main-thread work: uGUI layout and canvas rebuilds, the renderer features' Render Graph recording (UI band composite and blur, cloud prepass, underwater overlay), animation     | in the frame's CPU time, in no slot                                                                      | not filed; `AC-4` covers the UI blur's cost statically         |
| A whole-frame remainder                                                                                                                                                                     | CPU time outside every slot and outside `World.Update` has no figure                                     | not filed                                                      |
| Per-pass GPU time                                                                                                                                                                           | a GPU-bound frame cannot say which pass                                                                  | `ES-25`'s rendering baseline (Frame Debugger)                  |
| Memory per frame                                                                                                                                                                            | a memory spike cannot be tied to a hitch                                                                 | not filed                                                      |
| Main menu and loading scenes                                                                                                                                                                | `PerformanceMonitor` lives in the World scene, so they record no frames                                  | not filed                                                      |
| The initial load's counters                                                                                                                                                                 | sampled in `World.Update`, so they read 0 until it ends                                                  | not filed                                                      |
| Per-job latency split (queue wait, execution, completion lag)                                                                                                                               | latency is one figure                                                                                    | v2 swimlanes (extension roadmap)                               |
| Per-slot and per-counter statistics over a whole phase                                                                                                                                      | the report's phase statistics cover the frame fields; slots and counters per phase need the session file | not filed — PM-6 decision 4                                    |
| Log volume and cost                                                                                                                                                                         | —                                                                                                        | **PM-7**                                                       |
| Small or debug per-frame code: `ChunkLoadAnimation` (one per chunk), `VoxelRigidbody.Update`/`LateUpdate`, `TerrainGenDebugOverlay`, `CreditsMenuController`, the benchmark harness scripts | negligible or tooling                                                                                    | not filed — low value                                          |
| Untimed jobs: `CloudPatternJob`, `VoxelVisualizerJob`, the legacy generation job's busy time                                                                                                | —                                                                                                        | not filed — low value                                          |

**Re-deriving the map** (from the repo root):

```bash
# Every slot and the files that probe it
grep -rnoE "PerfStore\.Begin\(PerfSlot\.[A-Za-z]+" Assets/Scripts
# Per-frame scripts, marked probed or not (a plain method named Update shows here too — ChunkPoolManager is one)
for f in $(grep -rlE "void (Update|LateUpdate|FixedUpdate)\(\)" Assets/Scripts --include=*.cs); do
  grep -q "PerfStore.Begin(PerfSlot" "$f" && echo "probed    $f" || echo "UNPROBED  $f"; done | sort
# Timed jobs against every job struct
grep -rln "public JobBusyTimer Timer;" Assets/Scripts
grep -rnE "struct \w+ : IJob\w*" Assets/Scripts --include=*.cs
```

The counters and their sources are the body of `World.SamplePerfCounters`.

---

## 5. Prerequisites & integration points

- ⚠️ **PM-0 must answer the API questions first** (§8): which `ProfilerRecorder` counters are `Valid` in a
  Master player, whether `GC.GetAllocatedBytesForCurrentThread` works under IL2CPP, which Burst-callable
  timestamp source exists, and that `FrameTimingManager` reports GPU time once enabled.
- **Comparability:** `WorldFrameProfiler`'s facade must keep `PipelineTelemetry.PassMsTotals` identical —
  pinned by a `Validate Pipeline Backpressure` baseline before PM-1 lands (that suite indexes the slot
  array by `PhaseCount`). *PM-1 (2026-10-03):* that baseline already existed as **B22**, but asserts thresholds
  (≥ 8 ms, == 0), not identity; `Validate Performance Monitor` **B9** adds the exact check — every phase's
  `LastFrameMs` equals `ticks × (1000.0 / Stopwatch.Frequency)`.
- **Instrumentation variant:** decide whether `Windows - Development` should use the Instrumented
  variant; fix the "dev/editor builds only" comments; hide HUD rows and toggles whose code is compiled out.
- **No backlog IDs in UI** — tier/level labels and tooltips are plain language.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                      |
|-------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — instrumentation reads counters, never voxel storage                                                                               |
| Burst jobs 100 % Burst-compatible               | Jobs only write a timestamp pair into their output struct (source verified in PM-0); no managed calls added to jobs                           |
| No GC / LINQ in hot paths                       | Enum-indexed pre-allocated arrays; no strings, delegates or scopes on probes; formatting only on the HUD's 10 Hz refresh or the export thread |
| Pooling conventions                             | Hitch records and export buffers pooled; native rings allocated on tier entry only                                                            |
| No BinaryFormatter/JSON for terrain             | Logs are CSV diagnostics; the tier and levels are `Settings` config JSON                                                                      |
| BlockIDs constants, no raw IDs                  | No block IDs involved                                                                                                                         |
| Statics / domain reload                         | Every static annotated + reset in the single `[RuntimeInitializeOnLoadMethod]` (§4.8)                                                         |

---

## 7. Phased implementation plan

| Phase                              | Scope                                                                                                                                                                                                                                                                                                         | Effort | Depends on       | Status                                                                                                                                  |
|------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------------|-----------------------------------------------------------------------------------------------------------------------------------------|
| **PM-0 — Verify**                  | Execution packet §7.1. Master-build probe: dump `ProfilerRecorderHandle.GetAvailable` + `Valid`; `FrameTimingManager` with frame-timing stats on; `GC.GetAllocatedBytesForCurrentThread` under IL2CPP; Burst timestamp source; probe cost (QPC ns)                                                            |   🟢   | —                | ✅ 2026-10-03 — §8 q1–q4 answered in two Master builds                                                                                  |
| **PM-1 — Core store**              | `PerfStore`, `PerfSlot`, raw per-frame ring, exact window statistics, tier setting + live apply, `WorldFrameProfiler` facade (identical `PassMsTotals`), `/perf stats` + `/perf tier`                                                                                                                         |   🟡   | PM-0             | ✅ 2026-10-03 — code + suite (§7.2); confirmed in an IL2CPP Master build                                                                |
| **PM-2 — Frame tier**              | Per-frame GC + collection flag, `FrameTiming` GPU/render/present-wait, hitch detector + snapshots                                                                                                                                                                                                             |   🟡   | PM-1             | ✅ 2026-10-04 — code + suite (§7.3); confirmed in an IL2CPP Master build                                                                |
| **PM-3 — Coverage**                | Slots for every untimed `World.Update` region + unattributed remainder; non-World systems; `PerfCounter` + counter columns, with gauges for queues, in-flight jobs, pools, resident chunks (moved from PM-1); `World.cs`'s facade probes to the slotted API; the Master IL2CPP overhead A/B (moved from PM-1) |   🟡   | PM-1             | ✅ 2026-10-04 — code + suite (§7.4); confirmed in an IL2CPP Master build                                                                |
| **PM-4 — Workers & I/O**           | Job schedule→complete latency, in-job execute time, worker utilization; disk latency/bytes/compression; ThreadPool depth                                                                                                                                                                                      |   🟡   | PM-0, PM-1, PM-3 | ✅ 2026-10-05 — code + suite (§7.5); confirmed in an IL2CPP Master build                                                                |
| **PM-5 — HUD**                     | Systems panel, hitch list, GPU/CPU split, raw-max graph overlay (`DT-4` ran ahead of it, 2026-10-06)                                                                                                                                                                                                          |   🟡   | PM-2, PM-3       | —                                                                                                                                       |
| **PM-6 — Export**                  | Session CSV + hitch dumps on a background writer; benchmark reports read the store, with phase-wide percentiles (an aggregate spanning more than the ring — moved from PM-1); benchmark mode forces Capture                                                                                                   |   🟡   | PM-2             | ✅ 2026-10-06 — code + suite (§7.7); confirmed in an IL2CPP Master build; benchmarks raise the tier to Frame instead of forcing Capture |
| **PM-7 — Logger**                  | `EngineLog` categories/levels/rate limits/tags, migration of the 3 diagnostic flags and the 327 call sites (by category, in passes), stack-trace policy per build profile, variant-drift fix                                                                                                                  |   🟡   | —                | —                                                                                                                                       |
| **PM-8 — Behavior-tick breakdown** | Execution packet §7.6. Split the `Tick` slot's main-thread time into its parts — active-chunk snapshot, per-chunk fluid prepare, scheduling, the `Complete()` wait, and the drain (grass tick, fluid replay) — with per-tick fluid chunk and snapshot-byte counts                                             |   🟢   | PM-3, PM-4       | ✅ 2026-10-05 — code + suite (§7.6); confirmed in an IL2CPP Master build                                                                |

**Minimal set with standalone value:** PM-0 + PM-1 + PM-2 — worst-frame, p99, hitch snapshots with GC
and GPU attribution — answers the roadmap's "is the traversal spike GC?" question on its own. PM-7 is
independent and can run in parallel.

**Validation is built alongside, not after:** a new `Validate Performance Monitor` suite pins the pure
parts — percentile math, the hitch-detector truth table and window freeze, ring wrap-around,
tier gating (below-tier probes write nothing), the facade's slot mapping, and CSV formatting — and its own
scenario invokes `PerfStore.DomainReset` to cover the new statics (no generic statics sweep exists; "B34" named a
Lighting `Reset()` baseline). Timing *values* are not suite-testable (wall clock), so the overhead budget (Off: one
static read per probe; Systems: ≤ 50 µs/frame) is measured in the Editor first (`Minecraft Clone/Benchmarks/PerfStore
Overhead`), and by a Master IL2CPP A/B through the `perf-benchmark` skill only once PM-3's probes make the cost
large enough to resolve above benchmark noise.

### 7.1 Execution packet — PM-0 + the H-1 counters (planned 2026-10-02, not executed)

Fact sweep done at `376778fe`; **re-verify every anchor first**. The decisions below are **open** — the
executing session asks them before coding (recommendation first in each).

**Goal.** One Master IL2CPP run answers §8 questions 1–4, and the section/buffer pools gain the miss
counters the roadmap's hypothesis H-1 (`ENGINE_SCALING_PERFORMANCE_ROADMAP.md` §2.2.1) needs. No behavior
change: the probe runs only when triggered, and the counters are one in-lock increment each.

**Verified (at `376778fe`).**
- No `.asmdef` under `Assets/Scripts`; runtime harnesses live in `Assets/Scripts/Benchmarks/` (namespace
  `Benchmarks`). `IsolatedJobProbe.cs` is the precedent for scheduling a tiny job outside the pipeline.
- Report path: `BenchmarkEnvironment.WriteReportToDisk` (`:123`, folder `persistentDataPath/Benchmarks`,
  `:143`) with the `DescribeSystem()` header (`:35`) — the only reliable Master-vs-Release signal
  (`BuildStamp.Il2CppConfiguration`; `Debug.isDebugBuild` cannot tell them apart).
- **The F10 bootstrap pattern does not reach Master:** `P4BackpressureBenchmark.cs:60–79` sits inside
  `#if UNITY_INCLUDE_INSTRUMENTATION`, which the Release code variant (production) does not define. Triggers
  that do work in Master: a `[SettingAction]` button (template `UI/SettingsMenuController.cs:169–177`, Benchmark
  tab), a console command (`Commands/ConsoleCommandInstaller.cs:20–39`; `InstalledCommandCount = 18` is
  asserted by `CommandConsoleValidationSuite`), or the runtime gate `MicroBenchmarkGate` (`:27–31`).
- `enableFrameTimingStats: 0` at `ProjectSettings.asset:162`; `Windows - Development` / `Windows - Production`
  inherit (`m_Settings: []`), `Windows - Profiler` has its own frozen `0` (line 187). Production captures use
  `Windows - Production` (Master, Release variant, non-dev).
- API compatibility `apiCompatibilityLevel: 3`, no per-platform override. `GC.GetAllocatedBytesForCurrentThread`
  is already used Editor-side with an "is it live on this runtime" INCONCLUSIVE guard
  (`MeshBuildQueueValidationSuite.Baseline.cs:255–277`) — the precedent for testing liveness, not just existence.
- `ProfilerUnsafeUtility.Timestamp` + `TimestampToNanosecondsConversionRatio` exist in 6000.6 (`unity-api`
  MCP); Burst-callability is unverified. `FrameTimingManager.GetLatestTimings(uint, FrameTiming[])` fills a
  caller-owned array (no allocation).
- Pools: `ConcurrentDynamicPool.Get` (`Helpers/ConcurrentDynamicPool.cs:67–81`) increments `_totalGets` in the
  lock and runs `_createFunc()` **outside** it on a miss; `TotalDestroyed` uses `Interlocked`.
  `ChunkPoolManager._sectionPool` (`:16`, created `:91–98`), `GetChunkSection` (`:230`); DebugScreen's
  "Pool destroys" rows (`DebugScreen.cs:549–551`). `SerializationBufferPool` is a separate unbounded
  `ConcurrentBag` with no counters.

**Plan.**
1. **Counters.** In `ConcurrentDynamicPool.Get`, `_totalCreated++` inside the lock on the miss branch;
   `public long TotalCreated => Interlocked.Read(ref _totalCreated);` (instance field — no static rules).
   Same for `SerializationBufferPool` misses (Interlocked). Surface `CreatedSections`/`CreatedData` on
   `ChunkPoolManager` and a "Pool misses" DebugScreen row beside the destroys row.  
   **Gate:** `unity recompile --format json` clean; in play mode the section-miss count rises while new
   terrain generates and stays flat while standing still.
2. **Probe** `Assets/Scripts/Benchmarks/MasterBuildApiProbe.cs`: a coroutine of ~120 frames logging `[PM0]`
   lines and a `PM0_ApiProbe` report — every `ProfilerRecorderHandle.GetAvailable` handle (category, name,
   unit, `Valid`, non-zero over the window; recorders disposed after); `FrameTimingManager.IsFeatureEnabled()`
   + p50/max of `gpuFrameTime`, `cpuRenderThreadFrameTime`, `cpuMainThreadPresentWaitTime`; GC APIs with a
   known 1 MB allocation delta and a monotonic check (a compile failure of `GC.GetTotalAllocatedBytes` is
   itself the answer); a `[BurstCompile]` job reading `Timestamp` before/after with a `[BurstDiscard]` flag
   proving which path ran; ns/call of `Stopwatch.GetTimestamp` and managed `Timestamp` over 10⁶ calls.
   Gated off in automated/benchmark modes (`WorldLaunchState.IsAutomatedMode`).  
   **Gate:** recompile clean; one Editor run produces the report (Editor values are not the answer — Burst is
   async there).
3. **Runs (manual, player builds):** `Windows - Production` build with `enableFrameTimingStats`
   temporarily `1`, then the same
   build at `0` for its cost (answers §8 q4); optionally `Windows - Profiler` for contrast. Revert the flag
   unless PM-2 is next.
4. **Record** the answers in §8 (and the §1 summary if `ProfilerRecorder` turns out usable in Master), via
   `docs-sync`. The first H-1 reading can ride the same build: one fast generation flight with the counters on.

**Open decisions (ask first).**
1. **Trigger** — a Benchmark-tab `[SettingAction]` button (works in Master, user-initiated) · or a console
   command (bumps `InstalledCommandCount` 18 → 19 and its suite) · or a setting-gated startup one-shot.
   **Recommend the button.**
2. **Frame-timing stats for the probe** — a temporary project-wide flip, reverted after · or leave it on as
   PM-2's prerequisite. **Recommend temporary**, so the probe stays a zero-behavior-change build.
3. **After PM-0** — keep the probe as a Benchmark-tab diagnostic · or delete it. **Recommend keep** (it
   re-answers the same questions after every Unity upgrade).
4. **H-1 visibility** — DebugScreen row only · or also a benchmark-report row with generated vs loaded chunk
   counts (what H-1's test actually needs). **Recommend both.**

**Not doing.** `PerfStore` (PM-1) and any tier/settings work; enabling frame-timing stats permanently; fixing
anything H-1 points at (ES-9/ES-10 own that).

**Execution record (2026-10-03, code landed; Master player run pending).** Decisions as taken:
1. **Trigger → console command, plus the button**: `/perf probe` (`PerfCommand`, alias `/profiler`; `/perf` alone
   prints the last summary; `InstalledCommandCount` 18 → 19) in a world, and a Benchmark-tab **Run Engine API
   Probe** `[SettingAction]` that also works from the main menu — the cleaner run, with no chunk streaming in the
   frames sampled. Completion raises a toast, so `MainMenuController` now spawns a toast host too.
2. **Frame-timing stats → temporary flip** for the player run, reverted after.
3. **Probe → kept as part of the diagnostics layer**: `Assets/Scripts/Diagnostics/EngineApiProbe.cs` (namespace
   `Diagnostics`, the planned home of `PerfStore`/`EngineLog`), neutrally named — log tag `[ApiProbe]`, report
   `EngineApiProbe_<timestamp>.log`, no backlog ID in player-facing text. The Burst half is `Jobs/TimestampProbeJob.cs`.
4. **H-1 visibility → both**: a DebugScreen "Pool misses — data | sect | save buffers" row, and a per-phase
   "Pool misses" block in the benchmark report's Pipeline section (miss deltas per pool beside chunks generated vs
   loaded; misses per generated chunk is a phase-wide ratio, not a per-chunk attribution).

Corrections to the packet found while executing: `SerializationBufferPool` is a static class, so its counter is
annotated and zeroed in the existing `DomainReset`; `GC.GetTotalAllocatedBytes` does **not compile** under the
project's .NET Framework API level (a build-time answer, so the probe omits it rather than "failing to compile in
Master"); and the generated/loaded split cannot come from `AdmittedTicks` (stamped for disk loads too) — it is
counted at the two `StampPopulated` arms (new `generated` parameter), pinned by Pipeline Backpressure **B25**.
The phase deltas are taken only when both readings come from the same `ChunkPoolManager` instance.

The probe always finishes: a check that throws is reported as `THREW <type>: <message>` — itself an answer
about the build — and the remaining checks still run; recorders are released even when the run is cut short.
Proven in Play mode by two injected throws (one per check kind) and a mid-run `DestroyImmediate`.

### 7.2 PM-1 execution record (2026-10-03; confirmed in an IL2CPP Master build)

**Shipped** (`Assets/Scripts/Diagnostics/`, namespace `Diagnostics`): `PerfTier`, `PerfSlot` (the ten
`WorldFrameProfiler` phases, same values and names), `PerfFrame`, `PerfFrameRing` (2 048 frames, `Allocator.Persistent`;
slot columns allocated at Systems and up, freed below), `PerfWindowSummary` + `PerfWindowStats` (exact nearest-rank
selection), and `PerfStore` (static; one `DomainReset`; native memory freed on `Application.quitting` and before an
Editor assembly reload, after which commits are ignored rather than reallocating). `WorldFrameProfiler` is a facade:
`Enabled` is `PerfStore.ForceSlots`, OR'ed with the tier, so a harness clearing it never disables a tier the player
chose. `PerformanceMonitor` commits each frame's raw wall/CPU ticks and applies `Settings.perfMonitorTier` in
`OnEnable` (which also runs after a Play-mode script reload) and live through `OnSettingChanged`; the tier is in `OverlayBenchmarkSettingsFromDisk` (removed 2026-10-10). `/perf stats` prints worst,
p99, p50 and mean wall/CPU time over the ring, plus per-slot avg/p99/worst at Systems; `/perf tier` reads or sets the
tier through the setting.

**Decisions taken at plan review:**
1. **Slot-taking probe API with marker mirroring** (§3.1, §4.2) — the slot-less `Begin()` stays only for the facade.
2. **`PerfCounter` → PM-3**, where its producers land; no producer-less storage built.
3. **Operability → dropdown and console**: all four tiers in the Settings dropdown (interim Frame/Capture behavior
   stated in the tooltip; acceptable because PM-2…PM-7 are planned before the next player build), plus
   `/perf stats` / `/perf tier`.
4. **Exact percentiles** over the ring instead of histograms (§4.1); phase-wide percentiles → PM-6.
5. **Overhead measured in the Editor**; the Master A/B → PM-3.

**Verification.** `Validate Performance Monitor` (new, registered — `ExpectedSuiteCount` 31 → 32) 10/10, with B6
(frame boundary) and B9 (facade bit-identity) proven red by mutation — dropping the commit-time clear, and publishing
through a `float` — and green again on restore. Pipeline Backpressure 25/25 (B22 included; it now pins the Basic tier,
since a Systems+ tier keeps the facade's probes recording with `Enabled` off — red at a leftover Systems tier before
that, green after), Command Console 57/57.
Editor micro-benchmark (Mono, three runs): probe pair 2.8 ns inactive, ≈ 46 ns active (≈ 65 ns with the marker,
which Master compiles out); `CommitFrame` ≈ 35–40 ns at Basic, ≈ 160–210 ns at Systems — ≈ 1.2 µs/frame at Systems
with today's ten probe sites, against the 50 µs budget. Allocation-free: every case ran 10⁶ calls with 0 GC
collections and 0 KB heap growth, where a 16 B-per-call control triggers a collection.
Editor Play-mode smoke (fresh world, `/tp` to stream terrain, driven through the console's `CommandEngine`): `/perf
stats` reported worst ≥ p99 ≥ p50 at Basic and all ten slots at Systems; six back-to-back `/perf tier` switches (slot
columns freed and reallocated each time) logged nothing; a 855-frame GC.Alloc call-stack capture attributed 651.7 KB on
the main thread, none of it to `PerfStore`, `PerformanceMonitor` or the facade (98.8 % was the UI band composite's
pass recording, filed as `ES-28` in the scaling roadmap); leaving Play mode ran the shutdown hook
(ring freed) with no native-collection leak warning. `PerformanceMonitor` lives in the World scene only, so the main menu
records no frames; the tier is applied when the World scene's monitor is enabled.

**Corrections found while planning:** §4.1's slot list was not in the facade's order (`Tick` = 0, not
`GenerationProcess`); §4.2's slot-less `Begin()` could not emit a marker; §5's "pin before PM-1" baseline already
existed as B22 but tested thresholds; §7's "B34-style reset sweep" named no existing mechanism.

### 7.3 PM-2 execution record (2026-10-04; confirmed in an IL2CPP Master build)

**Shipped** (`Assets/Scripts/Diagnostics/`): the 40 B `PerfFrame` with `PerfGcState` (§4.1); `PerfFrameReadings`, the
raw end-of-frame readings `CommitFrame` now takes; `PerfFrameField` + `PerfFrameRing.CopyField`, so statistics skip frames
without a value; `PerfFrameTimingSource` (§4.3); `PerfHitchDetector` + `PerfHitchRecord` (§4.5). `PerfWindowSummary`'s
fields lost their `Ms` suffix (`Max`, `Mean`, `P50`, `P99`) because they now also carry kilobytes. `PerformanceMonitor`
passes the heap size and collection count and calls `PerfStore.SampleFrameTiming` after each commit; `Settings` gains
the settings-file fields `perfHitchMinMs` and `perfHitchMedianFactor`, applied with the tier. `/perf stats` gains GC,
GPU/render-thread/present-wait and hitch lines, `/perf hitches` lists the records, and an automated run logs both
readouts to Player.log when it ends (`LaunchSession.TryQuitAfterRun`) — the only way to read the store from an
unattended player until PM-6's export.

**Decisions taken at plan review:**
1. **Frame timing on demand** — the tier records the frame-time counters; the project setting stays 0 (§4.3, §8 q4).
2. **Timestamp back-fill** — each timing goes into the row it describes, not the row being committed.
3. **GC at every tier**, Basic included (§3.2).
4. **Master player check in a dedicated build**, read from the run-end Player.log summary (chosen while executing,
   since nothing else in a player exposes the store). Not yet run.

**Verification.** `Validate Performance Monitor` 19/19, the nine new scenarios B11–B19 covering the GC readings and
statistics, frame-timing back-fill, Frame-tier resources, the hitch threshold, window, merge and retention, attribution,
and tier drop/shutdown. B12, B13 and B16 proven red by mutation — collection frames counted as measured, the back-fill
interval shifted by one tick, and a window copy broken only across the ring wrap (B16's no-wrap case stayed green) — and
green again on restore, the file checksum identical. Pipeline Backpressure 25/25, Command Console 57/57.
Editor micro-benchmark (Mono, three clean runs): `CommitFrame` ≈ 45–52 ns at Basic, ≈ 141–151 ns at Frame and
≈ 272–286 ns at Systems (PM-1: 160–210 ns, before the row grew from 12 to 40 B); `SampleFrameTiming` ≈ 100–125 ns; 0
collections and 0 KB over 10⁶ calls in every case. The Frame tier costs ≈ 0.3 µs/frame.
Editor Play-mode smoke (new world, seed 4242, terrain streamed by moving the player to (1500, 140, 1500)): an injected
80 ms sleep produced a hitch record one frame after the call, not GC-correlated; an injected `GC.Collect` produced a
GC-correlated record and one collection frame left out of the allocation statistics; 24 893 of 24 893 frame timings
matched a row; no frame shrank without a collection; at Systems the traversal's records attributed their worst frames
(e.g. a window of 25 slow frames led by `LightMerge` at 47.8 ms); six back-to-back tier switches logged nothing; a
946-frame GC.Alloc capture attributed 720 KB on the main thread, none of it to `PerfStore`, the detector, the timing
source or `PerformanceMonitor` (99 % was `ES-28`'s UI passes); leaving Play mode logged no leak.

**Review follow-ups (same day, after the smoke):** the GC baseline is reset when `PerformanceMonitor` enables, so a
world entered from the main menu does not open with a delta spanning the menu; frame timings are written on every read,
counted on the first; a GPU time of 0 is stored as "none" (§4.3). Proven by new B11/B13 checks, each red under its
mutation (the reset as a no-op, the zero stored as-is) and green on restore with the checksum identical. With
re-reads, `SampleFrameTiming` costs ≈ 163–178 ns (two runs, 0 collections, 0 KB). The Play-mode smoke above predates
these three changes and was not repeated.

**Corrections found while executing:** `Stopwatch` is not the `FrameTiming` clock (§4.3); §4.5's record "counters" and
"log lines" cannot exist before PM-3 and PM-7; §3.2's "8 B/frame" never matched the code.

**Master player confirmation (2026-10-04).** One `Windows - Production` build (IL2CPP **Master**, non-development,
D3D11; `enableFrameTimingStats: 0` unchanged), two unattended Flow A runs (`perf-benchmark` `references/player-capture.md`,
generation 200 m/s + loading 50 m/s, `-mc-mute -mc-quit`, both exit 0), read from the run-end Player.log summary:
- **Frame tier** (`BenchmarkRun_2026-10-04_14-26-25`): timings arrived with the setting off — GPU p50 0.90 / p99
  2.16 ms, render thread p50 0.58 ms, present wait p99 0.20 ms over the last 2 048 frames — so recording the two
  counters is enough (§8 q4). Timings landed in their rows on the `ProfilerUnsafeUtility.Timestamp` clock: 14 of 16
  held hitch records carry their worst frame's GPU time. 10 of the 16 are GC-correlated, so the collection flag is
  live; one (frame 83192) is present-wait-bound (35.6 ms), the split this tier exists to show.
- **Systems tier** (`…14-30-54`): every record names its costliest slots. The traversal hitches at 200 m/s are led by
  `Tick` (26–38 ms of 34–52 ms frames); the initial load's by `LightMerge` (up to 47.8 ms) and `Tick`. One 39 ms frame
  measured 1.3 ms of CPU — time outside the phases `PerformanceMonitor` brackets. *(Corrected at PM-3: this said PM-3's
  remainder targets that time, but the `World.Update` remainder lies inside the bracketed Update phase and cannot see it;
  §7.4.)*

The match *rate* was not measured: the summary prints received/matched only when no GPU time arrives, and two records
per run show "n/a" for their worst frame — one with a present wait but no GPU time (stored as none, §4.3), one with no
timing at all.

**Left for later phases:** the hitch thresholds are not in `OverlayBenchmarkSettingsFromDisk`, so a benchmark run always
detects with the defaults (33 ms / ×2.5) — PM-6, which owns benchmark comparability, decides whether they join the
overlay; printing received/matched alongside a reported GPU time (so the match rate is always visible) fits PM-5's HUD
or PM-6's export. *PM-6 added the thresholds to the overlay; since 2026-10-10 there is no overlay, and a run uses the
file's thresholds on every launch path.*

### 7.4 PM-3 execution record (2026-10-04; confirmed in an IL2CPP Master build)

**Shipped.** `PerfSlot` grows from 10 to 31 slots and the new `PerfCounter` holds 18 counters (§4.1); `PerfFrameRing` and
`PerfHitchDetector` gain integer counter columns and a per-row counter block, allocated with their slot storage.
`PerfStore` gains the remainder bracket (`BeginWorldFrame`, `EndWorldFrame`, `EndWorldFrameAt` for exact tests,
`IsWorldFrameOpen`, `NegativeRemainderFrames`) and the counter API (`SetGauge`, `SampleTotal`, `ResetCounters`,
`SummarizeCounter`). `World.cs`'s ten facade probes use the slotted API, eleven `World.Update` regions and the disk-load
continuation gained probes, and `World.SamplePerfCounters` records the counters; twenty scripts outside `World.Update`
carry one probe per update method. `ChunkJobArrayPool` and `MeshOutputPool` gain `TotalAllocated` and `PooledCount`
(exposed through `WorldJobManager`); `PerformanceMonitor.OnEnable` also resets the counters. `/perf stats` lists the slots
that recorded time (the rest on one line), warns on a negative remainder and prints every counter's avg/p99/max;
`/perf hitches` adds the worst frame's non-zero counters.

**Decisions taken at plan review:**
1. **The remainder is a slot** (`WorldUnattributed`), not a `PerfFrame` field (§4.1).
2. **Slot set:** the §4.1 list less `SkyAndTime`, plus `Player` and `Environment`; `ChunkLoadAnimation` unprobed.
3. **Hitch records carry per-row counters** (§4.5).
4. **Overhead gate in the Editor**, where it resolves nanoseconds without a ~9-minute Master build; the Master
   Basic-vs-Systems A/B rides the PM-3 confirmation build instead of a build of its own.

**Verification.** `Validate Performance Monitor` 23/23, with four new scenarios: B20 (remainder from injected ticks, slot
time outside the bracket ignored, overlap counted, the bracket's open state), B21 (gauge and per-frame semantics,
running-total baselines), B22 (counter columns and exact statistics) and B23 (hitch-record counter rows across the ring
wrap). Proven red by mutation and green again on restore, the file checksum identical: B20 by subtracting the whole slot
sum instead of the in-bracket part, and by dropping the commit's close of a bracket left open; B21 by zeroing gauges at
commit; B23 by shifting the copied counter rows one frame. `Validate All` 32/32 suites green (run before the
`IsWorldFrameOpen` guard below; Performance Monitor, Pipeline Backpressure and Command Console re-run green after it).
Editor micro-benchmark (Mono, three clean sequential runs): an inactive bracket 3–5 ns, an active probe pair ≈ 45 ns
(≈ 63 ns with its marker), one frame's counter samples ≈ 32 ns, `CommitFrame` at Systems ≈ 640–660 ns (PM-2: ≈ 280 ns with
ten slots and no counters), and a whole Systems frame — 40 probe pairs, the bracket, the counters and the commit —
≈ 3.4–3.6 µs against the 50 µs budget; 0 collections in every case. The first runs exposed `EndWorldFrame` reading the
clock before checking for an inactive start (24 ns); fixed before the runs quoted.
Editor Play-mode smoke (two runs, new worlds at seed 4242, Systems tier, player moved to voxel (1500, 140, 1500) and back,
chunk borders toggled both ways, an 80 ms sleep injected): every slot that runs each frame recorded time; the far
teleport's crossing frame was led by `Unload` (496 ms) with `ViewDistance` 16 ms and `OriginShift` 3.6 ms, the return's by
`Unload` (124 ms), and the return's disk loads put 100–125 ms into `DiskLoadApply` across a hitch window. The conditional
slots (`BorderToggle`, `OriginShift`, `Unload`, `DiskLoadApply`) recorded only in the frames that ran them; `DebugHud`
stays 0 while the debug screen is closed. Counters: the far teleport's frame counted 730 chunk-data, 7 section and 841
save-buffer pool misses; hitch records showed the queues building (generation queue 697, mesh queue 401). A 1 322-frame
GC.Alloc capture attributed 1 003.8 KB on the main thread, none of it to `PerfStore`, the probes, the counter sampler or
`PerformanceMonitor` (≈ 99 % was `ES-28`'s UI passes); leaving Play mode logged no leak.

**Overlap found by the smoke.** The first run counted 7 negative-remainder frames (down to −1.1 ms), all around the far
teleport: a read that finished before its `await` resumed synchronously inside the `World.Update` slot that started the
load, so `DiskLoadApply` overlapped it. Proven with a temporary counter in the second run: with `DiskLoadApply` skipped
while the bracket is open, 2 synchronous continuations occurred, 0 frames went negative, and `DiskLoadApply` still
recorded 260.6 ms from the asynchronous ones.

**Corrections found while planning:** §4.1's `SkyAndTime` named no system separate from `WorldTime`; §7.3 claimed PM-3's
remainder targets time outside `PerformanceMonitor`'s brackets; §2 and §4.2 missed `CheckViewDistance`'s third caller
(`OnSettingsChanged`) and the `UnloadChunks` and cloud rebuild nested inside it; §2 omitted `Player`/`PlayerInteraction`
from the untimed systems; the scaling roadmap's ES-0 asked for an `ApplyModifications` slot that already existed as
`Apply`.

**Master player confirmation (2026-10-04).** One `Windows - Production` build (IL2CPP **Master**, non-development,
D3D11), four unattended Flow A runs (generation 200 m/s + loading 50 m/s, `-mc-mute -mc-quit`, all exit 0) in the order
Basic, Systems, Systems, Basic, read from the reports and the run-end Player.log summaries:
- **Coverage works in Master.** Both Systems runs record every slot that runs each frame, `DiskLoadApply` on the loading
  pass's disk hits (p99 1.1–1.2 ms, and 32 ms in one startup hitch), and the remainder at no more than 0.01 ms; neither run
  logged a negative remainder. Hitch records name their costliest slots and carry their counters (one Systems run's 16
  records): the 200 m/s phase's (75–166 ms frames) are led by `Tick` (32–104 ms, modification queue up to 2 236) and
  `LightMerge` (15–36 ms); the ensure sweep's (35–39 ms) by `Tick` (27–36 ms); and a 135 ms frame after the 200 m/s phase
  by `Unload` at 96.5 ms, with 563 chunk-data and 583 save-buffer pool misses counted in it. No exception or leak was
  logged.
- **Overhead A/B: no frame-level difference.** Average CPU per phase was equal at the reports' 0.1 ms resolution on the
  50 m/s loading pass (1.1 ms in all four runs) and the ensure sweep (1.2–1.3 ms); the 200 m/s phase read 42.6 / 37.0 /
  35.5 / 35.4 ms, the first run an outlier that the closing Basic run does not repeat. Over each run's last 2 048 frames,
  CPU p50 read 0.77 / 0.78 ms at Basic and 0.79 / 0.80 ms at Systems — at most ≈ 0.02 ms (≈ 3 %), inside the ±1–5 % noise
  floor with two runs per tier, and the Systems side also carries PM-2's Frame-tier work (hitch detector, frame-time
  recorders). The Editor's ≈ 3.5 µs per Systems frame remains the precise figure.

**Left for later:**
- Time outside `PerformanceMonitor`'s brackets (wall − CPU − present wait) stays unattributed; reaching it means changing
  the monitor's brackets, which the standing decision rules out.
- Unity's own work — uGUI canvas rebuilds, the UI band passes, animation, the physics engine — has no slot.
- In the Editor and development builds the slot markers add ≈ 20 ns per probe pair, and an exception inside a probed
  region leaves its marker open; Master compiles the markers out.
- Counters are sampled by `World.Update`, so during the initial load they read 0.

### 7.5 PM-4 execution record (2026-10-05; confirmed in an IL2CPP Master build)

**Shipped.**
- **Job timer.** `Jobs/Data/JobBusyTimer` is a blittable timer pointing at a record of busy ticks and a link count. Its
  default value is untimed. It is a field on nine production jobs: the worm carver, the terrain job (timed per
  column), the cave filter, the active-voxel scan, the mesh job, its post-process, the lighting job, `FluidTickJob` and
  `FluidEmitterScanJob`. The frozen legacy generation job takes none, so its jobs record latency only.
- **Record pool.** `Diagnostics/PerfJobTimingPool` holds 2 048 records of 16 B, allocated on the first rent. A job
  scheduled while every record is rented runs untimed and is counted.
- **Wiring.** `GenerationJobData`, `MeshingJobData` and `LightingJobData` carry their record and schedule timestamp.
  `IChunkGenerator.ScheduleGeneration` takes an optional timer, and `EditorChunkPipelineRunner` passes one through.
  `WorldJobManager` rents a record at its three schedule sites while slots record, and reads and returns it at its
  three removal sites. Below Systems, with no record rented, a removal skips the job lookup entirely. It keeps running
  totals per job type (completed, latency, busy, links) and frees the pool in `Dispose`, after completing every job. A generation schedule that throws keeps its record, because part of its chain
  may already be running.
- **Fluids.** `World.TickChunksParallel` rents a record per scheduled chunk job through `WorldJobManager.BeginJobTiming`
  and passes the timer through `FluidBurstTicker.ScheduleFluids`. Each job is recorded once its chunk's drain has consumed
  it. A chunk that scheduled no job, and any record a throw left unconsumed, is returned unrecorded
  (`CancelJobTiming`). `FluidEmitterScanner` keeps a one-record pool of its own, freed in its `Dispose` after its scan
  completes and before that method's not-allocated early return. It reports each read scan to the scanned world's
  `WorldJobManager.RecordJobCompletion`.
- **Store.** `PerfStore` gains `PerfJobType` (five types), per-type `PerfSampleRing`s of the newest 1 024 jobs' latency and busy time
  (held at Systems and up, emptied by `ResetCounters`), plus `RecordJob`, `SummarizeJobLatencyMs`/`SummarizeJobBusyMs`,
  `SumCounter`, `SumCounterFramesWallMs` and `WorkerUtilization`.
- **Counters.** `PerfCounter` grows from 18 to 46:
  - the gauge `IoInFlight`;
  - per-frame completed / latency µs / busy µs for each of the five job types, and `UntimedJobs`;
  - `DiskLoadHits`, `DiskLoadMisses`, `DiskReadUs`, `DeserializeUs`, `DiskLoadBytes`, `DiskSaves`, `SerializeUs`,
    `DiskWriteUs`, `DiskSaveBytes`, `IoQueueWaitUs` and `IoBackgroundOps` (the operations that waited for a pool
    thread, so the average wait excludes sync and retry saves).
- **Disk I/O.** The totals live in `Serialization/StorageIoStats`. Read and deserialize time are taken in the background
  body of `LoadChunkAsync`. Serialize and write time are taken in `SerializeWithInjection` and `WriteToRegion`, which
  the sync, async and retry save paths all share.
- **Readout.** `/perf stats` adds worker utilization, each type's per-job p50/p99/worst latency and busy time, and disk
  operations, MB/s and average milliseconds per operation. The run-end Player.log summary carries the same lines.
- **Benchmarks.** `PerfStore Overhead` gains the main-thread bookkeeping of one job, and the new
  `Minecraft Clone/Benchmarks/Job Timing Overhead` A/Bs a generation chain with and without the timer.

**Decisions taken at plan review:**
1. **Busy accumulation inside the job** rather than a begin/end pair in its output (§4.4). Utilization is exact for
   the parallel terrain job, and the default timer leaves the editor and benchmark callers unchanged.
2. **Per-frame counters plus per-job sample rings**, for exact per-job percentiles beside the hitch-record counters.
3. **Compression ratio deferred.** Compressed bytes and serialize time only; raw byte counts belong with `ES-26`'s
   rewrite of the section writer.
4. **A dedicated IL2CPP Master build** confirms PM-4 before PM-5 starts.

**Verification.**
- **Suite.** `Validate Performance Monitor` 30/30, with seven new scenarios:
  - B24: timing pool rent, exhaustion and return;
  - B25: a real generation chain counts every link — 257 with caves off, 259 with caves on;
  - B26: sample-ring retention;
  - B27: job-sample routing and tier gating;
  - B28: a real save, a load hit and a load miss counted exactly — three background operations — nothing below Systems,
    in-flight balanced;
  - B29: utilization and window sums, exact, over all five types' busy time;
  - B30: a real fluid tick job and a real fluid sound scan each time one link; untimed writes nothing.
- **Prove-red.** Each of these went red under its mutation, and green again on restore with the file checksum
  identical:
  - B25: the scan job's timer unwired (256 / 258 links);
  - B26: the ring wrapping one slot early;
  - B28: `Stamp` ignoring the tier, misses counted as hits, and the background-operation count dropped;
  - B29: wall time summed over every frame, and one job type (meshing, then fluids) dropped from utilization;
  - B30: the ticker's timer unwired (0 links).
- **Validate All:** 32/32 suites, 800 baselines. The fluid parallel-determinism check (9 chunks × 6 concurrent rounds) stays
  byte-identical to its serial baselines.
- **Editor micro-benchmark** (Mono, three sequential runs on a machine under other load, so treat the figures as
  indicative): a whole Systems frame costs 3.6–4.1 µs (PM-3: 3.4–3.6 µs, with 18 counters); `CommitFrame` at Systems
  744–1 591 ns (the high figure from one noisy run); one job's main-thread bookkeeping 74–85 ns; 0 collections in every
  case. The generation chain timed against untimed (best of 5 rounds × 100 chunks, three runs) differed by −90, +54 and
  +61 µs on ≈ 10 ms per chunk, so the worker-side cost of the timer is below that noise.
- **Editor Play-mode smoke** (new world, seed 4242, Systems tier, player moved to voxel (1500, 140, 1500) and back):
  - Every job's links matched its chain exactly: 259.000 per generation job, 1.000 per lighting job and 2.000 per mesh
    job, over 729, 2 312 and 1 773 jobs; none ran untimed.
  - The outbound trip counted 729 load misses and 841 saves; the return trip 729 hits and no misses, at an average of
    0.21 ms read, 0.75 ms deserialize, 0.63 ms serialize, 0.91 ms write and 0.10 ms ThreadPool wait.
  - The return's crossing hitch record (191.8 ms, led by `Unload` at 138.8 ms) carried 761 operations in flight, 725
    saves and 455 ms of serialization in that one frame.
  - Utilization read 4.6 % of 15 job threads over the return window.
  - Six back-to-back tier switches logged nothing, and leaving Play mode logged no leak.
  - Generation busy time read p50 31 ms per chunk. In the Editor, with many chunks sharing contended threads, that is
    not a cost figure; the Master build gives it.
- **Fluid Play-mode smoke** (a second new world at seed 4242, five floating water sources placed near the player):
  - 459 fluid tick jobs and 40 fluid sound scans, each at exactly 1.000 link per job, none untimed.
  - Fluid jobs read p50 0.13 ms busy and 1.6 ms latency. Sound scans read p50 0.02 ms busy, with latency p50 4.7 ms and
    p99 32 ms; the scan is consumed on a later frame.
  - Leaving Play mode logged no error or leak.

**Left for later:**
- Busy time is charged to the frame in which the job's result is consumed. One frame's utilization can therefore
  exceed 100 %, and only the window figure is reported.
- Utilization covers the five timed job types only. The cloud pattern job (rare, completed where it is scheduled) and
  Unity's own jobs are not counted, so it is the engine's share of the workers, not their total load.
- No per-job split of latency into queue wait, execution and completion lag; the v2 swimlanes below need begin/end
  stamps for that.
- Jobs completed during the initial load reach the per-job samples but not the counters, whose first sample takes their
  totals as its baseline.
- The 2 048-record pool timed every job at view distance 10; larger view distances are unmeasured. A full pool runs jobs
  untimed, counts them in `UntimedJobs`, and `/perf stats` warns that utilization reads low.

**Master player confirmation (2026-10-05).** One `Windows - Production` build (IL2CPP **Master**, non-development, D3D11;
Burst AOT, so the timer's `Interlocked` writes through a raw pointer compile in all nine jobs), six unattended Flow A runs
(generation 200 m/s + loading 50 m/s, `-mc-mute -mc-quit`) in the order Basic, Systems, Systems, Basic, Basic, Systems —
all exit 0, no exception logged — read from the reports and the run-end Player.log summaries:
- **Every job type records in Master.** Over each Systems run's last 2 048 frames (the ≈ 4 s after the loading pass),
  with no job untimed: per-job busy p50 — generation 12.4–12.8 ms (p99 ≈ 23 ms), lighting 0.36–0.37 ms, meshing
  1.36–1.37 ms, fluid tick 0.26–0.28 ms, fluid sound scan under 0.01 ms; latency p50 — generation 49–52 ms, lighting
  5.3–5.6 ms, meshing 4.6–5.0 ms, fluid tick 9.0–9.7 ms, sound scan 1.4–1.7 ms (p99 79–92 ms, worst 351–379 ms, for a
  scan that works for under 0.1 ms). Generation's busy time fits the roadmap's measured saturation throughput: ≈ 650
  chunks/s across 15 job threads is ≈ 23 ms of worker time per chunk. Utilization read 1.0–1.3 % over that quiet window,
  and the disk lines 351–378 load hits at 0.23–0.30 ms read, 0.04 ms deserialize and 0.02 ms ThreadPool wait.
- **The 200 m/s `Tick` hitches are the fluid tick.** All three Systems runs' hitch records are mostly led by `Tick` (31
  records), and their size tracks the tick's fluid job count: every record with more than 200 fluid chunk jobs (seven,
  222–282 jobs) had a 46–160 ms `Tick` and 170–399 ms of fluid worker time, and 26–36 ms `Tick` hitches had 73–135 jobs.
  `Tick` runs well above that worker time spread over 15 threads (110 ms against ≈ 27 ms in one record), so the
  per-chunk main-thread prepare (snapshot and neighbor-halo fill) and drain look like the larger share *(inferred — no
  slot splits them yet)*. A few `Tick` hitches (26–38 ms) had 7–14 fluid jobs and under 1 ms of fluid worker time, so
  other tick work contributes too.
- **Overhead A/B: no frame-level difference.** Average CPU per phase, Basic against Systems: the 200 m/s phase 39.6 / 39.9
  / 39.0 ms against 39.3 / 39.2 / 39.3 ms, the ensure sweep 1.3 ms in all six, the 50 m/s loading pass 1.3 / 1.3 / 1.3
  against 1.3 / 1.2 / 1.2 ms. *Unexplained:* the run-end window's CPU p50 read **higher** at Basic (1.19 / 1.11 / 1.12 ms)
  than at Systems (0.82 / 0.81 / 0.97 ms), where PM-3's runs read 0.77–0.80 ms at both. Basic runs strictly less PM-4
  code and the phase averages agree, so this is not PM-4 overhead; whether it is a tier-dependent measurement effect (the
  Frame tier's frame-time recorders) or machine load is open.

**Corrections found while planning:** §4.4's "long pair in its output struct" became a shared busy-time record. §7's
PM-4 row lists PM-3 as a dependency, since its counters build on PM-3's counter API. "ThreadPool depth" is measured by
the engine's own in-flight count and submission wait, not a ThreadPool API.

### 7.6 PM-8 execution record (2026-10-05; confirmed in an IL2CPP Master build)

**Goal.** Say where the `Tick` slot's main-thread time goes. PM-4's Master runs attributed the 200 m/s `Tick` hitches to
the fluid tick's fan-out (§7.5), but `Tick` exceeded the fluid worker time spread over 15 job threads by 3.5–8× in the
seven largest records, and a few `Tick` hitches had almost no fluid work at all.

**Shipped.**
- **Ticker split.** `FluidBurstTicker`'s prepare is public as `TryPrepare`, beside a new `Schedule`; `ScheduleFluids`
  stays as the pair for the suites. `SnapshotMaps` reports the full-chunk voxel maps the last prepare copied: the center
  plus each populated neighbor. `FillJobVoxelMap` copies every section whatever the Y-band, so each map is 128 KB.
- **Totals.** `Diagnostics/PerfTickTotals` holds running totals of the tick's six parts — the active-chunk list
  (`ProcessTickUpdates`), the per-chunk prepare, scheduling (ticker and timing-record rent, `Schedule`,
  `ScheduleBatchedJobs`), the `Complete()` wait, the grass tick and the fluid replay — with fluid chunks prepared, maps
  copied and grass voxels ticked. `World` owns one instance (no statics); `Chunk.DrainTick` takes it by `ref` to time
  grass and replay apart. Probes take a start from `PerfStore.Begin()` and chain one timestamp per boundary
  (`PerfTickTotals.Lap`), so below Systems each costs one static read and adds nothing.
- **Counters.** `PerfCounter` grows from 46 to 56 per-frame counts: `TickListUs`, `TickPrepareUs`, `TickScheduleUs`,
  `TickWaitUs`, `TickGrassUs`, `TickFluidReplayUs`, `TickFluidChunks`, `TickSnapshotKb`, `TickGrassVoxels` and
  `FluidTickerPoolMisses` (the ticker pool's held + rented + destroyed count; a new ticker allocates ≈ 1.4 MB of zeroed
  native scratch on its first prepare, inside the prepare part). `World.SampleTickCounters` converts ticks to
  microseconds at sample time, so truncation does not add up per chunk.
- **Readout.** `/perf stats` adds a "Tick split" block — each part's avg / p99 / worst ms and the average "Other" the
  parts leave (`Tick`'s mean minus the parts' means; exact for averages only). A hitch record whose top slot is `Tick`
  adds "Tick led by <part> <ms>". The run-end Player.log summary carries both.

**Decisions taken at plan review:**
1. **Per-frame counters**, not nested detail slots — the remainder and the `Tick` facade phase stay exact, and hitch
   records carry them unchanged.
2. **Per-frame sums** of the prepare; a per-chunk ring only if the sums had pointed at a few heavy chunks.
3. **Pool misses and grass voxels added** beyond the packet's two counts, to separate first-use allocation from copying
   and to explain the `Tick` hitches with little fluid work.
4. **Master confirmation with three Systems runs, no Basic-vs-Systems A/B** — the probes cost four `Stopwatch` reads per
   fluid chunk.

**Verification.**
- **Suite.** `Validate Performance Monitor` 31/31. New B31 runs the production `World.TickChunksParallel` over one
  seeded chunk (a fluid row, one grass voxel, one populated neighbor) on the Behavior suite's stub world: one fluid
  chunk, two maps and one grass voxel counted exactly; prepare, schedule, wait, grass and replay each timed and summing
  to no more than the call; the emitted modifications equal the serial `RunFluids` oracle's, in order; every counter
  carries its own total after a sample and commit; a second tick misses the pool no more; below Systems nothing is added.
- **Prove-red.** Each went red on its own check, and green again on restore with the file checksum identical: the
  prepare probe unwired, the counts left ungated below Systems, the center map left out of `SnapshotMaps`, and the grass
  and replay counters swapped.
- **Validate All:** 32/32 suites, 801 baselines.
- **Editor Play-mode smoke** (new world, seed 4242, Systems tier): every part recorded, and a 15 × 15-chunk grid of
  floating water sources led the hitch records — "Tick led by Prepare" in each — at 233 fluid chunks and 268 416 KB
  copied (233 × 9 × 128 KB). On warm ticks Prepare took 26.9–33.3 ms of a 29.9–36.6 ms `Tick`; the first such tick had
  133 pool misses and a 79.7 ms Prepare. Leaving Play mode logged no error or leak.

**Master player confirmation (2026-10-05).** One `Windows - Production` build (IL2CPP **Master**, non-development, D3D11),
three unattended Flow A runs (generation 200 m/s + loading 50 m/s, `-mc-set perfMonitorTier=Systems`, `-mc-mute
-mc-quit`), all exit 0, read from the run-end Player.log summaries:
- **The parts explain the slot.** 22 of the 48 held hitch records were `Tick`-led (5 / 8 / 9). In every one the six
  parts summed to within 0.13–0.31 ms of `Tick`. They were led by Prepare in 8, Wait in 6 and Grass in 8.
- **Large fluid ticks are prepare plus wait, not prepare alone.** The seven records with 229–273 fluid chunks had a
  51–87 ms `Tick`: Prepare 26.0–39.2 ms (36–59 %), Wait 12.5–45.6 ms (24–53 %), Replay 4.2–10.0 ms. They copied 258–307 MB
  of voxel maps per tick, 8.0–10.1 GB/s, ≈ 0.11–0.14 ms per chunk, with 0–28 pool misses. The wait ran 1.1–1.9× the fluid
  busy time spread over 15 threads, so the fluid jobs share the workers with the phase's generation and lighting
  *(inferred — no per-worker timeline)*. The hypothesis that the copy dominates holds only partly: it is the largest or
  second-largest part.
- **Grass is the third source.** Eight records were led by the managed grass tick at 18.7–27.5 ms, with 44 176–69 012
  active grass voxels (≈ 0.4 µs each). Two of them had 9–14 fluid chunks and under 2 ms of fluid worker time — the
  few-fluid `Tick` hitches §7.5 could not explain.
- **Phase averages.** Ensure sweep 1.3 / 1.3 / 1.2 ms and loading pass 1.2 / 1.2 / 1.3 ms average CPU, as in PM-4.
  *Unexplained:* the 200 m/s phase read 21.4 / 20.6 / 19.0 ms, against PM-4's 39.2–39.3 ms at Systems on the same
  arguments. PM-8 only adds work, so this is not a PM-8 effect; machine load during either set is the likeliest cause.

**Left for later:**
- No `ES-*` item owns the fixes yet. The candidates: the reserved gather levers in
  [`BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`](../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md) (banded neighbor fills,
  one snapshot per unique chunk per tick) for the prepare; the worker contention behind the wait; and the Grass-Burst
  follow-on there for the grass tick.
- The fluid ticker pool is never pruned. It keeps its peak count × ≈ 1.4 MB of native scratch — ≈ 380 MB after a
  273-chunk tick — until the world is destroyed.
- The prepare is a per-frame sum and includes first-use allocation; the pool-miss count says how many, not how long.
- B31 drives `TickChunksParallel` directly, so the list part is checked only in the smoke and the Master runs.

**Corrections found while planning:** the packet's verified facts cited `305604fb` plus the uncommitted PM-4 work, which
has since landed as `3ea2fc09`; its line references still held.

### 7.7 PM-6 execution record (2026-10-06; confirmed in an IL2CPP Master build)

**Goal.** Benchmark reports carry exact per-frame statistics over every frame of each phase, and the Capture tier writes
the store to disk, so an unattended player run is read from files rather than from the Player.log summary.

**Shipped** (as built in §4.7):
- **Final rows and phase statistics.** `PerfStore.RowFinalAge`, `PerfStore.PhaseRecorder`, `PerfPhaseRecorder` and
  `PerfPhaseSummary`; `PerfWindowStats` gains a native-array overload over the same selection; `PerfFrameField.Count`.
- **Report.** `PhaseMetrics.FrameStats` / `HasFrameStats` / `HitchFrames`; the Performance Monitor block, the overall
  frame-health lines and a Frame Health table per group in `BenchmarkReportGenerator`; the hitch thresholds in
  `OverlayBenchmarkSettingsFromDisk` (removed 2026-10-10).
- **Tier floor.** `PerfStore.TierFloor`: the tier in force is the one set, raised to the floor. `BenchmarkController`
  raises it to Frame before its settle wait and clears it in `OnDestroy`.
- **Export.** `PerfSessionExporter` and `PerfExportBlock`; `PerfStore.ConfigureExport` / `Exporter`, opened and closed
  with the tier, the folder (`PerformanceMonitor` sets `PerfLogs` while enabled) and shutdown; `PerfFrameRing.CopySlotRow`
  / `CopyCounterRow`; the settings-file fields `perfCapturePartMb` / `perfCaptureSessionMb`; a Capture line in
  `/perf stats`; the Capture tooltip and `PerfTier.Capture` describe the files.
- **`StringBuilderFormat.AppendInteger`.** `StringBuilder.Append(int/long)` builds a string on Unity's runtime (1–2
  collections and ≈ 24 MB over 10⁶ calls in the Editor), so `AppendFixed`, `AppendIntPadded`, `AppendBytes` and
  `AppendElapsedTime` allocated despite their documented contract. Their digits are now written as characters, with
  identical output; the HUD and debug-screen paths that use them stop allocating too.
- **Benchmark.** `Minecraft Clone/Benchmarks/Capture Export Overhead` writes a 200 000-frame session with every slot and
  counter set, beside a 16 B-per-frame control.

**Decisions taken at plan review:**
1. **Benchmarks raise the tier to Frame**, keeping a higher one, instead of forcing Capture (§3.2).
2. **The drain stamp stays out of PM-6** — it is ES-0's own remaining item (roadmap ES-0).
3. **Full-rate session files, capped**: a new part every 64 MB, writing stops at 1 GB, nothing deleted.
4. **Frame-level phase statistics only**; per-slot statistics per phase live in the session file.

**Verification.**
- **Suite.** `Validate Performance Monitor` 37/37, with six new scenarios:
  - B32: rows reach a phase only once final, a timing that arrives at age 16 included, routed by frame index; `Close`
    and the domain reset;
  - B33: 5 000 frames — more than the ring — against a sorted copy of every frame;
  - B34: the tier floor raises, keeps and restores;
  - B35: a session's CSV parsed back cell by cell, header count included, only frames since it opened;
  - B36: a hitch file's name, summary line, 151 rows oldest first and marks;
  - B37: parts each with a header, the session cap counting what it drops, and a write failure that stops cleanly and
    counts the hitch record it loses.
- **Prove-red.** Each went red under its mutation and green again with the file checksum identical: B32 — rows final
  one frame early, and a boundary row routed to the earlier phase; B33 — samples capped at the ring length; B34 — the
  floor ignored; B35 — a column dropped, NaN written as a number, and the newest rows not written on close (B37 red too);
  B36 — hitch rows shifted by one; B37 — the cap ignored. The first B32 draft took its ages from `RowFinalAge` itself and
  survived the first mutation; it now derives them from the back-fill limit B13 pins.
- **Validate All:** 32/32 suites, 807 baselines.
- **Allocation** (`Capture Export Overhead`, Editor Mono): the first run gave 26 collections over 200 000 rows — the
  `Append(number)` garbage above, from the writer thread. After `AppendInteger`: 0 collections and 276 KB of heap growth
  (≈ 1.4 B per row, less than any object), against 7.9 MB for the control; `CommitFrame` at Capture ≈ 1.1–1.2 µs; the
  writer ≈ 121 000 rows/s.
- **Editor benchmark smoke** (6 s phases, 50 and 200 m/s, loading 100 m/s): every Frame Health row has worst ≥ p99 ≥ p50,
  frames × mean wall time matches each phase's duration (109 frames over 6 s at 200 m/s), and the one-frame transition
  phase has a row where the averaged columns read 0.
- **Editor Capture smoke:** a Capture benchmark, then six tier switches. Four session files (the run's and one per return
  to Capture) and 46 hitch files, all 96 columns, no frame gap inside a session, no row dropped; leaving Play mode closed
  the last session and logged no error or leak.

**Review follow-ups (same day, after the Master runs):** a lost hitch window is counted (`HitchesDropped`, warned by
`/perf stats`) — a full block pool, the size cap or a failure dropped it silently; and the report prints the hitch
thresholds the detector applies (`PerfHitchDetector.ResolveMinMs` / `ResolveMedianFactor`), not invalid settings-file
values it replaced. B37's new check went red with the count removed and green again on restore, the checksum identical.
The Master runs predate both and were not repeated.

**Master player confirmation (2026-10-06).** One `Windows - Production` build (IL2CPP **Master**, non-development,
D3D11), four unattended Flow A runs (generation 200 m/s + loading 50 m/s, `-mc-mute -mc-quit`) in the order Frame,
Capture, Frame, Capture — the first and third at the benchmark's own floor, the others with `-mc-set
perfMonitorTier=Capture` — all exit 0, no exception or export warning logged:
- **The report reads the store.** Every run's tables say "Monitor detail: Frame" or "Capture", and each phase has its
  frame count, exact percentiles and GPU p99 (12–18 ms at 200 m/s). The 200 m/s phase: 690 / 1 091 / 1 052 / 1 066 frames,
  wall p99 78–93 ms, worst 144–154 ms, 104–110 collections and 111–160 hitch frames, where its averaged Peak Wall column
  reads 56–59 ms.
- **The files are complete in Master.** The Capture runs wrote one session file each — 145 997 and 92 513 rows, every
  frame from 3 to the last, 96 columns, no gap — so the quit-time close wrote the newest rows, plus 20 and 22 hitch files,
  none malformed. 7 % and 20 % of rows have no GPU time: timings that never arrived, the larger share in the fourth run,
  whose GPU p99 tripled in the ensure sweep (outside load, *inferred*).
- **No attributable cost.** Loading-pass CPU p50 read 0.6 / 1.1 ms at Frame and 0.9 / 0.8 ms at Capture; 200 m/s average
  CPU 45.4 / 41.1 at Frame against 41.1 / 40.0 ms at Capture. The spread between the two Frame runs exceeds any Capture
  difference.

**Left for later:**
- Frame-level phase statistics only (decision 4); per-slot ones per phase come from the session file.
- The last frames of a hitch file and of a closed session can lack GPU time: rows are copied before their timings arrive.
- A hitch file or part file allocates when opened (a few hundred bytes, on the writer thread).
- No tool reads the session files yet; `Tools/Python/tabulate_tick_hitches.py` still reads Player.log. *(2026-10-10:
  `Tools/Python/summarize_perf_session.py` now does, per benchmark phase — ES-0's re-measurement.)*
- The received / matched frame-timing count beside a reported GPU time (§7.3) is still PM-5's.

**Corrections found while planning:** §3.2's "benchmark mode forces Capture" would have removed the per-run tier choice
the PM-3/PM-4 A/Bs used; §4.7's reuse of `BenchmarkEnvironment`'s MediaStore route cannot stream; the scaling roadmap's
ES-0 row sent "the rest" to PM-6, but the drain stamp was never in PM-6's scope.

### Extension roadmap

| Version | Extension                                                                                     |
|---------|-----------------------------------------------------------------------------------------------|
| **v2**  | Per-worker job timeline (swimlanes) from the PM-4 timestamps, viewable in the HUD or exported |
| **v2**  | Remote/live view over a local socket for a second-monitor dashboard (local only)              |
| **v3+** | Automatic regression detection across saved sessions — own design doc                         |

---

## 8. Open questions (PM-0 answers 1–4)

Answers 1–4 come from `EngineApiProbe_2026-10-03_13-52-20.log`: a `Windows - Production` player (IL2CPP
**Master**, non-development, D3D11, Burst AOT), frame-timing stats temporarily on, probe run from the main menu.

1. Which `ProfilerRecorder` counters are `Valid` in a Master IL2CPP player (memory, "GC Allocated In
   Frame", draw calls/batches/triangles)? The v2.1 doc says categories were invalid in Release; PM-0
   records the exact list. **Answered:** 82 handles — 33 markers, **49 counters, all 49 recorder-valid**, 29
   non-zero over 120 frames (the Editor has 196). Valid and reporting: `GC Used/Reserved Memory`, `System Used
   Memory`, `Total Used/Reserved Memory`, `App Resident/Committed Memory`, `CPU Total/Main Thread/Render
   Thread Frame Time`, `GPU Frame Time`, `Standard`/`SRP Batcher Draw Calls Count`, `SetPass Calls Count`,
   `Triangles`/`Vertices Count`, render-texture and buffer counts, and per-frame vertex/index buffer uploads.
   **`GC Allocated In Frame` does not exist in a Master player** (Editor-only). The frame-time counters were
   read with frame-timing stats on; whether they need that setting is open (see 4).
2. Does `GC.GetAllocatedBytesForCurrentThread()` exist and stay monotonic under IL2CPP/Boehm with the
   project's API compatibility level? If not, §4.3's fallback applies. **Answered: it compiles but is NOT live**
   — 0 B over a known 1 MB allocation, in the Master player as in Editor Mono; `GC.GetTotalAllocatedBytes` is
   not in the API surface at all. With `GC Allocated In Frame` also absent (1), **§4.3's fallback applies**:
   heap delta plus a per-frame collection flag. `GC.CollectionCount(0)` works (`MaxGeneration` 0).
3. Which timestamp source is Burst-callable for in-job execute timing (`ProfilerUnsafeUtility.Timestamp`
   or equivalent), and what does it cost? **Answered: `ProfilerUnsafeUtility.Timestamp`** — Burst path
   confirmed in the Master player, **11.4 ns/read** (100 ns ticks). Managed main-thread reads: `Stopwatch.GetTimestamp`
   15.1 ns, `ProfilerUnsafeUtility.Timestamp` 11.6 ns. (Editor: ~14.8 ns in Burst.)
4. Does `FrameTimingManager` report `gpuFrameTime` on D3D11 (the primary API) with frame-timing stats on,
   and what does enabling the stats cost? **Answered: yes** — `IsFeatureEnabled` true, 120 of 120 frames
   timed, `gpuFrameTime` p50 3.30 ms, `cpuRenderThreadFrameTime` p50 0.11 ms, `cpuMainThreadPresentWaitTime`
   p50 3.54 ms (main menu). **The setting turned out not to be the gate.** The same build with it **off**
   (`EngineApiProbe_2026-10-03_14-27-27.log`) reported `IsFeatureEnabled` **false**, yet `GetLatestTimings`
   returned timings for 116 of 120 frames (GPU p50 3.41 ms), and the `GPU`/`CPU Main Thread Frame Time` recorder
   counters reported in 115. The probe starts those recorders before it samples, so the likely mechanism is that
   recording the frame-time counters turns frame timing on for as long as they record. *Unverified:* reading
   `FrameTimingManager` before any recorder starts would settle it. **Cost: no measurable difference.** The A/B
   pair of benchmarks (stats on `BenchmarkRun_2026-10-03_14-00-16`, off `…14-34-59`) differs in both directions
   — avg CPU 1.3 vs 1.0 ms at 10 m/s, but 1.1 vs 1.6 ms at 50 m/s loading — which is run-to-run spread, not an
   attributable cost. The cost question is moot unless PM-2 needs the setting itself. (The Editor reports these
   times regardless, so it cannot answer either half.) *PM-2 (2026-10-04):* built on that reading — the Frame tier
   records the two frame-time counters and leaves the setting at 0, and a Master player with the setting off delivered
   timings that way (GPU p50 0.90 ms over a benchmark run, §7.3). Whether timings also arrive with no recorder at all
   was not tested; the design no longer depends on it.
5. Default hitch thresholds (33 ms / ×2.5 median) — to be tuned against real sessions after PM-2.
6. Should Tier 0 stay the default, or should Tier 1 ship on by default once its cost is measured?

---

## 9. Rejected alternatives

| Alternative                                                | Why rejected                                                                                                               | Date       |
|------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------|------------|
| Gate `PerformanceMonitor`'s sampling on HUD visibility     | Standing decision: the always-on history is what shows a hitch that happened while the HUD was closed                      | 2026-07-02 |
| `ProfilerRecorder`/`ProfilerMarker` as the core            | Invalid in Release builds per the v2.1 doc; markers readable only via the Profiler (§3.1)                                  | 2026-10-02 |
| A single on/off toggle                                     | Tier costs differ by orders of magnitude (§3.2)                                                                            | 2026-10-02 |
| Replacing `WorldFrameProfiler` outright                    | Breaks comparability of every FP/P-9/P-4 capture (§3.3)                                                                    | 2026-10-02 |
| `IDisposable` scope structs / delegates on hot-path probes | Avoidable overhead and allocation risk; explicit begin/end matches the proven `WorldFrameProfiler` pattern                 | 2026-10-02 |
| Uploading telemetry                                        | Out of scope; output stays local                                                                                           | 2026-10-02 |
| Log-scale histograms for the ring's window percentiles     | Bucket width (±5–12 %) adds error on top of run-to-run noise; exact selection over the ring costs nothing per frame (§4.1) | 2026-10-03 |

---

## Document History

* **v1.17** - `DiskSaveRawBytes` (2026-10-10, shipped with the roadmap's `ES-26`): each written save also counts its
  uncompressed size, and `/perf stats` and the run-end summary print the compression ratio. This closes PM-4's deferred
  decision 3 (§7.5, kept as recorded); §4.9's counter inventory is 57.

* **v1.16** - Benchmark settings (2026-10-10): `OverlayBenchmarkSettingsFromDisk` and the defaults pin it served are
  removed, since every automated run already inherited the whole settings file; reports list each value that differs
  from the defaults instead (a dated note under Option B, and markers where the shipped phases named the whitelist).
* **v1.15** - ES-0 follow-ups (2026-10-10): benchmark Frame Health rows carry their frame range (§4.7) and
  `summarize_perf_session.py` reads session files per phase (§7.7); §4.9 lists the once-per-launch time-to-stable stamp
  (`StartupTimeline`, the roadmap's ES-0).
* **v1.14** - `DT-4` taken out of PM-5 (2026-10-06): it ran ahead as its own item (the archived DT-4 execution
  record), and §4.7 notes the Editor-only TMP string allocation any new HUD panel will meet.
* **v1.13** - **PM-6 complete** (2026-10-06, §7.7; confirmed in an IL2CPP Master build): benchmark reports carry exact
  per-frame statistics over every frame of each phase (`PerfPhaseRecorder`, fed each row once final at
  `PerfStore.RowFinalAge`), benchmarks raise the tier to Frame through `PerfStore.TierFloor` instead of forcing Capture,
  and Capture streams a session CSV plus one file per hitch from a background writer (`PerfSessionExporter`).
  `StringBuilderFormat.AppendInteger` makes the helper's zero-allocation claim true. As-built notes in §3.2 and §4.7;
  §4.9 coverage map updated.
* **v1.12** - **PM-8 complete** (2026-10-05, §7.6; confirmed in an IL2CPP Master build): the `Tick` slot split into six
  timed parts plus fluid-chunk, snapshot-KB, grass-voxel and ticker-pool-miss counts (`PerfTickTotals`, ten new
  counters), a "Tick split" readout and "Tick led by" on hitch records. Master: the 200 m/s `Tick` hitches are the fluid
  prepare's voxel-map copies and the wait for the fluid jobs in comparable shares, plus the managed grass tick at 44–69 k
  active grass voxels. §4.9 coverage map updated; the §7.6 packet became its execution record.
* **v1.11** - Added **§4.9 Coverage map**: what each tier measures, every slot with its probe sites, the timed and
  untimed jobs, the I/O and counter coverage, the known gaps with their owners, and the commands that re-derive the map.
* **v1.10** - Added **PM-8** (behavior-tick breakdown, §7.6 execution packet) to measure the `Tick` slot's main-thread
  parts, after PM-4's Master runs showed the fluid tick's fan-out leading the 200 m/s hitches with most of its time
  outside the jobs.
* **v1.9** - **PM-4 complete** (2026-10-05): confirmed in an IL2CPP Master build — all five job types and the disk counters
  record in Master, the Basic-vs-Systems A/B shows no frame-level difference, and the 200 m/s `Tick` hitches are
  attributed to the fluid tick's fan-out (§7.5); status line, plan row and §7.5 heading flipped from "Master build check
  pending".
* **v1.8** - **PM-4 code landed** (2026-10-05, §7.5; IL2CPP Master build check pending): in-job busy-time accumulation
  through `JobBusyTimer` and `PerfJobTimingPool` for generation, lighting, meshing, the fluid tick and the fluid sound
  scan, per-type job counters and per-job sample rings, worker utilization, and chunk disk I/O counters
  (`StorageIoStats`). As-built note in §4.4; §7's PM-4 row lists PM-3 as a dependency.
* **v1.7** - **PM-3 complete** (2026-10-04): confirmed in an IL2CPP Master build — coverage, counters and hitch-record
  counters record in Master, and the Basic-vs-Systems A/B shows no frame-level difference (§7.4); status line, plan row
  and §7.4 heading flipped from "Master build check pending".
* **v1.6** - **PM-3 code landed** (2026-10-04, §7.4; Master build check and overhead A/B pending): 31 slots incl. the
  `WorldUnattributed` remainder as a slot, `PerfCounter` gauges and per-frame pool-miss counts with per-row hitch-record
  counters, `World.cs`'s probes on the slotted API. As-built notes in §3.1, §4.1, §4.2 and §4.5; §7.3's claim that PM-3's
  remainder reaches time outside `PerformanceMonitor`'s brackets corrected.
* **v1.5** - **PM-2 complete** (2026-10-04, §7.3; confirmed in an IL2CPP Master build): GC allocation + collection flag at
  every tier, frame timings back-filled by timestamp at Frame, hitch detector + records, `/perf hitches`, run-end
  Player.log summary. As-built notes in §3.2, §4.1, §4.3, §4.5 and §8 q4; §4.3 corrected — rows time their end on the
  `FrameTiming` clock (`ProfilerUnsafeUtility.Timestamp`), not `Stopwatch`. Review follow-ups in §7.3: GC baseline reset
  on monitor enable, timings re-written on every read, a zero GPU time stored as none.
* **v1.4** - **PM-1 confirmed** in an IL2CPP Master build (`2026-10-03 - RC 96-3 Performance (PM-1)`): status line, plan row
  and §7.2 heading flipped from "in-game check pending".
* **v1.3** - **PM-1 code landed** (2026-10-03, §7.2; in-game check pending): `PerfStore` + ring + exact window
  statistics, `WorldFrameProfiler` facade (bit-identical, pinned by the new suite's B9), Monitor Detail setting,
  `/perf stats` / `/perf tier`. Plan-review decisions recorded in place: slot-taking probes with marker mirroring
  (§3.1/§4.2), `PerfCounter` and the Master overhead A/B moved to PM-3, exact percentiles replace histograms with
  phase-wide percentiles moved to PM-6 (§4.1), all four tiers exposed (§3.2). §4.1's slot order corrected.
* **v1.2** - **PM-0 complete** (2026-10-03, two Master player builds): §8 q1–q4 answered — 49 counters valid in
  Master, but no `GC Allocated In Frame`; the per-thread GC counter is not live under IL2CPP, so §4.3's fallback
  applies; `ProfilerUnsafeUtility.Timestamp` is Burst-callable at 11.4 ns; with the project setting off, frame
  timings still arrived while the frame-time recorders were recording (§4.3 note, mechanism unverified), and
  the A/B shows no measurable cost. Both builds' benchmarks refuted the roadmap's H-1.
* **v1.1** - PM-0 code landed (§7.1 execution record): `/perf probe` + `EngineApiProbe` in a new
  `Scripts/Diagnostics/` folder, pool-miss counters with a DebugScreen row and per-phase report rows (B25);
  §8 q2/q3 gained their Editor-side answers. Master player run still pending.
* **v1.0** - Initial design: four-tier opt-in `PerfStore` over the kept always-on `PerformanceMonitor`,
  `WorldFrameProfiler` facade, GC/GPU/worker/I-O coverage, hitch snapshots, session export, category
  logger, `PM-0`…`PM-7`; PM-0 execution packet (§7.1) with the H-1 pool-miss counters; §4.3 corrected —
  only the `Windows - Profiler` build profile holds a frozen Player Settings copy.

---

**Last Updated:** 2026-10-10  
**Next Review:** when PM-5 starts (PM-6 complete 2026-10-06)
