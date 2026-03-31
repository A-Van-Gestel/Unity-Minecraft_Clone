# Design Document: Production-Ready Performance Profiler

**Version:** 2.0 (Implemented)  
**Target:** Unity 6.4 (Mono)  
**Context:** Replaced the Voxel Engine's `DebugScreen.cs` performance metrics with a custom `Stopwatch`-based `PerformanceMonitor`.

## 1. Executive Summary

The Voxel Engine previously relied on `ProfilerRecorder` and `1f / Time.unscaledDeltaTime` to measure performance in `DebugScreen.cs`. This approach had significant flaws:

1. **Production Failure:** `ProfilerRecorder` categories returned invalid data in Release/Production builds unless Deep Profiling was explicitly enabled in the build settings, making the debug screen useless for end-users.
2. **Inaccuracy:** `Time.unscaledDeltaTime` measures the time between frame starts, which includes VSync waiting periods and GPU stalls. It did not accurately reflect actual CPU workload.
3. **Logic Bug:** In `DebugScreen.cs`, `_gcAllocatedInFrame` was incorrectly assigned from `_mainThreadTimeRecorder.LastValue`, silently breaking the CPU metric display (always showed `0.00 ms`).

By analyzing the `FPSCounter` BepInEx plugin, we extracted its core high-resolution `Stopwatch` methodology and adapted it natively into our engine.
The volatile `ProfilerRecorder` was replaced with a custom `PerformanceMonitor` that accurately tracks the exact CPU time spent in various Unity lifecycle phases while distinguishing true CPU effort from Wall Clock time.

---

## 2. Reference: FPSCounter BepInEx Plugin

The `FPSCounter` plugin (`_REFERENCES/FPSCounter-master/`) provided the measurement architecture. The following table summarizes what was adapted, improved, and excluded:

### Adapted Elements

| FPSCounter Pattern                                                               | Our Adaptation                                                                                   |
|----------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------|
| `FrameCounterHelper` at `int.MinValue` + `FrameCounterHelper2` at `int.MaxValue` | `PerformanceMonitor` at `int.MinValue` + `PerformanceMonitorLateHook` at `int.MaxValue`          |
| `_measurementStopwatch.Start()` in `FixedUpdate()` (not `Restart`)               | `_phaseStopwatch.Start()` in `FixedUpdate()` — prevents idle bleed when FixedUpdate doesn't fire |
| `TakeMeasurement()` → reset + return elapsed                                     | `SampleAndRestart()` → same pattern, plus accumulates into `_currentFrameCpuTicks`               |
| Separate `totalStopwatch` for wall-clock frame time                              | Separate `_frameStopwatch` for `WallFrameTime`                                                   |
| `MovingAverage` with `Queue<long>` (window=11)                                   | `MovingAverage` with `long[]` ring buffer (window=30) — zero-allocation                          |

### Improvements Over the Reference

1. **GC Allocation Tracking:** The reference used `GC.GetTotalMemory` deltas with a heuristic (negative delta = collection). We use the authoritative `GC.CollectionCount(g)` API per generation.
2. **GC Allocation Decay:** The reference (and original design) skipped sampling when delta ≤ 0. We always sample (clamping to 0), so the moving average correctly decays to zero when no allocations occur.
3. **Idle/Other Metric:** Added a derived `IdleTimeMs` (Wall − CPU) that quantifies VSync/GPU idle headroom — the reference only showed this as an unnamed "Other/Frameskip" line.
4. **Late Hook Caching:** `PerformanceMonitorLateHook` caches `GetComponent<PerformanceMonitor>()` in `Awake()` instead of polling the singleton every frame.
5. **Lifecycle Cleanup:** `OnDestroy()` nulls the static `Instance` for clean scene-scoped behavior.

### Correctly Excluded

- **Native OS Calls:** `kernel32.dll` / `psapi.dll` P/Invokes for memory tracking — replaced with cross-platform `Profiler.GetTotalAllocatedMemoryLong()` / `GetTotalReservedMemoryLong()`.
- **Harmony Patching:** `PluginCounter.cs` IL injection — BepInEx-specific, irrelevant to a native engine.
- **Custom String Formatters:** ~42KB `FixedString` / `StringBuilderNumerics` — overkill for a 10Hz-throttled TextMeshPro UI.
- **OnGUI Phase:** The reference measured `OnGUI` as a separate phase. We merge it into `RenderTime` since the engine uses TextMeshPro, not IMGUI. This avoids the overhead of an `OnGUI()` callback entirely.

---

## 3. Architecture

The implementation consists of three parts:

1. **`MovingAverage` (Utility):** A sealed, zero-allocation ring buffer to smooth Stopwatch tick values.
2. **`PerformanceMonitor` (Core Logic):** A centralized timing service using the "Dual-Hook" pattern to accurately slice the frame.
3. **`DebugScreen` (UI Integration):** Refactored to read from `PerformanceMonitor` instead of `ProfilerRecorder`.

### 3.1. Phase Measurement Strategy (The "Dual-Hook" Pattern)

To measure the exact time Unity spends in each phase without bleeding idle time, we manage a phase `Stopwatch` across the frame lifecycle, aggregating the active ticks into a `CpuFrameTime` total.

| Phase                    | Hook Point                                   | Measurement                                 | Notes                                                                  |
|--------------------------|----------------------------------------------|---------------------------------------------|------------------------------------------------------------------------|
| **FixedUpdate**          | `FixedUpdate()` at `int.MinValue`            | `_phaseStopwatch.Start()` (not `Restart`)   | If FixedUpdate doesn't run this frame, elapsed stays 0 — no idle bleed |
| **FixedUpdate → Update** | `Update()` at `int.MinValue`                 | `SampleAndRestart()` → `FixedUpdateTime`    | Captures all FixedUpdate iterations this frame                         |
| **Update Phase**         | `yield return null` in coroutine             | `SampleAndRestart()` → `UpdatePhaseTime`    | Includes all scripts' Update() calls and yield-null coroutines         |
| **Coroutine Phase**      | `LateUpdate()` at `int.MinValue`             | `SampleAndRestart()` → `CoroutinePhaseTime` | Unity's internal animation processing, coroutine scheduling            |
| **LateUpdate**           | `LateUpdate()` at `int.MaxValue` (late hook) | `SampleAndRestart()` → `LateUpdateTime`     | Full duration of all LateUpdate callbacks                              |
| **Render/GUI**           | `yield return WaitForEndOfFrame`             | `ElapsedTicks` → `RenderTime`               | Scene rendering + IMGUI passes                                         |
| **Frame Idle Reset**     | After Render sample                          | `_phaseStopwatch.Reset()`                   | Drops inter-frame idle time                                            |

### 3.2. Memory Measurement Strategy (Cross-Platform)

| Metric                      | Source                                                                     | Notes                                                   |
|-----------------------------|----------------------------------------------------------------------------|---------------------------------------------------------|
| **Native Alloc / Reserved** | `Profiler.GetTotalAllocatedMemoryLong()` / `GetTotalReservedMemoryLong()`  | Works in all build types                                |
| **Managed GC Heap**         | `GC.GetTotalMemory(false)`                                                 | Current managed heap size                               |
| **GC Alloc/frame**          | Delta of `GC.GetTotalMemory()` per frame, averaged via `MovingAverage(60)` | Shows bytes/frame (not bytes/sec) — directly actionable |
| **GC Generation Hits**      | `GC.CollectionCount(g)` minus session baseline                             | Session-relative; baseline captured in `Awake()`        |

### 3.3. Derived Metrics

| Metric              | Formula                                            | Purpose                                               |
|---------------------|----------------------------------------------------|-------------------------------------------------------|
| **CPU FPS**         | `Stopwatch.Frequency / CpuFrameTime.GetAverage()`  | Theoretical FPS without VSync/GPU constraints         |
| **Wall FPS**        | `Stopwatch.Frequency / WallFrameTime.GetAverage()` | Actual visible FPS                                    |
| **Idle/Other Time** | `WallFrameTime - CpuFrameTime` (in ms)             | VSync waits + GPU stalls + uncaptured Unity internals |

### 3.4. Lifecycle

- **Scene-scoped:** No `DontDestroyOnLoad`. Metrics reset on scene reload / Play Mode re-entry.
- **Singleton:** Static `Instance` property. `OnDestroy()` nulls the reference for clean re-entry.

---

## 4. Implementation Details

### 4.1. Zero-Allocation Moving Average

**`Assets/Scripts/Helpers/MovingAverage.cs`**

```csharp
namespace Helpers
{
    /// <summary>
    /// A zero-allocation ring buffer for calculating moving averages over a fixed window of samples.
    /// Uses a pre-allocated array instead of a Queue to avoid GC pressure.
    /// </summary>
    public sealed class MovingAverage
    {
        private readonly long[] _samples;
        private int _currentIndex;
        private int _count;
        private long _accumulator;

        public MovingAverage(int windowSize)
        {
            _samples = new long[windowSize];
        }

        public void Sample(long newSample)
        {
            if (_count == _samples.Length)
            {
                _accumulator -= _samples[_currentIndex];
            }
            else
            {
                _count++;
            }

            _samples[_currentIndex] = newSample;
            _accumulator += newSample;

            _currentIndex = (_currentIndex + 1) % _samples.Length;
        }

        public long GetAverage() => _count == 0 ? 0 : _accumulator / _count;

        public void Reset()
        {
            _currentIndex = 0;
            _count = 0;
            _accumulator = 0;

            for (int i = 0; i < _samples.Length; i++)
            {
                _samples[i] = 0;
            }
        }
    }
}
```

### 4.2. The Performance Monitor (Dual-Hook)

**`Assets/Scripts/PerformanceMonitor.cs`**

Contains two classes:

- **`PerformanceMonitor`** (`[DefaultExecutionOrder(int.MinValue)]`) — The core timing service.
- **`PerformanceMonitorLateHook`** (`[DefaultExecutionOrder(int.MaxValue)]`) — Dynamically added via `AddComponent` in `Awake()`.

Key implementation details:

- Phase timings use `private const int PHASE_WINDOW_SIZE = 30;`
- GC allocation tracking uses `private const int GC_WINDOW_SIZE = 60;`
- All `MovingAverage` fields are exposed as auto-properties with `{ get; }` (not public fields).
- GC allocation always samples `Math.Max(0, delta)` — even when delta is zero or negative — so the moving average correctly decays to zero when allocations stop.
- `_phaseStopwatch.Start()` (not `Restart()`) in `FixedUpdate` prevents idle bleed.
- `_phaseStopwatch.Reset()` after the Render sample ensures inter-frame idle time is dropped.

### 4.3. DebugScreen Integration

**`Assets/Scripts/DebugScreen.cs`**

**Removed:**

- All `ProfilerRecorder` fields (`_mainThreadTimeRecorder`, `_renderThreadTimeRecorder`, `_gcAllocatedInFrameRecorder`, `_systemUsedMemoryRecorder`, `_gcReservedMemoryRecorder`)
- `OnEnable()` / `OnDisable()` recorder lifecycle
- `_profilerRecordersAreValid`, `_didIEnableTheProfiler` flags
- `_frameRate`, `UpdateFrameRate()`, `_frameRateUpdateRate`, `_frameRateTimer`
- `FormatMilliseconds()` dead utility method
- Buggy profiler data caching from `UpdateInfrequentData()` that silently broke CPU metrics
- `using Unity.Profiling;` namespace

**Added:**

- `using System;`
- `using Stopwatch = System.Diagnostics.Stopwatch;` — type alias to avoid ambiguity
- FPS from `PerformanceMonitor.Instance.WallFPS` in `PopulateTopLeftBuilder()`
- Full performance panel in `PopulateTopRightBuilder()` reading from `PerformanceMonitor.Instance`

**Display Layout (Top-Right Panel):**

```
PERFORMANCE:
CPU FPS:  1500
Wall FPS: 450
CPU Time:   0.67 ms
Wall Time:  2.22 ms
Idle/Other: 1.55 ms

--- CPU Phases ---
FixedUpdate: 0.02 ms
Update:      0.30 ms
Coroutine:   0.01 ms
LateUpdate:  0.05 ms
Render/GUI:  0.29 ms

--- Memory ---
Native Alloc: 1.23 GB
Native Rsvd:  1.50 GB
Managed GC:   256.00 MB
GC Alloc/frame: 350 B
GC Gen0 Hits: 42
GC Gen1 Hits: 3
GC Gen2 Hits: 0
```

---

## 5. Performance Considerations & Benefits

1. **Production Reliability:** By using `Stopwatch` and standard framework APIs, CPU metrics and GC tracking work flawlessly in all build types across all target platforms.
2. **True CPU Time vs Wall Time:** By aggregating phase ticks into `CpuFrameTime` while maintaining a separate `WallFrameTime`, VSync wait states are completely filtered out. The `Idle/Other` metric quantifies the headroom.
3. **Phase Accuracy:** The Dual-Hook architecture combined with `Start()` / `Reset()` perfectly isolates execution phases, protecting measurements from inter-frame idle time bleed.
4. **GC Allocation Readability:** Displaying `GC Alloc/frame` (bytes per frame) instead of bytes/sec gives a directly actionable metric — high values (tens of KB+) indicate a hot-path allocation problem; low values (hundreds of bytes) indicate healthy code.
5. **Session-Relative GC Tracking:** Reading `GC.CollectionCount(g)` per generation with an `Awake()` baseline means Editor debugging metrics start fresh every Play Mode entry.

---

## 6. Future Enhancement: Performance Debug Mode

A third `DebugMode` enum value is planned that would show only FPS + performance metrics, without the world/player/chunk/voxel debug panels:

```csharp
public enum DebugMode
{
    FPSOnly,       // Just Wall FPS (top-left)
    Performance,   // FPS + performance panel (top-left + top-right only)
    Full,          // Everything (all panels)
}
```

This requires only toggling which panels are visible in `SetMode()` — the data infrastructure already supports it.
