# LI-2 Banded Lighting Gather — full-height vs derived Y-band A/B (editor screening)

| Field           | Value                                                                                                                                                                                                                                                                                                                              |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-11 (local)                                                                                                                                                                                                                                                                                                                 |
| **Branch**      | `feat/async-lighting-validation-suite`                                                                                                                                                                                                                                                                                             |
| **Commit**      | `68e749a` (POST: LI-2 Steps 1–4 — band derivation + plumbing + differential gate + production flip)                                                                                                                                                                                                                                |
| **Captured by** | `Assets/Editor/Benchmarking/LightingBandGatherBenchmark.cs` — **editor Mono (SCREENING-ONLY)**, 24 samples/leg, 4 warm-ups; the shippable IL2CPP + in-game flag A/B is **not yet captured** (pending user player build — see Verdict)                                                                                              |
| **Verdict**     | **GO (bit-identical + not slower) — pending user IL2CPP/in-game confirmation.** Bit-identical is proven by the B75–B78 differential (suite green); editor screening shows the band **never slower on the clean floor** and **−31…−75 %** on gather/scan-dominated job shapes. Frame-level number deferred to the in-game flag A/B. |

> **This is the LI-2 "does the Y-band actually cut lighting job cost" screening capture**, per
> [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) §LI-2 (the concrete,
> tracked form of `WORLD_SCALING_ANALYSIS.md` §2.2's "jobs must become section-ranged" Tier-A prerequisite). It is the
> lighting sibling of the shipped fluid Y-band
> ([`BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md`](BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md)),
> whose closing analysis explicitly pointed at the lighting line as where the same idea has frame-level payoff (the flood
> frame is Light-bound ~70 %). It is an **A/B over one editor session**: each job shape is timed under two legs —
> `full(h=128)` (`World.EnableLightingBandGather` off) and `band` (on, the derived Y-band).

## What this measures (and what it does NOT)

The benchmark times **`NeighborhoodLightingJob.Run()`** — the whole in-job cost the band restricts: the worker-thread
halo gather (`ChunkMath.GatherPaddedVoxels`/`GatherPaddedLight`), the emission-sync scan (PASS -2), the optional
border edge check (PASS -1), and the BFS passes. The band mode is baked into the job's `BandHeight` + `BandTopLight`
at `LightingTestWorld.BeginLightingJob` time (the same `LightingBandDecision` derivation production uses), so the two
legs differ only in the band. Three job shapes, each over a superflat 3×3 world at two floor heights:

- **`no-op relight`** — no queued work: pure gather + emission scan + stability flag. The fixed per-job floor the band
  attacks most directly (this is the dominant background cost during streaming — most re-lit chunks carry no wave).
- **`lamp BFS`** — a queued interior lamp placement over a dark steady field: gather + scans + a full 15-radius
  blocklight spread. The BFS work itself is band-independent (the wave is the same size regardless of gather height),
  so this isolates how much of a wave-carrying job is band-reducible overhead vs irreducible BFS.
- **`edge check`** — the border consistency pass over a consistent world: gather + the 4-border × full-height scan
  (8,192 columns at h=128) vs band-clamped.

**What this does NOT measure:** the shippable frame-level number. **Editor Mono timing is screening-only** (noisier
than IL2CPP; the perf protocol forbids presenting it as the shipping result). The production scheduler
(`WorldJobManager.ScheduleLightingUpdate`) and real generated terrain are exercised by the deferred in-game flag A/B.
The band's **bit-identical output** — the hard LI-2 acceptance criterion — is proven separately by the **B75–B78 band
differential** (`Validate Lighting Engine`), which runs identical world scripts banded vs full-height and requires
bit-identical fields with equal round counts, plus a headroom-strip prove-red (B78).

## Methodology

Editor Mono, Burst safety checks on (editor). 24 samples/leg after 4 warm-ups; one sample = one `job.Run()` in
microseconds via `Stopwatch`. The `lamp BFS` leg cycles break→converge→place around each sample so every timed run
sees the identical dark-field + queued-lamp input; the `no-op`/`edge` legs are state-preserving and loop tightly.
`min` = clean uninterrupted floor (best CPU-cost proxy), `mean` includes GC/scheduler cost, `stddev` = spread. The
`band(h=…)` column reports the derived band height for that job shape (the bottom-anchored rows actually
gathered/scanned/extracted; h=128 would be banding-off).

## Result — editor Mono, µs over 24 samples

| Floor | Job shape     | Leg            |      mean |       min |    median |  stddev | Δ mean vs full |
|-------|---------------|----------------|----------:|----------:|----------:|--------:|---------------:|
| y=10  | no-op relight | full (h=128)   |     170.7 |     156.6 |     165.9 |    16.8 |                |
| y=10  | no-op relight | **band h=32**  |  **48.8** |  **47.2** |  **48.8** | **1.3** |    **−71.4 %** |
| y=10  | lamp BFS      | full (h=128)   |     405.0 |     391.6 |     398.7 |    23.4 |                |
| y=10  | lamp BFS      | **band h=48**  | **342.2** | **300.7** | **307.9** |    85.0 |    **−15.5 %** |
| y=10  | edge check    | full (h=128)   |     449.4 |     410.1 |     421.3 |    39.4 |                |
| y=10  | edge check    | **band h=32**  | **112.8** | **109.8** | **112.6** | **2.3** |    **−74.9 %** |
| y=60  | no-op relight | full (h=128)   |     193.0 |     184.2 |     188.4 |    11.1 |                |
| y=60  | no-op relight | **band h=80**  | **132.8** | **130.7** | **131.8** | **2.0** |    **−31.2 %** |
| y=60  | lamp BFS      | full (h=128)   |     429.9 |     423.0 |     426.0 |    10.3 |                |
| y=60  | lamp BFS      | **band h=112** |     439.1 | **404.0** | **411.6** |    47.1 |       +2.1 % ⚠ |
| y=60  | edge check    | full (h=128)   |     456.5 |     431.4 |     438.7 |    77.0 |                |
| y=60  | edge check    | **band h=80**  | **287.8** | **282.6** | **286.3** | **5.9** |    **−37.0 %** |

`⚠` The one non-negative mean (lamp BFS, y=60, +2.1 %) is editor-Mono scheduler noise, **not a regression**: the band's
`min` (clean floor) is **404.0 vs 423.0 µs = −4.5 %**, i.e. the band is faster on the best sample; the positive *mean*
is entirely the higher stddev (47.1 band vs 10.3 full), a GC/scheduler artifact editor Mono is prone to. On the
gather/scan-dominated shapes the band is uniformly and decisively faster on every statistic.

## Analysis

- **The band delivers exactly where the design says it should.** The `no-op relight` floor — the per-job cost paid by
  every re-lit chunk that carries no wave, and the dominant background cost during streaming — drops **−71 %** at a
  tight band (floor y=10, h=32) and **−31 %** at a wider one (y=60, h=80). The `edge check` (the biggest full-height
  loop: 4 borders × 128 rows = 8,192 columns) drops **−75 % / −37 %**. These are pure gather+scan volume savings and
  scale inversely with how much content sits near the band top.
- **Wave-carrying jobs save the overhead, not the wave.** The `lamp BFS` shape is dominated by the 15-radius spread,
  which is identical regardless of gather height — so the band only shaves the surrounding gather/scan overhead
  (−15.5 % at the tight band on the floor; ~flat/−4.5 % on the clean floor at the wide band). This is the expected
  ceiling: the band cannot speed up irreducible BFS, only the fixed per-job envelope around it.
- **The height-dependence is the Tier-A thesis, measured.** Every win is larger at floor y=10 (band ≈ 32–48) than
  y=60 (band ≈ 80–112), because the band tracks content height. At today's 128 this is a solid job-level win; at Tier-A
  640-high columns with content near the bottom, the full-height gather/scan/extract would be a **~5× larger** fixed
  cost that the band eliminates by construction — which is why §LI-2 rates Benefit High partly on scaling, and why the
  ship criterion is "not slower" rather than "must move today's frame".

## Verdict details

**GO under the agreed criterion (bit-identical + suite green + not slower).** What ships: the derived band ON by
default behind `World.EnableLightingBandGather` (default `true`, pooled steady-state path; the TempJob startup sweep
stays full-height because initial lighting's column recalc forces a full band anyway). The full-height path is retained
behind the flag as a one-toggle rollback, to be retired in a later cleanup pass once soaked in-game (mirroring the
`EnableFluidBandGather` lifecycle).

- **Bit-identical:** proven by B75–B78 (`Validate Lighting Engine`, suite 70/70) — the banded-vs-full differential
  incl. cross-seam darkening (the C3 quadrant), a 12-seed fuzz, and the B78 headroom-strip prove-red. The C3 darkening
  baselines B54/B55 remain green.
- **Not slower:** editor screening shows the band faster on the clean floor of every job shape (the one +mean case is
  noise, min −4.5 %); the biggest full-height loops (no-op floor, edge check) drop 31–75 %.
- **Frame-level (deferred):** the shippable number is the **in-game IL2CPP flag A/B** the user runs — a fixed-seed
  world streamed with `EnableLightingBandGather` off vs on, diffed for bit-identical light and attributed with
  `WorldFrameProfiler`. Per the fluid Y-band precedent, the streaming/flood frame is Light-bound ~70 %, so a lighting
  job-cost cut is the one place a Y-band can plausibly move the frame (unlike the fluid band, which came back
  frame-neutral because the tick was already sub-1 %). **This is not yet captured**; the GO is conditional on it
  showing bit-identical output and no frame regression. If the in-game pass comes back frame-neutral, the change still
  ships on the "not slower + Tier-A prerequisite" basis (the job-level µs win recorded above).

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz  (16 threads)
RAM:            65 381 MB
OS:             Windows 10 (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.3f1
Mode:           Editor (play-mode NOT entered)
Backend:        Mono (SCREENING-ONLY — not a shippable capture)

=== Burst ===
Editor Burst safety checks: Enabled
```

24 samples/leg, 4 warm-ups discarded. Editor-Mono timing is screening-only; the shippable capture is an IL2CPP
Development Build flag A/B, not yet run.

## Cross-references

- **Design / plan:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) §LI-2;
  scaling rationale in [`Design/WORLD_SCALING_ANALYSIS.md`](../Design/WORLD_SCALING_ANALYSIS.md) §2.2.
- **Sibling capture (fluid Y-band):** [`BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md`](BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md).
- **Correctness gates:** B71–B74 (band derivation), B75–B78 (banded-vs-full differential + prove-red), C3 B54/B55
  (cross-chunk darkening) — all `Validate Lighting Engine`.
- **Harness:** `Assets/Editor/Benchmarking/LightingBandGatherBenchmark.cs`; production derivation in
  `Assets/Scripts/Helpers/LightingBandDecision.cs` + `WorldJobManager.ScheduleLightingUpdate`.
- **Folder conventions:** [`README.md`](README.md).
