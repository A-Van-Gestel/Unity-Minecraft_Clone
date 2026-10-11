# ES-31 — Registration Screen: Register Only the Voxels That Can Act, Before and After

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-11                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| **Commit**      | after: `586f27e8` + the uncommitted ES-31 change; before: a fresh Master build of `586f27e8` from a clean tree                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| **Captured by** | **IL2CPP player, Master configuration, non-development**, Burst AOT, D3D11, Unity 6000.6.5f1, i9-9900K / RTX 4070 Ti, `-mc-set perfMonitorTier=Capture`, legs alternated in one session: benchmark route Gen = `200`, loading pass `50` m/s, vd 10 × 2 per leg; the same at vd 32 × 1 per leg; `-mc-run startup-new` × 2 per leg. Session files read per phase with `Tools/Python/summarize_perf_session.py`, run-end hitch records with `Tools/Python/tabulate_tick_hitches.py`, `TickFluidVoxels` / `TickWaitUs` summed and peaked straight from the session CSVs                                                  |
| **Verdict**     | **GO.** The fluid voxels the tick evaluates fall **24.3 M → 0.13 M per vd-10 session** (−99.4 %) and **85.2 M → 0.72 M at vd 32**; the per-frame peak falls 1.30 M → 3–4 k (vd 10) and 2.79 M → 8.8 k (vd 32). The tick's cost per frame falls 0.41–0.44 → 0.06–0.07 ms at 200 m/s (vd 10) and 1.10 → 0.21 ms (vd 32); the wait's worst frame 39–47 → 3–4 ms and 72 → 5.5 ms; the replay's removals vanish. Frame-level: Tick-led hitch records 1 / 3 → 0 / 0 per vd-10 run; vd 32's loading pass 98 → 34 hitches, p99 40.7 → 34.0 ms, worst 101 → 72 ms. Drained → stable is unscored (2 runs per leg, known noisy) |

> Scores [`ES-31`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md), filed by ES-29's probe
> ([`ENGINE_SCALING_ES29_BEHAVIOR_TICK_IL2CPP_2026-10-11`](ENGINE_SCALING_ES29_BEHAVIOR_TICK_IL2CPP_2026-10-11_BENCHMARK.md)).
> Both legs are new builds of the same base, run alternately in one session (the separate-build rule in
> [`README.md`](README.md)).

## The change

- **The registration screen** (`Helpers/ActiveVoxelScreen`): a full registration keeps only the voxels the tick would act
  on, by the tick's own rules read inside the chunk. A level-0 fluid source is kept when it can fall or a cardinal cell is
  receptive (air, a non-solid `REPLACEABLE` block, the same fluid not falling and deeper than level 1); flowing fluid is
  always kept; grass is kept under a solid block or beside a convertible-dirt candidate. Generation's
  `ActiveVoxelScanJob` applies it, and `RescanActiveVoxels` (disk loads) now runs that Burst job over a flat copy.
- **The two-sided population seam wake**: a cross-seam read is left to the seam wake, which now also wakes the populated
  chunk's own slab, and wakes a level-0 source only across a cell it can spread into.
- **The pending-modification replay wakes** each changed cell's six neighbors (`World.ReplayPendingMods`).
- Exactness is proven in the Editor: `BH-B16` (every screened voxel is inactive with no mods on the real jobs), `BH-B17`
  (screened-then-tick ≡ blanket-then-tick on both drivers), `BH-B18`/`BH-B19` (the two wakes), each proven red; Validate
  All 820/820; the live `TickShapeProbe.AuditSleepers` reads 0 after an ocean burst and after a disk reload.

## Before measuring — the Editor (seed 4242, vd 10, the ES-29 ocean teleport, same moment)

| Measure                                | Before (blanket) | After (screened) |
|----------------------------------------|------------------|------------------|
| Registered fluid voxels                | 269 508          | 1 124            |
| Fluid bands (sections deep)            | 1–4              | 1–2              |
| Registered grass voxels                | 8 856            | 32               |
| Sleepers the tick would act on (audit) | 0                | 0                |
| Rescan, flooded chunk, first view      | 689 µs           | 458 µs           |
| Rescan, land chunk, first view         | 28 µs            | 18 µs            |

The rescan figures are the Editor's `Active-Voxel Scan (TG-2)` benchmark (Mono, Burst safety checks on). A managed
screen in the rescan measured 1 135 µs on the flooded chunk — more than the bucket inserts it saved — which is why the
rescan runs the Burst job. The generation job's own cost rises 115 → 417 µs per flooded chunk with safety checks on
(153 µs off), on a worker thread.

## Result — the behavior tick (ms per frame, per phase)

| Run      | Phase                | Tick  | Prepare | Wait  | Replay | Fluid chunks/frame | Grass voxels/frame |
|----------|----------------------|-------|---------|-------|--------|--------------------|--------------------|
| A vd10-1 | Generation (200 m/s) | 0.409 | 0.124   | 0.228 | 0.036  | 2.3                | 598                |
| A vd10-2 | Generation (200 m/s) | 0.444 | 0.133   | 0.257 | 0.036  | 2.5                | 636                |
| B vd10-1 | Generation (200 m/s) | 0.067 | 0.029   | 0.024 | 0.000  | 0.3                | 286                |
| B vd10-2 | Generation (200 m/s) | 0.061 | 0.029   | 0.021 | 0.000  | 0.3                | 293                |
| A vd10-1 | Loading (50 m/s)     | 0.011 | 0.004   | 0.005 | 0.001  | 0.1                | 19                 |
| B vd10-1 | Loading (50 m/s)     | 0.003 | 0.001   | 0.001 | 0.000  | 0.0                | 5                  |
| A vd32   | Generation (200 m/s) | 1.101 | 0.303   | 0.625 | 0.103  | 7.0                | 836                |
| B vd32   | Generation (200 m/s) | 0.212 | 0.085   | 0.062 | 0.002  | 2.6                | 520                |
| A vd32   | Ensure Generated     | 0.263 | 0.110   | 0.111 | 0.018  | 3.3                | 325                |
| B vd32   | Ensure Generated     | 0.097 | 0.056   | 0.020 | 0.001  | 2.3                | 103                |
| A vd32   | Loading (50 m/s)     | 0.298 | 0.123   | 0.128 | 0.023  | 3.2                | 457                |
| B vd32   | Loading (50 m/s)     | 0.088 | 0.051   | 0.019 | 0.001  | 1.8                | 98                 |

| Run      | Fluid voxels ticked (session) | Peak per frame | Worst `TickWaitUs` frame | Tick-led hitch records |
|----------|-------------------------------|----------------|--------------------------|------------------------|
| A vd10-1 | 24 263 319                    | 1 304 728      | 39.4 ms                  | 1 of 16                |
| A vd10-2 | 24 286 681                    | 1 319 422      | 46.5 ms                  | 3 of 13                |
| B vd10-1 | 134 345                       | 3 086          | 4.4 ms                   | 0 of 9                 |
| B vd10-2 | 134 651                       | 4 449          | 2.9 ms                   | 0 of 8                 |
| A vd32   | 85 162 973                    | 2 786 999      | 72.0 ms                  | 2 of 16 (Wait)         |
| B vd32   | 716 501                       | 8 794          | 5.5 ms                   | 2 of 16 (Prepare)      |

## Result — frame level

| Run      | Phase            | Frames | Wall p50 | Wall p99 | Worst    | Hitches | Avg FPS |
|----------|------------------|--------|----------|----------|----------|---------|---------|
| A vd10-1 | Generation       | 970    | 33.2 ms  | 72.5 ms  | 100.4 ms | 108     | 39.2    |
| A vd10-2 | Generation       | 911    | 35.7 ms  | 74.8 ms  | 130.1 ms | 104     | 35.8    |
| B vd10-1 | Generation       | 1 180  | 18.5 ms  | 68.2 ms  | 82.9 ms  | 119     | 48.0    |
| B vd10-2 | Generation       | 1 151  | 24.4 ms  | 69.4 ms  | 80.9 ms  | 105     | 45.7    |
| A vd10-1 | Ensure Generated | 70 007 | 1.4 ms   | 9.7 ms   | 42.2 ms  | 3       | 440.2   |
| A vd10-2 | Ensure Generated | 71 189 | 1.3 ms   | 9.4 ms   | 33.9 ms  | 1       | 447.2   |
| B vd10-1 | Ensure Generated | 70 712 | 1.4 ms   | 8.7 ms   | 22.4 ms  | 0       | 444.0   |
| B vd10-2 | Ensure Generated | 70 819 | 1.4 ms   | 8.7 ms   | 31.4 ms  | 0       | 444.9   |
| A vd10-1 | Loading          | 14 468 | 1.2 ms   | 8.6 ms   | 41.8 ms  | 1       | 488.5   |
| A vd10-2 | Loading          | 14 933 | 1.2 ms   | 8.6 ms   | 24.5 ms  | 0       | 503.2   |
| B vd10-1 | Loading          | 13 853 | 1.3 ms   | 8.8 ms   | 18.3 ms  | 0       | 465.4   |
| B vd10-2 | Loading          | 14 184 | 1.2 ms   | 8.2 ms   | 18.6 ms  | 0       | 475.7   |
| A vd32   | Generation       | 673    | 45.6 ms  | 88.8 ms  | 136.3 ms | 144     | 24.2    |
| B vd32   | Generation       | 661    | 47.3 ms  | 75.9 ms  | 87.4 ms  | 140     | 23.4    |
| A vd32   | Ensure Generated | 13 709 | 10.1 ms  | 57.1 ms  | 150.5 ms | 474     | 72.2    |
| B vd32   | Ensure Generated | 14 312 | 9.9 ms   | 54.1 ms  | 173.7 ms | 237     | 75.5    |
| A vd32   | Loading          | 2 370  | 9.2 ms   | 40.7 ms  | 101.2 ms | 98      | 82.0    |
| B vd32   | Loading          | 2 725  | 8.8 ms   | 34.0 ms  | 72.2 ms  | 34      | 93.1    |

The 200 m/s pass stays merge-bound — its hitch count does not move — but its median frame falls 33–36 → 18–24 ms at vd 10
and its worst frame 100–130 → 81–83 ms. The vd-32 rows are one run per leg.

## Startup (`-mc-run startup-new`, vd 10)

| Run | Handoff | Drained | Stable |
|-----|---------|---------|--------|
| A-1 | 3 828   | 5 166   | 26 358 |
| A-2 | 3 759   | 5 543   | 25 319 |
| B-1 | 3 656   | 4 922   | 35 218 |
| B-2 | 3 737   | 4 985   | 16 448 |

(ms after the scene load request). Drained moves 5.2–5.5 → 4.9–5.0 s; drained → stable spreads 11–30 s in B against
20–21 s in A. Two runs per leg cannot score a tail whose run-to-run spread is known to reach 4–75 s (ES-29's five-run
rule); it is left unscored.

## Caveats

- **Both legs' reports read `Git commit: 586f27e8-dirty`.** A was built from a clean tree (the ES-31 change stashed: zero
  changed paths, `ActiveVoxelScreen.cs` absent) and its counters show blanket registration. The `-dirty` is most likely
  assets the build itself writes before the stamp is baked (the runner restored `BuildStamp.asset` and the URP global
  settings after each build) — not investigated.
- Two vd-10 runs and one vd-32 run per leg — the tick-counter deltas are two orders of magnitude, far outside run noise;
  the frame-level vd-32 rows are a single pair.
