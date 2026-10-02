# Performance Monitor & Logger Overhaul Design

**Version:** 1.0  
**Date:** 2026-10-02  
**Status:** Proposed design — not implemented. PM-0 is a zero-behavior-change verification probe that
must run first; its answers may reshape PM-2/PM-4 (§8).  
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

#### Option B — `Stopwatch.GetTimestamp` probes into an enum-indexed store ✅ **CHOSEN**

The pattern `WorldFrameProfiler` already proves in Master builds: begin/end timestamps into
pre-allocated per-slot accumulators. Recorders are used **only as optional enrichment** for counters
PM-0 proves valid in the target build (memory, render stats), and each probe *also* emits a
`ProfilerMarker` under `ENABLE_PROFILER`, so Unity Profiler captures keep their names.

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
launches alike. Logging levels (§4.6) are independent of the tier.

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
    // World.Update (existing WorldFrameProfiler phases map onto these first, same order)
    GenerationProcess, Apply, LightMerge, LightStagingDrain, LightFailSafeScan, LightSchedule,
    MeshProcess, MeshSchedule, Tick, LightQueueProbe,
    // World.Update regions untimed today
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
- **Counters** (`PerfCounter` enum: queue depths, in-flight jobs per type, resident chunks, pool
  in-use/peak per pool, I/O ops/bytes, ThreadPool depth) are gauges sampled once per frame into a
  second column set, plus `Interlocked`-updated cumulative counters for worker-thread producers.
- **Statistics** per slot/metric use fixed log-scale histograms (e.g. 64 buckets from 1 µs to 1 s) over
  a sliding window — percentiles without sorting or allocation.

### 4.2 Probes

```csharp
long t = PerfStore.Begin();          // returns 0 and does nothing below the slot's tier
...
PerfStore.End(PerfSlot.ViewDistance, t);
```

`Begin` is one static tier read + `Stopwatch.GetTimestamp()`; `End` accumulates into the slot's current
frame cell (several calls per frame sum). Under `ENABLE_PROFILER` the same call pair also emits the
slot's static `ProfilerMarker`. No strings, no delegates, no `IDisposable` scopes on hot paths. Coverage
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
  needs its own edit.
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
  array by `PhaseCount`).
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
| **PM-0 — Verify** | Execution packet §7.1. Master-build probe: dump `ProfilerRecorderHandle.GetAvailable` + `Valid`; `FrameTimingManager` with frame-timing stats on; `GC.GetAllocatedBytesForCurrentThread` under IL2CPP; Burst timestamp source; probe cost (QPC ns) | 🟢 | — | — |
| **PM-1 — Core store** | `PerfStore`, `PerfSlot`/`PerfCounter`, raw per-frame ring, histograms, tier setting + live apply, `WorldFrameProfiler` facade (identical `PassMsTotals`) | 🟡 | PM-0 | — |
| **PM-2 — Frame tier** | Per-frame GC + collection flag, `FrameTiming` GPU/render/present-wait, hitch detector + snapshots | 🟡 | PM-1 | — |
| **PM-3 — Coverage** | Slots for every untimed `World.Update` region + unattributed remainder; non-World systems; gauges for queues, in-flight jobs, pools, resident chunks | 🟡 | PM-1 | — |
| **PM-4 — Workers & I/O** | Job schedule→complete latency, in-job execute time, worker utilization; disk latency/bytes/compression; ThreadPool depth | 🟡 | PM-0, PM-1 | — |
| **PM-5 — HUD** | Systems panel, hitch list, GPU/CPU split, raw-max graph overlay; `DT-4` | 🟡 | PM-2, PM-3 | — |
| **PM-6 — Export** | Session CSV + hitch dumps on a background writer; benchmark reports read the store; benchmark mode forces Capture | 🟡 | PM-2 | — |
| **PM-7 — Logger** | `EngineLog` categories/levels/rate limits/tags, migration of the 3 diagnostic flags and the 327 call sites (by category, in passes), stack-trace policy per build profile, variant-drift fix | 🟡 | — | — |

**Minimal set with standalone value:** PM-0 + PM-1 + PM-2 — worst-frame, p99, hitch snapshots with GC
and GPU attribution — answers the roadmap's "is the traversal spike GC?" question on its own. PM-7 is
independent and can run in parallel.

**Validation is built alongside, not after:** a new `Validate Performance Monitor` suite pins the pure
parts — histogram percentile math, the hitch-detector truth table and window freeze, ring wrap-around,
tier gating (below-tier probes write nothing), the facade's slot mapping, and CSV formatting — and the
B34-style reset sweep covers the new statics. Timing *values* are not suite-testable (wall clock), so the
overhead budget (Off: one static read per probe; Systems: ≤ 50 µs/frame) is verified by a Master
IL2CPP A/B through the `perf-benchmark` skill.

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

### Extension roadmap

| Version | Extension |
|---|---|
| **v2** | Per-worker job timeline (swimlanes) from the PM-4 timestamps, viewable in the HUD or exported |
| **v2** | Remote/live view over a local socket for a second-monitor dashboard (local only) |
| **v3+** | Automatic regression detection across saved sessions — own design doc |

---

## 8. Open questions (PM-0 answers 1–4)

1. Which `ProfilerRecorder` counters are `Valid` in a Master IL2CPP player (memory, "GC Allocated In
   Frame", draw calls/batches/triangles)? The v2.1 doc says categories were invalid in Release; PM-0
   records the exact list.
2. Does `GC.GetAllocatedBytesForCurrentThread()` exist and stay monotonic under IL2CPP/Boehm with the
   project's API compatibility level? If not, §4.3's fallback applies.
3. Which timestamp source is Burst-callable for in-job execute timing (`ProfilerUnsafeUtility.Timestamp`
   or equivalent), and what does it cost?
4. Does `FrameTimingManager` report `gpuFrameTime` on D3D11 (the primary API) with frame-timing stats on,
   and what does enabling the stats cost?
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

---

## Document History

* **v1.0** - Initial design: four-tier opt-in `PerfStore` over the kept always-on `PerformanceMonitor`,
  `WorldFrameProfiler` facade, GC/GPU/worker/I-O coverage, hitch snapshots, session export, category
  logger, `PM-0`…`PM-7`; PM-0 execution packet (§7.1) with the H-1 pool-miss counters; §4.3 corrected —
  only the `Windows - Profiler` build profile holds a frozen Player Settings copy.

---

**Last Updated:** 2026-10-02  
**Next Review:** when PM-0's verification probe has run in a Master build
