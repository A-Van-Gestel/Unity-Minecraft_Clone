# Seam-Wake Pass (Fluid §19) — Gate A/B Screening

| Field           | Value                                                                                                       |
|-----------------|-------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-27                                                                                                  |
| **Branch**      | `feat/world-scaling`                                                                                        |
| **Commit**      | `1757abfd` + uncommitted seam-wake work                                                                     |
| **Captured by** | `Minecraft Clone/Benchmarks/Seam Wake (Fluid 19)` (`Assets/Editor/Benchmarking/SeamWakeBenchmark.cs`), **Editor Mono**, 200 runs + 20 warm-ups per scenario |
| **Verdict**     | **GO (screening) for the pair-walk gate** — 13.5× on land/grass seams, y+1 widen free. **Ocean seam cost recorded, not gated: needs an IL2CPP fill-load capture before the P-4 flags are retired.** |

## What this measures (and what it does NOT)

**Measures:** one `SeamWakeDecision.WakeSeamSlab` call — the main-thread work a chunk population
adds for **one** already-populated cardinal neighbor. A population runs up to four, so the
per-population column is `mean × 4` (worst case: all four cardinals populated).

**Does NOT measure:**

- **The downstream tick inflation.** Woken voxels enter the fluid bucket and are fully evaluated
  by `FluidTickJob` on the next tick before quiescing again. On the ocean leg that is 528 × 4 ≈
  2,100 extra fluids for one tick — job time on a worker plus per-source drain bookkeeping on the
  main thread. **This is unmeasured and is plausibly the larger of the two costs.**
- **Frame-level impact.** Editor Mono, no P-4 admission pacing, no real fill-load. The shipping
  number is the standing P-4 fill-load harness on an IL2CPP Development Build.
- **Allocation.** Editor Mono allocation claims are unreliable (project precedent); not asserted.

## Methodology

Three scenarios over a synthetic chunk pair built on `BehaviorTestWorld` (it already stands up
`World.Instance`, `ChunkPool`, and the `IsActiveById`/`IsSolidById` tables). The woken chunk's
seam column is filled per scenario; the chunk "that just populated" faces it across the seam.
Active buckets are cleared **outside** the timed region so each run starts from the same state
and the reset cost is not attributed to the pass.

| Leg            | Woken side (seam column)   | Populated side (facing slab)               | Gate behaviour                        |
|----------------|----------------------------|--------------------------------------------|---------------------------------------|
| Ocean seam     | Water y=30..62, all 16 z   | Water, same rows                            | admits every cell (water is non-solid) |
| Land seam      | Grass row at y=40          | Stone, y=0..40                               | same-Y rejects → y+1 rejects → skip    |
| Grass seam     | Grass row at y=40          | Stone + **Dirt at y=41** (up-diagonal target) | same-Y rejects → **y+1 admits**        |

## Results (µs per `WakeSeamSlab` call, editor Mono)

| Scenario   | Cells scanned | Voxels woken | Mean (µs) | Min (µs) | ×4 = per population (µs) |
|------------|---------------|--------------|-----------|----------|---------------------------|
| Ocean seam | 2048          | 528          | 19.49     | 18.50    | 77.95                     |
| Land seam  | 2048          | 16           | 1.44      | 1.30     | 5.78                      |
| Grass seam | 2048          | 16           | 1.40      | 1.20     | 5.61                      |

## What the numbers settle

1. **The gate is worth keeping.** Land/grass seams cost **1.44 µs vs the ocean leg's 19.49 µs** over
   an identical 2048-cell scan. The difference is entirely per-hit work (`AddActiveVoxel` →
   `ClassifyFamily`'s managed `BlockType` deref + two native hash-set ops), confirming the review's
   reading that hits — not the scan — dominate. Ordinary land/underground populations pay ~13.5×
   less than they would unconditionally.
2. **The y+1 widen is free.** Grass (1.40 µs) is within noise of land (1.44 µs) even though the
   y+1 sample runs for *every* cell on those legs — the same-Y sample rejects, so the reject path
   pays for both. The correctness fix for grass costs nothing measurable.
3. **The ocean seam is the real cost and the gate does not touch it**, exactly as predicted: water
   is non-solid, so all 528 active cells per neighbor are admitted. ~78 µs of main-thread work per
   ocean population, on top of an unmeasured extra tick over ~2,100 voxels.

For scale: the P-4 §3.4 per-pass ms ceilings are 8/6/6/4 ms. At 78 µs, ~77 simultaneous ocean
populations would consume one 6 ms ceiling — but the wake runs in the `_completedGenJobs` sweep
*after* the budget checks, so it is bounded only indirectly by how many chunks are admitted per
frame, and the ceiling never measures it.

## Outstanding

- **IL2CPP fill-load capture over open ocean** — the only number that can gate this. Suggested
  alongside the P-4 flag-retirement work, which already uses that harness.
- **A measurement of the extra-tick cost**, which this harness deliberately does not cover.

## Cross-links

- `Documentation/Bugs/_FIXED_BUGS.md` → Fluid §19 (the pass this measures)
- `Documentation/Architecture/CHUNK_LIFECYCLE_PIPELINE.md` §3.4 (the seam wake's contract)
- Guarded by `BH-B10` / `BH-B11` (`Validate Behavior`)
