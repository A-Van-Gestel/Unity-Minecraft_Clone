# Lighting System Overview

This document provides a complete technical reference for the voxel lighting engine. The system is asynchronous, multi-threaded (via Unity's C# Job System and Burst), and handles two distinct light channels: **Sky light** (monochrome scalar, tinted by `SkyLightColor` in the shader) and **Blocklight** (per-channel RGB).
The design is heavily inspired by the **Starlight** lighting engine (now **Moonrise**), a high-performance replacement for Minecraft's vanilla lighting. Where our implementation diverges from Starlight, this document explains why.

> **Reference implementation:** `_REFERENCES/Moonrise/` contains the full Moonrise source. Key files are in `.../patches/starlight/light/`.

---

## 1. Core Concepts

### 1.1 Data Representation

Light is stored in a separate `ushort[] LightData` array per section (one `ushort` per voxel, 4096 entries per 16×16×16 section). The `uint` voxel data no longer carries light bits — bits 16-23 are reserved/zeroed (see `DATA_STRUCTURES.md`).

| Field        | Bits (ushort) | Range | Purpose                              |
|--------------|---------------|-------|--------------------------------------|
| Sky light    | 0-3           | 0-15  | Light from the sky (monochrome)      |
| Blocklight R | 4-7           | 0-15  | Red channel of block-emitted light   |
| Blocklight G | 8-11          | 0-15  | Green channel of block-emitted light |
| Blocklight B | 12-15         | 0-15  | Blue channel of block-emitted light  |

All light access goes through `LightBitMapping` helpers (`GetSkyLight`, `SetSkyLight`, `GetBlocklightR/G/B`, `PackLightData`, etc.).

**Final light value:** For rendering, the shader applies separate shade curves to sky light (modulated by day/night cycle and tinted by `SkyLightColor`) and blocklight RGB, then takes per-channel `max()`. See `SMOOTH_AND_RGB_LIGHTING.md` §3.6 for the full shader pipeline.

### 1.2 Block Light Properties

Each `BlockType` defines how it interacts with the lighting system:

| Property        | Type        | Meaning                                                                                                |
|-----------------|-------------|--------------------------------------------------------------------------------------------------------|
| `opacity`       | byte (0-15) | How much light is reduced when passing *through* this block. 0 = fully transparent, 15 = fully opaque. |
| `lightEmission` | byte (0-15) | How much light this block emits. 0 = none, 15 = brightest (e.g., lava).                                |

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
2. For each of 6 neighbors: calculate `targetLevel = sourceLight - max(1, neighborOpacity)`.
3. If `targetLevel > neighborCurrentLight`, update the neighbor and enqueue it for further spreading.
4. **Special rule:** Sky light at level 15 traveling straight *down* through fully transparent blocks does not attenuate (remains 15). This creates the fast vertical sky light columns.
5. **Opaque surface lighting:** Opaque blocks receive light on their surface (`sourceLight - 1`) but are never enqueued for further propagation. Light stops at opaque surfaces.

**Ordering is critical:** All darkness removal completes before any light spreading begins. This ensures the "clean slate" from removal is fully established before new light fills in, preventing ghost light artifacts.

**BFS boundary rule (critical):** The BFS must **never enqueue positions outside the center chunk** for further propagation. Both `PropagateDarkness` and `PropagateLight` use an `IsInCenterChunk(neighborPos)` guard before enqueueing.
Neighbor voxels still have their light **written** via `SetLight` (which routes to `CrossChunkLightMods`), but the BFS does not continue through them. Without this guard, the BFS can exit the center chunk,
travel through a neighbor's data (which may be all-zeros for unloaded chunks, appearing as a void of air), and re-enter the center chunk underground — creating vertical walls of light leaking through solid terrain at chunk borders.
The neighbor's own lighting job handles propagation within its borders after the cross-chunk modifications are applied by the main thread.

---

## 2. Sky Light vs. Blocklight

### 2.1 Blocklight

Blocklight is fully RGB — each light-emitting block defines an emission color `(R, G, B)` with independent 4-bit channels (0-15 each). The BFS propagates three channels independently, using per-channel `max()` at each destination voxel for correct additive color mixing. Each channel attenuates by `max(1, opacity)` per step.

### 2.2 Sky Light

More complex, with special optimizations. Sky light remains a monochrome scalar (0-15) in the BFS; time-of-day color (blue moonlight, warm dawn, red blood moon) is applied as a shader uniform (`SkyLightColor`) at render time.

**Source:** The sky above the chunk (conceptually Y = ChunkHeight), with a starting value of 15.

**Heightmap:** Each chunk maintains a `heightMap[256]` (16x16 columns) storing the Y-level of the highest light-obstructing block per column. This enables fast sky light initialization.

**Column Recalculation (`RecalculateSkylightForColumn`):**

1. **Above the heightmap:** All voxels above the highest opaque block are set to sky light 15.
2. **Horizontal shadow casting:** If the highest block is opaque, check its 4 horizontal neighbors for partial sky light (1-14). If found, set them to 0 and enqueue for darkness removal to clear stale scattered light.
3. **Below the heightmap:** Propagate downward from the highest block with opacity-based attenuation.

After column recalculation, the standard BFS phases handle horizontal spreading.

---

## 3. The Asynchronous Update Loop

### 3.1 Block Modification → Light Queues

When a player places or breaks a block (`ChunkData.ModifyVoxel`):

1. The old sky light and blocklight RGB values are captured from `section.LightData[]` via `LightBitMapping`.
2. The heightmap is updated if the block is light-obstructing.
3. The modified voxel and its 6 neighbors are added to `_skylightBfsQueue` and `_blocklightBfsQueue`.
4. The chunk is flagged: `HasLightChangesToProcess = true`.

### 3.2 Job Scheduling (`WorldJobManager.ScheduleLightingUpdate`)

On the main thread each frame, `World.Update()` iterates a **dirty set** (`_chunksNeedingLightWork`) containing only chunks with pending work, rather than scanning all loaded chunks:

1. **Drain staging queue:** Background threads (e.g., deserialization) enqueue positions into a `ConcurrentQueue<Vector2Int>`. The main thread drains this into the `HashSet` at the start of each frame.
2. **Iterate dirty set:** For each chunk in the set, check flags (`NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`).
3. Check that all 8 neighbors have finished terrain generation (`AreNeighborsDataReady`).
4. Create snapshot copies of the center chunk map (writable) and all 8 neighbor maps (read-only).
5. Transfer the managed light queues to `NativeQueue`s for the job.
6. Check `SkylightRecalculationQueue` for pending column recalculations (from unloaded neighbor recovery).
7. Schedule the `NeighborhoodLightingJob`.
8. **Self-clean:** Remove the chunk from the dirty set when all flags are clear.

A time-based fail-safe full scan (every ~1 second) re-populates the dirty set from `worldData.Chunks.Values` to catch any missed registrations. See [CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md) Section 4 for the full pseudocode.

### 3.3 The Job (`NeighborhoodLightingJob`)

**Inputs:** Center chunk map (writable), center light map (writable), 8 neighbor maps + light maps (read-only), heightmap, light queues, block type data.

**Execution order:**

1. **Edge check** *(optional)* — If `PerformEdgeCheck` is set, validate light at all 4 horizontal chunk borders against neighbor data. Border voxels with less light than their neighbor could supply are enqueued for re-spreading. See Section 3.6 for details.
2. **Seed** — Process column recalculation queue, sky light BFS queue, blocklight BFS queue.
3. **Sky light darkness removal** → **Sky light spreading**.
4. **Blocklight darkness removal** → **Blocklight spreading** (per-channel RGB).

**Cross-chunk writes:** The job cannot write to read-only neighbor maps. Instead:

- Neighbor light modifications are added to `CrossChunkLightMods` (a `NativeList<LightModification>`).
- A **write-through cache** (`NativeHashMap<long, uint>`) ensures that subsequent reads within the same job execution see the modified values. This is critical for darkness removal: if we set a neighbor voxel to 0, the re-spreading phase must see that 0, not the stale read-only
  value.

**Output:** Modified center map + light map, cross-chunk modifications list, `IsStable` flag.

### 3.4 Result Processing (`WorldJobManager.ProcessLightingJobs`)

Back on the main thread:

1. **Merge light data** into live chunk data via `ApplyLightingJobResult` — the `ushort[] LightData` array is copied back from the job's NativeArray. Block changes made to the `uint` voxel array during job execution are preserved (TOCTOU safety).
2. **Apply cross-chunk modifications** to loaded neighbor chunks, subject to two heightmap guards:
    - **Guard 1 (skip false darkness):** If a voxel above the heightmap currently has sky light=15 and the mod tries to decrease it, skip. The voxel has direct sky access; the cross-chunk shadow casting doesn't see this chunk's heightmap and would incorrectly darken it.
    - **Guard 2 (skip underground sky light increases):** If a sky light mod tries to increase light on a voxel at or below the heightmap, skip. This prevents light leakage where a neighbor's air column BFS wraps around and sets sky light on underground voxels.
      The chunk's own BFS handles underground lighting correctly via column recalculation.
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
- Have `IsAwaitingMainThreadProcess = false`.

> **Note:** `NeedsEdgeCheck` is intentionally NOT checked by `AreNeighborsReadyAndLit` or `ScheduleMeshing`. Edge checks are corrections that improve quality but do not block the meshing pipeline. A chunk with `NeedsEdgeCheck = true` can be meshed before the edge check runs.

This prevents meshes from being built with incomplete lighting data.

For the complete pipeline flow including all gates, flags, and interactions, see [CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md).

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

#### Vertical Sky Light No-Attenuation Rule

**Starlight:** Sky light at level 15 traveling downward through fully transparent blocks stays at 15.
**Our system:** Same rule in `PropagateLight` (line 204 of `NeighborhoodLightingJob.cs`).
**Status:** Implemented correctly.

#### Heightmap-Driven Column Optimization

**Starlight:** Uses `heightMapBlockChange[]` to track the lowest Y that needs updating per column.
**Our system:** Uses `heightMap[]` in `RecalculateSkylightForColumn` to skip air above the highest opaque block.
**Status:** Implemented correctly.

#### TOCTOU-Safe Light Merge

**Starlight:** Uses SWMR (Single-Writer Multi-Reader) nibble arrays to separate updating and visible light data.
**Our system:** `ApplyLightingJobResult` merges only light bits, preserving block changes made during job execution.
**Status:** Implemented correctly, different mechanism but same safety guarantee.

#### Opacity-Based Light Attenuation

**Starlight:** `targetLevel = propagatedLevel - max(1, opacity)`.
**Our system:** `targetLevel = sourceLight - max(1, neighborOpacity)`.
Both the BFS (`PropagateLight`) and column recalculation (`RecalculateSunlightForColumn`) use the same `max(1, opacity)` formula, ensuring consistent attenuation for semi-transparent blocks (e.g., water). The edge check (`CheckEdgeVoxel`) also uses this formula.
**Status:** Implemented correctly.

#### Edge Checking on Chunk Load

**Starlight:** Has a dedicated `checkChunkEdges()` method that runs on chunk load. It iterates every block on the 4 horizontal chunk borders and validates that each block's light level is consistent with its neighbors.
**Our system:** Implemented as a `PerformEdgeCheck` flag on `NeighborhoodLightingJob` with a `NeedsEdgeCheck` lifecycle flag on `ChunkData`. Runs once after initial lighting stabilizes and once for chunks loaded from disk. See Section 3.6 for details.
**Difference from Starlight:** Our edge check only adds missing light (placement queue), never removes stale light. Starlight's `checkChunkEdges` does both. This is a deliberate constraint — removal during edge checks risks false darkness when neighbor data is incomplete.
**Status:** Implemented (placement-only variant).

#### BFS Chunk Boundary Confinement

**Starlight:** Uses a bounded 5x5 chunk cache. Propagation naturally stops when the cache boundary is reached.
**Our system:** The BFS reads from the 3x3 neighbor grid but is explicitly confined to the center chunk via `IsInCenterChunk()` guards on all queue enqueue operations. Neighbor voxels have their light *written* (via `CrossChunkLightMods`) but are never enqueued for further BFS
propagation.
**Why this is critical:** Without this guard, the BFS exits the center chunk, travels through neighbor data (which may be all-zeros for unloaded chunks — appearing as a void of air), and re-enters the center chunk underground.
This creates vertical walls of light leaking through solid terrain at chunk borders facing unloaded chunks.
**Status:** Implemented correctly.

### 4.2 Missing Techniques (Applicable Improvements)

#### Direction Exclusion in BFS Queue ("No-Backtrack" Logic)

**Starlight:** Each queue entry is a 64-bit `long` encoding position, light level, and a **6-bit direction bitmask**. When block A propagates to neighbor B (e.g., East), the queue entry for B uses `everythingButTheOppositeDirection` to exclude West.
This prevents B from re-checking the block that just lit it.

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

**Recommendation:** **Medium priority.** Could be implemented by changing the queue entry to include a `fromDirection` byte and skipping the opposite direction in the propagation loop.
Alternatively, pack direction + level + position into a single `ulong` matching Starlight's layout.

#### `FLAG_RECHECK_LEVEL` for Re-Queued Sources

**Starlight:** During darkness removal, when a neighbor's light is higher than the removal target (indicating an alternative light source), Starlight re-queues it in the *increase* queue with `FLAG_RECHECK_LEVEL` set. During the increase phase, this flag triggers a verification:

```java
if (this.getLightLevel(posX, posY, posZ) != propagatedLightLevel) {
    continue;  // Level changed since we queued this — skip
}
```

This prevents re-propagating light from a position whose level was subsequently modified by another darkness wave.

**Our system:** No equivalent flag. Re-queued positions are placed directly in the placement queue without verification.

**Mitigating factor:** Our system processes all darkness removal before all light spreading (strictly sequential). This means by the time the placement queue is processed, all darkness waves have completed.
The current level read at the start of `PropagateLight` reflects the final state. **This largely eliminates the need for `FLAG_RECHECK_LEVEL` in our architecture.**

**Recommendation:** **Low priority.** Our sequential processing order makes this unnecessary. Starlight needs it because its architecture can interleave decrease and increase operations more aggressively.
If we ever move to a combined queue (interleaved processing), this flag would become necessary.

### 4.3 Starlight Techniques Not Applicable to Our System

#### SWMR Nibble Arrays

**Starlight:** Uses `SWMRNibbleArray` (Single-Writer Multi-Reader) — a custom data structure with separate `storageUpdating` (writer thread) and `storageVisible` (reader threads, volatile). Writers modify `storageUpdating`; `updateVisible()` syncs changes to `storageVisible`.
This allows zero-copy concurrent reads during light propagation.

**Our system:** Uses snapshot copies of chunk data (`GetChunkMapForJob` allocates a `NativeArray<uint>` copy). The job writes to its own copy, and `ApplyLightingJobResult` merges the light bits back.

**Why not applicable:** Unity's Job System enforces read-only access on shared NativeArrays via `[ReadOnly]` attributes. We cannot have a job write to a neighbor chunk's NativeArray while another job reads it.
The snapshot + merge approach is the idiomatic Unity solution and works correctly within Unity's safety system.

#### Null Section Initialization / Extrusion

**Starlight:** Starlight's nibble arrays are separate from block data. A null nibble means "no light data." When a null (empty) section borders a non-empty section,`initNibble()` either sets it to full light (above all blocks) or
"extrudes" the bottom layer of the section above (copies light downward). This ensures empty sections have correct light for neighbor lookups. `checkNullSection()` handles this with a cache (`nullPropagationCheckCache`) to avoid redundant initialization.

**Our system:** Light is stored in a separate `ushort[] LightData` array per section. An "empty" voxel (air, ID=0) still has light data that gets set normally by the BFS. There is no concept of a "null nibble" because every allocated section has a full `LightData` array.

**Why not applicable:** Our per-section `LightData` array means light storage always exists for every voxel in an allocated section. The BFS writes light values into air voxels the same way as any other. Null sections (unallocated) are handled at a higher level — they do not participate in the BFS.

#### Conditionally Opaque Blocks / VoxelShape Face Occlusion

**Starlight:** Supports blocks that are transparent in some directions but opaque in others (e.g., stairs, slabs, glass panes). Uses `VoxelShape.faceShapeOccludes()` for per-face transparency checks.
Queue entries carry `FLAG_HAS_SIDED_TRANSPARENT_BLOCKS` to enable the expensive check only when needed.

**Our system:** Uses a single `opacity` value per block type. Blocks are either uniformly opaque or uniformly transparent — no per-face variation.

**Why not applicable (currently):** We have no block types with directional transparency. If stairs, slabs, or other partial blocks are added in the future, this optimization would become relevant.
At that point, adding a `hasDirectionalOpacity` flag to `BlockTypeJobData` and implementing per-face checks would be necessary.

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

### 5.3 Virtual Sky Light (Heightmap-Based Read Optimization)

**Priority: Medium** | **Complexity: Medium**

**Current:** `RecalculateSkylightForColumn` writes sky light=15 to every air voxel above the heightmap via the `ushort LightData[]` array. This generates thousands of memory writes per column.

**Starlight approach:** Treats sky-exposed air as "extruded" — the level 15 is implicit from the heightmap, never explicitly stored.

**Proposed:** Add a heightmap check to `GetSkyLight`:

```csharp
if (y > heightMap[x + 16 * z]) return 15;  // Virtual: no memory read needed
return LightBitMapping.GetSkyLight(lightData[index]);  // Physical: read from LightData
```

This eliminates the write loop and reduces memory bandwidth.

### 5.4 Section-Level Skipping in BFS

**Priority: Medium** | **Complexity: Medium**

**Current:** The BFS propagates through every Y level, even empty sections above the terrain.

**Proposed:** Use the existing `SectionJobData.IsEmpty` flags to skip entire 16-block vertical ranges in the BFS. If a section is empty and the section above is also empty, sunlight is implicitly 15 (virtual skylight) and no propagation is needed.

### ~~5.5 Align BFS Attenuation with Starlight Formula~~ (Fixed)

All three attenuation sites (`PropagateLight`, `RecalculateSunlightForColumn`, `CheckEdgeVoxel`) now use the Starlight-aligned `max(1, opacity)` formula. The previous `1 + opacity` formula over-attenuated semi-transparent blocks (e.g., water) by 1 level compared to column recalculation, causing a 1-level shadow line at chunk borders underwater. See Section 4.2 "Opacity-Based Light Attenuation" for the current comparison.

### 5.6 Column Aggregation for Burst Updates

**Priority: Low** | **Complexity: Medium**

When multiple blocks change in the same vertical column (e.g., explosions, falling sand), deduplicate them into a single column recalculation. Use a `NativeArray<int>` (size 256) to track the lowest modified Y per column, and seed the BFS only from that Y level.

---

## 6. Lighting-Disabled Mode (`enableLighting = false`)

The `enableLighting` setting (`SettingsManager.enableLighting`) is an `[InitializationField]` — it can only be changed from the main menu and requires a world reload to take effect. When disabled, the entire lighting engine is bypassed and every block renders at full brightness (sky light = 15). This section documents where and how the disabled path diverges from the normal pipeline.

### 6.1 Design Intent

The setting exists to fully disable the lighting engine for debugging, performance testing, or aesthetic preference. The goal is simple: **every block, including underground caves, appears at full light level.** No BFS jobs run, no edge checks fire, and no cross-chunk light modifications are processed.

### 6.2 Pipeline Bypass Points

The disabled-lighting path is implemented via guards at specific pipeline entry points, not via a single top-level switch. Each guard prevents work from being enqueued that no job will ever consume:

| Location                                | Guard                                                                        | Purpose                                                                                         |
|-----------------------------------------|------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------|
| `ChunkData.AddToSkyLightQueue`          | `enableLighting` check                                                       | Prevents BFS queue entries + `HasLightChangesToProcess` flag                                    |
| `ChunkData.AddToBlockLightQueue`        | `enableLighting` check                                                       | Same, for blocklight channel                                                                    |
| `ChunkData.ModifyVoxel`                 | `lightingEnabled` local                                                      | Sets initial sky light = 15 (not 0); gates `QueueSkylightRecalculation`                         |
| `WorldJobManager.ProcessGenerationJobs` | Stage 2: gates `LightingStateManager` recovery                               | Prevents orphaned recalculation queue entries and stale `HasLightChangesToProcess`              |
| `WorldJobManager.ProcessGenerationJobs` | Stage 3: sky light fill (else branch)                                        | Fills `LightData` with sky=15 on all non-null sections via `LightingHelper.FillUniformSkyLight` |
| `WorldJobManager.ScheduleMeshing`       | `enableLighting` gate on `HasLightChangesToProcess` / `NeedsInitialLighting` | Bypasses lighting-readiness check — meshing proceeds without waiting for lighting               |
| `World.AreNeighborsMeshReady`           | `enableLighting` gate on `NeedsInitialLighting`                              | Same bypass for neighbor readiness                                                              |
| `World.ForceCompleteDataJobsCoroutine`  | Wraps entire Phase 2 lighting loop                                           | Skips all BFS jobs during initial world load; clears stale flags from disk-loaded chunks        |
| `World.Update` lighting scheduler       | `enableLighting` wraps entire block                                          | Skips dirty-set drain, watchdog scan, and job scheduling                                        |
| `GetChunkMapForJob` / `GetMapForJob`    | Post-copy sky light stamp when `enableLighting = false`                      | Stamps sky light=15 on every entry in the light map snapshot via `LightingHelper`               |

### 6.3 Sky Light Fill (Generation Path)

When a chunk completes terrain generation with lighting disabled, `ProcessGenerationJobs` fills `LightData` instead of setting `NeedsInitialLighting`:

```
for each section in chunkData.sections:
    if section is null → skip (avoids allocating sections for air-only volumes)
    LightingHelper.FillUniformSkyLight(section.LightData, skyLevel: 15)
```

The null-section skip is critical: without it, allocating a `ChunkSection` just to fill its `LightData` wastes ~24 KB per section for air-only volumes that meshing never reads (`IsEmpty = true`).

**Job snapshot sky light stamp:** The sky light fill only covers sections that are non-null at the time it runs. Sections allocated *after* the fill — by structure placement via `ApplyModifications` — have their `LightData` initialized to all-zeros by the section pool. Rather than patching individual cases, both `GetChunkMapForJob` and `GetMapForJob` apply a full-array sky light stamp to the light map snapshot when lighting is disabled, using `LightingHelper.StampFullBrightSunlight`. This catches all sources of stale sky light=0 without allocating
physical `ChunkSection` objects.

### 6.4 Block Modification Path

`ModifyVoxel` writes sky light = 15 to the section's `LightData[]` (instead of the normal 0) so every placed block starts at full brightness. The `QueueSkylightRecalculation` call is skipped entirely — without a running BFS engine, queued columns would set `HasLightChangesToProcess = true` with no job to clear it.

The heightmap is still maintained unconditionally (it is cheap and has no downstream consumers when lighting is disabled).

### 6.5 Initial World Load (Coroutine Path)

`ForceCompleteDataJobsCoroutine` Phase 2 is wrapped in `if (settings.enableLighting)`. The `else` branch clears stale lighting flags (`NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`) on all chunks in the initial load area. These flags may have been serialized as `true` from a previous session where lighting was enabled.

### 6.6 Key Invariants

1. **No path may set `HasLightChangesToProcess = true` when lighting is disabled** without a corresponding clear before meshing. The `ScheduleMeshing` gate bypass is a safety net, not a substitute for preventing the flag from being set.
2. **Null sections must remain null.** The sky light fill must skip them to avoid a memory explosion above terrain.
3. **`enableLighting` is `[InitializationField]`** — it cannot be toggled at runtime. All disabled-path logic assumes a consistent value for the entire world session. `ChunkData.Reset()` clears all transient flags on pool recycling, so a world reload with a different setting starts clean.

---

## 7. Key File Reference

| File                                         | Role                                                                                |
|----------------------------------------------|-------------------------------------------------------------------------------------|
| `Jobs/NeighborhoodLightingJob.cs`            | Core BFS flood-fill job (sky light + RGB blocklight propagation)                    |
| `WorldJobManager.cs`                         | Schedules lighting jobs, processes results, applies cross-chunk modifications       |
| `Data/ChunkData.cs`                          | Heightmap management, light queues, `ModifyVoxel` triggering, `NeedsEdgeCheck` flag |
| `Data/ChunkSection.cs`                       | Section-level voxel + `LightData` storage, `IsEmpty`/`IsFullySolid` flags           |
| `Jobs/BurstData/LightBitMapping.cs`          | Bit-packing/unpacking for light values in `ushort LightData[]`                      |
| `Jobs/BurstData/BurstVoxelDataBitMapping.cs` | Bit-packing/unpacking for block ID and metadata in `uint`                           |
| `Helpers/LightingHelper.cs`                  | Shared lighting utilities (`FillUniformSkyLight`, `StampFullBrightSunlight`)        |
| `Helpers/ChunkMath.cs`                       | Coordinate → flat index conversion                                                  |
| `Serialization/LightingStateManager.cs`      | Persists pending sky light recalculations for unloaded chunks                       |
| `Jobs/StandardChunkGenerationJob.cs`         | Initial heightmap computation during world generation                               |

**Starlight reference files** (Java, in `_REFERENCES/Moonrise/.../starlight/light/`):

| File                        | Role                                                             |
|-----------------------------|------------------------------------------------------------------|
| `StarLightEngine.java`      | Base class: BFS propagation, queue management, direction bitsets |
| `SkyStarLightEngine.java`   | Sunlight: column propagation, null section handling, extrusion   |
| `BlockStarLightEngine.java` | Blocklight: source detection, emission propagation               |
| `StarLightInterface.java`   | Public API: task queueing, scheduling, edge checking entry point |
| `SWMRNibbleArray.java`      | Thread-safe light data storage (Single-Writer Multi-Reader)      |
