# Flight-Profile Capture (Pipeline Telemetry) Design

**Version:** 1.8  
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
**Status:** ✅ **Implemented.** FP-0…FP-4 are all shipped; the capture is
[`../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md`](../Performance/CHUNK_PIPELINE_FP4_FLIGHT_PROFILE_IL2CPP_2026-07-28_BENCHMARK.md)
(three IL2CPP runs at viewDistance 5 / 10 / 20). **Verdict: ORDERING-BOUND at every view distance;
ADMISSION-BOUND only from viewDistance ≥ 10.** Per-phase status is in §7.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

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
| **Sub-phase timing**          | 🟡 Partial. `WorldFrameProfiler` (opt-in `Enabled`, flipped only by `FluidStressController` — 3 sites, verified) accumulates four per-frame phases: Apply / Light / Mesh / Tick, bookended in `World.Update` at `World.cs:1993`/`2298`. Frame-level, not per-chunk, and not wired into the benchmark. |
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

| **FP-5 — fix the run-boundary phase leak** | Clear `s_completedPhases` at benchmark-run start, so a second run in one process stops reporting the first run's phases. Found *by* FP-4 (§7.4). **Blocks trustworthy multi-run sessions — land before the next capture.** |   🟢   | FP-4       |
| **FP-6 — print `LoadDistance`**   | Add the capture's own sizing input to the Configuration block (§7.4). Caused v1.7's retracted capacity claim.                                          |   🟢   | FP-4       |

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

### 7.3 Ranked follow-ups from the FP-4 capture (v1.8)

The capture's whole purpose was to decide what to do next, so the ranking is recorded **here** — the report
that produced it is a point-in-time, append-only artifact and cannot be kept current. Engine items are
mirrored in the master backlog; instrument items are owned by this document.

| # | Item | Home | Why it ranks here |
|---|------|------|-------------------|
| **1** | **Chunk service ordering** | **`P-7`** — [analysis §6 item 5](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md), mirrored in [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) | Waste above threshold in **all 9** loading phases at every view distance, including the default with the gate never closing. Largest measured inefficiency in the engine, and now known to be intrinsic. |
| **2** | **Scale panic-gate thresholds with view distance** | **`P-8`** — same two docs | §7.1.1's sibling finding (F5), confirmed by measurement: 0 % / 92.8 % / 96.4 % closure at vd 5/10/20. Correctness-of-intent, not tuning. |
| **3** | **Adopt the visibility criterion as the acceptance target** | This doc + P-7's design | `latency ≤ viewDistance × 16 ÷ speed`. Falsifiable, matches independent visual observation across three legs, and gives (1) a target number rather than "less waste". |
| **4** | **FP-5 — fix the phase leak across runs** | **this doc, §7.4** | Blocks trustworthy multi-run sessions. A second run currently reports the first run's phases. |
| **5** | **FP-6 — print `LoadDistance` in the report** | **this doc, §7.4** | Caused v1.7's retracted capacity claim. Nearly free. |
| **6** | **§7.1 v2 — fix the plurality dilution** | this doc, §7.1.1 | Removes the manual recomputation every future capture would otherwise need. Must bump `RULE_VERSION`. |
| **7** | **Per-chunk CSV export** | Extension roadmap below (v3+) | Only way to separate F4's two stall populations. Demand case now recorded. |

**Not licensed by the capture:** any in-flight-cap or readiness work. Both regimes were tested for at three
view distances and are absent in all of them (`InFlightCap` ≤ 0.6 %, `AllDeclined` ≤ 7.8 %).

### 7.4 FP-5 / FP-6 — instrument defects found by running it (v1.8)

Both were found *by* the capture, and both must land before the next one or its output is untrustworthy.

| Phase | Defect | Fix |
|-------|--------|-----|
| **FP-5** | **Telemetry phases leak across benchmark runs in one process.** `s_completedPhases` is cleared only in `DomainReset` (`PipelineTelemetry.cs:305`), i.e. once per play-mode entry / player start. `BenchmarkController` sets `Enabled = true` at run start (`:196`) but never clears the list, while `_metricsCollector.StartRecording()` resets its own — so the two recorders **disagree at the run boundary** and a second run reports the first run's phases as its own. Observed: the vd-10 log carries all 9 vd-5 phases verbatim before its own 8. | Clear `s_completedPhases` where the run begins. One line, but it is a behavior change to the instrument — own commit, own suite pass. This is the *run-level* instance of exactly the desync FP-3's paired `BeginPhaseBoth`/`EndPhaseBoth` prevents at the phase level. |
| **FP-6** | **`LoadDistance` is absent from the report** despite being the input to the trace-capacity estimate and to any reproduction. It caused v1.7's retracted "capacity model under-predicts 46 %" claim — the wrong table row was read because the run's actual load distance was not in its own output. | Print it in the Configuration block. `_loadDistanceForCapture` is already captured at `BenchmarkController.cs:195`. |

> A third, lower-severity gap from the same capture: **generation waypoint counts differ per run** (12 / 6 / 4
> at vd 5 / 10 / 20), so the generation route is not held constant and cross-run *generation* comparisons are
> confounded. The loading pass uses 12 everywhere and is comparable. Either hold waypoints constant across a
> sweep, or print them prominently enough that the confound cannot be missed — they are currently in the
> Configuration block but easy to overlook.

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
2. **Disabled-path allocation-freedom is not assertable on editor Mono** — inspection plus an IL2CPP GC-alloc
   read is the whole of the evidence.
3. **Unlike the MP-1 counters, this layer is not `[Conditional]`-compiled out of release.** It cannot be: FP-4
   must *toggle* it inside a Development Build. The residual release cost is one static bool read per stage
   transition — the `WorldFrameProfiler` bargain, accepted for the same reason.
4. **Exact percentile recomputation from a written report is not supported** — see §7.2's stated limitation
   and the v3+ CSV item.
5. **FP fixes nothing.** The §4.4-vs-§4.1/§2-vs-ordering-vs-readiness choice happens *after* FP-4's verdict.
   Any change to engine behavior motivated by this capture is a separate item with its own design.

---

## Document History

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

**Last Updated:** 2026-07-27 (v1.6 FP-3 as-built sync)  
**Next Review:** when FP-0 starts, or if the flight symptom is diagnosed by other means first
