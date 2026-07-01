# Known Chunk Management related bugs

This document outlines **open** bugs related to chunk loading, unloading, pooling, and lifecycle management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)

---

## 01. `PopulateFromFlattened` reuses an existing section without clearing its `LightData`

**Severity:** Minor (likely masked by the initial lighting pass)
**Confidence:** Medium — stale-data path verified by inspection; unclear whether a real flow re-populates a section that still holds light.
**Files:** `Data/ChunkData.cs` — `PopulateFromFlattened` (section reuse branch)

When `PopulateFromFlattened` finds a non-null section already in a slot (the comment notes this is rare), it clears only `sections[i].voxels` before copying the new generation data — `LightData` keeps whatever the previous lifecycle wrote, and `SectionUniformSkyLevel` for that slot is not reset either. If a populated-and-lit chunk were ever re-generated in place, the new terrain would start with the old terrain's light values. The subsequent `NeedsInitialLighting` pass overwrites the section light wholesale, which is why no artifact has been observed —
but the reuse branch should `Array.Clear` `LightData` (and reset the slot's sky byte) for symmetry with `ChunkSection.Reset()`.

---
