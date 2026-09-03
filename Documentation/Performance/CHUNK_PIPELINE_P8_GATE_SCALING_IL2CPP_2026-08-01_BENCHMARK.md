# P-8 — Panic-gate thresholds scaled with the resident square, IL2CPP Release — seven-point sweep with same-build controls

| Field           | Value                                                                                                                                                                                                                                                                                                              |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-08-01 19:19–20:47                                                                                                                                                                                                                                                                                             |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                                                                                                               |
| **Commit**      | **`4ea1a38e`** ("Added: P-8 panic-gate thresholds scale with the resident square"), over FP-11a's coverage instrument (`26ec687e`). All ten runs share build GUID `cbb80fcb79164d7ab43a18a8bb28815d` — **one build**                                                                                                 |
| **Captured by** | `BenchmarkController` — **IL2CPP, Configuration: Release, Player, Burst on**. Ten runs: seven scaled (vd 5 / 8 / 10 / 15 / 20 / 26 / 32) and three **same-build unscaled controls** (vd 8 / 26 / 32), **n = 1 per configuration**. i9-9900K / 16 threads / 64 GB / D3D11                                              |
| **Rule**        | **§7.1 v2**, as FP-8 and FP-10. Not comparable to FP-4 (§7.1 v1)                                                                                                                                                                                                                                                    |
| **Verdict**     | **NO-GO.** Scaling the thresholds does what it says at the *gate* — closure falls 42 % → 24 % at vd 8 — but buys **almost no admitted work** (vd 5 → 32 growth **1.58×** against a required ≥ 3.0×; FP-10's unscaled figure was 1.51×), and it **costs frame time**: against its own same-build control, loading-pass minimum FPS falls **−37 % at vd 26** and **−32 % at vd 32**. At high view distance the backlog simply grows to meet the larger threshold, so the gate closes exactly as often as before (94.6 % ON vs 94.5 % OFF at vd 32) while the pipeline carries a deeper queue. **The premise was wrong: admission was not the binding constraint — `Quota` is.** |

> **Design home:** [`Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](../Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md)
> §6 item 6 (P-8), mirrored in [`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md).
> Motivating capture: [FP-10](CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md), which
> measured the mechanism (a fixed 256/128 threshold against a resident square growing as vd²) and ranked P-8
> the top open pipeline item. This capture tests the fix that mechanism implied — and falsifies it.

---

## What this measures (and what it does NOT)

**The production path**, changed at exactly one seam: `World.DrainGenerationRequests` obtains its close/reopen
pair from `GenerationPanicGate.DeriveThresholds(settings.ResidentWidth, …)` before feeding the unchanged
`GenerationPanicGate.Evaluate`. The backlog signal (`LightWorkScheduler.ReadyCount`, sampled after the previous
frame's lighting scan), the 3-frame close debounce, and the admissions-only contract are all untouched.

**The legs** toggle one field, `Settings.scalePanicGateThresholdsWithResidency`:

| vd | load dist | resident square | **scaled (ON)** close/reopen | **unscaled (OFF)** | ON ratio | OFF ratio |
|----|-----------|-----------------|------------------------------|--------------------|----------|-----------|
| 5  | 8  | 17×17 = 289    | **256 / 128**   | 256 / 128 | 88.6 % | 88.6 % |
| 8  | 11 | 23×23 = 529    | **346 / 173**   | 256 / 128 | 65.4 % | 48.4 % |
| 10 | 13 | 27×27 = 729    | **407 / 203**   | 256 / 128 | 55.8 % | 35.1 % |
| 15 | 18 | 37×37 = 1 369  | **557 / 279**   | 256 / 128 | 40.7 % | 18.7 % |
| 20 | 23 | 47×47 = 2 209  | **708 / 354**   | 256 / 128 | 32.1 % | 11.6 % |
| 26 | 29 | 59×59 = 3 481  | **888 / 444**   | 256 / 128 | 25.5 % | 7.4 %  |
| 32 | 35 | 71×71 = 5 041  | **1 069 / 535** | 256 / 128 | 21.2 % | 5.1 %  |

Every one of these was printed by its own run's settings block, so no leg is asserted from configuration alone.
vd 26 was added by the operator mid-capture as an intermediate point, on the grounds that vd 32 is throttled
hard enough to distort a two-point extrapolation — that judgement is vindicated below (§F4).

**Not measured:** ordering (P-7, untouched — waste is *expected* to rise and does); the OM-1 device-calibration
interaction (the capture machine is not calibrated down, so the sweep cannot exercise it); the ensure sweep as
a measurement (`RegimeBearing = false` throughout).

**Guard:** baseline **B19** freezes all seven threshold pairs as literals plus the flag-off byte identity and
the reference-width identity; `Validate All` is **367/367 across 16 suites** at this commit, telemetry both on
and off.

---

## Methodology

Ten runs, one build. Each: generation pass (10/20/50/100/200 m/s, 30 s each, 12 waypoints) → ensure-generated
sweep (non-measurement, 50 m/s over the closed 9 889 m tour = 197.8 s) → transition (drain + save + unload) →
loading pass (50/100/200 m/s, 30 s each). Route geometry derived per FP-9b, so generation waypoints and the
11 400 m timed travel are constant at every view distance.

**Comparability to FP-10 is limited and, at high view distance, broken** — see §F5 and Limitation 2. The
same-build ON/OFF pairs at vd 8 / 26 / 32 are the load-bearing comparison and are free of that problem.

---

## Raw results (§7.2 — the verdict never replaces these)

### Loading pass @ 200 m/s — the criterion phase

**Scaled (ON):**

| vd | frames | gate closed | requested | abandoned | **admitted** | completed | waste | p50 e2e | verdict |
|----|--------|-------------|-----------|-----------|--------------|-----------|-------|---------|---------|
| 5  | 12 509 | 0.0 %  | 7 536  | 1      | **7 535**  | 4 643 | 36.6 % | 354 ms   | Healthy + ORD |
| 8  | 7 459  | 23.6 % | 10 394 | 319    | **10 075** | 5 800 | 39.4 % | 355 ms   | Healthy + ORD |
| 10 | 6 980  | 78.0 % | 12 197 | 1 565  | **10 632** | 5 658 | 41.5 % | 797 ms   | AdmissionBound + ORD |
| 15 | 6 078  | 87.1 % | 16 685 | 5 779  | **10 906** | 6 464 | 30.0 % | 1 539 ms | AdmissionBound + ORD |
| 20 | 5 775  | 94.2 % | 21 233 | 9 509  | **11 724** | 6 466 | 30.2 % | 2 538 ms | AdmissionBound + ORD |
| 26 | 5 619  | 94.6 % | 27 388 | 15 836 | **11 552** | 6 803 | 17.3 % | 3 302 ms | AdmissionBound |
| 32 | 4 996  | 94.6 % | 33 389 | 21 450 | **11 939** | 5 694 | 17.9 % | 3 995 ms | AdmissionBound |

**Unscaled (OFF) — same build:**

| vd | frames | gate closed | requested | abandoned | **admitted** | completed | waste | p50 e2e | verdict |
|----|--------|-------------|-----------|-----------|--------------|-----------|-------|---------|---------|
| 8  | 8 248 | 42.0 % | 10 401 | 503    | **9 898**  | 5 885 | 37.2 % | 377 ms   | Healthy + ORD |
| 26 | 6 556 | 95.7 % | 27 237 | 16 058 | **11 179** | 7 098 | 12.7 % | 3 030 ms | AdmissionBound |
| 32 | 4 817 | 94.5 % | 33 432 | 21 521 | **11 911** | 6 807 | 10.7 % | 3 688 ms | AdmissionBound |

### Loading pass @ 50 and 100 m/s (ON)

| vd | 50 m/s: gate closed / admitted / completed / waste | 100 m/s: gate closed / admitted / completed / waste |
|----|---------------------------------------------------|-----------------------------------------------------|
| 5  | 0.4 % / 2 147 / 1 280 / 34.0 %   | 0.0 % / 3 679 / 2 277 / 34.4 % |
| 8  | 2.0 % / 2 987 / 2 063 / 24.9 %   | 0.0 % / 5 060 / 3 593 / 25.5 % |
| 10 | 1.5 % / 3 544 / 2 576 / 21.5 %   | 0.0 % / 5 940 / 4 435 / 21.9 % |
| 15 | 7.3 % / 5 137 / 3 960 / 17.5 %   | 17.0 % / 7 958 / 5 780 / 23.8 % |
| 20 | 25.7 % / 6 651 / 5 404 / 13.5 %  | 59.8 % / 9 315 / 6 441 / 21.8 % |
| 26 | 91.5 % / 7 969 / 6 252 / 11.7 %  | 81.7 % / 10 179 / 6 741 / 18.0 % |
| 32 | 93.6 % / 8 808 / 6 289 / 7.9 %   | 94.8 % / 9 141 / 4 882 / 13.0 % |

OFF controls: vd 8 — 1.6 % / 3 022 / 2 093 / 24.8 % and 0.0 % / 5 060 / 3 592 / 25.5 %; vd 26 — 93.7 % /
7 849 / 6 520 / 8.6 % and 90.9 % / 9 380 / 6 854 / 10.6 %; vd 32 — 93.1 % / 8 615 / 6 517 / 6.7 % and
92.9 % / 9 745 / 6 587 / 7.2 %.

### Generation pass @ 200 m/s (ON)

| vd | frames | gate closed | requested | abandoned | admitted | completed | waste | verdict |
|----|--------|-------------|-----------|-----------|----------|-----------|-------|---------|
| 5  | 4 401 | 0.0 %  | 6 383  | 0      | 6 383  | 4 091 | 33.4 % | Healthy + ORD |
| 8  | 2 994 | 59.8 % | 8 593  | 45     | 8 548  | 4 014 | 49.8 % | Healthy + ORD |
| 10 | 3 071 | 77.5 % | 10 116 | 1 149  | 8 967  | 4 039 | 47.3 % | AdmissionBound + ORD |
| 15 | 2 568 | 74.1 % | 13 868 | 4 805  | 9 063  | 3 915 | 41.8 % | AdmissionBound + ORD |
| 20 | 2 188 | 71.3 % | 18 198 | 8 726  | 9 472  | 3 782 | 38.5 % | AdmissionBound + ORD |
| 26 | 2 033 | 66.6 % | 23 613 | 13 134 | 10 479 | 3 734 | 30.5 % | AdmissionBound + ORD |
| 32 | 1 172 | 33.2 % | 30 221 | 18 229 | 11 992 | 3 433 | 30.8 % | AdmissionBound + ORD |

OFF controls @ 200 m/s: vd 8 — 83.0 % closed, 8 616 requested, 8 410 admitted, 3 834 completed, 48.9 % waste;
vd 26 — 78.2 %, 23 928, 9 926, 4 688, 19.7 %; vd 32 — 58.9 %, 30 188, 10 958, 4 328, 16.3 %.

### Frame cost — per-pass-group totals

**Scaled (ON):**

| vd | gen avg CPU | gen min FPS | ensure avg CPU | load avg CPU | load min FPS | peak total mem | avg GC/frame |
|----|-------------|-------------|----------------|--------------|--------------|----------------|--------------|
| 5  | 2.4 ms  | 38.4 | 0.8 ms  | 1.3 ms | 90.9 | 916.5 MB   | 19.0 KB  |
| 8  | 4.0 ms  | 23.8 | 1.2 ms  | 2.3 ms | 79.8 | 1 360.0 MB | 35.6 KB  |
| 10 | 5.3 ms  | 24.7 | 1.6 ms  | 2.8 ms | 64.1 | 1 557.0 MB | 47.0 KB  |
| 15 | 9.1 ms  | 20.3 | 2.7 ms  | 4.3 ms | 26.7 | 2 764.1 MB | 88.2 KB  |
| 20 | 11.7 ms | 19.6 | 4.9 ms  | 6.2 ms | 33.7 | 3 472.3 MB | 122.7 KB |
| 26 | 16.1 ms | 19.1 | 10.1 ms | 7.4 ms | 27.9 | 4 517.3 MB | 205.0 KB |
| 32 | 18.6 ms | 18.8 | 13.6 ms | 8.5 ms | 25.4 | 5 317.3 MB | 276.2 KB |

**Unscaled (OFF) — same build:**

| vd | gen avg CPU | gen min FPS | ensure avg CPU | load avg CPU | load min FPS | peak total mem | avg GC/frame |
|----|-------------|-------------|----------------|--------------|--------------|----------------|--------------|
| 8  | 3.5 ms  | 24.9 | 1.1 ms  | 2.1 ms | 85.9 | 1 331.9 MB | 30.4 KB  |
| 26 | 11.9 ms | 23.8 | 8.6 ms  | 6.2 ms | 44.4 | 4 262.7 MB | 147.3 KB |
| 32 | 17.4 ms | 20.4 | 17.3 ms | 8.2 ms | 37.5 | 5 118.3 MB | 237.3 KB |

### Tour coverage (FP-11a)

| vd | 5 | 8 | 10 | 15 | 20 | 26 | 32 |
|----|---|---|----|----|----|----|----|
| **ON** | 100.0 % | 100.0 % | 100.0 % | 100.0 % | 99.7 % | **98.1 %** | 99.6 % |
| **OFF** | — | 100.0 % | — | — | — | **98.6 %** | **98.5 %** |

---

## Findings

### F1 — The gate closes less at low view distance, and not at all less at high view distance

This is the finding that decides the capture. Compare gate closure in the same-build pairs, loading @ 200 m/s:

| vd | threshold ON | threshold OFF | closed ON | closed OFF |
|----|--------------|---------------|-----------|------------|
| 8  | 346 | 256 | **23.6 %** | 42.0 % |
| 26 | 888 | 256 | 94.6 % | 95.7 % |
| 32 | 1 069 | 256 | **94.6 %** | **94.5 %** |

At vd 8 the 1.35× threshold nearly halves closure, exactly as designed. At vd 32 a **4.2× larger threshold
produces a 0.1-point difference** — the backlog simply grows to meet whatever bar it is given, then sits
against it. The gate was never a fixed obstacle at high view distance; it was tracking a queue that the
pipeline cannot drain, and raising the bar just deepens the queue.

### F2 — Admitted work barely moves, and completions fall

Loading @ 200 m/s across vd 5 → 32, scaled: requests grow **4.43×** while admitted work grows **1.58×**
(7 535 → 11 939). FP-10's unscaled figure was 1.51×. The whole intervention is worth **+0.07× of growth**
against a criterion of ≥ 3.0×.

Same-build, at the two view distances where the change is largest:

| vd | admitted ON | admitted OFF | Δ | completed ON | completed OFF | Δ |
|----|-------------|--------------|---|--------------|---------------|---|
| 8  | 10 075 | 9 898  | **+1.8 %** | 5 800 | 5 885 | **−1.4 %** |
| 26 | 11 552 | 11 179 | **+3.3 %** | 6 803 | 7 098 | **−4.2 %** |
| 32 | 11 939 | 11 911 | **+0.2 %** | 5 694 | 6 807 | **−16.4 %** |

**More admission, fewer deliveries.** At vd 32 the scaled leg admitted 28 more chunks and finished 1 113
fewer. Never-admitted requests at vd 32 fell only 64.4 % → 64.2 %, against a criterion of ≤ 35 %.

### F3 — The binding constraint is `Quota`, not the gate

The stop-reason tallies say so directly. Loading @ 200 m/s, vd 32: `LightSchedule` reports `Quota` on
**4 961 of 4 996 frames (99.3 %)** scaled, and 4 793 of 4 817 (99.5 %) unscaled. `MeshSchedule` is likewise
quota-bound in both. `InFlightCap` never dominates any phase in any of the ten runs; `AllDeclined` never does
either.

That reframes FP-10's F2 result. FP-10 observed that completion-*of-admitted* had no trend with view distance
(53–68 %) and read it as "the pipeline's efficiency on accepted work is view-distance-invariant — only its
willingness to accept varies." The second half is now falsified: willingness was **downstream** of the same
throughput ceiling. The gate was not choosing to refuse work; it was reporting that lighting could not keep up.

### F4 — Frame-time cost, and why vd 26 earned its place in the sweep

Against its own same-build control:

| vd | gen avg CPU | load avg CPU | gen min FPS | **load min FPS** | peak mem |
|----|-------------|--------------|-------------|------------------|----------|
| 8  | +14 % | +9.5 % | −4.4 % | **−7.1 %** | +2.1 % |
| 26 | +35 % | +19 % | −20 % | **−37.2 %** | +6.0 % |
| 32 | +7 % | +3.7 % | −7.8 % | **−32.3 %** | +3.9 % |

Loading-pass minimum FPS — the worst frame a player actually feels — falls by roughly a third at both high
view distances. This is precisely the trade FP-10 F4 warned about: the gate had been protecting frame time,
and loosening it hands that back for no delivered work.

**The operator's mid-capture addition of vd 26 changed the reading.** From vd 20 → 32 alone the cost looks
like it flattens (min FPS −32 % at 32 is close to what a two-point line through 20 and 32 predicts), but vd 26
is the *worst* point in the sweep on every frame-cost axis (−37 % min FPS, +35 % generation CPU) and the peak
of the effect sits between the two points FP-10 sampled. A 20/32-only sweep would have understated the
regression.

### F5 — The FP-10 cross-build comparison is sound at vd 8 and unsound at vd 32 (criterion G6)

The unscaled controls exist to test whether this build reproduces FP-10. Per-pass-group avg CPU:

| vd | leg | gen: this build / FP-10 | load: this build / FP-10 |
|----|-----|--------------------------|---------------------------|
| 8  | OFF | 3.5 / 3.3 = **×1.06** | 2.1 / 2.1 = **×1.00** |
| 32 | OFF | 17.4 / 11.6 = **×1.50** | 8.2 / 5.2 = **×1.58** |

vd 8 reproduces FP-10 inside G2's ±10 % band; **vd 32 does not**. The most likely cause is FP-11a itself: this
build's ensure sweep flies a closed circuit and reaches 98.5–99.6 % tour coverage at vd 32, where FP-10's ran
92.3 % gate-throttled with coverage unmeasured. A loading pass over more fully generated terrain does more
work. That is a *correction* to the instrument, not a regression — but it means **FP-10's high-view-distance
rows are not a valid baseline for this build**, and every high-vd conclusion here rests on the same-build
controls instead. This is the case G6 was written for, and it fired.

### F6 — Waste rises, as predicted, and is not the problem

Loading @ 200 m/s waste, ON vs OFF: 39.4 % vs 37.2 % (vd 8), 17.3 % vs 12.7 % (vd 26), 17.9 % vs 10.7 %
(vd 32). Admitting more work into a pipeline that cannot drain it produces more discarded work — the outcome
criterion **G5** pre-committed to treating as expected rather than as failure. It is recorded and not held
against the change.

---

## Verdict against the pre-committed criteria

| # | Criterion | Threshold | Result |
|---|-----------|-----------|--------|
| **G1** | Admitted work scales with residency | vd 5 → 32 admitted growth ≥ **3.0×**; never-admitted at vd 32 ≤ **35 %** | ❌ **FAIL** — 1.58× (FP-10: 1.51×); never-admitted 64.2 % (FP-10: 66.2 %) |
| **G2** | No frame-time regression | avg CPU ≤ ×1.10, min FPS ≥ ×0.90 | ❌ **FAIL** vs same-build controls — loading min FPS ×0.63 (vd 26), ×0.68 (vd 32); generation avg CPU ×1.35 (vd 26) |
| **G3** | Memory holds | peak ≤ ×1.10 | ✅ **PASS** — +2.1 / +6.0 / +3.9 % vs controls |
| **G4** | Loading pass measured loading | coverage ≥ 99 % | ⚠️ **MARGINAL** — 99.6–100 % at every scaled point except **vd 26 (98.1 %)**; controls 98.5 % (vd 32) and 98.6 % (vd 26). Shortfalls are 1–2 pt and identical in both legs, so they do not explain any ON/OFF delta |
| **G5** | Rising waste is not a failure | — | ✅ Recorded, not charged (F6) |
| **G6** | Controls valid | reproduce FP-10 within G2's band | ❌ **FAIL at vd 32** (×1.50/×1.58), pass at vd 8. FP-10 comparison abandoned for high vd; same-build pairs used instead (F5) |

**NO-GO.** G1 fails by a wide margin and G2 fails against the cleanest available comparison. Neither is
recoverable by re-tuning the scale: F1 shows the backlog expanding to fill whatever threshold it is given, so a
larger constant would deepen the queue further without opening the gate.

---

## Verdict details — what ships, what does not, and where the idea goes

**Decided: the code stays, the flag flips to default-OFF.** `scalePanicGateThresholdsWithResidency = false`
ships, so the engine's behaviour is byte-identical to pre-P-8 at every view distance. The derivation
(`GenerationPanicGate.DeriveThresholds`), its guard (baseline **B19**), and the benchmark-overlay entry that
lets a capture switch legs are all retained — a full revert would throw away the guarded arithmetic and the
ability to re-test cheaply, which is the one thing this result says will be worth doing later.

Consequence for the flag census: this is **not** a rollback flag awaiting retirement. It is an opt-in
experimental path whose default is the legacy behaviour, so it must not be swept up in the P-4 family's
retirement pass — deleting it would delete the retained fix, not a dead legacy leg.

**The salvageable part.** The vd-8 result is real and is the one place the intervention behaved as designed:
closure 42 % → 24 % with +1.8 % admitted, at a −7.1 % min-FPS cost. If a future change raises the *throughput*
ceiling — the `Quota` that F3 identifies — the gate will become the binding constraint again, and at that point
a residency-scaled threshold is the right shape. **P-8 is not wrong; it is premature.** It should be re-tested
after, not before, the lighting/mesh schedule quota is addressed.

**What this redirects to.** The evidence points at the lighting schedule quota (`maxLightJobsPerFrame`, 24 on
this machine after OM-1 calibration) and the mesh schedule quota (11), which are `Quota`-saturated on
99 %+ of frames at high view distance in **both** legs. That is a throughput item, and FP-4 previously
deprioritised throughput work on the grounds that it was not binding — a conclusion this capture overturns for
the high-view-distance regime specifically. The correct next question is not "how do we admit more?" but
"why can the pipeline only finish ~6 500 chunks per 30 s phase regardless of what it admits?" — completions sit
in a 5 658–6 803 band across vd 10 → 32 in *both* legs, a ceiling far more stable than anything the gate does.

**What P-7 gains.** Nothing changes for ordering: waste rose as predicted (F6), confirming the FP-10
blockquote's reasoning that the gate was suppressing ordering waste by refusing work. P-7 remains scoped to
low view distance.

---

## Limitations

1. **n = 1 per configuration.** Deltas under ~10 % are not separable from run-to-run variance. The findings
   this report rests on (−32/−37 % min FPS; 1.58× vs 3.0×; 0.1-pt gate-closure difference at vd 32) are all
   far outside that.
2. **FP-10 is not a valid baseline at high view distance for this build** (F5). Established by the controls
   rather than assumed — but it does mean the sweep's absolute numbers cannot be laid beside FP-10's.
3. **Run-level averages are not comparable to FP-10** regardless: the ensure phase is ~5.5 % longer here.
4. **vd 26 has no FP-10 baseline** and is scored purely against its own same-build control.
5. **Tour coverage falls 1–2 pt short of 99 % at three points** (G4), all at high view distance and in both
   legs. Small and symmetric, so it cannot manufacture an ON/OFF delta, but the affected loading rows carry a
   little generation work.
6. **The OM-1 calibration interaction is untested** — the capture machine is not calibrated down, so the
   scenario where a scaled-up threshold meets scaled-down throughput caps never occurred here. Given F3, it is
   the configuration where this change would do the most harm.
7. **The trace buffer saturated** on the vd 26 and vd 32 ensure phases (both legs), so those blocks' latency
   percentiles cover a subset. Disposition counts and stop-reason tallies remain exact, and the ensure phase
   carries no verdict.
8. Player builds do not record their commit; the header's commit comes from build timing and the shared
   build GUID, as in FP-10.

---

## Document History

* **v1.0** — P-8 captured and reported (2026-08-01). Ten runs, one build, seven scaled view distances plus
  three same-build unscaled controls. **NO-GO**: the scaled thresholds buy 1.58× admitted growth against a
  required 3.0× while costing ~⅓ of loading-pass minimum FPS at vd 26/32, because the backlog grows to meet
  whatever threshold it is given (94.6 % vs 94.5 % gate closure at vd 32 across a 4.2× threshold difference).
  The capture reframes FP-10 F2: admission was downstream of a `Quota` throughput ceiling, not an independent
  choice. Also establishes, via the controls, that FP-10's high-view-distance rows are not a valid baseline for
  the FP-11a build.

---

**Last Updated:** 2026-08-01
