# Known Chunk Management related bugs

This document outlines **open** bugs related to chunk loading, unloading, pooling, and lifecycle management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. `ChunkCoord` integer division truncates negative coordinates incorrectly

**Severity:** Bug (latent)  
**Files:** `Chunk.cs` — `ChunkCoord(Vector2Int)` (lines 445–449), `ChunkCoord(Vector3Int)` (lines 457–461)

The `Vector2Int` and `Vector3Int` constructors perform raw integer division (`pos.x / VoxelData.ChunkWidth`) without `FloorToInt`. In C#, integer division truncates toward zero, so for negative coordinates (e.g., `pos.x = -1`), the result is `0` instead of the expected `-1`.

While the world is currently constrained to positive coordinates, this is latent — the `Vector3` and `Vector2` constructors correctly use `Mathf.FloorToInt`.

---

## 02. `_chunksToBuildMesh` uses `List.Remove()` which is O(n)

**Severity:** Improvement (performance)  
**Files:** `World.cs` — `CheckViewDistance`, `UnloadChunks`

When deactivating or unloading a chunk, `_chunksToBuildMesh.Remove(chunk)` performs an O(n) linear scan. While the `HashSet` (`_chunksToBuildMeshSet`) provides O(1) duplicate detection, the actual removal from the ordered list is still slow. With many pending rebuild chunks, this could bottleneck rapid player movement.

---

## 03. Chunk load animation adds `GetComponent` on every activation

**Severity:** Improvement (performance)  
**Files:** `Chunk.cs` — `PlayChunkLoadAnimation`

~~`GetComponent<ChunkLoadAnimation>() == null` is called on every pool activation to check for duplicates. `GetComponent` is not free.~~

> **Status:** Partially fixed (March 2026). A `_hasLoadAnimation` boolean flag was introduced to cache whether the component was already added, removing the `GetComponent` call. However, if the component is ever destroyed externally, the flag would be stale.
