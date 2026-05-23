# Known World Generation and Data related bugs

This document outlines **open** bugs related to world generation, seed handling, and voxel data management. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## TODO: 2D Cross-Section Preview missing flora / structure rendering

**Severity:** Feature Gap  
**Files:** `Assets/Editor/WorldTools/WorldGenPreviewWindow.CrossSection.cs` — `EvaluateColumn()`

`EvaluateColumn()` replicates the runtime `StandardChunkGenerationJob` logic per block column but skips flora and structure placement entirely. The generation job emits `StructureSpawnMarker` structs for trees, grass, flowers, and boulders, which are then expanded on the main thread via `IChunkGenerator.ExpandStructure()`. The cross-section preview has no equivalent expansion step, so all flora and structures are absent from the X-Y, Z-Y, and X-Z panels.

**Proposed fix:** After column evaluation, run a simplified flora pass that checks `StructurePoolEntry` grid election per-column and renders structure template blocks inline. Cross-chunk structures can be ignored since the preview only renders 2D slices.

---

## TODO: 3D Chunk Preview missing flora / structure rendering

**Severity:** Feature Gap  
**Files:** `Assets/Editor/WorldTools/ChunkPreview3DWindow.Pipeline.cs`

The new `ChunkPreview3DWindow` uses the actual runtime Burst jobs for terrain generation, lighting, and meshing, but skips structure expansion after generation. The `StandardChunkGenerationJob` already emits `StructureSpawnMarker`s into a `NativeQueue` — these are currently disposed unused.

Full flora support requires:

1. After generation completes, dequeue `StructureSpawnMarker`s and call `IChunkGenerator.ExpandStructure()` on the main thread to produce `VoxelMod`s.
2. Apply each `VoxelMod` to the correct chunk's `NativeArray<uint>` map using coordinate translation (global position → chunk origin + local offset).
3. Route cross-chunk `VoxelMod`s (structures spanning chunk boundaries) to neighbor chunk maps — requires a lightweight editor-only modification manager.
4. Re-run lighting after modifications are applied.

**Proposed fix:** Build an `EditorModificationManager` class that buffers `VoxelMod`s per chunk origin, applies them to the stored maps, and handles cross-chunk routing before the lighting phase begins.

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
