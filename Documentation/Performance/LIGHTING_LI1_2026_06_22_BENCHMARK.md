# Lighting LI-1 Benchmark — Halo-Padded Volume vs 9-Map Dispatch

> **➡️ Superseded (2026-06-22): the "standalone NO-GO" verdict below was the decision NOT to ship LI-1 with
> its gather on the main thread. P-2 Phase 1 then moved that gather to the worker thread and the layout
> shipped net-positive (−34 % to −50 % vs this report's LI-1 POST full-timing). See
> [`LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md). This report is
> retained unedited as the PRE baseline for that capture.**

| Field           | Value                                                                                           |
|-----------------|-------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-06-21 / 2026-06-22 (local)                                                                 |
| **Branch**      | `feat/Modular-World-Generation-&-World-Types`                                                   |
| **PRE commit**  | parent of `0e20407` + benchmark toggle `6e4e9c4` cherry-applied (see *Methodology*)             |
| **POST commit** | `d4d16a6` (LI-1 layout `0e20407` + bulk gather `073b63e` + review cleanups `40dc1da`/`d4d16a6`) |
| **Captured by** | `Assets/Scripts/Benchmarks/LightingJobBenchmark.cs` (IL2CPP player)                             |
| **Verdict**     | **Standalone LI-1: NO-GO** (gather-bound). **Layout: validated win.** **→ fold into P-2.**      |

> **This file documents a decision, not a shipped baseline.** LI-1 is implemented, bit-identical, and
> suite-guarded, but is **not being shipped standalone** — the on-demand gather costs more on the main
> thread than the in-job BFS win it buys. The validated layout moves to **P-2** (persistent halo-padded
> storage), where the gather cost vanishes. See `PERFORMANCE_IMPROVEMENTS_REPORT.md` → LI-1.

## What LI-1 changed

Replaced `NeighborhoodLightingJob`'s input — **9 separate neighbor maps + a `NativeHashMap` write-through
cache** read per-neighbor-per-BFS-node — with a **single 20×128×20 halo-padded volume** (`PaddedVoxels`
read-only + `PaddedLight` read/write). The BFS inner loop becomes a **branch-free flat index**; the
hashmap read side disappears (halo writes are plain array writes, harvested into `CrossChunkLightMods` at
the end).

- **Halo width = 2, not 1.** The doc suggested a 1-voxel halo (18×128×18); that would be a *correctness
  bug*. The sunlight column-recalc darkness path enqueues neighbor nodes at the ±1 rim and
  `PropagateDarkness` reads *their* ±2 face neighbors, so all four edges **and** the four diagonal corners
  are genuinely read. Volume = 16 + 2·2 per horizontal axis; Y is full-height (out-of-Y stays
  sentinel-guarded). Constant: `ChunkMath.LIGHTING_HALO = MAX_LIGHTING_BFS_REACH = 2`.
- **Correctness bar: bit-identical light output** — any divergence re-dirties the edge-check cascade on
  old saves. Verified via the lighting suite (47/47 green incl. the C3 cross-chunk darkening seam guards
  B54/B55) **and** a three-state prove-red: clean green → zero the halo light → **exactly** the 11 seam
  baselines redden (B7/B12/B32/B43/B46/B48/B50–52/B54/B55), no in-chunk baseline → revert → green. This
  proves the new code is live, the seam guards are non-vacuous, and the clean run is true bit-identity
  (not stale false-green).

## Methodology — two timing scopes, identical bench on both builds

`LightingJobBenchmark` has a `_excludePrepareFromTiming` toggle (added in `6e4e9c4`, applied to **both**
the PRE and POST codebases so the comparison is apples-to-apples — delivered to the PRE build via
`benchmark-timing-isolation.patch`, a clean LI-1-independent 3-hunk diff):

- **Isolated** (`=true`) — schedule + in-job BFS only; **PrepareJob/gather EXCLUDED**. Isolates the
  worker-thread BFS self-time (the cost LI-1 set out to cut).
- **Full** (`=false`) — full schedule-time; **gather INCLUDED**. The total per-chunk cost as production
  pays it at schedule time.

All runs: IL2CPP player, i9-9900K (16 threads), 65 GB RAM, Windows 10 (10.0.19045), Direct3D11, Burst
enabled + safety checks on. **256 jobs/run × 250 runs.**

> ⚠️ **PRE and POST `PrepareJob` are not the same work.** PRE has *no* gather — its prepare builds the 9
> neighbor maps the old job consumed. POST's prepare is the padded-volume gather. So "full-timing" compares
> *old 9-map prep* vs *new padded gather* as the schedule-time line item, which is exactly the production
> trade-off LI-1 makes.

## Result 1 — Isolated (in-job BFS only): LAYOUT VALIDATED, 2.4–3× faster

µs/job (derived from ms/run ÷ 256), PRE → POST:

| Scenario                  |   PRE |  POST |   Speedup   |
|---------------------------|------:|------:|:-----------:|
| Sunlight Vertical Flat    | 234.4 |  82.0 |  **2.86×**  |
| Sunlight Complex Caves    | 238.3 |  82.0 |  **2.91×**  |
| Sunlight Removal Covered  | 152.3 |  50.8 |  **3.00×**  |
| Blocklight Stress Test    | 367.2 | 152.3 |  **2.41×**  |
| Edge Check Consistency    | 187.5 |  70.3 |  **2.67×**  |
| Blocklight Simple         |  11.7 |   7.8 |  small abs  |
| Blocklight Removal Simple |   3.9 |   3.9 | noise floor |
| Blocklight Removal Stress |   3.9 |   3.9 | noise floor |

**The branch-free flat index genuinely speeds the BFS by 2.4–3× on every substantive scenario.** Wall-clock
of the POST run was *longer* (3m51s vs 2m18s) despite faster timed numbers — that's the now-untimed gather
still costing real main-thread wall-clock.

> **Round-1 correction.** An earlier run (50 runs, full-timing) read as "the rewrite buys nothing" because
> Blocklight Stress looked flat. That was **wrong** — the gather, inside the timed region, was masking the
> in-job win. Increasing runs (50→250) only lowered the noise floor; it was the *timing-scope* fix
> (isolation) that revealed the win. Lesson: separate worker-thread self-time from main-thread schedule
> time before judging a job-internal optimization.

## Result 2 — Full (gather included, #3-optimized bulk gather): STANDALONE NO-GO

µs/job, PRE → POST:

| Scenario                   |   PRE |  POST |          Δ |
|----------------------------|------:|------:|-----------:|
| Sunlight Vertical Flat     | 371.1 | 386.7 |   **+4 %** |
| Sunlight Complex Caves     | 367.2 | 394.5 |   **+7 %** |
| Sunlight Removal Covered   | 273.4 | 363.3 |  **+33 %** |
| Blocklight Simple          | 121.1 | 312.5 | **+158 %** |
| **Blocklight Stress Test** | 503.9 | 468.8 | **−7 % ✓** |
| Blocklight Removal Simple  | 113.3 | 312.5 | **+176 %** |
| Blocklight Removal Stress  | 113.3 | 312.5 | **+176 %** |
| Edge Check Consistency     | 312.5 | 386.7 |  **+24 %** |

**Only the single most BFS-bound scenario (Blocklight Stress) comes out ahead, and only by 7 %.** Everything
else regresses; the cheap scenarios catastrophically.

### Why — the gather is a scenario-independent floor

Subtract the isolated (BFS-only) number from the full number to isolate the **prepare/gather cost**:

|                                        | Isolated (BFS) |  Full |     Prepare cost |
|----------------------------------------|---------------:|------:|-----------------:|
| **POST gather** (Blocklight Simple)    |            7.8 | 312.5 | **≈ 305 µs/job** |
| **PRE 9-map prep** (Blocklight Simple) |           11.7 | 121.1 | **≈ 109 µs/job** |

The POST gather copies the **whole 51 200-cell volume regardless of light content**, so every POST scenario
sits on a hard ~80 ms / ~313 µs/job floor. That floor is **~2.6× more expensive than PRE's 9-map prep**. The
2.4–3× worker-BFS win is real, but smaller in *absolute µs* than the extra main-thread cost the gather adds —
so it only nets out ahead where the BFS itself dominates (Blocklight Stress).

**#3 (bulk-copy gather) did not close the gap.** It was a legitimate, bit-identical cleanup (replaced
per-cell branchy scatter with 3 bulk `NativeArray.Copy` runs per row: 2-wide West halo + 16-wide center span

+ 2-wide East halo, pz-band→source dispatch hoisted out of the inner loop — valid because X is the fastest
  axis (stride 1) in *both* the section-aware source and the linear padded layout). But the per-row structure
  is **128 y × 20 z × 3 segments ≈ 7 680 small `Copy` calls per buffer, ×2 buffers** — copy-call-overhead-bound,
  and the section-aware↔linear layout mismatch caps contiguity at the 16-wide X-run. That is roughly the floor
  for an *on-demand* gather.

## Verdict & decision

| Question                                   | Answer                                                                                                                                                     |
|--------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Is the halo-padded layout a real BFS win?  | **Yes** — 2.4–3× worker-thread BFS self-time (Result 1).                                                                                                   |
| Does standalone LI-1 win at schedule time? | **No** — the on-demand gather costs ~2.6× PRE's prep; net flat-to-worse on all but the most BFS-bound scenario (Result 2).                                 |
| Did the #3 gather optimization flip it?    | **No** — bit-identical cleanup, but the gather floor is inherent to an on-demand copy.                                                                     |
| Where does the layout belong?              | **P-2** — persistent halo-padded storage means the data is *already* padded, so the per-chunk gather **vanishes** and the 2.4–3× BFS win is kept for free. |

**Decision: do NOT ship standalone LI-1. Pursue P-2 immediately, building it on this validated layout.**

> **Microbench caveat (why this is "NO-GO standalone", not "LI-1 is bad").** The bench serializes 256
> gathers back-to-back on the **main thread** and runs the BFS serially too. Production amortizes the gather
> per-chunk at schedule time and lands the BFS savings on **saturated worker threads** during streaming —
> so this microbench structurally *over-weights* the gather and *under-weights* the parallel BFS benefit. It
> cannot settle whether standalone LI-1 helps the live game; only an **in-game profiler capture during chunk
> streaming** (main-thread headroom vs worker saturation) could. We are skipping that and going straight to
> P-2 because P-2 removes the gather entirely and makes the trade-off moot.

The LI-1 branch is **not wasted** — it is the proven foundation P-2 builds on: branch-free accessors, the
`LIGHTING_HALO = MAX_LIGHTING_BFS_REACH = 2` invariant, the gather/extract transcoders, and **47 lighting
baselines guarding bit-identity** across the halo seam.

## Capture environment

```
=== Build ===
Unity:          6000.5.0f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP

Build GUIDs:
  PRE  isolated   9496d496017b4d1c8c7fbbc335b204be
  POST isolated   eb874aeaf4684cbfb3f8f2e0b104848b
  PRE  full       bc2670204b6b4a70a04c184f671fa183
  POST full       bf50cfae725f44159ee6a6d5719447d7

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

System: i9-9900K, 16 threads, 65 381 MB RAM, Windows 10 (10.0.19045), Direct3D11.
256 jobs per run, averaged over 250 runs. Ms/run rounds to integer; µs/job is derived (1 ms = 3.9 µs/job at
256 jobs), which quantizes the cheap scenarios — read the cheap-scenario deltas as "floor-bound", the
magnitudes are coarse.

## Cross-references

- **Design doc:** [`Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) — **LI-1** (status block) and **P-2** (the destination).
- **Scaling context:** [`WORLD_SCALING_ANALYSIS.md`](../Design/WORLD_SCALING_ANALYSIS.md) §6 — halo-padded persistent storage is a shared prerequisite.
- **Suite guards:** lighting baselines B54/B55 (C3 cross-chunk darkening seam) + the 11 seam baselines listed above; harness doc [`LIGHTING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md).
- **Folder conventions:** [`Documentation/Performance/README.md`](README.md).
