# Lighting RGB Phase 2 Baseline — NeighborhoodLightingJob

| Field                 | Value                                                                    |
|-----------------------|--------------------------------------------------------------------------|
| **Captured**          | 2026-06-06                                                               |
| **Branch**            | `feat/Modular-World-Generation-&-World-Types`                            |
| **Commit**            | `b1aadf3`                                                                |
| **Baseline kind**     | Pre-Phase 2 scalar BFS baseline for RGB blocklight regression detection  |
| **Regression budget** | ≤ 30% for blocklight scenarios (per `SMOOTH_AND_RGB_LIGHTING.md §4.3.2`) |
| **Captured by**       | `Assets/Scripts/Benchmarks/LightingJobBenchmark.cs`                      |
| **Total wall-clock**  | 32.6 s                                                                   |

## Why this baseline exists

Captured before Phase 2 of the Smooth & RGB Lighting implementation (see `Documentation/Architecture/SMOOTH_AND_RGB_LIGHTING.md §3`). Phase 2 will:

- Add a separate `NativeArray<ushort>` light storage array per section (4 bits sun + 3×4 bits block RGB).
- Convert the blocklight BFS in `NeighborhoodLightingJob` from scalar to per-channel independent RGB propagation.
- Implement per-channel darkness removal (3 independent channel comparisons per neighbor).
- Expand `LightModification`, `LightRemovalNode`, and `LightQueueNode` structs for RGB data.

The blocklight scenarios are the primary regression targets — they measure the exact BFS code paths that Phase 2 changes. Sunlight scenarios should be unaffected (sunlight remains scalar, tinted in the shader). The edge check scenario may show minor overhead from reading the wider light data.

Any Phase 2 commit that touches `NeighborhoodLightingJob` must be re-benchmarked against the per-scenario numbers below. Blocklight scenarios whose `μs/job` value exceeds the +30% threshold should be investigated. Sunlight scenarios should not regress at all.

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz
CPU threads:    16
CPU base MHz:   3600
RAM:            65 381 MB
OS:             Windows 10  (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.4.10f1
Platform:       WindowsEditor
Mode:           Editor
Backend:        Mono
Git commit:     b1aadf3

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

## Benchmark configuration

- **Jobs per run:** 256
- **Runs averaged:** 50
- **Blocking wait:** Yes (most accurate timing)
- **Warm-up:** 1 discarded run per scenario (absorbs Burst JIT)

## Results

| Scenario                  | ms/run | μs/job | vs Baseline | Category            |
|---------------------------|--------|--------|-------------|---------------------|
| Sunlight Vertical Flat    | 99     | 386.7  | 1.0x        | Sun (baseline)      |
| Sunlight Complex Caves    | 90     | 351.6  | 0.9x        | Sun                 |
| Sunlight Removal Covered  | 60     | 234.4  | 0.6x        | Sun (removal)       |
| Blocklight Simple         | 24     | 93.8   | 0.2x        | **Block**           |
| Blocklight Stress Test    | 118    | 460.9  | 1.2x        | **Block**           |
| Blocklight Removal Simple | 27     | 105.5  | 0.3x        | **Block (removal)** |
| Blocklight Removal Stress | 104    | 406.3  | 1.1x        | **Block (removal)** |
| Edge Check Consistency    | 69     | 269.5  | 0.7x        | Edge check          |

### Phase 2 regression targets

The **bold** rows above are the primary regression targets for Phase 2. Expected impact:

- **Blocklight Simple / Stress Test:** The per-channel BFS does 3× more comparison work per neighbor. The `ushort` light array's better cache density (16-bit vs reading from 32-bit `uint`) should partially offset this. Target: ≤ 30% increase.
- **Blocklight Removal Simple / Stress:** Per-channel darkness removal is more complex (3 independent channel comparisons, per-channel re-spreading). Target: ≤ 30% increase.
- **Sunlight scenarios:** Should be unchanged — sunlight BFS remains scalar.
- **Edge Check:** Minor overhead from reading the `ushort` array in addition to the `uint` map. Target: ≤ 10% increase.

### Phase 2 stub scenarios

The following scenarios will be activated once the RGB blocklight BFS is implemented:

| Scenario               | Purpose                                                 |
|------------------------|---------------------------------------------------------|
| Blocklight RGB Simple  | Single colored torch — measures per-channel overhead    |
| Blocklight RGB Overlap | Red + green + blue overlapping — per-channel max stress |
| Blocklight RGB Removal | Remove one colored source from multi-color area         |
| Blocklight RGB Stress  | ~900 colored lights cycling R/G/B — worst-case RGB BFS  |

These stubs exist in the benchmark code but skip with "STUB (Phase 2)" until the RGB BFS is ready. Once implemented, they should be compared against their scalar equivalents above to measure the true RGB overhead.
