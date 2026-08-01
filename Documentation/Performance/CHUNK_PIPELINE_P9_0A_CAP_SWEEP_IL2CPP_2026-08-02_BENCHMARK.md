# P9-0a — Light-quota cap probe, IL2CPP Release — two same-build legs at view distance 32

| Field           | Value                                                                                                                                                                                                                                                                                                     |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-08-01 23:42 – 2026-08-02 00:30                                                                                                                                                                                                                                                                       |
| **Branch**      | `feat/world-scaling` (report authored at `7eabda7b`)                                                                                                                                                                                                                                                      |
| **Commit**      | Build GUID **`cbb80fcb79164d7ab43a18a8bb28815d`** — the **same build as the P-8 capture** (`4ea1a38e`, over FP-11a's `26ec687e`). No rebuild: both legs are settings-only, per the warm-cache finding in [`Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](../Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) §7.1 |
| **Captured by** | `BenchmarkController` — **IL2CPP, Configuration: Release, Player, Burst on**. Two runs, **n = 1 per leg**, both at view distance 32. i9-9900K / 16 threads / 64 GB / D3D11                                                                                                                                 |
| **Rule**        | **§7.1 v2**, as FP-8, FP-10 and P-8                                                                                                                                                                                                                                                                       |
| **Verdict**     | **MECHANISM CONFIRMED / NO-GO on the fix.** Doubling `maxLightJobsPerFrame` (24 → 48) does exactly what P-9 §3.1 predicts — gate closure falls **95.1 % → 62.6 %**, admission rises 25 %, completions rise **21 %**, and p50 end-to-end falls **26 %** — but it costs **×4.8 loading-pass CPU** and **×0.61 minimum FPS**, failing criterion Q2 by a wider margin than P-8 did. The binding limit did not move to a higher quota: it moved to the **8 ms schedule ceiling**, which now stops the pass on **95.8 %** of frames. The rate identity is real; raising the rate is unaffordable while `ProcessLightingJobs` stays unbudgeted |

> **Design home:** [`Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](../Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md)
> — this is that document's **phase P9-0a**, the zero-code falsification probe it opens with. The
> document predicted the outcome that would confirm it ("a genuine throughput win should collapse
> `enqueue→populated` via the gate reopening, not merely shrink `populated→lit`") and that prediction
> is what happened (§F2). Motivating capture:
> [P-8](CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md) §F3, which identified `Quota`
> as the binding constraint by elimination. This capture confirms the identification and prices the
> obvious fix.

---

## What this measures (and what it does NOT)

**The production path, unmodified.** No code changed between the legs and none was written for this
capture. The only difference is one settings value, read from disk through the warm settings cache and
**printed by each run's own settings block** (never asserted from configuration):

| Leg    | `maxLightJobsPerFrame` | `maxMeshRebuildsPerFrame` | Implied rate ceiling (`cap × 60`) |
|--------|------------------------|---------------------------|-----------------------------------|
| **L1** | **24** (OM-1 calibrated) | 11 (OM-1 calibrated)    | 1 440 light + 660 mesh schedules/s |
| **L2** | **48** (×2)              | 11 (unchanged)          | 2 880 light + 660 mesh schedules/s |

Everything else is identical and was verified from the settings blocks: view distance 32, load
distance 35 (71×71 = 5 041 resident), in-flight caps 32 / 64 / 20, ms ceilings 6 / 8 / 6 / 4,
budgets ON, FPS-cap scaling ON, panic gate ON with **`scalePanicGateThresholdsWithResidency` OFF**
(256 / 128 unscaled — the shipping default; P-8's flag was deliberately not exercised).

**Not measured:** the mesh cap (held at 11, so the mesh-side ceiling is untested); view distance 26
(no leg); ordering (P-7, untouched — waste rises and is not charged); any code fix. **Crucially, the
main-thread cost is not *attributed*** — `WorldFrameProfiler.Phase.Light` lumps `ProcessLightingJobs`
together with the schedule scan, and the benchmark does not enable it at all, so §F4's cost model is
**inference from ratios, not measurement**. Separating them is phase P9-0.

---

## Methodology

Two runs, one build, launched from the main menu (the path that inherits settings.json). Each run:
generation pass (10 / 20 / 50 / 100 / 200 m/s, 30 s each, 12 waypoints) → ensure-generated sweep
(non-measurement, 50 m/s over the closed 9 889 m tour = 197.8 s) → transition (drain + save + unload)
→ loading pass (50 / 100 / 200 m/s, 30 s each). Route geometry is derived per FP-9b, so waypoints and
the 11 400 m timed travel are identical in both legs.

**The scored phase is the loading pass @ 200 m/s**, as in P-8 and FP-10.

**L1 doubles as a control on the whole session.** It is the same build and the same configuration as
P-8's vd 32 unscaled control, run three weeks later, so it independently tests session-to-session
drift before any conclusion rests on the L1 ↔ L2 comparison — see §F1.

---

## Raw results (§7.2 — the verdict never replaces these)

### Loading pass @ 200 m/s — the criterion phase

| Leg | frames | gate closed | requested | abandoned | **admitted** | **completed** | in-flight @ end | unloaded | waste | p50 e2e | verdict |
|-----|--------|-------------|-----------|-----------|--------------|---------------|-----------------|----------|-------|---------|---------|
| L1  | 5 428  | 95.1 %      | 33 330    | 21 485    | **11 845**   | **6 933**     | 3 760           | 1 152    | 9.7 %  | 3 703 ms | AdmissionBound |
| L2  | 1 005  | **62.6 %**  | 32 302    | 17 464    | **14 838**   | **8 382**     | 3 551           | 2 905    | 19.6 % | **2 753 ms** | Healthy |

### Loading pass — stage latency p50 (only chunks reaching `MeshApplied` contribute)

| Speed   | Leg | `enqueue→populated` | `populated→lit` | `lit→meshApplied` | `enqueue→meshApplied` |
|---------|-----|---------------------|-----------------|-------------------|-----------------------|
| 200 m/s | L1  | 2 999 ms            | 533 ms          | 4.0 ms            | 3 703 ms              |
| 200 m/s | L2  | **2 134 ms**        | 494 ms          | 14.1 ms           | **2 753 ms**          |
| 100 m/s | L1  | 3 006 ms            | 641 ms          | 5.2 ms            | 3 875 ms              |
| 100 m/s | L2  | **1 592 ms**        | 603 ms          | 15.7 ms           | **2 510 ms**          |
| 50 m/s  | L1  | 4 272 ms            | 701 ms          | 4.1 ms            | 5 143 ms              |
| 50 m/s  | L2  | **1 069 ms**        | 1 017 ms        | 43.3 ms           | **3 214 ms**          |

### Loading pass @ 50 and 100 m/s — dispositions

| Speed   | Leg | gate closed | requested | abandoned | admitted | completed | waste  |
|---------|-----|-------------|-----------|-----------|----------|-----------|--------|
| 50 m/s  | L1  | 94.1 %      | 13 615    | 4 870     | 8 745    | 6 603     | 6.9 %  |
| 50 m/s  | L2  | **31.9 %**  | 12 787    | 2 775     | 10 012   | **8 266** | 12.2 % |
| 100 m/s | L1  | 93.3 %      | 16 042    | 6 189     | 9 853    | 6 674     | 8.0 %  |
| 100 m/s | L2  | **63.7 %**  | 15 385    | 3 645     | 11 740   | **7 911** | 15.2 % |

### Stop-reason tallies — loading pass @ 200 m/s (exact for the whole phase)

| Leg | Pass            | NotRun | OutOfWork | **Quota** | **Ceiling** | InFlightCap | AllDeclined |
|-----|-----------------|--------|-----------|-----------|-------------|-------------|-------------|
| L1  | `LightSchedule` | 0      | 1         | **5 405** | 21          | 1           | 0           |
| L1  | `MeshSchedule`  | 0      | 0         | **5 399** | 0           | 29          | 0           |
| L2  | `LightSchedule` | 0      | 2         | **8**     | **963**     | 32          | 0           |
| L2  | `MeshSchedule`  | 0      | 0         | **707**   | 4           | 294         | 0           |

### Generation pass @ 200 m/s

| Leg | frames | gate closed | requested | abandoned | admitted | completed | waste  | verdict                     |
|-----|--------|-------------|-----------|-----------|----------|-----------|--------|-----------------------------|
| L1  | 1 763  | 57.0 %      | 30 209    | 19 138    | 11 071   | **4 353** | 16.8 % | AdmissionBound              |
| L2  | 919    | 33.0 %      | 30 483    | 19 981    | 10 502   | **3 353** | 21.4 % | Healthy + **ORDERING-BOUND** |

### Frame cost — per-pass-group totals

| Leg | gen avg CPU | gen min FPS | ensure avg CPU | load avg CPU | **load min FPS** | peak total mem | avg GC/frame |
|-----|-------------|-------------|----------------|--------------|------------------|----------------|--------------|
| L1  | 15.2 ms     | 22.0        | 13.1 ms        | **7.4 ms**   | **37.4**         | 5 070.9 MB     | 206.7 KB     |
| L2  | 23.1 ms     | 20.0        | 27.1 ms        | **28.1 ms**  | **22.2**         | 5 319.8 MB     | 400.2 KB     |

Loading @ 200 m/s alone: avg CPU **6.1 ms → 29.2 ms**, min wall FPS **43.7 → 26.6**.

### Tour coverage

| Leg | after ensure sweep | on disk when loading starts |
|-----|--------------------|-----------------------------|
| L1  | **99.0 %**         | 99.0 %                      |
| L2  | **97.8 %**         | 97.8 % — *the loading pass generated the remainder* |

---

## Findings

### F1 — The session reproduces P-8's control, so the L1 ↔ L2 comparison is trustworthy

L1 is the same build and configuration as P-8's vd 32 unscaled control, three weeks later, and the
operator reported heavy desktop multitasking during it. Loading @ 200 m/s:

| Metric          | P-8 OFF control | L1       | Δ       |
|-----------------|-----------------|----------|---------|
| Requested       | 33 432          | 33 330   | −0.3 %  |
| Admitted        | 11 911          | 11 845   | −0.6 %  |
| Completed       | 6 807           | 6 933    | +1.9 %  |
| **p50 e2e**     | 3 688 ms        | 3 703 ms | **+0.4 %** |
| Gate closed     | 94.5 %          | 95.1 %   | +0.6 pt |
| Loading min FPS | 37.5            | 37.4     | −0.3 %  |
| Peak memory     | 5 118 MB        | 5 071 MB | −0.9 %  |

Every scored metric within 2 %. This is the strongest evidence available that the scored phase is
insensitive to the session, and it partly retires the `n = 1` limitation for the criterion phase.
It does **not** extend to the unscored phases: L1's generation pass ran 13 % cheaper than P-8's and
its ensure pass 24 % cheaper, both outside the ±10 % band and unexplained — a busier machine running
*faster* is not a mechanism anyone has proposed.

### F2 — The rate identity is confirmed, and by the predicted mechanism

The design doc committed in advance to what a genuine throughput win would look like: **the admission
wait collapses because the gate reopens**, not the lighting hop shrinking. That is precisely the
observed shape at 200 m/s:

- Gate closure **95.1 % → 62.6 %** (−32.5 points) with no threshold touched — the gate keys on
  `LightWorkScheduler.ReadyCount`, so a drained backlog reopens it automatically.
- `enqueue→populated` **2 999 → 2 134 ms** (−28.9 %), which is 91 % of the total latency improvement.
- `populated→lit` **533 → 494 ms** (−7.2 %) — nearly unchanged.
- Admitted **+25.3 %**, completed **+20.9 %**.

The effect is monotone in speed: gate closure falls furthest at 50 m/s (94.1 % → 31.9 %), where
`enqueue→populated` drops **4 272 → 1 069 ms**. This is the identity in §3.1 behaving exactly as
derived — `cap × 60` is a real throughput ceiling, and admission was downstream of it, confirming
P-8 §F3's identification rather than merely re-observing it.

### F3 — The binding limit moved to the ms ceiling, not to a higher quota

`LightSchedule` at ×2 reports `Quota` on **8 frames** and `Ceiling` on **963 of 1 005 (95.8 %)**. The
pass now spends its full 8 ms every frame.

Two consequences. First, **the count cap stopped mattering** the moment the ceiling bound: throughput
became `ceiling_ms ÷ per-item cost`, so a ×4 leg would have produced almost the same numbers and was
dropped from the run matrix. Second, this quantifies the headroom the design doc inferred: §4.2 argued
from the group-average CPU that the pass finished its quota "well inside 8 ms" and that headroom
existed. It did — but **less than ×2 worth**. The doc's second §4.2 point, that 8 + 6 ms was never
validated as a *steady-state* budget and only ever sized as a hitch guard, is what this leg vindicates.

The effective delivered rate rose only **~20 %** (completions +20.9 %) against a **100 %** larger cap,
because the ceiling intercepted the rest.

### F4 — The frame cost is not in the schedule pass (inference)

Loading @ 200 m/s frame time rose **6.1 → 29.2 ms**, +23.1 ms. The schedule pass cannot account for it:

| Term                              | L1        | L2         |
|-----------------------------------|-----------|------------|
| Frames per second (30 s phase)    | 181       | 33.5       |
| Light-schedule ms per frame       | ~1.2 ms † | 8.0 ms (ceiling-bound) |
| Light-schedule ms **per second**  | ~223 ms   | ~268 ms    |

† Derived: L2 is ceiling-bound at 8 ms × 33.5 fps = 268 ms/s for an effective ~1 730 schedules/s,
giving ~0.155 ms per lighting schedule; applying that cost to L1's quota-bound 1 440/s gives ~223 ms/s
= ~1.2 ms/frame.

So the schedule pass explains **+6.8 ms of +23.1 ms**. The remaining **~16 ms** is unattributed by any
current instrument. The arithmetic points at one candidate: the effective light-job rate rose ~20 %
while the frame rate fell **5.4×**, so **lighting jobs completing per frame rose ~8 → ~52 (6.5×)** —
and `ProcessLightingJobs`' merge is the one pipeline pass deliberately left **unbudgeted**
(analysis §2 / P-3 owns it). A cost of ~0.37 ms per full-volume merge scan reproduces both legs:

| Leg | schedule | merge (52 vs 8 per frame × 0.37 ms) | modelled | measured |
|-----|----------|--------------------------------------|----------|----------|
| L1  | 1.2 ms   | 3.0 ms                               | 4.2 ms   | 6.1 ms   |
| L2  | 8.0 ms   | 19.1 ms                              | 27.1 ms  | 29.2 ms  |

If that attribution is right it has a feedback shape — slower frames land more completions per frame,
which costs more unbudgeted merge time, which slows frames further — i.e. the §3 spiral the budgets
exist to prevent, running through the one pass the budgets do not cover.

⚠️ **This is a model fitted to two runs with one free parameter, not a measurement.** It is consistent
and it predicts both legs, but `WorldFrameProfiler` does not separate `ProcessLightingJobs` from the
schedule scan and the benchmark never enables it. **P9-0 exists to confirm or kill this.**

### F5 — Waste and ordering behave exactly as P-7 predicts

Loading @ 200 m/s waste **9.7 % → 19.6 %**; generation @ 200 m/s **16.8 % → 21.4 %**, which crosses the
20 % threshold and flips that phase's verdict to **ORDERING-BOUND**. Delivering more work through an
unfixed ordering stage produces more discarded work — criterion Q5 pre-committed to recording this
rather than charging it, and the P-8 capture saw the same thing when admission rose. It is corroboration
for P-7, not a cost of P9-0a.

### F6 — Generation-pass completions fell 23 %

Generation @ 200 m/s: **4 353 → 3 353** completions despite a more open gate (57.0 % → 33.0 % closed).
The generation pass runs the same lighting pipeline at a lower frame rate (group avg CPU 15.2 → 23.1 ms),
so the frame-cost regression removes more delivery than the extra admission adds. The loading pass gains
and the generation pass loses; only the loading pass is scored, and this is recorded so that a future
reader does not mistake the headline for a uniform improvement.

---

## Verdict against the pre-committed criteria

Criteria are §2 of the design doc, fixed before the capture.

| #       | Criterion                    | Threshold                                                | Result                                                                                                    |
|---------|------------------------------|----------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| **Q1**  | Visibility budget met        | p50 ≤ `vd × 16 ÷ speed` = **2 560 ms**; partial credit if the shortfall ratio improves ≥ ×1.3 | ⚠️ **PARTIAL** — 3 703 → 2 753 ms, shortfall **1.45× → 1.08×**, an improvement of **×1.35**. Budget still missed |
| **Q2**  | **Frame time holds** ⚠️      | min FPS ≥ ×0.95 **and** avg CPU ≤ ×1.05                  | ❌ **FAIL, decisively** — min FPS **×0.61** (43.7 → 26.6), avg CPU **×4.79** (6.1 → 29.2 ms). Worse than P-8   |
| **Q3a** | Rate lever moved the ceiling | `Quota` share < 90 % **and** completions ≥ ×1.35         | ⚠️ **SPLIT** — quota share **99.6 % → 0.8 %** ✅, completions **×1.21** ❌                                      |
| **Q4**  | Memory holds                 | peak ≤ ×1.10                                             | ✅ **PASS** — ×1.05 (5 071 → 5 320 MB)                                                                        |
| **Q5**  | Waste not scored             | recorded, not charged                                     | ✅ Recorded (F5)                                                                                              |
| **Q6**  | Coverage                     | ≥ 99 %                                                    | ❌ **FAIL for L2** — 97.8 %, and the log states the loading pass generated the remainder (see Limitation 3)     |

**MECHANISM CONFIRMED / NO-GO on the fix.** Q2 fails by a factor, and Q2 was written as a hard gate
that overrides every other criterion precisely so that a throughput-only result could not pass. The
design doc is *not* refuted — its §3.1 mechanism is confirmed by F2, which was the probe's primary
purpose — but the lever it prices is unaffordable in its current form.

---

## Verdict details — what this redirects to

**Nothing ships.** Both legs are settings-only; the shipping configuration remains
`maxLightJobsPerFrame` 24 / `maxMeshRebuildsPerFrame` 11 as OM-1 calibrates them, and the capture
machine must be restored to those values or every later capture inherits the ×2 leg through the warm
settings cache (§7.1).

**Option A′ (raise the caps) is priced and closed as a standalone.** +21 % delivery for ×4.8
main-thread cost, and the ceiling intercepts anything beyond ×2 anyway. It is not re-openable by
re-tuning the cap; it becomes re-openable only if the per-item cost falls.

**Option B2 (deliver-then-refine) is weakened.** The design doc leads with it on the reasoning that
the per-chunk multiplier is spent ahead of first visibility. The stage-latency split says the
recoverable part is small: `populated→lit` + `lit→meshApplied` is 537 ms of L1's 3 703 ms and 508 ms of
L2's 2 753 ms. Eliminating both hops entirely leaves ~3 166 ms against a 2 560 ms budget — B2 cannot
meet the visibility criterion at vd 32 / 200 m/s on its own. It remains defensible on its own product
terms (a dark chunk beats void) but not as the lead throughput lever.

**Option C (cut per-item main-thread cost) is promoted to the gating lever**, and specifically
**P-3, the jobified lighting merge** (analysis §2). Once the ceiling binds, delivery is
`ceiling_ms ÷ per-item cost`, so reducing that cost is the only way to raise throughput at constant
frame time — and F4's model says the merge, not the schedule scan, is where the cost lives. FP-4
deprioritised P-3 on the grounds that throughput was not binding; this capture reverses that for the
high-view-distance regime, as P-8 already did for throughput work generally.

**The next step is P9-0, not another settings leg.** Every remaining question is "where did the 16 ms
go", which settings cannot answer. The run matrix's remaining legs are withdrawn: **L5 (light ×4) is
pointless** because the pass is already ceiling-bound at ×2, and **L3 (mesh ×2) is compromised**
because `MeshSchedule` already reports `InFlightCap` on 294 of 1 005 frames (29 %) at ×2 light, so a
mesh-quota leg would substantially measure the in-flight cap of 20 instead.

---

## Limitations

1. **n = 1 per leg.** Mitigated for the scored phase by F1, which reproduces an independent control
   within 2 % — but single runs remain single runs. The findings rest on deltas of 21–380 %.
2. **L1 was captured while the operator was multitasking** (self-reported). F1 bounds the effect on the
   scored phase; the unscored generation and ensure phases ran 13 % / 24 % cheaper than P-8's control,
   unexplained.
3. **L2 fails the coverage gate (97.8 % vs ≥ 99 %)**, and its loading pass generated the shortfall.
   This *inflates* `enqueue→populated` in L2, so F2's latency improvement is an **understatement**, not
   an overstatement — the direction of the bias is against the finding, which is the safe direction.
4. **L2's transition phase is degenerate**: 0.3 s over 1 frame with all-zero metrics and 5 041 traces
   left `InFlightAtPhaseEnd`, against L1's 5.5 s / 505 frames / 0. It is a non-measurement phase and
   nothing scored depends on it, but it means the drain may not have completed before the loading pass
   began, and it is the source of the 0.0 min-FPS in L2's overall summary. Unexplained; worth a look
   before the next capture.
5. **§F4's cost attribution is a fitted model, not a measurement** — see the warning in that section.
6. **Only the light cap was varied.** The mesh-side ceiling is untested (Limitation: `MeshSchedule`
   stays `Quota`-bound on 70 % of L2's frames, so a second ceiling may sit behind it).
7. **Only view distance 32 was measured.** No vd 26 leg, so the worst frame-cost point in the P-8 sweep
   is uncovered here; the Q2 failure at vd 32 made a vd 26 leg redundant for the verdict.
8. **The trace buffer saturated on both ensure phases**, so those latency percentiles cover a subset.
   Disposition counts and stop-reason tallies remain exact, and the ensure phase carries no verdict.
9. Player builds do not record their commit; the header's commit comes from the shared build GUID, as
   in FP-10 and P-8.

---

## Document History

* **v1.0** — P9-0a captured and reported (2026-08-02). Two settings-only legs on the P-8 build at
  view distance 32. **Confirms** the design doc's rate identity by its own pre-committed mechanism
  (gate closure −32.5 pt, `enqueue→populated` −28.9 %, completions +20.9 %) and **rejects** raising
  the cap as the fix (min FPS ×0.61, avg CPU ×4.79). Establishes that the binding limit moves to the
  8 ms schedule ceiling at ×2, that the frame cost is *not* in the schedule pass, and — as a fitted
  model pending P9-0's attribution — that the unbudgeted `ProcessLightingJobs` merge is where it goes.

---

**Last Updated:** 2026-08-02
