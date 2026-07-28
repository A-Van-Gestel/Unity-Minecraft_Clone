# FP-4 — Flight-Profile Capture (Pipeline Telemetry), IL2CPP — view-distance sweep

| Field           | Value                                                                                                                             |
|-----------------|-----------------------------------------------------------------------------------------------------------------------------------|
| **Captured**    | 2026-07-27 21:48:44 (vd 20), 2026-07-28 18:02:55 (vd 5), 2026-07-28 18:06:42 (vd 10)                                              |
| **Branch**      | `feat/world-scaling`                                                                                                              |
| **Commit**      | `73de6511` ("Added: FP-3 report section + verdict rule…"), **clean tree** — `UniversalRenderPipelineGlobalSettings.asset` and `ProjectSettings.asset` showed as modified in `git status` but are byte-identical to `HEAD` (stat-dirty only, from Unity touching them). All three runs are reproducible from that commit alone. |
| **Captured by** | `BenchmarkController` — **IL2CPP Development Build, Player, Burst on**. Three runs at **viewDistance 5 / 10 / 20**, same build GUID `33b0ae23ff1d4bfaaca6ca95f44e728e`, same machine, same session per run |
| **Design doc**  | [`Design/FLIGHT_PROFILE_CAPTURE.md`](../Design/FLIGHT_PROFILE_CAPTURE.md) v1.8 — this report is FP-4                               |
| **Verdict**     | **ORDERING-BOUND at every view distance** (waste 22.9–61.2 %, above the 20 % threshold in **all 9** loading phases). **ADMISSION-BOUND only from viewDistance ≥ 10**, caused by an unscaled panic-gate threshold (F5). Never throughput-bound, never readiness-bound. |

> **GO/NO-GO does not apply.** FP ships no behavior change (§1 non-goals, §9 limitation 5); the deliverable is
> the **regime verdict**. Per the design doc, a capture that produces numbers but no verdict has failed.

> **Supersedes an uncommitted single-run draft** dated 2026-07-27 that covered only the viewDistance-20 leg.
> That draft never entered the record. Two of its claims are corrected here and both corrections are stated
> explicitly rather than silently absorbed — see **C1** and **C2** under Findings.

---

## What this measures — and what it does NOT

**Measures.** End-to-end chunk latency split into three hops (`enqueue→populated→lit→meshApplied`), terminal
disposition of every traced chunk, per-frame stop reasons for four pipeline passes, and panic-gate closure
rate — across a scripted speed sweep, now repeated at three view distances.

**Does NOT measure.**

1. **Any pass outside `PipelinePass`** — decisively, there is **no admission pass**. The P-4 §3.5 panic gate
   withholds admissions at `World.DrainGenerationRequests`, instrumented only as a per-frame boolean, never as
   a stop reason. See F1.
2. **Light-job completion.** `PipelinePass` has `GenerationProcess` and `MeshProcess` but no light-process
   pass, so the largest hop in the generation sweep (`populated→lit`) has no admission instrument inside it.
3. **Where a chunk was relative to the player.** Player position is never sampled, so ordering is read from
   the waste fraction, not from geometry. F6 reconstructs the geometric consequence arithmetically instead.
4. **Frame-cost attribution.** This is a pipeline-flow instrument; it does not say what a frame was bound by.

---

## Methodology

Three IL2CPP Development Build runs, same build and machine, one view-distance setting each. This is a
**characterization sweep**, not a change comparison — no baseline delta, no regression budget.

| | **Run A** | **Run B** | **Run C** |
|---|---|---|---|
| viewDistance | **5 (default)** | **10** | **20** |
| LoadDistance (`vd + DATA_LOAD_BUFFER 3`) | 8 | 13 | 23 |
| Resident square | 17×17 = **289** | 27×27 = **729** | 47×47 = **2 209** |
| Ring diameter | 272 m | 432 m | 752 m |
| Backlog 256 as % of resident | **88.6 %** | **35.1 %** | **11.6 %** |
| Generation waypoints | 12 | 6 | **4** |
| Loading waypoints | 12 | 12 | 12 |
| Avg CPU / frame | 1.2 ms | 2.2 ms | 6.6 ms |
| Source log | `BenchmarkRun_2026-07-28_18-02-55.log` | `BenchmarkRun_2026-07-28_18-06-42.log` | `BenchmarkRun_2026-07-27_21-48-44.log` |

**Confound, stated up front: the three runs do not share a generation route.** Generation waypoint counts are
12 / 6 / 4, so the generation pass flies a different path in each run, and cross-run *generation* comparisons
are confounded. **The loading pass uses 12 waypoints in all three runs and is directly comparable** — every
cross-view-distance claim below that carries weight is drawn from the loading pass. F2 is the exception, and
it survives the confound for a reason given there.

Percentiles are nearest-rank (`TraceStatistics`, pinned by B9); the verdict rule is `PipelineRegimeVerdict`
(pinned by B10). Both pure, both green in `Validate All` 358/358 at this commit.

---

## The verdict

### As produced by §7.1 v1, verbatim — all three runs

| Loading phase | Run A (vd 5) | Run B (vd 10) | Run C (vd 20) |
|---|---|---|---|
| 50 m/s  | Healthy + **ORDERING-BOUND** (33.1 %) | Healthy + **ORDERING-BOUND** (22.9 %) | Healthy + **ORDERING-BOUND** (27.2 %) |
| 100 m/s | Healthy + **ORDERING-BOUND** (35.3 %) | Healthy + **ORDERING-BOUND** (23.5 %) | Healthy + **ORDERING-BOUND** (29.8 %) |
| 200 m/s | Healthy + **ORDERING-BOUND** (37.1 %) | Healthy + **ORDERING-BOUND** (46.4 %) | Healthy + **ORDERING-BOUND** (61.2 %) |

**9 of 9 loading phases are ordering-bound.** The primary axis reads *Healthy* in all of them, which F1 shows
is an artifact of the rule rather than a finding.

### Arbitration of the reported symptom

**Ordering-bound is universal and intrinsic.** It fires at the **default** view distance, at 33–37 % waste,
with the panic gate **never closing once** across all nine of Run A's phases. Ordering is therefore *not* a
consequence of the gate, not a large-view-distance artifact, and not a stress-configuration curiosity — it is
how the pipeline behaves out of the box.

**Admission-bound is conditional.** It appears only from viewDistance ≥ 10, and F5 identifies the cause: a
panic-gate threshold that does not scale with view distance.

Both other regimes are ruled out by direct measurement in every run: `InFlightCap` never exceeds 45 frames in
any loading phase (≤ 0.6 %), and `AllDeclined` peaks at 1 954 frames (7.8 %, Run A 200 m/s) — never close to a
plurality.

---

## Findings

### C1 — Correction: waste does **not** rise monotonically with view distance

The single-run draft inferred "waste rises with view distance" from Run C alone. With three legs that is
**false at moderate speed**:

| Loading | vd 5 | vd 10 | vd 20 |
|---|---|---|---|
| 50 m/s  | **33.1 %** | 22.9 % | 27.2 % |
| 100 m/s | **35.3 %** | 23.5 % | 29.8 % |
| 200 m/s | 37.1 % | 46.4 % | **61.2 %** |

At 50 and 100 m/s the **default** view distance is the *worst* of the three. Only at 200 m/s does waste rise
with view distance. The unifying relation is `waste ≈ latency ÷ residence-time`, where residence ≈ ring
diameter ÷ speed: a small ring gives a chunk less time to be useful, so vd 5 suffers at moderate speed; a
large ring is more forgiving until the gate inflates latency and flips it at 200 m/s. Stated as a heuristic
that explains the direction — it is not a fitted law.

### C2 — Correction: the capacity-model claim, and the reason it was wrong

The draft reported the §8 Q1 trace-capacity model under-predicting by ~46 %. It does not. The draft compared
Run C's 21 848 traces against the design doc's `LoadDistance = 12` table row; **Run C ran at LoadDistance 23**,
where `EstimateTraceCapacity(23, 200, 30)` = **29 751** — ~27 % headroom, no saturation banner, model correct.
The error was possible only because **`LoadDistance` is not printed in the capture report** (L3).

### F1 — §7.1 v1's plurality rule is diluted by two passes that cannot vote

`GenerationProcess` and `MeshProcess` are **ceiling-only** passes: they can emit `OutOfWork` or `Ceiling` and
nothing else. Across all three runs — 25 phases × 2 passes × 3 reasons — **every one of the 150 cells is
zero**. §7.1 sums reasons across all four passes, so two passes that can vote for one outcome are added to two
contesting five, and they contribute ~100 % `OutOfWork`.

Run C's 200 m/s phase came down to **68 frames out of 27 744** (OutOfWork 50.0 % vs Quota 49.8 %) and printed
*Healthy*. Restricted to the passes that actually hold an admission budget:

| Loading 200 m/s | dominant, all four passes | dominant, scheduling passes only |
|---|---|---|
| Run A (vd 5)  | OutOfWork | OutOfWork 70.1 % — genuinely healthy on this axis |
| Run B (vd 10) | OutOfWork | **Quota 51.7 %** |
| Run C (vd 20) | OutOfWork (by 0.25 %) | **Quota 99.5 %** |

The rule was not fed bad data; it aggregated good data wrongly. **§7.2 is what made this recoverable** — the
correction needed only the printed tallies, no re-capture.

**Not fixed here** (§9.5, and amending a pre-committed rule after seeing its result would defeat its purpose).
A §7.1 v2 should restrict the plurality to passes capable of expressing the contested reason, or report a
per-pass regime vector. Whoever writes it must bump `RULE_VERSION`.

### F2 — The generation pipeline is **lockstep**: each chunk advances one stage per boundary crossing

Expressing every generation-pass hop as a multiple of the boundary-crossing interval (`ChunkWidth / speed`,
i.e. 16 m of travel) collapses **12 measurements across three view distances and five speeds** onto one
structure:

| Run | Speed | `enq→pop` | `pop→lit` | `lit→mesh` | **Total** |
|---|---|---|---|---|---|
| A (vd 5) | 10 m/s | 0.01 | **2.02** | **0.97** | **3.01** |
| A | 20 m/s | 0.03 | 2.05 | 0.95 | 3.03 |
| A | 50 m/s | 0.09 | 2.14 | 0.85 | 3.08 |
| A | 100 m/s | 0.15 | 2.28 | 0.72 | 3.16 |
| A | 200 m/s | 0.30 | 2.67 | 0.42 | 3.40 |
| B (vd 10) | 10 m/s | 0.02 | 2.05 | 0.95 | 3.02 |
| B | 100 m/s | 0.21 | 2.52 | 0.55 | 3.28 |
| C (vd 20) | 10 m/s | 0.04 | 2.11 | 0.90 | 3.05 |
| C | 50 m/s | 0.19 | 2.56 | 0.58 | 3.33 |

**`populated→lit` costs almost exactly 2 crossings and `lit→meshApplied` almost exactly 1, for a total of 3** —
and the total is **invariant to view distance** across a 7.6× change in resident-set size (3.01 / 3.02 / 3.05
at 10 m/s). Latency *falls* as speed rises, which no throughput limit produces.

This is why the confound in Methodology does not sink F2: the finding is that latency is a fixed multiple of a
distance interval, and it reproduces at three view distances and five speeds on three *different* routes. A
route artifact would not survive that.

**Interpretation (mechanism strongly indicated, not directly instrumented):** `CheckViewDistance` rebuilds the
request queue on each 16 m boundary crossing, and stages appear to advance in step with it. The integer 2 : 1
split is the evidence — a throughput-limited pipeline has no reason to produce whole numbers. The residual
creep (3.01 → 3.40 as speed rises) is genuine throughput pressure appearing *on top of* the lockstep floor.

**Consequence: the generation pass has a latency floor that no amount of extra throughput can lower.** At 10 m/s
that floor is 4.8 s. Anyone optimizing generation throughput against that figure would be optimizing the wrong
thing.

### F3 — The loading pass: two different stories either side of viewDistance 10

At vd 5 the pipeline is fast and still wasteful. At vd 20 it is slow *and* wasteful, and the gate is why:

| Loading 200 m/s | vd 5 | vd 10 | vd 20 |
|---|---|---|---|
| `enq→pop` p50 | 3.9 ms | 463.9 ms | **1 822.7 ms** |
| `pop→lit` p50 | 240.6 ms | 432.9 ms | 579.3 ms |
| `lit→mesh` p50 | 64.0 ms | 3.1 ms | 3.2 ms |
| **total p50** | **346.6 ms** | **982.2 ms** | **2 489.6 ms** |
| panic gate closed | **0.0 %** | 92.8 % | 96.4 % |
| waste | 37.1 % | 46.4 % | 61.2 % |

Admission cost goes from **1 %** of total latency at vd 5 to **73 %** at vd 20. That entire swing is the gate.
Meshing is never implicated anywhere: `lit→meshApplied` is 3–64 ms in every loading phase.

At vd 20, 200 m/s the pipeline starts **728 chunks/s and delivers 219** — about 3.3 units of work per chunk
shipped. At vd 5 it starts 242/s and delivers 148, a far healthier 1.6.

### F4 — A thin catastrophic tail, worst at the *default* view distance

| | p50 | p95 | **p99** | max |
|---|---|---|---|---|
| Run C, generation 20 m/s | 2 488 ms | 2 683 ms | **24 082 ms** | 29 706 ms |
| Run B, generation 50 m/s | 999 ms | 1 961 ms | **6 128 ms** | 8 055 ms |
| Run A, generation 50 m/s | 989 ms | 2 246 ms | **4 196 ms** | 5 151 ms |

At Run C's 20 m/s, ~95 % of chunks land in a tight ~2.5 s cohort and **~1 % take 24–30 s**. A player sees a
mostly-complete world with occasional holes that persist for half a minute. Tail attribution is **not
resolvable from aggregates** — in Run C the `populated→lit` and `lit→meshApplied` p99s sum past the phase's
own maximum end-to-end latency, proving they belong to *different* chunks, i.e. two distinct stall
populations. Separating them needs the per-chunk CSV export (§7.2's stated limitation, the v3+ roadmap item).
This is now a recorded demand case for that item.

### F5 — CONFIRMED: the panic gate's threshold does not scale with view distance

The single-run draft predicted this arithmetically. **Runs A and B test it, and it holds.**

`GenerationPanicGate` closes when the lighting ready-count reaches `panicGateCloseThreshold` — a **fixed user
setting (default 256, reopen 128)**, read at `World.cs:3363` with no view-distance term in its derivation. The
quantity it guards grows with the **square** of view distance:

| viewDistance | Resident square | Backlog 256 as % of it | **Gate closed, loading 200 m/s** |
|---|---|---|---|
| **5 (default)** | 289 | 88.6 % | **0.0 %** (0 of 25 086 frames) |
| **10** | 729 | 35.1 % | **92.8 %** (9 057 of 9 763) |
| **20** | 2 209 | 11.6 % | **96.4 %** (6 687 of 6 936) |

At the default view distance the gate **never closes once in the entire run** — 9 phases, ~380 000 sampled
frames, zero closures. Closing would require 256 of 289 resident chunks (88.6 %) simultaneously in the
lighting ready set. At vd 20 the same bar is 11.6 % and is cleared constantly.

**This is a design gap, not a tuning complaint.** A hysteresis band pinned to an absolute count cannot hold its
intended meaning across a setting that moves the guarded population by 7.6×. The same constant is an
unreachable emergency brake at vd 5 and a near-permanent throttle at vd 20. Candidate fix (own design pass):
derive the thresholds from the resident square, or a fraction of it, so the band means the same thing
everywhere.

Note the transition is **sharp, not gradual** — vd 10 already sits at 92.8 % at 200 m/s while showing 0.0 % at
100 m/s. The gate behaves as a tipping point, which is what a fixed threshold against a scaling quantity
predicts.

### F6 — The visibility criterion: `latency × speed` vs `viewDistance × 16`

**The finding that ties the telemetry to what the operator actually saw.** A chunk is requested when it enters
load range and must render before the player reaches it; the margin is `viewDistance` chunks. Expressing median
latency as *chunks of travel* and comparing it to the view distance itself:

| Loading | vd 5 | vd 10 | vd 20 |
|---|---|---|---|
| 50 m/s  | 4.3 ch = **0.86×VD** | 4.4 ch = 0.44×VD | 6.3 ch = 0.31×VD |
| 100 m/s | 4.3 ch = **0.86×VD** | 4.5 ch = 0.45×VD | 13.1 ch = 0.65×VD |
| **200 m/s** | 4.3 ch = **0.87×VD** | 12.3 ch = **1.23×VD** | 31.1 ch = **1.56×VD** |

**The ratio crosses 1.0 between vd 5 and vd 10 at 200 m/s — exactly where the operator's eye reported the
transition.** Reported independently of the numbers, before this analysis:

> *"at the default 5 render distance, all chunks visually loaded fine; at 10, few chunks already started
> visually lagging behind; at 20, this was even worse and at the final 200 m/s stage, only a handful of chunks
> even rendered."*

- **vd 5 → 0.87×VD**: chunks arrive just *inside* the view boundary. "Loaded fine." ✔
- **vd 10 → 1.23×VD**: chunks arrive just *outside* it. "Few chunks lagging." ✔
- **vd 20 → 1.56×VD**: chunks arrive **11 chunks beyond** the view boundary — the player has flown 31 chunks
  since the request. "Only a handful even rendered." ✔

Every generation-pass cell is ≤ 0.68×VD, and the operator reported no generation-pass problem. The criterion
separates the reported-good from the reported-bad configurations with no exceptions in 21 cells.

**Why this matters more than the waste percentage.** Waste says work was thrown away; this says whether the
*player sees a hole*, and it is directly actionable: it is a budget. **A chunk must complete within
`viewDistance × 16 ÷ speed` seconds.** At vd 20 and 200 m/s that budget is 1.6 s and the pipeline spends 2.5 s.
It also explains why raising view distance hurts *twice*: the budget grows linearly with view distance while
latency grows faster, because the gate engages.

**Caveat:** the criterion uses the median. A configuration passing at p50 still shows holes from its tail (F4).
Treat 1.0 as the onset of *systematic* failure, not the onset of any visible artifact.

### F7 — BUG: telemetry phases leak across benchmark runs in one process

**Found in the data, confirmed in code.** Run B's log contains **17 telemetry phases**: the 9 phases of Run A
verbatim, followed by Run B's genuine 8. Its frame-metrics tables correctly show 8.

`PipelineTelemetry.s_completedPhases` is cleared **only** in `DomainReset`
(`[RuntimeInitializeOnLoadMethod]`, `PipelineTelemetry.cs:305`) — i.e. once per play-mode entry or player
start. `BenchmarkController` sets `PipelineTelemetry.Enabled = true` at run start
(`BenchmarkController.cs:196`) but never clears the phase list, while `_metricsCollector.StartRecording()`
resets its own. **The two recorders therefore disagree at the run boundary**, so a second benchmark run in the
same process reports the previous run's phases as if they were its own.

This is the run-level instance of exactly the desync that FP-3's paired `BeginPhaseBoth`/`EndPhaseBoth` was
introduced to prevent at the *phase* level. Fix is one line — clear `s_completedPhases` where `Enabled` is set
true — but it is a behavior change to the instrument and belongs in its own commit with a suite pass.

**Impact on this report: none.** Run B's stale block is byte-identical to Run A and was identified and excluded
before analysis. Every Run B figure here comes from its genuine 8 phases. Appendix B marks the exclusion
in place.

---

## Capture limitations

**L1 — Generation sweeps are incomplete, and unequally so.** Waypoint counts differ (12 / 6 / 4), so the routes
differ *and* the sweeps terminate at different points: Run A completed all 5 speeds (200 m/s ran 19.7 s of 30 s),
Run B reached 100 m/s (19.8 s of 30 s), Run C reached 100 m/s at only 0.7 s / 17 frames / 8 chunks. **Run C's
100 m/s row is not a result** — its 95.7 % `InFlightAtPhaseEnd` is an artifact of the cutoff. Cross-run
generation comparisons are confounded; the loading pass (12 waypoints everywhere) is clean.

**L2 — `LoadDistance` is not printed in the report.** It is the input to the capacity estimate and to any
reproduction, and its absence directly caused C2's retracted claim. **Highest-value fix in this list**;
`_loadDistanceForCapture` is already held at `BenchmarkController.cs:195`.

**L3 — Minor hop-count discrepancies.** `enq→pop` n exceeds `pop→lit` n by 1–595 in some loading phases
(largest: Run B 200 m/s, 5 759 vs 5 164). Plausibly chunks loaded from disk with lighting already resolved, so
`StampLit` never fires. **Unverified.** Immaterial to the percentiles but worth closing so it is not
rediscovered as a mystery.

**L4 — Run A's Transition phase is degenerate.** 0.1 s / 11 frames, and its memory row reads 0.0 MB — too short
to sample. This propagates a `Min Wall FPS 0.0` into Run A's Overall Summary. A reporting artifact, not a
measurement.

**L5 — Single run per view distance.** No variance estimate. Cross-leg consistency (F2's 12 points, ±7 %) is
the stability evidence available.

**L6 — Gate closure is sampled per frame, not attributed to chunks.** F3's causal chain (quota → backlog →
gate → admission latency) is assembled from co-located measurements, not from a per-chunk record of *why* a
given chunk waited.

---

## What this licenses next (ranked)

FP arbitrates; it does not fix (§9.5). Each item needs its own design pass.

> **This list is a snapshot, not the backlog.** This report is append-only and cannot be kept current, so the
> ranking is *maintained* in three places that can be: engine items as **`P-7`** (ordering) and **`P-8`**
> (gate scaling) in [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §6](../Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md)
> and the master backlog [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md);
> instrument items as **FP-5 / FP-6** in
> [`FLIGHT_PROFILE_CAPTURE.md` §7.3–§7.4](../Design/FLIGHT_PROFILE_CAPTURE.md). If those disagree with the
> list below, they are right and this is stale.

1. **Ordering — highest value, and now known to be universal.** Waste exceeds the 20 % threshold in **all nine**
   loading phases including the default view distance with the gate never closing. This is intrinsic pipeline
   behavior, not a stress artifact, and it is the largest measured inefficiency in the engine.
2. **Scale the panic-gate thresholds with view distance (F5).** Confirmed by measurement, not just arithmetic:
   0 % closure at vd 5 versus 92.8 % at vd 10. A fixed 256/128 band means "unreachable" at the default and
   "always on" at vd 20. This is a correctness-of-intent fix, and it is what makes high view distances
   disproportionately worse.
3. **Adopt F6's budget as the acceptance criterion for both.** `latency ≤ viewDistance × 16 ÷ speed` is
   falsifiable, matches independent visual observation across three legs, and gives ordering work a target
   number instead of "less waste".
4. **Fix F7** (telemetry phase leak) before the next capture, or multi-run sessions will keep producing
   reports containing other runs' data.
5. **Print `LoadDistance`** (L2) — trivial, and it caused this report's one retracted claim.
6. **§7.1 v2** — fix the plurality dilution (F1) so future captures need no manual recomputation.
7. **Per-chunk CSV export (v3+)** — F4's two stall populations cannot be separated without it.

Explicitly **not** licensed: any in-flight-cap or readiness work. Both regimes were tested for at three view
distances and are absent in all of them.

---

# Appendix A — Run A, viewDistance 5 (default), verbatim

Source: `BenchmarkRun_2026-07-28_18-02-55.log`

````
--- BENCHMARK RUN PERFORMANCE REPORT ---
Date:                2026-07-28 18:02:55
Total runtime:       3m 50s

=== System ===
CPU:            Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz
CPU threads:    16
CPU base MHz:   3600
RAM:            65 381 MB
OS:             Windows 10  (10.0.19045) 64bit
Graphics API:   Direct3D11

=== Build ===
Unity:          6000.5.5f1
Platform:       WindowsPlayer
Mode:           Player
Backend:        IL2CPP
Build GUID:     33b0ae23ff1d4bfaaca6ca95f44e728e
Git commit:     (player build — record manually)

=== Burst ===
Compilation:    Enabled
Safety checks:  Enabled
Synchronous:    Disabled

=== Configuration ===
Region size:         100 chunks (configured: 64, auto-scaled)
Phase duration:      30 s
Generation speeds:   10; 20; 50; 100; 200 m/s
Loading speeds:      50; 100; 200 m/s
Generation WPs:      12
Loading WPs:         12
VSync override:      Forced Off (was: On)
FPS cap override:    Uncapped (was: Uncapped)

=== Overall Summary ===
Total phases:        9
Total samples:       4 496
Wall-clock runtime:  3m 50s
Phase duration sum:  3m 49s
Avg CPU time:        1,2 ms
Peak CPU time:       28,3 ms
Avg Wall time:       1,4 ms
Avg GC alloc:        17,3 KB
Avg Wall FPS:        1554,8
Min Wall FPS:        0,0
Avg CPU FPS:         2204,7
Min CPU FPS:         0,0
Avg Total Memory:    670,2 MB
Peak Total Memory:   857,0 MB

=== Generation Pass — Performance ===
  Phase              Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  10 m/s               30,0 s   0,4 ms    2,5 ms    0,5 ms     2,7 ms
  20 m/s               30,0 s   0,5 ms    3,2 ms    0,7 ms     3,5 ms
  50 m/s               30,0 s   0,8 ms    4,6 ms    1,0 ms     4,8 ms
  100 m/s              30,0 s   1,2 ms    4,1 ms    1,4 ms     4,4 ms
  200 m/s              19,7 s   5,9 ms   28,3 ms    6,2 ms    29,1 ms
  -- Group Total --    2m 19s   1,4 ms   28,3 ms

=== Generation Pass — FPS ===
  Phase              Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  10 m/s                   2402,4         372,8       3405,1        407,0
  20 m/s                   2010,1         288,9       3109,0        317,0
  50 m/s                   1706,0         209,1       2468,1        218,4
  100 m/s                  1189,2         229,8       1598,6        241,8
  200 m/s                   262,7          34,4        286,0         35,3
  -- Group Total --        1621,2          34,4

=== Generation Pass — Memory ===
  Phase    Avg Total  Peak Total  Avg Native  Peak Native  Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  10 m/s    511,8 MB    554,1 MB    346,8 MB     391,9 MB  640,0 MB   640,0 MB     164,9 MB      169,9 MB
  20 m/s    553,3 MB    596,4 MB    381,3 MB     419,4 MB  674,2 MB   704,0 MB     172,0 MB      177,6 MB
  50 m/s    605,8 MB    625,7 MB    425,7 MB     438,9 MB  698,7 MB   704,0 MB     180,2 MB      189,4 MB
  100 m/s   654,0 MB    742,4 MB    446,5 MB     525,0 MB  720,0 MB   752,0 MB     207,6 MB      223,0 MB
  200 m/s   745,0 MB    774,9 MB    528,6 MB     557,0 MB  826,4 MB   832,0 MB     216,4 MB      223,0 MB

=== Generation Pass — GC Allocations ===
  Phase    Avg GC/frame  Peak GC/frame
  10 m/s         2,2 KB        57,5 KB
  20 m/s         3,7 KB        46,1 KB
  50 m/s        10,8 KB        69,1 KB
  100 m/s       22,3 KB        58,0 KB
  200 m/s      136,2 KB       528,0 KB

=== Transition — Performance ===
  Phase                  Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  Drain + Save + Unload     0,1 s   0,0 ms    0,0 ms    0,0 ms     0,0 ms

=== Transition — FPS ===
  Phase                  Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  Drain + Save + Unload           0,0           0,0          0,0          0,0

=== Transition — Memory ===
  Phase                  Avg Total  Peak Total  Avg Native  Peak Native  Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  Drain + Save + Unload     0,0 MB      0,0 MB      0,0 MB       0,0 MB    0,0 MB     0,0 MB       0,0 MB        0,0 MB

=== Transition — GC Allocations ===
  Phase                  Avg GC/frame  Peak GC/frame
  Drain + Save + Unload        0,0 KB         0,0 KB

=== Loading Pass — Performance ===
  Phase              Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  50 m/s               30,0 s   0,7 ms    3,9 ms    0,8 ms     4,2 ms
  100 m/s              30,0 s   0,7 ms    4,9 ms    0,9 ms     5,1 ms
  200 m/s              30,0 s   1,4 ms   10,8 ms    1,6 ms    11,1 ms
  -- Group Total --    1m 30s   0,9 ms   10,8 ms

=== Loading Pass — FPS ===
  Phase              Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  50 m/s                   1844,6         240,4       2643,2        253,4
  100 m/s                  1616,5         195,1       2250,7        204,5
  200 m/s                   887,5          89,8       1102,5         92,7
  -- Group Total --        1452,6          89,8

=== Loading Pass — Memory ===
  Phase    Avg Total  Peak Total  Avg Native  Peak Native  Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  50 m/s    756,1 MB    799,5 MB    539,1 MB     584,2 MB  847,8 MB   848,0 MB     217,1 MB      222,3 MB
  100 m/s   766,3 MB    839,5 MB    546,7 MB     622,2 MB  856,3 MB   864,0 MB     219,6 MB      225,0 MB
  200 m/s   799,9 MB    857,0 MB    578,3 MB     634,8 MB  896,6 MB   960,0 MB     221,6 MB      230,6 MB

=== Loading Pass — GC Allocations ===
  Phase    Avg GC/frame  Peak GC/frame
  50 m/s         1,7 KB        40,7 KB
  100 m/s        2,3 KB        41,8 KB
  200 m/s        5,3 KB        81,7 KB

=== Pipeline Telemetry (FP) ===
Verdict rule:        §7.1 v1 (dominant/plurality stop reason; ordering axis at waste ≥ 20%)
Raw results below are MANDATORY context, not decoration: the verdict is derived
from them and can be re-derived differently from the same numbers.

--- Generation Pass / 10 m/s ---
Duration:            30,0 s
Frames sampled:      71 713
  Note: the per-frame detail window wrapped (a rolling window by design). The
        stop-reason tallies below are still EXACT for the whole phase.

  Stage latency (ms) — only chunks that reached MeshApplied contribute:
  Hop                    n     min     p50     p95     p99     max
  enqueue→populated    165    14,5    18,5    31,4    33,0    33,1
  populated→lit        165  3209,2  3240,0  3265,0  3268,5  3273,4
  lit→meshApplied      165  1522,7  1559,5  1584,6  1590,7  1593,3
  enqueue→meshApplied  165  4804,7  4820,7  4831,4  4832,0  4832,0

  Raw histogram — enqueue→meshApplied (every sample bucketed, none dropped):
  Bucket    count  % of n
  <=5000ms    165  100,0%

  Waste accounting — absolute counts beside every percentage (§7.2):
  Disposition                count  % of traces started  waste?
  Pending                        0                 0,0%
  MeshApplied                  165                53,9%
  DiscardedOutOfRange            0                 0,0%   WASTE
  LoadStranded                   0                 0,0%   WASTE
  Rerequested                    0                 0,0%
  InFlightAtPhaseEnd           141                46,1%
  UnloadedBeforeMeshApplied      0                 0,0%   WASTE
  -- traces started --         306               100,0%

  Admission pressure — FULL stop-reason tallies, never only the winner (§7.2):
  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     70 951    712        0            0           50
  MeshSchedule            0     71 432    257        0            0           24
  GenerationProcess       0     71 713      0        0            0            0
  MeshProcess             0     71 713      0        0            0            0
    Panic gate closed:  0 / 71 713 frames (0,0%)

  Verdict inputs (verbatim):
    dominant stop reason = OutOfWork, runner-up = Quota
    waste = 0 / 306 terminal traces = 0,0% (ordering threshold 20%)
  VERDICT: Healthy

--- Generation Pass / 20 m/s ---
Duration:            30,0 s
Frames sampled:      59 921

  Stage latency (ms):
  Hop                    n     min     p50     p95     p99     max
  enqueue→populated    385    10,2    22,1    38,8    45,3    49,9
  populated→lit        385  1600,9  1643,8  1724,4  2324,6  2335,7
  lit→meshApplied      385    81,1   759,9   788,0   800,0   810,0
  enqueue→meshApplied  385  2401,8  2426,6  2443,8  2474,1  2474,5

  Raw histogram: <=5000ms 385 (100,0%)

  Waste accounting:
  Disposition                count  % of traces started  waste?
  Pending                        0                 0,0%
  MeshApplied                  385                59,6%
  DiscardedOutOfRange            0                 0,0%   WASTE
  LoadStranded                   0                 0,0%   WASTE
  Rerequested                    0                 0,0%
  InFlightAtPhaseEnd           151                23,4%
  UnloadedBeforeMeshApplied    110                17,0%   WASTE
  -- traces started --         646               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     58 563  1 275        0            0           83
  MeshSchedule            0     58 991    766        0            3          161
  GenerationProcess       0     59 921      0        0            0            0
  MeshProcess             0     59 921      0        0            0            0
    Panic gate closed:  0 / 59 921 frames (0,0%)
    waste = 110 / 646 = 17,0%
  VERDICT: Healthy

--- Generation Pass / 50 m/s ---
Duration:            30,0 s
Frames sampled:      50 487

  Hop                      n    min    p50     p95     p99     max
  enqueue→populated    1 000   17,2   27,8    45,3    55,2    69,5
  populated→lit        1 000  624,8  686,3   903,0  3865,0  4849,4
  lit→meshApplied      1 000    6,6  272,0   306,1  2199,3  3777,1
  enqueue→meshApplied  1 000  963,0  988,8  2246,4  4196,4  5150,6

  Raw histogram: <=1000ms 874 (87,4%) | <=2000ms 75 (7,5%) | <=5000ms 50 (5,0%) | >5000ms 1 (0,1%)

  Disposition                count  % of traces started  waste?
  MeshApplied                1 000                62,7%
  InFlightAtPhaseEnd           151                 9,5%
  UnloadedBeforeMeshApplied    444                27,8%   WASTE
  -- traces started --       1 595               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     47 190  3 088        0            0          209
  MeshSchedule            0     49 098  1 191        1            0          197
  GenerationProcess       0     50 486      0        1            0            0
  MeshProcess             0     50 487      0        0            0            0
    Panic gate closed:  0 / 50 487 frames (0,0%)
    waste = 444 / 1 595 = 27,8%
  VERDICT: Healthy + ORDERING-BOUND

--- Generation Pass / 100 m/s ---
Duration:            30,0 s
Frames sampled:      34 363

  Hop                      n    min    p50     p95     p99     max
  enqueue→populated    2 034   10,2   24,0    37,3    46,5    60,4
  populated→lit        2 034  230,1  365,2   417,0  2021,9  2614,6
  lit→meshApplied      2 034    1,4  115,7   148,0  1123,5  2010,4
  enqueue→meshApplied  2 034  478,5  506,5  1151,1  2262,7  2730,5

  Raw histogram: <=500ms 554 (27,2%) | <=1000ms 1 372 (67,5%) | <=2000ms 72 (3,5%) | <=5000ms 36 (1,8%)

  Disposition                count  % of traces started  waste?
  MeshApplied                2 034                63,8%
  InFlightAtPhaseEnd           151                 4,7%
  UnloadedBeforeMeshApplied  1 005                31,5%   WASTE
  -- traces started --       3 190               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     28 104  5 783        0            0          476
  MeshSchedule            0     33 192    882        3            0          286
  GenerationProcess       0     34 363      0        0            0            0
  MeshProcess             0     34 361      0        2            0            0
    Panic gate closed:  0 / 34 363 frames (0,0%)
    waste = 1 005 / 3 190 = 31,5%
  VERDICT: Healthy + ORDERING-BOUND

--- Generation Pass / 200 m/s ---
Duration:            19,7 s
Frames sampled:      4 972

  Hop                      n    min    p50    p95     p99     max
  enqueue→populated    2 647    7,2   24,4   62,7    82,9    97,6
  populated→lit        2 647   91,4  213,6  411,3   988,8  1238,1
  lit→meshApplied      2 647    0,4   33,8  146,3   474,2  1023,1
  enqueue→meshApplied  2 647  227,6  273,4  645,0  1116,5  1541,5

  Raw histogram: <=500ms 2 447 (92,4%) | <=1000ms 156 (5,9%) | <=2000ms 44 (1,7%)

  Disposition                count  % of traces started  waste?
  MeshApplied                2 647                63,4%
  InFlightAtPhaseEnd           151                 3,6%
  UnloadedBeforeMeshApplied  1 378                33,0%   WASTE
  -- traces started --       4 176               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0        502  4 407        5            0           58
  MeshSchedule            0      2 672  1 571        3           45          681
  GenerationProcess       0      4 970      0        2            0            0
  MeshProcess             0      4 972      0        0            0            0
    Panic gate closed:  0 / 4 972 frames (0,0%)
    waste = 1 378 / 4 176 = 33,0%
  VERDICT: Healthy + ORDERING-BOUND

--- Transition / Drain + Save + Unload ---
Duration: 0,1 s | Frames sampled: 11 | no completed chunks | 0 traces started
  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0          7      3        0            0            1
  MeshSchedule            0          3      8        0            0            0
  GenerationProcess       0         11      0        0            0            0
  MeshProcess             0         11      0        0            0            0
    Panic gate closed:  0 / 11 frames (0,0%)
  VERDICT: Healthy

--- Loading Pass / 50 m/s ---
Duration:            30,0 s
Frames sampled:      54 212

  Hop                      n     min     p50     p95     p99     max
  enqueue→populated    1 323     2,2     5,1     9,4    10,5    15,7
  populated→lit        1 323   911,1   967,7  1015,0  1224,6  1376,5
  lit→meshApplied      1 323     1,1   407,8   452,8   469,2   509,8
  enqueue→meshApplied  1 323  1361,0  1378,7  1394,0  1439,9  1452,6

  Raw histogram: <=2000ms 1 323 (100,0%)

  Disposition                count  % of traces started  waste?
  MeshApplied                1 323                60,7%
  InFlightAtPhaseEnd           133                 6,1%
  UnloadedBeforeMeshApplied    722                33,1%   WASTE
  -- traces started --       2 178               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     49 600  4 503        0            0          109
  MeshSchedule            0     52 120  1 659        0            2          431
  GenerationProcess       0     54 212      0        0            0            0
  MeshProcess             0     54 212      0        0            0            0
    Panic gate closed:  0 / 54 212 frames (0,0%)
    waste = 722 / 2 178 = 33,1%
  VERDICT: Healthy + ORDERING-BOUND

--- Loading Pass / 100 m/s ---
Duration:            30,0 s
Frames sampled:      47 009

  Hop                      n    min    p50    p95     p99     max
  enqueue→populated    2 319    1,1    3,3    8,4    10,3    14,1
  populated→lit        2 319  320,5  482,4  559,7  1782,9  4396,3
  lit→meshApplied      2 319   28,9  157,5  225,5   408,5  3424,4
  enqueue→meshApplied  2 319  480,1  691,6  717,3  2233,6  4571,5

  Raw histogram: <=500ms 831 (35,8%) | <=1000ms 1 429 (61,6%) | <=2000ms 29 (1,3%) | <=5000ms 30 (1,3%)

  Disposition                count  % of traces started  waste?
  MeshApplied                2 319                61,2%
  InFlightAtPhaseEnd           133                 3,5%
  UnloadedBeforeMeshApplied  1 338                35,3%   WASTE
  -- traces started --       3 790               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     39 409  7 425        0            0          175
  MeshSchedule            0     44 891  1 735        0            2          381
  GenerationProcess       0     47 009      0        0            0            0
  MeshProcess             0     47 009      0        0            0            0
    Panic gate closed:  0 / 47 009 frames (0,0%)
    waste = 1 338 / 3 790 = 35,3%
  VERDICT: Healthy + ORDERING-BOUND

--- Loading Pass / 200 m/s ---
Duration:            30,0 s
Frames sampled:      25 086

  Hop                      n    min    p50    p95     p99     max
  enqueue→populated    4 428    1,3    3,9    7,5    13,9    34,4
  populated→lit        4 428  142,1  240,6  331,3  1073,1  2264,7
  lit→meshApplied      4 428    0,4   64,0  112,5   384,9  1712,7
  enqueue→meshApplied  4 428  219,3  346,6  395,2  1253,5  2308,2

  Raw histogram: <=500ms 4 270 (96,4%) | <=1000ms 80 (1,8%) | <=2000ms 71 (1,6%) | <=5000ms 7 (0,2%)

  Disposition                count  % of traces started  waste?
  MeshApplied                4 428                61,0%
  InFlightAtPhaseEnd           140                 1,9%
  UnloadedBeforeMeshApplied  2 695                37,1%   WASTE
  -- traces started --       7 263               100,0%

  Pass               NotRun  OutOfWork   Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     11 044  13 743        0            0          299
  MeshSchedule            0     20 134   2 991        0            7        1 954
  GenerationProcess       0     25 086       0        0            0            0
  MeshProcess             0     25 085       0        1            0            0
    Panic gate closed:  0 / 25 086 frames (0,0%)
    waste = 2 695 / 7 263 = 37,1%
  VERDICT: Healthy + ORDERING-BOUND
````

---

# Appendix B — Run B, viewDistance 10, verbatim

Source: `BenchmarkRun_2026-07-28_18-06-42.log`

> **Exclusion notice (F7).** The source log's telemetry section opens with **9 phases byte-identical to
> Run A**, leaked from the previous benchmark run in the same process because `s_completedPhases` is not
> cleared at run start. Those 9 phases are **omitted here** — they are Run A's data and appear in Appendix A.
> The 8 phases below are Run B's genuine output, and are the only ones used in this report's analysis. The
> unedited log retains both blocks.

````
=== Configuration ===
Region size:         100 chunks (configured: 64, auto-scaled)
Phase duration:      30 s
Generation speeds:   10; 20; 50; 100; 200 m/s
Loading speeds:      50; 100; 200 m/s
Generation WPs:      6
Loading WPs:         12
VSync override:      Forced Off (was: On)
FPS cap override:    Uncapped (was: Uncapped)

=== Overall Summary ===
Total phases:        8
Total samples:       3 844
Wall-clock runtime:  3m 23s
Phase duration sum:  3m 20s
Avg CPU time:        2,2 ms
Peak CPU time:       19,5 ms
Avg Wall time:       2,6 ms
Avg GC alloc:        26,1 KB
Avg Wall FPS:        654,9
Min Wall FPS:        50,5
Avg CPU FPS:         951,4
Min CPU FPS:         51,2
Avg Total Memory:    1265,5 MB
Peak Total Memory:   1499,2 MB

=== Generation Pass — Performance ===
  Phase              Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  10 m/s               30,0 s   0,7 ms    3,8 ms    0,9 ms     4,0 ms
  20 m/s               30,0 s   1,4 ms    7,6 ms    2,0 ms     8,0 ms
  50 m/s               30,0 s   2,0 ms    6,7 ms    2,4 ms     7,0 ms
  100 m/s              19,8 s   4,2 ms   15,4 ms    4,6 ms    15,8 ms
  -- Group Total --    1m 49s   1,9 ms   15,4 ms

=== Generation Pass — FPS ===
  Phase              Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  10 m/s                   1249,2         249,7       1703,6        265,9
  20 m/s                    712,7         124,7       1393,6        131,3
  50 m/s                    641,7         143,2        928,4        149,4
  100 m/s                   290,2          63,2        335,3         65,0
  -- Group Total --         769,5          63,2

=== Generation Pass — Memory ===
  Phase    Avg Total  Peak Total  Avg Native  Peak Native   Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  10 m/s   1128,5 MB   1169,7 MB    779,5 MB     829,8 MB  1172,0 MB  1172,0 MB     349,0 MB      361,6 MB
  20 m/s   1173,2 MB   1224,1 MB    808,4 MB     849,3 MB  1175,9 MB  1204,0 MB     364,8 MB      379,6 MB
  50 m/s   1243,4 MB   1314,1 MB    860,1 MB     927,0 MB  1179,3 MB  1188,0 MB     383,2 MB      402,6 MB
  100 m/s  1279,9 MB   1332,3 MB    853,6 MB     889,2 MB  1217,2 MB  1236,0 MB     426,3 MB      446,1 MB

=== Generation Pass — GC Allocations ===
  Phase    Avg GC/frame  Peak GC/frame
  10 m/s         5,9 KB        85,0 KB
  20 m/s        17,5 KB        85,7 KB
  50 m/s        33,0 KB       131,5 KB
  100 m/s      104,5 KB       316,0 KB

=== Transition — Performance ===
  Phase                  Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  Drain + Save + Unload     0,3 s   2,5 ms    3,0 ms    2,7 ms     3,3 ms

=== Transition — FPS ===
  Phase                  Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  Drain + Save + Unload         383,1         306,9        424,5        330,8

=== Transition — Memory ===
  Phase                  Avg Total  Peak Total  Avg Native  Peak Native   Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  Drain + Save + Unload  1280,5 MB   1283,9 MB    838,0 MB     841,4 MB  1236,0 MB  1236,0 MB     442,5 MB      442,5 MB

=== Transition — GC Allocations ===
  Phase                  Avg GC/frame  Peak GC/frame
  Drain + Save + Unload       63,4 KB        63,4 KB

=== Loading Pass — Performance ===
  Phase              Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  50 m/s               30,0 s   1,7 ms    6,9 ms    2,0 ms     7,2 ms
  100 m/s              30,0 s   2,7 ms   14,1 ms    3,0 ms    14,4 ms
  200 m/s              30,0 s   3,7 ms   19,5 ms    4,0 ms    19,8 ms
  -- Group Total --    1m 30s   2,7 ms   19,5 ms

=== Loading Pass — FPS ===
  Phase              Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  50 m/s                    722,1         138,1       1052,3        144,6
  100 m/s                   469,0          69,6        601,6         71,0
  200 m/s                   339,4          50,5        376,6         51,2
  -- Group Total --         512,8          50,5

=== Loading Pass — Memory ===
  Phase    Avg Total  Peak Total  Avg Native  Peak Native   Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  50 m/s   1312,1 MB   1338,1 MB    879,1 MB     909,1 MB  1165,2 MB  1188,0 MB     432,9 MB      442,5 MB
  100 m/s  1348,4 MB   1433,5 MB    910,4 MB     999,4 MB  1234,5 MB  1268,0 MB     438,0 MB      449,1 MB
  200 m/s  1387,7 MB   1499,2 MB    946,0 MB    1066,6 MB  1357,4 MB  1460,0 MB     441,7 MB      457,5 MB

=== Loading Pass — GC Allocations ===
  Phase    Avg GC/frame  Peak GC/frame
  50 m/s         5,2 KB        45,3 KB
  100 m/s       18,7 KB       129,0 KB
  200 m/s       26,6 KB       252,5 KB

=== Pipeline Telemetry (FP) — Run B's genuine 8 phases ===

--- Generation Pass / 10 m/s ---
Duration:            30,0 s
Frames sampled:      37 158

  Hop                    n     min     p50     p95     p99     max
  enqueue→populated    315    14,4    29,4    46,9    49,5    58,1
  populated→lit        315  3212,9  3273,7  3328,6  3345,6  4271,6
  lit→meshApplied      315   537,0  1525,7  1574,0  1586,6  1595,7
  enqueue→meshApplied  315  4805,1  4830,7  4855,3  4857,2  4857,4

  Raw histogram: <=5000ms 315 (100,0%)

  Disposition                count  % of traces started  waste?
  MeshApplied                  315                64,8%
  InFlightAtPhaseEnd           171                35,2%
  UnloadedBeforeMeshApplied      0                 0,0%   WASTE
  -- traces started --         486               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     36 357    747        0            0           54
  MeshSchedule            0     36 901    248        0            0            9
  GenerationProcess       0     37 158      0        0            0            0
  MeshProcess             0     37 158      0        0            0            0
    Panic gate closed:  0 / 37 158 frames (0,0%)
    waste = 0 / 486 = 0,0%
  VERDICT: Healthy

--- Generation Pass / 20 m/s ---
Duration:            30,0 s
Frames sampled:      20 961

  Hop                    n     min     p50     p95     p99     max
  enqueue→populated    735    18,4    37,5    63,7    96,9   130,2
  populated→lit        735  1572,5  1693,5  1957,2  2351,9  2393,0
  lit→meshApplied      735    20,4   710,7   883,9   955,0   990,3
  enqueue→meshApplied  735  2404,6  2438,4  2627,4  2650,7  2657,3

  Raw histogram: <=5000ms 735 (100,0%)

  Disposition                count  % of traces started  waste?
  MeshApplied                  735                71,6%
  InFlightAtPhaseEnd           241                23,5%
  UnloadedBeforeMeshApplied     50                 4,9%   WASTE
  -- traces started --       1 026               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     19 213  1 664        2            0           82
  MeshSchedule            0     18 492  2 389        1           10           69
  GenerationProcess       0     20 961      0        0            0            0
  MeshProcess             0     20 961      0        0            0            0
    Panic gate closed:  0 / 20 961 frames (0,0%)
    waste = 50 / 1 026 = 4,9%
  VERDICT: Healthy

--- Generation Pass / 50 m/s ---
Duration:            30,0 s
Frames sampled:      18 436

  Hop                      n    min    p50     p95     p99     max
  enqueue→populated    1 892   13,5   37,9    63,6    76,9   104,5
  populated→lit        1 892  634,2  725,7   820,6  5493,4  7722,7
  lit→meshApplied      1 892    7,8  240,8   326,0  3150,4  6659,1
  enqueue→meshApplied  1 892  963,6  999,1  1960,5  6128,1  8054,7

  Raw histogram: <=1000ms 985 (52,1%) | <=2000ms 814 (43,0%) | <=5000ms 54 (2,9%) | >5000ms 39 (2,1%)

  Disposition                count  % of traces started  waste?
  MeshApplied                1 892                74,6%
  InFlightAtPhaseEnd           241                 9,5%
  UnloadedBeforeMeshApplied    402                15,9%   WASTE
  -- traces started --       2 535               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     14 766  3 424        0            0          246
  MeshSchedule            0     16 583  1 742        4            4          103
  GenerationProcess       0     18 434      0        2            0            0
  MeshProcess             0     18 436      0        0            0            0
    Panic gate closed:  0 / 18 436 frames (0,0%)
    waste = 402 / 2 535 = 15,9%
  VERDICT: Healthy

--- Generation Pass / 100 m/s ---
Duration:            19,8 s
Frames sampled:      5 386

  Hop                      n    min    p50    p95     p99     max
  enqueue→populated    2 520   11,3   33,3   68,6    80,1    95,8
  populated→lit        2 520  229,3  403,7  509,9  3096,2  4244,2
  lit→meshApplied      2 520    0,1   88,3  168,8  1438,7  3518,7
  enqueue→meshApplied  2 520  481,4  523,6  725,4  3545,5  4376,9

  Raw histogram: <=500ms 299 (11,9%) | <=1000ms 2 100 (83,3%) | <=2000ms 40 (1,6%) | <=5000ms 81 (3,2%)

  Disposition                count  % of traces started  waste?
  MeshApplied                2 520                75,3%
  InFlightAtPhaseEnd           262                 7,8%
  UnloadedBeforeMeshApplied    563                16,8%   WASTE
  -- traces started --       3 345               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0      1 527  3 706        4            0          149
  MeshSchedule            0      4 138  1 063        3           35          147
  GenerationProcess       0      5 381      0        5            0            0
  MeshProcess             0      5 386      0        0            0            0
    Panic gate closed:  0 / 5 386 frames (0,0%)
    waste = 563 / 3 345 = 16,8%
  VERDICT: Healthy

--- Transition / Drain + Save + Unload ---
Duration: 0,3 s | Frames sampled: 45 | no completed chunks | 0 traces started
  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0          7     36        0            0            2
  MeshSchedule            0         39      6        0            0            0
  GenerationProcess       0         45      0        0            0            0
  MeshProcess             0         45      0        0            0            0
    Panic gate closed:  0 / 45 frames (0,0%)
  VERDICT: Healthy

--- Loading Pass / 50 m/s ---
Duration:            30,0 s
Frames sampled:      21 101

  Hop                      n    min     p50     p95     p99     max
  enqueue→populated    2 705    2,6    11,1   255,5   755,4   764,7
  populated→lit        2 690   49,6  1011,0  1111,2  1383,7  1529,3
  lit→meshApplied      2 690    0,4   372,5   495,5   610,9   704,4
  enqueue→meshApplied  2 705  272,6  1400,6  1526,8  1673,9  1844,6

  Raw histogram: <=500ms 10 (0,4%) | <=1000ms 76 (2,8%) | <=2000ms 2 619 (96,8%)

  Disposition                count  % of traces started  waste?
  MeshApplied                2 705                71,9%
  InFlightAtPhaseEnd           193                 5,1%
  UnloadedBeforeMeshApplied    863                22,9%   WASTE
  -- traces started --       3 761               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0     15 625  5 376        3            0           97
  MeshSchedule            0     17 887  2 876        0            8          330
  GenerationProcess       0     21 101      0        0            0            0
  MeshProcess             0     21 100      0        1            0            0
    Panic gate closed:  838 / 21 101 frames (4,0%)
    waste = 863 / 3 761 = 22,9%
  VERDICT: Healthy + ORDERING-BOUND

--- Loading Pass / 100 m/s ---
Duration:            30,0 s
Frames sampled:      13 331

  Hop                      n    min    p50    p95     p99     max
  enqueue→populated    4 392    2,0    8,1   51,8    81,3   102,1
  populated→lit        4 392  277,4  517,9  720,6  2024,7  6019,1
  lit→meshApplied      4 392    0,6  138,3  261,7   564,4  4998,4
  enqueue→meshApplied  4 392  475,6  713,8  881,9  2738,3  6183,4

  Raw histogram: <=500ms 725 (16,5%) | <=1000ms 3 528 (80,3%) | <=2000ms 72 (1,6%) | <=5000ms 56 (1,3%) | >5000ms 11 (0,3%)

  Disposition                count  % of traces started  waste?
  MeshApplied                4 392                71,3%
  InFlightAtPhaseEnd           323                 5,2%
  UnloadedBeforeMeshApplied  1 446                23,5%   WASTE
  -- traces started --       6 161               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0      5 945  7 270        3            0          113
  MeshSchedule            0     10 235  2 472        3            8          613
  GenerationProcess       0     13 331      0        0            0            0
  MeshProcess             0     13 331      0        0            0            0
    Panic gate closed:  0 / 13 331 frames (0,0%)
    waste = 1 446 / 6 161 = 23,5%
  VERDICT: Healthy + ORDERING-BOUND

--- Loading Pass / 200 m/s ---
Duration:            30,0 s
Frames sampled:      9 763

  Hop                      n    min    p50     p95     p99     max
  enqueue→populated    5 759    4,5  463,9  1044,7  1466,2  3064,2
  populated→lit        5 164   21,9  432,9  1220,6  2072,4  3434,7
  lit→meshApplied      5 164    0,2    3,1   147,2   835,9  2604,3
  enqueue→meshApplied  5 759  169,6  982,2  1990,6  2744,6  4185,4

  Raw histogram: <=200ms 3 (0,1%) | <=500ms 927 (16,1%) | <=1000ms 2 015 (35,0%) | <=2000ms 2 537 (44,1%) | <=5000ms 277 (4,8%)

  Disposition                 count  % of traces started  waste?
  MeshApplied                 5 759                48,9%
  InFlightAtPhaseEnd            558                 4,7%
  UnloadedBeforeMeshApplied   5 462                46,4%   WASTE
  -- traces started --       11 779               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0          2  9 752        9            0            0
  MeshSchedule            0        262  9 190        7           22          282
  GenerationProcess       0      9 762      0        1            0            0
  MeshProcess             0      9 763      0        0            0            0
    Panic gate closed:  9 057 / 9 763 frames (92,8%)
    waste = 5 462 / 11 779 = 46,4%
  VERDICT: Healthy + ORDERING-BOUND
````

---

# Appendix C — Run C, viewDistance 20, verbatim

Source: `BenchmarkRun_2026-07-27_21-48-44.log`

````
--- BENCHMARK RUN PERFORMANCE REPORT ---
Date:                2026-07-27 21:48:44
Total runtime:       3m 14s

=== Configuration ===
Region size:         100 chunks (configured: 64, auto-scaled)
Phase duration:      30 s
Generation speeds:   10; 20; 50; 100; 200 m/s
Loading speeds:      50; 100; 200 m/s
Generation WPs:      4
Loading WPs:         12
VSync override:      Forced Off (was: On)
FPS cap override:    Uncapped (was: Uncapped)

=== Overall Summary ===
Total phases:        8
Total samples:       3 288
Wall-clock runtime:  3m 14s
Phase duration sum:  3m 2s
Avg CPU time:        6,6 ms
Peak CPU time:       36,0 ms
Avg Wall time:       7,4 ms
Avg GC alloc:        72,6 KB
Avg Wall FPS:        191,6
Min Wall FPS:        27,4
Avg CPU FPS:         264,1
Min CPU FPS:         27,7
Avg Total Memory:    2289,1 MB
Peak Total Memory:   2661,9 MB

=== Generation Pass — Performance ===
  Phase              Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  10 m/s               30,0 s   3,7 ms   15,2 ms    4,8 ms    15,7 ms
  20 m/s               30,0 s   4,8 ms   13,7 ms    6,3 ms    15,3 ms
  50 m/s               30,0 s  13,3 ms   36,0 ms   14,0 ms    36,5 ms
  100 m/s               0,7 s  28,5 ms   33,2 ms   28,9 ms    33,6 ms
  -- Group Total --    1m 30s   7,2 ms   36,0 ms

=== Generation Pass — FPS ===
  Phase              Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  10 m/s                    255,9          63,7        452,0         65,8
  20 m/s                    197,3          65,2        348,3         73,1
  50 m/s                    103,7          27,4        119,2         27,7
  100 m/s                    34,9          29,8         35,4         30,1
  -- Group Total --         187,3          27,4

=== Generation Pass — Memory ===
  Phase    Avg Total  Peak Total  Avg Native  Peak Native   Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  10 m/s   2081,6 MB   2111,7 MB   1296,1 MB    1318,8 MB  1647,5 MB  1670,5 MB     785,5 MB      812,3 MB
  20 m/s   2228,4 MB   2366,1 MB   1402,1 MB    1518,3 MB  1713,6 MB  1830,5 MB     826,4 MB      868,4 MB
  50 m/s   2278,7 MB   2415,4 MB   1409,5 MB    1544,8 MB  1889,0 MB  1958,5 MB     869,2 MB      896,8 MB
  100 m/s  2303,5 MB   2312,7 MB   1430,8 MB    1432,9 MB  1958,5 MB  1958,5 MB     872,7 MB      882,8 MB

=== Generation Pass — GC Allocations ===
  Phase    Avg GC/frame  Peak GC/frame
  10 m/s        36,2 KB       190,3 KB
  20 m/s        81,0 KB       201,1 KB
  50 m/s       258,7 KB       711,7 KB
  100 m/s      582,4 KB       648,5 KB

=== Transition — Performance ===
  Phase                  Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  Drain + Save + Unload     1,4 s  11,9 ms   34,0 ms   12,9 ms    34,4 ms

=== Transition — FPS ===
  Phase                  Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  Drain + Save + Unload         135,2          29,1        184,9         29,4

=== Transition — Memory ===
  Phase                  Avg Total  Peak Total  Avg Native  Peak Native   Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  Drain + Save + Unload  2317,8 MB   2325,2 MB   1434,6 MB    1442,0 MB  1958,5 MB  1958,5 MB     883,1 MB      883,1 MB

=== Transition — GC Allocations ===
  Phase                  Avg GC/frame  Peak GC/frame
  Drain + Save + Unload      226,0 KB       639,5 KB

=== Loading Pass — Performance ===
  Phase              Duration  Avg CPU  Peak CPU  Avg Wall  Peak Wall
  50 m/s               30,0 s   6,0 ms   20,7 ms    6,5 ms    21,2 ms
  100 m/s              30,0 s   6,8 ms   21,2 ms    7,2 ms    21,6 ms
  200 m/s              30,0 s   5,1 ms   22,1 ms    5,4 ms    22,4 ms
  -- Group Total --    1m 30s   6,0 ms   22,1 ms

=== Loading Pass — FPS ===
  Phase              Avg Wall FPS  Min Wall FPS  Avg CPU FPS  Min CPU FPS
  50 m/s                    178,9          47,1        209,4         48,3
  100 m/s                   165,6          46,4        178,4         47,1
  200 m/s                   244,3          44,5        267,2         45,2
  -- Group Total --         196,5          44,5

=== Loading Pass — Memory ===
  Phase    Avg Total  Peak Total  Avg Native  Peak Native   Avg Rsvd  Peak Rsvd  Avg Managed  Peak Managed
  50 m/s   2361,2 MB   2553,5 MB   1473,0 MB    1660,7 MB  1865,8 MB  2038,5 MB     888,1 MB      907,0 MB
  100 m/s  2454,2 MB   2661,9 MB   1560,3 MB    1758,4 MB  2113,9 MB  2150,5 MB     893,9 MB      913,1 MB
  200 m/s  2337,8 MB   2488,7 MB   1441,1 MB    1582,2 MB  2062,9 MB  2118,6 MB     896,7 MB      915,2 MB

=== Loading Pass — GC Allocations ===
  Phase    Avg GC/frame  Peak GC/frame
  50 m/s        22,4 KB       131,4 KB
  100 m/s       20,5 KB        77,7 KB
  200 m/s       15,5 KB        74,5 KB

=== Pipeline Telemetry (FP) ===

--- Generation Pass / 10 m/s ---
Duration:            30,0 s
Frames sampled:      7 436

  Hop                    n     min     p50     p95     p99     max
  enqueue→populated    615    25,5    70,1   129,6   134,5   136,5
  populated→lit        615  3208,9  3377,0  4682,2  4885,7  4933,4
  lit→meshApplied      615    14,6  1440,6  1670,9  1704,8  1769,7
  enqueue→meshApplied  615  4805,6  4901,0  5075,1  5094,2  5094,4

  Raw histogram: <=5000ms 448 (72,8%) | >5000ms 167 (27,2%)

  Disposition                count  % of traces started  waste?
  MeshApplied                  615                72,7%
  InFlightAtPhaseEnd           231                27,3%
  UnloadedBeforeMeshApplied      0                 0,0%   WASTE
  -- traces started --         846               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0      6 802    580        3            0           51
  MeshSchedule            0      5 822  1 559        1           49            5
  GenerationProcess       0      7 433      0        3            0            0
  MeshProcess             0      7 429      0        7            0            0
    Panic gate closed:  0 / 7 436 frames (0,0%)
    waste = 0 / 846 = 0,0%
  VERDICT: Healthy

--- Generation Pass / 20 m/s ---
Duration:            30,0 s
Frames sampled:      5 620

  Hop                      n     min     p50     p95      p99      max
  enqueue→populated    1 419    25,2    80,2   122,5    147,5    186,7
  populated→lit        1 419  1577,6  1764,3  1930,3  17841,2  28921,7
  lit→meshApplied      1 419    14,4   660,5   831,1  15908,9  27179,5
  enqueue→meshApplied  1 419  2409,2  2488,0  2682,5  24082,4  29706,4

  Raw histogram: <=5000ms 1 358 (95,7%) | >5000ms 61 (4,3%)

  Disposition                count  % of traces started  waste?
  MeshApplied                1 419                79,5%
  InFlightAtPhaseEnd           367                20,5%
  UnloadedBeforeMeshApplied      0                 0,0%   WASTE
  -- traces started --       1 786               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0      4 645    895       11            0           69
  MeshSchedule            0      4 242  1 325        1           52            0
  GenerationProcess       0      5 613      0        7            0            0
  MeshProcess             0      5 612      0        8            0            0
    Panic gate closed:  0 / 5 620 frames (0,0%)
    waste = 0 / 1 786 = 0,0%
  VERDICT: Healthy

--- Generation Pass / 50 m/s ---
Duration:            30,0 s
Frames sampled:      2 900

  Hop                      n    min     p50     p95      p99      max
  enqueue→populated    3 682   15,9    61,1   129,6    158,4    172,0
  populated→lit        3 682  412,7   817,7  1282,0   8435,2  14201,1
  lit→meshApplied      3 682    1,1   186,1   963,7   2474,6  13056,7
  enqueue→meshApplied  3 682  957,1  1057,2  1977,3  10438,1  14518,2

  Raw histogram: <=1000ms 380 (10,3%) | <=2000ms 3 120 (84,7%) | <=5000ms 95 (2,6%) | >5000ms 87 (2,4%)

  Disposition                count  % of traces started  waste?
  MeshApplied                3 682                83,4%
  InFlightAtPhaseEnd           465                10,5%
  UnloadedBeforeMeshApplied    268                 6,1%   WASTE
  -- traces started --       4 415               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0        927  1 854       58            0           61
  MeshSchedule            0      1 952    763        5          168           12
  GenerationProcess       0      2 895      0        5            0            0
  MeshProcess             0      2 879      0       21            0            0
    Panic gate closed:  0 / 2 900 frames (0,0%)
    waste = 268 / 4 415 = 6,1%
  VERDICT: Healthy

--- Generation Pass / 100 m/s ---  ⚠ NOT A RESULT (L1): 0,7 s of a nominal 30 s, 17 frames, 8 chunks
Duration:            0,7 s
Frames sampled:      17

  Hop                  n    min    p50    p95    p99    max
  enqueue→populated    8   37,9   69,8  138,9  138,9  138,9
  populated→lit        8  394,1  405,1  497,4  497,4  497,4
  lit→meshApplied      8   11,4   16,4   20,2   20,2   20,2
  enqueue→meshApplied  8  552,8  553,0  553,2  553,2  553,2

  Raw histogram: <=1000ms 8 (100,0%)

  Disposition                count  % of traces started  waste?
  MeshApplied                    8                 4,3%
  InFlightAtPhaseEnd           180                95,7%
  -- traces started --         188               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0          0      9        8            0            0
  MeshSchedule            0          0      1        1           15            0
  GenerationProcess       0         17      0        0            0            0
  MeshProcess             0         16      0        1            0            0
    Panic gate closed:  0 / 17 frames (0,0%)
  VERDICT: Healthy

--- Transition / Drain + Save + Unload ---
Duration: 1,4 s | Frames sampled: 163 | no completed chunks | 0 traces started
  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0        106     56        0            0            1
  MeshSchedule            0          2    161        0            0            0
  GenerationProcess       0        163      0        0            0            0
  MeshProcess             0        163      0        0            0            0
    Panic gate closed:  0 / 163 frames (0,0%)
  VERDICT: Healthy

--- Loading Pass / 50 m/s ---
Duration:            30,0 s
Frames sampled:      5 160

  Hop                      n    min     p50     p95     p99      max
  enqueue→populated    5 099    4,7   962,5  2399,5  2958,6   3485,2
  populated→lit        5 098   53,2   823,2  2627,1  3195,9   8739,3
  lit→meshApplied      5 098    0,7     5,5   355,8  1047,0  11197,9
  enqueue→meshApplied  5 099  101,5  2002,2  3878,8  5253,7  14705,9

  Raw histogram: <=200ms 26 (0,5%) | <=500ms 78 (1,5%) | <=1000ms 332 (6,5%) | <=2000ms 2 106 (41,3%) | <=5000ms 2 498 (49,0%) | >5000ms 59 (1,2%)

  Disposition                count  % of traces started  waste?
  MeshApplied                5 099                68,5%
  InFlightAtPhaseEnd           320                 4,3%
  UnloadedBeforeMeshApplied  2 022                27,2%   WASTE
  -- traces started --       7 441               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0        671  4 450       20            0           19
  MeshSchedule            0        923  4 126        2           31           78
  GenerationProcess       0      5 160      0        0            0            0
  MeshProcess             0      5 160      0        0            0            0
    Panic gate closed:  3 601 / 5 160 frames (69,8%)
    waste = 2 022 / 7 441 = 27,2%
  VERDICT: Healthy + ORDERING-BOUND

--- Loading Pass / 100 m/s ---
Duration:            30,0 s
Frames sampled:      4 738

  Hop                      n    min     p50     p95     p99      max
  enqueue→populated    5 972    5,1  1226,6  2703,1  4194,9   6045,4
  populated→lit        5 968   30,1   581,1  2667,3  6657,0  17921,4
  lit→meshApplied      5 968    0,5     5,0   165,5  1058,7   5782,8
  enqueue→meshApplied  5 972  482,9  2094,5  4423,0  6980,5  19404,3

  Raw histogram: <=500ms 48 (0,8%) | <=1000ms 1 061 (17,8%) | <=2000ms 1 760 (29,5%) | <=5000ms 2 919 (48,9%) | >5000ms 184 (3,1%)

  Disposition                count  % of traces started  waste?
  MeshApplied                5 972                60,0%
  InFlightAtPhaseEnd         1 016                10,2%
  UnloadedBeforeMeshApplied  2 966                29,8%   WASTE
  -- traces started --       9 954               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0          8  4 710       19            0            1
  MeshSchedule            0        173  4 498        4           45           18
  GenerationProcess       0      4 738      0        0            0            0
  MeshProcess             0      4 738      0        0            0            0
    Panic gate closed:  4 082 / 4 738 frames (86,2%)
    waste = 2 966 / 9 954 = 29,8%
  VERDICT: Healthy + ORDERING-BOUND

--- Loading Pass / 200 m/s ---
Duration:            30,0 s
Frames sampled:      6 936

  Hop                      n    min     p50     p95     p99     max
  enqueue→populated    6 574  173,5  1822,7  4199,9  5994,4  6708,8
  populated→lit        6 573   25,8   579,3  1631,0  2730,4  5690,7
  lit→meshApplied      6 573    0,4     3,2    20,1    93,5  1351,3
  enqueue→meshApplied  6 574  233,3  2489,6  5328,0  7012,1  7700,0

  Raw histogram: <=500ms 68 (1,0%) | <=1000ms 123 (1,9%) | <=2000ms 1 671 (25,4%) | <=5000ms 4 358 (66,3%) | >5000ms 354 (5,4%)

  Disposition                 count  % of traces started  waste?
  MeshApplied                 6 574                30,1%
  InFlightAtPhaseEnd          1 911                 8,7%
  UnloadedBeforeMeshApplied  13 363                61,2%   WASTE
  -- traces started --       21 848               100,0%

  Pass               NotRun  OutOfWork  Quota  Ceiling  InFlightCap  AllDeclined
  LightSchedule           0          0  6 915       21            0            0
  MeshSchedule            0          0  6 889        2           45            0
  GenerationProcess       0      6 936      0        0            0            0
  MeshProcess             0      6 936      0        0            0            0
    Panic gate closed:  6 687 / 6 936 frames (96,4%)
    waste = 13 363 / 21 848 = 61,2%
  VERDICT: Healthy + ORDERING-BOUND
````
