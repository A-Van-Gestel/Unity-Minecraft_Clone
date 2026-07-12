# LI-2b Bottom Lighting Band — shippable IL2CPP + in-game flag A/B (frame attribution)

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
|-----------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-11 (local, ~16:18–16:24)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| **Branch**      | `feat/async-lighting-validation-suite`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| **Commit**      | `61c7832`+ (LI-2b Steps 1–5; captured on the branch tip)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **Captured by** | **IL2CPP Development Build (Player)** — the shippable backend. Frame attribution via `WorldFrameProfiler` (full-world fluid stress pass, 5×5 flood); per-job via `LightingJobBenchmark` (256 jobs/run × 250 runs — **noise floor only, see below: that harness pins full height in both builds**). Two player builds, flag baked per build: band **off** (GUID `c513a4ab`) vs **on** (GUID `0c1a2846`).                                                                                                                                                       |
| **Verdict**     | **GO — ships default-on (frame-neutral in-game, the outcome the plan explicitly priced in).** Bit-identical (B83–B85, suite 77/77) + in-game underground lamp verification. Flood and settled-basin phases are frame-neutral (all deltas within cross-run variance; worst-frame Light −8.6 %, settled tails/GC better). The bottom band's engaged wins (−49…−59 % marginal, editor screening) live in scenarios this stress pass does not weight; it ships on the agreed **not-slower + Tier-A prerequisite** basis, exactly like the fluid Y-band precedent. |

> This is the **shippable capture** the LI-2b screening report
> ([`LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md`](LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md)) deferred to
> the user's player builds — the LI-2b sibling of
> [`LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md).
> **Framing caveat:** the OFF leg is *full height* (the flag disables the whole band), so this A/B measures
> **LI-2 + LI-2b combined vs no banding** — the LI-2b increment cannot be isolated at the frame level from these two
> legs alone. Cross-session comparison against the LI-2 capture's legs is indicative only (the same scenario's OFF leg
> differs by ~23 % between the two same-day sessions — see Analysis), which is exactly why the per-report verdicts rest
> on same-session deltas.

## What this measures (and what it does NOT)

**Frame attribution (fluid stress pass):** identical to the LI-2 capture — a real loaded world through the real
throttled pipeline, per-frame Tick / Apply / Mesh / Light split, two phases: **Baseline (settled)** — the stabilized
suspended-basin field (4 s) — and **Flood (25-chunk cascade)** — the Light-heavy worst case (16.4 s). Note the flood's
content sits at y100–110 and its lighting work is column-recalc + BFS dominated; queued recalcs force the bottom band
to 0 **by design**, so the flood was never LI-2b's target scenario. The scenarios where the bottom band engages
hardest — settled-streaming re-lights and edge checks over terrain with deep dark undersides, and underground relight
churn — are under-weighted by this pass (the LI-2 capture's settled-streaming phase is the closest proxy, not re-run
this session).

**Per-job (`LightingJobBenchmark`) — a noise-floor measurement, not a band measurement:** this harness constructs its
jobs directly with `BandHeight = CHUNK_HEIGHT, BandMinY = 0` (it deliberately benchmarks the full-height job path and
never goes through `ScheduleLightingUpdate`), so **the flag does not reach it — both builds run identical job
configurations** and its deltas bound the build/session noise floor, nothing else. This retroactively qualifies the
per-job table in the LI-2 in-game report too (append-only; noted here rather than edited there): its −2…−5 % per-job
deltas are the same class of build-to-build noise. A band-aware variant of `LightingJobBenchmark` is a follow-up if
per-job IL2CPP band numbers are ever needed.

**What this does NOT establish independently:** bit-identical output — proven separately by the B83–B85 bottom
differential with its engagement assertion and raised-floor prove-red (`Validate Lighting Engine` 77/77,
`Validate All` 174), plus the user's in-game underground-lamp verification (cave lamps at chunk centers and
chunk/section borders, break + re-place cycles).

## Result — frame attribution, IL2CPP, WorldFrameProfiler

Δ is band-on relative to band-off, same session, same deterministic scenario.

### Baseline phase — settled suspended-basin field (4 s)

| Metric (per frame)     | Band OFF | Band ON |        Δ |
|------------------------|---------:|--------:|---------:|
| Frame avg (ms)         |    1.439 |   1.571 | +9.2 % ⚠ |
| Light avg (ms)         |    0.070 |   0.075 | +7.1 % ⚠ |
| Frame peak (ms)        |   16.495 |  12.975 |  −21.3 % |
| Light peak (ms)        |    9.507 |   9.788 |   +3.0 % |
| Min FPS                |       61 |      77 |  **+16** |
| GC/frame avg (KB)      |      1.0 |     0.3 |    −70 % |
| GC/frame peak (KB)     |   1772.0 |   152.0 |    −91 % |
| Frames rendered in 4 s |     2780 |    2547 |          |

`⚠` The settled averages sit inside cross-run variance for this 4-second phase: the same configuration's OFF leg
measured 1.860 ms in the LI-2 session and 1.439 ms here — a 23 % swing between two same-day sessions of the *identical*
scenario, dwarfing the +0.13 ms delta above. The tail metrics moved the other way (frame peak −21 %, min FPS 61→77,
GC peak −91 % — the OFF leg's 1772 KB GC spike is likely the incidental cause of its worse peak/min-FPS). Honest
reading: **settled is neutral at this phase's noise level**, with no evidence of regression in either direction that
survives the variance.

### Flood phase — 25-chunk cascade (Light-heavy worst case)

| Metric (per frame)           |       Band OFF |        Band ON |          Δ |
|------------------------------|---------------:|---------------:|-----------:|
| Frame avg (ms)               |          2.979 |          3.015 |     +1.2 % |
| Light avg (ms)               |          0.323 |          0.331 |     +2.5 % |
| Frame peak (ms)              |         37.165 |         37.260 |     +0.3 % |
| Light peak (independent max) |         32.224 |         32.756 |     +1.7 % |
| Worst-frame Light component  | 9.629 (25.9 %) | 8.805 (23.6 %) | **−8.6 %** |
| Min FPS                      |             27 |             27 |          0 |
| GC/frame avg (KB)            |            2.1 |            2.0 |            |
| GC/frame peak (KB)           |         1396.0 |          796.0 |            |

**Flood is frame-neutral** — every average and peak within ±2.5 %, min FPS identical. Expected: the flood's lighting is
recalc-driven (bottom band 0 by rule) and BFS-bound (irreducible wave). Notably this session's OFF leg already sits at
the LI-2 session's *ON*-leg flood Light level (0.323 vs 0.327 ms) — further evidence that cross-session absolute
comparisons for this pass carry ~10 %-class variance and only same-session deltas are meaningful.

## Result — per-job, IL2CPP + Burst (noise floor; both builds run identical full-height jobs)

| Job shape                 | Band OFF | Band ON |      Δ |
|---------------------------|---------:|--------:|-------:|
| Sunlight Vertical Flat    |    257.8 |   273.4 | +6.1 % |
| Sunlight Complex Caves    |    246.1 |   261.7 | +6.3 % |
| Sunlight Removal Covered  |    210.9 |   218.8 | +3.7 % |
| Blocklight Simple         |    156.3 |   156.3 |  0.0 % |
| Blocklight Stress Test    |    335.9 |   335.9 |  0.0 % |
| Blocklight Removal Simple |    152.3 |   152.3 |  0.0 % |
| Blocklight Removal Stress |    152.3 |   152.3 |  0.0 % |
| Edge Check Consistency    |    246.1 |   238.3 | −3.2 % |

The harness pins `BandHeight = 128, BandMinY = 0` in both builds (see above), so this table's spread (−3…+6 %) is the
**build/session noise floor** for identical code paths — useful as the error bar for every other IL2CPP number, and a
bound on any accessor-level cost of the LI-2b band checks (which are present in both legs here; a genuine accessor
regression would need a build where they are absent, i.e. a pre-LI-2b commit, same session — not captured).

## Analysis

- **The plan's prediction held precisely.** The LI-2b plan stated up front: *column recalcs force the bottom to 0, so
  floods and initial lighting keep LI-2's profile; the new wins are confined to settled-streaming re-lights.* This
  capture confirms the first half (flood frame-neutral) and does not exercise the second (no streaming phase in this
  session). The engaged-scenario evidence remains the editor screening's marginal −49…−59 % on no-op relights and edge
  checks over dark undersides, and the combined band's frame-level value remains anchored by the LI-2 in-game capture's
  settled-streaming −26 %.
- **Nothing got slower.** Same-session deltas are within the measured noise floor everywhere; the only metric beyond
  it is the settled tail improving (frame peak −21 %, min FPS +16 — plausibly GC-luck rather than band merit, and
  claimed as neither).
- **The fluid Y-band precedent applies verbatim.** TG-4's Y-band shipped frame-neutral on the "correct-by-construction +
  height-independence" basis; LI-2b ships the same way, with the additional asset that its top-band sibling already
  banked a real frame win. At Tier-A 640-high columns the dark underside below player content is proportionally far
  larger than today's 2–3 sections — the bottom band is the half of the §2.2 prerequisite that grows with depth.

## Verdict details

**GO — ships default-on**, folded into `World.EnableLightingBandGather` (single flag by decision; rollback = full
height). Pooled steady-state path only; TempJob startup sweep full-height, unchanged.

- **Bit-identical:** B79–B85 (`Validate Lighting Engine` 77/77, `Validate All` 174) incl. the engagement assertion and
  raised-floor prove-red; in-game underground lamp verification by the user.
- **Frame-level (this capture):** neutral in both phases within the session's measured noise floor; no regression.
- **Job-level (editor screening):** marginal −49…−59 % where the bottom engages; parity where it cannot.
- **Ship basis:** the agreed "bit-identical + suite green + not slower" gate — met; the frame win case
  (settled-streaming over deep terrain) is banked prospectively on the screening evidence and the Tier-A scaling
  rationale rather than a captured frame number, mirroring the fluid Y-band's shipped rationale.
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
Build GUID:     c513a4ab (band off) / 0c1a2846 (band on)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled
```

Frame attribution: fluid stress pass, Baseline 2780/2547 frames (off/on) over 4 s, Flood 5511/5452 frames over 16.4 s.
Per-job: 256 jobs/run × 250 runs, full schedule-time (gather included) — full-height configuration in both builds.

## Cross-references

- **Screening sibling (editor Mono, 3-leg full/top/t+b):**
  [`LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md`](LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md).
- **Top-band in-game predecessor:**
  [`LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md) (its
  per-job table carries the same full-height-pinned caveat documented here).
- **Fluid Y-band precedent (frame-neutral GO):**
  [`BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md`](BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md).
- **Design / plan:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) §LI-2
  (LI-2b banner); scaling rationale in [`Design/WORLD_SCALING_ANALYSIS.md`](../Design/WORLD_SCALING_ANALYSIS.md) §2.2.
- **Correctness gates:** B79–B82 (bottom derivation + emissive metadata), B83–B85 (bottom differential + engagement +
  prove-red) — `Validate Lighting Engine` 77/77.
- **Folder conventions:** [`README.md`](README.md).
