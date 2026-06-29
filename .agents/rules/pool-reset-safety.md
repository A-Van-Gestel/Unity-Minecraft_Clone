---
name: pool-reset-safety
description: Ensures all transient fields on pooled types (ChunkData, ChunkSection, Chunk, VisualizerChunkData) are reset when recycled. Enforced when editing any pooled type or its Reset/Release methods.
trigger: glob
glob: "Assets/Scripts/{Chunk.cs,ChunkPoolManager.cs,ChunkLoadAnimation.cs,Data/ChunkData.cs,Data/ChunkSection.cs,DebugVisualizations/VisualizerChunkData.cs}"
paths:
  - "Assets/Scripts/Chunk.cs"
  - "Assets/Scripts/ChunkPoolManager.cs"
  - "Assets/Scripts/ChunkLoadAnimation.cs"
  - "Assets/Scripts/Data/ChunkData.cs"
  - "Assets/Scripts/Data/ChunkSection.cs"
  - "Assets/Scripts/DebugVisualizations/VisualizerChunkData.cs"
---

# Pool Reset Safety Rules

This project uses custom object pools (`DynamicPool<T>`, `ConcurrentDynamicPool<T>`) to recycle chunk-related objects and avoid GC pressure. A recycled object that carries stale state from its previous lifecycle silently corrupts the pipeline — lighting gets stuck, meshes render for the wrong position, or edge checks never converge.

## The invariant

**Every transient field on a pooled type must have a corresponding reset in the type's `Reset()` (or `Release()`) method. Add both the field and its reset in the same commit.**

## Pooled types and their reset entry points

| Type | Pool | Reset method | Release method |
|---|---|---|---|
| `ChunkData` | `ConcurrentDynamicPool<ChunkData>` | `Reset(Vector2Int pos)` | — |
| `ChunkSection` | `ConcurrentDynamicPool<ChunkSection>` | `Reset()` | — |
| `Chunk` | `DynamicPool<Chunk>` | `Reset(ChunkCoord)` | `Release()` |
| `VisualizerChunkData` | `DynamicPool<VisualizerChunkData>` | `Reset(ChunkCoord, Material, Transform)` | `Release()` |
| `ChunkLoadAnimation` | *(component on pooled Chunk GameObject)* | `ResetToUnderground(Vector3)` | `enabled = false` in `Chunk.Release()` |

## When adding a field to a pooled type

1. **Add the field.**
2. **Add its reset** in the corresponding `Reset()` method (see table above). For `Chunk`, decide whether the reset belongs in `Reset()` (set up for new lifecycle) or `Release()` (tear down before returning to pool) — most state resets go in `Reset()`.
3. **Both changes must be in the same commit.** A field without a reset is a latent pool corruption bug that may not surface until chunks are recycled under load.

## What counts as a transient field

- Any field marked `[NonSerialized]`
- Any field that is not part of the on-disk save format
- Any runtime flag, counter, queue, or cached reference that accumulates state during a chunk's lifecycle
- Any `bool` that gates pipeline progression (`NeedsInitialLighting`, `IsAwaitingMainThreadProcess`, etc.)

## Reset value guidelines

- **Flags and booleans:** reset to their "not yet started" default (usually `false`).
- **Counters with a non-zero default** (e.g., `RemainingEdgeCheckRounds = 2`): reset to the same value used in the constructor or initial assignment — not `0`.
- **Collections** (queues, hashsets, lists): call `.Clear()` to retain allocated capacity.
- **Arrays** (voxel data, heightmaps): use `Array.Clear()` to zero out while retaining the allocation.
- **Object references** (e.g., `ChunkData.Chunk`): set to `null` to unlink.
- **Native containers**: `.Dispose()` in `Release()`, not `Reset()` — they are re-allocated on demand.

## Property setter subtlety

`ChunkData` lighting flags (`NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`) use property setters that invoke `OnLightWorkFlagged` when set to `true`. In `Reset()`, always set these through the property (not the backing field) so the guard logic runs correctly. Setting to `false` does NOT fire the callback — this is intentional.

## Verification checklist

When reviewing a change that adds or modifies a field on a pooled type:

- [ ] Field has a corresponding line in `Reset()` (or `Release()`)
- [ ] Reset value matches the constructor/initial default, not just `0`/`false`/`null`
- [ ] If the field is a pipeline flag, it is also documented in `CHUNK_LIFECYCLE_PIPELINE.md` (per the chunk-pipeline rule)
- [ ] The reset is not guarded by a condition that could skip it (e.g., `if (World.Instance != null)` — the field must be reset regardless)

## Historical context

`RemainingEdgeCheckRounds` was added without a corresponding reset in `ChunkData.Reset()`. Recycled chunks inherited the decremented counter from their previous lifecycle, causing edge-check rounds to silently stop firing. This class of bug is difficult to diagnose because it only manifests after pool recycling under specific movement patterns.
