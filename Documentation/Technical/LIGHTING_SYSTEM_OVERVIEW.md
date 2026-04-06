# Lighting System Overview

This document provides a complete technical reference for the voxel lighting engine. The system is asynchronous, multi-threaded (via Unity's C# Job System and Burst), and handles two distinct light channels: **Sunlight** and **Blocklight**. The design is heavily inspired by the **Starlight** lighting engine (now **Moonrise**), a high-performance replacement for Minecraft's vanilla lighting. Where our implementation diverges from Starlight, this document explains why.

> **Reference implementation:** `_REFERENCES/Moonrise/` contains the full Moonrise source. Key files are in `.../patches/starlight/light/`.

---

## 1. Core Concepts

### 1.1 Data Representation

Light is not stored as a separate component. It is packed directly into each voxel's `uint` data (see `DATA_STRUCTURES.md` for the full bit layout):

| Field | Bits | Range | Purpose |
|-------|------|-------|---------|
| Sunlight | 16-19 | 0-15 | Light from the sky |
| Blocklight | 20-23 | 0-15 | Light emitted by torches, lava, etc. |

**Final light value:** For rendering, the shader uses `max(sunlight, blocklight)`. 15 is full brightness; 0 is darkness (clamped to `MinLightLevel` = 0.15 for ambient).

### 1.2 Block Light Properties

Each `BlockType` defines how it interacts with the lighting system:

| Property | Type | Meaning |
|----------|------|---------|
| `opacity` | byte (0-15) | How much light is reduced when passing *through* this block. 0 = fully transparent, 15 = fully opaque. |
| `lightEmission` | byte (0-15) | How much light this block emits. 0 = none, 15 = brightest (e.g., lava). |

**Derived properties** (computed from opacity):
- `IsOpaque`: `opacity >= 15` — blocks all light completely.
- `IsFullyTransparentToLight`: `opacity == 0` — light passes through unchanged (air, glass).
- `IsLightObstructing`: `opacity > 0` — has some attenuation (used for heightmap).

### 1.3 The Algorithm: Dual-Phase BFS Flood-Fill

The core of the system is a Breadth-First Search (BFS) flood-fill. When a voxel's light changes, the update propagates through neighbors in two sequential phases:

**Phase 1 — Darkness Removal (`PropagateDarkness`):**
1. Dequeue a removal node (position + old light level).
2. For each of 6 neighbors: if the neighbor has light > 0 and light < the removal node's old level, set the neighbor's light to 0 and enqueue it for further removal.
3. If the neighbor's light >= the removal node's old level, it has light from a *different* source. Enqueue it for re-spreading in Phase 2.

**Phase 2 — Light Spreading (`PropagateLight`):**
1. Dequeue a position, read its current light level.
2. For each of 6 neighbors: calculate `targetLevel = sourceLight - 1 - neighborOpacity`.
3. If `targetLevel > neighborCurrentLight`, update the neighbor and enqueue it for further spreading.
4. **Special rule:** Sunlight at level 15 traveling straight *down* through fully transparent blocks does not attenuate (remains 15). This creates the fast vertical sunlight columns.
5. **Opaque surface lighting:** Opaque blocks receive light on their surface (`sourceLight - 1`) but are never enqueued for further propagation. Light stops at opaque surfaces.

**Ordering is critical:** All darkness removal completes before any light spreading begins. This ensures the "clean slate" from removal is fully established before new light fills in, preventing ghost light artifacts.

**BFS boundary rule (critical):** The BFS must **never enqueue positions outside the center chunk** for further propagation. Both `PropagateDarkness` and `PropagateLight` use an `IsInCenterChunk(neighborPos)` guard before enqueueing. Neighbor voxels still have their light **written** via `SetLight` (which routes to `CrossChunkLightMods`), but the BFS does not continue through them. Without this guard, the BFS can exit the center chunk, travel through a neighbor's data (which may be all-zeros for unloaded chunks, appearing as a void of air), and re-enter the center chunk underground — creating vertical walls of light leaking through solid terrain at chunk borders. The neighbor's own lighting job handles propagation within its borders after the cross-chunk modifications are applied by the main thread.

---

## 2. Sunlight vs. Blocklight

### 2.1 Blocklight

Simple: originates from blocks with `lightEmission > 0` and propagates outward in all 6 directions using the standard BFS. Light decreases by `1 + opacity` per step.

### 2.2 Sunlight

More complex, with special optimizations:

**Source:** The sky above the chunk (conceptually Y = ChunkHeight), with a starting value of 15.

**Heightmap:** Each chunk maintains a `heightMap[256]` (16x16 columns) storing the Y-level of the highest light-obstructing block per column. This enables fast sunlight initialization.

**Column Recalculation (`RecalculateSunlightForColumn`):**
1. **Above the heightmap:** All voxels above the highest opaque block are set to sunlight 15.
2. **Horizontal shadow casting:** If the highest block is opaque, check its 4 horizontal neighbors for partial sunlight (1-14). If found, set them to 0 and enqueue for darkness removal to clear stale scattered light.
3. **Below the heightmap:** Propagate downward from the highest block with opacity-based attenuation.

After column recalculation, the standard BFS phases handle horizontal spreading.

---

## 3. The Asynchronous Update Loop

### 3.1 Block Modification → Light Queues

When a player places or breaks a block (`ChunkData.ModifyVoxel`):
1. The old sunlight and blocklight values are captured.
2. The heightmap is updated if the block is light-obstructing.
3. The modified voxel and its 6 neighbors are added to `_sunlightBfsQueue` and `_blocklightBfsQueue`.
4. The chunk is flagged: `HasLightChangesToProcess = true`.

### 3.2 Job Scheduling (`WorldJobManager.ScheduleLightingUpdate`)

On the main thread each frame:
1. Scan for chunks with `HasLightChangesToProcess`.
2. Check that all 8 neighbors have finished terrain generation (`AreNeighborsDataReady`).
3. Create snapshot copies of the center chunk map (writable) and all 8 neighbor maps (read-only).
4. Transfer the managed light queues to `NativeQueue`s for the job.
5. Check `SunlightRecalculationQueue` for pending column recalculations (from unloaded neighbor recovery).
6. Schedule the `NeighborhoodLightingJob`.

### 3.3 The Job (`NeighborhoodLightingJob`)

**Inputs:** Center chunk map (writable), 8 neighbor maps (read-only), heightmap, light queues, block type data.

**Execution order:**
1. **Edge check** *(optional)* — If `PerformEdgeCheck` is set, validate light at all 4 horizontal chunk borders against neighbor data. Border voxels with less light than their neighbor could supply are enqueued for re-spreading. See Section 3.6 for details.
2. **Seed** — Process column recalculation queue, sunlight BFS queue, blocklight BFS queue.
3. **Sunlight darkness removal** → **Sunlight spreading**.
4. **Blocklight darkness removal** → **Blocklight spreading**.

**Cross-chunk writes:** The job cannot write to read-only neighbor maps. Instead:
- Neighbor light modifications are added to `CrossChunkLightMods` (a `NativeList<LightModification>`).
- A **write-through cache** (`NativeHashMap<long, uint>`) ensures that subsequent reads within the same job execution see the modified values. This is critical for darkness removal: if we set a neighbor voxel to 0, the re-spreading phase must see that 0, not the stale read-only value.

**Output:** Modified center map, cross-chunk modifications list, `IsStable` flag.

### 3.4 Result Processing (`WorldJobManager.ProcessLightingJobs`)

Back on the main thread:
1. **Merge light bits** into live chunk data via `ApplyLightingJobResult` — only light bits are overwritten, preserving block changes made during job execution (TOCTOU safety).
2. **Apply cross-chunk modifications** to loaded neighbor chunks, subject to two heightmap guards:
   - **Guard 1 (skip false darkness):** If a voxel above the heightmap currently has sunlight=15 and the mod tries to decrease it, skip. The voxel has direct sky access; the cross-chunk shadow casting doesn't see this chunk's heightmap and would incorrectly darken it.
   - **Guard 2 (skip underground sunlight increases):** If a sunlight mod tries to increase light on a voxel at or below the heightmap, skip. This prevents light leakage where a neighbor's air column BFS wraps around and sets sunlight on underground voxels. The chunk's own BFS handles underground lighting correctly via column recalculation.
   - If neither guard triggers: set the light value directly and enqueue the position in the neighbor's light queue for further propagation.
3. **Handle unloaded neighbors:** If a target neighbor isn't loaded, save the affected column coordinates to `LightingStateManager` for recovery when the chunk eventually loads.
4. **Stability check:**
   - If `IsStable`: request mesh rebuild for this chunk and neighbors.
   - If not stable: set `HasLightChangesToProcess = true` for another pass next frame.

### 3.5 Readiness Gates

Mesh generation is gated by `AreNeighborsReadyAndLit`, which requires all 8 neighbors to:
- Have finished terrain generation.
- Have no running lighting jobs.
- Have `HasLightChangesToProcess = false`.
- Have `NeedsInitialLighting = false`.
- Have `NeedsEdgeCheck = false`.
- Have `IsAwaitingMainThreadProcess = false`.

This prevents meshes from being built with incomplete lighting data.

### 3.6 Edge Consistency Checking (Starlight-Inspired)

After a chunk's initial lighting stabilizes, its border voxels may have incorrect light due to neighbor load order, dropped cross-chunk modifications, or save/load inconsistencies. The edge checking system detects and corrects these issues.

**Lifecycle:**
1. When `NeedsInitialLighting` is cleared (initial lighting scheduled), `NeedsEdgeCheck` is set to `true`. Also set for chunks loaded from disk with stable lighting.
2. In the main update loop, `NeedsEdgeCheck` is checked after initial lighting but before regular updates. It requires `AreNeighborsDataReady` before scheduling.
3. `WorldJobManager.ScheduleLightingUpdate` reads `chunkData.NeedsEdgeCheck` into the job's `PerformEdgeCheck` flag and clears it.
4. The job's edge check runs as "Pass -1" before the normal BFS seeding.

**Algorithm (`CheckEdges`):**
- Iterates all voxels on the 4 horizontal chunk borders (South z=0, North z=15, West x=0, East x=15).
- For each border voxel, reads the cross-chunk neighbor's light level.
- Calculates `expectedFromNeighbor = max(0, neighborLight - 1 - centerOpacity)`.
- If `expectedFromNeighbor > centerLight`, the center voxel is missing light. Enqueues it in the placement queue for the BFS to correct.

**Design constraint:** The edge check only **adds** missing light (placement queue). It does not remove stale light (no removal queue entries). Removal during edge checks risks propagating false darkness inward when neighbor data is stale or incomplete.

---

## 4. Cross-Reference: Our System vs. Starlight (Moonrise)

This section documents how our lighting engine compares to the Starlight reference implementation. For each Starlight technique, we note whether it's implemented, applicable, or unnecessary given our architecture.

### 4.1 Techniques We Implement Correctly

#### Dual-Phase BFS (Removal → Spreading)
**Starlight:** `performLightDecrease()` runs before `performLightIncrease()`.
**Our system:** Same. All darkness removal completes before light spreading begins, for both channels.
**Status:** Implemented correctly.

#### Vertical Sunlight No-Attenuation Rule
**Starlight:** Sunlight at level 15 traveling downward through fully transparent blocks stays at 15.
**Our system:** Same rule in `PropagateLight` (line 204 of `NeighborhoodLightingJob.cs`).
**Status:** Implemented correctly.

#### Heightmap-Driven Column Optimization
**Starlight:** Uses `heightMapBlockChange[]` to track the lowest Y that needs updating per column.
**Our system:** Uses `heightMap[]` in `RecalculateSunlightForColumn` to skip air above the highest opaque block.
**Status:** Implemented correctly.

#### TOCTOU-Safe Light Merge
**Starlight:** Uses SWMR (Single-Writer Multi-Reader) nibble arrays to separate updating and visible light data.
**Our system:** `ApplyLightingJobResult` merges only light bits, preserving block changes made during job execution.
**Status:** Implemented correctly, different mechanism but same safety guarantee.

#### Opacity-Based Light Attenuation
**Starlight:** `targetLevel = propagatedLevel - max(1, opacity)`.
**Our system:** `targetLevel = sourceLight - 1 - neighborOpacity`.
**Note:** These differ for semi-transparent blocks. Starlight uses `max(1, opacity)` while we use `1 + opacity`. For air (opacity=0) both give `sourceLight - 1`. For water (opacity=2), Starlight gives `sourceLight - 2` while ours gives `sourceLight - 3`. This means our BFS attenuates semi-transparent blocks more aggressively than the column recalculation (which uses just `opacity`), causing a 1-level shadow line at chunk borders under water. Aligning with Starlight's `max(1, opacity)` formula would fix this, but requires careful testing with all semi-transparent block types.
**Status:** Known discrepancy — see Section 5 for the proposed fix.

#### Edge Checking on Chunk Load
**Starlight:** Has a dedicated `checkChunkEdges()` method that runs on chunk load. It iterates every block on the 4 horizontal chunk borders and validates that each block's light level is consistent with its neighbors.
**Our system:** Implemented as a `PerformEdgeCheck` flag on `NeighborhoodLightingJob` with a `NeedsEdgeCheck` lifecycle flag on `ChunkData`. Runs once after initial lighting stabilizes and once for chunks loaded from disk. See Section 3.6 for details.
**Difference from Starlight:** Our edge check only adds missing light (placement queue), never removes stale light. Starlight's `checkChunkEdges` does both. This is a deliberate constraint — removal during edge checks risks false darkness when neighbor data is incomplete.
**Status:** Implemented (placement-only variant).

#### BFS Chunk Boundary Confinement
**Starlight:** Uses a bounded 5x5 chunk cache. Propagation naturally stops when the cache boundary is reached.
**Our system:** The BFS reads from the 3x3 neighbor grid but is explicitly confined to the center chunk via `IsInCenterChunk()` guards on all queue enqueue operations. Neighbor voxels have their light *written* (via `CrossChunkLightMods`) but are never enqueued for further BFS propagation.
**Why this is critical:** Without this guard, the BFS exits the center chunk, travels through neighbor data (which may be all-zeros for unloaded chunks — appearing as a void of air), and re-enters the center chunk underground. This creates vertical walls of light leaking through solid terrain at chunk borders facing unloaded chunks.
**Status:** Implemented correctly.

### 4.2 Missing Techniques (Applicable Improvements)

#### Direction Exclusion in BFS Queue ("No-Backtrack" Logic)
**Starlight:** Each queue entry is a 64-bit `long` encoding position, light level, and a **6-bit direction bitmask**. When block A propagates to neighbor B (e.g., East), the queue entry for B uses `everythingButTheOppositeDirection` to exclude West. This prevents B from re-checking the block that just lit it.

```
Queue entry layout (64-bit long):
  Bits 0-27:  Encoded position (x | z << 6 | y << 12)
  Bits 28-31: Propagated light level (0-15)
  Bits 32-37: Direction bitset (which of 6 directions to check)
  Bits 38-61: Unused
  Bits 62-64: Flags (FLAG_WRITE_LEVEL, FLAG_RECHECK_LEVEL, FLAG_HAS_SIDED_TRANSPARENT_BLOCKS)
```

The direction bitset indexes into a precomputed lookup table `OLD_CHECK_DIRECTIONS[64]`, where each entry is an array of only the active directions for that bitmask. This avoids iterating all 6 directions every time.

**Our system:** Checks all 6 directions for every queue entry. The level comparison (`targetLevel > neighborLight`) prevents infinite loops, so this is a performance issue rather than a correctness bug.

**Impact:** ~17% unnecessary neighbor checks. Each BFS node visits 6 neighbors instead of 5 (the source direction is always redundant).

**Recommendation:** **Medium priority.** Could be implemented by changing the queue entry to include a `fromDirection` byte and skipping the opposite direction in the propagation loop. Alternatively, pack direction + level + position into a single `ulong` matching Starlight's layout.

#### `FLAG_RECHECK_LEVEL` for Re-Queued Sources
**Starlight:** During darkness removal, when a neighbor's light is higher than the removal target (indicating an alternative light source), Starlight re-queues it in the *increase* queue with `FLAG_RECHECK_LEVEL` set. During the increase phase, this flag triggers a verification:
```java
if (this.getLightLevel(posX, posY, posZ) != propagatedLightLevel) {
    continue;  // Level changed since we queued this — skip
}
```
This prevents re-propagating light from a position whose level was subsequently modified by another darkness wave.

**Our system:** No equivalent flag. Re-queued positions are placed directly in the placement queue without verification.

**Mitigating factor:** Our system processes all darkness removal before all light spreading (strictly sequential). This means by the time the placement queue is processed, all darkness waves have completed. The current level read at the start of `PropagateLight` reflects the final state. **This largely eliminates the need for `FLAG_RECHECK_LEVEL` in our architecture.**

**Recommendation:** **Low priority.** Our sequential processing order makes this unnecessary. Starlight needs it because its architecture can interleave decrease and increase operations more aggressively. If we ever move to a combined queue (interleaved processing), this flag would become necessary.

### 4.3 Starlight Techniques Not Applicable to Our System

#### SWMR Nibble Arrays
**Starlight:** Uses `SWMRNibbleArray` (Single-Writer Multi-Reader) — a custom data structure with separate `storageUpdating` (writer thread) and `storageVisible` (reader threads, volatile). Writers modify `storageUpdating`; `updateVisible()` syncs changes to `storageVisible`. This allows zero-copy concurrent reads during light propagation.

**Our system:** Uses snapshot copies of chunk data (`GetChunkMapForJob` allocates a `NativeArray<uint>` copy). The job writes to its own copy, and `ApplyLightingJobResult` merges the light bits back.

**Why not applicable:** Unity's Job System enforces read-only access on shared NativeArrays via `[ReadOnly]` attributes. We cannot have a job write to a neighbor chunk's NativeArray while another job reads it. The snapshot + merge approach is the idiomatic Unity solution and works correctly within Unity's safety system.

#### Null Section Initialization / Extrusion
**Starlight:** Starlight's nibble arrays are separate from block data. A null nibble means "no light data." When a null (empty) section borders a non-empty section, `initNibble()` either sets it to full light (above all blocks) or "extrudes" the bottom layer of the section above (copies light downward). This ensures empty sections have correct light for neighbor lookups. `checkNullSection()` handles this with a cache (`nullPropagationCheckCache`) to avoid redundant initialization.

**Our system:** Light is packed into the same `uint` as block data. An "empty" voxel (air, ID=0) still has light bits that get set normally by the BFS. There is no concept of a "null nibble" because every voxel always has a `uint` allocated.

**Why not applicable:** Our packed `uint` format means light storage always exists for every voxel. The BFS writes light values into air voxels the same way as any other. Starlight needs extrusion because its light storage is separate and lazily allocated; our storage is always present.

#### Conditionally Opaque Blocks / VoxelShape Face Occlusion
**Starlight:** Supports blocks that are transparent in some directions but opaque in others (e.g., stairs, slabs, glass panes). Uses `VoxelShape.faceShapeOccludes()` for per-face transparency checks. Queue entries carry `FLAG_HAS_SIDED_TRANSPARENT_BLOCKS` to enable the expensive check only when needed.

**Our system:** Uses a single `opacity` value per block type. Blocks are either uniformly opaque or uniformly transparent — no per-face variation.

**Why not applicable (currently):** We have no block types with directional transparency. If stairs, slabs, or other partial blocks are added in the future, this optimization would become relevant. At that point, adding a `hasDirectionalOpacity` flag to `BlockTypeJobData` and implementing per-face checks would be necessary.

#### Deferred Light Writes / `FLAG_WRITE_LEVEL`
**Starlight:** Uses `FLAG_WRITE_LEVEL` to defer the actual light write until the queue entry is processed. This allows batch writes and avoids redundant writes when a voxel is updated multiple times in the same pass.

**Our system:** `SetLight` writes immediately when called. Since our BFS processes each position at most once per phase (the level comparison prevents re-processing), redundant writes are already rare.

**Why not applicable:** With our sequential dual-phase approach, each voxel is written at most once during darkness removal (set to 0) and at most once during spreading (set to final level). The overhead of deferred writing would not provide meaningful benefit.

---

## 5. Performance Optimization Opportunities

These are optimizations from Starlight that *are* applicable to our system, ranked by potential impact.

### 5.1 Branchless Neighbor Lookup (Unified Map Buffer)
**Priority: High** | **Complexity: High**

**Current:** `GetPackedData` in `NeighborhoodLightingJob` uses a chain of `if/else if` branches to determine which of 9 NativeArrays to read from, for *every single neighbor check*.

**Starlight approach:** Uses a single contiguous cache array indexed by `(x >> 4) + 5 * (z >> 4) + 25 * (y >> 4)`. No branches — just math.

**Proposed:** Allocate a single `NativeArray<uint>` of size `48 * 128 * 48` (3x1x3 chunks = ~1.2 MB). Copy the 9 chunk maps into the correct offsets at job start. Replace all `GetPackedData` branch logic with:
```csharp
int index = (localPos.x + 16) + (localPos.z + 16) * 48 + localPos.y * (48 * 48);
uint data = unifiedMap[index];
```

### 5.2 Direction Bitmask in Queue Entries
**Priority: Medium** | **Complexity: Low-Medium**

See Section 4.2 above. Pack position + level + direction bitmask into a `ulong` queue entry. Skip the reverse direction when propagating. Use `math.tzcnt` (count trailing zeros) for branchless bit iteration.

### 5.3 Virtual Skylight (Heightmap-Based Read Optimization)
**Priority: Medium** | **Complexity: Medium**

**Current:** `RecalculateSunlightForColumn` writes sunlight=15 to every air voxel above the heightmap. This generates thousands of memory writes per column.

**Starlight approach:** Treats sky-exposed air as "extruded" — the level 15 is implicit from the heightmap, never explicitly stored.

**Proposed:** Add a heightmap check to `GetSunLight`:
```csharp
if (y > heightMap[x + 16 * z]) return 15;  // Virtual: no memory read needed
return (byte)((packedData >> 16) & 0xF);    // Physical: read from packed data
```
This eliminates the write loop and reduces memory bandwidth.

### 5.4 Section-Level Skipping in BFS
**Priority: Medium** | **Complexity: Medium**

**Current:** The BFS propagates through every Y level, even empty sections above the terrain.

**Proposed:** Use the existing `SectionJobData.IsEmpty` flags to skip entire 16-block vertical ranges in the BFS. If a section is empty and the section above is also empty, sunlight is implicitly 15 (virtual skylight) and no propagation is needed.

### 5.5 Align BFS Attenuation with Starlight Formula
**Priority: Medium** | **Complexity: Low** | **Known Bug**

**Current:** `PropagateLight` uses `sourceLight - 1 - opacity` (i.e., `1 + opacity` attenuation per step).
**Starlight:** Uses `sourceLight - max(1, opacity)`.

For air (opacity=0) both give `-1`. For water (opacity=2): ours gives `-3`, Starlight gives `-2`. The column recalculation (`RecalculateSunlightForColumn`) attenuates by just `opacity` per block. This means the BFS and column recalculation produce different values for semi-transparent blocks, causing a **1-level shadow line at chunk borders under water**.

**Fix:** Change the BFS formula to `Mathf.Max(0, sourceLight - Mathf.Max(1, neighborProps.Opacity))`. This requires updating the column recalculation to also use `max(1, opacity)` for consistency, and testing with all semi-transparent block types.

### 5.6 Column Aggregation for Burst Updates
**Priority: Low** | **Complexity: Medium**

When multiple blocks change in the same vertical column (e.g., explosions, falling sand), deduplicate them into a single column recalculation. Use a `NativeArray<int>` (size 256) to track the lowest modified Y per column, and seed the BFS only from that Y level.

---

## 6. Key File Reference

| File | Role |
|------|------|
| `Jobs/NeighborhoodLightingJob.cs` | Core BFS flood-fill job (sunlight + blocklight propagation) |
| `WorldJobManager.cs` | Schedules lighting jobs, processes results, applies cross-chunk modifications |
| `Data/ChunkData.cs` | Heightmap management, light queues, `ModifyVoxel` triggering, `NeedsEdgeCheck` flag |
| `Data/ChunkSection.cs` | Section-level voxel storage, `IsEmpty`/`IsFullySolid` flags |
| `Jobs/BurstData/BurstVoxelDataBitMapping.cs` | Bit-packing/unpacking for light values in `uint` |
| `Helpers/ChunkMath.cs` | Coordinate → flat index conversion |
| `Serialization/LightingStateManager.cs` | Persists pending sunlight recalculations for unloaded chunks |
| `Jobs/StandardChunkGenerationJob.cs` | Initial heightmap computation during world generation |

**Starlight reference files** (Java, in `_REFERENCES/Moonrise/.../starlight/light/`):

| File | Role |
|------|------|
| `StarLightEngine.java` | Base class: BFS propagation, queue management, direction bitsets |
| `SkyStarLightEngine.java` | Sunlight: column propagation, null section handling, extrusion |
| `BlockStarLightEngine.java` | Blocklight: source detection, emission propagation |
| `StarLightInterface.java` | Public API: task queueing, scheduling, edge checking entry point |
| `SWMRNibbleArray.java` | Thread-safe light data storage (Single-Writer Multi-Reader) |
