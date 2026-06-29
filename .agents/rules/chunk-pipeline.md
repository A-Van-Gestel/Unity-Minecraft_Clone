---
name: chunk-pipeline
description: Chunk lifecycle invariants for the generation → lighting → meshing pipeline. Enforced when editing the pipeline orchestration files to prevent recurring deadlocks.
trigger: glob
glob: "{Assets/Scripts/World.cs,Assets/Scripts/WorldJobManager.cs,Assets/Scripts/ChunkPoolManager.cs}"
paths:
  - "Assets/Scripts/World.cs"
  - "Assets/Scripts/WorldJobManager.cs"
  - "Assets/Scripts/ChunkPoolManager.cs"
---

# Chunk Pipeline Invariants

The chunk lifecycle pipeline has produced the same class of deadlock bug three separate times. These rules exist to prevent the fourth.

## Read the pipeline document first

Before editing any pipeline logic in these files, read `@Documentation/Architecture/CHUNK_LIFECYCLE_PIPELINE.md` — specifically the state flags table (Section 2) and readiness gates (Section 3).

## Invariants that must not break

- **Flag pairing.** Every flag that is set somewhere (`NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`, `IsAwaitingMainThreadProcess`) must have exactly one corresponding clear site. If you add a new flag, define both the set and the clear in the same commit.

- **Gate ordering.** A chunk MUST NOT advance to meshing until it passes `AreNeighborsReadyAndLit`. Do not add a "fast path" that skips this gate — historical meshing deadlocks trace to exactly that shortcut.

- **`AreNeighborsDataReady` vs `AreNeighborsReadyAndLit`.** These are different gates with different purposes. The first checks that neighbor terrain data exists (used for initial lighting). The second checks that neighbors are fully lit and stable (used for meshing). Do not conflate them.

- **Pool recycle safety.** `ChunkData.Reset()` must clear every transient flag. When you add a flag, add its clear to `Reset()` in the same change — otherwise a recycled chunk from the pool inherits stale state and silently breaks the pipeline.

- **Main-thread only mutations.** State flags are mutated on the main thread in `World.Update()`. Job code reads a snapshot via `NativeArray`. Never mutate flags from inside a job.

- **Throttling awareness.** The pipeline throttles jobs per frame. A change that assumes "this job runs immediately after scheduling" will fail under load. Always handle the case where a scheduled job completes on a future frame.

## After editing

- Update `@Documentation/Architecture/CHUNK_LIFECYCLE_PIPELINE.md` in the same commit if you changed any flag, gate, or scheduling order.
- Cross-check against `@Documentation/Bugs/CHUNK_MANAGEMENT_BUGS.md` and `@Documentation/Bugs/_FIXED_BUGS.md` to ensure the change does not reintroduce a known-fixed issue.
