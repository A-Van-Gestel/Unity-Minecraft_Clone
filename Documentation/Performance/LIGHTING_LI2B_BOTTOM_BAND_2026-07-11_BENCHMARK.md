# LI-2b Bottom Lighting Band — full vs top-only vs top+bottom A/B (editor screening)

| Field           | Value                                                                                                                                                                                                                                                                                                                                                                                                                |
|-----------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-11 (local)                                                                                                                                                                                                                                                                                                                                                                                                   |
| **Branch**      | `feat/async-lighting-validation-suite`                                                                                                                                                                                                                                                                                                                                                                               |
| **Commit**      | `61c7832` (POST: LI-2b Steps 1–4 — emissive metadata + bottom derivation + BandMinY plumbing + production flip)                                                                                                                                                                                                                                                                                                      |
| **Captured by** | `Assets/Editor/Benchmarking/LightingBandGatherBenchmark.cs` — **editor Mono (SCREENING-ONLY)**, 24 samples/leg, 4 warm-ups; the shippable IL2CPP + in-game flag A/B is **not yet captured** (pending user player build — see Verdict)                                                                                                                                                                                |
| **Verdict**     | **GO (bit-identical + not slower) — pending user IL2CPP/in-game confirmation.** Bit-identical is proven by the B83–B85 bottom differential (suite 77/77 green, engagement-asserted). Editor screening: where the bottom band engages it cuts another **−49…−59 % on top of the shipped LI-2 top band** (gather/scan-dominated shapes); where it cannot engage it is **never slower** (marginal deltas within noise). |

> **This is the LI-2b "does the bottom band pay on top of LI-2" screening capture** — the second half of
> [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) §LI-2 (the bottom band was
> deliberately deferred out of LI-2 v1; this capture closes that follow-up). It extends the LI-2 screening
> ([`LIGHTING_LI2_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_2026-07-11_BENCHMARK.md)) whose in-game IL2CPP confirmation
> ([`LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md)) recorded
> the sustained frame win the top band shipped on. It is an **A/B/B over one editor session**: each job shape is timed
> under three legs — `full` (banding off, h=128/b=0), `top` (Derived with the bottom sabotaged to 0 — **exactly the
> shipped LI-2 configuration**, the drift-corrected local baseline), and `t+b` (Derived top+bottom, the LI-2b change
> under test). The `vs top` column is therefore the bottom band's **marginal** win.

## What this measures (and what it does NOT)

Same harness and timing envelope as the LI-2 screening: **`NeighborhoodLightingJob.Run()`** — worker-thread halo gather,
emission-sync scan, optional border edge check, and the BFS passes. The band configuration is baked in at
`LightingTestWorld.BeginLightingJob` time through the same shared `LightingBandDecision` production uses
(`DeriveBandHeight` + the new `DeriveBandMinY`). The original three shapes run at the original two floors, plus a new
**deep-floor section (y=47, three full dark stone sections)** carrying the LI-2b shapes:

- **`no-op relight`** — the streaming-dominant background shape; now bounded on BOTH ends where the floor is deep.
- **`lamp BFS` / `buried lamp`** — a queued lamp placement (above-ground / inside solid stone at y=20). Note both carry
  a **sunlight column recalc** in the timed job (the place edit changes column opacity), which **forces the bottom to 0
  by design** — the recalc's PASS 2 walks to Y=0. These legs measure the "bottom cannot engage" parity case.
- **`edge check`** — the 4-border scan, now clamped on both ends.

**What this does NOT measure:** the shippable frame-level number (editor Mono is screening-only; the production
scheduler and real terrain are exercised by the deferred in-game flag A/B). **Bit-identical output** is proven
separately by the **B83–B85 bottom differential** (`Validate Lighting Engine` 77/77): identical world scripts banded vs
full-height, bit-identical fields + equal round counts, an **engagement assertion** (at least one banded job must
actually derive `bandMinY > 0` — a vacuous always-0 bottom cannot pass), an 8-seed deep-floor fuzz (B84), and a
raised-floor prove-red (B85). The engagement assertion earned its keep immediately: it caught an externally-mangled
`GetHeightmapMinY` (always returning 0 = bottom silently disabled) that bit-identity alone could never see.

## Methodology

Editor Mono, Burst safety checks on (editor). 24 samples/leg after 4 warm-ups; one sample = one `job.Run()` in
microseconds via `Stopwatch`. Lamp legs cycle break→converge→place around each sample; `no-op`/`edge` legs are
state-preserving and loop tightly. `min` = clean uninterrupted floor (best CPU-cost proxy), `mean` includes
GC/scheduler cost. The leg columns report the actual band the jobs ran with: `h` = exclusive band top (128 = top
banding off), `b` = band bottom (`BandMinY`, 0 = bottom banding off/not engaged).

## Result — editor Mono, µs over 24 samples

| Floor     | Job shape     | Leg                  |      mean |       min |    median |   stddev | Δ mean vs full | Δ mean vs top (marginal) |
|-----------|---------------|----------------------|----------:|----------:|----------:|---------:|---------------:|-------------------------:|
| y=10      | no-op relight | full (h=128, b=0)    |     197.4 |     191.1 |     194.5 |      7.3 |                |                          |
| y=10      | no-op relight | top (h=32, b=0)      |      58.8 |      57.7 |      58.4 |      1.2 |        −70.2 % |                          |
| y=10      | no-op relight | **t+b (h=32, b=0)**  |  **58.4** |  **57.7** |  **58.2** |  **0.7** |    **−70.4 %** |                   −0.7 % |
| y=10      | lamp BFS      | full (h=128, b=0)    |     519.8 |     499.1 |     506.5 |     34.3 |                |                          |
| y=10      | lamp BFS      | top (h=48, b=0)      |     403.8 |     388.1 |     396.6 |     16.0 |        −22.3 % |                          |
| y=10      | lamp BFS      | **t+b (h=48, b=0)**  |     415.1 | **386.0** |     399.1 |     49.1 |        −20.1 % |                 +2.8 % ⚠ |
| y=10      | edge check    | full (h=128, b=0)    |     496.7 |     486.0 |     495.2 |     11.9 |                |                          |
| y=10      | edge check    | top (h=32, b=0)      |     146.6 |     131.0 |     136.1 |     20.8 |        −70.5 % |                          |
| y=10      | edge check    | **t+b (h=32, b=0)**  | **140.9** |     131.6 | **135.7** | **12.2** |    **−71.6 %** |                   −3.9 % |
| y=60      | no-op relight | full (h=128, b=0)    |     245.5 |     229.9 |     238.7 |     19.8 |                |                          |
| y=60      | no-op relight | top (h=80, b=0)      |     170.3 |     162.0 |     165.2 |     15.8 |        −30.6 % |                          |
| y=60      | no-op relight | **t+b (h=80, b=44)** |  **70.1** |  **68.2** |  **68.9** |  **4.8** |    **−71.5 %** |              **−58.8 %** |
| y=60      | lamp BFS      | full (h=128, b=0)    |     548.3 |     537.8 |     545.3 |      8.9 |                |                          |
| y=60      | lamp BFS      | top (h=112, b=0)     |     528.4 |     514.9 |     524.9 |     14.6 |         −3.6 % |                          |
| y=60      | lamp BFS      | **t+b (h=112, b=0)** |     571.2 | **517.1** | **525.8** |    131.8 |       +4.2 % ⚠ |                 +8.1 % ⚠ |
| y=60      | edge check    | full (h=128, b=0)    |     528.7 |     521.0 |     527.2 |      5.7 |                |                          |
| y=60      | edge check    | top (h=80, b=0)      |     347.3 |     342.7 |     346.2 |      3.2 |        −34.3 % |                          |
| y=60      | edge check    | **t+b (h=80, b=44)** | **158.4** | **150.5** | **153.6** |     24.4 |    **−70.0 %** |              **−54.4 %** |
| y=47 deep | no-op relight | full (h=128, b=0)    |     223.1 |     219.6 |     221.8 |      7.2 |                |                          |
| y=47 deep | no-op relight | top (h=64, b=0)      |     135.2 |     130.7 |     133.4 |      6.0 |        −39.4 % |                          |
| y=47 deep | no-op relight | **t+b (h=64, b=31)** |  **65.8** |  **63.8** |  **64.7** |  **3.4** |    **−70.5 %** |              **−51.3 %** |
| y=47 deep | buried lamp   | full (h=128, b=0)    |     227.8 |     221.8 |     223.5 |     15.2 |                |                          |
| y=47 deep | buried lamp   | top (h=64, b=0)      |     133.9 |     132.7 |     133.7 |      1.0 |        −41.2 % |                          |
| y=47 deep | buried lamp   | **t+b (h=64, b=0)**  | **134.1** | **132.3** | **133.4** |  **2.0** |        −41.1 % |                   +0.2 % |
| y=47 deep | edge check    | full (h=128, b=0)    |     522.9 |     511.6 |     515.9 |     28.7 |                |                          |
| y=47 deep | edge check    | top (h=64, b=0)      |     278.4 |     272.7 |     277.7 |      3.6 |        −46.8 % |                          |
| y=47 deep | edge check    | **t+b (h=64, b=31)** | **140.9** | **138.3** | **140.7** |  **1.6** |    **−73.1 %** |              **−49.4 %** |

`⚠` The two positive-mean cells are editor-Mono noise, **not regressions**, and both sit on legs where the bottom band
did NOT engage (b=0 — the timed job carries a column recalc, so `t+b` and `top` run the *identical* configuration):
lamp BFS y=10 `min` is 386.0 vs top's 388.1 (banded floor marginally *faster*); lamp BFS y=60's +8.1 % mean is entirely
one outlier sample (stddev 131.8 vs 14.6) — its `min` (517.1 vs 514.9) and `median` (525.8 vs 524.9) are equal within
noise.

## Analysis

- **The bottom band's marginal win lands exactly where designed: another −49…−59 % on top of the shipped LI-2
  top band**, on the gather/scan-dominated shapes over terrain with dark buried sections (no-op relight and edge check
  at floors 47/60, b=31/44). Combined with the top band those shapes now run **−70…−73 % vs the pre-LI-2 full-height
  job** — and notably the mid-floor no-op relight (y=60), where LI-2 alone managed only −30.6 % because content sits
  high, recovers to −71.5 % once the dark underside is also skipped. The bottom band **flattens the win across floor
  heights**: the job cost now tracks the *content slab thickness*, not the content's altitude.
- **Recalc-carrying jobs are the designed parity case.** Any queued sunlight column recalc forces `bandMinY = 0`
  (PASS 2 walks to Y=0 unconditionally — there is no downward analog of the top's full-sky escape), so the placement
  edit itself never runs bottom-banded: both lamp legs show b=0 and parity with `top` (+0.2 % on the buried lamp's
  stable distribution). The bottom band's wins accrue on the jobs *around* the edit — the follow-up relight rounds,
  neighbor re-lights, and edge checks, which dominate streaming.
- **Never slower where it cannot engage.** Shallow floors (y=10: the inert run is 0 sections) and recalc jobs show
  marginal deltas of −3.9…+2.8 % — noise-band parity, matching the derivation's guarantee that a non-engaged bottom is
  the identity configuration.
- **Tier-A scaling note:** at 640-high columns the dark underside below player-relevant content is proportionally much
  larger than today's 2–3 sections; together with the top band this makes the lighting job's fixed cost track content
  thickness rather than world height — the §LI-2/§2.2 prerequisite in full.

## Verdict details

**GO under the agreed criterion (bit-identical + suite green + not slower), pending the user's IL2CPP in-game A/B.**
What ships: the bottom band folded into the existing derived band, ON by default behind the same
`World.EnableLightingBandGather` flag (single flag by decision — rollback reverts to full height, not top-only; the
top-only configuration remains reachable in the editor benchmark via the bottom sabotage hook). Pooled steady-state
path only; the TempJob startup sweep stays full-height, unchanged from LI-2.

- **Bit-identical:** B83–B85 (`Validate Lighting Engine` 77/77, `Validate All` 174) — bottom differential with
  engagement assertion, 8-seed deep-floor fuzz, raised-floor prove-red (confirmed red then green). B75–B78 and the C3
  darkening guards B54/B55 remain green.
- **Not slower:** engaged shapes −49…−59 % marginal; non-engaged shapes parity (min/median within noise).
- **In-game smoke (captured):** user-verified 2026-07-11 — generation/rendering correct; underground cave lamps at
  chunk centers and chunk/section borders, break + re-place cycles all light correctly.
- **Frame-level (deferred):** the shippable number is the in-game IL2CPP flag A/B (`EnableLightingBandGather` off vs
  on — note the OFF leg is now *full height*, so the A/B measures LI-2+LI-2b combined; the LI-2b-only frame delta vs
  the shipped top band is expected to be a fraction of LI-2's −26 % settled-streaming frame win, concentrated in
  streaming over terrain with deep dark undersides). If it comes back frame-neutral relative to the already-shipped
  LI-2, the change still ships on the "not slower + Tier-A prerequisite" basis.

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

- **Design / plan:** [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) §LI-2
  (bottom-band follow-up); scaling rationale in
  [`Design/WORLD_SCALING_ANALYSIS.md`](../Design/WORLD_SCALING_ANALYSIS.md) §2.2.
- **Predecessor captures:** [`LIGHTING_LI2_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_2026-07-11_BENCHMARK.md) (top-band
  screening) and [`LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md`](LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md)
  (top-band in-game GO — the frame-level reference the deferred LI-2b A/B extends).
- **Correctness gates:** B79–B82 (bottom derivation + emissive metadata), B83–B85 (bottom differential + engagement +
  prove-red), B71–B78 (top band), C3 B54/B55 — all `Validate Lighting Engine`.
- **Harness:** `Assets/Editor/Benchmarking/LightingBandGatherBenchmark.cs` (3-leg full/top/t+b);
  production derivation in `Assets/Scripts/Helpers/LightingBandDecision.cs` (`DeriveBandMinY`) +
  `WorldJobManager.ScheduleLightingUpdate`; per-section emissive metadata in `ChunkSection.emissiveCount` +
  `Helpers/EmissiveBlockLookup`.
- **Folder conventions:** [`README.md`](README.md).
