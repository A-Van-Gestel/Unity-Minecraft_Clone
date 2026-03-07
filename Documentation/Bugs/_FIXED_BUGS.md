# Fixed Bugs Archive

This file consolidates all bugs that have been resolved. Entries are moved here from their respective bug files when the fix is confirmed working. Each entry is kept for historical reference and to document why the problem occurred and how it was solved.

---

## Lighting

### ~~01. Ghost lighting at chunk borders~~

**Severity:** Bug  
**Files:** `NeighborhoodLightingJob.cs`, `WorldJobManager.cs`  
**Fixed:** March 2026

**Symptom:** After blocking sunlight access through a vertical tunnel, a bright spot of ~light level 5 persisted on chunk borders and did not fully darken, even after the sky was completely sealed.

**Root Cause:** A cross-chunk read-after-write hazard in `NeighborhoodLightingJob`. `PropagateDarkness` could not modify neighbor chunk data (marked `[ReadOnly]`), so `PropagateLight` read stale data and re-spread light, undoing the darkness propagation.

**Fix:** Implemented a write-through cache (`NativeHashMap<long, uint>`) inside `Execute()`. `SetLight` writes to the cache for neighbor positions; `GetPackedData` checks the cache before the ReadOnly arrays. This ensures all light changes within a single job execution are immediately visible to subsequent reads.

---

### ~~02. Light leakage on chunk corners~~

**Severity:** Bug  
**Files:** `World.cs` — `AreNeighborsReadyAndLit`, `VoxelData.cs`  
**Fixed:** March 2026

**Symptom:** Digging and then sealing a vertical tunnel at a chunk corner left the tunnel fully lit even after blocking all skylight access.

**Root Cause:** `AreNeighborsReadyAndLit` only checked the 4 cardinal neighbors. Mesh and lighting jobs copy data from all 8 neighbors (including diagonals), so a diagonal neighbor running a lighting job could provide stale data.

**Fix:** Extended `AreNeighborsReadyAndLit` to also iterate `VoxelData.DiagonalNeighborOffsets` and apply the same stability checks (no active generation/lighting jobs, no pending light changes) to all 4 diagonal neighbors.

---

### ~~03. Cross-chunk sunlight modification skips blocks at `currentSunlight == 15`~~

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (lines 569–574)  
**Fixed:** March 2026

**Symptom:** Placing a block that should cast a shadow across a chunk border sometimes had no effect on the neighbor's sunlight.

**Root Cause:** The code skipped any cross-chunk sunlight modification where the target voxel already had sunlight level 15 and the incoming value was lower — intended to ignore stale data, but also silenced legitimate darkening operations.

**Fix:** Removed the unconditional skip. The stale-data scenario is now handled by proper job sequencing and the write-through cache.

---

### ~~04. `ModifyVoxel` heightmap downward-scan uses `IsOpaque` instead of `IsLightObstructing`~~

**Severity:** Bug  
**Files:** `ChunkData.cs` — `ModifyVoxel` (lines 335–351)  
**Fixed:** March 2026

**Symptom:** Removing a block that was transparent to rendering but opaque to light produced an incorrect new heightmap value, corrupting sunlight in that column.

**Root Cause:** Case 2 of the heightmap update (block removal) scanned downward checking `IsOpaque` while Case 1 (block placement) correctly used `IsLightObstructing`. A block transparent to rendering but blocking to light would be missed by the scan.

**Fix:** Changed the Case 2 downward scan in `ModifyVoxel` to use `IsLightObstructing`, matching Case 1.

---

### ~~05. Diagonal neighbors not checked by `AreNeighborsReadyAndLit`~~

**Severity:** Bug  
**Files:** `World.cs` — `AreNeighborsReadyAndLit`, `VoxelData.cs`  
**Fixed:** March 2026

**Note:** Resolved as part of the fix for Light Leakage #02 above. `VoxelData.DiagonalNeighborOffsets` was added and the check was extended.

---

## Fluid

### ~~01. Cross-chunk fluid simulation stops at chunk borders~~

**Severity:** Bug  
**Files:** `Chunk.cs` — `Reset`, `World.cs` — `ApplyModifications`  
**Fixed:** March 2026

**Symptom:** Water would flow one block into a neighboring chunk and then stop, leaving a dead-end fluid tile that did not continue spreading.

**Root Cause:** During the initial world load (`StartWorld`), `ChunkData` for a chunk could be fully populated before the corresponding `Chunk` GameObject was retrieved from the pool. `Chunk.Reset()` did not call `OnDataPopulated()` when the data was already populated, so active voxels (e.g., water source blocks) placed during generation were never registered in `_activeVoxels`, and fluid simulation never started for them.

**Fix:**
- Added a check in `Chunk.Reset()`: if `ChunkData.IsPopulated` is already true when the chunk is reset, `OnDataPopulated()` is called immediately.
- The noisy `[FLUID DEBUG]` warning in `World.ApplyModifications()` is now gated on `_isWorldLoaded` to suppress expected noise during the initial load phase.

---

### ~~02. Downward flow creates infinite source blocks (waterfalls don't drain)~~

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`, `Active`; `FluidDataGenerator.cs`; `FluidMeshData.cs`  
**Fixed:** March 2026

**Symptom:** Placing a single water source block at height Y and then removing it left the entire waterfall column permanently, because every block in the column had become an independent infinite source. Additionally, even after an initial fix (FluidLevel=1), falling water spread ~60 blocks on landing because level 1 still allowed 6 levels of horizontal spread.

**Root Cause:** `HandleFluidFlow` Rule 1 created downward flow without properly encoding the upstream level. In Minecraft Beta 1.3.2, falling fluid uses metadata `>= 8` (the "falling flag"), with the lower 3 bits carrying the effective upstream level. When a falling block lands, horizontal spread starts from `effectiveLevel + 1`, not from level 1.

**Fix (Minecraft-style falling metadata):**

1. **Falling flag encoding:** Added `FALLING_FLAG = 8`, `IsFalling()`, `GetEffectiveLevel()`, `MakeFalling()` helpers to `BlockBehavior.cs`.
2. **Step A (Gravity):** Downward flow now places `MakeFalling(effectiveLevel)` — e.g., a source (level 0) falling becomes FluidLevel 8, a level-2 flow falling becomes FluidLevel 10.
3. **Step B (Settle):** A falling block that lands on solid ground converts to its non-falling effective level before spreading horizontally.
4. **Step C (Horizontal spread):** Spread starts from `effectiveLevel + 1`, not from the raw FluidLevel.
5. **`Active()` updated** with 6 reasons including falling-aware drain checks.
6. **`FluidDataGenerator.cs`:** Template indices 8–15 now generate as `1.0f` (full block height for falling columns). Added validation that `flowLevels <= 8`.

---

## Chunk Management

### ~~02. `ChunkCoord.GetHashCode()` has high collision potential~~

**Severity:** Improvement  
**Files:** `ChunkCoord.cs` — `GetHashCode`  
**Fixed:** Early 2026

**Root Cause:** The hash function `31 * X + 17 * Z` produces a very small hash space and has poor distribution for typical chunk coordinate ranges.

**Fix:** Replaced with `HashCode.Combine(X, Z)`.

---

### ~~03. `Tick()` iterates `_activeChunks` which can be modified concurrently~~

**Severity:** Bug  
**Files:** `World.cs` — `Tick`  
**Fixed:** Early 2026

**Root Cause:** The `Tick` coroutine used a `foreach` over `_activeChunks`, which could be modified by `CheckViewDistance` between `WaitForSeconds` yields, causing `InvalidOperationException`.

**Fix:** Changed the tick loop to iterate over a snapshot copy of the active chunks set.

---

### ~~06. `PrepareJobData` is called for soon-to-be-destroyed World instances~~

**Severity:** Bug  
**Files:** `World.cs` — `Awake`  
**Fixed:** Early 2026

**Root Cause:** `PrepareJobData()` was called outside the Singleton else-branch and ran for both the surviving and the duplicate (about to be destroyed) World instances, leaking NativeArray memory.

**Fix:** Moved `PrepareJobData()` inside the else-branch so it only runs for the surviving instance.

---

### ~~07. Unreachable diagnostic check in `UnloadChunks`~~

**Severity:** Code Quality  
**Files:** `World.cs` — `UnloadChunks`  
**Fixed:** Early 2026

**Root Cause:** Dead code — the same `IsAwaitingMainThreadProcess` condition was already checked above and caused a `continue`, making the second check unreachable.

**Fix:** Removed the unreachable block.

---

## Player

### ~~03. Player falls through the world during loading~~

**Severity:** Bug  
**Files:** `Player.cs` — `FixedUpdate`, `World.cs`  
**Fixed:** March 2026

**Symptom:** On some loads, the player would briefly fall through terrain before chunks finished meshing.

**Root Cause:** `FixedUpdate` applied gravity and movement before `StartWorld` finished loading and meshing the spawn chunks.

**Fix:**
- Exposed `_isWorldLoaded` as `public bool IsWorldLoaded => _isWorldLoaded` on `World`.
- `FixedUpdate` in `Player.cs` now returns immediately if `!_world.IsWorldLoaded`.

---

### ~~04. `GameObject.Find("Main Camera")` is fragile~~

**Severity:** Improvement  
**Files:** `Player.cs` — `Start`, `LoadSaveData`  
**Fixed:** March 2026

**Root Cause:** Camera was resolved by string name — renaming the GameObject would silently break the player controller.

**Fix:** Replaced both occurrences with `Camera.main?.transform`.

---

### ~~05. `Application.Quit()` on Escape is not editor-safe~~

**Severity:** Improvement  
**Files:** `Player.cs` — `GetPlayerInputs`  
**Fixed:** March 2026

**Root Cause:** `Application.Quit()` is a no-op in the Unity Editor, which meant pressing Escape during editor play-mode did nothing.

**Fix:** Wrapped with `#if UNITY_EDITOR` / `#else`: the editor path calls `UnityEditor.EditorApplication.isPlaying = false`, while the build path calls `Application.Quit()`.

---

## World Generation & Data

### ~~03. `ModifyVoxel` heightmap scan uses `IsOpaque` instead of `IsLightObstructing`~~

**Severity:** Bug  
**Files:** `ChunkData.cs` — `ModifyVoxel`  
**Fixed:** March 2026

**Note:** Same fix as Lighting #04 above. The inconsistency was in `ChunkData.ModifyVoxel`'s Case 2 downward scan.

---

### ~~05. `RequestNeighborMeshRebuilds` used chunk coordinates as world-space offsets~~

**Severity:** Bug  
**Files:** `World.cs` — `RequestNeighborMeshRebuilds`, `QueueNeighborRebuild`  
**Fixed:** During `ChunkCoord` refactoring (before March 2026)

**Resolution:** Resolved during the `ChunkCoord` refactoring. `RequestNeighborMeshRebuilds` now uses `chunkCoord.Neighbor(dx, dz)`, and `QueueNeighborRebuild` calls `.ToVoxelOrigin()` internally.

---

## Block Behavior

### ~~04. Fluid `FluidLevel` set redundantly in `HandleFluidFlow`~~

**Severity:** Code Quality  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`  
**Fixed:** March 2026

**Root Cause:** `FluidLevel` was set once inside the object initializer and a second time directly on the variable immediately after — harmless but clearly a copy-paste oversight.

**Fix:** Removed the redundant second assignment.

---
