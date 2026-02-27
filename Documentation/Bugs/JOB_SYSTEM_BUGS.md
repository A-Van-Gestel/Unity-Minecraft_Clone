# Known Job System related bugs

This document outlines known bugs and major improvements related to the Unity Job System integration for chunk generation, meshing, and lighting.


## 01. Mesh job tracks 8 neighbor maps but `AreNeighborsReadyAndLit` only checks 4

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ScheduleMeshing` (lines 136–151), `World.cs` — `AreNeighborsReadyAndLit` (lines 1259–1304)

When scheduling a mesh job, data for **all 8 neighbors** (4 cardinal + 4 diagonal) is copied into `NativeArray`s. However, the dependency check `AreNeighborsReadyAndLit` only validates the **4 cardinal** neighbors' stability. This means a diagonal neighbor could still be running a lighting job, and the mesh job would use stale diagonal lighting data. This can cause subtle seam/lighting artifacts at chunk corners.


## 02. `ProcessGenerationJobs` always uses `biomes[0]` for flora generation

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessGenerationJobs` (line 352)

> [!CAUTION]
> **SEED BREAKING:** Fixing this changes tree height distributions in non-default biomes. Already-explored chunks are unaffected (trees are baked into saved voxel data), but **newly generated chunks** in an existing world would have different tree patterns. This creates a visible seam at the boundary between old and new terrain.

When processing tree generation mods from a completed generation job, the code uses `_world.biomes[0].minHeight` and `_world.biomes[0].maxHeight`. This hardcodes the first biome's values for ALL flora, regardless of which biome the tree position actually falls in. In a multi-biome world, trees in non-default biomes would have incorrect height ranges.

```csharp
// Current: always uses biomes[0]
IEnumerable<VoxelMod> floraMods = Structure.GenerateMajorFlora(
    mod.ID, mod.GlobalPosition,
    _world.biomes[0].minHeight,    // Should be based on actual biome
    _world.biomes[0].maxHeight);   // Should be based on actual biome
```



## 03. `ApplyLightingJobResult` creates sections without updating opaque/non-air counts correctly

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ApplyLightingJobResult` (lines 693–697)

When the lighting job result contains non-zero voxel data for a null section (meaning light exists in previously empty air), a new `ChunkSection` is created from the pool and populated. However, no `nonAirCount` or `opaqueCount` is set during this initial population — only the `RecalculateCounts` call on line 730 handles this. The issue is that `RecalculateCounts` is called inside the `else` branch of `if (!sectionHasData)`, so it **only** runs when the section is *not* being considered for removal. This is correct for existing sections, but for newly created sections that gained only light data (the voxels array would have light bits set but ID might still be 0 = air), the subsequent `RecalculateCounts` call might produce unexpected results since light-only air voxels aren't considered "non-air" by the `GetId` check.


## 04. `ProcessLightingJobs` logs every frame when any dropped updates exist

**Severity:** Improvement  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (lines 640–644)

The logging statement at line 640 fires every frame that `_droppedLightUpdates` has entries. Since `_droppedLightUpdates` is cleared and rebuilt from scratch each iteration, this effectively logs every frame when any completed lighting job had cross-chunk modifications for unloaded neighbors. In busy worlds this can flood the console and impact performance.


## 05. Generation job output disposal happens before all consumers are done

**Severity:** Improvement (potential risk)  
**Files:** `WorldJobManager.cs` — `ProcessGenerationJobs` (line 397)

The generation job data (`jobEntry.Value.Dispose()`) is called on line 397, which disposes the `NativeArray<uint>` output map. This is correct because `Populate()` (line 345) copies the data into managed arrays. However, the `Mods` queue disposal happens at the same time, which is only safe because the `TryDequeue` loop on line 349 has already fully drained the queue. If future code were to defer mod processing (e.g., apply them lazily), this dispose would cause a use-after-free crash. The dispose should ideally happen at the very end of the processing block, after all data consumption is guaranteed complete.
