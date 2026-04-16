---
name: chunk-lifecycle
description: Authoritative guide for working on the chunk generation → lighting → meshing pipeline in this voxel engine. Use whenever the change touches chunk state transitions, readiness gates, neighbor-dependency checks, the generation/lighting/meshing job schedulers, or the chunk pool recycle path — the pipeline has a history of recurring deadlocks and cross-chunk race conditions.
---

# Chunk Lifecycle Protocol

The chunk pipeline has produced the same class of bug three separate times: a meshing deadlock caused by readiness gate violations or flag-clearing order mistakes. This skill exists to force a read of the authoritative pipeline document before you touch the code, so the fourth
regression does not happen.

## When to use this skill

- Modifying `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`, or anything under `Assets/Scripts/Jobs/` that touches generation, lighting, or meshing jobs.
- Changes to `ChunkData.cs` that add, remove, or re-purpose a state flag (`IsPopulated`, `NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`, `IsAwaitingMainThreadProcess`, `IsLoading`).
- Anything involving neighbor-readiness checks (`AreNeighborsDataReady`, `AreNeighborsReadyAndLit`).
- User reports: "chunks are stuck", "meshing won't run", "lighting never settles", "generation queue backed up", "deadlock".

## How to use it

### Step 1 — Read the pipeline document FIRST

Before editing any pipeline code, read `@Documentation/Technical/CHUNK_LIFECYCLE_PIPELINE.md`. Specifically:

- Section 2 — State flags, who sets them, who clears them.
- Section 3 — Readiness gates (`AreNeighborsDataReady` vs `AreNeighborsReadyAndLit`) — the distinction is load-bearing.
- The flag lifecycle diagram — every state transition must remain reachable from the diagram after your change.

### Step 2 — Invariants that must not break

- **Flag pairing.** Every flag that is set somewhere must have exactly one corresponding clear site. If you add a new `NeedsX` flag, identify the set and the clear in the same commit.
- **Gate ordering.** A chunk must not advance to meshing until it passes `AreNeighborsReadyAndLit`. Do not add a "fast path" that skips this gate — historical meshing deadlocks trace to exactly that pattern.
- **Pool recycle.** `Reset()` must clear every transient flag. When you add a flag, add its clear to `Reset()` in the same change, or a recycled chunk from the pool will inherit stale state.
- **Main-thread only mutations.** State flags are mutated on the main thread in `World.Update()`. Job code reads a snapshot. Do not mutate flags from inside a job.

### Step 3 — Verify with the graph

After your edit, use the code-review-graph MCP:

- `query_graph` pattern=`callers_of` on any flag setter you changed — every caller must still be correct.
- `get_impact_radius` on the modified file — confirm you didn't unintentionally destabilize an adjacent pipeline stage.
- `detect_changes` — risk score should not flag the readiness gates unless you intentionally changed them.

### Step 4 — If behavior is wrong

Do not guess. Switch to the `voxel-debugging` skill and instrument first. The pipeline's symptoms (stuck chunks, missing mesh, black lighting at borders) almost never point at their actual cause — instrument to confirm the stage that stalled before editing.

### Step 5 — Update the pipeline document

`@Documentation/Technical/CHUNK_LIFECYCLE_PIPELINE.md` is a living document and MUST stay in sync with the code. If your change alters any of the following, update the doc in the same commit:

- A state flag (added, removed, renamed, or re-purposed) — update Section 2 (State Flags Reference) and the flag lifecycle diagram.
- A readiness gate (new condition, changed check order) — update Section 3 (Readiness Gates).
- Job scheduling or processing order — update the relevant stage description.
- A new pipeline stage or sub-stage — add a section and extend the diagram.

Do not leave the doc update for a follow-up commit — the doc and the code must ship together so the next reader (human or agent) never consults a stale pipeline reference.

## Known bug categories

Before committing, cross-check against `@Documentation/Bugs/CHUNK_MANAGEMENT_BUGS.md`, `@Documentation/Bugs/LIGHTING_BUGS.md`, `@Documentation/Bugs/JOB_SYSTEM_BUGS.md`, and `@Documentation/Bugs/_FIXED_BUGS.md` to confirm the change does not reintroduce a previously-fixed issue.
