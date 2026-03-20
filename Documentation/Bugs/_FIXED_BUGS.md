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

**Fix:** Implemented a write-through cache (`NativeHashMap<long, uint>`) inside `Execute()`. `SetLight` writes to the cache for neighbor positions; `GetPackedData` checks the cache before the ReadOnly arrays. This ensures all light changes within a single job execution are
immediately visible to subsequent reads.

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

### ~~06. `ApplyLightingJobResult` creates sections without updating opaque/non-air counts correctly~~

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ApplyLightingJobResult`  
**Fixed:** March 2026

**Symptom:** Internal chunk section voxel counts get desynchronized because light-only air blocks were being counted as solid.
**Root Cause:** When the lighting job result contains non-zero voxel data for a null section (light in previously empty air), a new `ChunkSection` is created and populated. `RecalculateCounts` counted these air voxels as non-air because `data != 0`.
**Fix:**

1. `RecalculateCounts` and `RecalculateNonAirCount` now check for solidity using `(data & ID_MASK) != 0` to properly ignore light-only air voxels.
2. Added an `isNewSection` flag to `ApplyLightingJobResult` to completely skip `RecalculateCounts` for freshly created light-only sections, as their pool-reset counts `(0, 0)` are already correct.

---

### ~~07. `ProcessLightingJobs` logs every frame when any dropped updates exist~~

**Severity:** Improvement  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs`  
**Fixed:** March 2026

**Symptom:** Performance hitching and extreme console log spam.
**Root Cause:** The logging statement fired every frame that `_droppedLightUpdates` had entries. Since the collection is rebuilt each iteration, in busy worlds this effectively logged every frame.
**Fix:** Gated the log behind `enableDiagnosticLogs`, replaced LINQ `.Sum()` with a manual loop to prevent allocations, and removed the unused `using System.Linq` directive.

---

## Fluid

### ~~01. Cross-chunk fluid simulation stops at chunk borders~~

**Severity:** Bug  
**Files:** `Chunk.cs` — `Reset`, `World.cs` — `ApplyModifications`  
**Fixed:** March 2026

**Symptom:** Water would flow one block into a neighboring chunk and then stop, leaving a dead-end fluid tile that did not continue spreading.

**Root Cause:** During the initial world load (`StartWorld`), `ChunkData` for a chunk could be fully populated before the corresponding `Chunk` GameObject was retrieved from the pool. `Chunk.Reset()` did not call `OnDataPopulated()` when the data was already populated, so active
voxels (e.g., water source blocks) placed during generation were never registered in `_activeVoxels`, and fluid simulation never started for them.

**Fix:**

- Added a check in `Chunk.Reset()`: if `ChunkData.IsPopulated` is already true when the chunk is reset, `OnDataPopulated()` is called immediately.
- The noisy `[FLUID DEBUG]` warning in `World.ApplyModifications()` is now gated on `_isWorldLoaded` to suppress expected noise during the initial load phase.

---

### ~~02. Downward flow creates infinite source blocks (waterfalls don't drain)~~

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`, `Active`; `FluidDataGenerator.cs`; `FluidMeshData.cs`  
**Fixed:** March 2026

**Symptom:** Placing a single water source block at height Y and then removing it left the entire waterfall column permanently, because every block in the column had become an independent infinite source. Additionally, even after an initial fix (FluidLevel=1), falling water
spread ~60 blocks on landing because level 1 still allowed 6 levels of horizontal spread.

**Root Cause:** `HandleFluidFlow` Rule 1 created downward flow without properly encoding the upstream level. In Minecraft Beta 1.3.2, falling fluid uses metadata `>= 8` (the "falling flag"), with the lower 3 bits carrying the effective upstream level. When a falling block lands,
horizontal spread starts from `effectiveLevel + 1`, not from level 1.

**Fix (Minecraft-style falling metadata):**

1. **Falling flag encoding:** Added `FALLING_FLAG = 8`, `IsFalling()`, `GetEffectiveLevel()`, `MakeFalling()` helpers to `BlockBehavior.cs`.
2. **Step A (Gravity):** Downward flow now places `MakeFalling(effectiveLevel)` — e.g., a source (level 0) falling becomes FluidLevel 8, a level-2 flow falling becomes FluidLevel 10.
3. **Step B (Settle):** A falling block that lands on solid ground converts to its non-falling effective level before spreading horizontally.
4. **Step C (Horizontal spread):** Spread starts from `effectiveLevel + 1`, not from the raw FluidLevel.
5. **`Active()` updated** with 6 reasons including falling-aware drain checks.
6. **`FluidDataGenerator.cs`:** Template indices 8–15 now generate as `1.0f` (full block height for falling columns). Added validation that `flowLevels <= 8`.

---

### ~~03. Side face rendering between different fluid levels (Internal Water Grids)~~

**Severity:** Visual Artifact / Performance  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs` — fluid face cull logic)  
**Fixed:** March 2026

**Symptom:** Side faces between fluid voxels of different fluid levels (or fully submerged voxels) were incorrectly rendering full geometric quads from `y=0` to the surface height, causing massive internal overdraw and visible overlapping grid seams inside bodies of water.  
**Root Cause:** The mesher conservatively rendered faces between differing fluid levels to prevent gaps near waterfalls, and failed to account for fully submerged neighbor blocks sharing a `1.0f` ceiling.  
**Fix / Implementation:** We eliminated 100% of internal horizontal fluid faces and perfectly sealed vertical gaps using a three-part logic upgrade:

1. **Mathematical Symmetry:** Exploited `GetSmoothedCornerHeight` to pre-calculate the exact surface heights of shared neighbor edges.
   If a fluid block is adjacent to another, it now draws a "curtain" face extending *only* from the neighbor's sloped surface height up to our surface height, erasing geometry that would have plunged internally into the neighbor's volume.
2. **Submerged Culling:** Expanded the Burst neighbor lookup to include `above_N, above_S, etc.`. If both the current block and the neighbor are submerged (`neighborHasFluidAbove`),
   their top edges mathematically align at `1.0f`. There is zero gap to fill, so the face is completely culled.
3. **Waterfall Preservation:** Added an `isFullHeight` fallback. Waterfalls erupting from under solid stone ceilings (`hasFluidAbove == false` but `template == 1.0f`) now correctly recognize their full height,
   overriding the shallow puddle cull to draw a perfect gap-filler face down to the surface below.

---

### ~~05. 7x7 Horizontal Spreading Cube in Mid-Air~~

**Severity:** Gameplay / Physics bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** When a source block is placed on top of an elevated surface (like a tree), the fluid flows outwards and sometimes incorrectly spawns horizontal spreading blocks in mid-air (forming a floating 7x7 water grid) instead of accurately checking if those spread locations
have ground support below them.

**Root Cause:** The `isSupportedBelow` check during horizontal spreading incorrectly allowed fluid to spread if the block below was the *same fluid type*, regardless of whether it was falling or solid. Additionally, falling waterfall columns and pulsating decay loops contributed
to broken spread distances.

**Fix:**

1. Aligned horizontal spread gating with Minecraft logic (fluids only spread horizontally if supported by a solid block, preventing mid-air grids).
2. Resolved infinite decay loops by treating falling blocks as level 0 when calculating expected support levels.
3. Added a `waterfallsMaxSpread` configuration toggle in `BlockType.cs` to let the user select between Minecraft parity (infinite waterfall spread) and physics-based volume conservation.

---

### ~~07. Missing Source Block Regeneration (Infinite Water)~~

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`  
**Fixed:** March 2026

**Symptom:** In Minecraft Beta 1.3.2, two or more adjacent water source blocks resting on solid ground spontaneously regenerate an empty air block between them into a new source block. This core "infinite water" mechanic was missing from the engine.

**Root Cause:** The `BlockBehavior.Fluids.cs` decay pass successfully gathered environmental constraints, but had no logic to construct new infinite sources out of thin air, meaning water was strictly finite.

**Fix:**

1. Added an `infiniteSourceRegeneration = false` flag to `BlockType.cs` to allow toggling this for specific fluids (e.g., Water = true, Lava = false).
2. Integrated the horizontal source-counting logic directly into `CalculateExpectedFluidLevel` when looking for cross-chunk neighbors.
3. If `>= 2` native source blocks (level 0) are horizontally adjacent, and the block below is either solid ground or another level 0 source block,
   the block organically overrides its target decay to `0`, successfully generating an infinite source pool without extra overhead or allocations.

---

### ~~08. Missing Lava Viscosity Randomization~~

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`  
**Fixed:** March 2026

**Symptom:** Lava spread horizontally at the exact same deterministic rate as water, lacking its thick, random flow pattern.

**Root Cause:** Fluid flow evaluation always proceeded at 100% chance for all fluids.

**Fix:** Added a `spreadChance` configuration `float` (0.0 - 1.0) to `BlockType.cs` and exposed it in the `BlockEditorWindow`. Inside `HandleFluidSpread`, `UnityEngine.Random.value` is rolled against this chance; if it fails, the horizontal spread step is cleanly aborted for that
tick.

---

### ~~10. Missing Dynamic Flow Direction Texturing~~

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`  
**Fixed:** March 2026

**Symptom:** Water did not visually animate in the direction it was physically spreading, relying on a static world-space liquid shader.
**Fix / Implementation:** Implemented flow derivative math in `VoxelMeshHelper.cs` (`CalculateCornerFlow` and `ProjectFlowToSideFace`).
This calculates a 2D XZ flow vector based on surrounding fluid height differentials and maps it to the UV channels of the generated mesh.
The `UberLiquidShader` receives this vector (`localFlowVector`) and uses a dual-phase crossfading technique to scroll the noise and simulate continuous directional flow.
*(Note: As tracked in FLUID_BUGS.md #16, this is currently functionally operational but the math could be further refined in the future to eliminate surface stretching.)*

---

### ~~11. Missing Unique Textures for Falling Fluids (Waterfalls)~~

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`  
**Fixed:** March 2026

**Symptom:** Falling fluid blocks (waterfalls) used the exact same visual properties as resting horizontal fluids.
**Fix / Implementation:** In `VoxelMeshHelper.cs`, the mesh generation job now explicitly checks if the voxel has the falling flag (`fluidLevel >= 8`).
If true, instead of calculating complex horizontal flow math, it bypasses the evaluation and forces a strict, high-velocity downward flow vector (`new Vector2(0f, 1.5f)`).
The `UberLiquidShader` natively interprets this large V-axis vector, resulting in a fast-scrolling, distinct vertical waterfall effect that properly blends with horizontal pools at its base.

---

## Chunk Management

### ~~01. `ChunkCoord` integer division truncates negative coordinates incorrectly~~

**Severity:** Bug (latent)  
**Files:** `Chunk.cs` — `ChunkCoord`
**Fixed:** March 2026

**Root Cause:** `FromVoxelOrigin` and `FromWorldPosition` used raw integer division (`pos.x / VoxelData.ChunkWidth`) without `Mathf.FloorToInt`. In C#, integer division truncates toward zero instead of stepping down to the correct negative grid coordinate.

**Fix:** Replaced the integer division in the `Vector2Int` and `Vector3Int` constructors with floating-point division coupled with `Mathf.FloorToInt`.

---

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

### ~~04. `PrepareJobData` is called for soon-to-be-destroyed World instances~~

**Severity:** Bug  
**Files:** `World.cs` — `Awake`  
**Fixed:** Early 2026

**Root Cause:** `PrepareJobData()` was called outside the Singleton else-branch and ran for both the surviving and the duplicate (about to be destroyed) World instances, leaking NativeArray memory.

**Fix:** Moved `PrepareJobData()` inside the else-branch so it only runs for the surviving instance.

---

### ~~05. Unreachable diagnostic check in `UnloadChunks`~~

**Severity:** Code Quality  
**Files:** `World.cs` — `UnloadChunks`  
**Fixed:** Early 2026

**Root Cause:** Dead code — the same `IsAwaitingMainThreadProcess` condition was already checked above and caused a `continue`, making the second check unreachable.

**Fix:** Removed the unreachable block.

---

### ~~06. Chunk load animation adds `GetComponent` on every activation and loops on modify~~

**Severity:** Improvement (performance) & Bug
**Files:** `Chunk.cs` — `PlayChunkLoadAnimation`, `ChunkLoadAnimation.cs`
**Fixed:** March 2026

**Root Cause:** `GetComponent<ChunkLoadAnimation>()` was called on each pool activation. A primitive boolean flag was previously attempted, but it could become stale if the component were destroyed. Furthermore, applying animations from the pool caused a 1-frame visual flash, and
subsequent chunk modifications re-triggered the upward animation natively since it hooked into mesh generation.

**Fix:**

- Used a cached `_loadAnimation` reference inside `Chunk.cs` utilizing Unity's overriden `== null` safety check rather than a primitive boolean flag.
- Refactored `ChunkLoadAnimation` to take an absolute target position (`ResetToUnderground`) to prevent infinite vertical offsets.
- Added a `_hasPlayedLoadAnimation` flag to `Chunk.cs` to guarantee animations don't re-trigger when local voxel modifications request a mesh rebuild.

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

### ~~04. `heightMap` uses `byte` which limits world height to 255~~

**Severity:** Improvement  
**Files:** `ChunkData.cs` — `heightMap`, `ModifyVoxel`; `WorldJobManager.cs`  
**Fixed:** March 2026

**Symptom:** The heightmap was stored as `byte[]`, limiting tracked height to 0–255. If chunk height were ever increased beyond 255, heights would silently truncate.

**Fix:** Changed `heightMap` to `ushort[]` (0–65535) across the generation and lighting pipeline (`ChunkData`, `GenerationJobData`, `LightingJobData`). Added region serialization backwards compatibility to upgrade V1 chunks (`byte[]`) to V2 chunks (`ushort[]`).

---

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

### ~~06. `WorldData.GetLocalVoxelPositionInChunk` uses `(int)` cast instead of `FloorToInt`~~

**Severity:** Bug (latent)  
**Files:** `WorldData.cs` — `GetLocalVoxelPositionInChunk`
**Fixed:** March 2026

**Symptom:** Yielded incorrect chunk boundary voxel references for negative world coordinates.
**Root Cause:** The method used an `(int)` cast which truncates toward zero, producing wrong results for negative coordinates.
**Fix:** Both `x` and `z` components now use `Mathf.FloorToInt()`. The `y` component retains the `(int)` cast since Y is always non-negative.

---

## Block Behavior

### ~~04. Fluid `FluidLevel` set redundantly in `HandleFluidFlow`~~

**Severity:** Code Quality  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`  
**Fixed:** March 2026

**Root Cause:** `FluidLevel` was set once inside the object initializer and a second time directly on the variable immediately after — harmless but clearly a copy-paste oversight.

**Fix:** Removed the redundant second assignment.

---

### ~~05. Hardcoded Block IDs throughout the codebase~~

**Severity:** Improvement  
**Files:** `BlockBehavior.cs`, `BlockBehavior.Grass.cs`, `BlockBehavior.Fluids.cs`, `Structure.cs`, `MeshGenerationJob.cs`, `World.cs`, `PlayerInteraction.cs`, `LightingJobBenchmark.cs`  
**Fixed:** March 2026

**Symptom:** Block IDs like Grass (`2`), Dirt (`3`), Air (`0`), Stone (`1`), Oak Log (`14`), Oak Leaves (`15`), Cactus (`16`), and Lava (`20`) were hardcoded as magic numbers across 8 files.  
**Impact:** Any change to the `BlockDatabase` array order would silently break all logic referencing these IDs.  
**Fix:**

1. Created `Assets/Editor/BlockIdGenerator.cs` — an Editor tool (`Minecraft Clone > Generate Block IDs`) that reads the `BlockDatabase` and auto-generates `Assets/Scripts/Data/BlockIDs.cs` containing `public const ushort` fields for every block type.
2. Replaced all 26 hardcoded block ID instances across 8 files with `BlockIDs.*` constants.
3. Added a `BlockDatabaseChangeDetector` asset postprocessor that warns when the database changes and the generated file may be stale.
4. Generator enforces the **Air = 0 architectural invariant** with a hard-assert, refusing to write the file if `blockTypes[0]` is not "Air".

**Note:** This resolves magic numbers in code but changing the `BlockDatabase` order still breaks save files. The long-term fix is the Chunk Palette Mapping system (see `Documentation/Design/CHUNK_PALETTE_MAPPING.md`).

---

### ~~01. `BlockBehavior.s_mods` is a shared static list (thread safety / reentrancy hazard)~~

**Severity:** Bug (latent)  
**Files:** `BlockBehavior.cs`  
**Fixed:** March 2026

**Symptom:** Race condition if block behavior is evaluated across multiple threads, leading to data corruption as `Behave()` overwrites the returned reusable metadata list.
**Root Cause:** The `Behave` method used a single shared static `List<VoxelMod>` (`s_mods`) that was cleared and reused on every call.
**Fix:** Replaced the single static list with a `[ThreadStatic]` lazily instantiated backing list mapped to a `Mods` property. This guarantees every thread hitting `Behave()` gets its own private, zero-allocation reusable list.

---

## Player & Input

### ~~01. Collision only checks at two height levels (feet and +1)~~

**Severity:** Bug  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`
**Fixed:** March 2026

**Root Cause:** The horizontal collision properties only checked at two Y levels.
**Fix:** Encapsulated player physics into `VoxelRigidbody` and implemented dynamic AABB sweeping across the entity's full `entityHeight`.

---

### ~~02. Collision checks don't account for player width in the cross-axis~~

**Severity:** Bug  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`
**Fixed:** March 2026

**Root Cause:** Each directional collision check sampled a single line, causing clipping on diagonal movement.
**Fix:** `VoxelRigidbody` now sweeps an axis-aligned bounding box face dynamically.

---

### ~~03. Raycast-based block placement can be incorrect on exact voxel edges~~

**Severity:** Bug (latent)  
**Files:** `PlayerInteraction.cs` — `RaycastForVoxel`
**Fixed:** March 2026

**Root Cause:** The block placement used modulo `% 1` which failed on negative coordinates.
**Fix:** Rewrote `RaycastForVoxel` to use a robust previous-step integer coordinate tracking method.

---

### ~~04. Block placement overlap check only covers 2 voxels of player height~~

**Severity:** Bug  
**Files:** `PlayerInteraction.cs` — `PlaceCursorBlocks`
**Fixed:** March 2026

**Root Cause:** The placement validity check only checked `y` and `y+1`, ignoring actual `playerHeight`.
**Fix:** Replaced hardcoded checks with a dynamic AABB intersection check against the placed voxel's bounds.

---

### ~~05. `collisionPadding` shrinks AABB incorrectly causing erratic collision behavior~~

**Severity:** Bug  
**Files:** `Physics/VoxelRigidbody.cs`  
**Fixed:** March 2026

**Symptom:** Setting `collisionPadding` to non-zero values caused erratic collision behavior (e.g., random teleports if negative, or not affecting actual collision distance correctly if positive). Corners of voxels intersected the collision bounding box.  
**Root Cause:** Bounding box continuous widths and radius calculations were loosely defined and sometimes conflated, leading to scalar errors during AABB sweep tests.  
**Fix:** Introduced unified `CollisionHalfWidthX` and `CollisionHalfDepthZ` properties as the single source of truth, removing `* 0.5f` redundant calculations and properly incorporating `collisionPadding`.

---

### ~~06. Block placement disabled too early due to overly strict player AABB intersection~~

**Severity:** Bug  
**Files:** `PlayerInteraction.cs`  
**Fixed:** March 2026

**Symptom:** The player could not replace a broken block if they were standing close to the wall (e.g., 1/3 of a block away) because the placement check overlapped with the player's collision bounds too safely.  
**Root Cause:** `PlayerInteraction` used `VoxelRigidbody.collisionWidthX` and depth without accurately accounting for padding or using consistent extents.  
**Fix:** Refactored the overlap calculation in `PlayerInteraction.cs` to accurately use the new `CollisionHalfWidthX` and `CollisionHalfDepthZ` properties.

---

### ~~07. Hardcoded collision epsilon `0.001f` causing readability issues~~

**Severity:** Code Quality  
**Files:** `Physics/VoxelRigidbody.cs`  
**Fixed:** March 2026

**Root Cause:** The collision snapping logic used hardcoded `0.001f` magic numbers across properties.  
**Fix:** Centralized with a `COLLISION_EPSILON` constant.

---

## Architecture & Configuration

### ~~01. Settings logic is duplicated and tightly coupled to World.cs~~

**Severity:** Code Quality  
**Files:** `World.cs`, `SettingsManager.cs`, `TitleMenu.cs`, `WorldSelectMenu.cs`  
**Fixed:** March 2026

**Symptom:** JSON Settings creation and loading logic was duplicated across multiple scripts.  
**Root Cause:** `Settings` was previously nested inside `World.cs` and lacked a dedicated serialization controller.  
**Fix:** Extracted `Settings` into a standalone POCO and created a centralized `SettingsManager` that handles loading, saving, and Editor-specific DB refresh functionality. The duplicated load logic was removed from all caller scripts.

---

## Fluid Simulation

### ~~05. 7x7 Horizontal Spreading Cube in Mid-Air~~

**Severity:** Bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Pouring water off a cliff caused it to spread out horizontally into a 7x7 mid-air platform.
**Root Cause:** Horizontal spread allowed fluid blocks to freely expand if the block below them was empty air, rather than matching Minecraft's gating logic which requires soft/fluid support to be solid.
**Fix:** Added a conditional gate enforcing that non-source blocks can only spread if the block directly below them is solid, forcing waterfalls to drop straight down.

---

### ~~06. Missing Optimal Flow Direction Pathfinding~~

**Severity:** Missing Feature  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Water spread outward in a uniform diamond shape, oblivious to nearby holes or drops.
**Root Cause:** The simulation lacked Minecraft's recursive `calculateFlowCost` terrain scanner.
**Fix:** Injected a dot-net 2.1 zero-allocation Breadth-First-Search iterative pathfinder using Unity's `NativeQueue` and bitmasks to determine the optimal downhill path.

---

### ~~07. Severed waterfalls cause infinite decay loops~~

**Severity:** Bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Breaking a source block with a waterfall beneath it left floating, non-decaying waterfall columns that indefinitely supplied water to adjacent blocks.
**Root Cause:** `CalculateExpectedFluidLevel` allowed orphaned waterfall blocks to act as level-0 support for each other, establishing a self-sustaining loop.
**Fix:** Added an `isFedFromAbove` check. Now, if a falling fluid block is cut off from the stream above, it immediately decays to air or regular decaying fluid, ending the loop.

---
