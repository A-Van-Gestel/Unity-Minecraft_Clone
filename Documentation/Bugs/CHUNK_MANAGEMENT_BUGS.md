# Known Chunk Management related bugs

This document outlines **open** bugs related to chunk loading, unloading, pooling, and lifecycle management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. `_chunksToBuildMesh` uses `List.Remove()` which is O(n)

**Severity:** Improvement (performance)  
**Files:** `World.cs` — `CheckViewDistance`, `UnloadChunks`

When deactivating or unloading a chunk, `_chunksToBuildMesh.Remove(chunk)` performs an O(n) linear scan. While the `HashSet` (`_chunksToBuildMeshSet`) provides O(1) duplicate detection, the actual removal from the ordered list is still slow. With many pending rebuild chunks, this could bottleneck rapid player movement.

---
