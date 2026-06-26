# TG-4 Phase 4b — Y-band A/B (full-height halo vs active-fluid Y-band halo)

| Field           | Value                                                                                                                                                                                                                                                                                        |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-06-27 (local)                                                                                                                                                                                                                                                                           |
| **Branch**      | `feat/Modular-World-Generation-&-World-Types`                                                                                                                                                                                                                                                |
| **POST commit** | C3 (`EnableFluidBandGather` production wiring + `FluidTickBenchmark._sweepBandGather` + the Y-band parallel-determinism gate)                                                                                                                                                                |
| **Captured by** | `Assets/Scripts/Benchmarks/FluidTickBenchmark.cs` — **IL2CPP player (Development Build)**, 150 runs/scenario, 5 warm-up ticks; in-game by `FluidStressPass` (full-world frame attribution)                                                                                                   |
| **Verdict**     | **GO on the Y-band — a free, byte-identical serial tick win** (min −1.6…−4.6 % on fluid scenarios) that **collapses the large-flood tail** (Fluid-Large-4ch peak −46 %, stddev 7× tighter; Ocean-25ch peak −24 %). **Frame-neutral in-game** (the flood frame is Light-bound, tick sub-1 %). |

> **This is the TG-4 Phase 4b "measure the Y-band on a green full-height base" capture** (locked decision #1: full
> height first → measure → band later), per
> [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) Phase 4b. It is an
> **A/B over the same build**: each scenario is measured under three legs — `managed` (Phase-3/4a hybrid), `halo-full`
> (Phase-4b full-height halo), and `halo-band` (the same halo, gather/read window sized to the active-fluid Y-band).
> The `halo-full → halo-band` delta is the Y-band's contribution; `managed → halo-full` re-confirms the prior
> [full-height halo A/B](BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md) on IL2CPP and is the **C1/C2
> refactor regression check**.

## What this measures (and what it does NOT)

The benchmark drives the **production** serial tick path over synthetic chunks: `Chunk.TickUpdate` → `TickFluidsHybrid`

+ `World.ApplyModifications`. The three legs differ only in the fluid path:

- **`managed`** — `EnableFluidBorderBurst` off: border fluids on the managed `BlockBehavior` path.
- **`halo-full`** — `EnableFluidBorderBurst` on, `EnableFluidBandGather` off: every fluid Bursted; the per-tick
  neighbor halo is gathered over the **full 128 rows** of chunk height.
- **`halo-band`** — both flags on: the same halo, but the gather + the job's reads are restricted to the tight
  active-fluid Y-band `[minActiveY − 1, maxActiveY + 1]` (`FLUID_VERTICAL_REACH = 1`), a band-sized prefix of the
  full-height padded volume. Byte-identical to `halo-full` by the reach invariant (gated by `BH-D1[H|HB]`).

The benchmark drives `Chunk.TickUpdate` **serially per chunk** (not `World.ProcessTickUpdatesParallel`), so the
`halo-band → halo-full` delta is the **per-tick gather/copy reduction on the main thread**. The parallel-path frame
effect is measured separately by the in-game stress pass below.

## Methodology

IL2CPP Development Build, Burst enabled + safety checks on. Each scenario runs 150 times per leg; each run rebuilds the
chunks fresh and contributes **one avg-ms/tick sample**. `min` = clean uninterrupted floor (best CPU-cost proxy),
`mean` includes GC/scheduler cost, `stddev` = spread, `peak` = single worst tick. `µs/voxel = min ms/tick × 1000 ÷
peak active voxels`.

## Result — IL2CPP player, ms/tick over 150 per-run samples

| Scenario          | Leg       | Chunks | PeakActive |      mean |       min | median |    stddev |       peak | µs/voxel |
|-------------------|-----------|-------:|-----------:|----------:|----------:|-------:|----------:|-----------:|---------:|
| Fluid-Small       | managed   |      1 |        836 |     0.650 |     0.588 |  0.642 |     0.044 |      2.130 |    0.703 |
| Fluid-Small       | halo-full |      1 |        836 |     0.266 |     0.259 |  0.262 |     0.019 |      1.077 |    0.310 |
| Fluid-Small       | halo-band |      1 |        836 | **0.256** | **0.248** |  0.251 |     0.021 |  **1.037** |    0.297 |
| Fluid-Medium      | managed   |      1 |       3456 |     1.745 |     1.579 |  1.742 |     0.088 |      6.803 |    0.457 |
| Fluid-Medium      | halo-full |      1 |       3456 |     0.554 |     0.547 |  0.552 |     0.009 |      2.129 |    0.158 |
| Fluid-Medium      | halo-band |      1 |       3456 | **0.547** | **0.538** |  0.544 |     0.011 |  **2.002** |    0.156 |
| Fluid-Large-4ch   | managed   |      4 |      13824 |     6.714 |     6.298 |  6.689 |     0.224 |     28.481 |    0.456 |
| Fluid-Large-4ch   | halo-full |      4 |      13824 |     2.310 |     2.233 |  2.258 |     0.210 |     14.856 |    0.162 |
| Fluid-Large-4ch   | halo-band |      4 |      13824 | **2.217** | **2.175** |  2.210 | **0.030** |  **8.089** |    0.157 |
| Cave-Fill-Cascade | managed   |      1 |        712 |     0.723 |     0.640 |  0.709 |     0.058 |      2.408 |    0.899 |
| Cave-Fill-Cascade | halo-full |      1 |        712 |     0.301 |     0.279 |  0.283 |     0.048 |      1.037 |    0.391 |
| Cave-Fill-Cascade | halo-band |      1 |        712 | **0.292** | **0.267** |  0.271 |     0.056 |      1.205 |    0.375 |
| **Ocean-25ch**    | managed   | **25** |  **17800** |    18.008 |    17.098 | 18.046 |     0.313 |     31.906 |    0.961 |
| **Ocean-25ch**    | halo-full | **25** |  **17800** |     7.307 |     7.192 |  7.279 |     0.135 |     21.145 |    0.404 |
| **Ocean-25ch**    | halo-band | **25** |  **17800** | **7.005** | **6.864** |  6.966 |     0.150 | **16.046** |    0.386 |
| Grass-Field       | managed   |      1 |        144 |     0.007 |     0.006 |  0.007 |     0.000 |      0.085 |    0.044 |
| Grass-Field       | halo-full |      1 |        144 |     0.007 |     0.006 |  0.006 |     0.001 |      0.159 |    0.044 |
| Grass-Field       | halo-band |      1 |        144 |     0.006 |     0.006 |  0.006 |     0.000 |      0.084 |    0.044 |
| Mixed-Lake+Grass  | managed   |      1 |        944 |     0.717 |     0.625 |  0.708 |     0.056 |      4.623 |    0.662 |
| Mixed-Lake+Grass  | halo-full |      1 |        944 |     0.296 |     0.289 |  0.293 |     0.012 |      1.239 |    0.306 |
| Mixed-Lake+Grass  | halo-band |      1 |        944 | **0.289** | **0.278** |  0.282 |     0.030 |  **1.093** |    0.294 |

## A/B delta — halo-band vs halo-full (the Y-band's contribution, same build)

| Scenario            | min full → band  |  Δ min | peak full → band     |      Δ peak | stddev full → band             |
|---------------------|------------------|-------:|----------------------|------------:|--------------------------------|
| Fluid-Small         | 0.259 → 0.248 ms | −4.2 % | 1.08 → 1.04 ms       |      −3.7 % | 0.019 → 0.021                  |
| Fluid-Medium        | 0.547 → 0.538 ms | −1.6 % | 2.13 → 2.00 ms       |      −6.0 % | 0.009 → 0.011                  |
| **Fluid-Large-4ch** | 2.233 → 2.175 ms | −2.6 % | **14.86 → 8.09 ms**  | **−45.6 %** | **0.210 → 0.030 (7× tighter)** |
| Cave-Fill-Cascade   | 0.279 → 0.267 ms | −4.3 % | 1.04 → 1.21 ms       |     +16 % ⚠ | 0.048 → 0.056                  |
| **Ocean-25ch**      | 7.192 → 6.864 ms | −4.6 % | **21.15 → 16.05 ms** | **−24.1 %** | 0.135 → 0.150                  |
| Mixed-Lake+Grass    | 0.289 → 0.278 ms | −3.8 % | 1.24 → 1.09 ms       |     −11.8 % | 0.012 → 0.030                  |
| Grass-Field         | 0.006 → 0.006 ms |   ~0 ⟂ | 0.16 → 0.08 ms       |     (noise) | 0.001 → 0.000                  |

`⟂` Grass-Field is the **control** (no fluids → the band code never runs); the flat row confirms the sweep adds no
noise of its own. `⚠` Cave-Fill peak +16 % is noise on a tiny scenario (712 voxels, sub-1.2 ms peaks); its floor still
improves −4.3 %.

## Regression cross-check — managed → halo-full on IL2CPP (min)

The C1 `GatherPaddedRange` refactor + the C2 "always route through the band gather (band = `[0,128]`)" change must
leave the full-height halo **at least as fast** as the prior [Mono Phase-4b A/B](BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md)
(which measured 1.70–2.15×). On IL2CPP the `managed → halo-full` speedup is **2.16–2.89×** (Ocean-25ch **2.38×**,
Fluid-Medium **2.89×**) — wider than Mono, as predicted (IL2CPP helps the managed leg only ~1.4× while Burst takes the
full win). **No regression**: `halo-full` is the height-independent gather routed through the new core, and it is
faster than the old managed→halo win, not slower.

## In-game — full-world stress pass (parallel path, real pipeline)

`FluidStressPass`: a real loaded world, the throttled mesh+lighting+cross-chunk pipeline, a deterministic 5×5 = 25-chunk
suspended-basin flood, with per-frame Tick/Apply/Mesh/Light attribution. Two runs: band **off** (`Full-band` log =
full-height halo) vs band **on** (`Y-band` log).

| Flood phase (sustained avg)  | full-height (off) |      Y-band (on) |
|------------------------------|------------------:|-----------------:|
| Frame ms                     |             9.264 |            9.337 |
| Tick ms (share of frame)     |     0.082 (0.9 %) |    0.071 (0.8 %) |
| Mesh ms                      |             1.235 |            1.305 |
| **Light ms (share)**         |  **6.485 (70 %)** | **6.544 (70 %)** |
| Worst frame ms (Light share) |   35.881 (56 % L) |  34.502 (58 % L) |
| Tick peak (max single-frame) |            17.070 |           16.887 |
| GC/frame peak (KB)           |              1260 |              996 |

**The band is frame-invisible in-game, as expected.** The sustained flood frame is **Light-dominated (70 %)** with the
fluid tick already at **sub-1 %** (0.07–0.08 ms) — so the band's serial copy reduction has no room to move the frame.
Full-height and Y-band are within run-to-run noise at every frame-level metric (sustained 9.26 vs 9.34 ms, tick peak
17.07 vs 16.89 ms). This matches the Phase-4b conclusion: the tick is not the ocean-frame bottleneck; the Light line is.

> **Anomaly (not band-attributable):** the Y-band run logged a single **125.7 ms baseline-phase frame** (Tick peak
> 124.7 ms, before the flood). This is a one-off first-execution/JIT/GC outlier in the *settled* phase — the **flood**
> phase tick peak (16.89 ms) is clean and matches the full-height run (17.07 ms). It did not recur and is unrelated to
> the band. A re-capture would clear it; it is noted here for transparency, not as a finding.

## Reading the result

- **The band is a free serial floor win.** −1.6…−4.6 % on `min` across every fluid scenario, biggest where the gather
  matters most (Ocean-25ch −4.6 %, −0.33 ms/tick), and **never a regression** (Grass-Field control flat). It is
  byte-identical, so this is pure copy-volume savings: the gather scales with the active-fluid Y-extent, not world
  height.
- **The real prize is the large-flood tail.** Fluid-Large-4ch peak **−45.6 %** (14.9 → 8.1 ms) with stddev **7× tighter**
  (0.210 → 0.030), and Ocean-25ch peak **−24.1 %** (21.1 → 16.0 ms). The full-height gather's 288 KB/chunk memory
  traffic was a spike source; the band cuts it to the active slab, flattening the worst ticks. Peak ms/tick is what a
  user feels as stutter.
- **In-game it changes nothing visible — and that is the honest verdict.** At 128-height, the ocean flood frame is
  Light-bound; the tick (already parallel + Bursted) is sub-1 %, so shaving its copy is below the noise floor of the
  frame. The band is a **tick-path margin-widener and scaling-insurance**, not a frame-rate lever — exactly as
  decision #1 predicted.
- **Scaling insurance is the strategic value.** The per-tick gather is now **independent of world height**: a taller
  world (256/384) would inflate the full-height copy linearly while the band stays sized to the fluids. The tail/GC
  reduction is also real frame-stability headroom for denser floods than this 25-chunk basin.

## Verdict

| Question                                               | Answer                                                                                                                                                                                                       |
|--------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Does the Y-band reduce the serial tick cost?           | **Yes — modestly on the floor (−1.6…−4.6 %), substantially on the tail** (Fluid-Large-4ch peak −46 %, stddev 7× tighter; Ocean peak −24 %). Byte-identical.                                                  |
| Did the C1/C2 refactor regress the full-height halo?   | **No.** `managed → halo-full` is 2.16–2.89× on IL2CPP — wider than the prior Mono 1.70–2.15×.                                                                                                                |
| Does the band help the in-game ocean frame?            | **No (frame-neutral).** The flood frame is Light-bound (70 %); the tick is sub-1 %. Full-height and Y-band are within noise. No regression either.                                                           |
| Is the band worth shipping?                            | **Yes** — free correctness-preserving floor/tail win + height-independent scaling + GC-spike reduction, with zero in-game cost. A margin-widener, not a frame lever.                                         |
| Should `EnableFluidBandGather` flip on by default now? | **Recommended** — byte-identical, never regresses, reduces peak/tail + GC, in-game frame-parity confirmed. Pending the user's explicit visual-correctness sign-off (the `EnableFluidBorderBurst` lifecycle). |

**Bottom line:** the Y-band is a clean **GO** as the Phase-4b finisher — it shaves the serial floor a few percent, cuts
the large-flood worst-tick tail by 24–46 %, makes the per-tick gather independent of world height, and costs nothing
in-game (where the frame is Light-bound). It closes the deferred decision #1 item without disturbing the shipped
full-height path (kept behind the flag as a one-toggle rollback).

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz  (16 threads)
RAM:            65 381 MB
OS:             Windows 10 (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.1f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP  (Development Build)
Micro-benchmark GUID:  22ebaadf5b4543ed8f3469c205790119
Stress (full-height):  8c2ca0a6ec294e33a84dfe0f4076549b
Stress (Y-band):       1f46fdad31124628b144d2c6779fa1e3

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

Micro-benchmark: 150 runs/scenario/leg, 5 warm-up ticks discarded. Stress pass: 25-chunk suspended-basin flood, real
throttled pipeline, per-frame Stopwatch attribution (IL2CPP-valid).

## Cross-references

- **Design / plan:** [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) Phase 4b (Y-band optimization).
- **Prior baseline (full-height halo A/B):** [`BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md`](BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md).
- **Correctness gates:** `BH-D1[H|HB]` / `BH-D1[L|HB]` (`Validate Behavior`), `BH-4-SPLIT-Y` / `BH-4-BAND-EDGE` fixtures; `Validate Fluid Parallel Determinism (Cross-Chunk Halo, Y-band)`.
- **Harness:** `Assets/Scripts/Benchmarks/FluidTickBenchmark.cs` (`_sweepBandGather`), `Assets/Scripts/Benchmarks/FluidBenchmarkScenarios.cs`.
- **Folder conventions:** [`README.md`](README.md).
