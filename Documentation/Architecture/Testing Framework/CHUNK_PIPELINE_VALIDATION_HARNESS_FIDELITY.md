# Chunk Pipeline Validation Harness — Fidelity Boundary & Extension Backlog

**Status:** ✅ **Active backlog** — slice 1 shipped 2026-08-22 (`NS-3`): fixture + frame pump + 6 baselines `B1`–`B6`, plus LP-2's `B7` predicate census (2026-08-23) — **7 total**, menu item **`Minecraft Clone/Dev/Validate Chunk Pipeline`**. The suite guards the pipeline **state machine** (readiness gates, scheduling arms, unload policy), not job output.  
**Created:** 2026-08-22  
**Last updated:** 2026-08-23  
**Scope:**
`Assets/Editor/Validation/ChunkPipeline/` — `ChunkPipelineFixture` + `ChunkPipelineSimulator` + `PipelineAssert`. **Siblings:** [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](LIGHTING_VALIDATION_HARNESS_FIDELITY.md), [MESHING_VALIDATION_HARNESS_FIDELITY.md](MESHING_VALIDATION_HARNESS_FIDELITY.md) — same document shape.

---

## 1. Why this document exists

Every other suite in this project asserts an **output**: a light field, a mesh, a byte stream. This one asserts a **process** — that a set of chunks driven through an adversarial event order still reaches lit + meshed, and that no state flag is left set whose clear site is unreachable. That difference is the whole fidelity risk. An output suite that models its subject badly produces wrong numbers and goes red; a process suite that models its subject badly **converges effortlessly and goes green**, proving nothing.

Two structural defenses follow from that, and both are load-bearing:

1. **`B1` asserts the stranding itself, not merely non-convergence.** It neuters the §9.6 strand guard and requires the center chunk to end up permanently unable to clear `HasLightChangesToProcess`. If `B1` ever passes trivially, the pump has stopped modeling production and `B2`–`B6` are worthless — fix
   `ChunkPipelineSimulator`, not the engine. (§4 explains why the flag, and not the mesh, is the signal.)
2. **Every convergence assertion carries a non-vacuity floor.** `PipelineAssert.Converged` fails a run in which no chunk was ever parked or mesh-declined, and `FlagsPaired` fails a run in which no lighting flag was ever set. A scenario whose adversarial ordering never bit is a scenario that tested nothing.

## 2. What is real, and what is modeled

| Layer                                                                               | In the harness                                                                            |
|-------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------|
| `World.AreNeighborsDataReady` / `AreNeighborsReadyAndLit` / `AreNeighborsMeshReady` | **REAL** — called on a stub `World` whose `worldData` holds real `ChunkData`              |
| `LightingScanDecision.EvaluateReadyChunk`                                           | **REAL**                                                                                  |
| `MeshingScheduleDecision.Evaluate` / `DequeuesChunk`                                | **REAL**                                                                                  |
| `ChunkUnloadDecision.Evaluate`                                                      | **REAL**                                                                                  |
| `ChunkData` flags and their setters                                                 | **REAL** instances                                                                        |
| Generation / lighting / meshing job execution                                       | **MODELED** — a completion event raised after `JobLatencyFrames`                          |
| Per-frame budgets                                                                   | **MODELED** as plain counts; the P-4 rate-quota math is the Pipeline Backpressure suite's |
| Scan visit order                                                                    | **MODELED** as a deterministic coordinate sort; production's is `HashSet` order           |

The gates are drivable for a precise reason, verified by reading them: they read only the three job dictionaries, `worldData`, five `ChunkData` bools, `settings.enableLighting` and `IsChunkInWorld`. No generator, no native memory, no jobs. That is why `WorldJobManager` is stood up **without** its real constructor — and why a gate that later reaches for generator state will throw here rather than pass vacuously.

## 3. Known blind spots

- **Job internals.** A bug inside `NeighborhoodLightingJob` or `MeshGenerationJob` is invisible here; those belong to the Lighting and Meshing suites. This suite only decides *when* jobs may run.
- **True concurrency.** Replay is single-threaded and deterministic. In-flight staleness is modeled through the schedule/complete split; genuine data races are not reachable.
- **Scan order sensitivity.** Production's ready-set is a `HashSet`, whose iteration order is non-deterministic; the pump sorts. A defect that only manifests under a specific visit order would be missed — the `LightingFrameSimulator`'s seeded-shuffle `CompletionOrder` is the pattern to copy when this is closed (**CP-H1** below).
- **The behavior tick.** §3.4 of the pipeline doc: fluid/grass ticks have no neighbor gate at all. Out of scope; owned by the Behavior suite.
- **`MeshBuildQueue` / `MeshDrainPolicy`.** The pump keeps its own simple queue rather than driving the real queue and drain loop. Those are pinned by the MeshQueue suite and the meshing suite's `B25`/`B26`
  respectively, but the *composition* of the real drain with the real gates is not yet exercised (**CP-H2**).
- **Unload geometry.** Scenarios place an out-of-range chunk directly adjacent to a chunk under test. In production `DATA_LOAD_BUFFER` keeps a loaded-but-invisible band between the two, so this geometry is deliberately harsher than reality. It is the right shape for stranding assertions and the wrong shape for meshing ones — see §4.

## 4. Why the §9.6 scenarios assert a flag, not a mesh

`B1` and `B5` assert on `HasLightChangesToProcess`, not on whether the stranded chunk meshes. This was discovered the hard way: a chunk adjacent to an unloaded neighbor fails `AreNeighborsMeshReady` on the **missing neighbor alone**, so a mesh-based assertion goes red whether or not stranding was fixed. It would have been a permanent false green — passing for a reason unrelated to the mechanism under test.

Being permanently unable to clear the flag is what pipeline §9.6 actually describes ("can't schedule lighting, can't mesh, can't be unloaded"), and it is the one signal that distinguishes a guarded pipeline from an unguarded one. `B5` additionally asserts the guard **releases** — a guard that defers forever is its own stall.

## 5. Extension backlog

| ID        | Gap                                                 | Notes                                                                                                                                       |
|-----------|-----------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------|
| **CP-H1** | Seeded-shuffle scan order                           | Mirror `LightingFrameSimulator.CompletionOrder`; re-run every baseline across N seeds                                                       |
| **CP-H2** | Drive the real `MeshBuildQueue` + `MeshDrainPolicy` | Composes the real drain with the real gates; today they are pinned only in isolation                                                        |
| **CP-H3** | Pool recycle + replay                               | `ChunkData.Reset()` must clear every transient flag; `LifecycleEpoch` ABA re-check after an await                                           |
| **CP-H4** | Fault injection in the completion passes            | HF-2 per-job isolation: a merge fault must not strand `IsAwaitingMainThreadProcess` (the lighting suite's `B65` does this for its own pass) |
| **CP-H5** | `NeedsEdgeCheck` / `RemainingEdgeCheckRounds`       | The edge-check arm and its round budget are modeled only as a flag the scan can take; §7's lifecycle is unexercised                         |
| **CP-H6** | Load-from-disk arm                                  | `PopulateFromSave` + the CP-3 load-arm fault path (`IsLoading` clear, re-enqueue on the next crossing)                                      |

## Document History

| Date       | Change                                      |
|------------|---------------------------------------------|
| 2026-08-22 | Created alongside NS-3 slice 1 (`B1`–`B6`). |
| 2026-08-23 | `B7` added by LP-2 — a census of the shared `NeighborReadinessDecision` predicate (3 gates × 2⁷ facts vs an independent oracle). Not a pump scenario: it guards the gate-term matrix the pump provably cannot see. |
