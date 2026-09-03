# P9-2 — Convergent edge-check cascade, IL2CPP Release — 8-run OFF/ON sweep + a corrected vd-32 cap-24 pair

| Field           | Value |
|-----------------|-------|
| **Captured**    | 2026-08-02 19:14 – 21:07 |
| **Branch**      | `feat/world-scaling` (report authored at `3cceab54`) |
| **Commit**      | Build GUID **`fc6ffa2130d646cc94c29146ca0a3802`** — carries the P9-2 fix (`93f8037d`). **All ten runs share this GUID.** Not comparable to P9-0a / P9-1 builds (§7 baseline rule) |
| **Captured by** | `BenchmarkController` — **IL2CPP, Configuration: Release, Player, Burst on**. Ten runs, **n = 1 per configuration**. i9-9900K / 16 threads / 64 GB / D3D11 |
| **Rule**        | **§7.1 v2**, as FP-8, FP-10, P-8, P9-0a and P9-1 |
| **Verdict**     | ✅ **GO — `enableConvergentEdgeCheckCascade` ships default-ON.** At the shipping cap (vd 32, `maxLightJobsPerFrame` 24): lighting amplification **6.12 → 1.86** per delivered chunk, delivery **×2.12**, p50 end-to-end **3 603 → 822 ms** against a 2 560 ms budget (**Q1 met, 0.32× of budget**), and the pipeline spends **less** main thread per second (731 → 630 ms/s) while delivering twice as much. **The rate quota stops being the binding constraint** — `Quota`-bound frames fall 94.3 % → 8.3 % and the panic gate goes from 85 % closed to fully open. Q2 fails as originally written and passes on its reworded per-delivered-chunk form (design §2). **Q4 fails at ×1.15** and is accepted as a recorded cost |

> **Design home:** [`Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](../Design/CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md)
> — this is that document's **phase P9-2**, the Option B1 fix. It is the first P-9 capture that tests a
> change rather than establishing a baseline.

---

## What this measures

`Settings.enableConvergentEdgeCheckCascade` OFF vs ON, same build, one variable. The flag makes
`WorldJobManager.MergeCompletedLightingJob` re-arm the post-generation edge-check cascade on **effect**
(the merge changed light) rather than on **stability** (`IsStable`, which a pass that wrote *nothing* also
satisfies). The round is spent either way; only the propagation — the self `NeedsEdgeCheck` and the four
cardinal `TriggerNeighborEdgeChecks` — is conditional.

**Two capture sessions, and the second one matters.** Runs 1–8 swept vd 10/20/26/32 × OFF/ON but ran at
`maxLightJobsPerFrame` **48**, inherited from P9-0a via the build folder's `settings.json` (§7.0 defect 1).
Runs 9–10 repeat vd 32 at the shipping cap **24**. Everything scored below comes from runs 9–10; the
cap-48 sweep is reported as supporting evidence for the view-distance sweep only.

---

## Raw results — the scored pair (vd 32, loading @ 200 m/s, cap 24)

| Measure | OFF | ON | ratio |
|---------|-----|-----|-------|
| Delivered chunks | 6 861 | **14 518** | ×2.12 |
| Delivered/s | 228 | **482** | ×2.12 |
| **Lighting amplification (total/chunk)** | 6.12 | **1.86** | ÷3.3 |
| Pre-delivery amplification | 3.82 | **1.09** | ÷3.5 |
| Mesh amplification | 1.00 | 1.00 | — |
| Lighting schedules/s | 1 397 | 897 | ×0.64 |
| **p50 `enqueue→MeshApplied`** | 3 603 ms | **822 ms** | ×0.23 |
| `enqueue→populated` | 2 778 ms | 481 ms | ×0.17 |
| `populated→lit` | 594 ms | 200 ms | ×0.34 |
| `lit→meshApplied` | 9.7 ms | 43.1 ms | ×4.4 |
| **`Quota`-bound frames** | 1 776 / 1 884 (94.3 %) | **73 / 883 (8.3 %)** | |
| **Panic gate closed** | 85.2 % | **0.0 %** | |
| Pipeline main-thread ms/s | 731.0 | **630.5** | ×0.86 |
| **Pipeline ms per delivered chunk** | 3.21 | **1.31** | ÷2.4 |
| Avg CPU frame | 17.6 ms | 34.5 ms | ×1.96 |
| Min wall FPS | 24.3 | 20.5 | ×0.84 |
| Peak total memory (run) | 4 950 MB | 5 703 MB | **×1.15** |
| Waste | 12.0 % | 35.7 % | |
| Tour coverage | 97.8 % ❌ | **100.0 %** ✅ | |

### The cap-48 view-distance sweep (runs 1–8, supporting evidence only)

Loading @ 200 m/s. Frame-time columns are **not** scoreable (see Limitation 1); amplification and latency
are, since neither depends on how the per-second work is packed into frames.

| vd | delivered OFF→ON | amp OFF→ON | pre-delivery | p50 e2e OFF→ON | budget | ON vs budget |
|----|------------------|------------|--------------|----------------|--------|--------------|
| 10 | 5 809 → 9 106 | 6.93 → **1.44** | 3.84 → 1.19 | 802 → **358 ms** | 800 ms | **0.45×** ✅ |
| 20 | 7 134 → 15 623 | 6.42 → **1.49** | 4.03 → 1.21 | 2 008 → **573 ms** | 1 600 ms | **0.36×** ✅ |
| 26 | 6 955 → 11 704 | 6.39 → **1.83** | 3.94 → 1.27 | 2 854 → **1 225 ms** | 2 080 ms | **0.59×** ✅ |
| 32 | 6 590 → 11 430 | 6.54 → **1.67** | 4.01 → 1.16 | 3 508 → **1 792 ms** | 2 560 ms | **0.70×** ✅ |

### Generation pass @ 10 m/s — the high-amplification regime

Pre-delivery amplification, the figure P9-1 measured at 6.59–6.76:

| vd | OFF | ON |
|----|-----|-----|
| 10 | 6.36 | 2.63 |
| 20 | 6.62 | 3.22 |
| 26 | 6.66 | 3.47 |
| 32 (cap 24) | 6.72 | **3.54** |

---

## Findings

### F1 — The redundancy was real, and it was most of the multiplier

Lighting amplification falls **6.12 → 1.86** total and **3.82 → 1.09** pre-delivery at the shipping cap.
A pre-delivery figure of 1.09 means a chunk now reaches the player after **essentially its initial lighting
pass alone**. Post-delivery correction collapses with it (the `no live trace` bucket: 11 973 → 389), so the
work was not deferred past delivery — it stopped happening, because it was recomputing an unchanged result.

This closes the condition §6's Option B1 carried from the start: *"conditional on finding work that
recomputes an unchanged result."* P9-1 sized the multiplier and explicitly could not show any of it was
redundant. It was.

### F2 — The rate quota stops being the binding constraint ⭐

This is the larger result. At vd 32 / 200 m/s the light schedule goes from **`Quota` on 94.3 % of frames**
to **`Quota` on 8.3 %**, with `OutOfWork` dominant (699 / 883). Utilisation is 63 % of a cap the pass no
longer wants to spend. The panic gate — closed on 85.2 % of OFF frames because it keys on the lighting
backlog — is **fully open** on the ON leg, and `AbandonedBeforeAdmission` falls 21 250 → 4 858.

P-9 opened on the premise that `cap × 60` is an absolute throughput ceiling with no view-distance term.
That premise is confirmed and untouched; what changed is that **the pipeline no longer operates against it**
at vd 32. B1 did not raise the ceiling — it reduced demand below it.

### F3 — Q1 is met at every view distance, on an absolute reading

The visibility budget `vd × 16 ÷ speed` has been missed by 1.4–1.6× at vd 20/26/32 since P-8. Every ON leg
meets it, and the scored pair meets it by **3.1×** (822 ms against 2 560 ms). This reading does not depend
on the OFF leg: it is an absolute latency against an absolute budget, measured on a **100 %-coverage** run.

### F4 — The pipeline got cheaper per second AND per delivered chunk; the frame rate still fell

Instrumented pipeline cost falls **731.0 → 630.5 ms/s** in absolute terms while delivering 2.12× more, i.e.
**3.21 → 1.31 ms per delivered chunk (÷2.4)**. Avg CPU per *frame* nonetheless rises 17.6 → 34.5 ms, because
the frame count halves (1 884 → 883 in 30 s): delivering 2.1× more chunks costs downstream work outside the
instrumented region — `Tick` rises 45 → 109 ms/s, and the un-instrumented remainder (rendering, upload)
rises 269 → 370 ms/s.

**This is the opposite shape to P-8's failure**, where main-thread milliseconds were spent to buy admission.
Here the lever's own cost fell and the frame cost is a consequence of the extra delivery. Q2 was written for
the P-8 shape and mis-scores this one; design §2 reworks it accordingly.

### F5 — The cap-48 session's frame-time numbers were contaminated; its throughput numbers were not

Predicted before runs 9–10 ran: the cap-48 OFF leg was `Ceiling`-bound at 1 437 schedules/s, and cap 24's
quota rate is 1 440/s, so the cap-24 OFF leg should reproduce it. **Half right.** Delivery (6 861 vs 6 590)
and amplification (6.12 vs 6.54) reproduced; **frame time did not** — 17.6 vs 34.9 ms avg CPU. Instrumented
work per second was nearly identical (731 vs 749 ms/s), packed into 2.2× more frames (11.7 vs 26.6 ms/frame).

That is P9-1 §F4's "repacked into 3.2× fewer, 3.2× longer frames" reproduced on a third build, and it means
**§7.0 defect 1 invalidated exactly the frame-time axis and nothing else.** The corrected pair was necessary.

### F6 — Q4 fails: peak memory ×1.15

4 950 → 5 703 MB. The growth is **native + reserved** (2 995 → 3 847 MB native, 3 715 → 4 491 MB reserved);
managed is flat at ~1.9 GB. Consistent with more resident mesh buffers from 2.12× delivery. This exceeds the
≤×1.10 threshold and the 5 GB marker FP-10 left open at vd 32.

**Accepted as a recorded cost** (product decision, 2026-08-02): vd 32 is a stress configuration well above
the intended 12–15 default, the driver is the extra delivery that is the point of the change, and the
capture machine has 64 GB. ⚠️ **Nobody has measured where the vd-32 memory ceiling is on a smaller machine**,
and this raises the number that question is about. It belongs to whoever picks up the memory item.

---

## Verdict against the pre-committed criteria

| # | Criterion | Result |
|---|-----------|--------|
| **Q1** | Visibility budget ⭐ | ✅ **MET at every view distance.** Scored pair: 822 ms against 2 560 ms = **0.32×**. Sweep: 0.45× / 0.36× / 0.59× / 0.70× at vd 10/20/26/32 |
| **Q2** | Frame time (as written) | ❌ avg CPU ×1.96, min FPS ×0.84 |
| **Q2′** | Frame time per delivered chunk (reworded, design §2) | ✅ **3.21 → 1.31 ms per delivered chunk (÷2.4)**; absolute pipeline cost ×0.86 |
| **Q3c** | Amplification lever moved the divisor ⭐ | ✅ (b) amp 6.12 → 1.86; (c) delivered/s ×2.12, matching the reciprocal; (d) identity closes in **both** legs (1 397 ÷ 6.12 = 228 vs 228; 897 ÷ 1.86 = 482 vs 482). (a) inapplicable — the pass left the rate-bound regime entirely (F2) |
| **Q4** | Memory ≤ ×1.10 | ❌ **×1.15** — accepted as a recorded cost, see F6 |
| **Q5** | Waste not scored | Recorded: 12.0 % → 35.7 % |
| **Q6** | Coverage ≥ 99 % | ON leg **100.0 %** ✅; OFF leg 97.8 % ❌ (flatters the OFF→ON delta, but Q1's ON reading is absolute and unaffected) |
| **Q7** | Corrections still converge | ✅ **Confirmed in-game by the product owner**: chunk generation lights correctly while flying, and **RGB blocklight converges and mixes across chunk borders** — the most defect-prone path in the engine (Bugs 12/16/17/18, fidelity C10/C12). Suites additionally green at 374 baselines |

---

## Limitations

1. **Runs 1–8 ran at `maxLightJobsPerFrame` 48, not 24** (§7.0 defect 1). Their **frame-time, FPS and
   memory** columns are void — F5 shows the cap changes frame packing without changing per-second work.
   Their amplification, delivery and latency columns stand, which is why the view-distance sweep is
   reported for those axes only.
2. **n = 1 per configuration**, and the scored pair is a single vd. vd 20/26 at the shipping cap are
   inferred from the cap-48 sweep's throughput axis plus the vd-32 corrected pair, not measured.
3. **The OFF leg of the scored pair misses Q6** (97.8 %), so its `enqueue→populated` is inflated and the
   OFF→ON latency delta is overstated. Q1's verdict does not rest on it.
4. **The benchmark report does not print `enableConvergentEdgeCheckCascade`** (§7.0 defect 2), so no run
   here self-documents its flag state. The 3.3× amplification shift is the evidence that the ON legs were
   ON — a positive result is self-verifying in a way a null result would not have been.
5. **Q4's acceptance is a product decision, not a measurement.** The vd-32 memory ceiling on a
   memory-constrained device remains unmeasured, and this change raises the peak by 750 MB.
6. Player builds do not record their commit; the header's commit comes from the shared build GUID.

---

## Document History

* **v1.0** — P9-2 captured and reported (2026-08-02). First P-9 capture to test a fix. **Option B1's
  standing condition is discharged**: the post-generation edge-check cascade was re-arming on `IsStable`,
  which a pass that wrote nothing also satisfies, and removing that redundancy cuts lighting amplification
  **6.12 → 1.86** per delivered chunk (pre-delivery **3.82 → 1.09**) at the shipping cap. Delivery rises
  ×2.12, p50 end-to-end falls to **0.32× of the visibility budget**, and the pipeline spends **less** main
  thread per second while delivering twice as much. **The rate quota stops binding** — `Quota` frames
  94.3 % → 8.3 %, panic gate 85 % closed → fully open — so P-9's ceiling is relieved at vd 32 by reducing
  demand rather than raising the cap. Q2 fails as written and passes reworded per delivered chunk (÷2.4);
  Q4 fails at ×1.15 and is accepted as a recorded cost; Q7 confirmed in-game including cross-border RGB.
  Also reproduces P9-1 §F4's frame-repacking effect on a third build, which is what invalidated the
  cap-48 session's frame-time axis and forced the corrected vd-32 pair.

---

**Last Updated:** 2026-08-02
