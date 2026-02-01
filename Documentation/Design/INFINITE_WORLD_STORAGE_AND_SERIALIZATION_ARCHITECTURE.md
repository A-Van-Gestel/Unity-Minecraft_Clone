# Design Document: Infinite World Storage & Serialization Architecture

**Version:** 1.3
**Status:** Draft
**Target:** Unity 6.2 (Mono Backend)

## 1. Executive Summary

This document outlines the architectural overhaul required to transition the voxel engine from a fixed-size, memory-resident world to a scalable, infinite world system backed by efficient disk storage.

The core of this transition involves:

1. **Data Structure Migration:** Replacing the fixed `Chunk[,]` array with a dynamic Coordinate-Map system.
2. **Region-Based Storage:** Implementing a file format inspired by Minecraft's Anvil format to group chunks ($32\times32$) into single files.
3. **Global State Persistence:** Saving player inventory, capabilities (flying/noclip), and pending voxel modifications/lighting updates that target unloaded chunks.
4. **Custom Binary Serialization:** Abandoning `BinaryFormatter` for a high-performance, versioned binary writer/reader.
5. **Asynchronous I/O Pipeline:** ensuring saving and loading never stalls the Main Thread.

---

## 2. Core Architecture Changes

### 2.1. The World Container (`WorldData`)

**Current:** `Dictionary<Vector2Int, ChunkData>` exists but is secondary to `Chunk[,]` in `World`.
**New:** The `Dictionary` becomes the authoritative source of truth for **loaded** data.

* **Removal:** `Chunk[,] chunks` in `World.cs` will be removed.
* **Replacement:** A `Dictionary<ChunkCoord, Chunk>` for active game objects and `Dictionary<ChunkCoord, ChunkData>` for data.
* **Access:** `GetChunk(ChunkCoord)` becomes the standard accessor.

### 2.2. The Coordinator (`ChunkStorageManager`)

A new subsystem responsible for the lifecycle of chunk data:

1. **Request:** World asks for Chunk `(X, Z)`.
2. **Check Memory:** Is it in `WorldData.ActiveChunks`? Return it.
3. **Check Disk:** Is it in the corresponding Region File? Load, Decompress, Return.
4. **Generate:** If not on disk, queue for `WorldJobManager` generation.

### 2.3. The Editor "Volatile" Mode

To satisfy the requirement of "known states" in the editor without destroying save data:

* **Production Saves:** `Application.persistentDataPath/Saves/{WorldName}/`
* **Editor Volatile Saves:** `Application.persistentDataPath/Editor_Temp_Saves/{WorldName}/`
    * When entering Play Mode in Editor (with "Volatile Mode" checked), the `Editor_Temp_Saves` folder is wiped or ignored.

### 2.4. Global State & Modification Manager

To handle state that exists outside of specific chunk blobs:

* **ModificationManager:** Replaces `World._modifications`. Handles `VoxelMod`s targeting chunks that don't exist yet. Persists to `pending_mods.bin`.
* **LightingStateManager:** Persists `SunlightRecalculationQueue` (columns waiting for neighbors) to `lighting_pending.bin`.
* **PlayerStateManager:** Hooks into `Player.cs` and `Toolbar.cs` to serialize inventory and capabilities to `level.dat`.

---

## 3. Storage Format: The "Region" System

Storing 100,000 individual `.chunk` files is disastrous for performance. We will group chunks into **Regions**.

### 3.1. Region Logic

* **Region Size:** $32 \times 32$ Chunks (1024 Chunks per file).
* **Naming Convention:** `r.{regionX}.{regionZ}.bin`

### 3.2. File Structure (Binary)

A Region file consists of a **Header Table** followed by **Variable Length Data**.

| Byte Offset     | Size | Description                                                                 |
|:----------------|:-----|:----------------------------------------------------------------------------|
| **0 - 4095**    | 4KB  | **Location Table:** 1024 entries. Mapping local chunk index to data offset. |
| **4096 - 8191** | 4KB  | **Timestamp Table:** 1024 entries. Last update time.                        |
| **8192...**     | Var  | **Chunk Data Payload:** Compressed binary blobs.                            |

### 3.3. Cubic Chunks Compatibility

We will store the *entire column* in the blob for now. Since `ChunkData` is already segmented into `ChunkSection[]`, the binary format will write sections sequentially. If a section is empty, we write a `0` byte flag and skip it. This ensures compatibility if we switch to loading
sections individually later.

---

## 4. Serialization Data Model

We will implement `ChunkSerializer` using `System.IO.BinaryWriter`.

### 4.1. World Meta Data (`level.dat`)

A lightweight JSON file at the root of the save folder. Includes Player State and Inventory.

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

### 4.2. Chunk Blob Format (Inside Region File)

1. **Compression:** GZip/Deflate.
2. **Structure:**
    * `byte` **Version**.
    * `int` **X**, `int` **Z**.
    * `byte` **HeightMap[]** (Length 256).
    * `int` **Section Bitmask** (Indicates which sections exist).
    * **Sections Data:** (Iterate through bitmask)
        * `ushort` **NonAirCount**.
        * `uint[]` **Voxels**.
    * *Note: `_activeVoxels` (Fluids/Grass) are NOT saved explicitly. They are recalculated via `Chunk.OnDataPopulated()` on load.*

### 4.3. Pending Mods Format (`pending_mods.bin`)

Stores structure generation (e.g., trees) spilling into unloaded chunks.

* `int` **Count** (Number of chunks).
* **Entries:** `int` ChunkX, `int` ChunkZ, `int` ModCount, **List<VoxelMod>**.

### 4.4. Pending Lighting Format (`lighting_pending.bin`)

Stores `WorldData.SunlightRecalculationQueue`. If the game closes while lighting is propagating or waiting for a neighbor, this ensures the column is marked dirty on reload.

* `int` **Count** (Number of chunks).
* **Entries:**
    * `int` **ChunkX**, `int` **ChunkZ**.
    * `int` **ColumnCount**.
    * **Columns:** `byte` LocalX, `byte` LocalZ (repeated ColumnCount times).

---

## 5. The I/O Pipeline (Threading)

Saving cannot happen on the main thread.

### 5.1. The Save Queue

1. **Trigger:** `World.cs` determines a chunk needs saving (Unload or Auto-save).
2. **Snapshot:** Fast-copy the `uint[]` arrays to a buffer on the Main Thread.
3. **Worker:** A generic `Task` or `Thread` picks up the buffer, locks the Region File, and writes data.

### 5.2. Loading

1. **Request:** `ChunkStorageManager.LoadChunkAsync(coord)`.
2. **Worker:** Reads Region, Decompresses, Deserializes.
3. **Main Thread:**
    1. Callback receives new `ChunkData`.
    2. `ModificationManager` applies `pending_mods.bin` data.
    3. `LightingStateManager` re-injects columns into `SunlightRecalculationQueue`.
4. **Result:** Chunk is ready for `Start` (Lighting/Meshing).

---

## 6. UI & Management

The current "Auto-load based on seed" logic must be replaced.

### 6.1. Main Menu Flow

1. **Title Screen:** [Play] [Settings] [Quit]
2. **Play Screen (World Selector):**
    * List view of folders in `Application.persistentDataPath/Saves/`.
    * Reads `level.dat` for metadata (Name/Date).
    * [Create New World] / [Load Selected] / [Delete].

---

## 7. Implementation Plan

### Phase 1: Data Structures & Serialization Logic

1. Create `ChunkSerializer.cs` & `RegionFile.cs`.
2. Create `ModificationManager.cs`: Handles `VoxelMod` and `SunlightQueue` persistence.
3. Create `ChunkStorageManager.cs`: Manages Region cache.

### Phase 2: Player & State Integration

1. Update `Player.cs`: Add `GetSaveData()` and `LoadSaveData(data)` methods (Position, Rotation, Flying, Noclip).
2. Update `Toolbar.cs`: Add methods to export/import `ItemStack[]`.
3. Update `SaveSystem.cs`: Logic to write `level.dat` gathering data from Player and Toolbar.

### Phase 3: World Integration

1. Modify `WorldData.cs`: Remove legacy load logic. Integrate `ChunkStorageManager`.
2. Modify `World.cs`: Update `CheckViewDistance` loop.
3. Implement "Volatile Mode".

### Phase 4: The UI

1. Create `WorldInfo` struct.
2. Build `WorldSelectList.cs` UI controller.

---

## 8. Performance Targets

* **Memory:** Max 500-1000 chunks active. Aggressive unloading.
* **Disk:** Region files prevent File System fragmentation.
* **CPU:** Serialization background threads. Main thread impact < 1ms.