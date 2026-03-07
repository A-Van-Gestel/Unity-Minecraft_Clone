# Known Job System related bugs

This document outlines **open** bugs related to the Unity Job System integration for chunk generation, meshing, and lighting. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Mesh job tracks 8 neighbor maps but `AreNeighborsReadyAndLit` only checked 4 — ✅ FIXED

Moved to `_FIXED_BUGS.md` → **Lighting #05 / Chunk Management #02**.

> **Status:** Fixed (March 2026). `AreNeighborsReadyAndLit` now checks all 8 neighbors.

---

## 02. `ProcessGenerationJobs` always uses `biomes[0]` for flora generation

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessGenerationJobs` (line 352)

> [!CAUTION]
> **SEED BREAKING:** Fixing this changes tree height distributions in non-default biomes. Already-explored chunks are unaffected (trees are baked into saved voxel data), but **newly generated chunks** in an existing world would have different tree patterns, creating a visible seam at the old/new terrain boundary.

When processing tree generation mods, the code uses `_world.biomes[0].minHeight` and `_world.biomes[0].maxHeight` for ALL flora positions, regardless of which biome they fall in. In a multi-biome world, trees in non-default biomes get incorrect height ranges.

```csharp
// Current: always uses biomes[0]
IEnumerable<VoxelMod> floraMods = Structure.GenerateMajorFlora(
    mod.ID, mod.GlobalPosition,
    _world.biomes[0].minHeight,    // Should be the actual biome at mod.GlobalPosition
    _world.biomes[0].maxHeight);
```

---

## 03. `ApplyLightingJobResult` creates sections without updating opaque/non-air counts correctly

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ApplyLightingJobResult` (lines 693–697)

When the lighting job result contains non-zero voxel data for a null section (light in previously empty air), a new `ChunkSection` is created and populated. However, `RecalculateCounts` is only called inside the `else` branch of `if (!sectionHasData)`, meaning it runs for existing sections but may produce unexpected results for newly created sections that received only light data (where `GetId` would still return air = 0).

---

## 04. `ProcessLightingJobs` logs every frame when any dropped updates exist

**Severity:** Improvement  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (lines 640–644)

The logging statement fires every frame that `_droppedLightUpdates` has entries. Since the collection is rebuilt each iteration, in busy worlds this effectively logs every frame and can flood the console and impact performance.

---

---
