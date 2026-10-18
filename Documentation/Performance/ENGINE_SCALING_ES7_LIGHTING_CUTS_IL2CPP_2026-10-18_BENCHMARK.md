# ES-7a — Lighting Cuts: Slab Fill, No Merge Recount, Band-Clamped Merge, Before and After

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
|-----------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-10-18                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| **Commit**      | after (B): `f5006286` (ES-7.1 `9d260d95` + ES-7.2 `aa4cf5af` + ES-7.3 `56b900b6`); before (A): `5d3205ae`, the ES-7 Step 0 merge split counters with none of the cuts. Both fresh Master builds from clean trees, stamps without `-dirty`                                                                                                                                                                                                                                                                                                                                               |
| **Captured by** | `BenchmarkController` (`-mc-run benchmark`) — **IL2CPP player, Master configuration, non-development**, Managed Code Variant `Release`, Burst AOT, D3D11, Unity 6000.6.6f1, i9-9900K, idle machine. Legs alternated in one session: route Gen = `200`, loading `50` m/s, vd 10 × 3 per leg; Gen = `50`, loading `50` m/s, vd 32 × 2 per leg. `-mc-set perfMonitorTier=Capture`, budgets pinned (`maxMeshRebuildsPerFrame=10`, `maxLightJobsPerFrame=32`). Session files read per phase with `Tools/Python/summarize_perf_session.py` (every cross-check: "every phase matches")         |
| **Verdict**     | **GO for ES-7a.** The lighting merge costs **0.448 → 0.233 ms per merged job at 200 m/s (−48 %)** and 0.419 → 0.234 ms at vd 32 (−44 %); its apply part 0.352 → 0.143 ms (−59 %). At the frame level the 200 m/s generation pass runs **3.1× the frames** (1 031 → 3 213 over the same 30 s), wall p50 **27.0 → 6.6 ms**, p99 **68.5 → 39.2 ms**, hitch frames 112 → 68; vd 32 at 50 m/s: p50 34.8 → 20.8 ms, p99 61.8 → 50.6 ms, hitch frames 82.5 → 67. ES-7.1's scheduling cut is **not resolved** in this capture (per-job schedule cost reads +15–19 %, confounded — see Analysis) |

> Scores [`ES-7`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md) items 7.1–7.3 ("ES-7a"); 7.4 (remesh gating) was split off
> and is priced below. The A leg is the Step 0 commit rather than the pre-ES-7 `cd092756` so both legs carry the merge
> split counters this report reads. The before capture of the same commit is in the roadmap's ES-7 note; the baseline the
> roadmap scored 200 m/s against is
> [`ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10`](ENGINE_SCALING_ES0_REMEASURE_IL2CPP_2026-10-10_BENCHMARK.md).

## The change

- **ES-7.1 — slab fill** (`ChunkData.FillJobLightMap`): a compact uniform-sky section reaches a job's light map as one
  `NativeArray.Copy` from a 16-level static slab table instead of 4 096 per-element writes. Every lighting job fills 9
  light maps and every mesh job does too. Guard: lighting `B124` (all 16 levels, poisoned buffer).
- **ES-7.2 — no merge recount** (`ChunkData.ApplyJobLightMap`): the merge no longer recounts the block counts of every
  section it keeps; `SetVoxel` maintains them on every write. Instrumented builds (the Editor, `Checked`/`Instrumented`
  variants) recount each kept section and compare (`VerifySectionCounts`, counters `MergeCountChecks`/`MergeCountDrifts`).
  No suite reaches the production counts path, so the evidence is the verifier: 0 drifts in 12 781 + 3 216 + 6 174
  Editor checks over a 200 m/s-style flight and a place/break/water/lava/lamp soak. Guard: `B40` (planted drift caught and
  repaired). Master compiles the verifier out (`Count checks` 0 in every B run).
- **ES-7.3 — band-clamped merge**: sections wholly outside the job's LI-2 band are skipped, keeping section, light and
  compact byte; an absent section whose job light is uniform sky compacts straight to its byte instead of being rented,
  filled, classified and returned. Guards: lighting `B125` (120-trial fuzz, clamped ≡ full merge), `B126` (direct
  compaction ≡ the rent path's outcome), `B127` (a compact section promoted mid-flight outside the band stays promoted —
  same light, accepted representation difference). The lighting harness now merges with each job's band.
- Validate All 844/844 at `56b900b6`; every new baseline proved red (`prove_red.py`).

## What this measures (and what it does NOT)

The production `World.Update` → `WorldJobManager.ProcessLightingJobs` → `MergeCompletedLightingJob` path in a Master
player, read through the `LightMerge`, `LightSchedule` and `MeshSchedule` slots and the Step 0 merge counters. It does
not measure the fill alone: no counter times `FillJobLightMap` inside the schedule slots, so ES-7.1 is visible only as a
change of whole-slot cost per job. Ensure Generated is not compared: B's faster generation pass left the pre-generator
almost nothing to do (1 frame in every B run against 17–47 in A).

## Result — Generation pass, vd 10, 200 m/s (per run; ms per frame unless noted)

| Metric                     | a1             | a2            | a3            | b1        | b2        | b3        | A mean | B mean | B vs A  |
|----------------------------|----------------|---------------|---------------|-----------|-----------|-----------|--------|--------|---------|
| Frames (30 s)              | 720            | 1 180         | 1 194         | 3 023     | 3 192     | 3 424     | 1 031  | 3 213  | +211 %  |
| Wall p50                   | 43.92          | 20.62         | 16.49         | 6.75      | 6.95      | 6.15      | 27.01  | 6.62   | −75.5 % |
| Wall p99                   | 69.78          | 67.22         | 68.63         | 41.45     | 39.13     | 37.11     | 68.54  | 39.23  | −42.8 % |
| Worst                      | 82.70          | 78.78         | 82.89         | 59.97     | 54.29     | 64.91     | 81.46  | 59.72  | −26.7 % |
| Hitch frames               | 116            | 104           | 116           | 88        | 49        | 68        | 112.0  | 68.3   | −39.0 % |
| `LightMerge`               | 20.425         | 12.146        | 11.865        | 2.645     | 2.486     | 2.231     | 14.812 | 2.454  | −83.4 % |
| `LightSchedule`            | 6.506          | 3.898         | 3.834         | 1.940     | 1.778     | 1.665     | 4.746  | 1.794  | −62.2 % |
| `MeshSchedule`             | 2.858          | 1.665         | 1.654         | 0.892     | 0.822     | 0.771     | 2.059  | 0.828  | −59.8 % |
| Merge ms per merge         | 0.451          | 0.448         | 0.444         | 0.236     | 0.235     | 0.228     | 0.448  | 0.233  | −48.0 % |
| Apply ms per merge         | 0.354          | 0.352         | 0.350         | 0.146     | 0.141     | 0.143     | 0.352  | 0.143  | −59.3 % |
| Schedule ms per light job  | 0.144          | 0.144         | 0.143         | 0.173     | 0.168     | 0.171     | 0.144  | 0.170  | +18.6 % |
| Schedule ms per mesh job   | 0.164          | 0.157         | 0.157         | 0.175     | 0.169     | 0.171     | 0.160  | 0.172  | +7.7 %  |
| Apply (of which counts)    | 16.017 (6.662) | 9.545 (3.964) | 9.360 (3.883) | 1.641 (0) | 1.493 (0) | 1.394 (0) | 11.641 | 1.509  | −87.0 % |
| Route                      | 3.982          | 2.341         | 2.256         | 0.896     | 0.895     | 0.746     | 2.860  | 0.846  | −70.4 % |
| Merges per frame           | 45.3           | 27.1          | 26.7          | 11.2      | 10.6      | 9.8       | 33.0   | 10.5   | −68.1 % |
| Out-of-band sections/merge | 1.26           | 1.17          | 1.24          | 0.94      | 0.93      | 0.91      | 1.22   | 0.93   |         |
| Churned sections/merge     | 2.22           | 2.18          | 2.16          | 0.00      | 0.00      | 0.00      | 2.19   | 0.00   | −100 %  |
| Light jobs completed       | 32 590         | 31 976        | 31 930        | 33 987    | 33 740    | 33 427    | 32 165 | 33 718 | +4.8 %  |
| Mesh jobs completed        | 12 513         | 12 494        | 12 546        | 15 405    | 15 485    | 15 397    | 12 518 | 15 429 | +23.3 % |
| Generation jobs completed  | 9 877          | 9 796         | 9 768         | 9 892     | 9 918     | 9 892     | 9 814  | 9 901  | +0.9 %  |

Hitch records (the files the detector writes, one per window) are led by `LightMerge` in both legs: A 6/6/6 records
(LightMerge 6/6/5), B 12/5/10 (LightMerge 11/4/7, LightSchedule 1/1/3). B writes more records because the threshold
follows the median frame, which fell; the frames over threshold (above) fell.

## Result — Generation pass, vd 32, 50 m/s

| Metric                      | va1             | va2             | vb1             | vb2             | A mean | B mean  | B vs A  |
|-----------------------------|-----------------|-----------------|-----------------|-----------------|--------|---------|---------|
| Frames                      | 888             | 899             | 1 391           | 1 364           | 893.5  | 1 377.5 | +54.2 % |
| Wall p50                    | 35.05           | 34.48           | 20.61           | 21.07           | 34.77  | 20.84   | −40.1 % |
| Wall p99                    | 63.06           | 60.44           | 50.11           | 51.04           | 61.75  | 50.58   | −18.1 % |
| Worst                       | 78.33           | 68.77           | 56.41           | 58.95           | 73.55  | 57.68   | −21.6 % |
| Hitch frames                | 82              | 83              | 73              | 61              | 82.5   | 67.0    | −18.8 % |
| `LightMerge`                | 12.391          | 12.047          | 4.362           | 4.451           | 12.219 | 4.406   | −63.9 % |
| `LightSchedule`             | 4.388           | 4.321           | 3.178           | 3.260           | 4.354  | 3.219   | −26.1 % |
| `MeshSchedule`              | 2.234           | 2.177           | 1.784           | 1.805           | 2.205  | 1.794   | −18.6 % |
| Merge ms per merge          | 0.423           | 0.415           | 0.235           | 0.234           | 0.419  | 0.234   | −44.1 % |
| Apply ms per merge          | 0.337           | 0.331           | 0.140           | 0.140           | 0.334  | 0.140   | −58.1 % |
| Schedule ms per light job   | 0.150           | 0.149           | 0.171           | 0.172           | 0.149  | 0.171   | +14.6 % |
| Schedule ms per mesh job    | 0.143           | 0.142           | 0.163           | 0.162           | 0.143  | 0.163   | +14.2 % |
| Route                       | 2.244           | 2.172           | 1.576           | 1.599           | 2.208  | 1.587   | −28.1 % |
| Light / mesh jobs completed | 26 043 / 13 835 | 26 062 / 13 814 | 25 903 / 15 182 | 25 901 / 15 195 |        |         |         |

## Result — Loading pass, 50 m/s

| Leg set            | Wall p50 A → B | Wall p99 A → B | Hitch frames A → B | `LightMerge` A → B |
|--------------------|----------------|----------------|--------------------|--------------------|
| vd 10 (3 + 3 runs) | 1.07 → 1.06    | 7.94 → 7.19    | 0.33 → 0.33        | 0.081 → 0.020      |
| vd 32 (2 + 2 runs) | 7.24 → 7.16    | 31.97 → 21.22  | 17 → 11            | 0.706 → 0.205      |

## Analysis

- **Where the merge win comes from.** Per merged job, apply fell 0.352 → 0.143 ms. The removed recount was 0.147 ms of
  A's 0.352 (6.662 ms of counts over 45.3 merges per frame in a1, likewise a2/a3); the band clamp and the churn cut took
  the rest of the apply down from ≈ 0.205 to 0.143 ms. Route per merge is unchanged (≈ 0.08–0.09 ms) — the other merge
  parts were not in scope.
- **The frame-level effect is larger than the per-job one** because 200 m/s was merge-bound: cheaper merges let each frame
  finish sooner, the same work spreads over 3× the frames, and the queues drain faster (mean light job latency 40–44 → 18–20 ms,
  23 % more mesh jobs finished inside the phase). The A runs vary (a1 p50 43.9 ms against 16–21 ms for a2/a3, the first
  run of the session); B's are tight (6.2–7.0 ms).
- **ES-7.1 is not resolved here.** Schedule cost per job rose 0.144 → 0.170 ms (light) and 0.160 → 0.172 ms (mesh) at
  200 m/s, +14–15 % for both at vd 32. Both slots carry per-frame work besides the per-job fills, and B runs a third as
  many jobs per frame, so any fixed per-frame cost lands on fewer jobs. Summed over the phase, `LightSchedule` is
  4.89 s (A) against 5.76 s (B) for 32 165 against 33 718 jobs; a fixed cost of ≈ 0.3 ms per frame would account for the
  whole difference (an estimate fitted to these two points, not a measurement). The fill itself was measured only in
  the Editor (36.5 → 1.6 µs per chunk, Mono). A per-job fill timer, or an IL2CPP micro-benchmark of the fill, would
  settle it; neither ran.
- **ES-7.4 price (B, 200 m/s, per run):** 19 412–19 895 of 22 380–22 866 stable merges changed no light (87 %); they
  newly queued 6 863–7 185 of 7 801–8 139 neighbor mesh rebuilds (88 %), against 15 397–15 485 mesh jobs completed in
  the phase. Own-chunk requests are nearly always already queued (77–90 new). Whether those neighbor rebuilds are
  redundant is the open ES-7.4 question (light changed by other writers, diagonal corner light) — this prices the
  ceiling, not the saving.
- **Unloads with skylight pending** (the ES-27 observation): 727–736 chunks holding ≈ 1.07 M nodes per 200 m/s B run;
  248 chunks / 0.32 M nodes per vd-32 run in both legs.

## Verdict details

**GO for ES-7a**, shipped without a rollback flag (pure cost cuts with bit-identical output, guarded by B124–B127 and
the verifier). ES-7.1 stays — its output is proven identical and the Editor measured the fill cut — with its Master
effect recorded as unresolved. ES-7.4 is the next lighting item, to be planned separately against the price above.
Lighting Bug 23 (the two-job livelock) appeared once in the Step 0 before capture at vd 10 and in none of these ten runs.
