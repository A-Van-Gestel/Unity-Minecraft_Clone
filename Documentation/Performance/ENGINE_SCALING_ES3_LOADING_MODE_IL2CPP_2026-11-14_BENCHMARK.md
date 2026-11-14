# ES-3 — Loading Mode: Structure-Quota Lift and Shared Sweep Snapshots in the Startup Coroutine

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                            |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-11-14                                                                                                                                                                                                                                                                                                                                                                                                       |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                           |
| **Commit**      | base `2556cfdc` plus the uncommitted ES-3 working tree; the step-0 price from one build, the A/B from a second build of the same tree, the legs switched by flag                                                                                                                                                                                                                                                 |
| **Captured by** | `StartupProbeController` (`-mc-run startup-new`, `-mc-run startup-existing`) — **IL2CPP player, Master configuration, non-development**, D3D11, Unity 6000.6.8f1, i9-9900K / RTX 4070 Ti, vd 10 (729 chunks, seed 0), `-mc-set perfMonitorTier=Capture`. Each run writes a `StartupRun_*.log` in the player's Benchmarks folder; the "Startup Coroutine" section of it is the source of every phase figure below |
| **Verdict**     | **GO.** New-world handoff **3 458 → 2 150 ms (−1.31 s, −37.8 %)**, drained **4 555 → 3 303 ms (−1.25 s)**; existing-world handoff 547 → 495 ms (−52 ms). Phase 1 falls 1 445 → 807 ms (the quota lift), the lighting sweeps 1 635 → 1 015 ms (the shared snapshots; acquiring and filling per-job maps 684 → 111 ms). No run of 28 logged an exception or a native leak. Time to stable is not scored (noisy)    |

> Scores [`ES-3`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) (sub-parts: phase-1 structure-mods quota lift, shared
> per-sweep snapshots). The startup phase report that priced it is itself part of the change.

## The change

- **Startup phase report** (`Diagnostics/StartupPhaseTotals`, owned by `World`): per launch, phase 1 (generation)
  iterations, quota stops, main-thread pass and apply-mods time, jobs, structure mods and collections; per lighting sweep
  recalc / schedule (acquire share) / wait / merge (apply, drain, route, cascade at the Systems tier and above) / jobs /
  shared maps / cross-chunk mods / collections; and the `StartWorld` tail (report, spawn, clouds, border wall). It prints
  as a `=== Startup Coroutine ===` section of `StartupRun_*.log` and as a `[Startup]` `Player.log` line at the handoff,
  at every build and tier. Performance Monitor baseline B41 guards it.
- **Phase-1 structure-mods quota lift:** `ProcessGenerationJobs(window, liftModsQuota)`; the startup coroutine passes
  `Settings.startupLiftStructureQuota` (Dev tab, default on). Gameplay keeps `maxStructureModsPerFrame`. Lifting the
  quota can change which of two overlapping structure mods wins, so the terrain is seed-visible different from a
  quota-bound startup; that was accepted.
- **Shared per-sweep snapshots** (`Helpers/StartupSweepSnapshots`): during each startup lighting sweep's schedule loop,
  every `TempJob` lighting job reads its 8 neighbor maps and its own center voxels from one shared voxel and light map
  per chunk, filled on first request. The center light map stays per job (the merge extracts into it). The set is
  ref-counted, closed after the schedule loop (before the merge) and freed with the last job's release;
  `WorldJobManager.Dispose` frees one left open. `LightingJobData.SharedSnapshotSet` marks the jobs and `Dispose`
  skips the shared maps. Rollback flag `Settings.startupSharedSweepSnapshots` (Dev tab, default on). Chunk Pipeline
  baseline B8 guards it (prove-red 4 of 4).
- **Opt-in `startupWorldDigest`:** logs FNV-1a voxel and light digests of the waited-for chunks at the handoff and counts
  shared snapshots that went stale before the merge (must be 0; an Editor run read 0, a planted write read 1).

## What this measures (and what it does NOT)

The Master player's startup coroutine from scene load to handoff, plus the drained time, for a new world
(`startup-new`) and an existing one (`startup-existing`). It does not measure: time to stable (settling is the fluid
tick's, and noisy); anything after the handoff except the drained stamp; frames the player sees (no frame before the
handoff is visible today; the loading screen belongs to ES-4); other view distances or hardware; or terrain
equivalence across runs — see "World digest" below.

## Step 0 — what the startup cost, before any change was judged

Build of `2556cfdc`-dirty, 2026-11-14 13:38–13:40 (`StartupRun_2026-11-14_13-38-31` … `13-40-39`), phase report only.

| Leg                                                    | n   | Handoff ms | Drained ms |
|--------------------------------------------------------|-----|-----------:|-----------:|
| A: defaults (both flags on)                            | 5   | 3 481      | 4 635      |
| B: A plus `-mc-set maxStructureModsPerFrame=100000000` | 5   | 2 868      | 4 038      |
| Existing world, defaults                               | 3   | 573        | 1 540      |

What the A runs' report showed (medians):

| Part                   | Value                                                                                                |
|------------------------|------------------------------------------------------------------------------------------------------|
| Phase 1 (generation)   | 1 397 ms over 86 iterations; 83–84 of them stopped by the 5 000-per-frame structure-mods quota       |
| Lighting sweeps, total | ≈ 1 701 ms, of which acquiring and filling per-job maps 709 ms                                       |
| Sweep 1                | ≈ 987 ms, of which the merge's cross-chunk route ≈ 517 ms (≈ 1.06 M mods routed, ≈ 112 k effective)  |
| `StartWorld` tail      | `clouds.Initialize` ≈ 190–205 ms with 14–15 gen-0 collections; chunk borders and the border wall ≈ 0 |

## Result — A/B, new world and existing world (medians; ms)

Second build, same base, 2026-11-14 13:59–14:06 (`StartupRun_2026-11-14_13-59-38` … `14-06-40`), the legs alternated
within the session. L0 = both flags off, L1 = quota lift only, L2 = both on (the default).

| Leg                  | n   | Handoff             | Drained | Phase 1 | Sweeps total | Acquire |
|----------------------|-----|---------------------|--------:|--------:|-------------:|--------:|
| L0 both flags off    | 5   | 3 458 (3 330–3 682) | 4 555   | 1 445   | 1 635        | 684     |
| L1 lift only         | 5   | 2 861 (2 788–3 061) | 3 964   | 806     | 1 644        | 690     |
| L2 both on (default) | 5   | 2 150 (2 123–2 154) | 3 303   | 807     | 1 015        | 111     |
| existing L0          | 3   | 547                 | 1 506   | —       | 182          | 115     |
| existing L2          | 3   | 495                 | 1 472   | —       | 122          | 55      |

The L2 range excludes run 9, an outlier: handoff 6 653 ms (merge route 3 279 ms, phase 1 in 3 iterations at 1.7 s).
It is reported, and the median is unaffected (2 149 ms without it).

## Analysis

- **The lift** removes ≈ 640 ms of phase 1 (1 445 → 806) and nothing from the sweeps (1 635 → 1 644): phase 1 stops
  needing ≈ 83 quota-bound frames. The saving is wall time of fewer iterations; the structure-mod work itself is the same.
- **The shared snapshots** remove ≈ 630 ms from the sweeps (1 644 → 1 015), nearly all of it acquire (690 → 111): each
  chunk is copied once per sweep instead of once per reader.
- **Existing world:** handoff −52 ms (547 → 495); phase 1 does not run, the sweeps are 182 → 122 ms.
- **Pre-handoff frames** are present-paced at ≈ 15.3 ms (VSync count 1; CPU ≈ 5 ms), the same floor as idle
  post-handoff frames, so fewer phase-1 frames is what removes that cost (inferred).
- **Post-handoff mesh tail** (≈ 0.4 s) drains at `maxMeshRebuildsPerFrame` (calibrated 10 per frame), not at the in-flight
  mesh cap; scaling that cap would not help, so that planned sub-item was not built.

## World digest — and why equivalence is proven in-process

Two L0 runs (behavior unchanged from the base) produced different world digests (`8C7E7F58E6CBABCD` against
`1D57C9DD1C8FE8AC`), and the cross-chunk routed counts also vary per run. A fresh new-world startup is therefore not
bit-reproducible run to run even before this change, so digests cannot compare legs. The shared snapshots' equivalence
rests on the in-process stale-snapshot count (0) and on B8, not on cross-run digests.

## Open findings (follow-ups; none filed as IDs)

- Sweep 1's merge spends ≈ 517 ms routing ≈ 1.06 M cross-chunk light mods: now the largest single startup cost.
- `clouds.Initialize()` costs ≈ 190 ms and 14–15 gen-0 collections in the frozen tail (≈ 145 ms on an existing world).
- Not done from ES-3's roadmap text: banding the startup sweeps, the nearest-first admission cap (`SU-2`), the wait radius
  `LoadDistance + 1`, post-handoff quota ×4–8, the pipelined coroutine, and a loading overlay (ES-4 owns any loading
  screen; nothing in ES-3 assumes pre-handoff frames are visible).

## Verdict details

**GO, both sub-parts default-on behind their rollback flags.** New-world handoff −1.31 s (−37.8 %) and drained −1.25 s
at vd 10; existing world −52 ms; all 28 runs exited with a report, none logged an exception or a native leak. Stable was
not scored.
