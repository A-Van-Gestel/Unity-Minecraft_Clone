# Design Document: Infinite World Storage & Serialization Architecture

**Version:** 1.6
**Date:** 2026-02-15  
**Status:** Implemented (Stable)  
**Target:** Unity 6.3 (Mono Backend)

## 1. Executive Summary

This document outlines the architectural overhaul required to transition the voxel engine from a fixed-size, memory-resident world to a scalable, infinite world system backed by efficient disk storage.

The core of this transition involves:

1. **Data Structure Migration:** Replacing the fixed `Chunk[,]` array with a dynamic Coordinate-Map system.
2. **Region-Based Storage:** Implementing a file format inspired by Minecraft's Anvil format to group chunks ($32\times32$) into single files.
3. **Global State Persistence:** Saving player inventory, capabilities (flying/noclip), and pending voxel modifications/lighting updates that target unloaded chunks.
4. **Custom Binary Serialization:** Abandoning `BinaryFormatter` for a high-performance, versioned binary writer/reader with **LZ4 Compression**.
5. **Asynchronous I/O Pipeline:** Ensuring saving and loading never stalls the Main Thread.
6. **Lighting State Preservation:** Critical system to prevent "black spots" by preserving lighting calculation state across save/load cycles.

---

## 2. Core Architecture Changes

### 2.1. The World Container (`WorldData`)

**Previous:** `Dictionary<Vector2Int, ChunkData>` existed but was secondary to `Chunk[,]` in `World`.  
**Current:** The `Dictionary` is now the authoritative source of truth for **loaded** data.

* **Removed:** `Chunk[,] chunks` in `World.cs`.
* **Implemented:** `Dictionary<Vector2Int, ChunkData>` for data storage, `Dictionary<ChunkCoord, Chunk>` for active game objects.
* **Access Pattern:** `WorldData.RequestChunk(coord)` → checks memory → checks disk → generates if needed.

**Implementation Note:** The coordinate system uses `Vector2Int` where `.x` = chunk's X world coordinate and `.y` = chunk's Z world coordinate (not Y height).

### 2.2. The Coordinator (`ChunkStorageManager`)

A subsystem responsible for the lifecycle of chunk data across memory, disk, and generation.

**Lifecycle Flow:**

1. **Memory Check:** `WorldData.Chunks.TryGetValue(coord)` - instant return if loaded.
2. **Disk Check:** `ChunkStorageManager.LoadChunkAsync(coord)` - async load from region file.
3. **Generation:** `WorldJobManager.ScheduleGeneration(coord)` - creates new chunk if not found.

**Implementation Details:**

* Uses `ConcurrentDictionary<Vector2Int, Lazy<RegionFile>>` for thread-safe region file access.
* Region files are opened lazily and cached for the session.
* All disk I/O happens on background threads via `Task.Run()`.

**File Location:** `Assets/Scripts/Serialization/ChunkStorageManager.cs`

### 2.3. The Editor "Volatile" Mode

Implemented to allow testing without corrupting production saves.

* **Production Saves:** `Application.persistentDataPath/Saves/{WorldName}/`
* **Editor Volatile Saves:** `Application.persistentDataPath/Editor_Temp_Saves/{WorldName}/`
* **Behavior:** When "Volatile Mode" is enabled in editor settings, saves go to temporary location.
* **Note:** Volatile saves persist between editor sessions unless manually deleted.

**Implementation:** Constructor parameter in `ChunkStorageManager`, `ModificationManager`, and `LightingStateManager`.

### 2.4. Global State & Modification Managers

Three separate managers handle state that exists outside individual chunk blobs:

#### A. ModificationManager

**Purpose:** Persists `VoxelMod`s targeting unloaded chunks (e.g., tree generation spilling into neighbors).

**File:** `pending_mods.bin`  
**Location:** `Assets/Scripts/Serialization/ModificationManager.cs`  
**Key Methods:**

* `AddPendingMod(ChunkCoord, VoxelMod)` - Queue modification for unloaded chunk
* `TryGetModsForChunk(ChunkCoord, out List<VoxelMod>)` - Retrieve and remove pending mods on load

**Critical:** Mods are applied in `World.LoadOrGenerateChunk()` immediately after `PopulateFromSave()`.

#### B. LightingStateManager

**Purpose:** Preserves lighting calculation state to prevent "black spots" bug.

**File:** `lighting_pending.bin`  
**Location:** `Assets/Scripts/Serialization/LightingStateManager.cs`  
**Saves:** Columns from `WorldData.SunlightRecalculationQueue` that belong to unloaded chunks.

**Key Methods:**

* `AddPending(ChunkCoord, HashSet<Vector2Int>)` - Save pending sunlight columns for a chunk
* `TryGetAndRemove(ChunkCoord, out HashSet<Vector2Int>)` - Restore pending columns on load

**Critical Design Decision:**  
This manager stores *local* column coordinates (0-15), not global positions. This reduces file size and makes data portable if chunk coordinates shift.

**Restoration Flow:**

1. Chunk loads from disk via `LoadChunkAsync()`
2. `World.LoadOrGenerateChunk()` checks `LightingStateManager`
3. If pending columns exist, they're converted to global coordinates and re-injected into `WorldData.SunlightRecalculationQueue`
4. Chunk's `HasLightChangesToProcess` flag is set
5. Lighting job is scheduled in the next Update cycle

**Why This Matters:**  
Without this manager, chunks unloaded during lighting propagation would permanently lose their "needs lighting" state, resulting in dark columns that never recover. This was the root cause of the "black spots" bug.

#### C. PlayerStateManager

**Purpose:** Serialize player position, rotation, capabilities, and inventory.

**File:** `level.dat` (JSON format)  
**Location:** Logic spread across `Player.cs`, `Toolbar.cs`, and `SaveSystem.cs`

**Implementation Status:** ⚠️ Partially implemented. Currently, saves position/rotation but inventory persistence needs completion.

---

## 3. Storage Format: The "Region" System

### 3.1. Region Logic

* **Region Size:** $32 \times 32$ Chunks (1024 Chunks per file)
* **Naming Convention:** `r.{regionX}.{regionZ}.bin`
* **Coordinate Mapping:**
    - `regionX = Floor(chunkX / 32)`
    - `regionZ = Floor(chunkZ / 32)`
    - Local chunk index: `(chunkX % 32) + (chunkZ % 32) * 32`

**Example:** Chunk at world coordinates (50, 45) → Region (1, 1), local index 18+13*32 = 434

### 3.2. File Structure (Binary)

| Byte Offset     | Size | Description                                                                                      |
|:----------------|:-----|:-------------------------------------------------------------------------------------------------|
| **0 - 4095**    | 4KB  | **Location Table:** 1024 entries (uint: offset, ushort: length, byte: algorithm, byte: reserved) |
| **4096 - 8191** | 4KB  | **Timestamp Table:** 1024 long entries (DateTime.Ticks)                                          |
| **8192...**     | Var  | **Chunk Data Payload:** Compressed binary blobs                                                  |

**Implementation Detail:**  
Each location table entry is 8 bytes:

* 4 bytes: Offset to chunk data in file
* 2 bytes: Compressed data length
* 1 byte: Compression algorithm (0=None, 1=GZip, 2=LZ4)
* 1 byte: Reserved for future use

**File Location:** `Assets/Scripts/Serialization/RegionFile.cs`

### 3.3. Compression Support

**Architecture:** Abstraction via `CompressionFactory` to support multiple algorithms transparently.

* **Factory:** `Assets/Scripts/Serialization/CompressionFactory.cs`
* **Default:** **LZ4** (High performance)
* **Supported Algorithms:**
    * `None (0)`: Raw bytes. Useful for debugging size overhead.
    * `GZip (1)`: High compression ratio, slower speed. Uses .NET `DeflateStream`.
    * `LZ4 (2)`: Low compression ratio, extreme speed. Uses `NativeCompressions` library (Native C++ bindings).

**Implementation Details:**

* The Region File Header stores the algorithm ID for each chunk individually.
* This allows mixing compression types within the same world (e.g., migrating from GZip to LZ4 incrementally).
* `ChunkSerializer` requests a stream from `CompressionFactory`, which handles the wrapping (and disposal) of the underlying compression stream.
* **Safety:** `CompressionFactory` includes a robust `IsLZ4Available` check to fallback to GZip if the native DLL is missing, preventing data loss.

**Performance Profile (LZ4):**

* **Save Time:** ~0.15ms per chunk (vs ~4ms GZip)
* **Load Time:** ~0.2ms per chunk (vs ~3ms GZip)

### 3.4. Cubic Chunks Compatibility

Currently, stores the *entire column* as a single blob. Since `ChunkData` is segmented into `ChunkSection[8]`, the format writes sections sequentially with a bitmask indicating which sections contain data.

**Future-Proofing:** Empty sections are skipped entirely (only bitmask bit set). This allows future transition to per-section loading without breaking existing saves.

---

## 4. Serialization Data Model

### 4.1. World Meta Data (`level.dat`)

JSON file at save folder root containing world metadata and player state.

**File Location:** `{SavePath}/level.dat`  
**Format:** UTF-8 JSON

```json
{
  "Version": 1,
  "WorldName": "My World",
  "Seed": 12345,
  "CreationDate": 638400000000000000,
  "LastPlayed": 638400000000000000,
  "WorldState": {
    "TimeOfDay": 0.5
  },
  "Player": {
    "Position": {
      "x": 10.5,
      "y": 70.0,
      "z": -5.5
    },
    "Rotation": {
      "x": 0.0,
      "y": 90.0,
      "z": 0.0
    },
    "Capabilities": {
      "IsFlying": false,
      "IsNoclipping": false
    },
    "Inventory": [
      {
        "Slot": 0,
        "ID": 14,
        "Count": 64
      },
      {
        "Slot": 1,
        "ID": 3,
        "Count": 12
      }
    ],
    // Null if empty
    "CursorItem": {
      "ID": 5,
      "Count": 64,
      "OriginSlot": 2
    }
  }
}
```

**Implementation:** `SaveSystem.cs` writes this file on world exit and auto-save intervals.

### 4.2. Chunk Blob Format (Inside Region File)

**Compression Layer:** Applied to entire blob before storage  
**Serializer:** `ChunkSerializer.cs` using `BinaryWriter/BinaryReader`

**Version 1 Structure:**

```
┌─────────────────────────────────────────┐
│ byte:    Chunk Format Version (1)       │
├─────────────────────────────────────────┤
│ int:     Chunk X Coordinate             │
│ int:     Chunk Z Coordinate             │
├─────────────────────────────────────────┤
│ bool:    NeedsInitialLighting Flag      │ ← CRITICAL for black spot prevention
├─────────────────────────────────────────┤
│ byte[256]: HeightMap (16x16)            │
├─────────────────────────────────────────┤
│ int:     Section Bitmask                │ ← Bits 0-7 indicate which sections exist
├─────────────────────────────────────────┤
│ FOR EACH SECTION (if bit set):          │
│   ┌───────────────────────────────────┐ │
│   │ byte:   Section Version (1)       │ │
│   │ ushort: NonAirCount               │ │
│   │ uint[4096]: Voxel Data (packed)   │ │ ← 16x16x16 = 4096 voxels
│   └───────────────────────────────────┘ │
├─────────────────────────────────────────┤
│ Lighting Queues:                        │
│   ┌───────────────────────────────────┐ │
│   │ int: Sunlight Queue Count         │ │
│   │ FOR EACH NODE:                    │ │
│   │   int: Position.x                 │ │
│   │   int: Position.y                 │ │
│   │   int: Position.z                 │ │
│   │   byte: OldLightLevel             │ │
│   └───────────────────────────────────┘ │
│   ┌───────────────────────────────────┐ │
│   │ int: Blocklight Queue Count       │ │
│   │ (same structure as sunlight)      │ │
│   └───────────────────────────────────┘ │
└─────────────────────────────────────────┘
```

**Critical Implementation Notes:**

1. **NeedsInitialLighting Flag:**  
   Must be saved and restored. If a chunk is unloaded before initial lighting completes, this flag ensures it will be re-lit on reload. Without this, chunks appear completely dark.

2. **Lighting Queues:**  
   Represent in-progress BFS propagation. Saving these allow lighting calculations to resume exactly where they left off. Queue nodes contain the voxel position and the *old* light level (needed for removal propagation).

3. **Active Voxels Not Saved:**  
   Fluids, grass, and other "active" blocks are recalculated via `Chunk.OnDataPopulated()` on load. This reduces save file size by ~10% and ensures behavior updates apply retroactively.

**File Location:** `Assets/Scripts/Serialization/ChunkSerializer.cs`

### 4.3. Pending Mods Format (`pending_mods.bin`)

Binary file storing voxel modifications targeting unloaded chunks.

**Structure:**

```
int:  ChunkCount
FOR EACH CHUNK:
  int:  ChunkX
  int:  ChunkZ
  int:  ModCount
  FOR EACH MOD:
    int:    GlobalPosition.x
    int:    GlobalPosition.y
    int:    GlobalPosition.z
    ushort: BlockID
    byte:   Orientation
    byte:   FluidLevel
```

**Use Case:** Structure generation (eg: trees) that extends into neighboring chunks that haven't loaded yet.

**File Location:** `Assets/Scripts/Serialization/ModificationManager.cs`

### 4.4. Pending Lighting Format (`lighting_pending.bin`)

Binary file storing columns that need sunlight recalculation.

**Structure:**

```
int:  ChunkCount
FOR EACH CHUNK:
  int:  ChunkX
  int:  ChunkZ
  int:  ColumnCount
  FOR EACH COLUMN:
    byte: LocalX (0-15)
    byte: LocalZ (0-15)
```

**Critical Implementation Detail:**  
Coordinates are stored as *local* (0-15) to keep data portable and reduce file size. They're converted to global coordinates on load.

**Why Bytes?**  
Since columns are always 0-15, using `byte` instead of `int` saves 75% space per column. With hundreds of pending columns, this matters.

**File Location:** `Assets/Scripts/Serialization/LightingStateManager.cs`

---

## 5. The I/O Pipeline (Threading)

All disk I/O is asynchronous to prevent frame hitches.

### 5.1. The Save Pipeline

**Trigger Points:**

1. Chunk exits load distance (`World.UnloadChunks()`)
2. Auto-save interval (configurable)
3. World shutdown (`OnApplicationQuit()`)

**Save Flow:**

```
Main Thread                         Background Thread
─────────────────────────────────────────────────────────
1. Determine chunk needs save
2. Check if modified
3. Copy chunk data to buffer    →  4. Receive buffer
   (fast, stack allocated)          5. Serialize to bytes
                                    6. Compress (if enabled)
                                    7. Lock region file
                                    8. Write to disk
                                    9. Update timestamp
                                    10. Return buffer to pool
```

**Memory Management:**  
Uses `SerializationBufferPool` to recycle byte arrays, preventing GC pressure.

**Thread Safety:**  
Region files use `lock(this)` on write operations. Multiple chunks in different regions can save concurrently.

**Performance Target:**  
< 1ms main thread impact per chunk save (measured: ~0.3ms)

### 5.2. The Load Pipeline

**Trigger:** `World.LoadOrGenerateChunk()` determines chunk doesn't exist in memory

**Load Flow:**

```
Main Thread                         Background Thread
─────────────────────────────────────────────────────────
1. Check memory (miss)
2. Request async load           →  3. Open region file
                                    4. Read chunk blob
                                    5. Decompress
   [Continue frame, no blocking]    6. Deserialize
                                    7. Return ChunkData
8. Receive callback
9. data.PopulateFromSave(loaded)
10. Apply pending mods
11. Restore lighting queues     ←  12. All lighting state restored
12. Schedule lighting job (if needed)
13. Schedule meshing job
```

**Critical Restoration Steps:**

**Step 9 - PopulateFromSave:**

```csharp
// In ChunkData.cs
public void PopulateFromSave(ChunkData loadedData)
{
    this.heightMap = loadedData.heightMap;
    this.sections = loadedData.sections;
    
    // CRITICAL: Restore the flag that triggers initial lighting
    this.NeedsInitialLighting = loadedData.NeedsInitialLighting;
    
    // Copy lighting queues
    foreach(var node in loadedData.SunlightBfsQueue) 
        this.AddToSunLightQueue(node.Position, node.OldLightLevel);
    foreach(var node in loadedData.BlocklightBfsQueue) 
        this.AddToBlockLightQueue(node.Position, node.OldLightLevel);
    
    // Transfer pending flags
    if (loadedData.HasLightChangesToProcess) 
        this.HasLightChangesToProcess = true;
    
    this.IsPopulated = true;
}
```

**Step 11 - Restore Lighting Queues:**

```csharp
// In World.LoadOrGenerateChunk()
if (LightingStateManager.TryGetAndRemove(coord, out HashSet<Vector2Int> localCols))
{
    // Convert local (0-15) to global coordinates
    HashSet<Vector2Int> globalCols = new HashSet<Vector2Int>();
    foreach(var lCol in localCols)
    {
        globalCols.Add(new Vector2Int(lCol.x + pos.x, lCol.y + pos.y));
    }
    
    // Re-inject into global queue
    if (worldData.SunlightRecalculationQueue.ContainsKey(pos))
        worldData.SunlightRecalculationQueue[pos].UnionWith(globalCols);
    else
        worldData.SunlightRecalculationQueue[pos] = globalCols;
    
    data.HasLightChangesToProcess = true;
}
```

**Performance Target:**  
< 5ms from disk read to chunk ready for meshing (measured: ~3ms typical)

---

## 6. Lighting State Preservation System

### 6.1. The Problem

Lighting propagation is multi-frame and cross-chunk. A chunk can be in several states:

1. **Newly Generated** - Needs initial sunlight calculation
2. **Lighting In Progress** - Job running, results not yet applied
3. **Lighting Awaiting Neighbors** - Waiting for adjacent chunks to load
4. **Cross-Chunk Propagation** - Light from this chunk affecting neighbors

If a chunk unloads during states 1-4, critical data is lost, resulting in "black spots" on reload.

### 6.2. The Solution: Multi-Layered State Tracking

**Layer 1: Per-Chunk Flags (Transient)**

```csharp
// In ChunkData.cs
[NonSerialized]
public bool NeedsInitialLighting = false;        // Needs first lighting pass

[NonSerialized]
public bool HasLightChangesToProcess = false;    // Has pending BFS work

[NonSerialized]
public bool IsAwaitingMainThreadProcess = false; // Job done, waiting for apply
```

**Layer 2: Per-Chunk Queues (Serialized)**

```csharp
// In ChunkData.cs - These ARE saved to disk
public Queue<LightQueueNode> SunlightBfsQueue;
public Queue<LightQueueNode> BlocklightBfsQueue;
```

**Layer 3: Global Queue (LightingStateManager)**

```csharp
// In WorldData.cs - NOT automatically saved
public Dictionary<Vector2Int, HashSet<Vector2Int>> SunlightRecalculationQueue;

// LightingStateManager extracts and persists relevant entries on unload
```

### 6.3. Unload Safety Protocol

**In World.UnloadChunks():**

```csharp
// Step 1: Check for active work
bool isJobRunning = JobManager.generationJobs.ContainsKey(coord)
                    || JobManager.meshJobs.ContainsKey(coord)
                    || JobManager.lightingJobs.ContainsKey(coord);

bool isProcessingLight = data.IsAwaitingMainThreadProcess || 
                        data.HasLightChangesToProcess;

if (isJobRunning || isProcessingLight)
{
    continue; // Skip unload - work in progress
}

// Step 2: Rescue orphaned global queue data
if (worldData.SunlightRecalculationQueue.TryGetValue(pos, out var globalCols))
{
    if (globalCols != null && globalCols.Count > 0)
    {
        // Convert to local coordinates
        HashSet<Vector2Int> localCols = new HashSet<Vector2Int>();
        foreach(var gCol in globalCols)
        {
            localCols.Add(new Vector2Int(gCol.x - pos.x, gCol.y - pos.y));
        }
        
        // Persist to LightingStateManager
        LightingStateManager.AddPending(coord, localCols);
    }
    
    worldData.SunlightRecalculationQueue.Remove(pos);
}

// Step 3: Save chunk (includes per-chunk queues and NeedsInitialLighting flag)
if (worldData.ModifiedChunks.Contains(data))
{
    _ = StorageManager.SaveChunkAsync(data);
}
```

**Critical:** This protocol ensures no lighting state is ever lost during unload.

### 6.4. Load Restoration Protocol

**In World.LoadOrGenerateChunk():**

```csharp
ChunkData loaded = await StorageManager.LoadChunkAsync(pos);
if (loaded != null)
{
    // 1. Transfer all data including lighting queues and flags
    data.PopulateFromSave(loaded);
    
    // 2. Restore global queue columns
    if (LightingStateManager.TryGetAndRemove(coord, out var localCols))
    {
        // Re-inject into global queue (see code in section 5.2)
        data.HasLightChangesToProcess = true;
    }
    
    // 3. Check if chunk needs initial lighting
    if (data.NeedsInitialLighting)
    {
        if (AreNeighborsDataReady(coord))
        {
            // Trigger lighting immediately
            data.RecalculateSunLightLight();
            data.NeedsInitialLighting = false;
            
            if (data.Chunk != null)
            {
                JobManager.ScheduleLightingUpdate(data.Chunk);
            }
        }
        else
        {
            // Defer until neighbors load
            // ProcessPendingInitialLighting() in Update will retry
        }
    }
}
```

### 6.5. Cross-Chunk Propagation Handling

**The Vanishing Neighbor Problem:**  
During lighting job execution, neighbor chunks may unload. Light updates targeting those chunks must not be dropped.

**Solution - Batched Deferred Updates:**

**In WorldJobManager.ProcessLightingJobs():**

```csharp
// Track all vanishing neighbor updates
Dictionary<ChunkCoord, HashSet<Vector2Int>> vanishingNeighborUpdates = 
    new Dictionary<ChunkCoord, HashSet<Vector2Int>>();

foreach (LightModification mod in jobData.Mods)
{
    Vector2Int neighborCoord = worldData.GetChunkCoordFor(mod.GlobalPosition);
    ChunkData neighborChunk = worldData.RequestChunk(neighborCoord, false);

    if (neighborChunk == null || !neighborChunk.IsPopulated) 
    {
        // Neighbor unloaded - batch for LightingStateManager
        ChunkCoord coord = new ChunkCoord(neighborCoord);
        int localX = mod.GlobalPosition.x - neighborCoord.x;
        int localZ = mod.GlobalPosition.z - neighborCoord.y;
        
        if (!vanishingNeighborUpdates.ContainsKey(coord))
        {
            vanishingNeighborUpdates[coord] = new HashSet<Vector2Int>();
        }
        vanishingNeighborUpdates[coord].Add(new Vector2Int(localX, localZ));
        
        continue;
    }

    // Apply to loaded neighbor (existing logic)
}

// Batch save all vanishing neighbor updates
foreach (var kvp in vanishingNeighborUpdates)
{
    _world.LightingStateManager.AddPending(kvp.Key, kvp.Value);
}
```

**Performance Impact:**  
Batching reduces what was previously 10,000+ individual save operations down to ~10-50 per frame, eliminating frame hitches at chunk boundaries.

---

## 7. World Boundary Considerations

### 7.1. The Edge Chunk Problem

With a world size limit (e.g., 100×100 chunks), chunks at boundaries (x=0, x=99, z=0, z=99) have fewer than 4 cardinal neighbors.

**Standard Neighbor Check (Broken at Edges):**

```csharp
// This fails for edge chunks
bool AreNeighborsDataReady(ChunkCoord coord)
{
    // Checks North, South, East, West
    // Returns false if ANY neighbor missing
    // Edge chunks will NEVER pass!
}
```

**Result Without Fix:**  
Edge chunks with `NeedsInitialLighting=true` never get lit → permanent black borders.

### 7.2. The Fix: Boundary-Aware Neighbor Check

**In World.cs:**

```csharp
/// <summary>
/// Checks if all of a chunk's cardinal neighbors that EXIST IN THE WORLD 
/// have finished generating. Out-of-bounds neighbors are considered "ready".
/// </summary>
private bool AreNeighborsDataReady(ChunkCoord coord)
{
    ChunkCoord[] neighbors = new ChunkCoord[]
    {
        new ChunkCoord(coord.X, coord.Z + 1), // North
        new ChunkCoord(coord.X, coord.Z - 1), // South
        new ChunkCoord(coord.X + 1, coord.Z), // East
        new ChunkCoord(coord.X - 1, coord.Z)  // West
    };

    foreach (ChunkCoord neighborCoord in neighbors)
    {
        // NEW: Skip out-of-bounds neighbors
        if (!IsChunkInWorld(neighborCoord))
        {
            continue; // Treat as "ready" - it will never exist
        }

        Vector2Int neighborPos = new Vector2Int(
            neighborCoord.X * VoxelData.ChunkWidth,
            neighborCoord.Z * VoxelData.ChunkWidth
        );

        if (!worldData.Chunks.TryGetValue(neighborPos, out ChunkData neighborData))
        {
            return false; // In-world neighbor not loaded yet
        }

        if (!neighborData.IsPopulated || 
            JobManager.generationJobs.ContainsKey(neighborCoord))
        {
            return false; // Neighbor not ready
        }
    }

    return true; // All in-world neighbors ready
}
```

**Edge Case Handling:**  
An edge chunk at (0, 50) only waits for North (0,51), South (0,49), and East (1,50). The West neighbor (-1,50) is out-of-bounds and automatically "ready".

### 7.3. Performance Consideration

**Symptom:** High "Neighbors not ready" log counts near world edges.

**Why:** Players flying near boundaries repeatedly load/unload edge chunks. Each has 1-2 missing neighbors, causing deferred lighting.

**Solution:** This is expected behavior. Edge chunks will light once their in-world neighbors are ready. No fix needed, just remove verbose logging in production.

---

## 8. UI & Management

### 8.1. Main Menu Flow

**Implemented:**

1. **Title Screen:** [Play] [Settings] [Quit]
2. **World Selector:**
    * Scans `Application.persistentDataPath/Saves/`
    * Reads `level.dat` for world name, seed, last played date
    * Displays world list with thumbnails (future feature)
    * [Create New World] / [Load Selected] / [Delete]

**File Location:** `Assets/Scripts/UI/WorldSelectMenu.cs`

### 8.2. In-Game Saving

**Auto-Save (To Implement):**

* Interval: Configurable (default: 5 minutes)
* Saves all modified chunks + global state
* Non-blocking (async)

**Manual Save:**

* Currently: Automatic on world exit
* Player Controlled: F4 quick-save hotkey

---

## 9. Performance Analysis

### 9.1. Achieved Metrics

| Operation                   | Target  | Measured | Status |
|-----------------------------|---------|----------|--------|
| Chunk Save (Main Thread)    | < 1ms   | ~0.3ms   | ✅      |
| Chunk Load (Async)          | < 5ms   | ~3ms     | ✅      |
| Lighting Restoration        | < 2ms   | ~1ms     | ✅      |
| Memory (Active Chunks)      | < 1000  | ~500-800 | ✅      |
| File Size (Per Chunk, GZip) | < 100KB | ~50KB    | ✅      |

### 9.2. Bottleneck Analysis

**Previous Bottleneck (Fixed):**  
"Vanishing Neighbor" updates were logged individually, causing 10,000+ log writes per frame at chunk boundaries.

**Fix:**  
Batching all updates for a single neighbor chunk into one `AddPending()` call reduced log spam by 99.5% and eliminated frame drops.

### 9.3. Memory Management

**Chunk Lifecycle:**

1. **Generation:** ~2ms CPU, allocates ~20KB
2. **Lighting:** ~3ms CPU, allocates ~10KB temporary buffers
3. **Meshing:** ~5ms CPU, allocates mesh data
4. **Active:** ~100KB total per chunk (data + mesh + chunk border visualization mesh)
5. **Unload:** Mesh destroyed, Chunk GameObject destroyed, data saved
6. **Disk:** ~50KB compressed

**Buffer Pooling:**  
`SerializationBufferPool` reuses byte arrays for save operations, preventing GC spikes.

---

## 10. Known Issues & Resolutions

### 10.1. Black Spots Bug ✅ RESOLVED

**Symptom:** Chunks reloaded after rapid movement showed completely dark (light level 0) columns.

**Root Cause:** Three separate issues:

1. `NeedsInitialLighting` flag not preserved during `PopulateFromSave()`
2. Global `SunlightRecalculationQueue` entries lost when chunks unloaded
3. Cross-chunk light propagation dropped when target neighbor unloaded

**Resolution:**

1. Fixed `ChunkData.PopulateFromSave()` to copy all lighting flags
2. Implemented `LightingStateManager` to persist global queue entries
3. Added batched deferred updates in `ProcessLightingJobs()`

**Verification:**  
Extensive testing with circular flying patterns (worst-case scenario). No black spots observed in 30+ minute sessions.

**Files Modified:**
* `ChunkData.cs` - PopulateFromSave()
* `LightingStateManager.cs` - New file
* `World.cs` - UnloadChunks(), LoadOrGenerateChunk()
* `WorldJobManager.cs` - ProcessLightingJobs()

### 10.2. Logging Spam ✅ RESOLVED

**Symptom:** 180MB log files with 10,000+ duplicate messages in minutes.

**Root Cause:** Each individual light modification to an unloaded chunk was logged separately.

**Resolution:**  
Batched all modifications per-chunk before logging, reducing messages by 99.5%.

**Example:**  
Before: 1000 messages of `[VANISHING NEIGHBOR] Chunk (50,50) column (0,0)`  
After: 1 message of `[LIGHTING] Saved updates for 1 unloaded chunks (16 columns)`

### 10.3. World Edge Lighting ⚠️ REQUIRES TESTING

**Symptom:** Chunks at world boundaries may have delayed lighting due to missing neighbors.

**Status:** Fix implemented but needs extended testing with bounded worlds.

**Fix:** Modified `AreNeighborsDataReady()` to treat out-of-bounds chunks as "ready".

**Verification Needed:**  
Load a world, fly to edge (0,0) or (99,99), verify no permanent dark strips.

### 10.4. Chunk Regeneration on Reload (Data Loss) ✅ RESOLVED

**Symptom:** Modifications (e.g., player building) in the initial starting area were lost after quitting and reloading. The chunks appeared to regenerate from seed.

**Root Cause:**

1. **Race Condition:** `EnsureChunkExists` in `WorldData` was implicitly scheduling a Generation Job. In `StartWorld`, this job raced against the async `LoadChunk` task. Often, the Generation Job would finish *after* the disk load, overwriting the saved data with fresh terrain.
2. **Flush Failure:** `OnApplicationQuit` was writing chunk bytes to the `FileStream` but not explicitly Disposing/Flushing the `StorageManager`. This meant the Region File's **Header Table (Offsets)** was not updated on disk. On reload, the game read the old offsets (0), assumed
   the chunk didn't exist, and regenerated it.

**Resolution:**

1. Refactored `EnsureChunkExists` to only create the data placeholder, moving the responsibility of scheduling generation to `LoadOrGenerateChunk`.
2. Added explicit `StorageManager.Dispose()` in `OnApplicationQuit` to force-flush file buffers to physical disk.

### 10.4. Seed Mismatch ✅ RESOLVED

**Symptom:** New worlds were generating with Seed 0 for the first batch of chunks, then switching to the correct random seed for later chunks.

**Root Cause:** `VoxelData.Seed` (static) was being assigned *after* `WorldData` initialization and the first batch of Generation Jobs had already been scheduled.

**Resolution:** Moved `VoxelData.Seed` assignment to the very top of `StartWorld`.


---

## 11. Future Enhancements

### 11.1. Short-Term

1. **Chunk Prioritization (partially implemented)**
    * **Current:** Spiral load around player, starting from player and spiraling further and further away until load distance has been reached.
    * Load chunks nearest to player first ← Implemented
    * Priority queue based on distance + direction of movement
    * Reduces perceived loading time

2. **ProcessPendingInitialLighting in Update**
    * Currently, chunks with "neighbors not ready" wait indefinitely
    * Add periodic retry system in `World.Update()`
    * Ensures edge chunks eventually light

### 11.2. Long-Term

1. **Incremental Saves**
    * Only save changed sections, not entire column
    * Reduces disk writes by ~70%
    * Requires section-level dirty tracking

2. **Chunk Streaming**
    * Background thread pre-loads chunks ahead of player movement
    * Predicts path based on velocity
    * Seamless infinite world experience

3. **Region File Defragmentation**
    * Compact region files to remove deleted chunks
    * Run during world load or as background task

---

## 12. Testing & Validation

### 12.1. Test Scenarios

**Scenario 1: Rapid Movement (Circular Flying)**

* Description: Fly in circles to rapidly load/unload chunks
* Purpose: Stress test lighting state preservation
* Pass Criteria: No black spots, no crashes, log file < 10MB
* Status: ✅ PASS

**Scenario 2: World Boundaries**

* Description: Fly to world edge, build, save, reload
* Purpose: Verify edge chunk lighting works
* Pass Criteria: No permanent dark borders
* Status: ⚠️ NEEDS TESTING

**Scenario 3: Long Session**

* Description: 60+ minutes of normal gameplay
* Purpose: Check for memory leaks, save file growth
* Pass Criteria: Memory stable, save files reasonable size
* Status: ⚠️ NEEDS TESTING

**Scenario 4: Crash Recovery**

* Description: Force-quit during chunk generation
* Purpose: Verify no save corruption
* Pass Criteria: World loads correctly, no missing chunks
* Status: ⚠️ NEEDS TESTING

### 12.2. Regression Test Checklist

Before each major release:

- [ ] Circular flying test (10 minutes minimum)
- [ ] Edge chunk lighting verification
- [ ] Save file size check (< 100KB per chunk average)
- [ ] Memory profiler (no leaks over 30 minutes)
- [ ] Log file review (no errors, warnings reasonable)

---

## 13. Implementation Checklist

### ✅ Completed

- [x] ChunkStorageManager
- [x] RegionFile system
- [x] ChunkSerializer (Version 1)
- [x] ModificationManager
- [x] LightingStateManager
- [x] Async save pipeline
- [x] Async load pipeline
- [x] NeedsInitialLighting preservation
- [x] Cross-chunk light propagation handling
- [x] Batched vanishing neighbor updates
- [x] World boundary neighbor checks
- [x] World selector UI
- [x] Volatile mode for editor
- [x] **LZ4 Compression Support**
- [x] **Seed Mismatch Fix**
- [x] **Quit/Flush Reliability Fix**

### ⚠️ Partial / In Progress

- [ ] PlayerStateManager (position saved, inventory saved but could be improved)
- [ ] Chunk prioritization system
- [ ] Save versioning / migration system
- [ ] ProcessPendingInitialLighting retry system

### 📋 Planned

- [ ] Increased region file write throughput
- [ ] Incremental section saves
- [ ] Chunk streaming/prediction
- [ ] Region file defragmentation

---

## 14. Appendix

### A. File Locations Quick Reference

| Component            | File Path                                              |
|----------------------|--------------------------------------------------------|
| ChunkSerializer      | `Assets/Scripts/Serialization/ChunkSerializer.cs`      |
| ChunkStorageManager  | `Assets/Scripts/Serialization/ChunkStorageManager.cs`  |
| RegionFile           | `Assets/Scripts/Serialization/RegionFile.cs`           |
| ModificationManager  | `Assets/Scripts/Serialization/ModificationManager.cs`  |
| LightingStateManager | `Assets/Scripts/Serialization/LightingStateManager.cs` |
| CompressionFactory   | `Assets/Scripts/Serialization/CompressionFactory.cs`   |
| WorldData            | `Assets/Scripts/Data/WorldData.cs`                     |
| ChunkData            | `Assets/Scripts/Data/ChunkData.cs`                     |
| World                | `Assets/Scripts/World.cs`                              |
| WorldJobManager      | `Assets/Scripts/WorldJobManager.cs`                    |

### B. Save File Structure Example

```
Saves/
└── My World/
    ├── level.dat                    (JSON metadata)
    ├── pending_mods.bin             (VoxelMod queue)
    ├── lighting_pending.bin         (Sunlight queue)
    └── Region/
        ├── r.0.0.bin                (1024 chunks)
        ├── r.0.1.bin
        ├── r.1.0.bin
        └── ...
```

### C. Glossary

**Chunk:** 16×128×16 voxel column. Basic unit of terrain.  
**Section:** 16×16×16 sub-division of a chunk. Used for empty section culling.  
**Region:** 32×32 chunk group stored in a single file.  
**Voxel:** Individual block in the world. Stored as packed `uint`.  
**ChunkCoord:** Chunk-space coordinates (not world voxel coordinates).  
**Lighting Queue:** BFS queue for light propagation algorithm.  
**Vanishing Neighbor:** Chunk that unloaded while a lighting job needed to update it.  
**Black Spots:** Visual artifact where chunks have 0 light level due to lost lighting state.

---

## Document History

* **v1.0** - Initial draft of architecture
* **v1.1** - Added region file format details
* **v1.2** - Expanded lighting state preservation
* **v1.3** - Documented async I/O pipeline
* **v1.4** - Status updated to "Implemented (Stable)"
* **v1.5** - Comprehensive update with all implementation details, bug resolutions, and future plans
* **v1.6** - Added LZ4 Compression implementation details and documented resolution of Chunk Regeneration bugs

---

**Last Updated:** 2026-02-15  
**Next Review:** Chunk prioritization or Defragmentation
