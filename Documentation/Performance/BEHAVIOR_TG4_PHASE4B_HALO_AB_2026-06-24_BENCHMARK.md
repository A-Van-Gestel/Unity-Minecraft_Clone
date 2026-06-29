# TG-4 Phase 4b — Full-Height Halo A/B (managed-border hybrid vs full Burst halo)

| Field           | Value                                                                                                                                                                                                                                                                                                                   |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-06-24 (local)                                                                                                                                                                                                                                                                                                      |
| **Branch**      | `feat/Modular-World-Generation-&-World-Types`                                                                                                                                                                                                                                                                           |
| **POST commit** | `4d31d1e` (C5 determinism gate) + the uncommitted Phase-4b A/B sweep tooling (`FluidTickBenchmark._sweepBorderBurst`)                                                                                                                                                                                                   |
| **Captured by** | `Assets/Scripts/Benchmarks/FluidTickBenchmark.cs` — **Mono editor**, 150 runs/scenario, 5 warm-up ticks                                                                                                                                                                                                                 |
| **Verdict**     | **GO on the full-height halo — it is already a net serial *win*, not a cost.** Moving border fluids off the managed path into Burst (the +9-snapshot gather included) is **1.70–2.15× faster** on min ms/tick and **collapses the GC variance + peak spikes**. The Y-band optimization is **not required** for the win. |

> **This is the TG-4 Phase 4b "measure the full-height halo as a baseline" capture** (locked decision #1: full height
> first → measure → band later), per
> [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) Phase 4b. It is an
> **A/B over the same build**: each scenario is measured twice — `managed` (border fluids on the managed path, the
> Phase-3/4a hybrid) vs `halo` (every fluid Bursted, border voxels reading the per-tick **9-snapshot neighbor halo**).

## What this measures (and what it does NOT)

The benchmark drives the **production** tick path over synthetic chunks: `Chunk.TickUpdate` → `TickFluidsHybrid`
(interior fluids already Burst-ticked in **both** legs) + `World.ApplyModifications`. The **only** variable between
the two legs is the **border** (Tier-2) fluids:

- **`managed`** — `EnableFluidBorderBurst` off: border fluids evaluated by managed `BlockBehavior.Behave`/`Active`
  (per-voxel `List<VoxelMod>` allocation, managed `GetVoxelState` neighbor reads).
- **`halo`** — `EnableFluidBorderBurst` on: **all** fluids go through the Burst `FluidTickJob`; border voxels resolve
  cross-seam reads from a per-tick gathered neighbor halo (1 center + 8 neighbor `FillJobVoxelMap` snapshots/chunk).

The benchmark drives `Chunk.TickUpdate` **serially per chunk** (not `World.ProcessTickUpdatesParallel`), so the
`halo − managed` delta is the **added serial main-thread gather cost**, *net of* the managed-border work it replaces.
The parallel win (Phase 4a/4b across chunks) is **on top of** this and not measured here.

**Border fraction caveat (the win is understated here):** the scenarios seed a `[2,13]` box (MARGIN 2), so the
footprint is 144 columns of which the margin-4 interior is 8×8 = 64 → **~56 % of the fluids are border**. A *fully*
flooded 16×16 chunk is 256 columns with the same 64 interior → **75 % border**. So a real ocean chunk has an even
larger share of fluids on the slow managed path in the hybrid leg — the halo advantage **grows** with border fraction.

## Methodology

Mono editor build, Burst enabled + safety checks on. Each scenario runs 150 times per leg; each run rebuilds the
chunks fresh, runs `ticks` behavior steps, and contributes **one avg-ms/tick sample**. `min` = clean uninterrupted
floor (best CPU-cost proxy), `mean` includes GC/scheduler cost, `stddev` = spread (per-voxel `List<VoxelMod>` GC in
the managed leg), `peak` = single worst tick. `µs/voxel = min ms/tick × 1000 ÷ peak active voxels`.

## Result — Mono editor, ms/tick over 150 per-run samples

| Scenario          | Border  | Chunks | PeakActive |       mean |        min |     median | stddev |   peak | µs/voxel |
|-------------------|---------|-------:|-----------:|-----------:|-----------:|-----------:|-------:|-------:|---------:|
| Fluid-Small       | managed |      1 |        836 |      1.075 |      0.974 |      0.997 |  0.268 | 18.490 |    1.164 |
| Fluid-Small       | halo    |      1 |        836 |      0.566 |      0.557 |      0.562 |  0.012 |  1.583 |    0.666 |
| Fluid-Medium      | managed |      1 |       3456 |      2.616 |      2.390 |      2.418 |  0.663 | 25.347 |    0.692 |
| Fluid-Medium      | halo    |      1 |       3456 |      1.143 |      1.111 |      1.125 |  0.064 |  4.977 |    0.322 |
| Fluid-Large-4ch   | managed |      4 |      13824 |     10.304 |      9.609 |      9.692 |  1.606 | 70.610 |    0.695 |
| Fluid-Large-4ch   | halo    |      4 |      13824 |      4.612 |      4.550 |      4.585 |  0.126 | 20.537 |    0.329 |
| Cave-Fill-Cascade | managed |      1 |        712 |      1.112 |      1.054 |      1.067 |  0.223 | 17.768 |    1.481 |
| Cave-Fill-Cascade | halo    |      1 |        712 |      0.600 |      0.591 |      0.597 |  0.014 |  1.504 |    0.830 |
| **Ocean-25ch**    | managed | **25** |  **17800** | **28.478** | **26.693** | **27.103** |  2.895 | 91.405 |    1.500 |
| **Ocean-25ch**    | halo    | **25** |  **17800** | **15.834** | **15.694** | **15.806** |  0.108 | 35.302 |    0.882 |
| Grass-Field       | managed |      1 |        144 |      0.013 |      0.012 |      0.013 |  0.001 |  0.175 |    0.087 |
| Grass-Field       | halo    |      1 |        144 |      0.013 |      0.012 |      0.013 |  0.001 |  0.170 |    0.086 |
| Mixed-Lake+Grass  | managed |      1 |        944 |      1.117 |      1.058 |      1.071 |  0.242 | 15.533 |    1.120 |
| Mixed-Lake+Grass  | halo    |      1 |        944 |      0.618 |      0.607 |      0.614 |  0.013 |  1.864 |    0.643 |

## A/B delta — halo vs managed (per leg, same build)

| Scenario          | min managed → halo | **speedup** | stddev managed → halo |  peak managed → halo |
|-------------------|-------------------:|:-----------:|----------------------:|---------------------:|
| Fluid-Small       |   0.974 → 0.557 ms |  **1.75×**  |   0.268 → 0.012 (22×) |  18.49 → 1.58 (−91%) |
| Fluid-Medium      |   2.390 → 1.111 ms |  **2.15×**  |   0.663 → 0.064 (10×) |  25.35 → 4.98 (−80%) |
| Fluid-Large-4ch   |   9.609 → 4.550 ms |  **2.11×**  |   1.606 → 0.126 (13×) | 70.61 → 20.54 (−71%) |
| Cave-Fill-Cascade |   1.054 → 0.591 ms |  **1.78×**  |   0.223 → 0.014 (16×) |  17.77 → 1.50 (−92%) |
| **Ocean-25ch**    | 26.693 → 15.694 ms |  **1.70×**  |   2.895 → 0.108 (27×) | 91.41 → 35.30 (−61%) |
| Mixed-Lake+Grass  |   1.058 → 0.607 ms |  **1.74×**  |   0.242 → 0.013 (19×) |  15.53 → 1.86 (−88%) |
| Grass-Field       |   0.012 → 0.012 ms |   1.00× ⟂   |  0.001 → 0.001 (none) |    0.18 → 0.17 (~0%) |

`⟂` Grass-Field is the **control**: no fluids, so the border-burst flag is inert — the identical rows confirm the
sweep harness adds no measurement noise of its own.

## Reading the result

- **The halo path does *more* work yet runs *faster*.** Each `halo` tick adds 9 `FillJobVoxelMap` snapshots/chunk
  and moves every border voxel into the job — yet min ms/tick drops **1.70–2.15×**. The reason: the managed border
  path (per-voxel `List<VoxelMod>` GC + managed `GetVoxelState`) is far costlier per voxel than Burst, and ~56 % of
  each scenario's fluids are border. Bursting them **more than pays for the gather**.
- **The gather is not the dominant term — even at 25 chunks.** Ocean-25ch does 25 × 9 = 225 neighbor snapshots/tick
  and is still **1.70× faster**. Full-height gather cost is real but small next to the managed-border work it deletes.
- **GC variance and frame-spikes collapse — the real frame-stability prize.** stddev tightens **10–27×** and the
  single worst tick drops **61–92 %** (Cave-Fill peak `17.8 → 1.5 ms`; Ocean peak `91 → 35 ms`). The managed
  per-voxel `List<VoxelMod>` allocation was the spike source; full-Burst removes it. Peak ms/tick is what users see
  as stutter, so this matters as much as the mean.
- **The win generalizes and grows.** Real flooded chunks are ~75 % border (vs the benchmark's 56 %), so a live ocean
  has *more* fluids on the slow managed path in the hybrid leg — the halo margin widens in production.
- **Y-banding is a *future* optimization, not a prerequisite.** Full-height halo already wins decisively. The deferred
  Y-band (gather only `[minActiveY−1, maxActiveY+1]` instead of the full 128 rows) would shrink the per-tick gather
  further and make the volume height-independent — widening the margin — but there is **no GO blocker** without it
  (locked decision #1 confirmed: ship full height, band later).

## Verdict

| Question                                                    | Answer                                                                                                                                                                                                    |
|-------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Does the full-height halo gather cost outweigh its benefit? | **No — the opposite.** It is 1.70–2.15× *faster* serially than the managed-border hybrid; the gather is cheaper than the managed border it replaces.                                                      |
| Is the snapshot/gather cost a concern at ocean scale?       | **No.** Ocean-25ch (225 snapshots/tick) is still 1.70× faster and its frame-spikes drop 61 %.                                                                                                             |
| Is the Y-band optimization needed before shipping Phase 4b? | **No.** Full height already wins; the band is a deferred margin-widener (decision #1).                                                                                                                    |
| Should the flag flip on by default now?                     | **Not yet.** Stays off pending in-game visual confirmation (decision #2: keep behind a flag). Correctness is already gated by BH-D1[L\|H] + the C5 cross-chunk determinism gate; this adds the perf case. |

**Bottom line:** the full-height Phase-4b halo is a clear **GO** — it is *faster*, not a tax, even before
parallelization and before Y-banding, and it converts the managed border's GC-spiky tail into a flat, predictable
Burst cost. The parallel pass (Phase 4a/4b) compounds this further. The flag remains default-off until the in-game
flood is visually confirmed.

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz  (16 threads)
RAM:            65 381 MB
OS:             Windows 10 (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsEditor
Mode:           Editor
Backend:        Mono
Git commit:     4d31d1e (+ uncommitted Phase-4b A/B sweep)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

150 runs per scenario per leg, 5 warm-up ticks discarded. **Mono editor** run — IL2CPP would compress both legs, but
per the [Phase-3 profile gate](BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md) IL2CPP helps the *managed* tick only
~1.4× while Burst jobs take the full Burst win, so an IL2CPP recapture would most likely **widen** the halo advantage,
not narrow it. A confirming IL2CPP capture is a nice-to-have, not a gate.

## Cross-references

- **Design / plan:** [`Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md`](../Design/TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) Phase 4b (Option B halo gather).
- **Prior baseline:** [`BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md`](BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md) (the §5 profile gate — fully-managed tick).
- **Parallel determinism gate:** `Assets/Editor/Validation/Behavior/FluidParallelDeterminismValidation.cs` (`Validate Fluid Parallel Determinism (Cross-Chunk Halo)`).
- **Harness:** `Assets/Scripts/Benchmarks/FluidTickBenchmark.cs`, `Assets/Scripts/Benchmarks/FluidBenchmarkScenarios.cs`.
- **Folder conventions:** [`README.md`](README.md).

```
