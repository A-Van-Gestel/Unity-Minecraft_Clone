# Lighting Validation Harness — Fidelity Boundary & Known Gaps

**Status:** Living document
**Created:** 2026-06-13
**Scope:** `Assets/Editor/Validation/Lighting/` — the `LightingValidationSuite` + `LightingTestWorld` + `LightingFrameSimulator` harness.

---

## 1. Why this document exists

The lighting validation suite (44 baselines + frame simulator, menu item
**`Minecraft Clone/Dev/Validate Lighting Engine`**) is strong where it runs **real production code**:
it executes the real `NeighborhoodLightingJob`, stores voxels + light in a real `ChunkData` (section /
uniform-sky storage, merge, and snapshot all run production code — see A1), and shares the real decision
helpers `CrossChunkLightModApplier`, `LightingScheduleDecision`, and `LightingJobProcessor`.

it executes the real persist/replay store (`LightingStateManager` in its disk-free in-memory mode, behind
`IPendingLightStore`) and shares the cross-chunk persist column math via `LightingModPersister` (see B1).

It is **blind** wherever it *reimplements* production or *omits a pipeline stage*. A green suite does
**not** prove those areas are correct. This document enumerates those blind spots so that:

- We don't mistake "all baselines pass" for "lighting is correct" in an un-modelled area.
- We don't waste effort authoring a repro for a bug that lives in a structurally-unreachable layer
  (e.g. Bug 09 — see [LIGHTING_BUGS.md](../Bugs/LIGHTING_BUGS.md)).
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

### B3 — No genuine concurrency (synchronous `.Run()` only)  ·  **WONTFIX (structural)**

- The simulator permutes *orderings* (FIFO/Reverse/Shuffled, multi-frame held flights, budget pressure)
  but not Burst-scheduling / memory-ordering races. 29 baselines have **exhausted** synchronous
  order-permutation for Bug 09.
- **Implication:** the remaining Bug 09 repro, if real, likely needs **in-build instrumentation** (IL2CPP
  master build logging of mod delivery), not another synchronous harness layer. Do not invest in a 6th
  simulator permutation expecting it to catch Bug 09.

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

---

## 5. Priority backlog (snapshot)

| #  | Finding                                         | Status            | Priority         | Effort         |
|----|-------------------------------------------------|-------------------|------------------|----------------|
| A3 | `ModifyVoxel` heightmap (shared) / enqueue path | **PARTIAL**       | Low              | heightmap done |
| A4 | Oracle shared-assumption probes                 | **MOSTLY CLOSED** | Low (2nd oracle) | probes done    |
| B5 | Meshing-gate coverage                           | OPEN              | Low (by design)  | —              |
| C2 | Bug-05 dense-canopy geometry (found Bug 10)     | **CLOSED**        | —                | done           |
| B2 | `neighborsDataReady` toggle                     | **CLOSED**        | —                | done           |
| C1 | Bug-09 geometry fuzz (randomize geometry)       | **CLOSED**        | —                | done           |
| B1 | Chunk-unload / persist-replay path              | **CLOSED**        | —                | done           |
| B4 | Pool-recycle / flag-pairing                     | **CLOSED**        | —                | done           |
| A1 | Section / uniform-sky merge bypass              | **CLOSED**        | —                | done           |
| A2 | Shared mod-routing decision                     | **CLOSED**        | —                | done           |

---

## 6. Cross-references

- Harness file map & API: `.agents/skills/validation-driven-bugfix/references/lighting-suite.md`
- Frame simulator design: [LIGHTING_FRAME_SIMULATOR_DESIGN.md](../Design/LIGHTING_FRAME_SIMULATOR_DESIGN.md)
- Open lighting bugs (Bug 05, Bug 09): [LIGHTING_BUGS.md](../Bugs/LIGHTING_BUGS.md)
- Lighting system overview: [LIGHTING_SYSTEM_OVERVIEW.md](./LIGHTING_SYSTEM_OVERVIEW.md)
- Pipeline invariants: `.agents/rules/chunk-pipeline.md`, `.agents/rules/pool-reset-safety.md`
