# FP-10 — Flight-Profile Capture (Pipeline Telemetry), IL2CPP **Release** — six-point view-distance sweep, first capture on a derived route

| Field           | Value                                                                                                                                                                                                                                                                                                              |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-08-01 09:59:41 (vd 5), 10:07:41 (vd 8), 10:17:04 (vd 10), 10:25:10 (vd 15), 10:33:39 (vd 20), 10:45:50 (vd 32)                                                                                                                                                                                                 |
| **Branch**      | `feat/world-scaling`                                                                                                                                                                                                                                                                                               |
| **Commit**      | **`5284461d`** ("Added: FP-9b derived benchmark route geometry…", 2026-08-01 01:15) — the last commit before every run. All six runs share build GUID `7f09745ecc1949c6a5d5fbe42aaeb6fb`, so they are **one build**.                                                                                                 |
| **Captured by** | `BenchmarkController` — **IL2CPP *Release* Build, Player, Burst on**. Six runs at **viewDistance 5 / 8 / 10 / 15 / 20 / 32**, same build, same machine, one session per run, **n = 1 per view distance**. i9-9900K / 16 threads / 64 GB / D3D11.                                                                     |
| **Design doc**  | [`Design/FLIGHT_PROFILE_CAPTURE.md`](../Design/FLIGHT_PROFILE_CAPTURE.md) v1.14 — this report is FP-10                                                                                                                                                                                                              |
| **Rule**        | **§7.1 v2** (participation-weighted plurality; ordering axis at waste ≥ 20 % of *admitted* terminal traces, min 30; primary regime needs ≥ 1 000 eligible observations and a `RegimeBearing` phase). Same rule as FP-8. **Not comparable to FP-4** (§7.1 v1).                                                        |
| **Verdict**     | **FP-8's inverted conclusion REPRODUCES across a total route rework** — ordering-boundness decays with view distance (38.5 / 43.2 / 36.6 / 19.5 / 13.7 / 8.6 % at vd 5–32, loading @ 200 m/s), within ~1 pt of FP-8 at four of five overlapping points. **The panic gate clamps admitted work across the whole sweep: it grows only 1.5–1.7× from vd 5 to vd 32 while requests grow 4.5–4.8×.** **P-8 confirmed at #1**, now with a measured mechanism rather than an inferred one. |

> **GO/NO-GO does not apply.** FP ships no behavior change (design §1 non-goals, §9 limitation 6); the
> deliverable is the **regime verdict**.

> **First capture in which the generation pass is cross-view-distance comparable.** FP-9b inverted the route
> geometry — the region is now *derived* from `Σ(speed × phaseSeconds)` rather than the route being derived
> from a fixed region — so **generation waypoints are 12 and timed travel is 11 400 m at every view distance**,
> and every speed phase ran its full 30 s. FP-8's D2 confound is gone, and the generation tables below are
> reported on equal footing with the loading tables for the first time.

---

## Relationship to FP-8 — a reproduction, not a continuation

**FP-8 is a terminal baseline and this is not a continuation of the same series.** The route changed
completely: FP-8's regions were a fixed 64 chunks with waypoint counts that varied 12 / 8 / 6 / 4 / 4 by view
distance and final phases that truncated to as little as 0.7 s; FP-10's regions are derived (134–204 chunks),
waypoints are constant, and every phase ran its full duration. Absolute numbers are therefore **not** a
like-for-like extension of FP-8's rows.

What *can* be compared is the shape of the curve, and it survives:

**Loading pass @ 200 m/s, waste as % of admitted terminal traces**

| vd | 5    | 8        | 10   | 15   | 20   | 32  |
|----|------|----------|------|------|------|-----|
| **FP-8**  | 37.8 | 38.0 | 36.2 | 19.8 | 14.6 | —   |
| **FP-10** | 38.5 | **43.2** | 36.6 | 19.5 | 13.7 | 8.6 |

Four of the five overlapping points agree within ~1 pt despite the route being rebuilt underneath them. The
vd-8 point is the one that moved (+5.2 pt) and, at n = 1 per configuration, a single 5-pt shift is not
separable from run-to-run variance.

**The inference this licenses:** the waste fraction is a property of *the pipeline and the view distance*, not
of the route the benchmark happens to fly. That was an assumption in FP-8 and is now evidence — which matters,
because the ordering axis is what P-7 rests on.

---

## Methodology

Six runs, one build, `viewDistance` the only variable. Each run:

1. **Generation pass** — 10 / 20 / 50 / 100 / 200 m/s over virgin terrain, 30 s per phase, 12 waypoints.
2. **Ensure-generated sweep** — a non-measurement phase (`RegimeBearing = false`) traversing the 64-chunk
   loading tour at a fixed 50 m/s, so terrain missed at the higher generation speeds is filled in *here*
   rather than polluting the loading pass. Ran **187.5 s in all six runs**, which confirms the tour was never
   shrunk below its nominal 64 chunks at any view distance.
3. **Transition** — drain + save + unload.
4. **Loading pass** — 50 / 100 / 200 m/s over already-generated terrain, 30 s per phase, 12 waypoints.

Derived route per run (all printed by the report, none configurable):

| vd | load dist | resident   | region (chunks) | rows | row stride | route length | **timed travel** |
|----|-----------|------------|-----------------|------|------------|--------------|------------------|
| 5  | 8         | 17×17 = 289    | 134 | 6 | 16 | 12 608 m | **11 400 m** |
| 8  | 11        | 23×23 = 529    | 135 | 6 | 22 | 12 608 m | **11 400 m** |
| 10 | 13        | 27×27 = 729    | 135 | 6 | 26 | 12 544 m | **11 400 m** |
| 15 | 18        | 37×37 = 1 369  | 137 | 6 | 36 | 12 576 m | **11 400 m** |
| 20 | 23        | 47×47 = 2 209  | 156 | 6 | 46 | 14 240 m | **11 400 m** |
| 32 | 35        | 71×71 = 5 041  | 204 | 6 | 70 | 18 464 m | **11 400 m** |

Timed travel — the distance the speed phases actually consume — is identical everywhere, which is the FP-9b
guarantee. Route length grows at vd 20 and 32 only because the fixed 64-chunk tour forces a minimum row width
of `64 + 2 × loadDistance`; that extra length sits outside the timed window.

**Negative chunk coordinates were exercised for the first time.** Regions of 134–204 chunks centred near
chunk 50 place the sweep well into negative chunk space. No errors, no coordinate anomalies, and internally
coherent verdicts at every point — the sign-safe floor-division from the world-scaling track holds under the
benchmark.

---

## Raw results (§7.2 — the verdict never replaces these)

`Rerequested` and `LoadStranded` were **0 in all 60 phases**. `DiscardedOutOfRange` was 0 in all but one
(vd 8 / generation / 200 m/s: 3).

### Generation pass — comparable across view distance for the first time

**10 m/s** — waste was **0 at every view distance**; no ordering signal exists at this speed.

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste** | p50 e2e  | verdict |
|----|--------|-------------|---------|-------------|----------|-----------|----------|-----------|----------|---------|
| 5  | 39 666 | 0.0 %       | 286     | 151         | 0        | 0         | 135      | **0.0 %** | 4 833 ms | Healthy |
| 8  | 20 284 | 0.0 %       | 378     | 225         | 0        | 0         | 153      | **0.0 %** | 4 837 ms | Healthy |
| 10 | 19 158 | 0.0 %       | 438     | 273         | 0        | 0         | 165      | **0.0 %** | 4 832 ms | Healthy |
| 15 | 14 285 | 0.0 %       | 666     | 465         | 0        | 0         | 201      | **0.0 %** | 4 847 ms | Healthy |
| 20 | 9 304  | 0.0 %       | 846     | 615         | 0        | 0         | 231      | **0.0 %** | 4 873 ms | Healthy |
| 32 | 3 791  | 0.7 %       | 1 278   | 975         | 0        | 0         | 303      | **0.0 %** | 4 922 ms | Healthy |

**20 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|---------|
| 5  | 40 997 | 0.0 %       | 646     | 385         | 110      | 0         | 151      | **17.0 %** | 2 415 ms | Healthy |
| 8  | 25 746 | 0.0 %       | 874     | 595         | 74       | 0         | 205      | **8.5 %**  | 2 427 ms | Healthy |
| 10 | 22 316 | 0.0 %       | 1 026   | 735         | 50       | 0         | 241      | **4.9 %**  | 2 428 ms | Healthy |
| 15 | 11 802 | 0.0 %       | 1 406   | 1 085       | 0        | 0         | 321      | **0.0 %**  | 2 450 ms | Healthy |
| 20 | 8 610  | 0.0 %       | 1 786   | 1 435       | 0        | 0         | 351      | **0.0 %**  | 2 465 ms | Healthy |
| 32 | 2 796  | 0.8 %       | 2 698   | 2 210       | 0        | 0         | 488      | **0.0 %**  | 2 525 ms | Healthy |

**50 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict          |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|------------------|
| 5  | 30 027 | 0.0 %       | 1 612   | 1 000       | 443      | 0         | 169      | **27.5 %** | 986 ms   | Healthy + ORD    |
| 8  | 16 106 | 0.0 %       | 2 159   | 1 546       | 420      | 0         | 193      | **19.5 %** | 996 ms   | Healthy          |
| 10 | 14 219 | 0.0 %       | 2 535   | 1 910       | 408      | 0         | 217      | **16.1 %** | 997 ms   | Healthy          |
| 15 | 6 174  | 0.0 %       | 3 475   | 2 820       | 384      | 0         | 271      | **11.1 %** | 1 029 ms | Healthy          |
| 20 | 3 285  | 0.0 %       | 4 371   | 3 630       | 276      | 0         | 465      | **6.3 %**  | 1 113 ms | Healthy          |
| 32 | 2 252  | **81.9 %**  | 6 674   | 3 738       | 176      | 563       | 2 197    | **2.9 %**  | 4 687 ms | AdmissionBound   |

**100 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict          |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|------------------|
| 5  | 19 163 | 0.0 %       | 3 159   | 2 013       | 995      | 0         | 151      | **31.5 %** | 506 ms   | Healthy + ORD    |
| 8  | 9 157  | 0.0 %       | 4 298   | 3 128       | 965      | 0         | 205      | **22.5 %** | 515 ms   | Healthy + ORD    |
| 10 | 6 011  | 0.0 %       | 5 046   | 3 863       | 941      | 0         | 242      | **18.6 %** | 522 ms   | Healthy          |
| 15 | 5 145  | 86.1 %      | 6 916   | 4 577       | 1 041    | 484       | 814      | **16.2 %** | 2 097 ms | AdmissionBound   |
| 20 | 4 914  | 90.8 %      | 8 831   | 4 398       | 1 032    | 1 917     | 1 484    | **14.9 %** | 3 100 ms | AdmissionBound   |
| 32 | 4 462  | 90.7 %      | 14 810  | 3 548       | 881      | 6 094     | 4 287    | **10.1 %** | 5 831 ms | AdmissionBound   |

**200 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict              |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|----------------------|
| 5  | 5 420  | 0.0 %       | 6 383   | 3 965       | 2 266    | 0         | 152      | **35.5 %** | 280 ms   | Healthy + ORD        |
| 8  | 4 993  | 87.7 %      | 8 616   | 3 526       | 4 119    | 502       | 466      | **50.8 %** | 1 079 ms | Healthy + ORD        |
| 10 | 5 288  | 85.3 %      | 10 118  | 3 511       | 3 962    | 2 017     | 628      | **48.9 %** | 1 349 ms | Healthy + ORD        |
| 15 | 4 569  | 86.3 %      | 14 261  | 3 869       | 2 865    | 6 142     | 1 385    | **35.3 %** | 2 005 ms | AdmissionBound + ORD |
| 20 | 4 529  | 86.6 %      | 18 484  | 4 065       | 2 455    | 9 807     | 2 157    | **28.3 %** | 2 631 ms | AdmissionBound + ORD |
| 32 | 2 839  | 72.1 %      | 30 403  | 4 396       | 1 788    | 19 337    | 4 882    | **16.2 %** | 3 587 ms | AdmissionBound       |

### Loading pass

**50 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict          |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|------------------|
| 5  | 31 278 | 0.5 %       | 2 148   | 1 280       | 730      | 0         | 138      | **34.0 %** | 1 378 ms | Healthy + ORD    |
| 8  | 21 956 | 1.4 %       | 3 079   | 2 096       | 774      | 30        | 179      | **25.4 %** | 1 374 ms | Healthy + ORD    |
| 10 | 15 316 | 4.8 %       | 3 795   | 2 661       | 838      | 89        | 207      | **22.6 %** | 1 377 ms | Healthy + ORD    |
| 15 | 10 053 | 18.9 %      | 5 693   | 4 128       | 792      | 496       | 277      | **15.2 %** | 1 391 ms | Healthy          |
| 20 | 7 209  | 54.1 %      | 7 715   | 5 370       | 805      | 1 192     | 348      | **12.3 %** | 1 561 ms | Healthy          |
| 32 | 5 764  | 96.1 %      | 13 614  | 6 039       | 486      | 5 248     | 1 841    | **5.8 %**  | 6 257 ms | AdmissionBound   |

**100 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict          |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|------------------|
| 5  | 27 756 | 0.0 %       | 3 679   | 2 277       | 1 265    | 0         | 137      | **34.4 %** | 692 ms   | Healthy + ORD    |
| 8  | 13 641 | 0.0 %       | 5 060   | 3 587       | 1 294    | 0         | 179      | **25.6 %** | 689 ms   | Healthy + ORD    |
| 10 | 10 955 | 0.0 %       | 5 937   | 4 432       | 1 298    | 0         | 207      | **21.9 %** | 693 ms   | Healthy + ORD    |
| 15 | 8 343  | 53.9 %      | 8 125   | 5 665       | 1 478    | 587       | 395      | **19.6 %** | 874 ms   | Healthy          |
| 20 | 6 990  | 89.1 %      | 10 238  | 6 343       | 910      | 2 233     | 752      | **11.4 %** | 2 097 ms | AdmissionBound   |
| 32 | 5 260  | 95.8 %      | 16 447  | 6 022       | 558      | 7 307     | 2 560    | **6.1 %**  | 4 490 ms | AdmissionBound   |

**200 m/s**

| vd | frames | gate closed | started | MeshApplied | Unloaded | Abandoned | InFlight | **waste**  | p50 e2e  | verdict              |
|----|--------|-------------|---------|-------------|----------|-----------|----------|------------|----------|----------------------|
| 5  | 15 754 | 0.3 %       | 7 533   | 4 481       | 2 897    | 1         | 154      | **38.5 %** | 354 ms   | Healthy + ORD        |
| 8  | 9 809  | 72.8 %      | 10 396  | 4 969       | 4 051    | 1 027     | 349      | **43.2 %** | 593 ms   | AdmissionBound + ORD |
| 10 | 9 830  | 90.9 %      | 12 209  | 5 513       | 3 496    | 2 649     | 551      | **36.6 %** | 1 033 ms | AdmissionBound + ORD |
| 15 | 9 823  | 97.3 %      | 16 699  | 6 244       | 1 782    | 7 561     | 1 112    | **19.5 %** | 1 802 ms | AdmissionBound       |
| 20 | 9 091  | 97.2 %      | 21 311  | 6 490       | 1 314    | 11 737    | 1 770    | **13.7 %** | 2 561 ms | AdmissionBound       |
| 32 | 7 061  | 96.5 %      | 33 662  | 6 432       | 979      | 22 280    | 3 971    | **8.6 %**  | 3 903 ms | AdmissionBound       |

### Frame cost

Per-pass group totals. **Not comparable to FP-8's** run-level averages: FP-10's phase mix includes the 187.5 s
ensure-generated sweep, which is cheaper than the timed phases and drags every run-level mean down.

| vd | gen avg CPU | gen min FPS | ensure avg CPU | load avg CPU | load min FPS | peak total mem | avg GC/frame |
|----|-------------|-------------|----------------|--------------|--------------|----------------|--------------|
| 5  | 1.7 ms      | 44.6        | 0.6 ms         | 1.0 ms       | 167.5        | 920.9 MB       | 14.6 KB      |
| 8  | 3.3 ms      | 25.1        | 1.1 ms         | 2.1 ms       | 44.2         | 1 345.8 MB     | 26.0 KB      |
| 10 | 3.5 ms      | 31.5        | 1.4 ms         | 2.2 ms       | 61.7         | 1 549.3 MB     | 30.6 KB      |
| 15 | 5.4 ms      | 23.4        | 2.4 ms         | 3.0 ms       | 68.4         | 2 638.3 MB     | 48.0 KB      |
| 20 | 7.1 ms      | 24.7        | 4.2 ms         | 3.8 ms       | 59.1         | 3 263.5 MB     | 74.5 KB      |
| 32 | 11.6 ms     | 22.7        | 9.8 ms         | 5.2 ms       | 59.1         | **4 965.0 MB** | 154.5 KB     |

---

## Findings

### F1 — FP-8's inverted verdict reproduces across a route rework *(confirms FP-8 F1)*

Ordering-boundness decays with view distance on the loading pass (38.5 / 43.2 / 36.6 / 19.5 / 13.7 / 8.6 % at
200 m/s), matching FP-8 to within ~1 pt at four of five overlapping points. The generation pass — comparable
for the first time — shows the same decay at 50 and 100 m/s (27.5 → 2.9 % and 31.5 → 10.1 %).

**The 200 m/s generation row is the exception and it is informative:** waste *rises* from 35.5 % at vd 5 to a
sweep-wide maximum of **50.8 % at vd 8**, holds at 48.9 % at vd 10, then decays. The peak sits exactly where
the gate has just started closing (0 % → 87.7 % between vd 5 and 8) but has not yet had time to suppress
admissions — the pipeline is admitting at full rate *and* discarding at full rate. This is the worst-case
regime in the entire sweep, and it is at a view distance close to the default.

**Consequence for P-7:** unchanged from FP-8 — justified, scoped to low view distance, and now with the
worst case located at vd 8 / 200 m/s rather than at vd 5.

### F2 — The gate clamps admitted work to a near-constant, independent of view distance

This is the sweep's cleanest result. Admitted = started − abandoned:

**Loading @ 200 m/s**

| vd | requested | abandoned pre-admission | **admitted** | completed | completed / admitted |
|----|-----------|-------------------------|--------------|-----------|----------------------|
| 5  | 7 533     | 1                       | **7 532**    | 4 481     | 59.5 %               |
| 8  | 10 396    | 1 027                   | **9 369**    | 4 969     | 53.0 %               |
| 10 | 12 209    | 2 649                   | **9 560**    | 5 513     | 57.7 %               |
| 15 | 16 699    | 7 561                   | **9 138**    | 6 244     | 68.3 %               |
| 20 | 21 311    | 11 737                  | **9 574**    | 6 490     | 67.8 %               |
| 32 | 33 662    | 22 280                  | **11 382**   | 6 432     | 56.5 %               |

**Generation @ 200 m/s** — the same clamp, tighter:

| vd | requested | abandoned | **admitted** | completed |
|----|-----------|-----------|--------------|-----------|
| 5  | 6 383     | 0         | **6 383**    | 3 965     |
| 8  | 8 616     | 502       | **8 114**    | 3 526     |
| 10 | 10 118    | 2 017     | **8 101**    | 3 511     |
| 15 | 14 261    | 6 142     | **8 119**    | 3 869     |
| 20 | 18 484    | 9 807     | **8 677**    | 4 065     |
| 32 | 30 403    | 19 337    | **11 066**   | 4 396     |

Across vd 5 → 32, **requests grow 4.47× (loading) / 4.76× (generation) while admitted work grows only 1.51× /
1.73×** — and from vd 8 up it is flatter still, spanning 9 138–11 382 (loading) and 8 101–11 066 (generation).
Completion-of-admitted has no trend at all (53–68 %). **The
pipeline's efficiency on the work it accepts is view-distance-invariant — what changes is how much it
refuses.** By vd 32 / loading / 200 m/s, **66.2 % of all requests never ran a single stage**.

This also reframes the low waste numbers at high view distance: 8.6 % waste at vd 32 is 8.6 % *of the 34 %
that got in*. The ordering axis is measuring a shrinking subpopulation as view distance rises. FP-9a's
exclusion of `AbandonedBeforeAdmission` is what makes that visible rather than hidden — but a reader must
carry the denominator, not just the percentage.

### F3 — The mechanism: an absolute threshold against a quadratic resident square

The panic gate closes on a fixed **256 backlogged chunks** (reopens at 128) while residency grows as
`(2 × loadDistance + 1)²`. The report prints the resulting ratio, and it collapses:

| vd | resident | close threshold as share of resident | gate closed, loading @ 200 m/s |
|----|----------|--------------------------------------|--------------------------------|
| 5  | 289      | **88.6 %**                           | 0.3 %                          |
| 8  | 529      | 48.4 %                               | 72.8 %                         |
| 10 | 729      | 35.1 %                               | 90.9 %                         |
| 15 | 1 369    | 18.7 %                               | 97.3 %                         |
| 20 | 2 209    | 11.6 %                               | 97.2 %                         |
| 32 | 5 041    | **5.1 %**                            | 96.5 %                         |

At vd 5 the gate needs 89 % of the resident world backlogged to trip. At vd 32 it needs 5 %. **From vd 15 up
the gate is essentially never open**, so the pipeline never runs in the regime its budgets were tuned for.

The knee is again between vd 5 and vd 8, reproducing FP-8 F2 (which read 0 / 54.7 / 91.3 / 96.9 / 88.6 %).

**This is a design question, not an unambiguous defect.** An absolute backlog cap bounds latency and memory in
the units that actually matter, and there is a real argument for it. But it sits oddly against the rest of the
P-4 family, where quotas are frame-time-proportional (`ComputeQuota` × `unscaledDeltaTime`) and the ceiling
refinement made FPS-cap scaling explicit. **P-8 stays at #1**, and this capture upgrades its justification
from "the gate closes a lot at high vd" to a specific arithmetic cause with a specific fix shape: scale
close/reopen with resident count (or vd²) and re-sweep — cheap to evaluate now that the route is held constant.

### F4 — The gate is succeeding at the half of its job it was built for

Frame time is **non-monotonic in flight speed** at high view distance:

| vd | phase    | gate closed | avg CPU | avg wall FPS |
|----|----------|-------------|---------|--------------|
| 20 | gen 50 m/s  | 0.0 %    | 10.0 ms | 115.6        |
| 20 | gen 100 m/s | 90.8 %   | 9.5 ms  | 185.2        |
| 32 | gen 50 m/s  | 81.9 %   | 16.5 ms | 83.3         |
| 32 | gen 100 m/s | 90.7 %   | 10.5 ms | 165.0        |

Flying *faster* costs *less* frame time, because the faster phase trips the gate and the gate then throttles
admission. This is the panic gate doing exactly what it was designed to do — protecting frame time — at the
cost of completeness (F2). Any P-8 change that loosens the gate at high view distance **must be gated on
frame time**, not only on admission counts, or it will trade this away.

### F5 — The visibility criterion reproduces its boundary exactly

`latency ≤ viewDistance × 16 ÷ speed`, against loading-pass p50 end-to-end:

| speed   | vd 5        | vd 8        | vd 10        | vd 15         | vd 20         | vd 32         |
|---------|-------------|-------------|--------------|---------------|---------------|---------------|
| 50 m/s  | 1 378/1 600 ✅ | 1 374/2 560 ✅ | 1 377/3 200 ✅ | 1 391/4 800 ✅ | 1 561/6 400 ✅ | 6 257/10 240 ✅ |
| 100 m/s | 692/800 ✅   | 689/1 280 ✅  | 693/1 600 ✅  | 874/2 400 ✅   | 2 097/3 200 ✅ | 4 490/5 120 ✅  |
| 200 m/s | 354/400 ✅   | 593/640 ✅    | 1 033/800 ❌  | 1 802/1 200 ❌ | 2 561/1 600 ❌ | 3 903/2 560 ❌  |

Met everywhere except 200 m/s at vd ≥ 10 — **the identical boundary FP-8 found**, on a different route. As in
FP-8, vd 5 / 200 m/s passes on p50 (354/400) but is marginal: its p95 is 490 ms.

### F6 — vd 32 is a qualitatively different operating point

Not merely "more of vd 20":

* **Peak total memory 4 965 MB** (vd 20: 3 264 MB), managed heap alone ~1 954 MB, GC 154.5 KB/frame average.
* **The transition phase takes 9.7 s** against 0.0–0.1 s at every lower view distance — draining, saving and
  unloading 5 041 resident chunks is a measurable cost for the first time, and the first transition in the
  series to sample more than a handful of frames (1 050).
* **`LightSchedule` reported `OutOfWork` on 0 frames** in loading @ 50 m/s, and `MeshSchedule` likewise. Not
  "rare" — zero. The schedulers are quota-saturated on every frame of the phase.
* **Ensure-generated p99 `populated→lit` = 149 559 ms** — two and a half minutes for a single chunk's lighting
  hop.

vd 32 should be read as a stress point that maps the pipeline's failure mode, not as a supported configuration.

---

## Instrument observations

### I1 — The trace-buffer integrity banner fired in production for the first time

vd 32 / ensure-generated:

```
⚠ TRACE BUFFER SATURATED — the figures below cover only the first 50 691 chunks of this phase, not all of it.
   · latency sample cap reached (percentiles cover the kept samples)
```

The latency percentiles in that block cover **32 768 of the phase's 35 517 completed chunks**. FP-7's banner
is doing precisely what it was built for — a Release capture self-reporting a limit that the development-only
asserts are compiled out of.

**Consequence:** vd 32's ensure-generated *latency percentiles* are a subset and must not be quoted as
whole-phase figures. The disposition counts in that block remain internally consistent (35 517 + 1 562 +
4 288 + 9 324 = 50 691) and the stop-reason tallies are exact for the whole phase, as the standard note says.

### I2 — FP-9a's two mechanisms are both visible, and the second one is load-bearing

`RegimeBearing` and the 1 000-observation floor were shipped together, and this capture separates them:

* At vd 5/8/10/15/20 the transition phase has **4 eligible observations** — the floor alone would have
  suppressed it.
* At **vd 32** the transition has **4 200** and ensure-generated has **77 896**, both comfortably over the
  floor. Only `RegimeBearing` suppresses them.

That is production confirmation of the §7.3 row-7 claim that no floor could have caught the non-measurement
phases — filed at the time as reasoning, now observed.

### I3 — FP-9b's guarantee holds in the field

12 generation waypoints and **11 400 m timed travel at every view distance**, every speed phase running its
full 30 s. FP-8's D2 confound is closed, which is what makes this report's generation tables admissible at all.
The ensure-generated sweep ran 187.5 s in all six runs, confirming the 64-chunk tour was never shrunk.

### I4 — NEW: the ensure-generated sweep is itself gate-throttled, and its coverage is unmeasured

The ensure pass exists to guarantee the loading pass flies over generated terrain. It is **subject to the same
panic gate as everything else**, and at high view distance the gate wins:

| vd | ensure gate closed | ensure abandoned |
|----|--------------------|------------------|
| 5  | 0.0 %              | 1                |
| 10 | 0.5 %              | 81               |
| 15 | 3.2 %              | 503              |
| 20 | 14.4 %             | 1 676            |
| 32 | **92.3 %**         | **9 324**        |

At vd 32 the sweep was throttled on 92 % of its frames, so **it cannot be assumed to have generated the tour**,
and the subsequent "loading" pass may be partly a generation pass. The loading-pass `enqueue→populated` p50 at
vd 32 / 50 m/s is 5 362 ms against 427 ms at vd 20, which is consistent with that — **but it cannot be
attributed from these aggregates**, because that hop contains both admission wait and generation, and the gate
was 96.1 % closed in the same phase. Either cause produces the same number.

**The instrument does not currently measure what it needs to answer this.** The fix is to check and print
actual tour coverage (chunks generated / chunks in tour) at the end of the ensure pass, rather than inferring
it from the sweep having run. Filed below.

### I5 — Minor: the ensure sweep's own parameters are not printed

The `Route (derived — not configurable)` block prints region, rows, route length, timed travel and tour size,
but not the ensure sweep's speed (50 m/s) or duration (187.5 s) — both of which are derived and both of which
a reader needs to interpret I4. Same class as FP-6: a derived value that shapes the capture but is invisible
in it.

---

## Ranked follow-ups

| # | Item | Change vs FP-8 | Why |
|---|------|----------------|-----|
| **1** | **P-8 — scale panic-gate thresholds with resident count** | **confirmed at #1, mechanism upgraded** | F3: the 256/128 threshold is 88.6 % of the resident square at vd 5 and 5.1 % at vd 32, so the gate is permanently closed from vd 15 up. F2 shows the consequence: admitted work grows 1.5–1.7× while requests grow 4.5–4.8×. F4 sets the constraint — any change must hold frame time. |
| **2** | **P-7 — chunk service ordering, low view distance** | **unchanged, worst case relocated** | F1: worst case is now vd 8 / 200 m/s at 50.8 %, not vd 5. Acceptance criterion remains F5's visibility bound, which fails only at 200 m/s and vd ≥ 10. |
| **3** | **I4 — measure and print ensure-pass tour coverage** | **new** | Without it, every high-vd loading-pass number rests on an unverified assumption. Cheap, and it is the difference between "the loading pass measured loading" and "we think it did". |
| **4** | **I1 — raise or make configurable the latency-sample cap** | new | 32 768 is reached at vd 32. The banner means no number is silently wrong, so this is a coverage improvement, not a correctness fix. |
| **5** | **I5 — print the ensure sweep's speed and duration** | new | FP-6 class. One line. |
| **6** | **Per-chunk CSV export** (v3+) | unchanged | Still the only way to separate the stall populations; F6's 149 559 ms p99 is the strongest demand case yet recorded. |

**Not licensed by this capture:** readiness work (`AllDeclined` never dominated any of the 60 phases) and
in-flight-cap work (`InFlightCap` never dominated any phase; its maximum was 450 frames at vd 32 /
ensure-generated, against 18 970 `Quota`).

---

## Limitations

1. **n = 1 per view distance.** FP-8 is the only cross-check, and only on the loading pass. The vd-8 delta in
   §"Relationship to FP-8" is within what a single run can produce.
2. **FP-8 is not numerically continued.** The route changed; §"Relationship to FP-8" compares curve shape, not
   values.
3. **Run-level averages are not comparable to FP-8** — the ensure-generated phase changes the phase mix. Only
   the per-phase tables carry across.
4. **vd 32 ensure-generated latency percentiles are a subset** (I1), and its tour coverage is unverified (I4).
5. **The waste fraction's denominator shrinks with view distance** (F2). Percentages at vd ≥ 15 describe a
   minority of requested work and should always be read beside the admitted count.
6. Every run's `Git commit` line reads `(player build — record manually)`; the commit in the header table was
   established from build timing and the shared build GUID, not from the log.

---

## Document History

* **v1.0** — FP-10 captured and reported (2026-08-01). Six-point sweep, first capture on FP-9b's derived route
  and therefore the first with a cross-view-distance-comparable generation pass, first to include vd 32.
  Headline: FP-8's inverted ordering verdict **reproduces** across a total route rework, and the admission
  trend gains a measured mechanism — a fixed 256-chunk gate threshold against a resident square growing as
  vd², which holds admitted work to 1.5–1.7× growth while requests grow 4.5–4.8×. P-8 confirmed at #1. One new
  instrument defect filed (I4, ensure-pass coverage unmeasured) plus two minor items.

---

**Last Updated:** 2026-08-01
