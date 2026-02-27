# Known Lighting related bugs

This document outlines known bugs related to current lighting implementation.


## 01. Ghost lighting that seems to be possible to occur at the other end of a chunk border.

**example:**
- Block sunlight at the chunk border between chunk A and B -> These parts behave the same as before the fix, but blocking the vertical tunnel to the surface one by one, gradually (mostly) fully darkens the cave
- A bright spot of around light level 5 and darkening remains on a chunk border C and A, probably because Chunk C is not properly updated, or stopped to early during the darkens propagation pass.


## 02. Light leakage on chunk corners

**example:**
- Dig a vertical tunnel in chunk A, right at the chunk corner next to chunk B and chunk C.
- Dig into chunk C (Sky needs to be accessible)
- Block sky access in both chunk C and then chunk A.
- FAILURE: The vertical tunnel is still fully lit, even though **no** Skylight (sunlight) is accessible.


## 03. Cross-chunk sunlight modification skips blocks at `currentSunlight == 15`

**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (lines 569–574)

When processing cross-chunk light modifications, the code skips any sunlight modification where the target voxel already has sunlight level 15 and the incoming value is lower. While this is meant to prevent stale job data from overwriting correct values, it can also prevent legitimate darkening operations (e.g. when a player places a block that should cast a shadow across a chunk border). The stale-data scenario should be handled by re-queueing the affected column for recalculation instead of silently dropping the update.


## 04. `ModifyVoxel` heightmap downward-scan uses `IsOpaque` instead of `IsLightObstructing`

**Files:** `ChunkData.cs` — `ModifyVoxel` (lines 335–351)

When removing a light-obstructing block (Case 2 in heightmap update), the scan to find the new highest obstructing block checks `IsOpaque` (an unrelated property based on rendering opacity) rather than `IsLightObstructing`. This can cause the heightmap to report an incorrect height for blocks that are transparent to rendering but should still block sunlight (or vice versa). Case 1 (placing a block) correctly uses `IsLightObstructing`, so this appears to be an oversight in Case 2.


## 05. Diagonal neighbors are not checked by `AreNeighborsReadyAndLit` but are used by mesh/lighting jobs

**Files:** `World.cs` — `AreNeighborsReadyAndLit` (lines 1259–1304)

`AreNeighborsReadyAndLit` only checks the 4 cardinal neighbors for lighting stability. However, both `ScheduleMeshing` and `ScheduleLightingUpdate` copy data from all 8 neighbors (including diagonals) into their job inputs. If a diagonal neighbor is still running a lighting job when the center chunk starts meshing, the diagonal's lighting data is stale, potentially causing lighting seam artifacts at chunk corners. This may be a contributing factor to Bug #02 (light leakage on chunk corners).
