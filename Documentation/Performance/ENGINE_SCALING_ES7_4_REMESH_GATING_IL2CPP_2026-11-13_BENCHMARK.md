# ES-7.4 — Merge Remesh Gating: Gate Off, Gate On, Gate On Without Diagonals

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-11-13                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Commit**      | `341eb293` (ES-7.4 `dc061c06` plus its review fixes); one fresh Master build from a clean tree, the legs switched by flag                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| **Captured by** | `BenchmarkController` (`-mc-run benchmark`) — **IL2CPP player, Master configuration, non-development**, Burst AOT, D3D11, Unity 6000.6.8f1, i9-9900K, idle machine. Three legs alternated in one session: route Gen = `200`, loading `50` m/s, vd 10 × 3 per leg; Gen = `50`, loading `50` m/s, vd 32 × 2 per leg. `-mc-set perfMonitorTier=Capture`, budgets pinned (`maxMeshRebuildsPerFrame=10`, `maxLightJobsPerFrame=32`). Session files read per phase with `Tools/Python/summarize_perf_session.py` (every cross-check: "every phase matches"), legs compared with `Tools/Python/summarize_ab_legs.py`                                                                          |
| **Verdict**     | **GO for the gate (provisional, pending the user's in-game seam check).** In the generation pass the gate removes **81 % (vd 10) and 96 % (vd 32) of the neighbor re-meshes** stable merges queued; mesh jobs completed fall **14 399 → 11 196 (−22 %)** at 200 m/s and **15 092 → 9 692 (−36 %)** at vd 32, for the same light work (light jobs ±1.5 %). `MeshSchedule` −35 % / −50 %. vd 32 frame level: wall p50 **18.4 → 11.5 ms**, p99 49.7 → 45.7 ms, 26 % more frames, frames over 40 ms 104 → 63. Diagonals cost no measurable generation mesh jobs (DIAG −2.4 % / −0.1 % against NODG, noise) and +10 % / +3 % in the loading pass, and buy the absence of stale corner light |

> Scores [`ES-7`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) item 7.4. The price it was planned against is in
> [`ENGINE_SCALING_ES7_LIGHTING_CUTS_IL2CPP_2026-10-18`](ENGINE_SCALING_ES7_LIGHTING_CUTS_IL2CPP_2026-10-18_BENCHMARK.md)
> (87 % of stable merges changed no light and newly queued 88 % of the neighbor rebuilds).

## The change

- Each `ChunkData` carries `MeshInputsCurrent`: set when a mesh job snapshots the chunk, cleared by any light or voxel
  write the mesher reads — its own, or one in the facing border column or corner of a neighbor
  (`ChunkBorderMask`, `MeshInputPropagation`).
- A stable lighting merge re-meshes itself unconditionally and, with `gateMergeNeighborRemeshes` on, only the
  neighbors whose flag is clear — cardinals, and diagonals when `gateMergeIncludeDiagonals` is on
  (`MergeRemeshDecision`). With the gate off it re-meshes the four cardinals as before.
- The three legs: **OFF** (`gateMergeNeighborRemeshes=false`), **DIAG** (both flags on, the defaults), **NODG**
  (gate on, `gateMergeIncludeDiagonals=false`). Each report's "Settings (differing from defaults)" block shows its leg.
- Correctness was gated in the Editor before this capture: the mesh-input digest audit
  (`Tools/UnityCli/PlayMode/MeshStalenessProbe.cs`) found **0 stale meshes** over a 30-step flight, 8 edit scenarios,
  an unload round trip and an inactive-neighbor scenario with diagonals on; with diagonals off it found only diagonal
  corner-light mismatches (5 of 441 visible chunks after the flight), as designed.

## What this measures (and what it does NOT)

The production merge → mesh request → `ScheduleMeshing` path in a Master player, read through the merge counters
(`MergeNeighborRemeshes`, `MergeGateSkippable`, `MergeGateDiagonals`), jobs completed and the frame slots. It does
not measure staleness — Master has no audit; the Editor soak above owns that. `MergeNeighborRemeshes` counts
diagonals with the gate on, so OFF's and DIAG's neighbor counts are not like for like; mesh jobs completed are.

## Result — Generation pass, vd 10, 200 m/s (leg means; ms per frame unless noted)

| Metric                     | OFF        | DIAG       | NODG       | DIAG vs OFF | NODG vs OFF |
|----------------------------|------------|------------|------------|-------------|-------------|
| Frames (30 s)              | 4 822      | 5 624      | 4 791      | +16.6 %     | −0.7 %      |
| Wall p50                   | 4.28       | 3.44       | 4.02       | −19.6 %     | −6.1 %      |
| Wall p99                   | 33.26      | 29.61      | 35.03      | −11.0 %     | +5.3 %      |
| Hitch frames (report)      | 39.3       | 32.3       | 45.3       | −17.8 %     | +15.3 %     |
| `LightMerge`               | 1.578      | 1.257      | 1.661      | −20.3 %     | +5.3 %      |
| `MeshSchedule`             | 0.506      | 0.329      | 0.428      | −35.0 %     | −15.3 %     |
| Light jobs completed       | 30 146     | 29 866     | 29 818     | −0.9 %      | −1.1 %      |
| **Mesh jobs completed**    | **14 399** | **11 196** | **11 474** | **−22.2 %** | **−20.3 %** |
| Stable merges              | 19 276     | 19 031     | 19 015     |             |             |
| Neighbor re-meshes queued  | 6 542      | 1 250      | 1 420      |             |             |
| of which diagonals         | —          | 556        | 0          |             |             |
| Mesh jobs per stable merge | 0.747      | 0.588      | 0.603      |             |             |

Per run: OFF 14 812 / 14 065 / 14 319 mesh jobs, DIAG 11 419 / 11 401 / 10 769, NODG 11 891 / 11 639 / 10 891.

## Result — Generation pass, vd 32, 50 m/s (leg means)

| Metric                      | OFF        | DIAG      | NODG      | DIAG vs OFF | NODG vs OFF |
|-----------------------------|------------|-----------|-----------|-------------|-------------|
| Frames (30 s)               | 1 490      | 1 872     | 1 831     | +25.6 %     | +22.9 %     |
| Wall p50                    | 18.44      | 11.50     | 12.28     | −37.6 %     | −33.4 %     |
| Wall p99                    | 49.70      | 45.68     | 47.22     | −8.1 %      | −5.0 %      |
| Hitch frames (report)       | 110.0      | 171.5     | 178.0     | +55.9 %     | +61.8 %     |
| Frames > 40 ms (fixed)      | 104.5      | 62.5      | 76.0      | −40.2 %     | −27.3 %     |
| Frames > 50 ms (fixed)      | 14.5       | 6.5       | 9.5       | −55.2 %     | −34.5 %     |
| `LightMerge`                | 3.904      | 2.865     | 2.893     | −26.6 %     | −25.9 %     |
| `MeshSchedule`              | 1.633      | 0.819     | 0.865     | −49.8 %     | −47.1 %     |
| Light jobs completed        | 24 238     | 24 589    | 24 447    | +1.5 %      | +0.9 %      |
| **Mesh jobs completed**     | **15 093** | **9 692** | **9 698** | **−35.8 %** | **−35.7 %** |
| Neighbor re-meshes queued   | 6 670      | 234       | 216       |             |             |
| of which diagonals          | —          | 54        | 0         |             |             |
| Mean mesh job latency (ms)  | 31.0       | 32.8      | 33.5      | +5.7 %      | +7.9 %      |
| Mean light job latency (ms) | 27.8       | 24.2      | 24.4      | −12.9 %     | −12.5 %     |

## Result — Loading pass, 50 m/s (leg means)

| Metric                    | OFF          | DIAG            | NODG            |
|---------------------------|--------------|-----------------|-----------------|
| vd 10 mesh jobs completed | 4 691        | 4 288 (−8.6 %)  | 3 900 (−16.8 %) |
| vd 10 wall p50 / p99      | 0.74 / 3.95  | 0.82 / 4.30     | 0.81 / 4.18     |
| vd 32 mesh jobs completed | 12 129       | 11 832 (−2.4 %) | 11 528 (−5.0 %) |
| vd 32 wall p50 / p99      | 7.04 / 22.57 | 6.97 / 22.67    | 6.89 / 22.44    |

## Analysis

- **The saving is where the price said it would be.** Generation is where stable merges churn: OFF queued 6 542
  (vd 10) and 6 670 (vd 32) neighbor re-meshes per phase; the gate keeps 1 250 and 234. Mesh jobs fall by roughly
  the difference (−3 203 and −5 401), and the light work is unchanged, so this is removed work, not lost throughput.
- **Frame level at vd 32 is a clear win; at vd 10 it is inside run-to-run noise.** The vd-10 runs drift with their
  position in the session (the first runs of every leg are the slowest: OFF 5.54 / 3.73 / 3.56 ms p50, NODG 4.27 / 4.60 /
  3.18 ms), so DIAG's −20 % p50 and NODG's −6 % are not separable from order; the job counts are tight in every leg.
  At 200 m/s the frame is no longer mesh-bound after ES-7a (`MeshSchedule` 0.5 ms of a 4 ms p50).
- **The report's hitch count rises at vd 32 because its threshold moved.** A hitch is a frame over 33 ms or 2.5× the
  median of the last 240 frames, whichever is higher; OFF's ≈ 18 ms median puts its bar near 46 ms, the gated legs'
  ≈ 12 ms median leaves them on the 33 ms floor. Counted against fixed thresholds from the session CSVs, the gated legs
  have fewer slow frames (> 40 ms: 104.5 → 62.5 / 76.0; > 50 ms: 14.5 → 6.5 / 9.5).
- **No-regress guard.** Mesh job latency rises 6–8 % at vd 32 (31.0 → 32.8 / 33.5 ms) while light latency falls
  13 %; delivery is not worse at the frame level (p99 down). The vd-10 loading pass runs at ≈ 0.8 ms p50 in every leg,
  where a 0.07 ms shift is below what this capture resolves.
- **Diagonals (D2).** DIAG against NODG: generation mesh jobs −2.4 % (vd 10) and −0.1 % (vd 32), i.e. noise; loading +9.9 % /
  +2.6 %; frame level within noise. Without them the Editor audit leaves about 1 corner in 90 visible chunks stale
  until the next voxel edit or reload; with them it finds none.

## Verdict details

**GO for the gate, provisional:** both flags stay default-ON behind the rollback census until the user's in-game seam
and corner check. `gateMergeIncludeDiagonals` stays default-ON (decided 2026-11-13); the measured cost of keeping it on
is nil in generation and ≈ 3–10 % of the (light) loading pass's mesh jobs. All 15 runs exited 0 with a report.
