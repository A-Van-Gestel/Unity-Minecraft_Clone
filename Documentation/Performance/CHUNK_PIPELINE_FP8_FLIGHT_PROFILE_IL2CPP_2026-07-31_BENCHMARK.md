# FP-8 — Flight-Profile Capture (Pipeline Telemetry), IL2CPP **Release** — five-point view-distance sweep

| Field           | Value                                                                                                                                                                                                                                                                                                     |
|-----------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-31 22:10:00 (vd 5), 22:20:07 (vd 8), 22:15:16 (vd 10), 22:24:20 (vd 15), 22:29:15 (vd 20)                                                                                                                                                                                                          |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                                                                                                      |
| **Commit**      | **`4302b174`** ("Added: FP-7 report integrity banners…", 2026-07-31 21:52) — the last commit before every run. All five runs share build GUID `f4808cbe802345b4bb266e815af77641`, so they are **one build**, produced between 21:52 and 22:10.                                                              |
| **Captured by** | `BenchmarkController` — **IL2CPP *Release* Build, Player, Burst on**. Five runs at **viewDistance 5 / 8 / 10 / 15 / 20**, same build, same machine, one session per run. i9-9900K / 16 threads / 64 GB / D3D11.                                                                                             |
| **Design doc**  | [`Design/FLIGHT_PROFILE_CAPTURE.md`](../Design/FLIGHT_PROFILE_CAPTURE.md) v1.11 — this report is FP-8                                                                                                                                                                                                      |
| **Rule**        | **§7.1 v2** (participation-weighted plurality; ordering axis at waste ≥ 20 % of *admitted* terminal traces, min 30). **Not comparable to FP-4**, which ran §7.1 v1 — see "Relationship to FP-4".                                                                                                            |
| **Verdict**     | **ORDERING-BOUND is a LOW-view-distance phenomenon, not a universal one** — it fires on all three loading speeds at the default vd 5 (33–38 %) and fades to absent by vd 20 (12.7–14.6 %). **ADMISSION-BOUND from vd ≥ 8** and dominant from vd ≥ 10. Never readiness-bound. Throughput-bound once, on a 14-frame phase that should not have produced a verdict at all (see D1). |

> **GO/NO-GO does not apply.** FP ships no behavior change (design §1 non-goals, §9 limitation 6); the
> deliverable is the **regime verdict**.

> **This capture is the first taken in a Release build.** FP-4 used a Development Build. The choice is
> deliberate and load-bearing: the P-4 budgets are frame-time-proportional (`ComputeQuota` scales by
> `unscaledDeltaTime`, `ScaleCeilingMs` by the FPS-cap interval), so a Development Build's overhead lengthens
> frames, inflates quotas, and measures an admission regime no player experiences. The reports now print
> `Configuration: Development | Release` so this can never again be invisible.

---

## Relationship to FP-4 — the correction changed the answer, not just the number

FP-4 concluded: *ordering-bound at every view distance, waste 22.9–61.2 %, worst at high vd, therefore
intrinsic.* **That conclusion does not survive FP-7a**, and the reason is arithmetic rather than
interpretive.

Under §7.1 v1, every chunk that was requested and then unloaded counted as *waste* — including requests the
panic gate **never admitted**, for which no stage ran and no work was discarded. FP-7a split those out as
`AbandonedBeforeAdmission` and removed them from both terms of the fraction. Because the gate closes harder
as view distance rises, the correction is largest exactly where FP-4's signal was strongest.

**Loading pass @ 200 m/s** — the same raw counts, scored both ways:

| vd | abandoned | v1 waste (abandoned counted) | **v2 waste (correct)** |
|----|-----------|------------------------------|------------------------|
| 5  | 1         | 37.8 %                       | **37.8 %**             |
| 8  | 550       | 41.4 %                       | **38.0 %**             |
| 10 | 2 004     | 47.1 %                       | **36.2 %**             |
| 15 | 6 915     | 53.0 %                       | **19.8 %**             |
| 20 | 12 087    | 62.2 %                       | **14.6 %**             |

The v1 column rises monotonically and peaks at 62.2 %, reproducing FP-4's reported 61.2 % high-water mark on
a different build — good corroboration that the two captures agree on the underlying counts. The v2 column
**falls**. At vd 20, 12 087 of 21 666 traces (55.8 %) were requests the pipeline never touched; counting them
as discarded work is what produced FP-4's headline.

**Stated precisely, because the shape differs by speed:** at 200 m/s the trend fully inverts (rising → falling).
At 50 and 100 m/s the v1 curve was U-shaped (33.1 → 24.9 → 23.0 → 21.4 → 27.1 % and 35.3 → 26.8 → 23.6 → 26.9
→ 32.1 %) and the v2 curve is monotonically decreasing. In all three, the high-vd end is the part that moves.

**What survives from FP-4:** ordering-boundness at the **default view distance**, where the gate never closes
and abandonment is therefore negligible (1 abandoned trace across the whole vd-5 200 m/s phase). That was
always the load-bearing half of P-7's justification, and it is confirmed, not weakened.

---

## Methodology

Five runs, one build, `viewDistance` the only variable. Each run: generation pass (10/20/50/100/200 m/s),
a drain/save/unload transition, then loading pass (50/100/200 m/s) over already-generated terrain, 30 s
nominal per phase.

**Cross-view-distance comparison is restricted to the LOADING pass throughout this report.** The generation
pass derives its waypoints from `LoadDistance` (`BuildWaypoints`: `marginChunks = loadDistance`,
`rowStride = loadDistance × 2`), so waypoint counts were **12 / 8 / 6 / 4 / 4** at vd 5/8/10/15/20 and the
final generation phase truncated to 19.7 / 3.2 / 19.8 / 2.2 / 0.7 s. The generation route is therefore not
held constant and its cross-vd numbers are confounded. The loading pass used **12 waypoints at every vd** and
is comparable. See D2.

---

## Raw results (§7.2 — the verdict never replaces these)

### Loading pass — the comparable pass

**50 m/s**

| vd | frames  | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste** | p50 e2e | verdict          |
|----|---------|-------------|---------|-------------|----------|-----------|----------|-----------|---------|------------------|
| 5  | 17 803  | 0.4 %       | 2 178   | 1 323       | 722      | 0         | 133      | **33.1 %**| 1 379 ms| Healthy + ORD    |
| 8  | 20 933  | 1.3 %       | 3 038   | 2 112       | 757      | 0         | 169      | **24.9 %**| 1 388 ms| Healthy + ORD    |
| 10 | 12 129  | 4.4 %       | 3 758   | 2 701       | 808      | 56        | 193      | **21.8 %**| 1 403 ms| Healthy + ORD    |
| 15 | 8 958   | 15.5 %      | 5 628   | 4 144       | 783      | 423       | 278      | **15.0 %**| 1 441 ms| Healthy          |
| 20 | 2 190   | 37.9 %      | 7 059   | 4 828       | 746      | 1 165     | 320      | **12.7 %**| 1 982 ms| Healthy          |

**100 m/s**

| vd | frames  | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste** | p50 e2e | verdict          |
|----|---------|-------------|---------|-------------|----------|-----------|----------|-----------|---------|------------------|
| 5  | 14 308  | 0.0 %       | 3 790   | 2 319       | 1 338    | 0         | 133      | **35.3 %**| 692 ms  | Healthy + ORD    |
| 8  | 16 591  | 0.0 %       | 5 194   | 3 633       | 1 380    | 12        | 169      | **26.6 %**| 700 ms  | Healthy + ORD    |
| 10 | 9 208   | 0.0 %       | 6 161   | 4 375       | 1 433    | 20        | 333      | **23.3 %**| 711 ms  | Healthy + ORD    |
| 15 | 6 651   | 52.0 %      | 8 059   | 5 482       | 1 544    | 626       | 407      | **20.8 %**| 822 ms  | Healthy + ORD    |
| 20 | 1 452   | 77.5 %      | 9 962   | 5 711       | 1 137    | 2 061     | 1 053    | **14.4 %**| 2 207 ms| AdmissionBound   |

**200 m/s**

| vd | frames  | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste** | p50 e2e | verdict              |
|----|---------|-------------|---------|-------------|----------|-----------|----------|-----------|---------|----------------------|
| 5  | 11 038  | 0.0 %       | 7 263   | 4 381       | 2 744    | 1         | 137      | **37.8 %**| 346 ms  | Healthy + ORD        |
| 8  | 9 515   | 54.7 %      | 9 946   | 5 426       | 3 566    | 550       | 404      | **38.0 %**| 421 ms  | AdmissionBound + ORD |
| 10 | 6 773   | 91.3 %      | 11 771  | 5 650       | 3 539    | 2 004     | 578      | **36.2 %**| 946 ms  | AdmissionBound + ORD |
| 15 | 8 885   | 96.9 %      | 16 735  | 6 786       | 1 948    | 6 915     | 1 086    | **19.8 %**| 1 703 ms| AdmissionBound       |
| 20 | 2 148   | 88.6 %      | 21 666  | 6 275       | 1 397    | 12 087    | 1 907    | **14.6 %**| 2 406 ms| AdmissionBound       |

`DiscardedOutOfRange`, `LoadStranded` and `Rerequested` were **0 in all 45 phases**. `LoadStranded` being zero
is expected and no longer meaningful — FP-7d retired the stamp as structurally unreachable.

---

## Findings

### F1 — Ordering-boundness is a low-view-distance problem *(supersedes FP-4's F-ordering)*

At the default vd 5 the ordering axis fires on all three loading speeds (33.1 / 35.3 / 37.8 %), with the
panic gate closed on ≤ 0.4 % of frames — so throttling cannot explain it, and it is intrinsic to how chunks
are ordered. That is the finding P-7 rests on and it is confirmed.

It then **decays with view distance** and is absent at vd 20 (12.7 / 14.4 / 14.6 %). The mechanism is visible
in the same tables: as the gate closes, admissions are withheld, so the pipeline stops starting work it will
throw away. The gate is, crudely and expensively, *solving* the ordering problem by refusing to do the work.

**Consequence for P-7:** still justified, but **scoped to default/low view distances**. Its acceptance target
(`latency ≤ vd × 16 ÷ speed`) should be evaluated there.

### F2 — The panic-gate knee is located between vd 5 and vd 10

Gate closure at 200 m/s: **0.0 % → 54.7 % → 91.3 % → 96.9 % → 88.6 %** at vd 5/8/10/15/20. The vd-8 point
(added for FP-8 precisely to bisect this) lands almost exactly on the transition, establishing it as a **knee,
not a ramp**. The threshold as a share of the resident square runs 88.6 / 48.4 / 35.1 / 18.7 / 11.6 %.

The cost is now measured rather than inferred — chunks the player requested and never received:

| vd | 50 m/s | 100 m/s | 200 m/s |
|----|--------|---------|---------|
| 5  | 0      | 0       | 1       |
| 8  | 0      | 12      | 550     |
| 10 | 56     | 20      | 2 004   |
| 15 | 423    | 626     | 6 915   |
| 20 | 1 165  | 2 061   | **12 087** |

At vd 20 / 200 m/s that is **55.8 % of everything requested, dropped before a single stage ran**. A fixed
256-chunk threshold against a resident square growing as vd² is not a tuning imperfection past vd 10; it is
the dominant behaviour of the pipeline.

**This promotes P-8 above P-7**, reversing FP-4's ranking.

### F3 — The wait is *before* admission, confirming F2 from the latency side

`enqueue→populated` p50, loading @ 200 m/s: **4.4 ms (vd 5) → 21.0 (vd 8) → 436.6 (vd 10) → 1 113.4 (vd 15) →
1 665.5 ms (vd 20)**, with vd 20's *minimum* at 606 ms — i.e. **no chunk at all** was admitted promptly. This
hop is queue-wait plus generation/disk, and generation cost does not vary with view distance, so the growth is
admission delay. Same phenomenon as F2, independently measured.

### F4 — The visibility criterion gives a clean boundary

`latency ≤ viewDistance × 16 ÷ speed`, against p50 end-to-end:

| speed  | vd 5          | vd 8          | vd 10         | vd 15          | vd 20          |
|--------|---------------|---------------|---------------|----------------|----------------|
| 50 m/s | 1 379/1 600 ✅ | 1 388/2 560 ✅ | 1 403/3 200 ✅ | 1 441/4 800 ✅  | 1 982/6 400 ✅  |
| 100 m/s| 692/800 ✅     | 700/1 280 ✅   | 711/1 600 ✅   | 822/2 400 ✅    | 2 207/3 200 ✅  |
| 200 m/s| 346/400 ✅     | 421/640 ✅     | 946/800 ❌     | 1 703/1 200 ❌  | 2 406/1 600 ❌  |

The criterion is met everywhere except 200 m/s at vd ≥ 10 — a falsifiable boundary for P-7/P-8 to target.
Note vd 5 @ 200 m/s passes on p50 (346/400) but **fails on p95** (422 ms), so it is marginal even where it
passes.

### F5 — vd 20 is not viable on this machine, independently of the pipeline

Avg CPU 15.8 ms, avg wall 16.8 ms, min 21.4 FPS, 2.2 GB avg / 2.55 GB peak total memory, GC 133.6 KB/frame
avg. Generation at 50 m/s alone averaged 25.3 ms CPU. **Every vd-20 conclusion above is partly confounded by
frame-rate collapse**, and vd 20 should be treated as a stress point rather than a supported configuration
until that is addressed separately.

---

## Instrument defects this capture exposed

Recorded here in the FP-5/FP-6/FP-7 tradition: the instrument is expected to be wrong in ways only running it
reveals.

### D1 — The min-sample floor guards the ordering axis but **not** the primary regime

FP-7 added `MinOrderingTerminalTraces = 30` so a handful of traces cannot produce an ordering verdict. No
equivalent guard exists on the plurality, and three phases in this capture assert regimes from almost nothing:

| Phase                                   | frames | printed verdict     |
|-----------------------------------------|--------|---------------------|
| vd 20 / Generation / 100 m/s            | **14** | `ThroughputBound`   |
| vd 15 / Generation / 100 m/s            | 148    | `AdmissionBound`    |
| vd 8 / Transition (drain + save + unload)| 333   | `AdmissionBound`    |

The vd-20 case is the clearest: 14 frames, 441 traces all `InFlightAtPhaseEnd`, and `InFlightCap` "winning"
at 50.0 % of eligible pass-frames. That is noise wearing a verdict. The transition case is worse in kind — a
drain-and-unload phase has no meaningful regime at all, yet prints one.

**These three verdicts should be disregarded.** The fix is the direct analogue of the ordering floor.

### D2 — Generation-pass cross-vd comparison is confounded (predicted, now quantified)

Waypoints **12 / 8 / 6 / 4 / 4** and final-phase durations **19.7 / 3.2 / 19.8 / 2.2 / 0.7 s** at vd
5/8/10/15/20, because `BuildWaypoints` derives both margin and row stride from `LoadDistance`. The loading
pass is unaffected (12 waypoints everywhere). Every cross-vd claim in this report is loading-pass only, by
construction.

### D3 — Positive: the integrity guards stayed silent across 45 phases

`4302b174` included FP-7's report-level integrity banners, so this build could have flagged a stale capability
matrix or a double-recorded pass. **Neither fired in any of the 45 phases.** That is production evidence the
§7.1 v2 capability matrix matches what the passes actually emit, and that the participation denominator's
one-report-per-pass-per-frame assumption holds — neither of which the edit-mode suite can establish, since
those hooks live in play-mode paths.

---

## Ranked follow-ups

| # | Item | Change vs FP-4 | Why |
|---|------|----------------|-----|
| **1** | **P-8 — scale panic-gate thresholds with view distance** | **promoted** (was #2) | F2: 0 → 96.9 % closure across vd 5→15, and 12 087 requests dropped before admission at vd 20 / 200 m/s. Correctness-of-intent, and it is what makes vd ≥ 10 admission-bound. |
| **2** | **P-7 — chunk service ordering** | **demoted + re-scoped** (was #1) | F1: confirmed intrinsic at the default vd (33–38 %, gate never closes), but absent by vd 20. Target the low-vd regime; its acceptance criterion is F4's. |
| **3** | **D1 — sample floor on the primary regime** | new | Three verdicts in this capture are unreliable. Cheap, and the ordering-axis analogue already exists. |
| **4** | **D2 — hold generation waypoints constant across a sweep** | was a v1.8 footnote, now quantified | Scale `benchmarkRegionSize` with `LoadDistance`, or restrict generation claims to within-run. |
| **5** | **Per-chunk CSV export** (v3+) | unchanged | The tail is still only visible in aggregate — vd 15 / gen / 50 m/s shows p50 1 023 ms against max 11 273 ms. |

**Not licensed by this capture:** readiness work (`AllDeclined` never dominated any phase) and in-flight/
throughput work (the single `ThroughputBound` verdict is D1 noise).

---

## Document History

* **v1.0** — FP-8 captured and reported (2026-07-31). Five-point sweep, first Release-build capture, first
  under §7.1 v2. Headline: FP-4's "ordering-bound at every view distance" is **superseded** — the correction
  in FP-7a removes never-admitted requests from the waste fraction, and with them the high-view-distance
  signal that drove FP-4's ranking. P-8 promoted over P-7. Two instrument defects filed (D1, D2).

---

**Last Updated:** 2026-07-31
