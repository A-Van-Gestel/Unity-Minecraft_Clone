# ES-35 / ES-36 — Startup Tail: Cloud Prebuild and the Post-Handoff Mesh Wave

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-11-14                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Branch**      | `main`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| **Commit**      | base `d80f7ce2` plus the uncommitted ES-35/ES-36 working tree; step 0 from one build (clouds split instrumentation only), the A/B from a second build of the final tree, the legs switched by flag                                                                                                                                                                                                                                                                                         |
| **Captured by** | `StartupProbeController` (`-mc-run startup-new`, `-mc-run startup-existing`) — **IL2CPP player, Master configuration, non-development**, D3D11, Unity 6000.6.8f1, i9-9900K / RTX 4070 Ti, vd 10 (seed 0), pins `maxMeshRebuildsPerFrame=10 maxLightJobsPerFrame=32 maxInFlightMeshJobs=20`. Phase figures from the "Startup Coroutine" section of each `StartupRun_*.log`; frame figures from `-mc-set perfMonitorTier=Capture` sessions                                                   |
| **Verdict**     | **ES-35 GO** (default on): the clouds part of the frozen tail frame **146 → 12 ms** and its gen-0 collections **14–21 → 0**; new-world handoff 2 063 → 1 998 ms (−65 ms) and existing-world 421 → 380 ms (−41 ms) in the one-build A/B. **ES-36 NO-GO as a default** (kept opt-in, default off): no Drained gain on a new world (3 097 → 3 086 ms) and heavier post-handoff frames (p95 38.7 → 49.7 ms); existing-world Drained −177 ms. No run of 40 logged an exception or a native leak |

> Scores [`ES-35` and `ES-36`](../Design/ENGINE_SCALING_PERFORMANCE_ROADMAP.md), and answers roadmap §8 question 8
> ([World Generation Bug 07](../Bugs/WORLD_GENERATION_BUGS.md)). Follows the
> [ES-3 report](ENGINE_SCALING_ES3_LOADING_MODE_IL2CPP_2026-11-14_BENCHMARK.md).

## The change

- **Clouds split in the startup report:** a `Clouds:` line after the `Tail:` line of `=== Startup Coroutine ===`: pattern
  (noise job, or the wait for a prebuild), threshold + fill, meshes (built or created from a prebuild), tiles, other,
  each with its gen-0 collections. Performance Monitor B41 guards the line.
- **Shared Burst-compatible mesher** (`Jobs/Helpers/CloudTileMesher`): the coverage threshold, the pattern fill and the
  Fast / Fancy tile meshers, over native containers (the Fancy corner normals in a dense array instead of a
  `Dictionary`). The main thread and the jobs run the same code; its output was bit-identical to the managed code it
  replaced (2 seeds × 2 layers × both styles × 64 tiles). The layer pattern is a persistent `NativeArray<bool>`.
- **ES-35 cloud prebuild:** once the seed is final, before the initial chunks load, `Clouds.BeginPrebuild` schedules per
  procedural layer the noise job, `CloudThresholdJob` and one `CloudTileMeshJob` per tile (`Helpers/CloudLayerPrebuild`
  owns the buffers). `Clouds.Initialize` in the tail adopts the pattern and creates the 64 meshes per layer from the
  finished buffers. Stale inputs (seed, style, noise settings, depth offset) discard the prebuild for the main-thread
  path. Rollback flag `startupPrebuildClouds` (Dev tab, default on). Cloud Render B7/B8 compare the job output with the
  main-thread build in process (prove-red 4 of 4), and B9 pins the main-thread build to the golden digests recorded
  from the managed mesher before the port (prove-red 4 of 4).
- **ES-36 mesh wave:** from the handoff until the first drain, the mesh pass's cap is
  `PipelinePassBudget.MeshRebuildCap` = the in-flight cap (20) instead of `maxMeshRebuildsPerFrame` (10), still scaled by
  frame time and bounded per iteration by the in-flight cap (OM-1). `Helpers/StartupMeshWave` ends it on drain (its own
  `PipelineDrainPredicate` call), on leaving the handoff chunk, or after 10 s, and `World` logs
  `[Startup] Mesh wave ended: <reason> at +N ms`. Flag `startupMeshWave` (Dev tab), **default off** after this A/B.
  Pipeline Backpressure B34 guards it (prove-red 4 of 4, including never-clear).

## What this measures (and what it does NOT)

The Master player's startup from scene load to handoff and to the Drained mark, for a new world and an existing one, plus
the frames between handoff and drain in one Capture run per leg (new world only). It does not measure: time to stable;
the existing world's post-handoff frames (no Capture run there); the cloud look (Cloud Render B7/B8 prove equal geometry;
the look is the user's in-game check); other view distances or hardware.

## Step 0 — what the clouds and the mesh tail cost

Build of `d80f7ce2`-dirty with only the clouds split added (`StartupRun_2026-11-14_19-36-37` … `19-38-42`), defaults.

| Part (median)               | New world (n=5)          | Existing world (n=3)     |
|-----------------------------|--------------------------|--------------------------|
| Tail `clouds`               | 146 ms                   | 149 ms                   |
| of which meshes (128 built) | 133 ms                   | 139 ms                   |
| pattern / threshold / tiles | ≈ 5 / 2 / 2–10 ms        | ≈ 5 / 2 / 2 ms           |
| tail gen-0 collections      | 14–15, all inside meshes | 19–21, all inside meshes |

There is no texture upload (the roadmap entry guessed one) and the pattern was already a Burst job: the cost was
managed tile meshing and the collections it triggered.

**Mesh tail (three Capture runs):** the post-handoff burst meshes ≈ 497 chunks over 1.06–1.13 s on a new world — nearly
the whole handoff → Drained span, not the ≈ 0.4 s the ES-3 report estimated — and 439 over 0.74 s on an existing world.
Per frame it stopped on the frame-time-scaled quota in 23–36 frames, on the in-flight cap in 9–32, and found nothing
ready in 12–22.

## Result — A/B, one build, legs alternated (medians; ms)

Build of the final tree, 2026-11-14 20:17–20:33 (`StartupRun_2026-11-14_20-17-14` … `20-33-16`). L0 = both flags off,
L1 = prebuild only, L2 = prebuild + mesh wave.

| Leg         | n   | Handoff             | Drained | Tail clouds | Phase 1 | Sweeps total | Wave ended                 |
|-------------|-----|---------------------|---------|-------------|---------|--------------|----------------------------|
| new L0      | 5   | 2 063 (2 034–2 172) | 3 169   | 74.8        | 832.8   | 984.6        | —                          |
| new L1      | 5   | 1 998 (1 964–2 003) | 3 097   | 12.2        | 807.7   | 977.3        | —                          |
| new L2      | 5   | 1 997 (1 964–1 998) | 3 086   | 12.3        | 824.6   | 983.7        | Drained at +1 098 (5 of 5) |
| existing L0 | 3   | 421 (421–440)       | 1 212   | 74.7        | —       | 135.1        | —                          |
| existing L1 | 3   | 380 (363–398)       | 1 199   | 13.0        | —       | 134.0        | —                          |
| existing L2 | 3   | 366 (357–369)       | 1 022   | 12.6        | —       | 132.5        | Drained at +655 (3 of 3)   |

Tail collections were 0 in every leg of this build: the shared native mesher (in L0 too) already removed them, and it
halved the main-thread clouds time (L0 75 ms against step 0's 146 ms; different builds, same machine).

**Post-handoff frames, new world (one Capture run per leg):**

| Leg | Burst                | Frames | Cap-bound frames | Wall median | p95  | Max  |
|-----|----------------------|--------|------------------|-------------|------|------|
| L1  | 1 134 ms, 497 meshes | 73     | 30               | 11.4        | 38.7 | 61.3 |
| L2  | 1 084 ms, 511 meshes | 56     | 43               | 14.4        | 49.7 | 63.6 |

## Analysis

- **ES-35:** the tail's clouds part falls from 146 ms (step 0) to ≈ 12 ms: the remaining time is creating 128 `Mesh`
  objects from finished buffers and placing 242 tiles. Phase 1 and the sweeps do not regress while the prebuild shares
  the workers (Phase 1 832.8 / 807.7, sweeps 984.6 / 977.3 L0 / L1). The handoff gain (−65 ms new, −41 ms existing) is
  the L0 → L1 clouds difference (−63 ms).
- **ES-36, new world:** doubling the quota packs more mesh work into each frame: fewer, heavier frames (73 → 56; p95
  +11 ms), the in-flight cap binding most frames, and the burst only 50 ms shorter, so Drained does not move. The
  post-handoff tail is limited by main-thread mesh work, not by the quota. It fails the plan's frame criterion (p95 and
  worst frame within 2 ms of the flag-off leg).
- **ES-36, existing world:** Drained −177 ms (1 199 → 1 022), consistent across 3 of 3 runs; its frames were not captured,
  so the frame criterion is unscored there.

## Verdict details

**ES-35 GO, default on behind `startupPrebuildClouds`.** **ES-36 kept as an opt-in (`startupMeshWave`, default off)**:
NO-GO as a default on the new-world evidence; the existing-world gain is unproven on frame cost. All 40 runs of the two
builds exited with a report; none logged an exception or a native leak.

## Addendum 2026-11-14 — ES-36 default on for loaded worlds

After this report, the user chose to keep the existing-world gain: `startupMeshWave` defaults on and the wave begins
only after a startup that generated no terrain (`StartupMeshWave.ShouldBegin`, Pipeline Backpressure B34). New worlds
keep the gameplay mesh rate (the L1 numbers above).

**Scored the same day** in a third Master build of that change (`StartupRun_2026-11-14_23-22-57` … `23-40-16`, pins as
above, legs alternated): W0 = `startupMeshWave=false`, W1 = default.

| Loaded world                              | W0 (wave off)          | W1 (wave on)           |
|-------------------------------------------|------------------------|------------------------|
| Drained, median (n=5)                     | 1 222 ms (1 207–1 406) | 1 026 ms (1 013–1 254) |
| Handoff → Drained, median (n=5)           | 868 ms                 | 668 ms                 |
| Mesh burst, median of 3 Capture runs      | 749 ms, 70–73 frames   | 640 ms, 46–48 frames   |
| Burst frames pooled: median / p95 / worst | 9.0 / 24.8 / 76.4 ms   | 11.3 / 25.7 / 74.0 ms  |

Drained −196 ms; p95 +0.9 ms and worst −2.4 ms, within the plan's 2 ms criterion (the median frame rises 2.3 ms). Both
`startup-new` runs at defaults logged no `Mesh wave ended` line: the wave does not start on a new world. **GO for loaded
worlds.**
