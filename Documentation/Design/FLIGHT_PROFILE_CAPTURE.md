# Flight-Profile Capture (Pipeline Telemetry) Design

**Version:** 1.16  
**Date:** 2026-07-27  
**Amended:** 2026-07-27 (v1.1) — re-verified every §2 row, §5 hook site, and both §8 questions against the
code. Six §2 rows corrected, the hook chain shortened from five stamps to four (MP-6), the stop-reason set
widened by two, and the verdict widened to **four** regimes. Both open questions are now closed.  
**Amended:** 2026-07-27 (v1.2) — the FP-0…FP-4 implementation plan's decisions folded in: §4.1
flush-and-restart (the side table's coord-collision defect), §5.2 stop reason returned by the pure policies,
§7.1 the pre-committed verdict rule, **§7.2 the mandatory raw-results block**, §9 assumptions + limitations,
and a correction to v1.1's overstated `Validate All` guard claim.  
**Amended:** 2026-07-27 (v1.3) — FP-0 as-built sync: dispositions 4 → 6, `NotRun` added to the stop-reason
enum, §8 Q1 closed with measured capacities.  
**Amended:** 2026-07-27 (v1.4) — FP-1 as-built sync: six hook sites, `UnloadedBeforeMeshApplied` restored
(dispositions → 7), and `StampRequested` made idempotent before admission.  
**Amended:** 2026-07-27 (v1.5) — FP-2 as-built sync: stop reasons returned by the pure policies, the enums
relocated to `Helpers`, one shared `ClassifyStop`, and the B8 baseline (Validate All → 356).  
**Amended:** 2026-07-27 (v1.6) — FP-3 as-built sync: pure `TraceStatistics` + `PipelineRegimeVerdict`, the
report section with §7.2's raw block, B9/B10, and the narrowed ordering criterion.  
**Amended:** 2026-07-28 (v1.7) — **FP-4 captured; the arc is complete.** The verdict, the §7.1 v1 rule defect
the capture exposed (§7.1.1), and the three follow-ups the numbers licensed are recorded below.  
**Amended:** 2026-07-28 (v1.8) — FP-4 **extended to a three-point view-distance sweep** (vd 5 / 10 / 20). The
single-leg verdict is refined: ordering-boundness is **universal**, admission-boundness is **conditional on
viewDistance ≥ 10**. Adds the lockstep result, the visibility criterion, and a telemetry bug the sweep exposed.  
**Amended:** 2026-07-28 (v1.9) — **FP-5 fixed and guarded** (§7.4): `BeginRun()` at the run boundary, baseline
**B11**, Validate All 358 → **359**. Records the UDR0002 constraint that dictates its shape.  
**Amended:** 2026-07-28 (v1.10) — **FP-6 done and widened** (§7.4): the report now prints every pipeline knob
that produces a stop reason, grouped by which one, via `PipelineSettingsSnapshot`. Baseline **B12**,
Validate All → **360**. Notes that the FP-4 captures predate this and their tuning is unrecorded.  
**Amended:** 2026-07-31 (v1.11) — **FP-7: five measurement defects fixed** (§7.4.2), found by reviewing the
instrument rather than by running it. Four concerned what the numbers *mean* — never-admitted requests counted
as waste, a quota stop reported as `OutOfWork`, flag-less lighting entries counted as declined, and a
disposition that was wrong in every firing — and the fifth closes §7.1.1 as **§7.1 v2**, a per-(pass, reason)
capability-weighted plurality. Baselines **B13**/**B14**, B10 rewritten, Validate All 360 → **362**.
**The FP-4 report is no longer comparable to future captures on either axis; `RULE_VERSION` says so.**  
**Amended:** 2026-07-31 (v1.12) — **FP-8 captured: five-point sweep (vd 5/8/10/15/20), first Release build,
first under §7.1 v2.** The headline **supersedes FP-4**: with never-admitted requests removed from the waste
fraction, ordering-boundness **decays** with view distance rather than growing, so it is a *low*-view-distance
phenomenon confirmed at the default vd and absent by vd 20. §7.3 re-ranked — **P-8 promoted over P-7**. Two
new instrument defects filed as FP-9a/FP-9b (§7.4.3).  
**Amended:** 2026-07-31 (v1.13) — **FP-9a fixed and guarded** (§7.4.3): the primary regime gains the
sample floor the ordering axis already had (`MinRegimeObservations = 1000` over eligible observations, plus
`PrimaryDecidable`), and non-measurement phases are excluded from **both** axes via `RegimeBearing`. Baseline
**B16**, Validate All 363 → **364**.  
**Amended:** 2026-08-01 (v1.14) — **FP-9b fixed and guarded** (§7.4.3). The route is now derived from the
configured speeds × phase duration instead of a region the user guessed at, so generation waypoints are
**constant across a view-distance sweep** and every speed phase runs its full duration — previously the
fastest generation phase never ran at all at vd ≥ 10. Adds a non-measurement **ensure-generated** sweep, a
fixed 64-chunk loading tour, and retires `benchmarkRegionSize` for `benchmarkGenerationWaypoints` +
`benchmarkPhaseSeconds`. Baseline **B17**, Validate All 364 → **365**.  
**Amended:** 2026-08-01 (v1.15) — **FP-10 captured: six-point sweep (vd 5/8/10/15/20/32), first on FP-9b's
derived route** and therefore the first with a cross-view-distance-comparable **generation** pass. FP-8's
inverted verdict **reproduces** across the route rework (within ~1 pt at four of five overlapping points),
which promotes it from a rescoring artifact to a property of the pipeline. The admission trend gains a
measured mechanism: a fixed 256-chunk gate threshold against a resident square growing as vd² **clamps
admitted work to 1.5–1.7× growth while requests grow 4.5–4.8×**. §7.3 updated — P-8 **confirmed** at #1 with a
frame-time constraint attached. One new instrument defect (ensure-pass coverage unmeasured) plus two minor
items.  
**Amended:** 2026-08-01 (v1.16) — **FP-11a/FP-11c shipped, and P-8 tested and NO-GO'd.** The ensure sweep now
measures and prints tour coverage at two instants (and flies the closed circuit the loading pass actually
flies, which it previously did not — see §7.4.4); guarded by **B18**, Validate All 365 → **366**. The
[P-8 capture](../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md) then **falsified
the fix this document ranked #1**: scaling the thresholds moves gate closure by 0.1 pt at vd 32 because the
backlog grows to meet the threshold, and the binding constraint is `Quota`, not admission. §7.3 re-ranked —
**P-8 demoted and re-scoped behind throughput work**, which is promoted from "not licensed" for the
high-view-distance regime specifically. B19 added with the (retained, default-OFF) derivation, Validate All
366 → **367**.  
**Status:** ✅ **Implemented.** FP-0…FP-4 are all shipped; the current capture is
[`../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md)
(six IL2CPP **Release** runs at viewDistance 5 / 8 / 10 / 15 / 20 / 32). **Verdict: ORDERING-BOUND is a
LOW-view-distance phenomenon** (worst case 50.8 % at vd 8 / 200 m/s, absent by vd 32); **ADMISSION-BOUND from
vd ≥ 8 and dominant from vd ≥ 15.** Per-phase status is in §7.

> **Superseded captures, kept for provenance.** FP-4
> ([report](../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md), §7.1 v1,
> Development build) concluded *ordering-bound at every view distance* — **superseded by FP-8**, whose FP-7a
> correction removed never-admitted requests from the waste fraction and inverted the trend. FP-8
> ([report](../Performance/CHUNK_PIPELINE_FP8_FLIGHT_PROFILE_IL2CPP_2026-07-31_BENCHMARK.md)) is correct on
> its own terms but ran the pre-FP-9b route; its numbers are **not** continued by FP-10, which reproduces the
> curve's shape rather than extending its values.  

**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> A telemetry layer that answers **one question the existing benchmark cannot**: when chunks appear
> sluggishly during sustained high-speed flight, is the pipeline **admission-bound** (P-4 budgets and the
> panic gate throttling by design), **throughput-bound** (a stage genuinely too slow), or **ordering-bound**
> (work completing for chunks the player has already flown past)? The pivotal decision is that this is
> **not a new tool**: `BenchmarkController` already flies waypoints at escalating speeds through both a
> generation and a loading pass — what is missing is *pipeline-internal* instrumentation, so this design adds
> a telemetry layer that the existing rig drives, and **per-chunk stage latency is its load-bearing output**,
> not another aggregate frame metric.

**Audited:** 2026-07-27, at commit `6c7609c0` (branch `feat/world-scaling`).
Findings are from static review of `Benchmarks/BenchmarkController.cs`, `BenchmarkMetricsCollector.cs`,
`WorldFrameProfiler.cs`, `Helpers/GenerationPanicGate.cs`, `Helpers/PipelinePassBudget.cs`,
`WorldJobManager.cs` (generation/lighting/mesh completion passes), and `World.cs`
(`CheckViewDistance`/`DrainGenerationRequests`/`Update`). Counter and ordering claims below were read in
code, not assumed.

**Re-audited:** 2026-07-27, at the **same** commit `6c7609c0`. The v1.1 corrections below are therefore
**errors in the original audit, not drift in the code** — nothing moved underneath this document between
the two passes. Recorded plainly so a future reader does not go looking for the commit that changed
behavior: there isn't one. The re-audit added line-level citations to every claim it kept, which is what
surfaced the six wrong rows (the v1.0 pass asserted several of them from the shape of the system rather
than from the file).

**Relationship to other documents:**

- [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) — the parent analysis.
  Its §6 order (§4.4 save bit → §2 jobified merge) is exactly what this capture is meant to *arbitrate*
  against the flight symptom, rather than picking by intuition.
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the stage
  boundaries this instruments; §3 readiness gates and §4 main-loop order define where the hooks go.
- [`../Performance/README.md`](../Performance/README.md) — the capture protocol and report conventions any
  numbers this produces must follow.

---

## 1. Goals & non-goals

### Goals

1. **Discriminate the four regimes** — admission-bound, throughput-bound, ordering-bound, and (added in
   v1.1) **readiness-bound** — from a single capture, so the next optimization is chosen on evidence.
   The fourth is not a hedge: §5.1's `AllDeclined` stop reason makes "the queue is full but nothing in it is
   *eligible*" directly observable, and it points at a different fix than the other three (the readiness gate
   / upstream lighting, rather than the budgets, a slow stage, or queue order). It was invisible to v1.0's
   taxonomy, which would have scored it as throughput-bound at best and as a healthy pipeline at worst.
2. **Per-chunk stage latency** — for each chunk: enqueue → populated → lit → mesh applied, with the
   wall-clock gap at each hop. Aggregate frame timings cannot separate "the stage is slow" from "the chunk
   waited in a queue". *(v1.1: the chain ends at `MeshApplied`; the former fifth `visible` hop is the same
   instant post-MP-6 — see §5.)*
3. **Waste accounting** — how much completed work is discarded because the player moved on, and how much of
   the wave-front is re-requested. At speed this is the difference between "too slow" and "doing the wrong
   work first".
4. **Admission pressure** — panic-gate closed %, per-pass ceiling exhaustion rate, queue depths per frame.
5. **Provably inert when disabled**, and usable in an **IL2CPP player**. *(v1.1 citation fix: the
   "editor Mono is screening-only, never presented as the shipping result" rule lives in the
   `perf-benchmark` skill, **not** in `Performance/README.md` — that README documents baseline naming,
   drift correction and the append-only rule, and says nothing about backends. The rule stands; only
   v1.0's pointer to it was wrong.)*
6. **Raw results, always** (v1.2) — every capture writes the distributions and counters the verdict was
   derived *from*, not merely the verdict. §7.2 makes this binding: a future session must be able to reach a
   **different** conclusion from the same report without re-running the capture.

### Non-goals (v1)

- **Replacing `PerformanceMonitor` / `BenchmarkMetricsCollector`** — frame health (FPS, GC, memory) stays
  theirs. This layer is strictly pipeline-internal and reports *alongside* them.
- **Fixing anything.** This is a measurement instrument. Any optimization it motivates is a separate item.
- **A live in-game HUD** for pipeline state — planned as a **v2 extension**, see §7.
- **Per-voxel or per-job tracing.** Chunk granularity is the unit; sub-job attribution stays with the Unity
  profiler and `WorldFrameProfiler`'s four phases.

---

## 2. Current state (what exists today)

| Area                          | State                                                                                                                                                                                                                             |
|-------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Flight rig**                | ✅ Exists and is exactly right. `BenchmarkController` flies waypoints through a **generation pass** (zigzag, escalating speeds) and a **loading pass** (diagonal cross-cuts after a force-unload), with `TIME_PER_PHASE = 30 s` per speed and a settle wait between phases. Speeds are configurable (`benchmarkGenerationSpeeds`). ⚠ **v1.1:** the generation pass's **last** speed phase is not 30 s — it runs until the waypoints are exhausted (`BenchmarkController.cs:274` only escalates while `speedIndex < len-1`), so phase durations are unequal and every per-phase rate must be divided by the phase's own `DurationSeconds`, never by `TIME_PER_PHASE`. |
| **What it collects**          | ❌ Frame health only. `BenchmarkMetricsCollector` subscribes to `PerformanceMonitor.OnMetricsSampled` and stores CPU/wall ms, FPS, GC alloc, native/managed/total memory per phase. **No pipeline state whatsoever.**                |
| **Sub-phase timing**          | ✅ **Resolved 2026-08-02 by P-9's phase P9-0** (was 🟡 Partial: four phases — Apply / Light / Mesh / Tick — enabled only by `FluidStressController`, never by the flight capture). `WorldFrameProfiler` now carries **ten** phases: Tick / Apply / **LightMerge** / **LightStagingDrain** / **LightFailSafeScan** / LightSchedule / MeshProcess / MeshSchedule / GenerationProcess / **LightQueueProbe** — one per budgeted pass, the three unbudgeted lighting regions, each of which runs outside the schedule pass's budget window and would otherwise make `LightSchedule` incomparable to its own 8 ms ceiling — and `BenchmarkController` enables it for the run. `LastFrameLightMs`/`LastFrameMeshMs` survive as derived sums, so this row's original consumer is unchanged. Still frame-level, not per-chunk. **`LightQueueProbe` (added 2026-08-23 by LP-1) is dev/editor-only** — it is `[Conditional]`-compiled out, so it always reads 0 ms in an IL2CPP capture. ⚠ **Editor captures taken before 2026-08-23 are not comparable on `LightFailSafeScan`:** the probe call sat inside that scan's open span, so its cost was counted in *both* slots and the "all timed regions" total double-counted it. Fixed by splitting the span (measured: a 200 ms probe read 200 ms in `LightFailSafeScan` before, 0 ms after). IL2CPP captures are unaffected — the probe never ran there. See [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) §8. |
| **Waste counters**            | 🟡 **Five exist, not one** (v1.1 correction). `WorldJobManager.cs:129–165` carries `MeshGoneChunkDiscards`, `MeshStaleInstanceMerges`, `MeshInFlightRetried`, `MeshScheduleAttempts` and `MeshMergeAttempts` — all `[Conditional]` on `UNITY_EDITOR`/`DEVELOPMENT_BUILD`, so they **do survive into FP-4's Development Build** and should be *read*, not rebuilt. The substantive gap holds and widens: **two** uncounted discard sites, not one — generation's §3.2 out-of-range discard (`WorldJobManager.cs:1012–1020`) and the disk-load stranding at `LoadOrGenerateChunkInner`'s post-await unload/ABA guard (`World.cs:1032–1042`, returns the loaded `ChunkData` to the pool). The latter is what closes §8 Q2. |
| **Panic gate observability**  | ✅ **v1.0 was wrong here.** The gate state is *not* unobservable: `World.cs:273–306` holds `_generationGateOpen` / `_generationGateClosedFrames` / `_generationGateCloseCount` behind the public probes `GenerationGateOpen`, `GenerationGateClosedFrames`, `GenerationGateCloseCount`, `GenerationRequestQueueCount`, `LightWorkReadyCount`, `LightWorkWaitingCount`, and **both transitions `Debug.Log` unconditionally** (`World.cs:3300–3313`) — they already feed the CP-1 debug HUD. `GenerationPanicGate` itself is indeed a pure decision function; the state simply lives in `World`, observably. What is missing is only **per-phase aggregation** (closed-frames binned against a benchmark phase). **This shrinks FP-2 to sampling existing probes.** |
| **Budget observability**      | ❌ Confirmed, and sharpened. `PipelinePassBudget` yields a quota and a window; **no pass records why it stopped.** Two v1.1 corrections to the shape of the fix: (1) there are **four** budgeted passes, not five — MP-6 retired the draw budget (see the §5.3 supersession note in the parent analysis) — and two of the four (`ProcessGenerationJobs`, `ProcessMeshJobs`) are **ceiling-only**, so they can never report a quota stop; (2) the reason set needs **four** values, not three — see §5. |
| **Queue ordering**            | 🟡 **v1.0 overstated this in one direction and understated it in another.** *Overstated:* the generation request queue **is** distance-ordered — `CheckViewDistance` rebuilds it nearest-first through `SpiralLoop` on every boundary crossing (`World.cs:3374–3409`), which v1.0's own next sentence said correctly while the row header denied it. The real defect is **staleness**, not absence: the order is only refreshed per crossing, so at speed the head can be work already flown past. *Understated:* `LightWorkScheduler._ready` is a `HashSet<Vector2Int>` and `SnapshotReady` iterates it in **hash order** (`LightWorkScheduler.cs:26,84–90`) — so under a quota break the lighting scan serves an **arbitrary** subset, not a FIFO one. That is a stronger ordering finding than "a ready/waiting split". `MeshBuildQueue` is FIFO with promotion-to-head on re-request (MT-1), unchanged. |

**Consequence:** the existing benchmark can already tell you *that* a 200 m/s phase has worse frame health
than a 10 m/s phase. It cannot tell you whether a chunk took 4 s to appear because generation was slow,
because it sat in a queue behind chunks you had already passed, or because the panic gate refused to admit it.

---

## 3. Decision: extend the benchmark rig, don't build a second tool

### Option A — a standalone flight-profile scene/controller (rejected)

- ✅ Clean separation; no risk of perturbing an established benchmark's numbers.
- ❌ **It would re-implement the hard part.** Waypoint construction (region auto-sizing from
  `LoadDistance`, margin/stride derivation), speed phasing, the drain→save→unload transition, pipeline-settle
  waits, HUD, and report generation already exist and are tuned. A second copy drifts from the first.
- ❌ Two rigs means two answers to "what was the world doing", and no shared baseline.

### Option B — telemetry layer attached to the existing passes ✅ **CHOSEN**

The generation and loading passes at escalating speeds **are** the experiment this needs — the speed sweep is
already the independent variable, and the gen/load split already separates "fresh terrain" from "disk
revisit", which is the exact distinction that decides between
[`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) §4.4 (helps revisits only)
and §4.1/§2 (helps both). The telemetry attaches per phase through the same `BeginPhase`/`EndPhase` boundary
`BenchmarkMetricsCollector` already uses, and emits an extra report section.

Precedent: `WorldFrameProfiler` is the same shape — a static, opt-in, allocation-free accumulator that
production code calls through cheap guarded hooks, driven by a stress controller that flips `Enabled`.

### Option C — Unity Profiler markers only (rejected)

- ✅ Zero bespoke code; deep per-call attribution.
- ❌ **Cannot answer the ordering question.** Profiler markers show where CPU time goes in a frame, not which
  *chunk* the work belonged to or how long that chunk had been waiting. Per-chunk lifecycle is not a
  CPU-time question.
- ❌ Requires an attached profiler session; the symptom is reported from ordinary play at speed.

---

## 4. Decision: side table, not a field on `ChunkData`

Recording per-chunk stage timestamps needs somewhere to put them.

### Option A — timestamp fields on `ChunkData` (rejected)

- ✅ O(1) access exactly where each stage transition happens; no lookup.
- ❌ **`ChunkData` is pooled**, so every field added incurs the `.agents/rules/pool-reset-safety.md`
  obligation (reset in `Reset()`, or a recycled chunk reports a previous life's timings) — a permanent
  correctness tax on the *engine* for a diagnostic that is off in every shipping session.
- ❌ Grows a hot, cache-relevant type for telemetry.

### Option B — side table keyed by `ChunkCoord`, owned by the telemetry static ✅ **CHOSEN**

A `Dictionary<ChunkCoord, ChunkTrace>` (pre-sized, cleared per phase) inside the telemetry class. The engine
gains **no fields and no pool obligations**; when telemetry is disabled the table is never touched and every
hook is a single `if (!Enabled) return;`. The lookup cost is paid only during a capture, where it is
irrelevant against the stage costs being measured.

### 4.1 Coord is not unique within a phase — flush-and-restart (v1.2)

A defect in the v1.0/v1.1 statement of Option B, found while planning FP-0. **`ChunkCoord` is not a unique
key over a phase's lifetime.** The *loading pass* exists precisely to revisit territory, and at 50–200 m/s a
coord is unloaded and re-loaded well within one 30 s phase; the generation pass does the same wherever the
zigzag's rows overlap at the turns. A plain `table[coord] = trace` therefore **silently overwrites the first
chunk's trace with the second's**, losing the very samples the capture exists to collect — and biasing what
survives toward whichever visit happened last.

**Decision: flush-and-restart.** When a `Requested` stamp arrives for a coord that is already traced, the
existing trace is **finalized into the phase aggregator** (with whatever terminal disposition it had reached,
`UnloadedBeforeMeshApplied` if none) and a **fresh** trace replaces it. Two reasons this is the right variant
rather than keep-first or keep-last:

- It is the only one that loses no samples — both visits are counted.
- The count of flushes **is** §1 goal 3's "how much of the wave-front is re-requested" metric, which no other
  variant produces. The defect's fix and a required output are the same mechanism.

Two hook-safety constraints follow, and they are binding on FP-1:

- **Never `Dictionary.Add`; always the indexer.** `Add` throws on a duplicate key — which, per the above, is a
  *routine* occurrence, not an error case.
- **Hooks must be non-throwing by construction.** The generation-discard hook sits inside HF-2's
  fault-isolation `try` (`WorldJobManager.cs:998–1020`); an exception escaping a telemetry call there would be
  caught and logged as a **job** fault, corrupting the pass's fault accounting and sending a future
  investigator after a nonexistent job bug.

---

## 5. Architecture

```
BenchmarkController  ──BeginPhase/EndPhase──▶  PipelineTelemetry (static, opt-in)
      │                                              │
      │ flies waypoints                              ├── ChunkTrace side table (coord → stage stamps)
      │ at escalating speeds                         ├── per-frame AdmissionSample ring buffer
      ▼                                              └── waste counters
World.Update ── guarded hooks ──────────────────────▶
  CheckViewDistance / DrainGenerationRequests  → Requested, Admitted, GateClosed   (World.cs:3361 / 3265)
  WorldJobManager.ProcessGenerationJobs        → Populated, DiscardedOutOfRange    (WorldJobManager.cs:948)
  LoadOrGenerateChunkInner (disk arm)          → Populated, LoadStranded           (World.cs:1032 / 1053)
  WorldJobManager.ProcessLightingJobs          → Lit (+ pass count)                (WorldJobManager.cs:1393)
  MeshCompletionDriver.MergeJob / apply        → MeshApplied  [TERMINAL]           (MeshCompletionDriver.cs:47)
  UnloadChunks (both unload arms converge)     → UnloadedBeforeMeshApplied         (World.cs:3123, v1.4)
                                                     │
                              BenchmarkReportGenerator ◀── new "Pipeline" report section
```

**Six sites, eight call sites** (as built in FP-1): `Populated` has both a generation arm and a disk-load
arm, and the disk arm's method also carries the `LoadStranded` guard. The sixth site — the unload hook — was
added during FP-1; see the disposition note below for why its absence would have mis-scored the ordering
regime.

**All five sites verified main-thread** at the re-audit: they are `World.Update` steps at `World.cs:2011`,
`2054`, `2048`, `2069` and `2249`, plus the `await` continuation of the load path, which resumes on Unity's
main-thread synchronization context. No hook touches job code, so the telemetry needs no synchronization and
nothing is read from inside a Burst job.

> **v1.1 — the chain is four stamps, not five.** `MeshApplied` and `Visible` were listed as separate hops;
> **MP-6 collapsed them into the same instant.** `MeshCompletionDriver.MergeJob` triggers the load animation
> immediately after the apply that earned it (`MeshCompletionDriver.cs:47–48`), and `World.Update` now states
> outright that "there is no step 8" (`World.cs:2274–2276`) — the draw queue that used to separate the two is
> gone. **Decision: `Visible` is dropped and `MeshApplied` is the terminal stamp.** The chunk is on screen the
> moment `ApplyMeshData` uploads; the load animation is a fade the player watches *start* at that instant, so a
> separate stamp would have measured a fixed animation duration, not pipeline latency. The stage chain is
> therefore **enqueue → populated → lit → mesh applied**.
>
> One consequence for FP-2: `ProcessLightingJobs` takes **no** `Window` (`WorldJobManager.cs:1393`) — its merge
> is deliberately unbudgeted (§2/P-3 owns it), so it contributes a `Lit` stamp but **no stop reason**.

**Two record types.** `ChunkTrace` is one struct per chunk (coords + a stamp per stage + a lighting-pass
counter + a terminal disposition). `AdmissionSample` is one struct per frame (queue depths, gate
open/closed, and a per-pass stop reason).

**Dispositions — seven, not four (v1.4, as built through FP-1).** The planning pass named four; building the
side table and wiring the hooks forced three more, each load-bearing rather than cosmetic:

| Disposition                 | Meaning                                                                                                    |
|-----------------------------|--------------------------------------------------------------------------------------------------------------|
| `Pending`                   | In flight — no terminal event yet. The zero value.                                                           |
| `MeshApplied`               | Reached the terminal stage. **The only disposition that contributes latency samples.**                       |
| `DiscardedOutOfRange`       | Generation result discarded — chunk left the unload boundary mid-flight (`WorldJobManager.cs:1012–1020`).    |
| `LoadStranded`              | Disk load thrown away — chunk unloaded or pool-recycled mid-read (`World.cs:1032–1042`).                     |
| `Rerequested` *(v1.3)*      | Superseded by a fresh request for the same coord — the §4.1 flush. **This count IS the re-request metric.**   |
| `InFlightAtPhaseEnd` *(v1.3)* | The phase ended first. **Not waste** — kept distinct so an unfinished chunk is never booked as discarded work, which would inflate the waste % the ordering verdict reads. |
| `UnloadedBeforeMeshApplied` *(v1.4, restored)* | Unloaded mid-flight: every stage the chunk completed was thrown away because the player outran it. **Waste, and the ordering-bound signal proper.** |

The alternative to `InFlightAtPhaseEnd` was to drop those traces (silent loss) or fold them into a waste
bucket (corrupting an input to §7.1). Neither is acceptable in an instrument whose whole purpose is to be
believed.

> **v1.4 — `UnloadedBeforeMeshApplied` was wrongly dropped in v1.3 and is restored.** v1.3's six-row table
> *replaced* the original fourth disposition instead of adding to it. FP-1 exposed the consequence: a chunk
> populated and then unloaded because the player flew past had **no hook at all**, so it surfaced as
> `Rerequested` (if later re-requested) or — worse — as `InFlightAtPhaseEnd`, which this document explicitly
> defines as *not* waste. The single most characteristic ordering-bound event would have been recorded as a
> benign one. The hook lands at the point where both unload arms converge (`World.cs`, after the
> `Unload` / `UnloadPersistLightPending` switch), is read-only, and cannot double-count a completed chunk —
> a trace that reached `MeshApplied` is already closed and removed, so the later unload stamp is a no-op
> (verified).

**Buffering, as built.** The per-frame ring is a **bounded rolling window** for post-hoc inspection; the
stop-reason and disposition **tallies it feeds are exact and unbounded**. Only the window can wrap, and it
reports that separately (`FrameWindowWrapped`) from true data loss (`TracesSaturated` / `SamplesSaturated`).
This is what lets §7.2 promise complete tallies without an unbounded per-frame buffer.

**Stop-reason attribution is the admission-bound signal** and is the one genuinely new thing the engine must
report: `PipelinePassBudget` already computes the quota and window, so each budgeted drain loop returns *why*
it stopped rather than just stopping.

### 5.1 Stop reasons — five values, not three (v1.1)

v1.0 specified `Quota` / `Ceiling` / `OutOfWork`. Reading the four budgeted loops shows two more break
conditions that are **not** reducible to those, and conflating either one mis-attributes the regime:

| Reason        | Break site                                                                                                       | What it means                                                                                      |
|---------------|--------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| `Quota`       | `lightJobsScheduled >= lightQuota` (`World.cs:2154`); `scheduled >= quota` (`MeshDrainPolicy.cs:54`)              | Rate budget spent. **Admission-bound.** Unreachable for the two ceiling-only completion passes.      |
| `Ceiling`     | `window.Expired` — all four passes (`World.cs:2154`, `MeshDrainPolicy.cs:54`, `WorldJobManager.cs:972`, `1241`)   | Main-thread ms slice spent. **Admission-bound**, but hitch-driven rather than rate-driven.           |
| `InFlightCap` | `LightingJobs.Count >= inFlightLightCap` (`World.cs:2155`); `host.InFlightCount >= cap` (`MeshDrainPolicy.cs:60`) | The OM-1 **memory** bound bit, not a throughput budget. Points at job latency/RAM, not at the knobs. |
| `AllDeclined` | Queue fully walked, nothing scheduled — `TrySchedule` false (`MeshDrainPolicy.cs:72`) / `ScanAction.Park` (`World.cs:2221`) | Work exists but **no chunk is eligible**: a readiness gate is failing upstream.                      |
| `OutOfWork`   | Loop ran to completion with work served                                                                            | Healthy. The pipeline is keeping up.                                                                 |

`AllDeclined` is the load-bearing addition. Without it, a pass that walks its whole queue and schedules
**nothing** because `AreNeighborsReadyAndLit` keeps failing is indistinguishable from `OutOfWork` — i.e. a
stalled pipeline would be reported as a **healthy** one. That is the single most dangerous misreading this
instrument could produce, and it is why §7's verdict gains a fourth regime.

> **v1.3 (as built in FP-0) — a sixth enum value, `NotRun = 0`, which is *not* a break reason.** The five
> above are outcomes of a pass that executed. A default-initialized sample would otherwise report the
> **first** value for a pass that never ran that frame, and with `OutOfWork` in that slot the instrument
> would silently claim "ran, nothing left to do" — a different and flattering claim — for passes that were
> skipped entirely (the mesh drain is skipped outright when its queue is empty or its in-flight cap is
> already reached, `World.cs:2257`). `NotRun` therefore takes the zero slot and is **never tallied**:
> `RecordPassStop` rejects it, so it can only ever appear in the rolling per-frame window, never in the
> histogram §7.1's verdict reads.
>
> **Binding consequence for FP-2:** the pass-skipped early-outs must record their *real* reason explicitly.
> The mesh drain's entry gate reaching the in-flight cap is an `InFlightCap` stop and must be recorded as
> one — leaving it to fall through as `NotRun` would silently drop a genuine admission-bound signal.

### 5.2 Decision: the stop reason is returned by the pure policies (v1.2)

Three placements were considered for *where* the reason is produced. The choice is not stylistic — it decides
whether FP-2 is testable at all.

| Option                                                | Verdict                                                                                                                                                                                              |
|-------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Return it from the pure policies** ✅ **CHOSEN**     | `MeshDrainPolicy.Drain` returns a `DrainResult { int Scheduled; StopReason Reason; }` instead of `int`, and the lighting scan's break conditions move into a matching pure helper. Both become **edit-mode drivable**, so the reason gets real baselines. |
| `out StopReason` parameter                            | Same testability, smaller diff — but every existing decision type in this engine (`ChunkUnloadDecision`, `PoolPruneDecision`, `SeamWakeDecision`, `LightingScanDecision`) *returns* its verdict. An out-param on a hot pure policy breaks that pattern for no gain. |
| Re-derive it in `World` after the pass                | **Rejected.** Zero production diff, but it duplicates the loop's break conditions in a second place that can silently disagree with them (re-reading `window.Expired` *after* the loop reports `Ceiling` for a pass that actually stopped on quota), and it lives in `World.Update`, where **no suite can reach it** — see §7's guard limitation. |

**Cost, as built:** the return-type change touched 10 call sites — `World.cs` (which now consumes the reason)
plus **9 mechanical `int scheduled = …` → `….Scheduled` edits** in
`Assets/Editor/Validation/Meshing/MeshingValidationSuite.Scheduling.cs` (B25/B26). Rename-shaped work, landed
in the same commit as the signature change.

**Payoff:** FP-2 stops being an unguarded observation layer. The reason set is suite-pinned by **B8**, so a
future change that quietly makes a pass stop for a new reason fails a baseline instead of silently
mislabeling every subsequent capture.

#### 5.2.1 As built (FP-2) — three decisions the plan did not anticipate

1. **`PipelinePass` and `PassStopReason` live in `Helpers`, not `Benchmarks`.** FP-0 declared them beside the
   telemetry; FP-2 exposed the layering problem that creates — `MeshDrainPolicy.Drain` would have to *return*
   a benchmark type, making a core scheduling policy depend on the diagnostic layer. They now sit in
   `Helpers/PipelinePassBudget.cs` alongside the quota/window math they describe, and `PipelineTelemetry`
   consumes them. The stop reason is a property of the **pass**, not of the instrument that reports it.

2. **One shared classifier, `PipelinePassBudget.ClassifyStop`.** Both scheduling loops break on the same three
   limits in the same order, so rather than two parallel implementations they route through one pure function.
   The two passes therefore cannot drift on what a stop *means*, and a single baseline (B8) pins both.

3. **`JobCompletionPass.RunMergeLoop` returns `bool` (did the ceiling break the loop?).** The same
   return-don't-re-derive principle as §5.2, applied to the two ceiling-only completion passes: re-reading
   `window.Expired` after the call would report a ceiling stop for a pass that finished all its work and only
   *then* ran out of window. Source-compatible — every existing caller ignores the value.

**One subtlety worth recording, because getting it wrong would fake a readiness stall.** The lighting scan's
candidate count deliberately **excludes stale ready-set entries** (chunks already unloaded, which the scan
launders away). They are bookkeeping, not work the pass declined to serve — counting them would let a mass
unload present as `AllDeclined` and score a healthy pipeline as readiness-bound. A parked *placeholder*, by
contrast, **does** count: that is genuinely "work exists but is not yet eligible".

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                              |
|-------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Chunk-granular only; no per-voxel records exist anywhere in the design.                                                 |
| Burst jobs 100 % Burst-compatible               | No job code is modified. All hooks sit on main-thread completion/scheduling paths.                                      |
| No GC / LINQ in hot paths                       | Pre-sized ring buffer + pre-sized dictionary, both struct-valued; disabled path is one static-bool branch per hook (the `WorldFrameProfiler` pattern). Report strings are built once, at phase end. |
| Pooling conventions                             | **Deliberately adds no field to any pooled type** (§4) — no `Reset()` obligation, no pool-recycle staleness class.       |
| No BinaryFormatter/JSON for terrain             | Output is a text report through the existing `BenchmarkReportGenerator`; no terrain data is serialized.                  |
| BlockIDs constants, no raw IDs                  | Not applicable — no block identity is recorded.                                                                         |

---

## 7. Phased implementation plan

| Phase                             | Scope                                                                                                                                          | Effort | Depends on |
|-----------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------|
| **FP-0 — Telemetry core** ✅ **DONE** | `Benchmarks/PipelineTelemetry.cs`: `Enabled` flag + domain reset, `ChunkTrace`/`AdmissionSample` structs, side table, rolling frame window + exact tallies, phase begin/end, `EstimateTraceCapacity`. Modeled on `WorldFrameProfiler`. |   🟢   | —          |
| **FP-1 — Stage stamps** ✅ **DONE** | Guarded hooks at **six** sites / eight call sites (§5) producing the **four-stamp** chain; terminal dispositions incl. both previously-uncounted discards (generation out-of-range *and* the disk-load stranding, §8 Q2) plus the mid-flight unload restored in v1.4. |   🟡   | FP-0       |
| **FP-2 — Admission pressure** ✅ **DONE** | Per-frame sampling: queue depths, panic-gate state (**sampled from the existing `World` probes** — §2 — not newly instrumented), and the per-pass stop reason (§5.1) returned by the pure policies (§5.2/§5.2.1), pinned by **B8**. |   🟡   | FP-0       |
| **FP-3 — Report section** ✅ **DONE** | `Benchmarks/TraceStatistics.cs` + `PipelineRegimeVerdict.cs` (both pure, so B9/B10 can pin them) + `PipelineReportSection.cs`: stage-latency distributions per speed phase — **normalized by each phase's own `DurationSeconds`**, since the last generation phase is not 30 s (§2) — waste accounting, gate-closed %, full stop-reason tallies, the §7.1 verdict, the **§7.2 raw-results block**, and the §8 Q1 saturation banner. `BenchmarkController` drives both recorders through one paired `BeginPhaseBoth`/`EndPhaseBoth`. |   🟢   | FP-1, FP-2 |
| **FP-4 — Capture + verdict** ✅ **DONE** | Captured 2026-07-27 in an **IL2CPP Development Build** at commit `73de6511`; report and verdict in [`../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md). **Verdict: ordering-bound + admission-bound** in the loading pass ≥ 50 m/s; throughput- and readiness-bound both ruled out by the raw counts. Exposed a defect in §7.1 v1 itself — see §7.1.1. |   🟢   | FP-3       |

| **FP-5 — fix the run-boundary phase leak** ✅ **DONE** | `PipelineTelemetry.BeginRun()` (public, calls the existing `DomainReset` body) called from `BenchmarkController` at run start, so a second run in one process no longer reports the first run's phases. Guarded by **B11**; prove-red confirmed. |   🟢   | FP-4       |
| **FP-6 — print the pipeline settings** ✅ **DONE** | `PipelineSettingsSnapshot` captured at run start and rendered as its own report block, grouped by the stop reason each knob produces (§7.4). Guarded by **B12**. |   🟢   | FP-4       |

**FP-0…FP-3 deliver standalone value**; FP-4 is the deliverable that actually arbitrates the next
optimization. **FP-5 and FP-6 are defects FP-4 exposed in the instrument itself** — they are not extensions,
and the ranked follow-ups in §7.3 put them ahead of any further capture.

**What is and is not guarded.** This is an instrument, not engine behavior, so it gets no scenario suite of
its own. Three things *are* checkable and should be:

1. **`Validate All` stays green with telemetry enabled.** ⚠ **v1.2 correction — this is a PARTIAL guard, and
   v1.1 overstated it.** The suites run in **edit mode with no `World` instance**, so hooks that live inside
   `World.Update` are never executed by them; flipping `Enabled` proves nothing whatsoever about those. It is
   a genuine test only for hooks inside code the suites actually drive — `MeshDrainPolicy.Drain` and the real
   `MeshCompletionDriver` (B24/B25/B31–B33). This is a further argument for §5.2's placement decision: putting
   the stop reason in the pure policies is what moves it from the unguarded side of this line to the guarded
   side. Read the enabled-run as *"the suite-reachable hooks are inert"*, never as *"the hooks are inert"*.
2. **FP-3's percentile selection is pinned by a baseline** (**B9**) — a wrong percentile silently mis-ranks
   every future capture. This requires the percentile math to be a **pure static**
   (`Benchmarks/TraceStatistics.cs`) callable from edit mode with no `World`; burying it in
   `BenchmarkReportGenerator`'s private code would make the baseline impossible to write.
3. **§7.1's verdict rule is pinned** (**B10**) — pure arithmetic over the counters, so a rule that silently
   changes meaning between captures is a baseline failure rather than a surprise. Two reports produced by
   different rules are not comparable, and nothing in a report would reveal that on its own.

> **v1.6 — these live in `Validate Pipeline Backpressure`, not the ChunkMath suite as v1.0 proposed.**
> ChunkMath is scoped to *coordinate and addressing* math (its own docstring says so, and it deliberately
> avoids even a namespace that would shadow `Helpers.ChunkMath`). Percentile selection and a regime verdict
> are pipeline-capture math, and Pipeline Backpressure already owns exactly that — the quota/window/ceiling
> math plus FP-2's `ClassifyStop` (B8). Putting them there keeps one suite per subject rather than filing
> capture statistics under coordinate math, where a future reader would not think to look.
4. **The stop-reason classifier is pinned by `Validate Pipeline Backpressure` B8** (added in FP-2):
   precedence across the three limits, and — the load-bearing pair — `AllDeclined` never collapsing into
   `OutOfWork` while an *empty* queue still reports `OutOfWork`. Prove-red confirmed by temporary mutation:
   collapsing the readiness arm turns exactly B8 red on exactly that assertion.

Allocation-freedom of the disabled path is **not** assertable on editor Mono (project precedent) and is
verified by inspection plus an IL2CPP GC-alloc read.

### 7.1 The verdict rule — pre-committed, mechanical, auditable (v1.2)

v1.0/v1.1 required FP-4 to "state which regime the numbers show" without saying what *decides* it, which
would have made the deliverable a judgment call dressed as a measurement. The rule is therefore fixed **now**,
before any number exists, so it can be argued with on its merits rather than fitted to a result:

| Signal (highest-speed phase of the relevant pass)          | Verdict            |
|-------------------------------------------------------------|--------------------|
| `Quota` or `Ceiling` dominates the stop-reason histogram     | **Admission-bound** |
| `InFlightCap` dominates, or stage latency dominates the hop breakdown with no dominant stop reason | **Throughput-bound** |
| `AllDeclined` dominates                                      | **Readiness-bound** |
| Waste fraction ≥ **20 %** of terminal traces | **Ordering-bound** |

Ordering is deliberately established on a **different axis** from the other three: it is a property of *which*
chunks were served, not of *why* a pass stopped, so it is read from the waste distribution rather than the
histogram, and it can co-occur with any of the other three — including `Healthy`, which is the shape the
reported flight symptom is most likely to take.

> **v1.6 — the ordering criterion changed during FP-3, and the change is a narrowing.** v1.2 specified "waste %
> high **and** the enqueue→applied gap concentrated in chunks already behind the player". The second clause is
> **not computable from what the capture records**: a `ChunkTrace` holds coords and stamps, but the player's
> position at each moment is never sampled, so "behind the player" has no operand. Rather than add
> player-position tracking (scope) or quietly drop the clause, the criterion is now the waste fraction alone —
> which is well-founded rather than a proxy, because every disposition it counts (`DiscardedOutOfRange`,
> `LoadStranded`, `UnloadedBeforeMeshApplied`) *literally means the chunk left range while work was in flight*.
> Waste is therefore already "work completed for chunks the player flew past"; the deleted clause was
> restating it less measurably.
>
> **The 20 % threshold is a judgment call, pre-committed before any capture existed** (`OrderingWasteThreshold`).
> One chunk in five is clear of the incidental churn a turning flight path produces, while still firing long
> before the pipeline is spending most of its budget on discarded work. It is a named constant precisely so a
> future session can disagree with it and recompute from the raw counts §7.2 mandates.

**"Dominant" means the plurality bucket of the phase's stop-reason histogram.** No margin threshold is
imposed — a near-tie is reported as a near-tie in the numbers, which §7.2 guarantees are present, and the
verdict names the plurality while the report shows the runner-up. This deliberately prefers an actionable
verdict with visible ambiguity over a "mixed regime, no single fix indicated" outcome that would leave the
capture having arbitrated nothing.

### 7.1.1 The rule has a defect, and FP-4 found it (v1.7)

**FP-4's first act was to falsify the rule it was pre-committed to.** Recorded here rather than quietly
patched, because §7.1 exists to be arguable and a rule that is silently corrected after seeing a result is
worth nothing.

The rule sums each stop reason **across all four passes**. But `GenerationProcess` and `MeshProcess` are
**ceiling-only** passes: they can emit only `OutOfWork` or `Ceiling`, never `Quota`, `InFlightCap` or
`AllDeclined`. The capture confirms this empirically — across 8 phases × 2 passes × 3 reasons, **all 48 cells
are zero**. Two passes that can vote for exactly one of the five outcomes are therefore added to two passes
genuinely contesting all five, and they reliably contribute ~100 % `OutOfWork`.

The consequence is not academic. At loading 200 m/s the plurality came down to **68 frames out of 27 744**
(OutOfWork 50.0 % vs Quota 49.8 %) and the rule printed *Healthy*. Restricted to the two passes that actually
have an admission budget, the same raw numbers give **Quota at 99.5 %** — decisively admission-bound. Across
the three loading phases the scheduling-pass-only plurality is Quota at **83.1 / 97.2 / 99.5 %**.

**The rule was not fed bad data; it aggregated good data wrongly.** §7.2 is what made this recoverable — the
correction needed only the printed tallies, not a re-capture on a build that no longer exists. Treat this as
the strongest available argument for §7.2 as a standing requirement.

**Not fixed here** (§9.5 — FP fixes nothing, and amending the rule would break comparability with the report
that found it). A §7.1 v2 should restrict the plurality to passes capable of expressing the contested reason,
or abandon the single-plurality framing for a per-pass regime vector. Whoever writes it must bump
`RULE_VERSION` so the two generations of report are never silently compared.

> **v1.11 — FIXED by FP-7e (§7.4.2).** The first option was taken, generalised: eligibility is per
> **(pass, reason)** rather than per pass, and shares are measured against each reason's own eligible
> opportunity. Note that **this section's own diagnosis was partly overtaken** — it says `GenerationProcess`
> "can emit only `OutOfWork` or `Ceiling`", and FP-7b showed that was never true: the pass carries a
> structure-mods quota, and the 48 all-zero cells the capture cited were evidence that the quota *was not hit
> during those phases*, not that it could not be. The empirical check was sound; the inference from it to a
> capability claim was not. `RULE_VERSION` is bumped to v2.

### 7.2 Raw results are mandatory — the verdict never replaces them (v1.2)

**A capture that reports only its conclusion has failed, exactly as surely as one that reports no
conclusion.** §7.1's rule is a convenience for the session that runs the capture; it must never become the
only thing a later session can read. Every report FP-3 emits therefore carries, per phase and per pass:

- **Full stop-reason tallies** — every one of the five values with its raw frame count, never only the winner.
- **Stage-latency distributions** — exact `count`, `min`, `p50`, `p95`, `p99`, `max` per hop, **plus a
  fixed-bucket histogram** of the underlying samples, so a reader can recompute a different statistic
  (approximately) rather than being limited to the percentiles chosen today.
- **Raw waste and disposition counts** — each terminal disposition, the flush-and-restart (re-request) count
  from §4.1, and the two previously-uncounted discards, as absolute numbers alongside any percentage.
- **The verdict's complete input vector, printed verbatim**, immediately above the verdict line, with the rule
  version that consumed it — so disagreeing with §7.1 requires only reading the same report, not re-running
  the capture on a build that may no longer exist.
- **The saturation banner** (§8 Q1) whenever a buffer filled, since every number above is then a prefix of the
  phase rather than the whole of it.

**Known limitation, stated rather than hidden:** the histogram supports *approximate* recomputation of
arbitrary percentiles, not exact. Exact re-derivation over every individual chunk trace is the **v3+ per-chunk
CSV export** in the roadmap below, and remains out of scope here. If a future capture's analysis is blocked by
bucket resolution, that is the demand case that promotes the v3 item — record it rather than widening FP-3.

> **v1.7 — the demand case arrived on the first capture.** FP-4 found a thin catastrophic tail (at 20 m/s,
> ~95 % of chunks land in a tight ~2.5 s cohort while **~1 % take 24–30 s**), and that tail is the most
> plausible source of the *felt* symptom — a mostly-complete world with occasional half-minute holes. It
> cannot be attributed from aggregates: the `populated→lit` and `lit→meshApplied` p99s **sum to more than the
> phase's own maximum end-to-end latency**, which proves they belong to different chunks, i.e. there are two
> distinct stall populations rather than one slow stage. Separating them needs per-chunk traces. This is a
> recorded demand case for the v3+ CSV export, per the rule above.

### 7.3 Ranked follow-ups — **re-ranked by FP-8 (v1.12), extended by FP-10 (v1.15), and re-ranked again by the P-8 result (v1.16)**

The capture's whole purpose was to decide what to do next, so the ranking is recorded **here** — the report
that produced it is a point-in-time, append-only artifact and cannot be kept current. Engine items are
mirrored in the master backlog; instrument items are owned by this document.

> **FP-8 reversed the top two, and the cause is FP-7a rather than any change in the engine.** FP-4 counted
> requests the panic gate never admitted as *waste*, which inflated the ordering signal most at exactly the
> view distances where the gate closes hardest. Rescored correctly, ordering-boundness **decays** with view
> distance instead of growing: 37.8 → 38.0 → 36.2 → 19.8 → 14.6 % at vd 5/8/10/15/20 (loading, 200 m/s),
> where the v1 rule gave 37.8 → 41.4 → 47.1 → 53.0 → 62.2 %. Derivation in
> [`../Performance/CHUNK_PIPELINE_FP8_FLIGHT_PROFILE_IL2CPP_2026-07-31_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP8_FLIGHT_PROFILE_IL2CPP_2026-07-31_BENCHMARK.md).

> **FP-10 reproduced that curve on a completely different route, so the ranking stands on evidence rather than
> on one rescoring.** Loading @ 200 m/s: 38.5 / 43.2 / 36.6 / 19.5 / 13.7 / 8.6 % at vd 5/8/10/15/20/32,
> against FP-8's 37.8 / 38.0 / 36.2 / 19.8 / 14.6 — four of five overlapping points within ~1 pt, despite
> FP-9b having rebuilt the route underneath. What FP-10 adds is the **mechanism** (§F3) and a **constraint on
> the fix** (§F4). Derivation in
> [`../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md).

> **The P-8 result overturns this ranking's top item, and the reader should start here.** FP-8 and FP-10
> both reasoned that a fixed 256/128 backlog threshold against a resident square growing as vd² was what
> held admitted work down, and ranked scaling it #1. **The fix was built, measured across ten IL2CPP Release
> runs with same-build controls, and returned NO-GO** — see
> [`../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_P8_GATE_SCALING_IL2CPP_2026-08-01_BENCHMARK.md).
> At vd 32 a **4.2× larger threshold changed gate closure by 0.1 points** (94.6 % vs 94.5 %), because the
> backlog simply grows to meet whatever bar it is given; admitted work rose 0.2 %, completions fell 16 %, and
> loading-pass minimum FPS fell ~⅓ at vd 26 and vd 32.
>
> **The inference that has to be corrected is FP-10 F2's second half.** F2 observed that completion-of-admitted
> has no trend with view distance and concluded "the pipeline's efficiency on the work it accepts does not vary
> — only its willingness to accept does." The willingness was **downstream** of a throughput ceiling: the
> lighting and mesh schedules report `Quota` on 99 %+ of frames at high view distance in **both** legs, and
> completions sit in a 5 658–6 803 band across vd 10 → 32 regardless of what admission does. The gate was not
> choosing to refuse work; it was reporting that lighting could not keep up.
>
> **Consequence for the "not licensed" note below:** it stands for in-flight-cap and readiness work, which
> were tested for and are absent. It does **not** extend to schedule-quota throughput, which this capture
> identifies as the binding constraint at high view distance — reversing FP-4's deprioritisation of
> throughput work *for that regime only*.

| # | Item | Home | Why it ranks here |
|---|------|------|-------------------|
| **1** | **Schedule-quota throughput at high view distance** ⬅ **new #1, promoted by the P-8 NO-GO** | **`P-9`** — filed in [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md); **design doc: [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md)** (2026-08-01), which answers the question in this cell — the quota is a *rate* (`cap × 60` items/second) whose terms contain neither view distance nor frame rate, so ~6 500 completions/phase is what a fixed rate divided by a fixed per-chunk cost looks like. Its phase **P9-0 extended this instrument** — per-pass main-thread ms, quota utilisation, work amplification and parked time — and **P9-1 captured with it on 2026-08-02** ([report](../Performance/CHUNK_PIPELINE_P9_1_ATTRIBUTION_IL2CPP_2026-08-02_BENCHMARK.md)), confirming the rate identity within 4 % across vd 10→32 and establishing that the instrumented pipeline is **~69 % of the main thread, view-distance-invariant** | The P-8 capture identifies the binding constraint by elimination *and* by direct measurement: `LightSchedule` reports `Quota` on **99.3 % of frames** at vd 32 / loading 200 m/s with scaling ON and **99.5 %** with it OFF; `MeshSchedule` likewise; `InFlightCap` and `AllDeclined` dominate no phase in any of the ten runs. Completions sit in a **5 658–6 803 band across vd 10 → 32 in both legs** — a ceiling far more stable than anything admission does. The question is no longer "how do we admit more?" but "why can the pipeline only finish ~6 500 chunks per 30 s phase regardless of what it admits?". Note this **reverses FP-4's deprioritisation of throughput work** for the high-vd regime specifically; FP-4 measured a different regime under a different rule. |
| **2** | **Chunk service ordering** — **re-scoped to low view distance** ⬅ was #2, unchanged in substance | **`P-7`** — [analysis §6](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) + [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) | Unchanged by the P-8 result except for one confirmation: waste **rose** in the scaled leg (loading 200 m/s, vd 32: 17.9 % ON vs 10.7 % OFF), which is what the FP-10 blockquote predicted would happen if the gate stopped suppressing ordering waste by refusing work. Worst case remains **vd 8 / 200 m/s**; acceptance criterion remains the visibility bound (row 4). |
| **3** | **Adopt the visibility criterion as the acceptance target** | This doc + P-7's design | `latency ≤ viewDistance × 16 ÷ speed`. Falsifiable, matches independent visual observation across three legs, and gives (1) a target number rather than "less waste". |
| **4** | **P-8 — residency-scaled panic-gate thresholds** ⬅ **was #1, NO-GO 2026-08-01, parked** | **`P-8`** — same two docs | Built, measured, refuted: 1.58× admitted growth across vd 5 → 32 against a pre-committed ≥ 3.0×, at a ~⅓ loading-pass min-FPS cost (see the blockquote above). Code and its **B19** guard are retained behind `scalePanicGateThresholdsWithResidency`, **default-OFF**. **Not dead — premature.** Re-test it after row 1 moves the throughput ceiling, at which point the gate becomes binding again and a residency-scaled threshold is the right shape. The one leg that behaved as designed was vd 8 (closure 42 % → 24 %). |
| **5** | ~~**FP-5 — fix the phase leak across runs**~~ ✅ **DONE 2026-07-28** | **this doc, §7.4** | Was blocking trustworthy multi-run sessions. Fixed via `BeginRun()`; guarded by **B11** (Validate All → 359). |
| **6** | ~~**FP-6 — print `LoadDistance` in the report**~~ ✅ **DONE 2026-07-28, widened** | **this doc, §7.4** | Became "print every knob that produces a stop reason", once it was clear the capture machine runs non-default quotas. Guarded by **B12** (Validate All → 360). |
| **7** | ~~**§7.1 v2 — fix the plurality dilution**~~ ✅ **DONE 2026-07-31** | **this doc, §7.4.2** | Shipped as FP-7e, as a per-(pass, reason) capability matrix rather than the "scheduling passes only" split this row assumed — FP-7b changed that premise. `RULE_VERSION` bumped to v2; guarded by the rewritten **B10**. |
| **8** | ~~**FP-9a — sample floor on the PRIMARY regime**~~ ✅ **DONE 2026-07-31** | this doc, §7.4.3 | Shipped as two mechanisms rather than one: a floor over eligible observations, plus `RegimeBearing` for phases that are not measurements — which no floor could have caught, the transition being comfortably over any sane threshold. Guarded by **B16**. |
| **9** | ~~**FP-9b — hold generation waypoints constant across a sweep**~~ ✅ **DONE 2026-08-01** | this doc, §7.4.3 | Shipped as an inversion rather than a patch: the route is now the input and the region the derived output, so waypoints are **constant at every view distance** and every phase runs its full duration. Guarded by **B17**. |
| **10** | ~~**FP-11a — measure and print ensure-pass tour coverage**~~ ✅ **DONE 2026-08-01** | this doc, §7.4.4 | **As built, it found a second defect and fixed that too:** the ensure sweep was walking waypoints 0..N-1 while the loading pass *loops* them, so the return leg went ungenerated — at vd 5 no other leg reaches within the load radius of it, so that strip was generated by the "loading" pass. The sweep now flies the closed circuit (~5.5 % longer) and coverage is reported at **two** instants — after the sweep, and when the loading pass starts — because the transition's job drain legitimately finishes work the gate deferred, and a single ensure-time figure would fail otherwise-clean captures. Guarded by **B18**. First results: 100 % at vd 5–15, 98.1–99.7 % at vd 20–32. Original filing: The ensure-generated sweep exists so the loading pass flies over generated terrain, but it is **subject to the same panic gate as everything else** — at vd 32 it was throttled on **92.3 % of its frames** with 9 324 requests abandoned, so its coverage is not guaranteed. The instrument cannot currently tell whether the high-vd loading pass measured loading or partly re-measured generation: `enqueue→populated` contains both admission wait and generation, and both were saturated. Check and print `chunks generated / chunks in tour` at the end of the pass. |
| **11** | **FP-11b — raise or make configurable the latency-sample cap** ⬅ **new** | this doc, §7.4.4 | 32 768 was reached at vd 32 / ensure-generated (35 517 completed chunks), so that block's percentiles cover a subset. FP-7's banner fired correctly, so **no number is silently wrong** — this is coverage, not correctness. |
| **12** | ~~**FP-11c — print the ensure sweep's speed and duration**~~ ✅ **DONE 2026-08-01** | this doc, §7.4.4 | Shipped with FP-11a; the route block now prints speed, distance and derived duration. Original filing: The derived-route block prints region, rows, route length, timed travel and tour size but not the ensure sweep's 50 m/s / 187.5 s — both derived, both needed to interpret FP-11a. FP-6 class; one line. |
| **13** | **Per-chunk CSV export** | Extension roadmap below (v3+) | Only way to separate F4's two stall populations. Demand case reinforced by FP-8 (vd 15 / gen / 50 m/s: p50 1 023 ms vs max 11 273 ms) and again by FP-10 (vd 32 / ensure: `populated→lit` p99 **149 559 ms**). |

**Not licensed by the capture:** any in-flight-cap or readiness work. Both regimes were tested for at three
view distances and are absent in all of them (`InFlightCap` ≤ 0.6 %, `AllDeclined` ≤ 7.8 %). **FP-10 re-tested
both across 60 phases at six view distances and neither dominated a single one** — `InFlightCap` peaked at
450 frames (vd 32 / ensure) against 18 970 `Quota` in the same phase.

#### 7.4.4 FP-10's instrument findings — and two guards confirmed in production

FP-10 filed one defect (FP-11a, above) and, unusually, **confirmed two earlier fixes with evidence the
edit-mode suite cannot produce**:

* **FP-7's trace-buffer banner fired for the first time in production** (vd 32 / ensure-generated). A Release
  capture self-reported a limit that the development-only asserts are compiled out of — which is exactly the
  case FP-7 was built for and could not otherwise be demonstrated.
* **FP-9a's two mechanisms were separated by the data.** At vd 5–20 the transition phase has 4 eligible
  observations, so the 1 000-observation floor alone would have suppressed it. At **vd 32** the transition has
  4 200 and ensure-generated has 77 896 — both over the floor, and **only `RegimeBearing` suppresses them**.
  §7.3 row 7 argued at the time that no floor could catch the non-measurement phases; that is now observed
  rather than reasoned.

### 7.4 FP-5 / FP-6 — instrument defects found by running it (v1.8)

Both were found *by* the capture, and both must land before the next one or its output is untrustworthy.

| Phase | Defect | Fix |
|-------|--------|-----|
| **FP-5** ✅ **FIXED** | **Telemetry phases leaked across benchmark runs in one process.** `s_completedPhases` was cleared only in `DomainReset`, i.e. once per play-mode entry / player start. `BenchmarkController` set `Enabled = true` at run start but never cleared the list, while `_metricsCollector.StartRecording()` resets its own — so the two recorders **disagreed at the run boundary** and a second run reported the first run's phases as its own. Observed: the vd-10 log carries all 9 vd-5 phases verbatim before its own 8. The *run-level* instance of exactly the desync FP-3's paired `BeginPhaseBoth`/`EndPhaseBoth` prevents at the phase level. | **As built:** public `PipelineTelemetry.BeginRun()`, called from `BenchmarkController` immediately before `Enabled = true` so both recorders start a run empty. **`BeginRun` is the caller of `DomainReset`, not the reverse** — a shared private helper is the obvious shape and is *wrong here*: UDR0002 requires every mutable static to be assigned **lexically inside** the `[RuntimeInitializeOnLoadMethod]`, and delegating outward trips it on `s_activePhase`. A comment at the site says so, because the natural "tidy-up" reintroduces the warning. Side effect worth knowing: `BeginRun` also clears `Enabled`, so callers restoring a saved flag must restore it *after* the call. |
| **FP-6** ✅ **DONE — and widened beyond the original scope** | **The report stated none of the tuning that produces its own stop reasons.** `LoadDistance` was the entry point (its absence caused v1.7's retracted "capacity model under-predicts 46 %" claim, since the wrong table row was read), but it is not the only one: a phase reporting `Quota` on 99 % of frames is uninterpretable without the quota, and the §7.1 rule turns exactly those tallies into a regime. Confirmed non-hypothetical, though **not** for the reason first recorded — see §7.4.1. | **As built:** `Benchmarks/PipelineSettingsSnapshot.cs` — 18 values captured at run start (not read at report time: settings are editable mid-session, so the report must state what the run *used*) and rendered by `AppendTo` into a new **`=== Pipeline Settings (as used by this run) ===`** block, **grouped by the stop reason each knob produces** (quotas → `Quota`, in-flight caps → `InFlightCap`, ms ceilings → `Ceiling`, panic gate → *no* stop reason, visible only as the per-phase gate-closed %). Prints the derived resident square and the F5 ratio. `_loadDistanceForCapture` is subsumed by the snapshot. Rendering lives **on the snapshot**, not in `BenchmarkReportGenerator`, so a field added to the capture cannot be silently left unprinted. Guarded by **B12**. |

| **FP-7** ✅ **DONE — five defects, all in what the numbers MEAN** | **The instrument mis-measured four of its own inputs, and the §7.1.1 rule defect was still unfixed.** Found by code review of the FP-1…FP-6 commits rather than by a capture, but every one of them moved a shipped verdict. See §7.4.2 for the five, the evidence for each, and what it costs the FP-4 report. | **As built:** `AbandonedBeforeAdmission` disposition + `StampUnloaded` (a); `ClassifyStop` wiring + `genModsQuotaSpent` (b); `lightCandidatesSeen--` on the flag-less arm (c); the `LoadStranded` stamp deleted (d); §7.1 **v2**'s capability-weighted plurality + a `RecordPassStop` staleness assert (e). Waste predicates moved onto `PipelineRegimeVerdict` so the verdict and the table under it cannot disagree. Guarded by **B13**/**B14**, B10 rewritten; Validate All 360 → **362**. |

#### 7.4.1 Why a capture's settings are genuinely undeterminable without printing them (v1.10)

An earlier draft of §7.4 justified FP-6 by claiming the FP-4 sweep ran at non-default quotas, citing values
read from `SettingsManager` **in the editor**. That claim is **retracted**: settings persist to
`Application.dataPath + "/settings.json"` (falling back to `persistentDataPath`), so the editor and every
player build keep **separate files**, and an editor reading says nothing about what a player build ran with.

The corrected justification is stronger, and rests on three verified mechanisms rather than one observation:

1. **OM-1 device calibration overwrites four of these very settings at startup.**
   `SettingsManager.ApplyCalibration` writes `maxMeshRebuildsPerFrame`, `maxLightJobsPerFrame`,
   `maxInFlightMeshJobs`, `maxInFlightGenerationJobs` (plus pool retention) from
   `DeviceCalibration.Resolve()`, which derives them from system RAM *and* a `StartupCalibrationProbe`
   micro-benchmark. They are therefore **device-dependent, and not knowable from any checked-in default**.
   It is skipped in edit mode (`Application.isPlaying` guard), so the editor and a player can legitimately
   disagree on all four.
2. **Per-platform settings files**, per the retraction above.
3. **Benchmark mode's "deterministic gameplay settings" intent is defeated by the settings cache.**
   `LoadSettings()`'s benchmark branch builds a fresh `new Settings()` and overlays only three fields
   (`benchmarkRegionSize`, `benchmarkGenerationSpeeds`, `benchmarkLoadingSpeeds`) — but **only if
   `s_cachedSettings` is null**. The cache is cleared solely by `ResetStatics()` at process start, never on a
   mode switch, and the normal route into a benchmark is via the main menu, which loads settings first. So a
   benchmark launched the usual way runs on the **player's real settings**, and the deterministic-defaults
   path is effectively unreachable. This is visible in the FP-4 data itself: waypoint counts are derived from
   `LoadDistance` and differ across the three runs (12 / 6 / 4), which the defaults path could not produce.

**Conclusion: what a capture ran with is not derivable from code inspection** — it depends on cache warmth,
on which platform's settings file exists, and on a device-specific calibration probe. Printing the values at
run time is the only thing that settles it, which is precisely FP-6. **The snapshot is correct by
construction here:** it reads the same shared `LoadSettings()` instance the `World` uses, so it records
whatever actually applied, regardless of which path produced it.

> **Open question, deliberately not answered here:** whether benchmark mode *should* force deterministic
> settings (making captures comparable across machines but no longer representative of real play), or keep
> using live settings (representative, but only comparable when the printed block matches). Both are
> defensible; changing it alters benchmark behaviour and needs its own decision. FP-6 makes either choice
> *auditable*, which is why it was worth doing first.

> A third, lower-severity gap from the same capture: **generation waypoint counts differ per run** (12 / 6 / 4
> at vd 5 / 10 / 20), so the generation route is not held constant and cross-run *generation* comparisons are
> confounded. The loading pass uses 12 everywhere and is comparable. Either hold waypoints constant across a
> sweep, or print them prominently enough that the confound cannot be missed — they are currently in the
> Configuration block but easy to overlook.

#### 7.4.2 FP-7 — five measurement defects found by reviewing the instrument, not by running it (v1.11)

FP-5 and FP-6 were found *by* a capture. FP-7's five were found by a code review of the FP-1…FP-6 commits.
That difference matters for how much confidence to place in the FP-4 numbers: a capture only surfaces defects
that make the output *look* wrong, and every one of these made it look *right*.

| # | Defect | Evidence it is real | Fix |
|---|--------|---------------------|-----|
| **a** | **Requests the panic gate never admitted were counted as waste.** `UnloadChunks` stamped `UnloadedBeforeMeshApplied` on every unloaded chunk still holding a trace — including placeholders created by `CheckViewDistance` that were never admitted, for which no stage ran. Waste is the *sole* input to the ORDERING-BOUND axis. | Structural: `GetOrCreatePlaceholder` creates the `ChunkData` at request time and `StampRequested` fires there, while `IsLoading`/`AdmittedTicks` are only set at admission. An un-admitted placeholder has no job, no light flags and cannot strand a neighbour, so `ChunkUnloadDecision` always returns `Unload` and it always reaches the stamp. Worst exactly where the gate is closed most — 92–96 % of frames at vd ≥ 10. | New `AbandonedBeforeAdmission` disposition; `StampUnloaded` picks between the two from `AdmittedTicks`, since the engine cannot see trace state. Excluded from the waste numerator **and denominator** — the fraction means "of the work the pipeline completed, how much was thrown away", and a request that never entered the pipeline is in neither term. Its count is still printed, plus an explicit exclusion line under the fraction (§7.2). |
| **b** | **`GenerationProcess` reported `OutOfWork` when its structure-mods quota stopped it.** (Its first fix over-corrected — see the quota note below §7.1 v2.) The pass has *two* exits, not one: the ms ceiling, and `maxStructureModsPerFrame`, which breaks the scan outright at one site and defers a job to the next frame at two others. `OutOfWork` maps to `Healthy`. | The `modsBudget` counter is declared once per pass and decremented across jobs, so it genuinely terminates the outer loop. Three docstrings asserted the opposite ("ceiling-only", "a quota stop is unreachable here", "Unreachable for the two ceiling-only passes") and had been wrong since FP-2. | Routed through the shared `ClassifyStop` with `genModsQuotaSpent` set at all three sites. `ClassifyStop` ranks Quota above Ceiling while this loop checks the ceiling first; the inversion is deliberate and documented — a spent quota left work behind whichever limit ended the scan, and that attribution cannot let an admission stall hide behind a hitch guard. |
| **c** | **The lighting scan counted flag-less entries as declined candidates.** The `Remove` arm fires when *no* lighting flag remains, i.e. an earlier schedule already cleared them — bookkeeping, not declined work. | `LightingScanDecision.EvaluateReadyChunk` returns `Remove` only under `!needsInitialLighting && !needsEdgeCheck && !hasLightChanges`. The code already excluded the *stale-entry* arm for exactly this reason, three lines above. After the ~1 s `PromoteAll`, the ready set is dominated by such entries → `AllDeclined` → `ReadinessBound` on a frame where nothing was declined. | `lightCandidatesSeen--` on that arm. The park arms, which *are* the readiness signal, still count. |
| **d** | **`LoadStranded` was wrong in 100 % of its firings and correct in none.** | `worldData.RemoveChunk` has exactly **one** call site — inside `UnloadChunks`, *after* the disposition stamp. So the post-await guard's "chunk gone" arms are only reachable once that coord's trace is already closed and removed, making the stamp a no-op there. The only arm that can still find a live trace is the pool-ABA recycle — where the trace belongs to the **successor** placeholder, which was then recorded as waste and lost its end-to-end latency sample. `LoadStranded = 0` in all 9 FP-4 phases is consistent. | Stamp deleted. The enum member is **retained and documented as retired** rather than renumbered, so disposition tables either side of the FP-7 boundary still line up column-for-column. |
| **e** | **§7.1.1's rule defect, still unfixed** (roadmap item 6). | Recorded in §7.1.1 since v1.7. | §7.1 **v2** — see below. |

**§7.1 v2 — the participation-weighted plurality.** Each reason is scored only over the passes able to emit
it: `share(reason) = Σ tallies over eligible passes ÷ Σ those passes' own reports`. Declared as a
`CanEmit(pass, reason)` matrix rather than hardcoded as "the scheduling passes", because **FP-7b changed the
premise §7.1.1 assumed** — `GenerationProcess` owns a real quota, so the split is not
scheduling-vs-completion, and only `MeshProcess` is genuinely ceiling-only.

> **The denominator is measured participation, not nominal opportunity — and the first draft got this
> wrong.** It divided by `frameCount × eligible pass count`, which charges a full phase of chances to a pass
> that never ran. `LightSchedule` sits inside `if (settings.enableLighting)`, so a lighting-off capture gave
> it a silent zero vote in every reason while it still occupied a denominator slot — capping `Quota` at 2⁄3
> against `OutOfWork`'s 3⁄4 and printing *Healthy* over a flat-out quota stall. **That is the §7.1.1 dilution
> rebuilt inside its own fix**, and it was caught by review, not by the B10 dilution scenario, which feeds
> all four passes non-zero tallies and therefore cannot see it. The regression scenario added for it uses
> proportions where the two formulas *disagree* (125⁄200 vs 125⁄300 against 175⁄300 vs 175⁄400) — a shape
> both accept would guard nothing.
>
> Participation is **derived from the tally matrix** rather than counted alongside it, so it cannot desync
> from the numerator it divides (the FP-5 lesson), and it is filtered by `CanEmit` for the same reason the
> numerator is. It assumes **one report per pass per frame**. That holds at every current call site — but
> only by enable-timing: `ForceCompleteDataJobsCoroutine` drives `ProcessGenerationJobs` in a tight
> `while` loop, and it merely happens that telemetry is still off during startup. A second `RecordPassStop`
> assert checks the invariant directly rather than trusting that ordering to survive.

Two consequences worth stating rather than discovering later:

1. **A hand-written capability claim is exactly what went stale in FP-7b**, so `RecordPassStop` now asserts
   the matrix against what production actually records, and logs loudly on divergence. Without that, v2
   inherits the defect it exists to fix. Both console asserts are **latched per (pass, reason)** — they run
   once per pass per frame, so an unlatched error would emit thousands of lines into the very log the capture
   is read from, burying the signal it exists to raise. `DomainReset` re-arms them so a repeat capture cannot
   run silently on a known-bad matrix.

   > **Those asserts are development-only, and a capture should be taken in a Release build** — the P-4
   > budgets are frame-time-proportional, so a Development Build's overhead lengthens frames, inflates quotas
   > and measures a different admission regime than a player ever sees. A guard that fires only in the build
   > nobody captures with is not a guard. Both conditions are therefore **also** checked at render time from
   > data the report already carries, and surface as banners in the artifact itself (`AppendIntegrityWarnings`,
   > baseline **B15**):
   >
   > - **Stale capability matrix** — a non-zero tally in a cell `CanEmit` forbids. Directly visible in the
   >   printed `[pass × reason]` matrix, so this needs no new state.
   > - **Double-recorded pass** — *not* reconstructible after the fact, so it is carried as a sticky
   >   `PassDoubleRecorded` flag set in **every** build (the `TracesSaturated` pattern). Note the symptom is
   >   **not** an out-of-range share: participation sums the same cells the numerator draws from, so shares
   >   stay ≤ 1 by construction. The offending pass simply votes with double weight, with nothing else in the
   >   report to show for it — which is why the flag exists at all.
2. **v2's shares are lower than §7.1.1's hand recomputation**, and deliberately so. That recomputation dropped
   the completion passes entirely and got "Quota at 99.5 %". v2 keeps `GenerationProcess` in Quota's
   denominator, so its `OutOfWork` frames count as genuine *abstentions* — a pass that could have reported a
   quota stop and didn't is evidence. Expect the more conservative number.

**Two further corrections from the same review, folded into v2 rather than versioned separately.** No capture
has ever run under v2 — FP-7 was still uncommitted — so there is no v2 report for a bump to protect, and a v3
whose only distinguishing feature was a bug that never reached a report would mislead rather than inform.

- **The structure-mods quota stop was over-reported.** FP-7b set `genModsQuotaSpent` at all three
  `modsBudget <= 0` sites, on the reasoning "quota spent ⇒ work left behind". That does not hold at the third
  site, which fires *after* a job is fully processed: if it was the last completed job in the scan, the break
  is equivalent to falling out of the loop and nothing was deferred — yet the frame voted `Quota` →
  `AdmissionBound`. The check now sits at the **top** of the loop body, where a completed job is about to be
  refused, mirroring the ceiling's placement. Which jobs get served is unchanged.
- **The ordering axis has a minimum-sample floor** (`MinOrderingTerminalTraces = 30`). Excluding
  never-admitted requests was right, but it shrank the denominator, and 1 waste of 3 terminal traces is 33 % —
  over threshold, off a sample of three. Below the floor the axis reports **undecidable**, rendered distinctly
  from "not ordering-bound": those are different claims, and only the second is a clean bill of health.
- **Exact share ties resolve to the bound regime, not to `Healthy`.** Ratios over differing denominators make
  exact ties reachable (200⁄400 and 150⁄300 are both 0.5), and the walk order reaches `OutOfWork` first, so a
  strict `>` sent every tie to the "everything is fine" arm. Ties between two *bound* reasons have no
  principled ordering and keep the deterministic walk order, visible as near-equal printed shares. (This bias
  is not a v2 regression — v1 had the identical walk and comparison; v2 merely makes ties more reachable.)

**What this costs the FP-4 report.** Its per-phase disposition tables, waste fractions, and primary verdicts
were all produced under superseded semantics. `RULE_VERSION` is bumped to v2 precisely so the two generations
can never be compared without noticing. The raw tallies §7.2 mandates remain valid and re-derivable — which is,
again, the argument for §7.2 as a standing requirement.

#### 7.4.3 FP-8's two instrument defects — found by the second capture, not by review (v1.12)

| Phase | Defect | Evidence | Fix (open) |
|-------|--------|----------|-----------|
| **FP-9a** ✅ **DONE** | **The min-sample floor guards the ordering axis but not the primary regime.** FP-7 added `MinOrderingTerminalTraces = 30` so a handful of traces cannot decide the ordering axis. Nothing equivalent gated the plurality, so a phase with almost no frames still asserted a regime. | FP-8 printed `ThroughputBound` for **vd 20 / Generation / 100 m/s** off **14 frames** (441 traces, all `InFlightAtPhaseEnd`, `InFlightCap` "winning" at 50.0 %); `AdmissionBound` for vd 15 / gen / 100 m/s off 148 frames; and `AdmissionBound` for vd 8's **Transition** — a drain-and-unload phase that has no meaningful regime at all. | **As built — two mechanisms, because one could not do it.** (1) `MinRegimeObservations = 1000` on `eligibleTotal`, with a new `PrimaryDecidable` flag mirroring `OrderingDecidable`. Measured in **eligible observations, not frames**: that is the unit `Evaluate` consumes, so the guard cannot drift from what it guards, and it needs no extra parameter. 1 000 clears the FP-8 evidence by an order of magnitude either way — it rejects 56 and 592 while the smallest legitimate phase carried ~13 600. (2) `PipelinePhaseMetrics.RegimeBearing` + an optional `BeginPhase(…, regimeBearing)`, set `false` for the transition — **a floor could never have caught that one**, since it carried ~1 332 observations, comfortably over any sane floor. The report renders the three no-regime outcomes distinguishably (`NO DATA` / `UNDECIDABLE (n of m)` / `NO REGIME`), per the FP-7 rule that "not bound" and "could not tell" must never read alike, and prints the observation count as a verdict input. A non-measurement phase is spared **both** axes, not just the primary — self-review caught that the ordering axis could otherwise label a drain-and-unload `ORDERING-BOUND` for discarding work on purpose, the same category error one axis over; FP-8's transitions hid it only by having zero traces. Guarded by **B16**. |
| **FP-9b** ✅ **DONE** | **Neither pass was comparable across a view-distance sweep.** `BuildWaypoints` derived margin *and* row stride from `LoadDistance` inside a region clamped to a legacy world size, so a larger view distance bought fewer waypoints — and the route was shorter than the speed phases needed at **every** view distance. | Waypoints **12 / 8 / 6 / 4 / 4** at vd 5/8/10/15/20. Worse than first recorded: at vd ≥ 10 the **200 m/s generation phase never ran at all** (`Total phases: 8`), and even the default truncated — route 9 344 m against the 11 400 m the phases travel. The **loading** tour was confounded too, shrinking 84 → 54 chunks because its extent came from `LoadDistance`. | **As built — the relationship is inverted: the route is now the input and the region the output.** `Benchmarks/BenchmarkRouteGeometry.cs` derives region, rows, width and tour from the *configured* speeds × phase duration, so a user adding 300/500 m/s grows it automatically (123 → 366 chunks). The `WorldSizeInChunks` clamp is gone — vestigial since the world became unbounded, and its only live use in the codebase. The generation pass is now **time-bounded** (every phase runs its full duration at every vd), which it could only become once a new **ensure-generated** sweep took over coverage: it flies the loading tour once at 50 m/s with `RegimeBearing = false` (FP-9a's mechanism), at a cost that stays *constant* (~211 s) as speeds are added, where a full-region sweep would grow to ~2 684 s. Loading tour fixed at **64 chunks** — the largest that fits inside the timed coverage at every vd in every tested configuration. `benchmarkRegionSize` retired for `benchmarkGenerationWaypoints` (honoured **exactly**; extra distance widens rows rather than adding them) + `benchmarkPhaseSeconds`. Guarded by **B17**. <br>**A review pass over FP-9b itself found the tour was placed against the wrong band** — sized against the *timed-covered* rows but centred on the *full* sweep, so it drifted toward the un-walked end by `(rows − completedRows) × stride ÷ 2`. At the default 12 waypoints the `LoadDistance` margin absorbed it (with **zero** margin to spare at vd 5); at 24 it put the loading tour wholly outside the generated area while `TourWasShrunk` still read `false`. Fixed by deriving size and position from one `CompletedRows` value. B17's coverage assertion was itself the reason this shipped: it re-ran the sizing helper with the constructor's own arguments, making it a tautology of the shrink check, and it exercised only the default waypoint count. It now asserts **containment of the final coordinates** and runs across 12/24/64 waypoints. |

### Extension roadmap (post-FP-4, in intended order)

| Version | Extension                                                                                                                             |
|---------|-----------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Live in-game HUD panel for pipeline state (queue depths, gate, stop reasons) — the same data, sampled continuously outside a benchmark run. |
| **v2**  | A **manual flight** capture mode: telemetry recording during ordinary play rather than scripted waypoints, so a user-reported "felt sluggish here" can be captured in situ. |
| **v3+** | Per-chunk trace export (CSV) for offline analysis of wave-front shape — gets its own design doc if the aggregate percentiles prove insufficient. |

---

## 8. Open questions — both closed (v1.1)

1. **Ring-buffer and side-table sizing at 200 m/s.** ✅ **CLOSED — sized and measured in FP-0 (v1.3).**
   `PipelineTelemetry.EstimateTraceCapacity(loadDistance, speed, phaseSeconds)` derives it from the same
   region geometry the rig flies: the resident load square `(2·LD+1)²` plus one square-width swath per chunk
   of travel, times 1.5 headroom for the §4.1 revisits, clamped to [4096, 65536]. Run against the actual
   speed sweep at `LoadDistance = 12`, 30 s phases:

   | Speed     | Estimated traces |
   |-----------|------------------|
   | 10–20 m/s | 4,096 (floor)    |
   | 50 m/s    | 4,462            |
   | 100 m/s   | 7,987            |
   | **200 m/s** | **15,000**     |

   The worst realistic case sits **~4× below the 65,536 ceiling**, so the clamp does not bind at sane
   settings and saturation should be the exception rather than the norm. Floor and ceiling were both
   verified at the extremes (`LD=0 @ 0 m/s → 4,096`; `LD=64 @ 500 m/s → 65,536`).

   > **v1.7 — FP-4 exercised this and the model held.** The 200 m/s loading phase started **21,848 traces**.
   > The capture ran at **`LoadDistance = 23`** (viewDistance 20 + `DATA_LOAD_BUFFER` 3), *not* the 12 the
   > table above tabulates, so the applicable budget is `EstimateTraceCapacity(23, 200, 30)` = **29,751** —
   > ~27 % headroom, and no saturation banner fired. **The sizing model is not implicated.**
   >
   > One process lesson worth more than the number: FP-4's first draft read the wrong row of this very table
   > and reported a phantom "46 % under-prediction", because **`LoadDistance` is not printed in the capture
   > report** and had to be supplied by the operator out of band. A capture that omits its own sizing input
   > invites exactly that error. Printing it is nearly free — `_loadDistanceForCapture` is already held at
   > `BenchmarkController.cs:195`.

   The failure mode is unchanged and remains the point: saturation is **never** silent truncation. The trace
   table and latency series carry sticky `TracesSaturated` / `SamplesSaturated` flags — reported separately
   from the merely-rolling `FrameWindowWrapped` — and FP-3 prints an explicit **"⚠ TRACE BUFFER SATURATED —
   percentiles below cover only the first N chunks of this phase"** banner. A saturated capture is still
   *readable*; it is simply not allowed to look complete. This is an FP-3 acceptance test, not a nice-to-have.
2. **Whether the loading pass needs a different disposition set.** ✅ **Answered: yes.** The load path has its
   own stranding site, and it is uncounted. `LoadOrGenerateChunkInner` re-checks after its `await
   StorageManager.LoadChunkAsync` whether the chunk was unloaded or pool-ABA-recycled mid-flight; if so it
   returns the freshly loaded `ChunkData` to the pool and bails (`World.cs:1032–1042`). That is exactly the
   "stranded disk load" v1.0 hypothesized — a completed disk read thrown away because the player moved on —
   and it is the loading pass's structural counterpart to generation's §3.2 discard. **FP-1 adds a
   `LoadStranded` disposition and the hook at that site** (already listed in §5). Note the asymmetry worth
   reporting on: the generation discard is a deliberate, flagged optimization, whereas this one is a
   correctness guard whose waste has simply never been measured.

   > **v1.11 — this answer is RETRACTED (FP-7d, §7.4.2).** The site is real, but it is **not reachable with a
   > live trace**, so no disposition can be stamped there. `worldData.RemoveChunk` has exactly one call site,
   > inside `UnloadChunks`, which closes and removes the coord's trace *before* the awaiting load can observe
   > the removal — so the guard's "chunk gone" arms always find nothing, and its only live-trace arm is the
   > pool-ABA recycle, where the trace belongs to the **successor** placeholder. The stamp was therefore
   > wrong in every firing. The waste itself is not unmeasured: the predecessor's
   > `UnloadedBeforeMeshApplied` already accounts for it. The reasoning error was inferring reachability from
   > the *existence* of a guard clause without tracing who can reach it — the same shape as §7.1.1's
   > capability inference.

---

## 9. Assumptions and limitations (v1.2)

Recorded here because a future reader inherits these whether or not they are written down; the only choice is
whether they inherit them *knowingly*.

### Assumptions — each with where it gets tested

1. **`ChunkCoord` hashing is adequate** for a side table holding ~10³ live entries per phase. Unverified
   directly, but it is already the key type for `GenerationJobs` / `MeshJobs` / `_pendingGenerationRequests`
   at comparable cardinality. Tested implicitly by FP-4's own capture overhead.
2. **Ring-buffer capacity from region math covers a 200 m/s phase.** Deliberately **unfalsifiable in
   advance** — which is precisely why §8 Q1's saturation flag is an acceptance test rather than a nicety.
   First real test is FP-4; a saturated first capture is a valid outcome, not a failed one.
3. **The `await` continuation in `LoadOrGenerateChunkInner` resumes on the main thread** (Unity's
   synchronization context). If false, that single hook needs synchronization and every other hook is
   unaffected. Verified by construction review at FP-1; a violation would surface as a torn trace.

### Limitations — what this instrument does NOT establish

1. **The enabled-run guard is partial**, per §7 item 1. Hooks resident in `World.Update` are not
   suite-reachable.

   > **v1.11 — FP-7 is the demand case for this limitation, and it is not cheap.** Four of FP-7's five
   > defects (a–d) live in play-mode call sites, so **B13/B14 pin only the classification logic, never the
   > wiring** — reverting `StampUnloaded` to `StampDisposition`, dropping `genModsQuotaSpent`, deleting the
   > `lightCandidatesSeen--`, or restoring the `LoadStranded` stamp would each leave the whole suite green.
   > Every one of those defects shipped past review once already. This is the second time the same
   > limitation has cost real correctness (B11's call site was the first); a play-mode harness is the
   > structural answer, and it remains out of scope here.
   >
   > **Partially mitigated for the verdict's integrity conditions:** the two that would silently corrupt a
   > §7.1 v2 verdict — a stale capability matrix and a double-recorded pass — are re-derived at *render*
   > time and surface as banners in the report, so they hold in a Release capture where the console asserts
   > are compiled out. **B15** pins both banners and drives the double-record flag through the real statics,
   > which makes it the first FP baseline to guard a hook's effect end-to-end rather than only its policy.

2. **Cross-generation report comparison is unsafe, and only `RULE_VERSION` prevents it.** FP-7 changed both
   what counts as waste and how the plurality is weighted, so a v1 report (every capture through FP-4) and a
   v2 report describe different quantities under identical headings. The raw §7.2 tallies remain valid and
   re-derivable in both; the *derived* figures do not carry across.
3. **Disabled-path allocation-freedom is not assertable on editor Mono** — inspection plus an IL2CPP GC-alloc
   read is the whole of the evidence.
4. **Unlike the MP-1 counters, this layer is not `[Conditional]`-compiled out of release.** It cannot be: FP-4
   must *toggle* it inside a Development Build. The residual release cost is one static bool read per stage
   transition — the `WorldFrameProfiler` bargain, accepted for the same reason.
5. **Exact percentile recomputation from a written report is not supported** — see §7.2's stated limitation
   and the v3+ CSV item.
6. **FP fixes nothing.** The §4.4-vs-§4.1/§2-vs-ordering-vs-readiness choice happens *after* FP-4's verdict.
   Any change to engine behavior motivated by this capture is a separate item with its own design.

---

## Document History

* **v1.15** - **FP-10 captured: the first sweep on a derived route, and the first reproduction** (2026-08-01).
  Six view distances (5/8/10/15/20/32), one IL2CPP **Release** build, 60 phases. Two things make it worth more
  than another data point. First, **FP-9b's route rework let it falsify itself and didn't**: waypoints and
  timed travel are now constant across the sweep, so the generation pass became comparable for the first time,
  and the loading-pass waste curve nonetheless landed within ~1 pt of FP-8 at four of five overlapping points.
  A conclusion that survives having its route rebuilt underneath it is a property of the pipeline, not of the
  benchmark — which is what FP-8 had to assume. Second, it **replaced an inference with arithmetic**: the
  panic gate's close/reopen pair is an absolute 256/128 backlogged chunks measured against a resident square
  growing as vd², so the trip threshold falls from 88.6 % of residency at vd 5 to **5.1 % at vd 32**. The
  consequence is measurable and clean — across vd 5 → 32 **requests grow 4.47× (loading) / 4.76×
  (generation) while admitted work grows only 1.51× / 1.73×**, with completion-of-admitted showing no trend at
  all (53–68 %). The pipeline's efficiency on what it accepts does not vary with view distance; only its
  willingness to accept does. P-8 stays at #1 with a constraint now attached: the gate is *succeeding* at
  protecting frame time (at vd ≥ 20, flying faster costs less CPU because the faster phase trips the gate), so
  loosening it must be gated on frame time rather than on admission counts alone. P-7's worst case relocates
  from vd 5 to **vd 8 / 200 m/s (50.8 %)** — the point where the gate has started closing but has not yet
  suppressed admissions. Also verified the standing assumption that the derived route would fly into negative
  chunk coordinates for the first time: it did, and nothing broke. One new defect (FP-11a — the
  ensure-generated sweep is itself gate-throttled at 92.3 % of frames at vd 32, so its coverage is
  unverified), two minor items, and production confirmation of FP-7's integrity banner and FP-9a's
  `RegimeBearing` (§7.4.4). Report:
  [`../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP10_FLIGHT_PROFILE_IL2CPP_2026-08-01_BENCHMARK.md).
* **v1.11** - **FP-7: the instrument's own measurements audited and corrected** (2026-07-31). Five defects,
  found by reviewing the FP-1…FP-6 commits rather than by running a capture — which matters, because a
  capture only surfaces what looks wrong, and all five looked right. Four concerned what the numbers *mean*:
  requests the panic gate never admitted were counted as **waste** (the sole input to the ordering axis, and
  worst exactly where the gate is closed 92–96 % of frames); `GenerationProcess`'s structure-mods quota stop
  was reported as `OutOfWork`, which maps to *Healthy*; the lighting scan counted flag-less entries as
  declined candidates, so the ~1 s `PromoteAll` could manufacture a `ReadinessBound` verdict; and
  `LoadStranded` was **wrong in 100 % of its firings** — `RemoveChunk` has one call site, which closes the
  trace first, leaving only the pool-ABA arm, where the trace belongs to the successor. The fifth closes
  §7.1.1 as **§7.1 v2**: a per-(pass, reason) capability matrix, generalised from that section's own
  proposal because FP-7b falsified its premise (`GenerationProcess` was never ceiling-only). A
  `RecordPassStop` assert now checks the matrix against production, since a stale capability claim is
  precisely what FP-7b was. Waste classification moved onto `PipelineRegimeVerdict` so the verdict and the
  table beneath it cannot diverge (the FP-5 lesson). Guarded by **B13**/**B14** with B10 rewritten;
  prove-red confirmed by three temporary mutations, each reddening exactly one baseline with the other
  thirteen untouched. Validate All **362/362** across 16 suites, telemetry disabled *and* enabled, the
  enabled leg reporting `phasesLeftBehind = 0` and no staleness assert. **Cost, stated plainly: the FP-4
  report is no longer comparable to future captures on either axis, and P-7's waste magnitudes are
  superseded pending a re-capture** — its ranking is not, since the default view distance exceeds threshold
  with the gate never closing.
  <br>**A second review pass over FP-7 itself found three more**, all folded into v2 before it shipped (no
  v2 report existed, so no version bump was warranted). The serious one: v2's first denominator divided by
  *nominal* opportunity, `frameCount × eligible passes`, which charges a full phase of chances to a pass that
  never ran — with lighting disabled, `LightSchedule` held a denominator slot while casting no vote, capping
  `Quota` at 2⁄3 against `OutOfWork`'s 3⁄4 and printing *Healthy* over a flat-out quota stall. **The dilution
  defect had been rebuilt inside its own fix**, and B10's dilution scenario could not see it because that
  scenario gives all four passes non-zero tallies. The denominator is now each eligible pass's *measured*
  participation, derived from the tally matrix so it cannot desync, with a new assert on the
  one-report-per-pass-per-frame invariant it rests on. Also: the quota stop was over-reported at a break that
  left no work behind, and the ordering axis gained a 30-trace floor plus a tie-break away from `Healthy`.
  Lesson worth keeping — **a guard scenario that both the correct and the incorrect implementation accept
  guards nothing**; the new regression case is built from proportions where the two formulas disagree.
  <br>**Then a third pass moved the verdict's integrity guards out of the console and into the report**
  (**B15**): the `RecordPassStop` asserts are development-only, but a capture belongs in a *Release* build
  (frame-time-proportional budgets mean a Development Build measures a different admission regime), so the
  guards were absent exactly where captures happen. A stale capability matrix is now flagged from the printed
  tally matrix, and a double-recorded pass from a sticky `PassDoubleRecorded` flag set in every build — the
  `TracesSaturated` pattern. This also retracted a claim in FP-7's own comments: a double record does **not**
  push shares above 1 (participation sums the same cells the numerator draws from, so shares are ≤ 1 by
  construction) — it inflates one pass's voting weight with no other visible symptom, which is why a flag was
  needed rather than a range check. The report also now prints `Configuration: Development | Release`, without
  which two captures differing only in build type are indistinguishable from their text.
* **v1.10** - **FP-6 done, and deliberately wider than it was filed as** (2026-07-28). It was scoped as "print
  `LoadDistance`"; the operator pointed out that the per-frame caps and time budgets shape the pipeline just as
  much, and that is right for a reason the original framing missed: **every one of those knobs *produces* one
  of the stop reasons the §7.1 verdict is computed from.** A phase reporting `Quota` on 99 % of frames is
  uninterpretable without the quota. **A first-draft justification for this was wrong and is retracted in
  §7.4.1**: it cited quota values read from the *editor's* settings, but settings persist per-platform
  (`Application.dataPath/settings.json`), so an editor reading says nothing about a player build. The
  corrected reasoning is stronger and rests on three verified mechanisms — **OM-1 device calibration
  overwrites four of these settings at startup** from system RAM plus a timing probe (so they are
  device-dependent and absent from any checked-in default), settings files are per-platform, and benchmark
  mode's deterministic-defaults branch is **unreachable via the normal main-menu route** because the settings
  cache is only cleared at process start. Net: a capture's tuning is *not derivable from code inspection*,
  which is exactly why it must be printed. As built: `Benchmarks/PipelineSettingsSnapshot.cs` captures 18 values
  at run start — **not** read at report time, since settings are editable mid-session and the report must
  state what the run *used*, the same reasoning that already governed the trace-capacity input — and renders
  them **grouped by the stop reason each produces** (quotas → `Quota`, in-flight caps → `InFlightCap`, ms
  ceilings → `Ceiling`, and the panic gate under an explicit "no stop reason" heading, because it withholds
  admissions at `DrainGenerationRequests`, outside all four instrumented passes, and is visible only as the
  per-phase gate-closed %). Two derived values are printed because the FP-4 analysis reasons from them rather
  than from the raw numbers: the resident square, and the gate threshold **as a percentage of it** — the F5
  predictor. Rendering lives **on the snapshot** rather than in `BenchmarkReportGenerator` (the FP-5 lesson:
  two places that must agree eventually don't), which also made it directly verifiable — the block was
  rendered from live settings and its derived values cross-checked against an independent recomputation.
  Guarded by **B12**, built from explicit `Settings` instances rather than `LoadSettings()` so a baseline
  never depends on the user's current configuration; it pins the geometry at all three swept view distances,
  the monotonicity F5 rests on, and a clamped degenerate case — which surfaced a real gap while being written,
  since `ResidentWidth` did not originally floor at 1 the way `EstimateTraceCapacity` does. **Prove-red
  confirmed:** corrupting the width formula turns exactly B12 red (11 passed / 1 failed) on all six geometry
  assertions, B1–B11 untouched. Gated at `dotnet build` 0 errors on both assemblies with no new warnings,
  Rider clean on all four touched files, Validate All **360/360** across 16 suites with telemetry disabled and
  enabled. **Known limitation:** the three FP-4 captures predate this, so their tuning is unrecorded and
  cannot be recovered — the report's own numbers are unaffected, but a comparison against a *future* capture
  must not assume matched settings. **New §7.4.1 records a retraction and the corrected reasoning**: the
  claim that the sweep ran at non-default quotas came from reading the *editor's* settings file, and settings
  are per-platform, so it said nothing about the player build that produced the captures. Investigating it
  surfaced three mechanisms that make the case for FP-6 much stronger than the retracted one — OM-1
  calibration writes four of these settings at startup from device RAM and a timing probe; settings files are
  per-platform; and benchmark mode's deterministic-defaults branch is **unreachable on the normal main-menu
  route**, because `s_cachedSettings` is cleared only at process start, so a benchmark runs on the player's
  live settings. The last is a latent harness gap in its own right (benchmark determinism is intended but not
  achieved) and is left as an explicit open question rather than changed here, since fixing it alters
  benchmark behaviour.
* **v1.9** - **FP-5 fixed and guarded** (2026-07-28). The run-boundary phase leak the sweep exposed is closed:
  public `PipelineTelemetry.BeginRun()`, called from `BenchmarkController` immediately before `Enabled = true`,
  so both recorders start a run empty. **The shape is dictated by UDR0002 and is deliberately not the obvious
  one:** a shared private helper called by both `DomainReset` and `BeginRun` is the natural refactor and
  *fails* — the analyzer requires every mutable static to be assigned **lexically inside** the
  `[RuntimeInitializeOnLoadMethod]`, and delegating outward raised UDR0002 on `s_activePhase` on the first
  attempt. So `BeginRun` calls `DomainReset`, not the reverse, with a comment at the site to stop the natural
  tidy-up reintroducing it. Side effect recorded because it bit the baseline: `BeginRun` also clears
  `Enabled`, so a caller restoring a saved flag must restore it *after* the call. Guarded by **B11** in
  `Validate Pipeline Backpressure` (registry 16, Validate All 358 → **359**), whose scope is stated in its own
  docstring rather than overclaimed: it pins `BeginRun`'s *semantics* — clearing completed phases, dropping a
  phase left open by an aborted run, and working while the layer is still disabled — but **not** the call
  site, which lives in a play-mode coroutine over a live `World` and is unreachable from edit mode (§7 item
  1); deleting the call would leave B11 green. **Prove-red confirmed by temporary mutation:** commenting out
  `s_completedPhases.Clear()` turns exactly B11 red (10 passed / 1 failed) on exactly its three leak
  assertions, with B1–B10 untouched. Gated at `dotnet build` 0 errors on **both** assemblies with **no new
  warnings** (the first attempt's UDR0002 was fixed, not suppressed), Rider inspections clean on all three
  touched files, and **Validate All 359/359 across 16 suites with telemetry both disabled and enabled** — the
  enabled leg additionally reporting `phasesLeftBehind = 0`, i.e. the whole registry runs without any suite
  leaving a phase recorded.
* **v1.8** - **FP-4 extended to a three-point view-distance sweep** (2026-07-28), vd 5 / 10 / 20 on one build.
  The single-leg verdict is **refined, and one v1.7 claim is retracted**. (1) **Ordering-boundness is
  universal**: waste exceeds the 20 % threshold in **all 9** loading phases across all three runs
  (22.9–61.2 %), including the **default** viewDistance 5 where the panic gate **never closes once** in
  ~380,000 sampled frames. Ordering is therefore intrinsic pipeline behavior, not a gate side-effect and not a
  stress-configuration artifact. (2) **Admission-boundness is conditional** on viewDistance ≥ 10 — **F5 is now
  CONFIRMED by measurement rather than arithmetic**: gate closure at loading 200 m/s is **0.0 % / 92.8 % /
  96.4 %** at vd 5 / 10 / 20, against a fixed 256-backlog threshold that represents 88.6 % / 35.1 % / 11.6 % of
  the resident square. The transition is a sharp tipping point, exactly as a fixed threshold against a
  quantity scaling with view-distance² predicts. (3) **RETRACTED — "waste rises with view distance"** was
  inferred from the single leg and is false at moderate speed: at 50 and 100 m/s the *default* view distance
  is the **worst** of the three (33.1 % / 22.9 % / 27.2 %); only at 200 m/s does it rise with vd. The
  unifying relation is `waste ≈ latency ÷ residence-time` where residence ≈ ring diameter ÷ speed.
  (4) **v1.7's F2 is upgraded to a structural result: the generation pipeline is LOCKSTEP.** Expressed in
  boundary-crossing intervals, `populated→lit` costs ~**2** crossings and `lit→meshApplied` ~**1**, totalling
  **3.01–3.40 across 12 measurements**, three view distances and five speeds — and the total is *invariant to
  view distance* (3.01 / 3.02 / 3.05 at 10 m/s) despite a 7.6× change in resident-set size. Integer hop counts
  are not something a throughput limit produces; the generation pass has a **latency floor no extra throughput
  can lower**. (5) **New: the visibility criterion.** A chunk must render before the player covers the
  view-distance margin, i.e. `latency ≤ viewDistance × 16 ÷ speed`. Measured as a ratio it is
  **0.87 / 1.23 / 1.56** at loading 200 m/s — crossing 1.0 exactly between vd 5 and vd 10, which is precisely
  where the operator's independent visual report changed from "all chunks loaded fine" to "chunks lagging
  behind", and at vd 20 chunks arrive **11 chunks beyond** the view boundary ("only a handful even rendered").
  The criterion separates reported-good from reported-bad in all 21 cells and gives ordering work a target
  number instead of "less waste". (6) **New bug found in the data, confirmed in code:**
  `PipelineTelemetry.s_completedPhases` is cleared only in `DomainReset`, never at benchmark-run start, while
  `BenchmarkMetricsCollector` resets itself — so a **second run in one process reports the previous run's
  phases as its own** (the vd 10 log carries all 9 vd 5 phases verbatim before its own 8). One-line fix, own
  commit. Also confirmed: the generation waypoint counts differ across runs (12 / 6 / 4), so cross-run
  *generation* comparisons are confounded and only the loading pass (12 everywhere) is directly comparable —
  F2 survives this because it reproduces on three different routes.
* **v1.7** - **FP-4 captured — the arc is complete** (2026-07-28). Report:
  [`../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md)
  (IL2CPP Development Build, 8 phases, commit `73de6511`). **Verdict: ordering-bound + admission-bound** in
  the loading pass ≥ 50 m/s — waste rises monotonically with speed (27.2 → 29.8 → **61.2 %**) while the panic
  gate sits closed on 69.8 → 86.2 → **96.4 %** of frames; **throughput-bound and readiness-bound are both
  ruled out** (`InFlightCap` ≤ 0.6 % and `AllDeclined` ≤ 0.9 % at 200 m/s). At 200 m/s the pipeline starts
  728 chunks/s and delivers 219 — **~3.3 units of work per chunk shipped** — which is why ordering outranks
  throughput as the next item. Three things the capture established that the design did not anticipate:
  **(1) §7.1 v1 is defective (new §7.1.1)** — summing stop reasons across all four passes lets two
  *ceiling-only* completion passes, structurally able to vote only `OutOfWork` (empirically 48/48 zero cells),
  dilute the plurality; loading 200 m/s came down to 68 frames of 27,744 and printed *Healthy*, where the same
  raw numbers restricted to the budgeted passes give **Quota at 99.5 %**. The rule aggregated good data wrongly
  and §7.2 is what made the correction possible without a re-capture — deliberately left unfixed here, since
  patching a pre-committed rule after seeing its result would defeat its purpose. **(2) Generation-pass latency
  is paced by distance, not throughput** — `p50 × speed` is a near-constant **49–55 m (~3 chunk boundaries)**
  across a 10× speed range, and latency *falls* as speed rises; the loading pass's same product is not constant
  (100/209/498 m), which rules out an arithmetic artifact. Nobody should optimize generation throughput against
  the 4.9 s figure at 10 m/s until that pacing hypothesis is confirmed. **(3) The felt symptom is likely the
  tail, not the median** — ~1 % of chunks at 20 m/s take 24–30 s against a 2.5 s cohort, and the two hop p99s
  sum past the phase maximum, proving two distinct stall populations; this is the recorded demand case that
  promotes the v3+ per-chunk CSV export (§7.2). **(4) The panic gate's threshold does not scale with view
  distance** — it is a fixed 256/128 band read from `SettingsManager`, while the resident square it guards
  grows as view-distance²: a 256 backlog is **88.6 %** of the resident set at the default viewDistance 5 but
  only **11.6 %** at the viewDistance 20 this capture ran, which is the mechanism behind the 96.4 % closure
  rate. A hysteresis band pinned to an absolute count cannot mean the same thing across a setting that moves
  the guarded population by 7.6×; that is a design gap needing its own pass, and it is now second in the
  report's ranked follow-ups.  
  **Capture caveats:** the run used **viewDistance 20 (4× default)**, so the *severity* figures are
  stress-configuration magnitudes while the *regime* conclusion is the robust part — the report carries a
  Generality table separating the two, and a viewDistance-5 leg is the cheapest confirmation available. The
  generation sweep is **incomplete** (100 m/s ran 0.7 s of 30 s, 200 m/s never ran — the 4-waypoint route is
  exhausted at speed). §8 Q1's capacity model was re-checked at the true `LoadDistance = 23` and **held**
  (29,751 budget vs 21,848 used); a first-draft claim that it under-predicted by 46 % was **retracted** — it
  had read the LD=12 row, an error caused by `LoadDistance` being absent from the report.
* **v1.6** - FP-3 as-built sync (2026-07-27). **FP-3 shipped and gated** — `TraceStatistics` (nearest-rank
  percentiles + a totality-guaranteed histogram), `PipelineRegimeVerdict` (the §7.1 rule as pure arithmetic),
  and `PipelineReportSection` (the §7.2 raw-results block with the verdict's input vector printed verbatim
  above the verdict line). **Validate All 358/358** (356 + B9 + B10), both baselines prove-red confirmed by
  disjoint mutations that turned exactly their own scenario red. Two decisions recorded rather than made
  silently: **(1) §7.1's ordering criterion was narrowed** — its second clause ("the gap concentrated in
  chunks already behind the player") is *not computable* from what the capture records, since no player
  position is ever sampled; the criterion is now the waste fraction alone, which is well-founded rather than
  a proxy because every disposition it counts literally means the chunk left range while work was in flight.
  The 20 % threshold is a named, pre-committed constant so a later session can disagree and recompute from
  the §7.2 counts. **(2) B9/B10 live in `Validate Pipeline Backpressure`, not the ChunkMath suite v1.0
  proposed** — ChunkMath is scoped to coordinate/addressing math, while Pipeline Backpressure already owns
  the pipeline's pure math including FP-2's `ClassifyStop`. Verified end-to-end by rendering the section from
  synthetic phase data: percentiles, histogram totality (buckets summing to n), waste arithmetic, the
  saturation banner, and a `Healthy + ORDERING-BOUND` verdict — the exact case the separate ordering axis
  exists to express.
* **v1.5** - FP-2 as-built sync (2026-07-27). **FP-2 shipped and gated** — per-pass stop reasons returned by
  the pure policies, per-frame admission sampling, **Validate All 356/356** (355 + the new B8) with telemetry
  disabled and enabled, Rider clean on every touched production file, and B8 prove-red confirmed by temporary
  mutation. New §5.2.1 records three decisions the plan did not anticipate: **(1)** `PipelinePass` /
  `PassStopReason` moved from `Benchmarks` to `Helpers` — otherwise `MeshDrainPolicy.Drain` would have to
  *return* a benchmark type, making a core scheduling policy depend on the diagnostic layer; the stop reason
  is a property of the pass, not of the instrument reporting it. **(2)** One shared
  `PipelinePassBudget.ClassifyStop` for both scheduling loops, so they cannot drift on what a stop means and a
  single baseline pins both. **(3)** `JobCompletionPass.RunMergeLoop` now returns whether the ceiling broke
  the loop — the same return-don't-re-derive principle as §5.2, since re-reading `window.Expired` afterwards
  would report a ceiling stop for a pass that finished everything and only then ran out of window
  (source-compatible; existing callers ignore it). Also records the candidate-counting subtlety: stale
  ready-set entries are excluded, because counting them would let a mass unload masquerade as a readiness
  stall, while a parked placeholder *is* counted as genuinely-ineligible work.
* **v1.4** - FP-1 as-built sync (2026-07-27). **FP-1 shipped and gated** — six hook sites / eight call sites,
  `dotnet build` 0 errors, Unity clean, **Validate All 355/355 both with telemetry disabled AND enabled with a
  live phase**, plus a synthetic lifecycle run pinning the recording logic. Two corrections, both of the same
  class — a measurement bias that would have skewed the verdict in the regime under investigation:
  **(1) `UnloadedBeforeMeshApplied` restored** (v1.3 replaced rather than extended the disposition list, so a
  chunk populated-then-outrun had no hook and surfaced as `InFlightAtPhaseEnd`, which this document defines as
  *not* waste — the most characteristic ordering-bound event would have read as benign); **(2) `StampRequested`
  made idempotent before admission** — `CheckViewDistance` clears and rebuilds the whole request queue on every
  boundary crossing, so a naive stamp restarted the trace each crossing and measured latency from the *last*
  crossing rather than the first request. That error grows with crossing rate, i.e. with speed, so it would
  have under-reported latency exactly where the capture must be trusted. Admission is now the discriminator
  between a re-enqueue (idempotent) and a genuinely dead journey (§4.1 flush). Also records what the enabled
  suite leg does and does not prove: the hooks execute without perturbing behavior (B31 drives the real
  `MeshCompletionDriver`), but nothing records, because no suite drives a full request→apply lifecycle.
* **v1.3** - FP-0 as-built sync (2026-07-27). **Status → Partially implemented**; FP-0 shipped and gated
  (`dotnet build` 0 errors with the file genuinely in the csproj, Unity console clean, Rider inspections
  clean, **Validate All 355/355 across 16 suites**, and a live smoke check confirming the disabled path is
  inert). Three code/doc drifts closed, each a case where building it revealed the design had under-specified
  something: **§5 dispositions 4 → 6** (`Rerequested` names the §4.1 flush and *is* the re-request metric;
  `InFlightAtPhaseEnd` keeps chunks the phase merely outran from being booked as waste, which would inflate
  an input to §7.1); **§5.1 gains `NotRun = 0`**, a non-outcome occupying the zero slot so a default-
  initialized sample cannot report the flattering "ran, nothing left" for a pass that never executed —
  with the binding FP-2 consequence that skipped-pass early-outs must record their real reason;
  **§8 Q1 CLOSED** with measured capacities (15,000 at the 200 m/s worst case, ~4× under the ceiling).
  Also records the as-built buffering split: exact unbounded tallies vs a rolling frame window, which is
  what makes §7.2's "full tallies, never truncated" promise implementable.
* **v1.2** - Implementation-planning amendment (2026-07-27) — folds the FP-0…FP-4 planning session's
  decisions into the design so a cold session inherits them from the doc rather than from a transcript.  
  **New §4.1:** `ChunkCoord` is not unique within a phase (the loading pass revisits by design), so the
  side table would have silently overwritten traces — resolved by **flush-and-restart**, which doubles as
  §1 goal 3's re-request metric; plus the two hook-safety constraints it implies (indexer never `Add`;
  non-throwing inside HF-2's `try`). **New §5.2:** the stop reason is returned by the **pure policies**
  (`MeshDrainPolicy.Drain` → `DrainResult`), with the rejected alternatives and the honest 10-call-site
  cost — this is what makes FP-2 suite-guarded. **New §7.1:** the verdict rule is pre-committed
  (dominant/plurality stop reason, ordering read off a separate axis) so FP-4 cannot fit a rule to a
  result. **New §7.2:** *raw results are mandatory* — full tallies, distributions + histogram, absolute
  counts, and the verdict's verbatim input vector, so a later session can disagree with §7.1 from the same
  report; exact per-chunk re-derivation stays the v3+ CSV item. **New §9:** assumptions (each with its
  verification step) and limitations. **§7 correction:** v1.1's "`Validate All` enabled proves the hooks do
  not perturb pipeline behavior" was overstated — the suites run in edit mode with no `World`, so it is a
  **partial** guard covering only suite-reachable hooks. Also fixes a v1.1 leftover: goal 2 still ended the
  stage chain at `visible`.
* **v1.1** - Re-verification amendment (2026-07-27), at the **same** commit `6c7609c0` — every correction
  below is an error in v1.0's audit, not code drift. **§2:** the panic-gate row was simply wrong (the state
  is exposed through six public `World` probes and both transitions log unconditionally — this *shrinks* FP-2
  to sampling); the queue-ordering row contradicted its own body (the generation queue **is** nearest-first;
  the real defect is per-crossing staleness) and missed that `LightWorkScheduler`'s ready set is a `HashSet`
  iterated in hash order; the waste-counter row undercounted five existing counters and missed a second
  uncounted discard site; the flight-rig row gained the unequal-phase-duration caveat. **§5:** `MeshApplied`
  and `Visible` were the same instant post-MP-6 — `Visible` is dropped, the chain is four stamps, and the new
  §5.1 widens the stop-reason set from three values to five (`InFlightCap`, `AllDeclined`). **§1/§7:** a
  fourth **readiness-bound** regime, made observable by `AllDeclined` — without it a stalled pipeline scores
  as a healthy one. **§8:** both questions closed (Q2 = yes, `World.cs:1032–1042`). §1 goal 5's citation
  corrected from `Performance/README.md` to the `perf-benchmark` skill. No decision in §3 or §4 changed.
* **v1.0** - Initial design

---

**Last Updated:** 2026-08-01 (v1.15 FP-10 capture sync)  
**Next Review:** when the post-FP-7 re-capture (FP-8) is run, or when P-7's design doc starts
