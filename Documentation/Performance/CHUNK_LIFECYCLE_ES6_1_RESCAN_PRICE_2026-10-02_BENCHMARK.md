# ES-6.1 — Price of the `OnDataPopulated` Rescan on Visual Re-Attach

| Field           | Value                                                                                                       |
|-----------------|-------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-02                                                                                                  |
| **Branch**      | `main`                                                                                                      |
| **Commit**      | `654d6343` + uncommitted `T_current` legs in the benchmark (no `Chunk` / `ChunkData` change)                  |
| **Captured by** | `Minecraft Clone/Benchmarks/Active-Voxel Scan (TG-2)` (`Assets/Editor/Benchmarking/ActiveVoxelScanBenchmark.cs`), **Editor Mono, Burst on, safety checks on**, Unity 6000.6.4f1, i9-9900K; 3 runs × 5 batches × 100 chunks per scenario |
| **Verdict**     | **GO (screening) for ES-6.1 steps 2–3** — above the proposed 0.5 ms-per-crossing bar (vd 32) on every leg: Land 1.56 ms (first view) / 1.76 ms (re-entry), Flooded 42.8 / 20.1 ms. Owner decision, 2026-10-02; no IL2CPP confirmation needed. |

> Serves step 1 of the ES-6.1 execution packet in
> [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) §7.1: decide with
> numbers whether skipping the rescan on visual re-attach is worth giving up the safety net it provides (it
> re-wakes every quiesced voxel on re-entry, which hides any missing-wake bug). The materiality bar —
> **> 0.5 ms per crossing at vd 32 in IL2CPP** — is the packet's proposal, not a decision.

## What this measures (and what it does NOT)

**Measures:** the shipped `Chunk.OnDataPopulated()` itself — not a copy of it — on a `ChunkData` populated from a
real generation result through `ChunkData.PopulateFromFlattened`. It skips null and empty sections, reads
`World.IsActiveById` per voxel, and registers each active voxel with `ChunkData.AddActiveVoxel(pos, id)`, which
classifies it (`ClassifyFamily`, a `World.BlockTypes` read) and adds it to a lazily created native bucket. Two
timed calls per chunk:

- **First view** — buckets empty. A data-only chunk's first time in view: every startup chunk and every chunk in
  the LoadDistance > viewDistance ring, because their population-time registration is skipped (`Chunk?.`). This
  is the forward-travel case; packet step 1b would remove it from the crossing.
- **Re-entry** — the same call again, buckets already full. The re-entry case; packet step 1a's flag would
  remove it.

Per crossing = per-chunk cost × chunks entering view (2·vd + 1: 21 at vd 10, 65 at vd 32).

**Parity guard:** after each first call the two buckets must hold exactly the generation job's active-voxel
count for that chunk, and the re-entry call must leave that count unchanged; a mismatch, or a batch that
registers nothing, aborts the run. All three runs passed for all 1,000 chunk-scans per scenario.

**Does NOT measure:**

- **IL2CPP.** This is Editor Mono with native-container safety checks on, which should overstate the player's
  cost (unmeasured — an assumption). So a reading **above** the bar does not prove the IL2CPP cost is above it;
  only a reading below the bar would have been conclusive.
- **Frame context.** The scan is timed in isolation, warm, one chunk at a time — not inside a crossing frame
  with its other work and cache state.
- **A cave-heavy shape.** Deliberately skipped (owner decision, 2026-10-02): the scan's cost has two drivers,
  non-empty sections and active voxels, and Land / Flooded span both. Land already contains the Standard
  world type's normal cave carving.
- **Realistic ocean depth.** Flooded raises sea level to 110, filling ~7 sections of water per chunk; a real
  ocean chunk at the asset sea level holds fewer actives, so Flooded is the upper end.
- **GC.** The buckets are native; the scan allocates no managed memory of its own.

## Methodology

- **Fixture:** 100 chunks generated once per scenario with `EditorChunkPipelineRunner` (seed 1337, Standard
  world type, chunk coords 0–19 × 0–4). **Land** uses the asset sea level; **Flooded** forces sea level 110.
- **World stub:** the `T_current` legs need `World.Instance` (section pool, `BlockTypes`, `IsActiveById`). A
  stub `World` in edit mode carries the real `BlockDatabase` — the bucket routing reads its fluid types, so a
  test palette would not do — and the previous instance is restored afterwards (verified null after each run).
  The benchmark refuses to run in play mode.
- **Sample:** one call per chunk, timed with `Stopwatch.GetTimestamp`. Setup (population, the uninitialized
  `Chunk` carrying only its data link) and teardown (`Reset` returning sections to the pool, `Dispose`) are
  untimed.
- **Stats:** *best-batch mean* = the lowest of 5 batch means (the harness's established statistic, least
  noise); *median / p95 / max* = over all 500 per-chunk samples of a run. Per crossing uses the best-batch mean.
- **Runs:** 3, back to back, same session. A pre-run with identical timing code (before a log-format fix to the
  active-voxel average) read Land 23.98 / 26.76 µs and Flooded 641.66 / 315.72 µs, in line with the runs below.

## Result — Editor Mono, µs per chunk over 3 runs

### Shipped scan (`T_current`)

| Scenario | Leg | Run | Best-batch mean | Median | p95 | Max | Per crossing vd 10 | Per crossing vd 32 |
|----------|-----|-----|-----------------|--------|-----|-----|--------------------|--------------------|
| Land | first view | 1 | 27.66 | 26.40 | 44.30 | 2,297.40 | 0.581 ms | 1.798 ms |
| Land | first view | 2 | 24.03 | 26.30 | 34.50 | 73.60 | 0.505 ms | 1.562 ms |
| Land | first view | 3 | **23.95** | 26.30 | 36.10 | 69.90 | 0.503 ms | **1.557 ms** |
| Land | re-entry | 1 | 32.15 | 29.30 | 51.20 | 675.90 | 0.675 ms | 2.090 ms |
| Land | re-entry | 2 | **27.09** | 29.00 | 40.70 | 75.30 | 0.569 ms | **1.761 ms** |
| Land | re-entry | 3 | 27.54 | 29.00 | 44.90 | 78.80 | 0.578 ms | 1.790 ms |
| Flooded | first view | 1 | 658.96 | 665.00 | 718.20 | 1,198.50 | 13.84 ms | 42.83 ms |
| Flooded | first view | 2 | **657.47** | 663.50 | 749.70 | 1,246.80 | 13.81 ms | **42.74 ms** |
| Flooded | first view | 3 | 658.45 | 665.20 | 727.90 | 1,288.60 | 13.83 ms | 42.80 ms |
| Flooded | re-entry | 1 | 310.57 | 314.20 | 352.30 | 547.70 | 6.52 ms | 20.19 ms |
| Flooded | re-entry | 2 | 310.38 | 312.00 | 350.50 | 640.80 | 6.52 ms | 20.17 ms |
| Flooded | re-entry | 3 | **307.85** | 312.60 | 369.80 | 634.20 | 6.46 ms | **20.01 ms** |

Average per chunk: Land 4.52 non-empty sections and 0.5 active voxels; Flooded 7.00 sections and 12,010.3 active
voxels. Run 1's Land legs are the first run after a domain reload (one 2.3 ms single-chunk spike) and read
~15 % high; runs 2–3 agree within 0.4 % on Land and all three within 0.3 % on Flooded. Bold = best run.

### TG-2 legs (continuity with the original benchmark)

| Scenario | Run | `T_old` | `T_bitmask` | `T_register` | `T_job` (Burst) |
|----------|-----|---------|-------------|--------------|-----------------|
| Land | 1 | 51.28 | 33.32 | 0.06 | 67.74 |
| Land | 2 | 50.93 | 33.09 | 0.06 | 67.42 |
| Land | 3 | 51.25 | 33.14 | 0.06 | 67.51 |
| Flooded | 1 | 417.77 | 405.33 | 393.36 | 101.86 |
| Flooded | 2 | 412.95 | 402.18 | 390.18 | 101.70 |
| Flooded | 3 | 413.50 | 402.86 | 390.59 | 101.12 |

## Analysis

- **Land is scan-bound.** With half an active voxel per chunk the cost is the walk over ~4.5 non-empty sections
  (~18,500 id lookups). The shipped section skip makes it ~28 % cheaper than `T_bitmask`'s full flat-map walk
  (23.95 vs 33.14 µs) — so pricing on `T_bitmask`, as the packet first proposed, would have overstated land.
- **Flooded is insertion-bound.** First view (658 µs) costs about twice re-entry (308 µs): the first call creates
  each bucket at a 64-entry capacity and grows it to ~12,000 entries, while re-entry only re-adds entries that are
  already there. `T_bitmask` (403 µs) **understates** the first view by ~39 %, because its managed set keeps its
  capacity from chunk to chunk — the opposite error to land.
- **Every leg is above the bar in the Editor.** At vd 32: Land 1.56 / 1.76 ms, Flooded 42.8 / 20.1 ms against
  0.5 ms. For land to fall under it in IL2CPP the player would need to be ≥ 3.1× (first view) / ≥ 3.5×
  (re-entry) faster than the Editor; for Flooded, ≥ 85× / ≥ 40×. The Editor cannot settle the land case; on
  water-heavy chunks no plausible backend speedup brings it under the bar.
- **What each packet step would remove.** Step 1a (the "buckets valid" flag) removes the whole re-entry cost
  from the crossing. Step 1b (data-only generated chunks register from the job's list) removes the first-view
  *scan* from the crossing — but not the bucket insertion, which moves to the generation pass instead
  (`T_register` 390 µs on Flooded, 0.06 µs on Land). So 1b removes land's first-view cost outright and moves
  most of ocean's to generation time.

## Verdict details

**GO — implement ES-6.1 steps 2–3 (packet items 1a + 1b) in their own session.** The water-heavy case is
material under any plausible backend speedup, and both steps also remove land's cost, so pinning the land
case's exact IL2CPP number would not change the decision; the IL2CPP harness the packet held in reserve is
therefore not built. What ships is the packet's design as written: the `_activeVoxelsRegistered` flag on
`ChunkData` (1a, removes the re-entry rescan), registration from the generation job's list for data-only
chunks (1b, takes the first-view scan off the crossing), the BH-B13 scenarios with their prove-red, and an
in-game soak at chunk re-entry for fluids and grass — the risk being a quiesced voxel that should have woken,
which today's rescan would have masked. The disk-load path keeps its rescan.
