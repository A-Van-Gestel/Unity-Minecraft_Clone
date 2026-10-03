# Performance Monitor & Logger Overhaul Design

**Version:** 1.3  
**Date:** 2026-10-02  
**Status:** In progress — PM-0 ✅ complete (2026-10-03, Master answers in §8); PM-1 ✅ code landed (2026-10-03, §7.2;
in-game check pending); PM-2…PM-7 not started. PM-0's answers reshaped PM-2/PM-4 (§8).  
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
  leftovers) is folded into PM-5.
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

| Area | State | Anchor |
|---|---|---|
| `PerformanceMonitor` | Always on by design: `Stopwatch` dual-hook phase times, wall time, GC alloc from `GC.GetTotalMemory` deltas, native/managed memory; history ring of 200 snapshots (10 s × 20 Hz) | `PerformanceMonitor.cs:26–377` |
| Smoothing | **Every recorded CPU/wall value is a 30-frame mean**; snapshots copy smoothed values at 20 Hz, so no individual frame is ever stored; the timer reset drops remainders, so real sampling falls below 20 Hz at low FPS | `:30, :109–136, :327–339` |
| GC allocation | 60-frame mean of `max(0, Δheap)` — **a collection frame records 0** and its allocation is lost; a 1 MB burst reads ≈17 KB; worker-thread allocation is charged to the main frame | `:31, :314–321` |
| GC collections | Session counts on the HUD only; Boehm reports `MaxGeneration = 0`; no per-frame flag, no duration, not in benchmark reports | `DebugScreen.cs:605–623` |
| Benchmark "Peak/Min" | Extremes of the smoothed 20 Hz snapshots — not frame peaks; no p99, worst-frame or hitch count | `BenchmarkMetricsCollector.cs:254–283` |
| `WorldFrameProfiler` | 10 `Stopwatch` slots inside `World.Update`; last frame only; enabled only by benchmark harnesses; no HUD | `Benchmarks/WorldFrameProfiler.cs:43–244` |
| Untimed regions in `World.Update` | `AdvanceWorldTime`, `BiomeTracker.Tick`, `ShiftOrigin`, `UpdateTeleportHold`, both `CheckViewDistance` sites (incl. `UnloadChunks`), border toggle, `HandleVisualization`, `DrainGenerationRequests`, `DrainFailedSaveRetries`, `ChunkPool.Update`, the CP-1 scan — and no unattributed remainder | `World.cs:2551–2619, 2637, 2950–2964` |
| Systems with no timing at all | Physics (`VoxelRigidbody`, only buried in FixedUpdate), audio directors, clouds, sky/day-night, UI/HUD (incl. `DebugScreen` itself), `VoxelVisualizer`, disk I/O latency/bytes/compression, ThreadPool depth, job worker time, GPU/render thread | grep |
| Pools | `ChunkPoolManager` counts on the HUD; **`ChunkJobArrayPool` and `MeshOutputPool` expose no counters** | — |
| Profiler APIs | No `ProfilerRecorder`/`FrameTimingManager` use; 3 `ProfilerMarker`s (Profiler-only); **`enableFrameTimingStats: 0`** in `ProjectSettings.asset:162` and live | — |
| Instrumentation variant drift | CP-1/LP-1/MP-1 probes are gated on `UNITY_INCLUDE_INSTRUMENTATION`, but comments say "dev/editor builds only"; the `Windows - Development` profile uses the default Release variant, so the probes are **compiled out of Development builds** while their "(dev)" HUD rows read 0 and the save-diagnostics toggle is shown but inert. Only `Windows - Profiler` (Checked variant) has them | `World.cs:1185, 1190, 3471`; build profiles |
| Logging | 327 runtime `Debug.Log*` sites (150 Log / 74 Warning / 103 Error); no central logger or `logMessageReceived` subscriber; tags mixed (system, method, class, backlog-ID, untagged); only 3 sites honor `enableDiagnosticLogs`, ~10 `enableWaterDiagnosticLogs`, save diagnostics double-gated (flag + `UNITY_INCLUDE_INSTRUMENTATION`) | census |
| Stack traces | Player settings: **Error = None, Assert/Warning/Log/Exception = ScriptOnly** — every `Log`/`Warning` in a player walks the managed stack (expensive in IL2CPP), while errors carry none (the reverse of the useful choice) | `ProjectSettings.asset:57` (live) |
| Settings hosting | `[SettingField(tab)]` + `DebugOnly`, `SubHeader`, `DisabledWhen`; live apply via `SettingsManager.OnSettingChanged`; the **DebugScreen** tab is player-facing; benchmark cold-launch only copies a whitelist (`OverlayBenchmarkSettingsFromDisk`) | `SettingsManager.cs:1003–1039, 1279–1305` |

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
slotted API when PM-3 re-touches those lines.

### 3.2 Opt-in model

#### Option A — One on/off toggle (rejected)

- ❌ The cost and data volume of "frame stats" and "full session capture" differ by orders of magnitude;
  one switch forces either too little or too much.

#### Option B — Four tiers, one setting ✅ **CHOSEN**

| Tier | Adds | Intended use |
|---|---|---|
| **0 — Basic** (default) | Today's always-on `PerformanceMonitor` **unchanged**, plus a raw per-frame wall/CPU ring (8 B/frame) feeding worst-frame/p99 | Every session; costs what today costs |
| **1 — Frame** | GC allocation per frame + collection flag, `FrameTimingManager` GPU/render-thread/present-wait, hitch detector + snapshots | "Why did that stutter?" |
| **2 — Systems** | Every timing slot and counter (§4.2), unattributed remainder, job/I-O counters | Attribution sessions, benchmark runs |
| **3 — Capture** | Session file streaming + hitch dumps to disk, log mirroring with frame numbers | Recorded investigations; benchmark mode forces this tier |

The tier is a `Settings` enum on the **DebugScreen** tab, applied live through `OnSettingChanged`.
Benchmark harnesses set it explicitly (as they already force `WorldFrameProfiler.Enabled`) and the
field is added to `OverlayBenchmarkSettingsFromDisk`, so captures stay comparable on cold and menu
launches alike. Logging levels (§4.6) are independent of the tier. *PM-1 (2026-10-03):* `Settings.perfMonitorTier`
("Monitor Detail") ships all four values, contiguous from 0, because the Settings dropdown maps option index to enum
value and the JSON stores the integer — a tier inserted later would shift saved values. Until PM-2/PM-6 land, Frame
records the same as Basic and Capture the same as Systems; the tooltip says so. `/perf tier` sets it from the console.

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
- **Counters** (`PerfCounter` enum: queue depths, in-flight jobs per type, resident chunks, pool
  in-use/peak per pool, I/O ops/bytes, ThreadPool depth) are gauges sampled once per frame into a
  second column set, plus `Interlocked`-updated cumulative counters for worker-thread producers.
  *Moved to PM-3 (2026-10-03),* which brings their first producers; PM-1 built no counter storage.
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

### 4.5 Hitch detection and snapshots

A frame is a hitch when `WallMs > max(absoluteMs, k × rolling median)` (defaults 33 ms and ×2.5,
settings-file tunable). On a hitch the store freezes the last *N* frames (default 120) and keeps
recording *M* more (default 30), then copies that window into a hitch record: frame rows, slot columns,
counters, GC flag, the top three slots by cost, and the log lines emitted in the window (§4.6). The HUD
lists recent hitches; at Capture tier each record is queued for export. Records are pooled; at most
*H* (default 16) are retained.

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
  split, a hitch list, and graphs overlaying raw max on the smoothed line. `DT-4`'s allocation leftovers
  are fixed in the same pass so the HUD stops polluting its own GC reading.

### 4.8 Ownership and threading

- All slot/column writes are main-thread only; worker producers use `Interlocked` cumulative counters
  sampled on the main thread. The ring and hitch records are owned by `PerfStore`; the export writer
  receives pooled copies, never the live ring.
- Static state follows the house rules: `[NoAutoStaticsCleanup] // reset in DomainReset` on every member,
  reset inside the class's single `[RuntimeInitializeOnLoadMethod]`; native buffers disposed on tier
  exit and on application quit.

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

| Project constraint | How this design complies |
|---|---|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — instrumentation reads counters, never voxel storage |
| Burst jobs 100 % Burst-compatible | Jobs only write a timestamp pair into their output struct (source verified in PM-0); no managed calls added to jobs |
| No GC / LINQ in hot paths | Enum-indexed pre-allocated arrays; no strings, delegates or scopes on probes; formatting only on the HUD's 10 Hz refresh or the export thread |
| Pooling conventions | Hitch records and export buffers pooled; native rings allocated on tier entry only |
| No BinaryFormatter/JSON for terrain | Logs are CSV diagnostics; the tier and levels are `Settings` config JSON |
| BlockIDs constants, no raw IDs | No block IDs involved |
| Statics / domain reload | Every static annotated + reset in the single `[RuntimeInitializeOnLoadMethod]` (§4.8) |

---

## 7. Phased implementation plan

| Phase | Scope | Effort | Depends on | Status |
|---|---|:---:|---|---|
| **PM-0 — Verify** | Execution packet §7.1. Master-build probe: dump `ProfilerRecorderHandle.GetAvailable` + `Valid`; `FrameTimingManager` with frame-timing stats on; `GC.GetAllocatedBytesForCurrentThread` under IL2CPP; Burst timestamp source; probe cost (QPC ns) | 🟢 | — | ✅ 2026-10-03 — §8 q1–q4 answered in two Master builds |
| **PM-1 — Core store** | `PerfStore`, `PerfSlot`, raw per-frame ring, exact window statistics, tier setting + live apply, `WorldFrameProfiler` facade (identical `PassMsTotals`), `/perf stats` + `/perf tier` | 🟡 | PM-0 | ✅ 2026-10-03 — code + suite (§7.2); in-game check pending |
| **PM-2 — Frame tier** | Per-frame GC + collection flag, `FrameTiming` GPU/render/present-wait, hitch detector + snapshots | 🟡 | PM-1 | — |
| **PM-3 — Coverage** | Slots for every untimed `World.Update` region + unattributed remainder; non-World systems; `PerfCounter` + counter columns, with gauges for queues, in-flight jobs, pools, resident chunks (moved from PM-1); `World.cs`'s facade probes to the slotted API; the Master IL2CPP overhead A/B (moved from PM-1) | 🟡 | PM-1 | — |
| **PM-4 — Workers & I/O** | Job schedule→complete latency, in-job execute time, worker utilization; disk latency/bytes/compression; ThreadPool depth | 🟡 | PM-0, PM-1 | — |
| **PM-5 — HUD** | Systems panel, hitch list, GPU/CPU split, raw-max graph overlay; `DT-4` | 🟡 | PM-2, PM-3 | — |
| **PM-6 — Export** | Session CSV + hitch dumps on a background writer; benchmark reports read the store, with phase-wide percentiles (an aggregate spanning more than the ring — moved from PM-1); benchmark mode forces Capture | 🟡 | PM-2 | — |
| **PM-7 — Logger** | `EngineLog` categories/levels/rate limits/tags, migration of the 3 diagnostic flags and the 327 call sites (by category, in passes), stack-trace policy per build profile, variant-drift fix | 🟡 | — | — |

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

### 7.2 PM-1 execution record (2026-10-03; in-game check pending)

**Shipped** (`Assets/Scripts/Diagnostics/`, namespace `Diagnostics`): `PerfTier`, `PerfSlot` (the ten
`WorldFrameProfiler` phases, same values and names), `PerfFrame`, `PerfFrameRing` (2 048 frames, `Allocator.Persistent`;
slot columns allocated at Systems and up, freed below), `PerfWindowSummary` + `PerfWindowStats` (exact nearest-rank
selection), and `PerfStore` (static; one `DomainReset`; native memory freed on `Application.quitting` and before an
Editor assembly reload, after which commits are ignored rather than reallocating). `WorldFrameProfiler` is a facade:
`Enabled` is `PerfStore.ForceSlots`, OR'ed with the tier, so a harness clearing it never disables a tier the player
chose. `PerformanceMonitor` commits each frame's raw wall/CPU ticks and applies `Settings.perfMonitorTier` in
`OnEnable` (which also runs after a Play-mode script reload) and live through `OnSettingChanged`; the tier is in `OverlayBenchmarkSettingsFromDisk`. `/perf stats` prints worst,
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

### Extension roadmap

| Version | Extension |
|---|---|
| **v2** | Per-worker job timeline (swimlanes) from the PM-4 timestamps, viewable in the HUD or exported |
| **v2** | Remote/live view over a local socket for a second-monitor dashboard (local only) |
| **v3+** | Automatic regression detection across saved sessions — own design doc |

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
   times regardless, so it cannot answer either half.)
5. Default hitch thresholds (33 ms / ×2.5 median) — to be tuned against real sessions after PM-2.
6. Should Tier 0 stay the default, or should Tier 1 ship on by default once its cost is measured?

---

## 9. Rejected alternatives

| Alternative | Why rejected | Date |
|---|---|---|
| Gate `PerformanceMonitor`'s sampling on HUD visibility | Standing decision: the always-on history is what shows a hitch that happened while the HUD was closed | 2026-07-02 |
| `ProfilerRecorder`/`ProfilerMarker` as the core | Invalid in Release builds per the v2.1 doc; markers readable only via the Profiler (§3.1) | 2026-10-02 |
| A single on/off toggle | Tier costs differ by orders of magnitude (§3.2) | 2026-10-02 |
| Replacing `WorldFrameProfiler` outright | Breaks comparability of every FP/P-9/P-4 capture (§3.3) | 2026-10-02 |
| `IDisposable` scope structs / delegates on hot-path probes | Avoidable overhead and allocation risk; explicit begin/end matches the proven `WorldFrameProfiler` pattern | 2026-10-02 |
| Uploading telemetry | Out of scope; output stays local | 2026-10-02 |
| Log-scale histograms for the ring's window percentiles | Bucket width (±5–12 %) adds error on top of run-to-run noise; exact selection over the ring costs nothing per frame (§4.1) | 2026-10-03 |

---

## Document History

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

**Last Updated:** 2026-10-02  
**Next Review:** when PM-2 or PM-3 starts (PM-1 code landed 2026-10-03)
