# Lighting Validation Harness — Fidelity Boundary & Known Gaps

**Status:** Living document
**Created:** 2026-06-13
**Scope:** `Assets/Editor/Validation/Lighting/` — the `LightingValidationSuite` + `LightingTestWorld` + `LightingFrameSimulator` harness.

---

## 1. Why this document exists

The lighting validation suite (84 baselines + frame simulator, menu item
**`Minecraft Clone/Dev/Validate Lighting Engine`**; B71–B74 guard the LI-2 band derivation, B75–B78 the
banded-vs-full differential + prove-red, B79–B82 the LI-2b bottom-band derivation + emissive metadata,
B83–B85 the bottom differential with its engagement assertion + raised-floor prove-red, B86–B88 the
Bug-16/17 RGB removal family — simple-form tripwire, runaway-cycle guard, ghost-island guard — B89 the
C12 RGB stale-pull-back self-heal guard, and B90 the Bug-18 RGB cross-seam removal initiator guard) is strong
where it runs **real production code**:
it executes the real `NeighborhoodLightingJob`, stores voxels + light in a real `ChunkData` (section /
uniform-sky storage, merge, and snapshot all run production code — see A1), and shares the real decision
helpers `CrossChunkLightModApplier`, `LightingScheduleDecision`, and `LightingJobProcessor`.

it executes the real persist/replay store (`LightingStateManager` in its disk-free in-memory mode, behind
`IPendingLightStore`) and shares the cross-chunk persist column math via `LightingModPersister` (see B1).

It is **blind** wherever it *reimplements* production or *omits a pipeline stage*. A green suite does
**not** prove those areas are correct. This document enumerates those blind spots so that:

- We don't mistake "all baselines pass" for "lighting is correct" in an un-modelled area.
- We don't waste effort authoring a repro for a bug that lives in a structurally-unreachable layer
  (e.g. Bug 09 — see [LIGHTING_BUGS.md](../../Bugs/LIGHTING_BUGS.md)).
- We have a prioritized backlog of harness improvements.

Findings came from a June 2026 audit comparing the harness against production
`WorldJobManager.ProcessLightingJobs` / `ApplyLightingJobResult` / `ScheduleLightingUpdate` and
`ChunkData.ModifyVoxel`.

### How to read the status tags

| Tag                      | Meaning                                                                        |
|--------------------------|--------------------------------------------------------------------------------|
| **OPEN**                 | Gap still exists; bugs in this area can pass the suite green.                  |
| **CLOSED**               | Addressed; harness now exercises shared/real code here.                        |
| **WONTFIX (structural)** | Cannot be closed within a synchronous editor harness; accept and route around. |

---

## 2. False-pass surfaces (harness reimplements or bypasses production)

These pass green in the suite but the corresponding production code is **not** exercised, so a defect
there is invisible.

### A1 — Section / uniform-sky merge is bypassed entirely ·  **CLOSED (2026-06-13)**

- **Was:** `LightingTestWorld.CompleteLightingJob` merged with `job.LightMap.CopyTo(chunk.Light)` onto a
  flat `ushort[]`; `TestChunk` had no sections. Production `WorldJobManager.ApplyLightingJobResult` does a
  **per-section** merge with `SectionUniformSkyLevel` compaction (`TryCompactSectionLight` →
  `LightingHelper.ClassifyLightData`), `ChunkSection` pool get/return, and a `UNIFORM_SKY_NONE` reset per
  section — and the job's **input** snapshot is reconstructed through the same layer
  (`FillChunkLightMapForJob`/`FillChunkMapForJob`). The harness skipped all of it, so section
  compaction/decompaction bugs, stale uniform-sky bytes, recycled-`ChunkSection` light, and read-path
  reconstruction defects could pass green.
- **Now:** the merge + fill logic was extracted onto `ChunkData` as shared instance methods
  (`ApplyJobLightMap`, `FillJobLightMap`, `FillJobVoxelMap`, `FillEmpty*Map`, with `TryCompactSectionLight`
  moved in). Production (`WorldJobManager.ApplyLightingJobResult`, `WorldData.FillChunk*ForJob`) now
  **delegates** to them — no on-disk layout change, logic-move only. The harness `TestChunk` holds a real
  `ChunkData`: voxels + light live in real `ChunkSection`s and every read/write/snapshot/merge runs the
  production code (`GetVoxel`/`SetVoxel`/`GetLightData`/`SetLightData`/`FillJob*Map`/`ApplyJobLightMap`).
  The suite now traverses the compaction layer it used to skip; all 29 baselines stay green. This also made
  voxel storage section-faithful as a side effect (the merge's keep/discard reads `section.voxels[i]`).
- **Still NOT covered (separate findings):** Phase B uniform-sky-section work is now exercised through the
  shared merge/read path. (`ChunkData.Reset()` / pool recycle stale-state — formerly listed here as B4 —
  is now **closed**; the harness recycles through the real `Reset()` and guards it with baselines B33/B34.)

### A2 — `ProcessLightingJobs` mod-routing orchestration was hand-copied ·  **CLOSED (2026-06-13)**

- **Was:** the per-mod drop/persist/defer/apply decision and the `_completedLightJobs` ordering rule and
  the stability override were duplicated inline in both `ProcessLightingJobs` and
  `CompleteLightingJob` — free to drift.
- **Now:** extracted into the shared `Helpers/LightingJobProcessor` (`RouteCrossChunkMod`,
  `CountsAsRealCrossChunkMod`, `IsEffectivelyStable`), called by both production and harness — mirroring
  the `CrossChunkLightModApplier` / `LightingScheduleDecision` seam. A divergence now breaks the build
  instead of producing a false pass. (Routing **decision** only — the processing *loop* and the merge
  are still separate; see A1.)

### A3 — `PlaceBlock` / `BreakBlock` reimplement `ChunkData.ModifyVoxel`  ·  **PARTIALLY CLOSED (2026-06-13) · remainder LOW**

- `LightingTestWorld.Builder.cs` hand-mirrors `ModifyVoxel`: same-chunk-only neighbor wake, removal-node
  seeding with old values, incremental heightmap maintenance, opacity-change column recalc.
- **Shared (2026-06-13):** the incremental **heightmap maintenance** (Case 1 / Case 2) was the largest
  `World`-independent duplicate; it now lives in `ChunkData.UpdateColumnHeightAfterEdit<TObstruction>`
  (allocation-free struct obstruction test, `IBlockObstruction`), called by **both** production
  `ModifyVoxel` and the harness `PlaceBlock`. A divergence in the height rule now breaks the build.
- **Still divergent (by necessity, LOW):** the BFS **enqueue path** — self removal-node seeding + the
  6-neighbor wake — is structurally `World`-coupled: `AddToSunLightQueue`/`AddToBlockLightQueue` guard on
  `World.Instance.settings.enableLighting` and flag `HasLightChangesToProcess` (→ `OnLightWorkFlagged`),
  so the editor harness (no live `World`) cannot reuse them and seeds its own `TestChunk` queues. Sharing
  this would require decoupling those methods from `World` **and** touching the hot `ModifyVoxel` edit
  path — not worth it for a LOW finding. Likewise, the opacity-change column recalc routes through the
  **world-level** `WorldData.SunlightRecalculationQueue` in production vs a per-chunk queue in the harness
  (the structural divergence near Bug 09's suspected "fluid re-flow floods the queue" path).
- **Mitigation for the remainder:** periodically re-diff the harness `PlaceBlock` enqueue/wake against
  `ModifyVoxel`. If the enqueue path is ever shared, decouple `AddTo*Queue` from `World` first.

### A4 — Oracle shares assumptions with the engine ·  **MOSTLY CLOSED (2026-06-14) · remainder LOW (optional 2nd oracle)**

- `LightingOracle` is a hand-written spec. Where it encodes the **same** rule as the engine — notably the
  vertical-sunlight rule (`isVerticalSunlight` requires `IsFullyTransparentToLight` on both source and
  target), the opaque-source rule, and `Attenuate = max(0, src − max(1, opacity))` — a shared-wrong
  assumption passes `MatchesOracle`.
- **Confirmed shared (2026-06-14 audit):** the suspicion was correct — these are not just *similar* rules,
  they are the **same mechanism** on both sides. `LightingOracle.SolveSky`'s column pass (15 above the
  heightmap, attenuate downward) mirrors production `NeighborhoodLightingJob.RecalculateSunlightForColumn`
  (PASS 1 / PASS 2) line-for-line; the `isVerticalSunlight` condition is byte-identical
  (`LightingOracle.cs` vs `NeighborhoodLightingJob.cs:477`); and both call the identical
  `max(0, src − max(1, opacity))` attenuation. So a defect in any of these, replicated on both sides,
  is invisible to `MatchesOracle`. (Block-obstruction is keyed on opacity — `IsLightObstructing = opacity > 0`,
  `IsFullyTransparentToLight = opacity == 0`, `IsOpaque = opacity ≥ 15` — never on solidity.)
- **Now:** every shared oracle rule has ≥1 **independent hand-derived probe** that asserts a constant the
  oracle never produced (no `MatchesOracle` call), so a formula broken in *both* engine and oracle still
  flips a probe red. Implemented as baselines **B35–B39** in `LightingValidationSuite.OracleProbes.cs`:
    - **B35** — vertical sunlight through open air reaches the floor at full `15` (column pass "15 above heightmap" + no depth attenuation).
    - **B36** — vertical sunlight through a *solid* glass column (opacity 0) stays `15`: the named highest-risk vertical-transparency rule — pins that only opacity, not solidity, blocks light.
    - **B37** — sealed shaft under a leaves cap (opacity 1) decays `14 → 10 → 6` (`−1`/voxel): PASS-2 downward attenuation + the opacity-1 step.
    - **B38** — torch horizontal blocklight falloff `14, 13, … 10` (`−1`/air voxel on all RGB channels): the `max(1, opacity)` air step.
    - **B39** — opaque face receives exactly `source−1` (=13) surface light but never propagates inward (enclosed center stays `0`): tighter than B9's containment-only check.
- **Still NOT covered (remainder, LOW):** the *optional* second, differently-implemented oracle for full
  differential testing is not built — the B35–B39 probes cover the named shared rules but are not a
  general differential check. Pre-existing independent probes (`R == 9`, `crossBorder >= 13`) and
  oracle-free invariants (`NoBlocklightInVolume`, `FieldsEqual` baseline-return) remain in force.

### A5 — Shared `ChunkData` accessors are fail-soft: out-of-bounds behavior is a position lottery ·  **CLOSED (2026-07-05, HF-1)**

- `ChunkData.GetVoxel` / `GetLightData` / `SetLightData` (`ChunkData.cs:853–900`) validate nothing. For an
  out-of-bounds **local** coordinate the outcome depends entirely on the position: a **compacted section**
  returns the uniform sky value for ANY x/z (fully silent, no array touched); most other positions alias a
  *different in-range voxel* (`index = x + 16·localY + 256·z` usually stays inside `[0, 4096)` — silent
  wrong-read); only the extremes (e.g. `x = −1` with `localY = 0, z = 0`, or `y` outside `[0, 128)`)
  actually throw.
- **Why this is a false-pass surface even though the code is shared:** the same contract violation is
  invisible, silently corrupting, or crashing depending on geometry — so any pinned scenario samples one
  lottery ticket. Proven during the Bug 14 hotfix: B60's deliberate both-guards-off sabotage run stayed
  **green** (its halo position wrong-read benignly, and the claim verifier's superseded check then skipped
  it) while the *identical* code crashed in-game on real terrain (`ProcessLightingJobs`
  `ObjectDisposedException` cascade — see B7). Any future consumer of local coordinates can land in
  "not reproducible in harness" status the same way.
- **Closed by roadmap item HF-1** (see
  [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) §10):
  `ChunkData.AssertLocalPositionInChunk` — a `[Conditional("UNITY_EDITOR")]`/`("DEVELOPMENT_BUILD")` guard
  called first in all four accessors (before `GetLightData`'s uniform-sky early-return, so compacted
  sections no longer read silently), throwing with the offending coordinates and chunk position; compiled
  out of IL2CPP master (the reads are the hottest in the engine). The prerequisite caller audit (all 69
  accessor call sites) found **no caller relying on the leniency**: every site is loop-bounded,
  derived-from-lookup, explicitly guarded, or job-volume-bounded (the Burst job's `GetPackedData` sentinel
  bounds every emitted mod/claim). Verified by re-running B60's both-guards-off sabotage: it now goes
  **RED** (`ArgumentOutOfRangeException` at the halo claim `(-1, 49, 8)`) where it previously stayed green —
  retroactively giving B60 its prove-red — with all 53 baselines green under the live assertions once the
  guards were restored. Pairs with **HF-3** (border heightmap fuzz, the C9 lesson — shipped 2026-07-05,
  see C9's follow-through note), which widens how many positions scenarios sample.

---

## 3. Missing harness features (whole bug classes unreachable)

### B1 — No chunk-unload / `RequestChunk == null` / persist-replay path ·  **CLOSED (2026-06-14)**

- **Was:** the grid was fixed; chunks never went unloaded mid-flight, so production's large
  `ProcessLightingJobs` branch — `PersistUndeliverableLightMod`, `_droppedLightUpdates` →
  `LightingStateManager.AddPending`/`AddPendingBlocklight`, `DegradeDeferredCrossChunkMods`, and
  replay-on-load — had no harness analog. `RouteCrossChunkMod`'s `PersistUndeliverable` arm was
  structurally unreachable (the harness hardcoded `targetLoaded: targetInWorld`). The whole
  persist/degrade/replay path — including Bug 08 path 1 and the "chunk lost data mid-flight" half of
  Bug 09 — was untested.
- **Now:** `TestChunk.IsLoaded` + `LightingTestWorld.MarkChunkUnloaded`/`MarkChunkLoaded` model an
  in-world-but-unloaded chunk. `CompleteLightingJob` passes the **real** `targetLoaded`, so the
  `PersistUndeliverable` route fires into a real, disk-free `LightingStateManager` (`CreateInMemory()`,
  held as `IPendingLightStore` so `Save`/`Load` are unreachable — disk I/O is impossible by construction,
  no cross-run `pending_*.bin` contamination). The emitting-chunk-unloaded-mid-flight `else` branch +
  `DegradeDeferredCrossChunkMods` are mirrored; `MarkChunkLoaded` replays the persisted work (sun columns
    + blocklight through the shared `CrossChunkLightModApplier.ComputeBlocklight`) or discards it
      (`ChunkLoadMode.FreshlyGenerated` → `DiscardPendingBlocklight`). The per-mod local-column math is shared
      with production via `LightingModPersister.TryComputeLocalColumn` (a build-time seam — divergence breaks
      the build). Baselines **B30** (persist→replay), **B31** (deferred-mod degrade → replay), and **B32**
      (freshly-generated discard, spill re-derived from the loaded neighbor) all converge to the oracle.
- **Still NOT covered (minor):** the on-disk `Save()`/`Load()` binary round-trip is out of scope by design
  (a serialization concern, not lighting correctness — the in-memory store exercises the identical
  `Add*`/`TryGetAndRemove*` logic). The `AddPendingBlocklight` placement-after-removal guard
  (`LightingStateManager.cs:145`) and sunlight-column persist→replay are run by the store but not yet
  pinned by a dedicated baseline assertion.

### B2 — `neighborsDataReady` is hardcoded `true` in the frame simulator ·  **CLOSED (2026-06-14)**

- **Was:** `LightingFrameSimulator.RunFrame` called
  `LightingScheduleDecision.Evaluate(IsChunkInFlight(coord), neighborsDataReady: true)`. Production's
  `NeighborsNotReady` decision (set `HasLightChangesToProcess = true`, return false, **don't** schedule)
  was never exercised — the third arm of the shared `LightingScheduleDecision.Evaluate` seam was dark,
  and with it the scheduling-deferral path adjacent to Bug 09.
- **Now:** a per-chunk neighbor-readiness toggle on the harness — `TestChunk.NeighborsReady` (default
  true) plus `LightingTestWorld.MarkNeighborsNotReady`/`MarkNeighborsReady` and the
  `AreNeighborsDataReady(coord)` accessor (harness analog of production's `World.AreNeighborsDataReady`).
  `RunFrame` now passes the **real** per-chunk readiness into `Evaluate` and handles the
  `NeighborsNotReady` arm by deferring (retaining the chunk's light work, scheduling nothing), counted in
  the new `FrameResult.ChunksNeighborsNotReady` (kept distinct from `ChunksStarved` so budget-pressure
  baselines stay meaningful). Baseline **B41** places a lamp on a chunk's border while its neighbors are
  marked not-ready, asserts the chunk is deferred every frame (no job scheduled, none in flight, work
  retained, no blocklight propagates), then marks neighbors ready and asserts the retained work runs and
  the field converges to the all-ready oracle — proving the deferral neither loses nor double-applies the
  work. The flag is per-chunk-being-scheduled and distinct from B1's `IsLoaded` (absent-for-mod-delivery
  vs. own-re-lighting-blocked-on-neighbor-terrain).
- **Still NOT covered (minor):** readiness is a hand-set toggle, not *derived* from neighbor
  `IsPopulated`/generation-in-flight state (the harness does not model terrain generation), so a bug in
  the readiness *computation* (`World.AreNeighborsDataReady` itself) is out of scope — only the *handling*
  of its result is pinned. The fuzz layer (C1) still passes `neighborsDataReady: true`.

### B3 — No genuine concurrency (synchronous `.Run()` only)  ·  **WONTFIX (structural) · scope amended 2026-07-03**

- The simulator permutes *orderings* (FIFO/Reverse/Shuffled, multi-frame held flights, budget pressure)
  but not Burst-scheduling / memory-ordering races. 29 baselines have **exhausted** synchronous
  order-permutation for Bug 09.
- **Implication:** the remaining Bug 09 repro, if real, likely needs **in-build instrumentation** (IL2CPP
  master build logging of mod delivery), not another synchronous harness layer. Do not invest in a 6th
  simulator permutation expecting it to catch Bug 09.
- **Scope amendment (2026-07-03 async-testability analysis,
  [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)):**
  the WONTFIX stands for what it covers — synchronous order-permutation of the **pre-MT-2** scheduling
  model — but it is not a blanket "async is untestable" verdict. Three routes remain open:
  (a) the MT-2 park/promote layer is a *new*, fully sync-modelable async surface (finding **B6**, roadmap
  AS-2); (b) genuine concurrency CAN enter the editor deterministically via real `Schedule()` +
  equivalence-to-serial gating, the `FluidParallelDeterminismValidation` pattern (roadmap AS-4 — covers
  pooled-buffer aliasing and Burst-side races; Burst codegen is identical editor/player); (c) the
  in-build instrumentation this finding calls for is now specced as an automated rig (roadmap AS-5).
  Only IL2CPP managed-code memory ordering and real wall-clock timing remain structurally out of editor
  reach (roadmap §8).

### B4 — No pool-recycle / flag-pairing modeling ·  **CLOSED (2026-06-14)**

- **Was:** per `.agents/rules/pool-reset-safety.md` and `chunk-pipeline.md`, `RemainingEdgeCheckRounds`-
  stale-after-recycle was a real shipped bug. The harness gated the pipeline on its OWN mirror state
  (`TestChunk.HasLightWork` + a local `const edgeCheckRounds = 2`), never on `ChunkData`'s real flags,
  and never called `ChunkData.Reset()` or recycled (`new ChunkData` per chunk). So a recycled-chunk-with-
  stale-flags defect — a documented *recurring* family — was invisible.
- **Now:** the harness drives pipeline gating off `ChunkData`'s **real** flags — `HasLightChangesToProcess`
  (backs `TestChunk.HasLightWork`), `RemainingEdgeCheckRounds` (consumed by the edge-check loops via the
  shared `DecrementEdgeCheckRound`), `NeedsEdgeCheck` (consumed + cleared in `BeginLightingJob`), and
  `IsAwaitingMainThreadProcess` (set/cleared across `CompleteLightingJob`). The static
  `ChunkData.OnLightWorkFlagged` is neutralized for the harness's lifetime (save/null/restore) so the real
  setters are safe headless. `LightingTestWorld.RecycleAllChunks()` routes every chunk through the real
  production `Reset()` (the pool return/acquire path; `World.Instance == null` → its `Array.Clear(sections)`
  fallback). Two baselines guard it: **B33** recycles a slab/sky-well world through `Reset()` and re-lights
  to the same oracle field (only correct if light/queues/sections/flags cleared AND
  `RemainingEdgeCheckRounds` restored to 2 — a stale 0 skips the edge rounds), and **B34**
  (`LightingAssert.AssertResetClearsTransientState`) dirties a real `ChunkData` and asserts `Reset()` clears
  every transient surface, with a **reflection backstop over every `[NonSerialized]` primitive field** so a
  new transient flag/counter added later without a reset is caught generically — without a test edit.
- **Still NOT covered (minor):** the harness's BFS wake-up queues remain `TestChunk`-managed mirrors of
  `ChunkData`'s (production's `AddTo*Queue` is `World`-coupled — see A3), so the production queues' reset is
  verified by B34's direct check rather than through the live enqueue path.

### B5 — Lighting→meshing handoff is out of scope ·  **OPEN · LOW (by design)**

- The suite proves "light **field** is correct", not "the mesh ever rebuilds". The `ScheduleMeshing` gate
  on `HasLightChangesToProcess` / `NeedsInitialLighting` (the recurring deadlock family) is unreachable.
  Reasonable boundary for a lighting-correctness suite — noted so we don't assume otherwise.

### B6 — MT-2 `LightWorkScheduler` park/promote layer is unmodeled ·  **CLOSED (2026-07-06, AS-2)**

- **What changed:** MT-2 (`Helpers/LightWorkScheduler`, shipped 2026-07-02 — *after* this document's
  June audit and the entire Bug-09 sync-repro campaign) split the lighting dirty set into a per-frame
  **ready** set and a parked **waiting** set. A parked chunk re-enters scheduling ONLY through a
  promotion event: its own flag transition (`ChunkData.OnLightWorkFlagged` → `Flag`), a 3×3
  neighborhood unblock (`PromoteNeighborhood` — generation/load completion, lighting job completion at
  `WorldJobManager.cs:1099`), or the ~1 s `PromoteAll` fail-safe backstop.
- **The gap:** `LightingFrameSimulator.RunFrame` Phase 2 still scans **`AllChunkCoords()` every frame** —
  the pre-MT-2 model in which no chunk can ever be forgotten. The harness also **nulls**
  `ChunkData.OnLightWorkFlagged` for its lifetime (`LightingTestWorld.cs:97-98`), so the
  flag→staging→ready event path is structurally absent. The `LightWorkSchedulerValidationSuite` (9
  baselines) tests the scheduler class in isolation; **nobody tests the integration** — whether every
  unblock event in the pipeline has a matching promotion hook.
- **Why it matters:** a *missed-promotion stall* (work parked forever, rescued only by the fail-safe or
  a player edit) is exactly Bug 09's symptom shape, and production explicitly treats a recurring
  non-zero fail-safe count as a bug to investigate (`World.cs:1588-1591`) — but no suite can turn that
  red today.
- **Fix:** roadmap item **AS-2** in
  [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) —
  an opt-in frame-simulator mode driving a real `LightWorkScheduler` with mirrored park/promote sites
  and the fail-safe OFF by default, so "converges without the backstop" becomes a mechanical gate.
  Legacy mode stays the default so existing baselines are untouched.
- **CLOSED (2026-07-06, AS-2 Phases 1–3):** `LightingFrameSimulator(schedulerMode:true)` now drives a
  real `LightWorkScheduler` — the ready/waiting split scanned via the shared `LightingScanDecision`, the
  flag sink wired (`SetLightWorkFlagSink`), and the promotion hooks mirrored (completion
  `PromoteNeighborhood` in the completion-pass driver, neighbor-ready/load via the `MarkNeighborsReady`/
  `MarkChunkLoaded` wrappers), with the `PromoteAll` fail-safe **off** by default. Phase-3 baselines:
  **B66** (cross-chunk both-modes parity), **B67** (neighbor-ready promotion un-parks), **B68**
  (50-seed Bug-09 geometry fuzz in scheduler mode, fail-safe off), **B69** (prove-red:
  `SuppressCompletionPromotion` stalls a chunk re-flagged mid-flight, only the fail-safe recovers it),
  **B70** (border-heightmap fuzz — the Bug-05 re-granted edge round — settles in scheduler mode). The
  `RunReGrantedEdgeCheckRound` quiescence hook is retained as a legacy-mode backstop (B70 confirms
  scheduler mode settles the border edit through the real edge gate). Suite at **62 baselines**.

### B7 — `ProcessLightingJobs` per-pass bookkeeping is production-only ·  **CLOSED — FULL (2026-07-06, HF-4 #2); near-term closure was 2026-07-05, HF-2**

- The harness replays per-**job** logic (`CompleteLightingJob` mirrors merge → deferred drain →
  pull-back-claim verification → mod routing) and the frame simulator replays scheduling decisions — but
  nobody replays the production **pass skeleton**: iterate the `LightingJobs` dictionary, release each
  completed job's containers *inside* the loop, remove entries from the dictionary only *after* the loop
  (via `_completedLightJobs`).
- **The failure class this hides:** any exception mid-pass strands already-released jobs in the dictionary
  → per-frame `ObjectDisposedException` spam re-touching disposed containers (observed in-game during the
  Bug 14 hotfix — the original thrower was hidden behind hundreds of cascade repeats). In the suite the
  same exception presents as **one red scenario** (the runner's per-scenario try/catch), never as a
  cascade — the class is structurally invisible, like B3/B5/B6 it lives in code the harness does not run.
- **Closed (near-term) by roadmap item HF-2** (see
  [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) §10):
  per-job fault isolation in production *eliminates* the cascade class instead of modeling it. All three
  passes (`ProcessLightingJobs`, `ProcessGenerationJobs`, `ProcessMeshJobs` — the audit confirmed the
  gen/mesh passes share the release-inside-loop/remove-after-loop surface) now isolate each job: a failed
  `Handle.Complete()` leaves the job enrolled un-released for retry; a fault after `Complete()` logs one
  error, still releases + removes that job, and the pass continues. The lighting pass clears
  `IsAwaitingMainThreadProcess` in a per-job `finally` (flag-pairing invariant), re-flags the chunk
  (`HasLightChangesToProcess`), and counts faults in `LastFaultedLightJobs`; the generation pass's
  budget-retry paths keep their deliberate un-released `continue` semantics. Behavior documented in
  `CHUNK_LIFECYCLE_PIPELINE.md` §4. The full-fidelity alternative — extracting the pass skeleton into a
  shared orchestrator the harness can drive — remains **HF-4**, deliberately folded into AS-2 / NS-3
  rather than done standalone.
- **Closed FULL by HF-4 #2 (2026-07-06):** the pass skeleton is now `Helpers/LightingCompletionPass.cs`
  (`RunMergeLoop` + `RunRemoveAndPromote`, driven via `ILightingCompletionDriver<TKey>`).
  `ProcessLightingJobs` implements the driver on `this` (byte-identical — all 57 baselines green); the
  frame simulator implements it too, so the harness replays the exact release-inside / remove-after
  ordering and two-stage fault isolation. Baseline **B65** injects a merge fault into one job of a
  four-job pass (`LightingFrameSimulator.SetMergeFaultInjector` → `LightingTestWorld.AbortLightingJob`)
  and asserts the fault is isolated + counted, the other jobs still complete, the faulted job is removed
  rather than stranded, and the field recovers once the lost work is resubmitted — the multi-job cascade
  class the runner's per-scenario try/catch could never present. Behavior in `CHUNK_LIFECYCLE_PIPELINE.md`
  §4 (shared skeleton pointer).

### B8 — The BFS work-cap fail-safe was asserted by only two scenarios ·  **CLOSED (2026-07-12)**

- Bug 16 (the runaway RGB removal cycle → OOM, `_FIXED_BUGS.md` Lighting #21) left a permanent fail-safe
  in `NeighborhoodLightingJob`: `MAX_BFS_NODES_PER_PASS` aborts a runaway pass with a
  `[LightingJob DIAG]` console **error** + near-cap node dump. Before this fix only **B87/B88** listened
  for it (`WorkCapAbortListener`). A future termination regression arming on a *different* scenario's
  geometry would log the error yet leave that scenario's PASS/FAIL untouched — the interactive menu summary
  stayed green.
- **Closed** by promoting the listener to a **runner-level invariant** in
  `Editor/Validation/Framework/ValidationSuiteRunner.Execute` (`FailSafeErrorScope` + the pure
  `IsFailSafeError` predicate): a scenario during whose body a `LogType.Error` carrying a registered
  fail-safe marker (`FAIL_SAFE_ERROR_MARKERS`, currently `[LightingJob DIAG]`) is logged is **force-failed**,
  for all 8 suites at once. Generic — a future engine fail-safe joins by adding its marker; scoped to the
  scenario body (subscribe/run/unsubscribe); restricted to tagged Errors so the fault-isolation baselines
  (which deliberately log errors) still pass. The per-baseline `WorkCapAbortListener` stays in B87/B88 as
  belt-and-braces. **Self-test:** two Validation Framework scenarios (16→18) pin the predicate and the
  scope's trip via a `Feed` seam (a real marker log would bubble through the global
  `Application.logMessageReceived` into the self-test's own scope); the force-fail was also proven
  end-to-end (a tagged-error scenario returning `true` is marked failed). `Validate All` green at 181
  baselines.

---

## 4. Coverage gaps (scenario authoring, not harness limits)

### C1 — The whole Bug 09 fleet uses ONE geometry ·  **CLOSED (2026-06-14)**

- **Was:** B15–B29 all used `LampWhite` at `(31,11,24)` on the `(1,1)/(2,1)` **+X** border; the 50-seed
  sweeps permuted only *order* (the `worldFactory` ignored the seed, so geometry was byte-identical across
  iterations). The other five faces, all four corners (diagonal-neighbor delivery), other source types,
  and which chunk is held in-flight were never varied. With order-permutation exhausted (B3), the entire
  *geometry* axis was the untested search space.
- **Now:** a property-based geometry fuzz layer (`LightingValidationSuite.Bug09Fuzz.cs`). `Bug09FuzzCase.FromSeed`
  randomizes — as a pure function of the seed — the **border** (4 faces + 4 corners), **source block**
  (`LampWhite/Red/Green/Blue/Torch`), **filler block** (`Water/Glass/DimGlass`), **held-in-flight chunk**
  (emitting vs. the possibly-diagonal target), and **per-frame budget** (`1/2/unlimited`), then runs
  break → filler → re-emit under `CompletionOrder.Shuffled` and asserts convergence to the borderless
  oracle. The new seeded `FindFailingSeed(Func<int,LightingTestWorld>, …)` overload threads the seed into
  the world factory **and** scenario body, so a returned failing seed reproduces geometry *and* ordering
  exactly. Tiered: **B40** runs 50 seeds on every suite invocation; the dedicated menu item
  **`Minecraft Clone/Dev/Validate Lighting Engine (Bug 09 Geometry Fuzz)`** runs 2000 nightly and logs the
  first failing seed's full `Describe()` case. Precondition was A4 — the oracle is now independently
  pinned (B35–B39) so a fuzz mismatch is an engine/harness defect, not an oracle artifact.
- **Result:** all 40 baselines green (B40 @ 50 seeds) and the nightly 2000-seed run all converge. This is
  **not** a Bug-09 repro — consistent with B3, a synchronous fuzz cannot catch an async/Burst race; its
  value is (a) broad regression coverage over the cross-chunk/corner geometry space that was dark before,
  and (b) strong evidence Bug 09 is not synchronous cross-chunk logic.
- **Still NOT covered (minor):** no failure **shrinker** — a failing seed is a complete repro but not a
  *minimal* one (would need manual reduction before promotion to a dedicated baseline); the grid is fixed
  3×3 (enough for diagonal delivery); and the fuzz still passes `neighborsDataReady: true` (see B2), so the
  scheduling-deferral path stays unexercised even under fuzzing.

### C2 — Bug 05 needs the right geometry, not a new capability ·  **CLOSED (2026-06-14)**

- **Was:** the harness already did `RunInitialLightingParallel` + 2 edge rounds, but only ever over B8's
  single-diagonal-well geometry (which converges). The *geometry* Bug 05 names — dense multi-pocket
  canopies whose pockets depend on multi-chunk diagonal paths — was the untested axis.
- **Now:** a procedural dense-canopy geometry fuzz (`LightingValidationSuite.Bug05Canopy.cs`).
  `Bug05CanopyCase.FromSeed` randomizes — as a pure function of the seed — the canopy height/thickness, the
  number and placement of sky wells, and the opaque under-canopy dividers that carve the gap into pockets
  and force winding cross-chunk paths; each case runs the production wave-parallel initial lighting and
  asserts the field matches the borderless oracle. Tiered like C1: **B42** runs a small sweep on every
  suite invocation; the menu item **`Minecraft Clone/Dev/Validate Lighting Engine (Bug 05 Canopy Fuzz)`**
  runs 200 nightly and, on a failing seed, re-runs it with forced extra edge rounds
  (`LightingTestWorld.RunInitialLightingParallelForcedEdgeRounds` — the realized form of the proposed
  `ConvergedWithin`) to classify it as a round-budget shortfall vs. an unreachable pocket.
- **Outcome:** the fuzz did **not** reproduce the Bug-05 shadow mechanism, but it **found a different,
  deterministic engine bug** — **Bug 10**, an over-bright *leak* of an opaque border block's surface light
  across the chunk boundary (`CheckEdgeVoxel`/`CheckEdgeVoxelRGB` lacked the neighbor-opacity guard that
  the in-chunk propagators have; the inverse artifact Bug 05's notes anticipated). With Bug 10 fixed, **all
  fuzz seeds converge to the oracle within the production 2 edge-check rounds** — confirming the analytical
  result that in-range 6-connected paths reconcile within 2 rounds, so Bug 05 (if real) is not
  synchronously reproducible (parallel to Bug 09 / finding B3). Per the validation-driven-bugfix
  "won't-reproduce → baseline" rule, the canopy fuzz now guards dense-canopy generation convergence as
  **B42** (broad regression coverage) rather than reproducing Bug 05.
- **Still NOT covered (minor):** no failure shrinker; grid fixed at 5×5; and, like C1, the search is
  synchronous (it cannot catch an async/Burst race). A faithful Bug-05 repro, if the bug is real, likely
  needs in-build instrumentation (see B3), not another synchronous geometry layer.

### C3 — Cross-chunk *sunlight removal / darkening* race quadrant ·  **CLOSED (2026-06-21) · was a PREREQUISITE for LI-1 → P-2**

- The dynamic cross-chunk matrix was lopsided. **B7** covers blocklight *removal* across a border with the
  neighbor in flight; **B13** covers sunlight *uplift* (addition) across a border in flight; **B12** covers
  blocklight cross-border *re-spread* after a removal. The *steady-state* sunlight-darkening-across-a-border
  half was subsequently covered by the Bug-12 family (**B51** asymmetric two-shaft, **B53** mutually-lit seam
  loop, **B52** multi-hop ring), but the **race** quadrant — a sunlight *removal* deferred into an **in-flight**
  neighbor (the sunlight twin of B7) — still had no scenario. This is also the exact neighborhood
  [Bug 11](../../Bugs/LIGHTING_BUGS.md) lived in (`CrossChunkLightModApplier.ComputeSunlight` removal path),
  so it doubles as a Bug-11 regression guard.
- **Why it was a prerequisite.** **LI-1** (single halo-padded lighting volume —
  [PERFORMANCE_IMPROVEMENTS_REPORT.md](../../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) Lighting §) and the
  **P-2** substrate it seeds change *how the BFS reads across chunk borders* (halo array reads replacing the
  9-map/hashmap dispatch). LI-1's acceptance criterion is **bit-identical light output**, and **TG-4** Phase 4
  rides the same halo substrate (option (a) = P-2). A halo that under-reads or mis-indexes the seam on the
  *darkening* path would produce wrong light there. C1/C2 (B40–B44) assert cross-chunk *brightening* fuzz; C3
  closes the *darkening* half. **C3 is now green and must stay green before LI-1 freezes any halo-vs-9-map diff.**
- **No new harness capability was needed:** reuses `PlaceBlock`, `BeginLightingJob`/`CompleteLightingJob`,
  `GetSkyLight`, the borderless oracle, and `RunToConvergence`/`RunWaveToConvergence` exactly as B7/B13/B53.
- **Closed by** (`Baselines/LightingValidationSuite.Baseline.C3Darkening.cs`, suite was at B53; prove-red
  confirmed 2026-06-21 — neutering the cross-chunk sunlight-removal apply reds B54/B55 with the (2,1) side
  stuck at the stale spill, restored → all green):
    - **B54 — in-flight race (the genuinely-missing quadrant).** A single sky shaft at x28 in `(1,1)` spills
      across the `(1,1)/(2,1)` seam into `(2,1)`. `BeginLightingJob((2,1))` (snapshots the bright pre-seal
      state) → seal the shaft in `(1,1)` → run `(1,1)`'s job: it emits a cross-chunk sunlight **removal** mod
      toward in-flight `(2,1)`, which **must be deferred** (asserted `ModsDeferred > 0`) and drained after
      `(2,1)`'s merge. `RunWaveToConvergence` → the previously-lit `(2,1)` voxel re-darkens to 0 and the field
      matches the borderless oracle. Without the defer/drain the removal is lost and `(2,1)` stays brighter
      than the oracle (the Bug-08-class failure, on the previously-untested sunlight-removal route).
    - **B55 — steady-state canonical representative.** Same geometry; seal under sequential
      `RunToConvergence`; the darkness crosses the seam and the corridor (incl. the `(2,1)` side) matches the
      oracle. A simpler explicit representative than the Bug-12 loop geometries (B51/B53).

### C4 — Sunlight-column persist→replay and the `AddPendingBlocklight` placement-after-removal guard are unpinned ·  **CLOSED (2026-06-14)**

- **Was:** elevated from the B1 "still NOT covered" note. **B30/B31/B32 all persist→replay *blocklight***
  (torch). The persist store's **sunlight-column** path (`PersistMod` `Channel == Sun` →
  `LightingStateManager.AddPending` → `SunColumnRecalcQueue` replay on load — `LightingTestWorld.cs:859`) and
  the `AddPendingBlocklight` placement-after-removal guard (`LightingStateManager.cs:145`) both *ran* but no
  baseline asserted their result.
- **Now (a — sunlight persist→replay, DONE):** baseline **B46** (`Baseline_PersistReplayCrossChunkSunlight`,
  the Sun-channel twin of B30). A roof break on the (1,1) border opens a shaft whose spill targets (2,1) while
  it is `MarkChunkUnloaded`; the suite asserts the emitting job's `ModsPersisted > 0` (the Sun-channel persist
  route fired), that the under-roof sky stays at its pre-break shadowed value while unloaded, then that
  `MarkChunkLoaded(LoadFromDisk)` replays the column, re-derives the spill from (1,1)'s lit border, brightens
  the region, and matches the all-loaded oracle. No new harness capability was needed.
- **Now (b — the `AddPendingBlocklight` guard, DONE):** baseline **B47**
  (`LightingAssert.AssertPendingBlocklightPlacementAfterRemovalGuard`) pins the order-sensitive guard
  (`LightingStateManager.cs:165`) directly against the real in-memory store (oracle-free, like B34 pins
  `Reset()`): a placement mod must NOT overwrite a pending removal for the same voxel (the removal's darkness
  wave must still run on load), while a removal MUST overwrite a pending placement. A regression here (dropping
  the guard, or making it symmetric) is invisible to every field-comparison baseline.

### C5 — Attenuation is only ever single-layer; no cumulative multi-layer probe ·  **CLOSED (2026-06-14)**

- **Was:** every attenuation check was a single obstruction — **B2** (one DimGlass pane), **B37** (one leaves
  cap, opacity 1, which can't even disambiguate the per-step convention since a leaf step equals the air step).
  No scenario pinned that light loses `max(1, opacity)` *per step* through several layers in series, so a
  shared engine+oracle bug in the per-step composition passed `MatchesOracle` silently — an A4-class blind spot.
- **Now:** baseline **B45** (`Baseline_ProbeCumulativeMultiLayerAttenuation`, oracle-independent like B35–B39):
  a vertical sky probe through a 3-block DimGlass cap over a sealed stone shaft (`15 → 10 → 5 → 4`) and a
  horizontal blocklight probe through two DimGlass panes in a stone tunnel (`15 → 10 → 5 → 4`, all channels) —
  both with hand-counted constants, no `MatchesOracle`. Confirms attenuation **composes** across opacity-5
  layers (each charges −5, distinguishable from the −1 air step).
- **Derivation correction surfaced while authoring (kept here as the rationale):** attenuation is charged on
  **entering** a voxel (the destination's opacity, `max(0, src − max(1, opacity))`), and the BFS flood
  dominates the column-recalc result. The sky-exposed top block reads 15 "for free" (it's the heightmap
  surface), so two *stacked* DimGlass charge only once vertically — a **third** layer is needed to observe two
  cumulative −5 steps. The horizontal case charges both panes because the lamp source isn't free. (My first
  pass asserted a 2-layer vertical stack and read `9`, not `5` — engine correct, probe wrong; fixed to a
  3-block cap. This is exactly the A4 value: an oracle-independent constant forces the rule into the open.)

### C6 — No per-channel removal-independence scenario ·  **CLOSED (2026-07-11, by the Bug-16/17 arc) — the gap's predicted bug class was real, twice**

- **Was:** B10/B11 *blend* two channels; B12 removes a *white* source. Nothing asserted that removing one
  *colored* source leaves the *other* channel intact (the per-channel `PropagateDarknessRGB` path).
- **Closed** by exactly the predicted scenario shape — and authoring it found **two shipped bugs** in the
  per-channel removal path (`_FIXED_BUGS.md` Lighting #21 Bug 16 runaway removal cycle → OOM, #22 Bug 17
  sourceless ghost island): **B86** (overlap red + green across the seam, break the red, green survives and
  red clears — the literal C6 ask), **B87** (the interrupted-cycling runaway guard, promoted from K16a),
  **B88** (the ghost-island guard, promoted from K17a). All in
  `Baselines/LightingValidationSuite.Baseline.Bug16Runaway.cs` (+ B88's registration site).
- **Standing lesson (mirrors C9's):** this finding sat OPEN·LOW–MEDIUM for a month while both bugs were
  live. A "small-effort, no-new-capability" coverage gap on a *removal* path is cheap insurance —
  removal is the engine's documented problem area (`LIGHTING_SYSTEM_OVERVIEW.md` §3.7) and deserves
  priority inflation over placement-path gaps of equal apparent size.

### C7 — Diagonal/corner delivery and dynamic in-chunk opaque-placement re-shadow lack deterministic baselines ·  **OPEN · LOW (corner half upgraded — Bug 16 evidence)**

- Corner (diagonal-neighbor) cross-chunk delivery exists only *inside* B40's fuzz (probabilistic per seed);
  there is no fixed, deterministic corner-spill baseline (source at a chunk corner → diagonal neighbor lit →
  oracle). **Priority evidence (2026-07-11):** Bug 16's infinite removal cycle armed at a **four-chunk
  corner** (the x15|16 / z31|32 junction) — multi-seam corners are where per-channel colored-light cycles
  live, not just a delivery edge case. The corner-spill baseline should use *colored* lamps, not white.
- Dynamic in-chunk column re-shadow on opaque *placement* (place a block mid-air in a lit column → everything
  below darkens to side-bleed → break → re-light) is untested as a dedicated baseline; B3 only opens a shaft.
  Likely caught incidentally by other oracle compares — hence LOW.

### C10 — No RGB analog of the Bug 12 sourceless-loop initiator ·  **CLOSED (2026-07-12) — the predicted latent bug was real (Bug 18), fixed + guarded by baseline B90**

- The sky↔RGB removal-machinery parity matrix after Bug 17: sky had the **initiator** (Bug 12,
  `EmitCrossChunkSunlightRemoval`), the **veto** (Bugs 11/13, `In/CrossChunkSunlightSupport`) and **claim
  verification** (Bug 14, `PullBackClaim`). RGB had the **veto only** (Bug 17,
  `In/CrossChunkBlocklightSupport`), and `EmitCrossChunkSunlightRemoval` had no blocklight counterpart.
- **The predicted bug was confirmed real.** The B53-twin scenario — a sealed blocklight corridor straddling
  the x15|16 seam, two equal-color lamps as the only sources, both broken in the same wave — reproduced RED:
  the seam settled stable-but-wrong at a ~38-voxel over-bright red residue (worst R13 at the seam) with no
  collapse path, the Bug 17 veto actively protecting the stale mutual support. Filed **Bug 18** (`_FIXED_BUGS.md`
  Lighting #23).
- **Fixed** by mirroring the Bug 12 initiator to RGB per channel (`NeighborhoodLightingJob.EmitCrossChunkBlocklightRemoval`,
  emitted from `PropagateDarknessRGB` at the 2-cycle signature `nX == node.LightX`), adjudicated by the
  **existing** Bug 17 veto (no apply-side change). The Bug 14 claim-verification analog was **not** needed —
  C12's pull-back self-heals (baseline B89) — so the fix is initiator-only, mirroring Bug 17 adding only the
  veto.
- **Guarded by baseline B90** (`LightingValidationSuite.C10RgbLoop.cs`, promoted from repro K18a). Prove-red
  confirmed: neutering the emit reds only B90 (the residue returns); B86–B88, B50–B53, and B89 stay green.

### C11 — The interrupted-reconciliation axis has exactly ONE recipe instance ·  **CLOSED (2026-07-12) — seeded fuzz B91 + band differential B92; the fuzz surfaced a Bug-09-shaped under-delivery lead**

- **Closed** by a seeded interrupted-reconciliation fuzz (`LightingValidationSuite.InterruptedReconFuzz.cs`):
  **B91** runs 24 seeds per suite invocation (nightly menu item `… (Interrupted Reconciliation Fuzz)` runs
  500), each building a randomized colored-lamp cross-seam world (± water) and running a randomized number of
  interrupted break/re-place cycles via the Bug-16 held-flight primitives (held neighbor flight + under-budgeted
  waves), then asserting **convergence + zero work-cap aborts + oracle**. **B92** is the cheap companion — the
  B87 recipe as a banded-vs-full differential (B75–B78 pattern), since interrupted flights change the LI-2
  queued-node extents the sequential-edit differentials never exercise.
- **Scope note (deliberate):** the fuzz covers **face-adjacent seams** — the cross-seam mutual-support topology
  the removal-machinery churn (Bug 16/17/18) lives on. Diagonal 4-chunk-corner pairs are excluded: they are not
  face-adjacent, and the interrupted schedule strands their cross-chunk *placement* delivery, surfacing an
  **under-bright** (not over-bright) divergence — the **Bug 09** shape, orthogonal to this fuzz's removal axis.
  That lead (a *synchronous* cross-chunk under-delivery — the first) is recorded under Bug 09 in
  `LIGHTING_BUGS.md`, not conflated into this baseline.
- **Diagonal-corner lead RESOLVED (2026-07-12) — harness-fidelity artifact, not a Bug-09 defect.** The
  excluded corner geometry's under-bright divergence exists only because the fuzz recipe settles with plain
  `RunWaveToConvergence`, which omits production's post-stabilization edge-check *re-add* rounds. Replaying
  the identical interrupted schedule and then driving one re-granted edge-check round
  (`RunReGrantedEdgeCheckRound`, i.e. `LightingFrameSimulator.RunToConvergence`'s quiescence hook — the
  Bug-05 border-column re-grant path production takes after the final water placement) heals it completely
  (41 → 0 divergent voxels). Confirms the §3.7 invariant: cross-chunk *placement* under-bright is always
  edge-check-recoverable. B91 stays seam-scoped and unchanged; a future diagonal-corner axis must settle
  through an edge-check-inclusive driver to avoid re-surfacing this artifact. Full classifier under Bug 09
  in `LIGHTING_BUGS.md`.

- Every scenario except B87/B88 edits a **converged** field and lets reconciliation **complete**. Bug 16
  required ≥2 *interrupted* cycles (edits landing mid-reconciliation: held pre-edit flights +
  under-budgeted waves + water attenuation) to build the non-monotone mixed-channel plateau state that
  armed the cycle — a state no hand-authored converged-field scenario can reach. B87/B88 pin one geometry ×
  one schedule; the axis itself is otherwise unexplored.
- **Missing scenario family:** a seeded **interrupted-reconciliation fuzz** (the HF-3/C1 pattern applied to
  the churn axis): randomized colored-lamp placements near seams/corners (± water volumes), randomized
  interleavings of {break, re-place, held flight, 1-wave budget}, then settle and assert convergence +
  zero work-cap aborts + oracle. Reuses `RunBug16InterruptedCyclingRecipe`'s primitives; suite-tier seed
  count + nightly menu item per the HF-3 precedent.
- Cheap companion: run the B87 recipe as a **banded-vs-full differential** (the B75–B78 pattern) — the
  LI-2/2b band derivations consume queued-node extents, and interrupted flights change those extents
  mid-stream; the existing differentials only script *sequential* edits.
- Modeling limit (note, don't chase): the real Burst fluid tick driving opacity churn during lighting
  reconciliation stays out of scope — B87 hand-models re-flow via `PlaceBlock(Water)`; cross-system
  interplay is B3-adjacent structural territory.

### C12 — RGB darkness-phase pull-backs are unverified (Bug 14's RGB mirror) ·  **CLOSED (2026-07-12) — verdict GREEN: the claim-free pull-back self-heals; scopes the fix to the initiator only**

- `CheckEdgeVoxelRGB`, when called from inside the darkness phase (the Bug 07 defect-2 seam pull-back),
  re-lights just-darkened border cells from stale snapshot halo values with **no claim verification** —
  `PullBackClaim` carries `WrittenSky` only, and `PullBackClaimStillSupported` is sky-only. This is the
  exact mechanism that planted sourceless sky ghost light in Bug 14, and it was one of the two candidate
  planting writes in Bug 17 (the fix took the veto route; the plant path itself was unverified).
- **Closed by baseline B89** (`Baselines/LightingValidationSuite.Baseline.C12RgbPullback.cs`): the B60/B61
  RGB mirror — a held-flight interleave that forces the west chunk's darkness wave to run its
  `CheckEdgeVoxelRGB` pull-back against a snapshot in which the east neighbor is still lit (it has since
  gone dark). **Verdict: the stale, claim-free pull-back self-heals** — the field converges to the
  borderless oracle because the ordinary *asymmetric* cross-seam removal branch clears the stale re-light
  on the following wave (the B51 lesson, on the RGB channel). This confirms the Bug 17 investigation's
  finding that "neutering the RGB claim path changed nothing," so **no RGB claim-verification mirror is
  needed** — the RGB parallel of Bug 17 adding only the veto.
- **Prove-red (demonstrated, not automated):** making the two lamps *symmetric* flips B89 red — that is
  exactly repro **K18a / Bug 18** (the mutually-equal seam with no removal initiator; a 56-voxel surviving
  ghost). So the held-flight harness demonstrably detects a surviving cross-seam RGB ghost; B89's
  *asymmetric* arrangement staying green is what isolates C12 (pull-back, self-heals) from Bug 18
  (initiator, the real gap).
- **Consequence for the fix:** the RGB removal-machinery gap is the missing **initiator** (Bug 12's RGB
  mirror — [C10](#c10--no-rgb-analog-of-the-bug-12-sourceless-loop-initiator-and-no-scenario-that-would-expose-it) →
  Bug 18) **only**, not the claim verification. The "one RGB removal-machinery parity work item" is
  therefore initiator-scoped.

### C8 — Initial lighting only ever runs as a single simultaneous wave ·  **OPEN · LOW (was Bug 05's untried axis; Bug 05 fixed via the post-edit axis 2026-07-05, so this drops to belt-and-braces re-verification)**

- Every generation-shaped scenario (B8, the B42 canopy fuzz, the B40 geometry fuzz) lights **all chunks
  in one concurrent wave** with `neighborsDataReady: true` throughout (the C1 remainder). Production
  lights a **moving frontier**: chunks join incrementally as generation completes, per-chunk readiness
  flips over time, and each chunk's 2 `RemainingEdgeCheckRounds` are consumed at *different relative
  times*. Dense canopies at a frontier are exactly Bug 05's habitat — this scheduling axis, unlike the
  exhausted geometry axis (C2), has never been fuzzed.
- **Needs no new harness capability** — a scenario driver can maintain a seeded joined-set, flag each
  chunk's initial lighting only at join time, and derive `MarkNeighborsReady`/`MarkNeighborsNotReady`
  from the joined-set, interleaved with simulator frames. Spec: roadmap item **AS-3** in
  [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)
  (includes the modeling limit: voxel data pre-exists — the stagger models "populated but not yet lit",
  which is the production initial-lighting environment, not "terrain absent" = B1 territory).

### C9 — Flat scenario worlds never exercise border shadow-casters ·  **CLOSED (2026-07-04, found by the Bug 14 hotfix)**

- Every scenario terrain is a superflat floor plus hand-placed features, so the column-recalc
  **shadow-caster** branch (`RecalculateSunlightForColumn` waking the highest block's horizontal
  neighbors, **including cross-border ones** — the one production path that seeds darkness nodes in the
  halo) essentially never fired at a seam. Real terrain hits it constantly. Consequence: the Bug 14 fix's
  pull-back claims could carry out-of-center positions in-game, crash `ProcessLightingJobs`, and leave
  every suite scenario green — the crash class was structurally invisible to the corpus.
- **Closed** by baseline **B60** (seam overhang → partially-lit neighbor → border-column edit; asserts
  the cross-border wave fires via `ModsEmitted > 0` + oracle convergence), plus the center-only claim
  contract in the job and defensive bounds-skips in both verifiers.
- **Residual — resolved by HF-1 (2026-07-05):** originally the crash itself was not scenario-provable —
  at B60's position the harness `ChunkData` tolerated the out-of-bounds read as a benign wrong-voxel read
  that the verifier's superseded check skipped (a deliberate both-guards-off sabotage run stayed green),
  so B60 only pinned path liveness/convergence. With HF-1's fail-fast accessor assertions (see A5) the
  same sabotage now reds B60 loudly, so the scenario guards the crash class too. Standing lesson for
  scenario authoring: prefer at least one **varied-heightmap-at-seam** geometry per new cross-chunk
  feature — flat worlds under-sample the shadow-caster and halo-node paths.
- **HF-3 follow-through (2026-07-05) — the lesson validated:** the border-heightmap fuzz shipped
  (`LightingValidationSuite.BorderHeightFuzz.cs`: per-column random heights at every seam, seam
  overhangs, seeded border edits; K15a 25-seed suite tier + 200-seed nightly menu item) and its first
  run found two bugs on the very geometry axis this finding predicted flat worlds were hiding:
  seed 0 → **Bug 15** (cross-seam surface stamps wiped by border-column edits; fixed + confirmed +
  archived `_FIXED_BUGS.md` Lighting #19, distilled repros promoted to baselines **B62/B63**), and
  seed 14 → the **first faithful synchronous Bug 05 repro** (post-edit edge-round exhaustion). **Bug 05
  was then fixed** (2026-07-05, `ChunkData.ModifyVoxel` re-grants a bounded edge-check round on a
  border-column opacity edit), confirmed in-game, and archived (`_FIXED_BUGS.md` Lighting #20); the fuzz
  was promoted from K15a to baseline **B64**. One geometry axis, two bugs — the lesson paid off twice.
- **New modeling note from the Bug 05 fix (frame-sim edge-check timing):** driving seed 14 green exposed
  that the frame simulator lacks production's neighbor-stability **edge-check gate** (`AreNeighborsReadyAndLit`,
  which naturally defers a re-armed edge check until the neighborhood settles). A per-completion re-arm in
  the simulator ran the edge check *mid-churn* and the field settled back to its under-report. The fix
  models the settled-field edge check by consuming the border-edit re-grant at grid **quiescence**
  (`LightingTestWorld.RunReGrantedEdgeCheckRound`, driven from `LightingFrameSimulator.RunToConvergence`) —
  outcome-faithful and consistent with how `RunInitialLighting*` already drive generation edge rounds as a
  post-convergence loop. The exact per-frame edge-gate was **delivered by AS-2** (2026-07-06): scheduler
  mode's ready scan has the real `AreNeighborsReadyAndLit` edge arm, and baseline **B70** confirms the
  border-edit re-grant settles through it in scheduler mode. The `RunReGrantedEdgeCheckRound` quiescence
  hook is retained as the legacy-mode backstop (the legacy baselines depend on it).

> **None of C3–C12 require a new harness capability** — each reuses existing primitives
> (`MarkChunkUnloaded`/`MarkChunkLoaded`, `BeginLightingJob`/`CompleteLightingJob`, the pure-channel lamp
> palette, `GetSkyLight`/`GetBlocklightRGB`, `RunBug16InterruptedCyclingRecipe`; C11's fuzz needs only a
> scenario-level seeded driver, the HF-3 shape). The genuinely open *harness/framework* gaps remain those
> already catalogued: the runner-level work-cap invariant (B8), no failure shrinker (C1/C2), fixed grid
> sizes (3×3 / 5×5), `neighborsDataReady` is a hand-set toggle not derived from generation state (B2), no
> second differential oracle (A4), the on-disk `Save()`/`Load()` round-trip is out of scope (B1), and the
> lighting→meshing gate is out of scope (B5).

---

## 5. Redundancy & overlapping coverage (Bug-09 fleet consolidated 2026-06-14)

This section catalogues baselines that test the **same** property (the inverse of a fidelity gap). The Bug-09
fleet redundancy was **resolved on 2026-06-14**; the smaller intentional overlaps below are kept by design.

### The Bug-09 guard fleet (former B15–B29) — CONSOLIDATED

**Was:** fifteen scenarios (promoted from K09a–m) all asserted a single property — *the defer/drain +
re-schedule mechanism converges to the oracle* — over a **single geometry** (`LampWhite` at `(31,11,24)`, the
`(1,1)/(2,1)` +X border), permuting only completion/scheduling order. With **B3** declared WONTFIX
(synchronous order-permutation exhausted) and **B40** fuzzing geometry *and* order, most were subsumed:

| Baseline(s)       | Distinct axis it adds                    | Subsumed by                                 | Verdict     |
|-------------------|------------------------------------------|---------------------------------------------|-------------|
| B15, B16          | direct-harness single / double in-flight | B40 (held-chunk) + B26–B28                  | collapsible |
| B17               | ContainsKey scheduling guard             | runs in *every* frame-sim scenario          | collapsible |
| B18               | single-slot budget                       | B27 / B40 `budget:1`                        | collapsible |
| B19               | reverse completion order                 | Shuffled order (B26–B29, B40)               | collapsible |
| B20, B21          | multi-frame held flight (one chunk)      | B40 holds a chunk across frames             | collapsible |
| B23, B24, B25     | fluid contention, single instances       | B26 (50-seed shuffled fluid), B27 (+budget) | collapsible |
| **B22, B28**      | **both chunks in flight simultaneously** | — (B40 only ever holds one)                 | **keep**    |
| **B26, B27, B29** | **full 5-face underwater environment**   | — (B40's fillers are single voxels)         | **keep**    |
| **B40**           | **the geometry / corner axis**           | —                                           | **keep**    |

**Now:** the ten single-instance permutations (former B15–B21 + B23–B25) were folded into **two deterministic
representatives**, reusing the freed low numbers: **B15** (direct-harness break+place — single- then
both-in-flight) and **B16** (fluid break→water→place under a held flight + single-slot budget). **B22**
(dual-chunk both-in-flight), **B26–B29** (50-seed sweeps) and **B40** (geometry fuzz) were kept — together they
still cover every retired axis (ContainsKey accumulate, budget, shuffled/reverse order, multi-frame held,
both-chunks-in-flight, fluid-opacity contention, the geometry/corner space). Numbers **B17–B21** and
**B23–B25** are intentionally retired and left unused so existing references and commit history stay valid.
Suite count 47 → 39, all green. (Cross-refs B7/B13 already cover the direct-harness *removal*/uplift in-flight
races, so B15's manual-flight path is not the only guard of that machinery.)

### Smaller, intentional overlaps (note, don't remove)

- **B1 ↔ B38** — same physical setup (torch in open air); B38 is deliberately B1 with hand-derived constants
  instead of `MatchesOracle` (the A4 design).
- **B9 ↔ B39** — both assert opaque receive-but-don't-propagate; B39's docstring says *"Tighter than B9."*
  B9 keeps the place/break BFS-wake trigger, B39 keeps the exact surface-stamp magnitude.
- **B3 ↔ B35 / B37** — the full-bright vertical shaft and the downward attenuation are re-pinned independently
  by the A4 probes.
- **B33 ↔ B8** — B33's *pre-recycle* block is a near byte-for-byte repeat of B8 (same geometry, same
  `RunInitialLightingParallel`, same `MatchesOracle`); only the post-recycle assertion is novel.

---

## 6. Priority backlog (snapshot)

| #   | Finding                                                                                                                                                                                                                                       | Status            | Priority         | Effort         |
|-----|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------|------------------|----------------|
| C10 | RGB sourceless-loop initiator absent (Bug 12's RGB mirror) — B53-twin **confirmed red → Bug 18**; FIXED (RGB initiator) + baseline B90, prove-red confirmed                                                                                   | **CLOSED**        | —                | done           |
| C11 | Interrupted-reconciliation axis has ONE recipe instance — **seeded fuzz B91 + band differential B92**; fuzz surfaced a Bug-09-shaped sync under-delivery lead → **RESOLVED harness artifact** (edge-check re-add heals it; Bug 09 stays open) | **CLOSED**        | —                | done           |
| C12 | RGB darkness-phase pull-backs unverified (Bug 14's RGB mirror) — B60/B61-twin; **verdict GREEN, self-heals → baseline B89**, scopes fix to initiator-only                                                                                     | **CLOSED**        | —                | done           |
| B8  | Work-cap fail-safe asserted by only B87/B88 — **promoted to a runner-level `FailSafeErrorScope` invariant** (all 8 suites) + 2 framework self-tests                                                                                           | **CLOSED**        | —                | done           |
| C4  | Sunlight persist→replay (B46) + `AddPendingBlocklight` guard (B47)                                                                                                                                                                            | **CLOSED**        | —                | done           |
| C5  | Cumulative multi-layer attenuation probe (B45)                                                                                                                                                                                                | **CLOSED**        | —                | done           |
| C3  | Cross-chunk sunlight darkening race quadrant (B54/B55) — prereq for LI-1 → P-2 / TG-4 Ph.4                                                                                                                                                    | **CLOSED**        | —                | done           |
| A5  | Fail-soft `ChunkData` accessors — out-of-bounds is a position lottery (closed by HF-1)                                                                                                                                                        | **CLOSED**        | —                | done           |
| B6  | MT-2 `LightWorkScheduler` park/promote layer unmodeled (closed by AS-2 scheduler mode + B66–B70)                                                                                                                                              | **CLOSED**        | —                | done           |
| B7  | `ProcessLightingJobs` pass bookkeeping production-only (HF-2 near-term; full replay via `LightingCompletionPass` + B65, HF-4 #2)                                                                                                              | **CLOSED (full)** | —                | done           |
| C8  | Single-wave-only initial lighting — staggered-frontier axis unfuzzed (→ roadmap AS-3; Bug 05 fixed via the post-edit axis, so now belt-and-braces)                                                                                            | OPEN              | Low              | medium         |
| C9  | Flat scenario worlds never exercise border shadow-casters (B60; HF-3 fuzz shipped 2026-07-05 — found Bug 15 → B62/B63 + the first sync Bug-05 repro)                                                                                          | **CLOSED**        | —                | done           |
| C6  | Per-channel removal independence (B86–B88 — authoring it found Bugs 16+17, `_FIXED_BUGS.md` #21/#22)                                                                                                                                          | **CLOSED**        | —                | done           |
| C7  | Deterministic corner spill / in-chunk re-shadow (corner half upgraded by Bug-16 evidence — use colored lamps)                                                                                                                                 | OPEN              | Low              | small          |
| §5  | Bug-09 fleet (B15–B25) consolidation                                                                                                                                                                                                          | **CLOSED**        | —                | done           |
| A3  | `ModifyVoxel` heightmap (shared) / enqueue path                                                                                                                                                                                               | **PARTIAL**       | Low              | heightmap done |
| A4  | Oracle shared-assumption probes                                                                                                                                                                                                               | **MOSTLY CLOSED** | Low (2nd oracle) | probes done    |
| B5  | Meshing-gate coverage                                                                                                                                                                                                                         | OPEN              | Low (by design)  | —              |
| C2  | Bug-05 dense-canopy geometry (found Bug 10)                                                                                                                                                                                                   | **CLOSED**        | —                | done           |
| B2  | `neighborsDataReady` toggle                                                                                                                                                                                                                   | **CLOSED**        | —                | done           |
| C1  | Bug-09 geometry fuzz (randomize geometry)                                                                                                                                                                                                     | **CLOSED**        | —                | done           |
| B1  | Chunk-unload / persist-replay path                                                                                                                                                                                                            | **CLOSED**        | —                | done           |
| B4  | Pool-recycle / flag-pairing                                                                                                                                                                                                                   | **CLOSED**        | —                | done           |
| A1  | Section / uniform-sky merge bypass                                                                                                                                                                                                            | **CLOSED**        | —                | done           |
| A2  | Shared mod-routing decision                                                                                                                                                                                                                   | **CLOSED**        | —                | done           |

---

## 7. Cross-references

- Harness file map & API: `.agents/skills/validation-driven-bugfix/references/lighting-suite.md`
- Frame simulator architecture: [LIGHTING_FRAME_SIMULATOR_DESIGN.md](LIGHTING_FRAME_SIMULATOR_DESIGN.md)
- Async-bug testability roadmap (AS-1…AS-5 — B3 amendment, B6, C8, Bug 05/09 plans) + harness-hardening
  follow-ups (HF-1…HF-4 — A5, B7, C9 remediations):
  [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)
- Open lighting bugs (Bug 09): [LIGHTING_BUGS.md](../../Bugs/LIGHTING_BUGS.md)
- Lighting system overview: [LIGHTING_SYSTEM_OVERVIEW.md](../LIGHTING_SYSTEM_OVERVIEW.md)
- Pipeline invariants: `.agents/rules/chunk-pipeline.md`, `.agents/rules/pool-reset-safety.md`
