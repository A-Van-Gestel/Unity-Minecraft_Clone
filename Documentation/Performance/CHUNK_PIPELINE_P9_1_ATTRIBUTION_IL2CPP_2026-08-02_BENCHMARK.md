# P9-1 — Attribution capture, IL2CPP Release — vd sweep 10/20/26/32 + a cap-48 A/B leg

| Field           | Value                                                                                                                                                                                                                                                                                              |
|-----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-08-02 13:05 – 14:22                                                                                                                                                                                                                                                                           |
| **Branch**      | `feat/world-scaling` (report authored at `0816b584`)                                                                                                                                                                                                                                               |
| **Commit**      | Build GUID **`496d7aeb48dd4f81871481d3dddaa68e`** — a fresh build carrying the P9-0 attribution instrument (`b5808a56` + `82aa6faa`). **All five runs share this GUID.** Not comparable to P-8 / P9-0a builds (§7 baseline rule)                                                                     |
| **Captured by** | `BenchmarkController` — **IL2CPP, Configuration: Release, Player, Burst on**. Five runs, **n = 1 per configuration**. i9-9900K / 16 threads / 64 GB / D3D11                                                                                                                                         |
| **Rule**        | **§7.1 v2**, as FP-8, FP-10, P-8 and P9-0a                                                                                                                                                                                                                                                         |
| **Verdict**     | **BASELINE — no fix was under test, so no GO/NO-GO applies to a change.** Three results carry: §3.1's rate identity is **confirmed to within 4 % across a 3.2× view-distance range**; §F4's merge attribution is **half-confirmed and quantitatively corrected** (the merge is the largest single cost centre at 26–29 % of wall, but ~40 % of the ×2-cap growth, not the ~70 % the fitted model implied); and **§3.3's mesh multiplier is refuted — pre-delivery mesh amplification is exactly 1.00 at every view distance**, which removes Option B2's mesh-side premise. §2's kill condition is **not** triggered. The cap-48 leg re-fails Q2 (×2.71 CPU, ×0.66 min FPS) on a second, independent build |

> **Design home:** [`Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](../Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md)
> — this is that document's **phase P9-1**. It is the first capture taken with the P9-0 instrument,
> and it exists to confirm or kill the fitted cost model in
> [P9-0a](CHUNK_PIPELINE_P9_0A_CAP_SWEEP_IL2CPP_2026-08-02_BENCHMARK.md) §F4 that promoted **P-3**
> to the gating lever.

---

## What this measures (and what it does NOT)

**The production path, unmodified, with the instrument on.** P9-0 added no production behaviour;
it added timing brackets and counters. What is new relative to every prior capture:

- **main-thread ms per pass**, with `ProcessLightingJobs` (the unbudgeted merge) separated from the
  budgeted ready-set scan, and the two other unbudgeted lighting regions (staging drain, ~1 Hz
  fail-safe scan) given their own slots;
- **items served vs quota granted** per scheduling pass, over frames where that pass had work;
- **work amplification** partitioned pre-delivery / no-live-trace / wasted / unresolved, with a
  reconciliation check against the independently-counted served total;
- **parked time per delivered chunk** — time in MT-2's lighting waiting set.

**Not measured.** The merge's *internals* (it is timed as one pass, so this confirms where the cost
sits, not which part of the merge owns it). Any fix — none was written. The OM-1 scaled-down device
regime. And `scalePanicGateThresholdsWithResidency` stayed **OFF** throughout (P-8 untouched, §1).

**Instrument integrity across all five runs:** no `NOT MEASURED` banner, no `RECONCILIATION GAP`,
no `CAPABILITY MATRIX STALE`, no `DOUBLE-RECORDED PASS`. Every amplification row's bucket total
equals the pass's independently-counted served total, exactly.

---

## Methodology

Five menu-launched runs on one build, settings inherited from disk (§7.1). R1–R4 sweep view distance
at the OM-1 caps (`maxLightJobsPerFrame` 24 / `maxMeshRebuildsPerFrame` 11, read back from each run's
own settings block). R5 repeats R4's view distance with `maxLightJobsPerFrame` 48 — the A/B leg that
tests §F4's slope, since that model was fitted *across* caps 24 and 48 and a single-cap sweep cannot
test it. R4 and R5 ran adjacent in time.

Each run: generation pass (10/20/50/100/200 m/s, 30 s each) → ensure-generated sweep (197.8 s,
non-measurement) → transition → loading pass (50/100/200 m/s, 30 s each).

**The scored phase is the loading pass @ 200 m/s**, as in P-8 and P9-0a.

---

## Raw results (§7.2 — the verdict never replaces these)

### Loading pass @ 200 m/s — dispositions and latency

| Run | vd | cap | frames | gate closed | started | abandoned | **delivered** | waste | **p50 e2e** | enq→pop | pop→lit | lit→mesh |
|-----|----|-----|--------|-------------|---------|-----------|---------------|-------|-------------|---------|---------|----------|
| R1  | 10 | 24  | 5 107  | 80.5 %      | 12 122  | 1 618     | **6 061**     | 37.3 %| **813 ms**  | 286 ms  | 420 ms  | 6.4 ms   |
| R2  | 20 | 24  | 4 240  | 93.2 %      | 21 249  | 10 783    | **7 022**     | 16.5 %| **2 269 ms**| 1 611 ms| 582 ms  | 5.7 ms   |
| R3  | 26 | 24  | 3 650  | 92.5 %      | 27 142  | 16 152    | **6 773**     | 13.9 %| **3 107 ms**| 2 354 ms| 537 ms  | 6.0 ms   |
| R4  | 32 | 24  | 3 012  | 91.0 %      | 33 287  | 21 288    | **6 857**     | 11.2 %| **3 644 ms**| 2 976 ms| 570 ms  | 7.1 ms   |
| R5  | 32 | 48  | 930    | **64.1 %**  | 32 445  | 18 882    | **7 564**     | 17.0 %| **3 158 ms**| 2 454 ms| 531 ms  | 14.8 ms  |

### Loading pass @ 200 m/s — main-thread cost per pass (ms per second of wall clock)

| Run | Tick | Apply | **LightMerge** | StagingDrain | FailSafeScan | **LightSchedule** | MeshProcess | MeshSchedule | GenProcess | **all timed** |
|-----|------|-------|----------------|--------------|--------------|-------------------|-------------|--------------|------------|---------------|
| R1  | 33.3 | 0.2   | **287.6**      | 0.4          | 0.1          | **220.6**         | 57.1        | 109.0        | 1.1        | **709.3 (70.9 %)** |
| R2  | 38.5 | 0.1   | **280.4**      | 0.4          | 0.2          | **214.4**         | 56.7        | 103.4        | 0.8        | **694.9 (69.5 %)** |
| R3  | 31.6 | 0.1   | **272.4**      | 0.3          | 0.2          | **218.3**         | 62.7        | 104.1        | 0.9        | **690.7 (69.1 %)** |
| R4  | 45.2 | 0.1   | **261.3**      | 0.3          | 0.3          | **216.6**         | 58.4        | 102.3        | 0.6        | **685.1 (68.5 %)** |
| R5  | 54.3 | 0.1   | **290.3**      | 0.3          | 0.2          | **250.4**         | 64.3        | 85.8         | 0.2        | **745.9 (74.6 %)** |

### Loading pass @ 200 m/s — the same costs per FRAME (ms)

| Run | LightMerge | LightSchedule | MeshSchedule | MeshProcess | Tick | all timed | avg CPU frame | min wall FPS |
|-----|-----------|---------------|--------------|-------------|------|-----------|---------------|--------------|
| R4  | 2.603     | 2.157         | 1.019        | 0.581       | 0.450| 6.824     | 11.7 ms       | 31.6         |
| R5  | **9.370** | **8.082**     | 2.769        | 2.077       | 1.752| 24.076    | **31.7 ms**   | **21.0**     |

### Loading pass @ 200 m/s — quota utilisation and stop reasons

| Run | granted/frame | served/frame | **utilisation** | **light served/s** | mesh served/s | Light `Quota` | Light `Ceiling` |
|-----|---------------|--------------|-----------------|--------------------|---------------|---------------|-----------------|
| R1  | 9.0           | 8.8          | 98.3 %          | **1 496**          | 705           | 5 067 / 5 107 | 19              |
| R2  | 10.7          | 10.4         | 97.0 %          | **1 462**          | 702           | 4 179 / 4 240 | 54              |
| R3  | 12.3          | 12.0         | 97.5 %          | **1 463**          | 696           | 3 599 / 3 650 | 51              |
| R4  | 14.8          | 14.3         | 96.3 %          | **1 435**          | 676           | 2 951 / 3 012 | 60              |
| R5  | 93.1          | 54.4         | **58.5 %**      | **1 686**          | 582           | **1**         | **925 / 930**   |

### Loading pass @ 200 m/s — work amplification (lighting quota units per delivered chunk)

| Run | **pre-delivery** | no live trace | wasted | unresolved | **total / chunk** | mesh pre-delivery |
|-----|------------------|---------------|--------|------------|-------------------|-------------------|
| R1  | **3.95**         | 1.72          | 1.70   | 0.04       | **7.41**          | **1.00**          |
| R2  | **3.83**         | 1.89          | 0.49   | 0.06       | **6.26**          | **1.00**          |
| R3  | **3.94**         | 1.98          | 0.46   | 0.10       | **6.47**          | **1.00**          |
| R4  | **3.92**         | 1.89          | 0.36   | 0.10       | **6.28**          | **1.00**          |
| R5  | **4.04**         | 1.59          | 0.86   | 0.20       | **6.69**          | **1.00**          |

### Generation pass @ 10 m/s — the §10 q4 regime (idle lighting pass, multi-second `populated→lit`)

| Run | vd | `LightSchedule` `OutOfWork` | gate closed | **`populated→lit` p50** | **parked p50** | parked ÷ hop | pre-delivery light amp |
|-----|----|-----------------------------|-------------|-------------------------|----------------|--------------|------------------------|
| R1  | 10 | 97.0 %                      | 0.0 %       | 3 282 ms                | **1 568 ms**   | **47.8 %**   | 6.59                   |
| R2  | 20 | 95.0 %                      | 0.0 %       | 3 343 ms                | **1 542 ms**   | **46.1 %**   | 6.68                   |
| R3  | 26 | 93.4 %                      | 0.0 %       | 3 392 ms                | **1 516 ms**   | **44.7 %**   | 6.70                   |
| R4  | 32 | 89.0 %                      | 0.0 %       | 3 434 ms                | **1 474 ms**   | **42.9 %**   | 6.76                   |
| R5  | 32 | 93.0 %                      | 0.0 %       | 3 418 ms                | **1 497 ms**   | **43.8 %**   | 6.63                   |

### Tour coverage (Q6 validity gate)

| Run | on disk when the loading pass starts |
|-----|--------------------------------------|
| R1  | **100.0 %** ✅                        |
| R2  | **99.7 %** ✅                         |
| R3  | **98.3 %** ❌ — the loading pass generated the remainder |
| R4  | **97.8 %** ❌ — the loading pass generated the remainder |
| R5  | **99.1 %** ✅                         |

---

## Findings

### F1 — §3.1's rate identity is confirmed to within 4 % across a 3.2× view-distance range

`cap × 60` predicts 1 440 lighting schedules/s. Measured on the scored phase: **1 496 / 1 462 /
1 463 / 1 435** at vd 10 / 20 / 26 / 32. Mesh predicts 660/s; measured 705 / 702 / 696 / 676. Both
sit marginally above the identity, exactly as §3.2's `CeilToInt` caveat says they should.

Delivered chunks per phase: **6 061 / 7 022 / 6 773 / 6 857** — the flat band P-9 was opened on,
reproduced on a fresh build across a view-distance range the design doc had only inferred it over.
The pipeline delivers **202–234 chunks/s regardless of view distance**, because the rate that feeds
it contains no view-distance term.

The identity closes exactly: delivered/s = light-served/s ÷ total-schedules-per-delivered-chunk.
At R4: 1 435 ÷ 6.28 = **228.5/s**, against 6 857 ÷ 30 = **228.6/s**.

### F2 — The pipeline consumes ~69 % of the main thread, and that share is view-distance-invariant

`all timed regions` = **70.9 / 69.5 / 69.1 / 68.5 %** of wall clock at vd 10 → 32. Because the rate
is fixed, the pipeline's *per-second* main-thread cost is fixed; what changes with view distance is
everything else (rendering), so the frame rate falls (170 → 100 fps) while the pipeline's slice
stays put.

This reframes the whole item. The pipeline is not "cheap at low vd and expensive at high vd" — it
costs the same 0.69 s of main thread per second of wall clock everywhere, and high view distance
simply leaves less room beside it.

### F3 — §F4 is HALF confirmed, and its magnitude was wrong by ~2×

P9-0a fitted a model with one free parameter (~0.37 ms per merge) that put ~16 of +23.1 ms in the
unbudgeted merge. Measured, on the R4 → R5 pair:

| Term            | R4 ms/frame | R5 ms/frame | Δ         | share of the instrumented growth |
|-----------------|-------------|-------------|-----------|----------------------------------|
| **LightMerge**  | 2.603       | 9.370       | **+6.767**| **39 %**                         |
| **LightSchedule**| 2.157      | 8.082       | **+5.925**| **34 %**                         |
| MeshSchedule    | 1.019       | 2.769       | +1.750    | 10 %                             |
| MeshProcess     | 0.581       | 2.077       | +1.496    | 9 %                              |
| Tick            | 0.450       | 1.752       | +1.302    | 8 %                              |
| **all timed**   | 6.824       | 24.076      | **+17.252**| —                               |

**What §F4 got right:** it sized the schedule pass almost exactly (predicted +6.8 ms, measured
+5.9 ms), and it correctly identified the merge as the largest single unattributed cost.
**What it got wrong:** the merge is ~39 % of the growth, not ~70 %. The residual is spread across
the mesh passes and the behaviour tick, which the model had no term for.

The per-item costs are stable and now measured directly:

| Run | ms per lighting **schedule** | ms per lighting **merge** |
|-----|------------------------------|---------------------------|
| R1  | 0.1475                       | 0.1922                    |
| R2  | 0.1467                       | 0.1918                    |
| R3  | 0.1492                       | 0.1862                    |
| R4  | 0.1510                       | 0.1821                    |
| R5  | 0.1485                       | 0.1722                    |

**A lighting job costs ~0.33 ms of main thread in total — ~0.15 ms to schedule, ~0.18 ms to merge.**
§F4's single fitted parameter of 0.37 ms/merge was in fact the *sum of both passes*. The merge is
real, it is the largest slot, and it is roughly co-equal with the scan rather than dominant.

### F4 — The ceiling binds exactly where it was predicted to, and the merge is the unbounded term

At cap 48 the light schedule reports `Ceiling` on **925 of 930 frames (99.5 %)** and spends
**8.082 ms/frame against an 8.0 ms ceiling** — dead on the budget. Quota utilisation collapses to
**58.5 %**: the frame is granted 93 items and the ceiling only lets it serve 54. P9-0a §F3 said the
count cap goes inert past ×2; measured, it goes inert *completely* (one `Quota` stop in 930 frames).

The merge, having no budget, went to **9.370 ms/frame — more than the budgeted pass's entire
ceiling.** That is the §3 spiral shape running through the one pass the budgets do not cover, and
it is now a measurement rather than an inference.

Note what did *not* happen: total pipeline work per second barely moved (685 → 746 ms/s, +9 %) and
delivery rose only 10 %. Raising the cap did not buy much more work — it repacked nearly the same
work into **3.2× fewer, 3.2× longer frames**.

### F5 — §3.3's mesh multiplier is REFUTED: pre-delivery mesh amplification is exactly 1.00

§3.3 inferred ~3.5 mesh schedules per delivered chunk and framed them as work spent ahead of first
visibility. Measured: **1.00, at every view distance, in every run, to the unit.** Every chunk is
meshed exactly once before it is delivered. There is no pre-delivery mesh redundancy to remove.

The multiplier is real but entirely **post-delivery** (1.5–2.0 per chunk in the `no live trace`
bucket — an upper bound, per the instrument's own caveat).

### F6 — Lighting amplification is ~3.9 pre-delivery, and it is regime-dependent, not vd-dependent

Pre-delivery lighting: **3.83–4.04** across the entire vd sweep on the loading pass — flat. §3.3
inferred ~6.3–7.6; that figure turns out to be close to the *total* (6.26–7.41), not the
pre-delivery half.

But at 10 m/s generation, pre-delivery lighting is **6.59–6.76**. So amplification varies with
*regime*, not with view distance — the low-speed generation case spends ~70 % more lighting work per
chunk ahead of delivery than the high-speed loading case does.

### F7 — The latency is 82 % admission wait, which bounds what deliver-then-refine can ever recover

At R4 (vd 32, loading 200 m/s), of a 3 644 ms end-to-end p50:

| Hop                 | p50      | share  |
|---------------------|----------|--------|
| `enqueue→populated` | 2 976 ms | **82 %** |
| `populated→lit`     | 570 ms   | 16 %   |
| `lit→meshApplied`   | 7.1 ms   | **0.2 %** |

The panic gate is closed on **91 %** of frames. The chain is: quota caps the lighting drain rate →
the lighting backlog stays above the gate threshold → the gate refuses admission → requests wait
~3 s before terrain even exists.

Option B2 (deliver on first viable mesh) can only act on `populated→lit` + `lit→meshApplied` =
**577 ms of 3 644 ms (16 %)**. Eliminating both hops entirely leaves 3 067 ms against a 2 560 ms
budget. P9-0a estimated this at 537 of 3 703 ms from a different build; the measurement agrees.

### F8 — §10 q4 answered: parking explains ~45 % of the idle-pass `populated→lit` hop

In the generation pass @ 10 m/s — where `LightSchedule` reports `OutOfWork` on 89–97 % of frames and
the panic gate never closes — `populated→lit` is 3 282–3 434 ms and **parked time is 1 474–1 568 ms,
i.e. 43–48 % of the hop, essentially invariant across a 3.2× view-distance range.**

So roughly half of that multi-second stall is a chunk sitting *ineligible* in MT-2's waiting set,
which no quota, gate or budget change can reach — exactly what §10 q4 predicted, now measured. The
remaining ~1.9 s is not parked and not un-served (the pass is idle); the most likely home is the
serialized edge-check cascade, since `populated→lit` measures to the **last** lighting completion
and pre-delivery amplification in this regime is ~6.7 passes per chunk. **That attribution is not
measured here** — the instrument counts parked time and schedules, not cascade depth per round.

### F9 — The cap-48 leg re-fails Q2 on an independent build

Loading @ 200 m/s, R4 → R5: avg CPU **11.7 → 31.7 ms (×2.71)**, min wall FPS **31.6 → 21.0
(×0.66)**, for +10 % delivery and −13 % p50 e2e. P9-0a measured ×4.79 / ×0.61 on the P-8 build; the
direction and the failure reproduce, the magnitude differs with the baseline. Raising the cap
remains unaffordable.

---

## Verdict against the pre-committed criteria

Criteria are §2 of the design doc. **P9-1 tests no fix**, so Q1/Q2 are scored only for the cap-48
leg (R5 vs R4); the rest are reported as baseline state.

| #      | Criterion                    | Result                                                                                                                                    |
|--------|------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------|
| **Q1** | Visibility budget            | **Baseline: missed at vd 20/26/32** — 1.42× / 1.49× / 1.42× (vd 10 **meets**, 813 ms vs an 800 ms budget). R5 improves the vd-32 shortfall 1.42× → 1.23×, a ×1.15 gain, **below** the ×1.3 partial-credit bar |
| **Q2** | **Frame time holds** ⚠️      | **R5 FAILS** — min FPS ×0.66, avg CPU ×2.71. Hard gate                                                                                     |
| **Q3a**| Rate lever moved the ceiling | **SPLIT, as in P9-0a** — `Quota` share 98 % → 0.1 % ✅, completions ×1.10 ❌ (needs ≥ ×1.35)                                                |
| **Q4** | Memory holds                 | ✅ peak 5 026 → 5 105 MB = **×1.02**                                                                                                       |
| **Q5** | Waste not scored             | Recorded — loading 200 m/s waste 11.2 % → 17.0 % at cap 48, the usual direction                                                            |
| **Q6** | Coverage ≥ 99 %              | ❌ **R3 (98.3 %) and R4 (97.8 %)**; R1/R2/R5 pass. See Limitation 1 — this hits the A/B baseline                                            |
| **Kill condition** | Do the two **scheduling** passes consume a majority of the frame at vd ≥ 26? | **NO — 32.2 % at vd 26, 31.9 % at vd 32** (`LightSchedule` + `MeshSchedule`). The kill condition is **not** triggered and §6's levers stay open |

---

## Verdict details — what this redirects to

**The kill condition survives on its literal terms, but the margin is smaller than it looks.** The
two *budgeted scheduling* passes are ~32 % of wall. But the whole instrumented pipeline is ~69 %,
and **lighting alone (merge + scan + drain + fail-safe) is ~49 %**. The headroom §4.2 reasoned about
exists in the scheduling passes; it does not exist in the frame.

**Option A′ (raise the caps) stays closed.** Second independent build, same Q2 failure.

**Option B2 (deliver-then-refine) is now refuted as a throughput or visibility lever, on
measurement rather than inference.** Two independent reasons: its mesh-side premise is void (F5 —
pre-delivery mesh amplification is exactly 1.00, there is no pre-delivery mesh work to skip), and
its reachable latency is 16 % of a gap that is 42 % wide (F7). Its *product* rationale in §3.4 — a
dark chunk beats void — is untouched and should be re-filed on its own terms rather than as a P-9
throughput phase.

**Option C (cut per-item cost, i.e. P-3) is confirmed as real but demoted from "gating" to
"enabler".** The merge is the largest single cost centre (26–29 % of wall) and is the unbounded term
in the frame-time spiral (F4), so removing it is worth doing. But at the shipping cap the light
schedule is **`Quota`-bound on 98 % of frames, not `Ceiling`-bound** — so making each item cheaper
frees frame time without delivering a single extra chunk. P-3 buys Q2 headroom; it does not move Q1
by itself. It is the precondition for A′, not a substitute for it.

**Option B1 (delete redundant amplification) is promoted to the lead lever**, and for the first time
it has a measured target. Delivered/s = rate ÷ schedules-per-chunk = 1 435 ÷ 6.28. Of those 6.28
lighting schedules per delivered chunk, **3.92 are pre-delivery, ~1.9 are post-delivery corrections
and ~0.4 are spent on chunks that were later discarded.** Cutting the multiplier raises delivery
proportionally at **zero** frame-time cost — the only lever in §6 with that property, and the one
that compounds with every other (§6's own argument for B1, now backed by numbers).

⚠️ Two honest caveats on that promotion. The `no live trace` bucket is an **upper bound** on
correction work by construction. And nothing here shows that any of the 6.28 is *redundant* — F6
shows it varies by regime, which is consistent with it being genuinely required convergence work.
**B1 remains conditional on finding work that recomputes an unchanged result**, exactly as §6 says;
this capture sizes the prize, it does not prove the prize exists.

---

## Limitations

1. **R3 and R4 fail the coverage gate (98.3 % / 97.8 %)**, and R4 is the baseline for the R5 A/B.
   Their loading passes generated part of their terrain, which **inflates `enqueue→populated`** —
   so R4's e2e is overstated and the R4 → R5 improvement is correspondingly **overstated**. The bias
   runs against the cap-48 leg looking bad, and it still fails Q2 by a factor, so the verdict is
   safe; the ×1.15 Q1 figure is not.
2. **n = 1 per configuration.** No control leg reproduces a prior session the way P9-0a's L1 did,
   because this is a new build and cross-build comparison is invalid (§7).
3. **The merge is timed as one pass.** This confirms *that* the merge is the largest cost centre; it
   does not say which part of it (full-volume light-map apply, cross-chunk mod routing, pull-back
   verification, stability bookkeeping) owns the 0.18 ms. **P-3 will need a finer breakdown before
   it can be scoped.**
4. **F8's residual is not attributed.** Parking explains ~45 % of the idle-pass `populated→lit`; the
   cascade hypothesis for the rest is consistent with the amplification figures but is **not
   measured**.
5. **Parked time is a lower bound**, biased against the longest waiters — delivered chunks only, the
   lighting waiting set only, and the fail-safe promote-to-rescan gap uncounted (design doc §10 q4).
6. **Generation-pass figures for R3/R4 at 100 and 200 m/s were read from pasted logs** whose phase
   boundaries I could not unambiguously resolve. Nothing in this report's findings rests on them;
   the scored loading phases and the 10 m/s generation phase are unambiguous. Re-read the original
   files before quoting those two rows.
7. **Only the light cap was varied.** The mesh-side ceiling is still untested, and `MeshSchedule`
   reported `InFlightCap` on 417 of 930 frames in R5 — a mesh-quota leg would substantially measure
   the in-flight bound of 20 instead.
8. Player builds do not record their commit; the header's commit comes from the shared build GUID.

---

## Document History

* **v1.0** — P9-1 captured and reported (2026-08-02). First capture with the P9-0 attribution
  instrument. Confirms §3.1's rate identity within 4 % across vd 10→32 and reproduces the flat
  completion band on a fresh build; measures the pipeline at ~69 % of the main thread,
  view-distance-invariant; **half-confirms and corrects §F4** (the merge is the largest single cost
  centre and the unbounded term in the spiral, but ~39 % of the ×2-cap growth, not ~70 % — its
  0.37 ms fitted parameter was the sum of scan + merge, measured at 0.15 + 0.18 ms);
  **refutes §3.3's mesh multiplier** (pre-delivery mesh amplification is exactly 1.00 everywhere),
  which removes Option B2's mesh-side premise; answers **§10 q4** (parking is 43–48 % of the
  idle-pass `populated→lit` hop); and re-fails Q2 for the cap-48 leg on an independent build.
  §2's kill condition is **not** triggered. Lever order becomes **B1 → C → A′**, with B2 re-filed as
  a product item.

---

**Last Updated:** 2026-08-02
