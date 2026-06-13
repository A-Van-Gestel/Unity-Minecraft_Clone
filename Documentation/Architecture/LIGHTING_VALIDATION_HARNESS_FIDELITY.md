# Lighting Validation Harness — Fidelity Boundary & Known Gaps

**Status:** Living document
**Created:** 2026-06-13
**Scope:** `Assets/Editor/Validation/Lighting/` — the `LightingValidationSuite` + `LightingTestWorld` + `LightingFrameSimulator` harness.

---

## 1. Why this document exists

The lighting validation suite (29 baselines + frame simulator, menu item
**`Minecraft Clone/Dev/Validate Lighting Engine`**) is strong where it runs **real production code**:
it executes the real `NeighborhoodLightingJob`, stores voxels + light in a real `ChunkData` (section /
uniform-sky storage, merge, and snapshot all run production code — see A1), and shares the real decision
helpers `CrossChunkLightModApplier`, `LightingScheduleDecision`, and `LightingJobProcessor`.

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
- **Still NOT covered (separate findings):** `ChunkData.Reset()` / pool recycle stale-state (B4) is its own
  gap — the harness creates fresh `ChunkData` per chunk and never recycles. Phase B uniform-sky-section work
  is now exercised through the shared merge/read path.

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
  path — not worth it for a LOW finding. Likewise the opacity-change column recalc routes through the
  **world-level** `WorldData.SunlightRecalculationQueue` in production vs a per-chunk queue in the harness
  (the structural divergence near Bug 09's suspected "fluid re-flow floods the queue" path).
- **Mitigation for the remainder:** periodically re-diff the harness `PlaceBlock` enqueue/wake against
  `ModifyVoxel`. If the enqueue path is ever shared, decouple `AddTo*Queue` from `World` first.

### A4 — Oracle shares assumptions with the engine ·  **OPEN · LOW**

- `LightingOracle` is a hand-written spec. Where it encodes the **same** rule as the engine — notably the
  vertical-sunlight rule (`isVerticalSunlight` requires `IsFullyTransparentToLight` on both source and
  target), the opaque-source rule, and `Attenuate = max(0, src − max(1, opacity))` — a shared-wrong
  assumption passes `MatchesOracle`.
- **Partly mitigated** today by independent hardcoded probes (`R == 9`, `crossBorder >= 13`) and
  oracle-free invariants (`NoBlocklightInVolume`, `FieldsEqual` baseline-return).
- **Suggested fix:** ensure every oracle rule has ≥1 independent hardcoded probe; the vertical-transparency
  rule is the highest-risk shared assumption and currently lacks one. Optionally add a second,
  differently-implemented oracle for differential testing.

---

## 3. Missing harness features (whole bug classes unreachable)

### B1 — No chunk-unload / `RequestChunk == null` / persist-replay path ·  **OPEN · HIGH**

- Production `ProcessLightingJobs` has a large branch the harness has no analog for:
  `DegradeDeferredCrossChunkMods`, `PersistUndeliverableLightMod`, `_droppedLightUpdates` →
  `LightingStateManager.AddPending`, and replay-on-load. The grid is fixed; chunks never vanish or become
  unpopulated mid-flight.
- Bug 09's own suspected cause ("job cancelled/re-scheduled", "chunk lost data mid-flight") lives partly
  here. The entire persist/replay light path is untested.
- **Suggested feature:** a "mark chunk unloaded/unpopulated mid-flight" capability on `LightingTestWorld`
  so `RouteCrossChunkMod` can return `PersistUndeliverable` and the degrade/replay path becomes reachable.

### B2 — `neighborsDataReady` is hardcoded `true` in the frame simulator ·  **OPEN · MEDIUM**

- `LightingFrameSimulator.RunFrame` calls
  `LightingScheduleDecision.Evaluate(IsChunkInFlight(coord), neighborsDataReady: true)`. Production's
  `NeighborsNotReady` decision (set `HasLightChangesToProcess = true`, return false, **don't** schedule)
  is never exercised — a scheduling-deferral path adjacent to Bug 09.
- **Suggested feature:** a per-chunk "neighbors not ready" toggle on the simulator.

### B3 — No genuine concurrency (synchronous `.Run()` only)  ·  **WONTFIX (structural)**

- The simulator permutes *orderings* (FIFO/Reverse/Shuffled, multi-frame held flights, budget pressure)
  but not Burst-scheduling / memory-ordering races. 29 baselines have **exhausted** synchronous
  order-permutation for Bug 09.
- **Implication:** the remaining Bug 09 repro, if real, likely needs **in-build instrumentation** (IL2CPP
  master build logging of mod delivery), not another synchronous harness layer. Do not invest in a 6th
  simulator permutation expecting it to catch Bug 09.

### B4 — No pool-recycle / flag-pairing modeling ·  **OPEN · MEDIUM**

- Per `.agents/rules/pool-reset-safety.md` and `chunk-pipeline.md`, `RemainingEdgeCheckRounds`-stale-after-
  recycle was a real shipped bug. The harness models the **value** `RemainingEdgeCheckRounds = 2` but never
  models `ChunkData.Reset()`, pool recycle, or the set/clear-site pairing of
  `HasLightChangesToProcess` / `NeedsEdgeCheck` / `IsAwaitingMainThreadProcess`. A recycled-chunk-with-
  stale-flags defect — a documented *recurring* family — is invisible.

### B5 — Lighting→meshing handoff is out of scope ·  **OPEN · LOW (by design)**

- The suite proves "light **field** is correct", not "the mesh ever rebuilds". The `ScheduleMeshing` gate
  on `HasLightChangesToProcess` / `NeedsInitialLighting` (the recurring deadlock family) is unreachable.
  Reasonable boundary for a lighting-correctness suite — noted so we don't assume otherwise.

---

## 4. Coverage gaps (scenario authoring, not harness limits)

### C1 — The whole Bug 09 fleet uses ONE geometry ·  **OPEN · HIGH ROI**

- B15–B29 all use `LampWhite` at `(31,11,24)` on the `(1,1)/(2,1)` border; the 50-seed sweeps permute only
  *order*. A property-based **fuzz layer** over the existing simulator — randomizing border location,
  source block type, edit sequence, and which chunk is held in-flight (reusing `FindFailingSeed`) — would
  explore orders of magnitude more state for almost no new infrastructure. Bump iterations well past 50 for
  a nightly run. **Highest-value cheap addition.**

### C2 — Bug 05 needs the right geometry, not a new capability ·  **OPEN · MEDIUM**

- The harness already does `RunInitialLightingParallel` + 2 edge rounds; what's missing is the *geometry*
  the bug names: dense multi-pocket canopies whose light depends on diagonal paths that consume both
  cardinal edge-check rounds. A procedural dense-canopy filler + a `ConvergedWithin(rounds, n)` assertion
  would make it findable.

---

## 5. Priority backlog (snapshot)

| #  | Finding                                         | Status      | Priority        | Effort         |
|----|-------------------------------------------------|-------------|-----------------|----------------|
| B1 | Chunk-unload / persist-replay path              | OPEN        | High            | Medium         |
| C1 | Bug-09 fuzz layer (randomize geometry)          | OPEN        | High (ROI)      | Low            |
| B2 | `neighborsDataReady` toggle                     | OPEN        | Medium          | Low            |
| B4 | Pool-recycle / flag-pairing                     | OPEN        | Medium          | Medium         |
| C2 | Bug-05 dense-canopy geometry                    | OPEN        | Medium          | Medium         |
| A3 | `ModifyVoxel` heightmap (shared) / enqueue path | **PARTIAL** | Low             | heightmap done |
| A4 | Oracle shared-assumption probes                 | OPEN        | Low             | Low            |
| B5 | Meshing-gate coverage                           | OPEN        | Low (by design) | —              |
| A1 | Section / uniform-sky merge bypass              | **CLOSED**  | —               | done           |
| A2 | Shared mod-routing decision                     | **CLOSED**  | —               | done           |

---

## 6. Cross-references

- Harness file map & API: `.agents/skills/validation-driven-bugfix/references/lighting-suite.md`
- Frame simulator design: [LIGHTING_FRAME_SIMULATOR_DESIGN.md](../Design/LIGHTING_FRAME_SIMULATOR_DESIGN.md)
- Open lighting bugs (Bug 05, Bug 09): [LIGHTING_BUGS.md](../Bugs/LIGHTING_BUGS.md)
- Lighting system overview: [LIGHTING_SYSTEM_OVERVIEW.md](./LIGHTING_SYSTEM_OVERVIEW.md)
- Pipeline invariants: `.agents/rules/chunk-pipeline.md`, `.agents/rules/pool-reset-safety.md`
