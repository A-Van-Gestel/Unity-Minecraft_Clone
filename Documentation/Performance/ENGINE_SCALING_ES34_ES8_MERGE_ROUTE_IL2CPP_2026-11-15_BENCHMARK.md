# ES-34 / ES-8 — Merge Route Lookup, Disk-Load Recount on the Worker, and the Gameplay Merge Ceiling

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-11-15                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| **Commit**      | base `12ed8149` plus the uncommitted ES-34/ES-8 working tree; step 1 priced in one build (route split only), the A/B in a second build of the final tree, the legs switched by flag                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **Captured by** | `StartupProbeController` (`-mc-run startup-new`, `-mc-run startup-existing`) and `BenchmarkController` (`-mc-run benchmark`: Gen 200 + loading 50 m/s at vd 10, Gen 50 + loading 50 m/s at vd 32) — **IL2CPP player, Master configuration, non-development**, D3D11, Unity 6000.6.9f1, i9-9900K / RTX 4070 Ti, seed 0, pins `maxMeshRebuildsPerFrame=10 maxLightJobsPerFrame=32 maxInFlightMeshJobs=20 perfMonitorTier=Capture` (one `-mc-set` per field; each Player.log's overrides line checked). Startup figures from the "Startup Coroutine" section of each `StartupRun_*.log`; gameplay figures from `Tools/Python/summarize_perf_session.py` per run (every cross-check "every phase matches") compared with `Tools/Python/summarize_ab_legs.py` |
| **Verdict**     | **ES-34 GO** (`lightRouteCache`, default on): new-world handoff **2 121 → 1 659 ms (−462 ms, −21.8 %)**, drained 3 244 → 2 830 ms, sweep-1 route 560.8 → 93.1 ms; gameplay vd 10 `LightMerge` 2.10 → 1.39 ms/frame, wall p99 38.5 → 29.1 ms. **ES-8 recount GO** (`loadRecountOnWorker`, default on): vd-32 loading `DiskLoadApply` 0.276 → 0.098 ms/frame. **ES-8 ceiling GO at 8 ms** (`lightMergeBudgetMs`, default 8): vd 32 worst 69.9 → 52.1 ms, frames > 50 ms 7.5 → 1.0, ensure sweep unchanged; 6 ms cuts frames further but slows the ensure sweep. No run of 52 logged an exception or a native leak; every digest run read 0 stale snapshots and 0 route-cache mismatches                                                                    |

> Scores [`ES-34` and `ES-8`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md). Follows the
> [ES-3 report](ENGINE_SCALING_ES3_LOADING_MODE_IL2CPP_2026-11-14_BENCHMARK.md) (which priced the route) and the
> [ES-35/36 report](ENGINE_SCALING_ES35_ES36_STARTUP_TAIL_IL2CPP_2026-11-14_BENCHMARK.md).

## The change

- **Route split in the startup report** (`Diagnostics/PerfRouteTotals`, `WorldJobManager.RouteTotals`): two `Route`
  lines after `Clouds:` (sweep 1 and the total): mods emitted and their arms (drop / persist / defer / direct, with a
  reconciliation flag), drained applies, effective applies by source (direct / drained / pull-back), removal support
  scans and their time, the same-target-chunk share, and a 1-in-64 sampled split of per-mod time into target
  resolution and arm work (an estimate). Under `startupWorldDigest`: a per-job census (`Helpers/CrossChunkModCensus`,
  Performance Monitor B42) and a `[Startup] Merge order` hash line. B41 pins the lines.
- **ES-34 route lookup** (flag `lightRouteCache`, default on, every merge): `Helpers/CrossChunkRouteCache` resolves a
  run of mods into the same target chunk once (Lighting B138), and `_completedLightJobSet` mirrors the `List`
  `_completedLightJobs` (`JobCompletionPass.RunMergeLoop` fills both in the same step, Meshing B27.6) so the
  "already merged" test is a set lookup instead of a list scan per mod. Exact by
  construction (no arm changes what the route reads); a diagnostic shadow re-resolves every routed mod the reference way
  under the digest flag and prints `N route-cache mismatches` (must be 0).
- **ES-8 disk-load recount** (flag `loadRecountOnWorker`, default on): section block counts read a flat
  `bool[] IsOpaqueById` (`JobDataManagerFactory.BuildIsOpaqueById`) everywhere; `LoadChunkAsync` recounts the
  deserialized sections on its worker and `PopulateFromSave` skips the main-thread recount (Serialization Round-Trip
  B18).
- **ES-8 gameplay merge ceiling** (`lightMergeBudgetMs`, Performance tab, default 8, 0 = off): `ProcessLightingJobs`
  takes a window; on expiry the remaining completed jobs stay registered for a later frame, visited from a rotating
  start (`PipelinePassBudget.NextScanStart`, Pipeline Backpressure B35); the lighting simulator converges to the
  unbudgeted light with one merge per pass (Lighting B139). `MergeCeilingDeferred` counts the jobs left. The startup
  merge stays unbudgeted.

## What this measures (and what it does NOT)

Startup handoff and drained for a new and an existing world, and the frames of the benchmark's generation, ensure and
loading phases. It does not measure: time to stable (four new-world runs per build hit the 300 s timeout on Stable, as
in build 1; not scored); frame cost of the startup itself; light equality across runs (a new world's voxels differ run
to run, World Generation Bug 07, and Editor plays re-save their world, so equivalence is the in-process mismatch count);
other hardware.

## Step 0 — the split (build 1, `StartupRun_2026-11-15_15-12-08` … `15-48-39`)

New world, sweep 1, medians of 5: route 557.0 ms + drain 15.5 ms for ≈ 2.02 M emitted mods (deferred 0.961 M, direct
1.059 M); 0 support scans, 0 removals; same-target 86.6 %; resolution ≈ 84 % of per-mod time (est.); census (2 digest
runs): 0 redundant placements, ≈ 12.6 k repeats. Existing world: 0 routed mods (its light loads from disk). Lever gate
(plan §3.G): route cache passed (resolution ≥ 30 %, same-target ≥ 80 %); coalescing failed (0 redundant); Burst
support scans failed (0 scans); skip-routing met its condition but is not exact (0.8 % of drained mods change light) and
was not built.

The ES-3 report's "≈ 1.06 M routed / ≈ 112 k effective" pairs direct-routed mods with effective applies of every source,
so it is not an effective rate; the route figure also excluded the deferred half of the mods, which land in the drain.

## Result — ES-34, startup (build 2, `StartupRun_2026-11-15_16-57-52` … `17-20-42`; medians, ms)

| Leg                 | n   | Handoff             | Drained             | Sweep 1 route | Sweeps total |
|---------------------|-----|---------------------|---------------------|--------------:|-------------:|
| new, cache off (S0) | 5   | 2 121 (2 057–2 135) | 3 244 (3 231–3 334) | 560.8         | 1 081.4      |
| new, cache on (S1)  | 5   | 1 659 (1 631–1 847) | 2 830 (2 710–2 989) | 93.1          | 618.9        |
| existing, cache off | 3   | 334 (325–337)       | 980 (979–999)       | 0.0           | 129.6        |
| existing, cache on  | 3   | 332 (331–370)       | 1 001 (984–1 030)   | 0.0           | 133.5        |

Digest runs (2 per leg): 0 stale shared snapshots; cache-on legs 0 route-cache mismatches. Their route time is not a
timing figure: the shadow check re-resolves every mod.

## Result — gameplay (build 2, `BenchmarkRun_2026-11-15_17-21-51` … `17-49-12`; means over runs)

Generation pass, vd 10, 200 m/s (3 runs per leg):

| Leg                | Wall p99 | Worst | Hitch frames | > 50 ms | `LightMerge` ms/frame | Route ms/frame | Light jobs completed |
|--------------------|---------:|------:|-------------:|--------:|----------------------:|---------------:|---------------------:|
| G1 cache off       | 38.5     | 60.2  | 64.7         | 4.0     | 2.10                  | 0.773          | 29 999               |
| G0 default (cache) | 29.1     | 45.2  | 19.0         | 0.3     | 1.39                  | 0.301          | 29 816               |
| G2 ceiling 6 ms    | 24.5     | 42.5  | 5.3          | 0.3     | 1.45                  | 0.316          | 30 062               |
| G3 ceiling 8 ms    | 27.4     | 50.3  | 8.3          | 0.7     | 1.46                  | 0.317          | 29 977               |

Generation pass and ensure sweep, vd 32, 50 m/s (2 runs per leg):

| Leg             | Wall p99 | Worst | Hitch frames | > 50 ms | `LightMerge` | Ensure sweep s | Ceiling-deferred jobs (gen) |
|-----------------|---------:|------:|-------------:|--------:|-------------:|---------------:|-----------------------------|
| G0 default      | 45.4     | 69.9  | 141          | 7.5     | 2.83         | 14.3 / 15.5    | 0 / 0                       |
| G2 ceiling 6 ms | 39.3     | 54.0  | 50           | 2.0     | 2.54         | 17.6 / 15.7    | 11 693 / 11 746             |
| G3 ceiling 8 ms | 39.2     | 52.1  | 77           | 1.0     | 2.71         | 15.0 / 15.0    | 5 686 / 5 381               |

Loading pass, vd 32 (2 runs per leg): `DiskLoadApply` 0.276 ms/frame, p99 4.54–4.55 ms with the recount on the main
thread (G4) against 0.098 ms/frame, p99 2.25–2.28 ms on the worker (G0), 320 load frames in every run.

## Analysis

- **ES-34, startup:** the route falls 560.8 → 93.1 ms and the handoff 462 ms, all of it in sweep 1; the deferred half
  of the mods still costs its drain (≈ 15 ms) and the apply arm the rest. A loaded world routes nothing at startup, so it
  neither gains nor regresses.
- **ES-34, gameplay:** the same lookup is most of the gameplay route too: `LightMerge` −34 % at vd 10 and the generation
  pass's p99 −24 %, hitch frames 65 → 19. This is the larger day-to-day win; the startup gain is the one ES-34 was filed
  for.
- **ES-8 ceiling:** both values clip merge bursts. 6 ms clips more frames but defers twice the jobs and lengthens the
  vd-32 ensure sweep (14.9 → 16.7 s mean, n = 2) with higher light-job latency there; 8 ms keeps the ensure sweep at
  15.0 s and still takes vd-32 frames over 50 ms from 7.5 to 1.0. By the plan's rule (the smallest value that cuts the
  worst merge frames with no throughput regression) the default is **8 ms**. At vd 10 the 8 ms worst frame (50.3 against
  45.2) is within the run spread (41.3–56.5).
- **ES-8 recount:** the per-hit recount was most of `DiskLoadApply` (−64 % mean, p99 halved, equal load frames), as the
  ES-8 plan inferred; loading wall p99 moves within noise (19.9 → 21.2 ms with the recount back on main).

## Verdict details

**ES-34 GO, `lightRouteCache` default on. ES-8 GO: `loadRecountOnWorker` default on, `lightMergeBudgetMs` default 8 ms.**
Validate All green on the final tree (874 baselines, 32 suites). All 52 Master runs of the
two builds exited with a report; none logged an exception, a native leak or a reconciliation `MISMATCH`.
