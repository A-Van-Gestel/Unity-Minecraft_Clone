# Advanced Lighting Optimizations (Starlight Implementation Details)

This document outlines low-level optimizations derived from a deep code analysis of the Starlight lighting engine (ScalableLux fork). While our current implementation uses the general BFS flood-fill algorithm, Starlight implements specific micro-optimizations regarding memory
layout, bit-packing, direction culling, and "virtual" data that we have not yet implemented.

Implementing these will further reduce memory bandwidth pressure and instruction count within the `NeighborhoodLightingJob`.

## 1. Directional Bitmasking in Queues (The "No-Backtrack" Logic)

### The Observation

In `NeighborhoodLightingJob.cs`, when we dequeue a node, we iterate over **all 6 neighbors** (`for (int i = 0; i < 6; i++)`) to propagate light. We check `GetSunLight` for the neighbor, realizing it's the source we just came from (or already brighter), and fail the check.

In `StarLightEngine.java`, the queue entry is not just a position. It is a `long` that packs the position, the light level, **and a bitmask of directions to propagate to**.

When a block propagates light to its neighbor (e.g., East), it adds the neighbor to the queue but modifies the mask to **exclude West**. This prevents the neighbor from wasting cycles checking the block that just lit it.

### Proposed Implementation (Burst/C#)

Change `LightQueueNode` from a struct of fields to a single `ulong` (64-bit) wrapper.

**Current Struct (~16 bytes):**

```csharp
public struct LightQueueNode {
    public Vector3Int Position; // 12 bytes
    public byte OldLightLevel;  // 1 byte
    // Total ~16 bytes with padding
}
```

**Optimized Packed Format (ulong):**

* **Bits 0-5:**  Direction Bitmask (Flags for N, E, S, W, U, D)
* **Bits 6-9:**  Light Level (0-15)
* **Bits 10-17:** Local X (0-15) (Or Global if handling larger batches)
* **Bits 18-25:** Local Z (0-15)
* **Bits 26-38:** Local Y (0-4096) (Supports tall worlds)
* **Bits 39+:** Flags (e.g., IsComplexShape)

**Algorithm Change:**
Inside `NeighborhoodLightingJob`:

1. Pop `ulong` data.
2. Extract `directionMask`.
3. Instead of `for(int i=0; i<6; i++)`, iterate only the set bits in the mask.
    * *Tip:* Use `Unity.Mathematics.math.tzcnt` (Count Trailing Zeros) to iterate bits efficiently without branching.
4. When enqueueing a neighbor, calculate the inverse direction bit and `AND` it out of the neighbor's new mask (e.g., `neighborMask = ALL_DIRS & ~Inverse(currentDir)`).

## 2. The "Extruded" Skylight Optimization (Virtual Skylight)

### The Observation

In `SkyStarLightEngine.java` (specifically `getLightLevelExtruded`), Starlight does not strictly store `15` values for all air blocks above the terrain. Instead, it treats vertical columns of air as "extruded" data.

Currently, in `ChunkData.RecalculateSunLightLight`, we explicitly iterate from `ChunkHeight` down to the `heightMap` level and write `15` into the `map` array for every single air voxel.

```csharp
// Current Code (Write heavy)
for (int y = VoxelData.ChunkHeight - 1; y > highestBlockY; y--) {
    SetLight(currentPos, 15, LightChannel.Sun); // Writes to NativeArray memory
}
```

### Proposed Implementation

We can virtualize the skylight for empty space to avoid memory writes.

1. **Modify `BurstVoxelDataBitMapping.GetSunLight` (or create a wrapper):**
   Pass the `HeightMap` into the getter.
   ```csharp
   byte GetSunLight(int x, int y, int z, uint packedData, NativeArray<byte> heightMap) {
       // If Y is above the highest opaque block, logic dictates it MUST be 15 (direct sun).
       // We don't need to read/write the actual voxel data bits for this.
       int heightIndex = x + Width * z;
       if (y > heightMap[heightIndex]) return 15; 
       
       return (byte)((packedData & SUNLIGHT_MASK) >> SUNLIGHT_SHIFT);
   }
   ```
2. **Stop the Loop:**
   In `NeighborhoodLightingJob`, remove the loop that fills the sky with 15s. Only start propagating *at* the heightmap level.

**Benefit:** Eliminates thousands of memory writes per chunk generation/update. The "Clear Sky" becomes implicitly defined rather than explicitly stored.

## 3. Branchless Neighbor Lookup (Unified Map Buffer)

### The Observation

In `NeighborhoodLightingJob.cs`, the `GetPackedData` method checks chunk boundaries:

```csharp
if (pos.x < 0) return NeighborWest[...]
else if (pos.x >= 16) return NeighborEast[...]
// ... etc
```

This branching occurs for **every single neighbor check**, causing significant CPU branch misprediction penalties.

Starlight (`StarLightEngine.java` -> `setupEncodeOffset`) sets up a cached viewing window where coordinates are offset, allowing a single mathematical formula to access data across the 3x3 chunk area without logical branches.

### Proposed Implementation

Instead of passing 9 separate `NativeArray<uint>` buffers to the job, pass **one unified buffer** or a struct that handles the offset mathematically.

1. **Unified Buffer Strategy:** Create a temporary `NativeArray<uint>` of size `(3*16) * 128 * (3*16)` (approx 2MB, fits in L3 Cache) at the start of the job. Copy the center and valid neighbors into their respective offsets (Center is at x=16, z=16).
2. **Math-based Indexing:**
   ```csharp
   // No if/else checks needed.
   // Offset x and z by 16 (ChunkWidth) so "local" -1 becomes index 15.
   int index = (localPos.x + 16) + (localPos.z + 16) * (16 * 3) + localPos.y * (16 * 3 * 16 * 3);
   uint data = UnifiedMap[index];
   ```

## 4. Section-Level Skipping (Null Nibble Logic)

### The Observation

Starlight uses `SWMRNibbleArray` (Single Writer Multi Reader). A key feature (`INIT_STATE_NULL`) is that if a 16x16x16 section of the chunk is completely empty (Air), the array is `null`.

In `StarLightEngine.java`, the propagation logic checks if a section is null. If it is, **it skips the entire 16x16x16 area** during iteration, knowing light just passes straight through (or stops, depending on context).

Our current `NeighborhoodLightingJob` iterates `y` linearly from 0 to `ChunkHeight`. If we have a 128-block high world, but the ground is only at Y=30, we waste cycles checking Y=31 to Y=128, even if we implement the "Extruded" optimization above, we still traverse the indices.

### Proposed Implementation

Introduce a **Section Bitmask** to `ChunkData` (or a separate job-safe structure).

* Divide the chunk into vertical sections (e.g., 16 blocks high).
* Store a `uint SectionMask`. If bit `n` is 0, that 16-block section is completely homogeneous (usually Air).
* In the lighting job, iterate sections.

```csharp
int sectionIndex = y / 16;
if ((SectionMask & (1 << sectionIndex)) == 0) {
// Skip 16 blocks
y += 16;
continue;
}
```

## 5. Column Aggregation (Vertical Deduplication)

### The Observation

When generating terrain or dealing with falling sand/explosions, multiple blocks in the same vertical column often update simultaneously. Starlight's `SkyStarLightEngine` uses a `heightMapBlockChange` array. It tracks only the *lowest* Y level that needs an update per X/Z column.

Currently, if 10 blocks break in a column, we queue 10 separate flood-fill events.

### Proposed Implementation

1. In `ChunkData` (or `World.cs` before scheduling), use a `NativeArray<int> columnUpdates` (size 256, initialized to -1).
2. When queueing a Sunlight update, `columnUpdates[x + z*16] = math.min(currentY, newY)`.
3. Pass this array to the job. The job seeds the queue *only* from the Y levels found in `columnUpdates`, preventing redundant recalculations of the same column.

## 6. "Conditionally Opaque" Fast-Path

### The Observation

Starlight's queue entry contains a flag: `FLAG_HAS_SIDED_TRANSPARENT_BLOCKS` (see `StarLightEngine.java`).

When Starlight processes a block, it checks if the block is "Simple" (Full opaque or Full transparent) or "Complex" (Stairs, Slabs, etc.).

* If "Simple", it performs a raw math calculation: `targetLevel = currentLevel - opacity`.
* If "Complex", it performs the expensive VoxelShape collision check.

Currently, our `NeighborhoodLightingJob` fetches `BlockTypes[neighborID]` and checks transparency properties for *every* neighbor check.

### Proposed Implementation

1. Add a `IsSimple` boolean to `BlockTypeJobData`. True if the block is a standard cube or fully air. False if it is a partial block (Slab, Stair, etc.).
2. When adding a node to the `LightQueue` (the `ulong` from Optimization 1), set a bit flag if the *source* block is Complex.
3. Inside the job:
   ```csharp
   if (!isComplexFlag) {
       // Fast path: No geometry checks, just subtraction
       byte attenuation = properties.Opacity; 
       // ... propagate
   } else {
       // Slow path: Check face occlusion logic
   }
   ```

## 7. Deferred Light Set (Queue Buffering)

### The Observation

In `StarLightEngine.java`, specifically `performLightIncrease`:
Starlight uses `increaseQueue` and `decreaseQueue`. When propagating, it often calculates the new value but **does not write it to the world immediately** if it can be avoided (e.g., avoiding cascading updates for block sources).

It also uses a `FLAG_WRITE_LEVEL` in the queue. If set, the value is written to the array upon dequeueing. If not, it assumes the value was written previously or will be handled by a bulk operation.

### Proposed Implementation

While difficult to retrofit entirely, we can optimize our `SetLight` calls.
Currently, `SetLight` in `NeighborhoodLightingJob`:

1. Calculates index.
2. Reads `uint`.
3. Masks bits.
4. Writes `uint`.

If we process nodes in a cache-friendly order (e.g., iterating the queue linearly), we might write to the same `uint` multiple times (e.g., updating Sunlight and Blocklight in separate passes).

**Optimization:** Combine Sunlight and Blocklight propagation into a single pass where possible, or ensure the queue is sorted by index (Spatial locality) so that multiple updates to the same/nearby voxels stay in L1 cache. Starlight's `SWMRNibbleArray` benefits from small array
sizes (4096 bytes) fitting in cache; our `uint` array is larger (4x size). Processing spatially adjacent nodes together is critical.

---

## Summary of Work & Priorities

| Optimization                                | Complexity | CPU Impact       | Memory Impact | Priority   |
|:--------------------------------------------|:-----------|:-----------------|:--------------|:-----------|
| **1. Directional Masks**                    | Low        | High (Micro)     | Low           | **High**   |
| **2. Virtual Skylight**                     | Medium     | High (Bandwidth) | High          | **High**   |
| **3. Branchless Lookup**                    | High       | Very High        | Medium        | **High**   |
| **4. Section Bitmask**                      | Medium     | Medium           | Low           | **Medium** |
| **5. Column Aggregation**                   | Medium     | High (Bursts)    | Low           | **Medium** |
| **6. Fast-Path Flag**                       | Low        | Low              | Low           | **Low**    |
| **7. Deferred Light Set (Queue Buffering)** | High       | Medium           | Low           | **Low**    |

**Recommendation:** Implement **1, 2, and 3** first. They solve the three biggest bottlenecks: redundant backtracking, memory write bandwidth, and branch misprediction. Optimization 4 is vital if you increase world height significantly (e.g., to 256 or 512).
