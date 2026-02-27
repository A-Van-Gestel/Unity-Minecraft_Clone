# Known World Generation and Data related bugs

This document outlines known bugs and major improvements related to world generation, seed handling, and voxel data management.


## 01. Seed calculation uses `Mathf.Abs(hashCode) / 10000` hack

**Severity:** Bug  
**Files:** `VoxelData.cs` — `CalculateSeed` (lines 115–144)

> [!CAUTION]
> **SEED BREAKING:** Fixing this will change the computed seed for all worlds created with **string names** or **random seeds**. Existing save files are unaffected (they store the already-computed integer seed), but entering the same seed string into a new world would generate entirely different terrain. Only worlds created with a **raw integer seed** would remain reproducible.

The seed calculation includes a hack (`Mathf.Abs(hashCode) / 10000`) marked with a TODO: *"This is a hack to make the world generation not shit itself."* This drastically reduces the effective seed space (from ~2 billion values to ~200,000), increasing the chance of seed collisions between different world names. Additionally:

- `Mathf.Abs(int.MinValue)` overflows and returns `int.MinValue` (negative), which then produces a negative seed after dividing. This edge case can cause downstream issues.
- String seed parsing bypasses this hack (line 137 returns the parsed integer directly), meaning seeds entered as numeric strings behave differently from hashed string seeds.

The underlying generation code should be fixed to handle the full integer seed range instead of restricting it here.


## 02. `heightMap` uses `byte` which limits world height to 255

**Severity:** Improvement  
**Files:** `ChunkData.cs` — `heightMap` (line 37), `ModifyVoxel` (line 332)

The heightmap is stored as `byte[]`, limiting the tracked height to 0–255. While the current `ChunkHeight` is 128 (well within range), this would need to change if chunk height is ever increased beyond 255. The heightmap value is cast from `localPos.y` to `byte` without bounds checking, which would silently truncate values above 255.


## 03. `ModifyVoxel` heightmap scan uses `IsOpaque` instead of `IsLightObstructing`

**Severity:** Bug  
**Files:** `ChunkData.cs` — `ModifyVoxel` (lines 335–351)

> [!WARNING]
> **SAVE COMPATIBILITY:** Existing saved chunks store heightmap data computed with the buggy `IsOpaque` check. After fixing, any block removal that triggers Case 2 in an already-saved chunk would produce a corrected heightmap, potentially changing the sunlight in that column. This only manifests visually (lighting change) and does not corrupt save data — but players may notice lighting shifts in previously explored areas.

When a light-obstructing block is removed (Case 2), the downward scan to find the new highest block checks `IsOpaque` (line 343) instead of `IsLightObstructing`. These properties may diverge for blocks that are transparent but still block light (e.g., a tinted glass block). This inconsistency means the heightmap could report a wrong height, leading to incorrect sunlight calculations.

The check on line 330 (Case 1) correctly uses `IsLightObstructing`, so the mismatch with Case 2 appears to be an oversight.


## 04. `WorldData.GetLocalVoxelPositionInChunk` uses `(int)` cast instead of `FloorToInt`

**Severity:** Bug  
**Files:** `WorldData.cs` — `GetLocalVoxelPositionInChunk` (line 136)

The method casts float coordinates to `int` using `(int)`, which truncates toward zero. For negative values of `(worldPos.x - chunkCoord.x)`, the result would be off by one compared to `Mathf.FloorToInt`. While the world is currently positive-only, this creates a latent bug at world boundaries or if coordinates are ever allowed to go negative.

```csharp
// Current (truncates toward zero):
return new Vector3Int((int)(worldPos.x - chunkCoord.x), (int)worldPos.y, (int)(worldPos.z - chunkCoord.y));

// Correct (floors toward negative infinity):
return new Vector3Int(Mathf.FloorToInt(worldPos.x - chunkCoord.x), ...);
```


## 05. `RequestNeighborMeshRebuilds` uses chunk coordinates as world-space offsets

**Severity:** Bug  
**Files:** `World.cs` — `RequestNeighborMeshRebuilds` (lines 1207–1220)

This method constructs neighbor coordinates using `new Vector2Int(coord.X + 1, coord.Z)`, treating the result as a raw chunk-space offset. However, `QueueNeighborRebuild` expects a `Vector2Int` in **world position** space (e.g., multiples of `ChunkWidth`). The method passes these `ChunkCoord`-scale values, which would look up the wrong chunk data in `worldData.Chunks` (which is keyed by world-scale positions like `(16, 32)` rather than `(1, 2)`).

In contrast, `NotifyChunkModified` correctly multiplies by `VoxelData.ChunkWidth` when calling `QueueNeighborRebuild`.
