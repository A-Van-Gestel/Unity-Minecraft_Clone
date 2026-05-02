# Phase 2 Baseline — Mesh Generation

| Field                 | Value                                                                |
|-----------------------|----------------------------------------------------------------------|
| **Captured**          | 2026-04-25 21:41 (local)                                             |
| **Branch**            | `feat/per-block-metadata-schemas`                                    |
| **Commit**            | `ba70f12`                                                            |
| **Save version**      | v5 (post-`MigrationV4ToV5VoxelModMeta`)                              |
| **Baseline kind**     | Working baseline for relative regression detection                   |
| **Regression budget** | ≤ 5% per pattern (per `PER_BLOCK_METADATA_SCHEMAS.md §11 Phase 4.4`) |
| **Captured by**       | `Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs`               |
| **Total wall-clock**  | 11m 0s                                                               |

## Why this baseline exists

Captured before Phase 2 of the per-block-metadata-schemas implementation. Phase 2 will:

- Rewrite the per-face Y-rotation block in `VoxelMeshHelper.GenerateStandardCubeFace` (line 72: `Quaternion.Euler(0, rotation, 0)`).
- Add a precomputed face/UV variant path for `MetadataSchema.Axis3` blocks instead of per-voxel quaternion rotation (per design doc §8.1 meshing note).
- Add schema-aware dispatch in `MeshGenerationJob.GenerateVoxelMeshData`.

Any Phase 2 commit that touches meshing must be re-benchmarked against the per-pattern numbers below. Patterns whose `μs/chunk` value exceeds the +5% threshold are considered regressions and must be investigated before merging.

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
Unity:          6000.4.4f1
Platform:       WindowsEditor
Mode:           Editor
Backend:        Mono
Git commit:     ba70f12

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

## Capture configuration

- **Chunks per run:** 256
- **Runs per scenario:** 50
- **Modes:** `WithDiagonals`, `CardinalsOnly` (every pattern × both modes = 14 scenarios)
- **Warm-up:** one discarded mesh job per scenario before the timed loop, to absorb Burst JIT, JobsUtility setup, and first-touch allocator overhead.

## Important caveats — read before comparing

1. **Burst Safety Checks are Enabled.** Editor default. The absolute numbers are not "ship absolute" — they are deliberately consistent relative measurements. **Any post-refactor capture used for regression comparison MUST also be captured with `Burst Safety Checks = Enabled` to keep the apples-to-apples comparison valid.**
2. **Burst Synchronous Compilation is Disabled.** The benchmark's per-scenario warm-up run forces compilation before timing, so async compilation does not contaminate the first iteration. If the warm-up is later removed, set `Synchronous = Enabled` instead.
3. **Mono backend (Editor).** Hot-path numbers are typically within ~10–20% of an IL2CPP build because the heavy work is Burst-compiled and the relative spread is what matters here. For a ship-readiness absolute number, capture a separate IL2CPP baseline at end of Phase 2 and append it to this file under a clearly labelled section.
4. **Run-to-run jitter.** Re-running the same code on the same hardware typically produces ~2-3% spread per pattern. The 5% budget allows for jitter + genuine improvement/regression. A single 5% over-baseline result should be confirmed with a second run before being treated as a real regression.
5. **Locale.** The captured report uses `,` as the decimal separator and ` ` (space) as the thousands separator (Belgian Dutch / European convention). The verbatim numbers below preserve this; the per-pattern analysis tables further down use `.` for portability. Both refer to the same underlying values.

## Verbatim captured report

```text
--- MESH GENERATION BENCHMARK REPORT ---
Test configuration: 256 chunks per run, averaged over 50 runs.
All numbers are: ms per run (256 chunks) | μs per chunk (derived).
Total wall-clock runtime: 11m 0s

=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz
CPU threads:    16
CPU base MHz:   3600
RAM:            65 381 MB
OS:             Windows 10  (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.4.4f1
Platform:       WindowsEditor
Mode:           Editor
Backend:        Mono
Git commit:     ba70f12

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
--- Solid ---
  - With Diagonals  :   137 ms  (  535,2 μs/chunk)
  - Cardinals Only  :   135 ms  (  527,3 μs/chunk)
  - Winner: CardinalsOnly by 2 ms (1,5% faster)

--- Checkerboard ---
  - With Diagonals  :  1118 ms  ( 4367,2 μs/chunk)
  - Cardinals Only  :  1115 ms  ( 4355,5 μs/chunk)
  - Winner: CardinalsOnly by 3 ms (0,3% faster)

--- OrientedCubes ---
  - With Diagonals  :   135 ms  (  527,3 μs/chunk)
  - Cardinals Only  :   136 ms  (  531,3 μs/chunk)
  - Winner: WithDiagonals by 1 ms (0,7% faster)

--- OrientedCheckerboard ---
  - With Diagonals  :  1148 ms  ( 4484,4 μs/chunk)
  - Cardinals Only  :  1151 ms  ( 4496,1 μs/chunk)
  - Winner: WithDiagonals by 3 ms (0,3% faster)

--- Fluid ---
  - With Diagonals  :   716 ms  ( 2796,9 μs/chunk)
  - Cardinals Only  :   716 ms  ( 2796,9 μs/chunk)
  - Result: Tied Performance.

--- Transparent ---
  - With Diagonals  :  1354 ms  ( 5289,1 μs/chunk)
  - Cardinals Only  :  1362 ms  ( 5320,3 μs/chunk)
  - Winner: WithDiagonals by 8 ms (0,6% faster)

--- MixedTerrain ---
  - With Diagonals  :   649 ms  ( 2535,2 μs/chunk)
  - Cardinals Only  :   647 ms  ( 2527,3 μs/chunk)
  - Winner: CardinalsOnly by 2 ms (0,3% faster)
```

## Per-pattern budget

`Mode-averaged` = mean of `WithDiagonals` and `CardinalsOnly` (they are statistically identical in every pattern, well under 1% spread).

| Pattern                | Mode-averaged baseline | +5% regression threshold | What it measures                                                                                               |
|------------------------|-----------------------:|-------------------------:|----------------------------------------------------------------------------------------------------------------|
| `Solid`                |         531.3 μs/chunk |           557.9 μs/chunk | Standard cube path, all-stone interior cull (vertex-throughput floor)                                          |
| `Checkerboard`         |        4361.4 μs/chunk |          4579.5 μs/chunk | Standard cube worst case — every stone has 6 air neighbors → 6 faces drawn (no rotation)                       |
| `OrientedCubes`        |         529.3 μs/chunk |           555.8 μs/chunk | Cycling orientations 0/1/4/5; rotation only exercised on boundary faces (~1.5k/chunk)                          |
| `OrientedCheckerboard` |        4490.3 μs/chunk |          4714.8 μs/chunk | **Primary rotation hot-path detector.** Checkerboard density × cycling orientations → ~98k rotated faces/chunk |
| `Fluid`                |        2796.9 μs/chunk |          2936.7 μs/chunk | `GenerateFluidMeshData` end-to-end with `FluidLevel4` semantics                                                |
| `Transparent`          |        5304.7 μs/chunk |          5569.9 μs/chunk | Transparent submesh path, alternating leaves/air at Checkerboard density                                       |
| `MixedTerrain`         |        2531.3 μs/chunk |          2657.9 μs/chunk | Realistic distribution exercising all four `MeshGenerationJob` render cases                                    |

## Reading the numbers

### Rotation cost is observable

The new `OrientedCheckerboard` pattern (4490 μs/chunk) is **~3% slower than `Checkerboard`** (4361 μs/chunk). They process the same number of faces; the only difference is rotation. So the per-face rotation overhead in `VoxelMeshHelper.GenerateStandardCubeFace` is approximately:

```
4490 - 4361 = 129 μs/chunk
129 μs / ~98_304 faces ≈ 1.3 ns/face
```

That ~1.3 ns/face is what Phase 2b's precomputed-variant rewrite is replacing. A correctness-preserving rewrite that doesn't break `Quaternion.Euler` semantics should land somewhere in the 0.3–1.0 ns/face range; anything above 1.3 ns/face is a regression to flag.

### `OrientedCubes ≈ Solid` (529 vs 531 μs/chunk)

All-stone interiors get culled before reaching the rotation code. Only chunk-boundary faces (≈ 1500 of ~196k) actually exercise rotation. Magnitude of detectable regression on `OrientedCubes` alone is small — that's why `OrientedCheckerboard` was added. **Use `OrientedCheckerboard` as the primary rotation-path regression detector; treat `OrientedCubes` as supplementary signal.**

### `Checkerboard` (4361 μs/chunk) is ~8.2× `Solid`

Confirms the per-face vertex generation cost dominates over the per-voxel work for exposed-face-heavy chunks. Standard-cube vertex generation is the largest budget item in this benchmark.

### `Transparent` (5305 μs/chunk) is the most expensive pattern

The transparent submesh path adds a small constant overhead over `Checkerboard`. Validates that the transparent triangle pathway is roughly comparable in cost to the opaque path under matched density.

### `Fluid` (2797 μs/chunk) and `MixedTerrain` (2531 μs/chunk) are in the same ballpark

`MixedTerrain` is dominated by its 70% Stone fraction (mostly culled) but slowed by the 5% Fluid voxels, so the overall μs/chunk lands close to a chunk that is half-fluid half-air on its dominant path.

### `WithDiagonals` vs `CardinalsOnly`

Differ by < 1% in every pattern. The 8 vs 4 neighbor-map cost is dominated by the per-voxel meshing work; the diagonal data is essentially free.

## Re-capturing post-refactor

After each Phase 2 commit that touches `VoxelMeshHelper`, `MeshGenerationJob`, `EditorMeshGenerator`, or any code reachable from them:

1. Confirm Inspector settings on `MeshGenerationBenchmark` are unchanged (`Benchmark Runs = 50`, `Chunks To Mesh = 256`, both modes, `Write Report To File = true`).
2. Confirm Burst settings are unchanged (`Safety Checks = Enabled`, `Synchronous = Disabled`, `Compilation = Enabled`).
3. Run the benchmark.
4. Compare the new `μs/chunk` per pattern against the **mode-averaged baseline** above. Any pattern exceeding its +5% threshold is a regression.
5. If a single pattern is over budget, re-run once more to rule out jitter (~2-3% per-run spread is normal). If both runs are over budget, investigate before merging.
6. **`OrientedCheckerboard` is the rotation regression canary.** If `OrientedCheckerboard` regresses but `Checkerboard` does not, the regression is localised in the rotation/face-translation code path. That makes Phase 2b commits the prime suspect.
7. If the Phase 2b rewrite intentionally regresses a pattern in service of correctness (e.g., `Axis3` blocks now do real per-axis face selection instead of identity), document the trade-off explicitly in the commit message and update this file's per-pattern budget for the affected pattern only.

## Cross-references

- **Design doc** that motivated this baseline: [`Documentation/Design/PER_BLOCK_METADATA_SCHEMAS.md`](../Design/PER_BLOCK_METADATA_SCHEMAS.md) — `§11 Phase 4.4` sets the 5% meshing budget.
- **Folder conventions:** [`Documentation/Performance/README.md`](README.md).
- **Benchmark source:** [`Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs`](../../Assets/Scripts/Benchmarks/MeshGenerationBenchmark.cs).
- **Environment helper:** [`Assets/Scripts/Benchmarks/BenchmarkEnvironment.cs`](../../Assets/Scripts/Benchmarks/BenchmarkEnvironment.cs).
