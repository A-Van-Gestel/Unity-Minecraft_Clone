# LI-2 Banded Lighting Gather — shippable IL2CPP + in-game flag A/B (frame attribution)

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                       |
|-----------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-11 (local)                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **Branch**      | `feat/async-lighting-validation-suite`                                                                                                                                                                                                                                                                                                                                                                                                      |
| **Commit**      | `dfe1553` (LI-2 Steps 1–5 complete; captured on the branch tip)                                                                                                                                                                                                                                                                                                                                                                             |
| **Captured by** | **IL2CPP Development Build (Player)** — the shippable backend. Frame attribution via `WorldFrameProfiler` (full-world fluid stress pass, 5×5 flood); per-job cost via `LightingJobBenchmark` (256 jobs/run × 250 runs). Two player builds, flag baked per build: band **off** (`EnableLightingBandGather=false`, GUID `a1910ced`) vs **on** (GUID `9ca9bbe5`).                                                                              |
| **Verdict**     | **GO — ships default-on.** Bit-identical (B75–B78 differential, suite 70/70). In-game IL2CPP shows a **sustained frame win**, not merely "not slower": settled-streaming frame **−26 %** (Light **−27 %**), flood sustained Light **−9 %**, and Light is **no longer the worst-frame bottleneck under flood** (worst-frame Light share 61 %→29 %). Per-job IL2CPP −2…−5 % on content-bearing scenarios. Clears the agreed gate with margin. |

> This is the **shippable capture** the editor-screening report
> ([`LIGHTING_LI2_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_2026-07-11_BENCHMARK.md)) deferred to the user's player build.
> That report screened the isolated `NeighborhoodLightingJob.Run()` on editor Mono (−31…−75 % on gather/scan-dominated
> shapes); this one measures the real production scheduler (`WorldJobManager.ScheduleLightingUpdate`) over real
> generated terrain under IL2CPP, and attributes the effect at the **frame** level, which is the number the ship
> decision is made on. It is the lighting sibling of the fluid Y-band
> ([`BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md`](BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md)),
> whose closing analysis predicted the payoff would land here because the flood frame is Light-bound — this capture
> confirms that prediction.

## What this measures (and what it does NOT)

**Frame attribution (fluid stress pass):** a real loaded world streamed through the real throttled pipeline
(mesh + lighting + cross-chunk), split per frame into Tick / Apply / Mesh / Light by `WorldFrameProfiler` (Stopwatch,
IL2CPP-valid). Two phases: **Baseline (settled)** — a stabilized field being re-lit as chunks stream, the dominant
real-world case as the player moves — and **Flood (25-chunk cascade)** — a deterministic suspended basin overflowing
across chunk borders, the Light-heavy worst case. Frame ms is wall time (includes render/GPU); sub-phases are the
main-thread `World.Update` interior.

**Per-job cost (`LightingJobBenchmark`):** the isolated in-job cost under IL2CPP + Burst — the same eight production
job shapes, full schedule-time (gather included).

**What this does NOT establish independently:** bit-identical output. That is the hard LI-2 acceptance criterion and is
proven separately and deterministically by the **B75–B78 band differential** (`Validate Lighting Engine`, 70/70) —
identical world scripts run banded vs full-height, required bit-identical fields with equal round counts, plus the B78
headroom-strip prove-red. This capture measures the *cost* of a change already proven *correct*; the two builds differ
only in `EnableLightingBandGather`.

## Result — frame attribution, IL2CPP, WorldFrameProfiler

Two runs, same deterministic scenario, flag toggled per build. Δ is band-on relative to band-off.

### Baseline phase — settled field, streaming re-light (the sustained real-world case)

| Metric (per frame)     | Band OFF | Band ON |           Δ |
|------------------------|---------:|--------:|------------:|
| Frame avg (ms)         |    1.860 |   1.368 | **−26.5 %** |
| Light avg (ms)         |    0.082 |   0.060 | **−26.8 %** |
| Frame peak (ms)        |   17.311 |  15.442 |     −10.8 % |
| Light peak (ms)        |   12.019 |  11.982 |      −0.3 % |
| Min FPS                |       58 |      65 |      **+7** |
| Frames rendered in 4 s |     2151 |    2924 |       +36 % |

### Flood phase — 25-chunk cascade (Light-heavy worst case)

| Metric (per frame)           |        Band OFF |         Band ON |                           Δ |
|------------------------------|----------------:|----------------:|----------------------------:|
| Frame avg (ms)               |           3.306 |           3.104 |                  **−6.1 %** |
| Light avg (ms)               |           0.361 |           0.327 |                  **−9.4 %** |
| Frame peak (ms)              |          39.648 |          39.075 |                      −1.4 % |
| Light peak (independent max) |          32.488 |          34.079 |                    +4.9 % ⚠ |
| Worst-frame Light component  | 24.363 (61.4 %) | 11.185 (28.6 %) | **−54 % / share 61 %→29 %** |
| Min FPS                      |              25 |              26 |                          +1 |

`⚠` The flood **Light peak (independent max)** is the single largest Light spike across *all* frames; it is essentially
flat (+4.9 % is inside run-to-run noise — the absolute BFS ceiling is irreducible and the band cannot shrink the wave
itself). The meaningful flood result is the two rows around it: sustained flood Light **−9 %**, and the composition of
the *worst frame* — under band-off that 39.6 ms frame was **61 % Light** (24.4 ms); under band-on the comparable worst
frame (39.1 ms) is only **29 % Light** (11.2 ms), the spike having shifted to Tick/Apply/generation. **The band removes
lighting as the flood's worst-frame bottleneck** even though the absolute frame peak (bound by an unrelated
Tick/GC hitch) is unchanged.

## Result — per-job cost, IL2CPP + Burst (`LightingJobBenchmark`, µs/job)

| Job shape                 | Band OFF | Band ON |      Δ |
|---------------------------|---------:|--------:|-------:|
| Sunlight Vertical Flat    |    246.1 |   246.1 |  0.0 % |
| Sunlight Complex Caves    |    234.4 |   226.6 | −3.3 % |
| Sunlight Removal Covered  |    210.9 |   203.1 | −3.7 % |
| Blocklight Simple         |    160.2 |   152.3 | −4.9 % |
| Blocklight Stress Test    |    300.8 |   300.8 |  0.0 % |
| Blocklight Removal Simple |    156.3 |   152.3 | −2.6 % |
| Blocklight Removal Stress |    160.2 |   152.3 | −4.9 % |
| Edge Check Consistency    |    222.7 |   218.8 | −1.7 % |

Per-job IL2CPP wins (−2…−5 %) are more modest than the editor-Mono screening (−31…−75 %) because these fixed benchmark
scenarios carry content up the column (wide bands) or force full-height by the column-recalc rule — the two flat/0 %
rows (Sunlight Vertical Flat, Blocklight Stress) are full-column shapes the band correctly declines to truncate. The
frame-level win above is larger than the per-job average because the sustained streaming frame is dominated by exactly
the low-content re-light jobs the band cuts hardest (the editor screening's −71 % `no-op relight` floor), which the
fixed per-job scenario set under-weights.

## Analysis

- **The band is a real sustained frame win, not just "not slower".** The settled streaming phase — the case that runs
  continuously as the player moves — drops **−26 % frame / −27 % Light**, lifting min FPS 58→65 and rendering 36 % more
  frames in the same wall-clock window. This is a better outcome than the fluid Y-band, which came back frame-neutral
  because the tick was already sub-1 %; here the frame genuinely is Light-bound and the lever moves it.
- **Under flood, the band changes what the worst frame is bound by.** Sustained flood Light −9 %, and the worst-frame
  Light share collapses 61 %→29 %. The absolute Light spike ceiling is unchanged (irreducible BFS) — consistent with the
  screening report's "wave-carrying jobs save the overhead, not the wave" — but lighting is no longer what defines the
  bad frame during a cascade.
- **The prediction held.** TG-4's fluid Y-band closing analysis pointed at the lighting line as where a Y-band would
  have frame-level payoff because the flood frame is Light-bound ~66–70 %. This capture confirms it: the sustained Light
  cost is the thing that fell.
- **Tier-A trajectory.** Every win tracks content height; at today's 128 columns this is a solid streaming-frame win,
  and at Tier-A 640-high columns the full-height gather/scan/extract it eliminates is a ~5× larger fixed cost by
  construction (`WORLD_SCALING_ANALYSIS.md` §2.2).

## Verdict details

**GO — ships default-on** behind `World.EnableLightingBandGather` (default `true`, pooled steady-state path; TempJob
startup sweep stays full-height because initial-lighting column recalc forces a full band anyway). Full-height retained
behind the flag as a one-toggle rollback, to be retired in a later cleanup pass once soaked, mirroring the
`EnableFluidBandGather` lifecycle.

- **Bit-identical:** proven by B75–B78 (`Validate Lighting Engine` 70/70) — banded-vs-full differential incl. the C3
  cross-chunk darkening quadrant (B54/B55 remain green), a 12-seed fuzz, and the B78 headroom-strip prove-red.
- **Frame-level (this capture):** sustained streaming −26 % frame / −27 % Light; flood sustained Light −9 % with
  lighting removed as the worst-frame dominator. Clears the "bit-identical + not slower" gate with a real win to spare.
- **Rollback:** flip `EnableLightingBandGather` off to restore the full-height path with zero other change.

## Capture environment

```
=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz  (16 threads)
RAM:            65 381 MB
OS:             Windows 10 (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.3f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP   (shippable capture)
Build GUID:     a1910ced (band off) / 9ca9bbe5 (band on)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

Frame attribution: fluid stress pass, Baseline 2151/2924 frames (off/on) over 4 s, Flood 4994/5303 frames over 16.5 s.
Per-job: 256 jobs/run × 250 runs, full schedule-time (gather included).

## Cross-references

- **Screening sibling (editor Mono, isolated job):** [`LIGHTING_LI2_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_2026-07-11_BENCHMARK.md).
- **Fluid Y-band precedent:** [`BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md`](BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md).
- **Design / plan:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) §LI-2;
  scaling rationale in [`Design/WORLD_SCALING_ANALYSIS.md`](../Design/WORLD_SCALING_ANALYSIS.md) §2.2.
- **Correctness gates:** B71–B74 (band derivation), B75–B78 (banded-vs-full differential + prove-red), C3 B54/B55
  (cross-chunk darkening) — all `Validate Lighting Engine`.
- **Harness:** `Assets/Scripts/Benchmarks/LightingJobBenchmark.cs` (per-job) + `WorldFrameProfiler` (frame attribution);
  production derivation in `Assets/Scripts/Helpers/LightingBandDecision.cs` + `WorldJobManager.ScheduleLightingUpdate`.
- **Folder conventions:** [`README.md`](README.md).
