# P-4 §3.4 FPS-cap-proportional ceilings — IL2CPP player A/B (scaling off vs on)

| Field           | Value                                                                                                                                                                                                                                                                                                                     |
|-----------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-23 21:51 (local)                                                                                                                                                                                                                                                                                                  |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                                                                                                                      |
| **Commit**      | `40354b75` — the exact tree benchmarked: FPS-cap ceiling scaling (`3bfeac98`) + its `/code-review` round + harness promotion (`40354b75`); build GUID `106f0b55a2d14624837e8b5d453a5d6a` (harness log's Git field is blank in player builds)                                                                              |
| **Captured by** | `P4BackpressureBenchmark` standing harness (F10 in-build, single 5-leg run, report auto-written by `BenchmarkEnvironment`) — **IL2CPP Development Build player, Burst on** (safety checks on), world `Test P4 - 2 IL2CPP`, LoadDistance 23 (47×47 = 2209-chunk square), cap anchors light 22 / mesh 11                    |
| **Verdict**     | **GO (final)** — scaling ON cuts capped-FPS fill latency substantially (30-cap ×1.82: 79.8 s → 43.8 s; 15-cap ×1.32: 117.0 s → 88.6 s) with **no frame-health cost** (worst frame ON ≈ OFF at both caps). A voluntarily capped session (AFK/battery/mobile) is no longer over-throttled. Ship default-ON behind the flag. |

> Measures the `scaleBudgetCeilingsWithFpsCap` refinement of the §3.4 ceilings (see
> `Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §3, "Implemented refinement"). Companion to the
> P-4 budgets-vs-legacy captures
> ([editor](CHUNK_PIPELINE_P4_BACKPRESSURE_2026-07-23_BENCHMARK.md),
> [IL2CPP](CHUNK_PIPELINE_P4_BACKPRESSURE_IL2CPP_2026-07-23_BENCHMARK.md)). Correctness guard at
> capture time: Validate Pipeline Backpressure B7 + Validate All 336/336 across 16 suites.

## What this measures (and what it does NOT)

Same harness and tail-inclusive drain predicate as the IL2CPP budgets capture (one leg = `/teleport`
to fresh terrain; metrics arm at arrival; fill = load square populated AND gen queue + in-flight gen

+ lighting ready-set + in-flight lighting jobs + in-flight mesh jobs + draw queue drained for 30 consecutive frames). **Budgets stay ON in every leg** — the only variable is
  `scaleBudgetCeilingsWithFpsCap`, off vs on, under an imposed FPS cap.

The A/B is the **fill-latency** of the two adjacent legs at each cap. Scaling is a no-op at uncapped FPS (the intended interval is ≤ the 60 FPS anchor → ×1), so the uncapped L1 leg is an anchor, not a comparison.

**NOT measured:** per-pass µs attribution, allocations (B7's zero-alloc note is `INCONCLUSIVE` on Mono; N/A here), editor-Mono anything. **Single run per leg** — fill is a macro-scenario, but the terrain varies per destination (uncontrolled ~±10% noise); the OFF/ON pairs teleport to *different*
ring points, so a few seconds of the per-cap gap is destination noise, not signal.

## Methodology

- Single build, single session, 5 legs back-to-back (F10). Budgets + panic gate ON throughout;
  `scaleBudgetCeilingsWithFpsCap` toggled per leg. FPS caps via `vSyncCount = 0` +
  `Application.targetFrameRate` (−1 / 30 / 15). Salted never-visited ring destinations.
- Base ceilings at the shipping defaults (light 8 / meshSched 6 / genProc 6 / meshApply 4 / draw 2 ms). Scaling legs multiply these by `clamp(60 / targetFps, 1, 8)`: **30-cap → ×2, 15-cap → ×4.**
- `hitches50` counts frames > 50 ms — cap-implied at 15 FPS (every 66 ms frame exceeds 50 ms by construction); at 30 cap a > 50 ms frame is a genuine stall. **worst-frame ms is the primary smoothness signal.**
- Machine: i9-9900K (16 threads), 64 GB, D3D11, Windows 10 19045, Unity 6000.5.4f1.

## Result — IL2CPP player, 2209-chunk square per leg

| Leg | Scaling     | FPS cap  | Fill (s)  | Frames | Avg FPS | Worst frame (ms) | Hitch frames >50 ms | Fill rate (chunks/s) | Gate closes |
|-----|-------------|----------|-----------|--------|---------|------------------|---------------------|----------------------|-------------|
| L1  | OFF (no-op) | uncapped | 11.56     | 1397   | 120.9   | 58.9             | 2                   | 191.2                | 9           |
| L2  | **OFF**     | 30       | 79.79     | 2377   | 29.8    | 179.1            | 4                   | 27.7                 | 5           |
| L3  | **ON**      | 30       | **43.80** | 1296   | 29.6    | 173.0            | 4                   | 50.4                 | 5           |
| L4  | **OFF**     | 15       | 117.00    | 1753   | 15.0    | 173.4            | 1753 (cap-implied)  | 18.9                 | 7           |
| L5  | **ON**      | 15       | **88.65** | 1327   | 15.0    | 175.4            | 1327 (cap-implied)  | 24.9                 | 8           |

**Derived:**

- **30-cap fill gain OFF→ON: ×1.82** (79.8 s → 43.8 s) · worst frame 179.1 → 173.0 ms
- **15-cap fill gain OFF→ON: ×1.32** (117.0 s → 88.6 s) · worst frame 173.4 → 175.4 ms

## Analysis

1. **Fixed ceilings throttle capped FPS worse-than-proportionally — confirmed.** Scaling OFF, a ÷4 FPS drop (uncapped → 30 cap) costs ×6.9 fill (11.56 → 79.79 s), not ×4: the absolute-ms ceilings cut per-second pipeline time linearly with FPS *on top of* the quota, so a voluntarily capped session streams far slower than the FPS reduction alone would explain. This is exactly the deep-cap limitation the parent report flagged.
2. **Scaling ON largely restores proportionality at 30 cap.** ON fills in 43.8 s — close to the FPS-proportional target (≈ 46 s = uncapped × 120.9/29.6) and ×1.82 faster than fixed ceilings. The ×2 ceilings let the 33 ms frame do roughly double the pipeline work it chose to have room for.
3. **No frame-health cost — the load-bearing result.** At both caps the scaling-ON worst frame is within noise of scaling-OFF (30 cap 173.0 vs 179.1 ms; 15 cap 175.4 vs 173.4 ms) and hitch counts are identical. Doubling/quadrupling the ceilings did **not** manufacture new hitches, because the player chose the lower cap: a 30 fps frame has a 33 ms budget that comfortably absorbs the ×2 (16 ms) lighting ceiling. The "no cross-ceiling governor" limitation (the five ×4 ceilings can sum above the 66 ms frame at 15 cap) did not bind in practice — the passes
   don't all saturate the same frame — so avg FPS held exactly at the cap.
4. **Diminishing returns at deeper caps (honest).** The 15-cap gain is ×1.32, smaller than 30-cap's ×1.82: past a point fill is bound by actual compute throughput (worker saturation, the P-3-owned completion merge, draw) rather than the schedule ceilings, and a ×4 quota+ceiling cannot conjure throughput the workers can't sustain. Still a net 28 s saved on a 15 fps session.
5. **Observation — a capped-mode worst-frame spike unrelated to scaling.** Every capped leg carries a
   ~173–179 ms worst frame absent from uncapped (58.9 ms), present **equally** with scaling on and off, so it is not scaling-attributable and does not affect the A/B. Likely the one-time
   `ShiftOrigin` re-anchor triggered by the harness's ~30 000-voxel teleports landing in the just-armed arrival window — a distance no normal streaming session covers in one hop. Flagged as a separate arrival/origin-shift follow-up, not a blocker here.

## Verdict details

**GO (final), ship default-ON behind `scaleBudgetCeilingsWithFpsCap`.** The refinement does exactly what it was designed for: a player who voluntarily lowers their frame cap (AFK, battery, mobile) no longer pays a disproportionate chunk-streaming penalty — 30-cap fill nearly halves and 15-cap fill drops a third, with worst-frame and hitch counts unchanged (the smoothness the cap was chosen for is preserved). The intent-not-`dt` discriminator means an *overloaded* uncapped machine still gets no scaling, so the §3 death-spiral guard is intact. The flag
stays as the rollback/A-B lever and can retire together with the P-4 family after the usual in-game soak. The deep-cap diminishing return and the absent cross-ceiling governor are documented, not hidden; the arrival-region worst-frame spike is a pre-existing artifact filed for separate follow-up.
