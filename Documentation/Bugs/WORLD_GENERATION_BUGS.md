# Known World Generation and Data related bugs

This document outlines **open** bugs related to world generation, seed handling, and voxel data management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** May 2026

---

## 01. Seed calculation uses `Mathf.Abs(hashCode) / 10000` hack

**Severity:** Bug  
**Files:** `VoxelData.cs` — `CalculateSeed` (lines 115–144)

> [!CAUTION]
> **SEED BREAKING:** Fixing this will change the computed seed for all worlds created with **string names** or **random seeds**. Existing save files are unaffected (they store the already-computed integer seed), but the same seed string in a new world would generate entirely different terrain. Only worlds created with a **raw integer seed** remain reproducible.
> The seed calculation includes a hack (`Mathf.Abs(hashCode) / 10000`) marked with a TODO. This reduces the effective seed space from ~2 billion to ~200,000, increasing collision odds between different world names. Additionally:

- `Mathf.Abs(int.MinValue)` overflows and returns `int.MinValue` (negative), causing a negative seed downstream.
- String seeds parsed as integers bypass this hack entirely, so numeric strings and string-hashed names behave differently.

---

## TODO: 3D Chunk Preview LOD / Quality Control for Large Radii

**Severity:** Feature Gap  
**Files:** `Assets/Editor/WorldTools/ChunkPreview3DWindow.cs`, `ChunkPreview3DWindow.Pipeline.cs`, `EditorChunkPipelineRunner.cs`, `MeshGenerationJob.cs`

The Cross Section and Biome Editor tabs offer an **X-Z Quality** dropdown (`Off / Full / Half / Quarter / Eighth`) that skips blocks and upscales for faster 2D rendering. The 3D Chunk Preview window has no equivalent — it always generates and meshes every chunk at full resolution, making large radii (8+) slow to iterate on.

**Proposed approach (two phases):**

**Phase 1 — Editor Preview LOD:**  
Add a "Quality" or "LOD" dropdown to the 3D Chunk Preview toolbar (e.g., `Full / Half / Quarter`). At reduced quality levels, reduce the effective mesh detail by either:

- **Skip-and-upscale meshing:** Mesh every Nth section and scale the output geometry, or
- **Reduced-resolution generation:** Generate chunks at a coarser voxel grid (e.g., 8x8x8 instead of 16x16x16) and mesh from that, or
- **Distance-based LOD:** Full-resolution meshing for the center chunk(s), progressively lower detail for outer rings.

The distance-based approach is the most visually useful — center detail stays sharp while outer terrain provides context without the generation cost.

**Phase 2 — Runtime LOD (future optimization):**  
Once the editor LOD system is proven, port the distance-based variant into the runtime chunk pipeline. Far chunks could use simplified meshes (fewer triangles, merged faces) to reduce draw calls and GPU load. This would integrate with the existing `MeshGenerationJob` section system and the chunk readiness pipeline.

---
