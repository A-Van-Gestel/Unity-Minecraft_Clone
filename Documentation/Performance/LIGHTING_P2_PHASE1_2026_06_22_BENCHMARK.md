# Lighting P-2 Phase 1 Benchmark — Worker-Thread Gather (Layer 1)

| Field            | Value                                                                                                                |
|------------------|----------------------------------------------------------------------------------------------------------------------|
| **Captured**     | 2026-06-22 (local)                                                                                                   |
| **Branch**       | `feat/Modular-World-Generation-&-World-Types`                                                                        |
| **POST commit**  | `e3e1635` (P-2 Phase 1 worker-thread gather) + `ffcaf6c` (full-timing toggle flip for this capture)                  |
| **PRE baseline** | `LIGHTING_LI1_2026_06_22_BENCHMARK.md` — "Result 2" full-timing (LI-1 POST, the NO-GO predecessor)                   |
| **Captured by**  | `Assets/Scripts/Benchmarks/LightingJobBenchmark.cs` (IL2CPP player), full-timing (`_excludePrepareFromTiming=false`) |
| **Verdict**      | **Phase 1 acceptance gate MET — net-positive, shipped.** LI-1 flips NO-GO → GO.                                      |

> **This is the acceptance-gate capture for P-2 Phase 1** (Layer 1, worker-thread gather), the deliverable
> designed in [`Design/PERSISTENT_CHUNK_STORAGE_P2.md`](../Design/PERSISTENT_CHUNK_STORAGE_P2.md) §8. It
> supersedes the standalone-LI-1 "NO-GO" verdict of
> [`LIGHTING_LI1_2026_06_22_BENCHMARK.md`](LIGHTING_LI1_2026_06_22_BENCHMARK.md): the validated halo-padded
> layout is now net-positive once its gather runs on the worker thread instead of serially on the main
> thread before scheduling.

## What Phase 1 changed

LI-1 moved the BFS onto a branch-free halo-padded volume (validated 2.4–3× in-job BFS win) but ran the
per-chunk gather that feeds it **serially on the main thread** before scheduling (~305 µs/job floor),
making it net-negative at total schedule-time cost. Phase 1 moves that gather **into
`NeighborhoodLightingJob.Execute()`**, so it runs on the worker thread (in parallel across all cores via the
benchmark's `JobHandle.CombineDependencies` batch) instead of serially on the main thread. Inputs are the
same point-in-time snapshots, so output is **bit-identical** (suite 47/47 incl. C3 B54/B55 + the 11-seam
prove-red). The main thread now pays only the snapshot fill; the worker pays gather + the faster BFS.

## Methodology

IL2CPP player, **full-timing** (`_excludePrepareFromTiming=false` — the timed region covers PrepareJob fill

+ schedule + the in-job gather + BFS), 256 jobs/run × 250 runs, Burst enabled + safety checks on. Identical
  harness to the LI-1 capture. The PRE numbers below are LI-1 POST "Result 2" (the gather-on-main-thread
  build); the pre-LI-1 column is the original 9-map dispatch (LI-1 doc "Result 2", PRE column), included to
  show the result against the true status quo.

> ⚠️ **Timing-scope note:** post-Phase-1 the gather is *inside the job*, so the `_excludePrepareFromTiming=true`
> ("isolated") mode now times gather + BFS and is **no longer comparable** to LI-1's isolated (BFS-only)
> numbers. Use **full-timing** for any Phase-1 comparison.

## Result — full-timing, µs/job

| Scenario                  | pre-LI-1 | LI-1 POST (PRE) | **P-2 Ph1 (POST)** | vs LI-1 POST | vs pre-LI-1 |
|---------------------------|---------:|----------------:|-------------------:|:------------:|:-----------:|
| Sunlight Vertical Flat    |    371.1 |           386.7 |          **234.4** |  **−39 %**   |    −37 %    |
| Sunlight Complex Caves    |    367.2 |           394.5 |          **226.6** |  **−43 %**   |    −38 %    |
| Sunlight Removal Covered  |    273.4 |           363.3 |          **199.2** |  **−45 %**   |    −27 %    |
| Blocklight Stress Test    |    503.9 |           468.8 |          **308.6** |  **−34 %**   |    −39 %    |
| Edge Check Consistency    |    312.5 |           386.7 |          **210.9** |  **−45 %**   |    −33 %    |
| Blocklight Simple         |    121.1 |           312.5 |          **156.3** |  **−50 %**   |    +29 %    |
| Blocklight Removal Simple |    113.3 |           312.5 |          **156.3** |  **−50 %**   |    +38 %    |
| Blocklight Removal Stress |    113.3 |           312.5 |          **156.3** |  **−50 %**   |    +38 %    |

Total wall-clock for the POST run: **1m 51s** (the LI-1 POST run was ~3m 51s).

## Reading the result

- **vs LI-1 POST: −34 % to −50 % on every scenario.** Relocating the gather to the worker did exactly what
  it was designed to: the serial ~305 µs/job main-thread floor parallelized down to ~156 µs. This is the
  change that converts LI-1 from NO-GO to GO.
- **vs the original pre-LI-1 baseline: every BFS-substantive scenario is −27 % to −39 %** — vertical
  sunlight, caves, removal-covered, blocklight stress, edge checks. The 2.4–3× branch-free BFS win now shows
  through at *total* cost, not just isolated self-time.
- **The only regressions (+29 % to +38 % vs pre-LI-1) are the three trivial near-no-op scenarios**
  (Blocklight Simple / Removal Simple / Removal Stress). Note they all bottom out at the **same** 156.3 µs:
  with ~no BFS work, the unconditional 51,200-cell gather (copy #2) dominates. In real streaming these are
  the rare incremental updates; the column/cave/stress jobs that dominate generation + loading are all big
  winners — net-positive where it counts.

## Verdict & decision

| Question                                                  | Answer                                                                                                                                                                                                                                                                                 |
|-----------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Did Phase 1 flip LI-1 net-positive at full schedule-time? | **Yes** — −34 % to −50 % vs LI-1 POST; net-positive on every substantive scenario vs pre-LI-1.                                                                                                                                                                                         |
| Phase 1 acceptance gate (§8) met?                         | **Yes** — bit-identical (suite + prove-red) AND full-timing net-positive. **Shipped.**                                                                                                                                                                                                 |
| Is Layer 2 (zero-copy persistent storage) warranted now?  | **No.** The residual ~156 µs floor on trivial jobs is the *gather* (copy #2), and the win is already substantial. Layer 2 is 🔴 high-risk and gated on a profiler showing the schedule-time *fill* (copy #1) is the bottleneck — that gate was **not** triggered. Layer 2 stays gated. |

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz  (16 threads)
RAM:            65 381 MB
OS:             Windows 10 (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     1243cd6b4236465c9be40b9e90fa1005

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

256 jobs per run, averaged over 250 runs. Ms/run rounds to integer; µs/job is derived (1 ms ≈ 3.9 µs/job at
256 jobs), which quantizes the cheap scenarios — read the trivial-scenario numbers as "gather-floor-bound",
the magnitudes are coarse.

## Cross-references

- **Predecessor / PRE baseline:** [`LIGHTING_LI1_2026_06_22_BENCHMARK.md`](LIGHTING_LI1_2026_06_22_BENCHMARK.md).
- **Design:** [`Design/PERSISTENT_CHUNK_STORAGE_P2.md`](../Design/PERSISTENT_CHUNK_STORAGE_P2.md) §3 (Layer 1), §8 (Phase 1), §10 (validation).
- **Backlog:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — P-2, LI-1.
- **Folder conventions:** [`README.md`](README.md).
