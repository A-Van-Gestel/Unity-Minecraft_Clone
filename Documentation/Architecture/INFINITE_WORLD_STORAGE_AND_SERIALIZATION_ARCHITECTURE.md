# Design Document: Infinite World Storage & Serialization Architecture

**Version:** 1.7
**Date:** 2026-06-13  
**Status:** Implemented (Stable)  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

## 1. Executive Summary

This document outlines the architectural overhaul required to transition the voxel engine from a fixed-size, memory-resident world to a scalable, infinite world system backed by efficient disk storage.

The core of this transition involves:

1. **Data Structure Migration:** Replacing the fixed `Chunk[,]` array with a dynamic Coordinate-Map system.
2. **Region-Based Storage:** Implementing a file format inspired by Minecraft's Anvil format to group chunks ($32\times32$) into single files.
3. **Global State Persistence:** Saving player inventory, capabilities (flying/noclip), and pending voxel modifications/lighting updates that target unloaded chunks.
4. **Custom Binary Serialization:** Abandoning `BinaryFormatter` for a high-performance, versioned binary writer/reader with **LZ4 Compression**.
5. **Asynchronous I/O Pipeline:** Ensuring saving and loading never stalls the Main Thread.
6. **Lighting State Preservation:** Critical system to prevent "black spots" by preserving lighting calculation state across save/load cycles.
7. **Versioned Save Format with AOT Migration:** Every save carries a version number (`level.dat`, currently v11) and is upgraded offline before the world opens — see `AOT_WORLD_MIGRATION_SYSTEM.md`.

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
* The constructor takes the world's `saveVersion` (from `level.dat`) and resolves an `IRegionAddressCodec` via `RegionAddressCodec.ForVersion()`. All region address arithmetic (chunk voxel position → region file + local slot) goes through this codec — see Section 3.1.

**File Location:** `Assets/Scripts/Serialization/ChunkStorageManager.cs` (codec: `Assets/Scripts/Helpers/RegionAddressCodec.cs`)

### 2.3. The Editor "Volatile" Mode

Implemented to allow testing without corrupting production saves.

* **Production Saves:** `Application.persistentDataPath/Saves/{WorldName}/`
* **Editor Volatile Saves:** `Application.persistentDataPath/Editor_Temp_Saves/{WorldName}/`
* **Benchmark Saves:** `Application.persistentDataPath/Benchmark_Saves/{WorldName}/` — used when `WorldLaunchState.CurrentMode == RuntimeMode.Benchmark`; can be purged via `SaveSystem.ClearAllBenchmarks()`.
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

**Purpose:** Preserves lighting calculation state to prevent "black spots" and ghost-light bugs.

**Files:** `pending_lighting.bin` (sunlight columns), `pending_blocklight.bin` (blocklight modifications)  
**Location:** `Assets/Scripts/Serialization/LightingStateManager.cs`  
**Saves:**

* Columns from `WorldData.SunlightRecalculationQueue` that belong to unloaded chunks.
* Cross-chunk **blocklight** modifications (local position + RGB + removal flag) targeting unloaded chunks. A sunlight column recalc cannot restore RGB data — without this store, blocklight removals (broken lamps) and uplifts that crossed into an unloaded chunk were permanently lost, leaving ghost light baked into saved data (Bug 08, path 1).

**Key Methods:**

* `AddPending(ChunkCoord, HashSet<Vector2Int>)` - Save pending sunlight columns for a chunk
* `TryGetAndRemove(ChunkCoord, out HashSet<Vector2Int>)` - Restore pending columns on load
* `AddPendingBlocklight(ChunkCoord, Vector3Int, r, g, b, isRemoval)` - Save one pending blocklight modification (last write per voxel wins)
* `TryGetAndRemovePendingBlocklight(ChunkCoord, out Dictionary<Vector3Int, PendingBlocklightMod>)` - Restore pending blocklight mods on load
* `DiscardPendingBlocklight(ChunkCoord)` - Drop pending blocklight mods for freshly GENERATED chunks (initial lighting recomputes from current neighbor data, so they're obsolete)

**Critical Design Decisions:**

* This manager stores *local* coordinates (columns 0-15; voxel positions 0-15 / 0-127), not global positions. This reduces file size and makes data portable if chunk coordinates shift.
* `pending_blocklight.bin` is a **separate, self-describing file** (leading version byte) rather than an extension of `pending_lighting.bin` — adding it required no save-format migration (its absence simply means "nothing pending").

**Restoration Flow:**

1. Chunk loads from disk via `LoadChunkAsync()`
2. `World.LoadOrGenerateChunk()` checks `LightingStateManager`
3. If pending columns exist, they're converted to global coordinates and re-injected into `WorldData.SunlightRecalculationQueue`
4. If pending blocklight mods exist, each is replayed through `CrossChunkLightModApplier.ComputeBlocklight` against the loaded light data — exactly like the live cross-chunk apply path — writing the new value and enqueueing a BFS wake-up node
5. Chunk's `HasLightChangesToProcess` flag is set
6. Lighting job is scheduled in the next Update cycle

**Why This Matters:**  
Without this manager, chunks unloaded during lighting propagation would permanently lose their "needs lighting" state, resulting in dark columns that never recover. This was the root cause of the "black spots" bug.

#### C. PlayerStateManager

**Purpose:** Serialize player position, rotation, capabilities, and inventory.

**File:** `level.dat` (JSON format)  
**Location:** Logic spread across `Player.cs`, `Toolbar.cs`, `DragAndDropHandler.cs`, and `SaveSystem.cs` (DTOs in `Assets/Scripts/Serialization/SaveDataTypes.cs`)

**Implementation Status:** ✅ Implemented. Saves position, rotation, capabilities (flying/noclip), the full toolbar inventory, and the cursor-held item (`cursorItem`, null when empty).

---

## 3. Storage Format: The "Region" System

### 3.1. Region Logic

* **Region Size:** $32 \times 32$ Chunks (1024 Chunks per file)
* **Naming Convention:** `r.{regionX}.{regionZ}.bin`
* **Coordinate Mapping (V2+ codec):** chunk voxel-space origin → chunk index → region address:
    - `chunkX = voxelX / ChunkWidth` (and likewise for Z)
    - `regionX = Floor(chunkX / 32)`
    - `regionZ = Floor(chunkZ / 32)`
    - Local slot: `localX = chunkX % 32` (negative-corrected), index `localX + localZ * 32`

**Example:** Chunk index (50, 45) → Region (1, 1), local index 18+13*32 = 434

**Versioned Addressing (`RegionAddressCodec`):**  
All address arithmetic is encapsulated in `IRegionAddressCodec` implementations selected by save version (`RegionAddressCodec.ForVersion(saveVersion)` in `Assets/Scripts/Helpers/RegionAddressCodec.cs`):

* **V1 (save version 1):** Historical bug — treated the chunk's *voxel-space* position as a chunk index, so local slots only ever hit 0 or 16 and regions were 32 voxels wide. The V1 codec exists solely so migration tooling can decode old layouts; its encoder throws unless `allowLegacyEncoder: true` is passed explicitly.
* **V2 (save version ≥ 2):** Correct chunk-index addressing as described above. Worlds on V1 are automatically repacked by `Migration_v1_to_v2_RegionRepack` (see `AOT_WORLD_MIGRATION_SYSTEM.md`).

### 3.2. File Structure (Binary)

The file is divided into 4KB **sectors** (Anvil-style):

| Byte Offset     | Size | Description                                                                                    |
|:----------------|:-----|:-----------------------------------------------------------------------------------------------|
| **0 - 4095**    | 4KB  | **Location Table:** 1024 packed int entries — `(sectorStart << 8) \| sectorCount`              |
| **4096 - 8191** | 4KB  | **Reserved sector** (allocated but currently unused; originally intended as a timestamp table) |
| **8192...**     | Var  | **Chunk Records:** sector-aligned compressed binary blobs                                      |

**Implementation Details:**

* Each location table entry is a 4-byte int: the high 3 bytes are the chunk's starting **sector index**, the low byte is its **sector count** (max 255 sectors ≈ 1MB per chunk). An entry of 0 means "chunk not present".
* Each chunk record on disk is: `int totalLength` (payload + 1), `byte compressionAlgorithm` (0=None, 1=Deflate, 2=LZ4), the compressed payload, then zero-padding up to the next sector boundary.
* **Fragmentation management:** `RegionFile` keeps an in-memory sector usage map. On save it overwrites in place when the new size needs the same sector count, otherwise it frees the old sectors and finds the first contiguous free run (or appends at the end). There is no defragmentation pass yet.
* **Thread safety:** `FileStream` position is not thread-safe, so each `RegionFile` takes an exclusive `_fileLock` for **both** reads and writes. Chunks in different regions still save/load concurrently.

**File Location:** `Assets/Scripts/Serialization/RegionFile.cs`

### 3.3. Compression Support

**Architecture:** Abstraction via `CompressionFactory` to support multiple algorithms transparently.

* **Factory:** `Assets/Scripts/Serialization/CompressionFactory.cs`
* **Default:** **LZ4** (High performance)
* **Supported Algorithms:**
    * `None (0)`: Raw bytes. Useful for debugging size overhead.
    * `Deflate (1)`: High compression ratio, slower speed. Uses .NET `DeflateStream` (raw DEFLATE — no GZip header/CRC). Formerly named `GZip`; renamed for accuracy, on-disk value 1 is unchanged.
    * `LZ4 (2)`: Low compression ratio, extreme speed. Uses `NativeCompressions` library (Native C++ bindings).

**Implementation Details:**

* Each chunk record stores its own algorithm byte (immediately after the length header), allowing mixed compression types within the same world (e.g., migrating from Deflate to LZ4 incrementally).
* `ChunkSerializer` requests a stream from `CompressionFactory`, which handles the wrapping (and disposal) of the underlying compression stream.
* **Safety:** `CompressionFactory` includes a robust `IsLZ4Available` check to fallback to Deflate if the native DLL is missing, preventing data loss. (Reading LZ4 data without the DLL cannot fall back and throws.)
* **Safety:** Before decompressing LZ4, the factory validates the LZ4 Frame magic number (`04 22 4D 18`) — NativeCompressions' `LZ4Stream` spins forever on non-frame input instead of throwing (see `Documentation/Bugs/SERIALIZATION_BUGS.md` #03). Corrupt payloads become an `InvalidDataException`, which the deserializer turns into "warn → regenerate chunk".
* The per-world default algorithm comes from `World.settings.saveCompression`.

**Performance Profile (LZ4):**

* **Save Time:** ~0.15ms per chunk (vs ~4ms Deflate)
* **Load Time:** ~0.2ms per chunk (vs ~3ms Deflate)

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
  "version": 11,
  "worldName": "My World",
  "seed": 12345,
  "chunkHeight": 128,
  "chunkWidth": 16,
  "worldSizeInChunks": 100,
  "worldType": 1,
  "spawnPosition": {
    "_chunkX": 50,
    "_chunkZ": 50,
    "localPosition": {
      "x": 0.0,
      "y": 72.0,
      "z": 0.0
    }
  },
  "creationDate": 638400000000000000,
  "lastPlayed": 638400000000000000,
  "worldState": {
    "timeOfDay": 0.5
  },
  "player": {
    "position": {
      "x": 10.5,
      "y": 70.0,
      "z": -5.5
    },
    "rotation": {
      "x": 0.0,
      "y": 90.0,
      "z": 0.0
    },
    "capabilities": {
      "isFlying": false,
      "isNoclipping": false
    },
    "inventory": [
      {
        "slotIndex": 0,
        "itemID": 14,
        "amount": 64
      },
      {
        "slotIndex": 1,
        "itemID": 3,
        "amount": 12
      }
    ],
    // Null if empty
    "cursorItem": {
      "itemID": 5,
      "amount": 64,
      "originSlotIndex": 2
    }
  }
}
```

**Implementation:** `SaveSystem.SaveWorld()` serializes a `WorldSaveData` DTO (`Assets/Scripts/Serialization/SaveDataTypes.cs`) via Unity's `JsonUtility` and writes it on world exit and on manual saves. The `version` field (currently **11** — `SaveSystem.CURRENT_VERSION`) drives the AOT migration system; the per-version changelog lives as comments above that constant and in `AOT_WORLD_MIGRATION_SYSTEM.md`.

### 4.2. Chunk Blob Format (Inside Region File)

**Compression Layer:** Applied to entire blob before storage  
**Serializer:** `ChunkSerializer.cs` using `BinaryWriter/BinaryReader`

**Version 7 Structure (current — `ChunkSerializer.CURRENT_CHUNK_VERSION`):**

```
┌──────────────────────────────────────────────┐
│ byte:    Chunk Format Version (7)            │
├──────────────────────────────────────────────┤
│ int:     Chunk X Coordinate (voxel-space)    │
│ int:     Chunk Z Coordinate (voxel-space)    │
├──────────────────────────────────────────────┤
│ bool:    NeedsInitialLighting Flag           │ ← CRITICAL for black spot prevention
├──────────────────────────────────────────────┤
│ ushort[256]: HeightMap (16x16, 512 bytes)    │
├──────────────────────────────────────────────┤
│ int:     Section Bitmask                     │ ← Bits 0-7: section has voxels OR light
├──────────────────────────────────────────────┤
│ FOR EACH SECTION (if bit set):               │
│   byte: Section Type Flag, then:             │
│   ┌────────────────────────────────────────┐ │
│   │ 0x00 Voxels + uniform sky:             │ │
│   │   byte: sky level                      │ │
│   │   ushort: NonAirCount                  │ │
│   │   uint[4096]: Voxel Data (packed)      │ │
│   ├────────────────────────────────────────┤ │
│   │ 0x01 Voxels + full LightData:          │ │
│   │   ushort: NonAirCount                  │ │
│   │   uint[4096]: Voxel Data (packed)      │ │
│   │   ushort[4096]: LightData              │ │
│   ├────────────────────────────────────────┤ │
│   │ 0x02 Light-only + uniform sky:         │ │
│   │   byte: sky level (2 bytes total)      │ │
│   ├────────────────────────────────────────┤ │
│   │ 0x03 Light-only + full LightData:      │ │
│   │   ushort[4096]: LightData              │ │
│   └────────────────────────────────────────┘ │
├──────────────────────────────────────────────┤
│ Lighting Queues:                             │
│   ┌────────────────────────────────────────┐ │
│   │ int: Sunlight Queue Count              │ │
│   │ FOR EACH NODE (16 bytes):              │ │
│   │   int: Position.x                      │ │
│   │   int: Position.y                      │ │
│   │   int: Position.z                      │ │
│   │   byte: OldLightLevel                  │ │
│   │   byte: OldBlockR                      │ │
│   │   byte: OldBlockG                      │ │
│   │   byte: OldBlockB                      │ │
│   └────────────────────────────────────────┘ │
│   ┌────────────────────────────────────────┐ │
│   │ int: Blocklight Queue Count            │ │
│   │ (same node structure as sunlight)      │ │
│   └────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

**Format history** (each transition has a matching AOT migration step — see `AOT_WORLD_MIGRATION_SYSTEM.md`):

| Chunk version | World version | Change                                                                                                                    |
|:--------------|:--------------|:--------------------------------------------------------------------------------------------------------------------------|
| 1-2           | ≤ v2          | Original layout: section version byte + NonAirCount + packed voxels; light lived in the voxel `uint` (bits 16-23)         |
| 3             | v3            | Version bump to force re-lighting of 'IsEmpty' sections (`Migration_v2_to_v3_RestoreLighting`)                            |
| 4             | v6            | Voxel metadata bytes converted to schema-aware encoding (`Migration_v5_to_v6_LegacyToSchemaBased`)                        |
| 5             | v8            | Light queue nodes grew from 13 to 16 bytes (RGB blocklight: OldBlockR/G/B) (`Migration_v7_to_v8_RGBLightQueues`)          |
| 6             | v9            | Section version byte replaced by flag-based section type; persists `ushort[] LightData` (`Migration_v8_to_v9_...`)        |
| 7             | v10           | Legacy light bits stripped from voxels (bits 16-23 reserved/zeroed); uniform-sky-level flags 0x00/0x02/0x03 (`v9_to_v10`) |

**Critical Implementation Notes:**

1. **NeedsInitialLighting Flag:**  
   Must be saved and restored. If a chunk is unloaded before initial lighting completes, this flag ensures it will be re-lit on reload. Without this, chunks appear completely dark.

2. **Lighting Queues:**  
   Represent in-progress BFS propagation. Saving these allow lighting calculations to resume exactly where they left off. Queue nodes contain the voxel position, the *old* sunlight level, and the *old* RGB blocklight channels (needed for removal propagation).

3. **Active Voxels Not Saved:**  
   Fluids, grass, and other "active" blocks are recalculated via `Chunk.OnDataPopulated()` on load. This reduces save file size by ~10% and ensures behavior updates apply retroactively.

4. **Strict version check:**  
   The live serializer only reads `CURRENT_CHUNK_VERSION`. Older versions are upgraded offline by the AOT Migration Manager before the world opens; encountering an old version byte at runtime throws (world is corrupt or bypassed migration).

5. **Compact sections:**  
   The section bitmask covers sections with voxels **or** light. A fully-air section whose sky light is uniform (tracked at runtime in `ChunkData.SectionUniformSkyLevel`) serializes as just 2 bytes (flag 0x02 + sky level) — no `ChunkSection` allocation on load.

**File Location:** `Assets/Scripts/Serialization/ChunkSerializer.cs`

### 4.3. Pending Mods Format (`pending_mods.bin`)

Binary file storing voxel modifications targeting unloaded chunks.

**Structure (v5+ format):**

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
    byte:   Meta
```

Save version v5 collapsed the previous `(Orientation, FluidLevel)` byte pair into a single schema-aware `Meta` byte (see `PER_BLOCK_METADATA_SCHEMAS.md` §7.4 and `Migration_v4_to_v5_VoxelModMeta`). The runtime-only `ImmediateUpdate` and `Rule` fields are intentionally not persisted.

**Use Case:** Structure generation (eg: trees) that extends into neighboring chunks that haven't loaded yet.

**File Location:** `Assets/Scripts/Serialization/ModificationManager.cs`

### 4.4. Pending Lighting Format (`pending_lighting.bin`)

Binary file storing columns that need sunlight recalculation. (The filename was standardized from the older `lighting_pending.bin` by `Migration_v6_to_v7_SaveFormatExtensibility`.)

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

### 4.5. Pending Blocklight Format (`pending_blocklight.bin`)

Binary file storing cross-chunk blocklight modifications awaiting unloaded target chunks (Bug 08, path 1 — see Section 2.4.B). Unlike `pending_lighting.bin`, the file is **self-describing** via a leading version byte, so its layout can migrate in isolation; its absence simply means "nothing pending".

**Structure (file version 1):**

```
byte: File Version (1)
int:  ChunkCount
FOR EACH CHUNK:
  int:  ChunkX
  int:  ChunkZ
  int:  ModCount
  FOR EACH MOD:
    byte: LocalX (0-15)
    byte: LocalY (0-127)
    byte: LocalZ (0-15)
    byte: R (0-15)
    byte: G (0-15)
    byte: B (0-15)
    bool: IsRemoval
```

In memory, mods are keyed by local voxel position with last-write-wins semantics — every mod carries absolute target channel values, so a newer mod fully supersedes an older one for the same voxel.

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
Main Thread                            Background Thread (ThreadPool)
─────────────────────────────────────────────────────────
1. Determine chunk needs save
2. Check if modified
3. Snapshot chunk data into a
   pooled ChunkData (sections,
   sky levels, queues)           →  4. Serialize snapshot to pooled buffer
                                    5. Compress (per settings.saveCompression)
                                    6. Lock region file (_fileLock)
                                    7. Write sector-aligned record to disk
                                    8. Update location table entry
9. Return buffer to its pool
   (finally); snapshot → pool on
   Written/FailedPermanent, →
   failed-save retry registry on
   Failed/Canceled (CP-6)
```

**Memory Management:**  
Uses `SerializationBufferPool` to recycle byte arrays, and `ChunkStorageManager.CreateSerializationSnapshot()` builds the thread-safe copy from pooled `ChunkData`/`ChunkSection` instances — zero steady-state GC.

**Cancellation:**  
`SaveChunkAsync` takes a `CancellationToken` (the world's shutdown token) so in-flight async saves abort cleanly on quit instead of racing the synchronous shutdown flush.

**Failure Handling (CP-6 durability contract):**  
`SaveChunkAsync` returns `Task<ChunkSaveResult>` (`Written` / `Canceled` / `Failed` / `FailedPermanent`) instead of swallowing exceptions. On `Failed` **or `Canceled`**, the serialization snapshot — the edits' only surviving copy once the live `ChunkData` is pool-recycled by `UnloadChunks` (or cleared from `ModifiedChunks` by a manual save) — transfers to the storage manager's **failed-save retry registry** (coord-keyed; a newer entry for the same coord supersedes the older snapshot) rather than returning to the pool. Staging `Canceled` matters because
cancellation only comes from the quit token: without it, a save canceled mid-quit after its chunk left `ModifiedChunks` would lose the edits silently. `FailedPermanent` (zero-length serialization — deterministic, retrying can never succeed) releases the snapshot with a loud per-chunk error instead of entering the retry loop. The registry is drained three ways:

1. **Per-frame retry** — `World.Update` calls `DrainFailedSaveRetries()` (one due entry per frame, exponential backoff 1→30 s, loud error per failed attempt; retryable entries are never dropped mid-session, deterministic ones are dropped loudly).
2. **Reload guard** — `LoadChunkAsync` synchronously flushes a pending retry for its coord before reading disk, so a returning player never loads pre-edit bytes. (Known window: a save still *in flight* for that coord is invisible to the guard — closing it would need per-coord in-flight tracking.)
3. **Quit flush** — the synchronous `SaveAllModifiedChunks` path calls `FlushFailedSavesSync()` (one attempt per entry, before `StorageManager.Dispose`); recovered and deterministic entries are removed, **retryably-failing entries are retained** (moot at real quit; preserves recoverability when the same path runs from a live-session force-unload).

All three save paths (sync, async, retry) share one write core (`WriteToRegion`), which also hosts the dev-only fault seam. The contract is guarded by `Minecraft Clone/Dev/Validate Save Durability` (B1–B9) with dev-only injection seams (`ChunkStorageManager.InjectSaveFaults`, `InjectZeroLengthSerializes`).

**Thread Safety:**  
Each region file takes an exclusive private `_fileLock` for both reads and writes (`FileStream` position is not thread-safe). Multiple chunks in different regions can save concurrently.

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

**Step 9 - PopulateFromSave (condensed — see `ChunkData.cs` for full version):**

```csharp
// In ChunkData.cs
public void PopulateFromSave(ChunkData loadedData)
{
    Array.Copy(loadedData.heightMap, heightMap, heightMap.Length);

    // CRITICAL: Steal section ownership instead of copying. loadedData is a pooled
    // instance that will be recycled — null out its slots so its Reset() doesn't
    // return the stolen sections to the pool.
    for (int i = 0; i < sections.Length; i++)
    {
        if (sections[i] != null) World.Instance.ChunkPool.ReturnChunkSection(sections[i]);
        sections[i] = loadedData.sections[i];
        loadedData.sections[i] = null;
    }

    // Transfer compact uniform sky levels
    Array.Copy(loadedData.SectionUniformSkyLevel, SectionUniformSkyLevel, SectionUniformSkyLevel.Length);

    // Copy lighting queues (RGB-aware)
    foreach (var node in loadedData.SunlightBfsQueue)
        AddToSunLightQueue(node.Position, node.OldLightLevel);
    foreach (var node in loadedData.BlocklightBfsQueue)
        AddToBlockLightQueue(node.Position, node.OldLightLevel, node.OldBlockR, node.OldBlockG, node.OldBlockB);

    // Transfer pending flags
    if (loadedData.HasLightChangesToProcess) HasLightChangesToProcess = true;
    if (loadedData.NeedsInitialLighting) NeedsInitialLighting = true;

    // Active blocks (fluids/grass) recalculated via RecalculateCounts, not saved
    foreach (ChunkSection section in sections)
        section?.RecalculateCounts(World.Instance.BlockTypes);

    IsPopulated = true;
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
// In ChunkData.cs — Properties with dirty-set callback.
// Setting any flag to true invokes OnLightWorkFlagged, which enqueues
// the chunk's position into a ConcurrentQueue for main-thread processing.
[NonSerialized] private bool _needsInitialLighting;
[NonSerialized] private bool _hasLightChangesToProcess;
[NonSerialized] private bool _needsEdgeCheck;

public bool NeedsInitialLighting { get => ...; set { ...; if (value) OnLightWorkFlagged?.Invoke(Position); } }
public bool HasLightChangesToProcess { get => ...; set { ...; if (value) OnLightWorkFlagged?.Invoke(Position); } }
public bool NeedsEdgeCheck { get => ...; set { ...; if (value) OnLightWorkFlagged?.Invoke(Position); } }

[NonSerialized]
public bool IsAwaitingMainThreadProcess = false; // Job done, waiting for apply (plain field — no callback)
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

    // 2b. Replay pending cross-chunk blocklight mods. Each mod runs through
    //     CrossChunkLightModApplier.ComputeBlocklight — exactly like the live
    //     cross-chunk apply path (see section 2.4.B). When lighting is disabled,
    //     the store is left untouched so the mods survive until a session with
    //     lighting enabled.
    if (settings.enableLighting &&
        LightingStateManager.TryGetAndRemovePendingBlocklight(coord, out var pendingBlocklight))
    {
        // ... apply each mod, enqueue BFS wake-up nodes, release pooled dictionary
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

**Solution - Batched Deferred Updates (per channel):**

**In WorldJobManager.ProcessLightingJobs():**

```csharp
foreach (LightModification mod in jobData.Mods)
{
    Vector2Int neighborCoord = worldData.GetChunkCoordFor(mod.GlobalPosition);
    ChunkData neighborChunk = worldData.RequestChunk(neighborCoord, false);

    if (neighborChunk == null || !neighborChunk.IsPopulated) 
    {
        // Neighbor unloaded — degrade per channel:
        if (mod.Channel == LightChannel.Sun)
        {
            // Sunlight: the affected COLUMN is batched into _droppedLightUpdates and
            // saved to LightingStateManager at the end of the pass — a column recalc
            // is authoritative for the sky channel.
        }
        else
        {
            // Blocklight: a column recalc cannot restore RGB data — persist the FULL
            // modification (local position + RGB + removal flag) for replay on load.
            _world.LightingStateManager.AddPendingBlocklight(coord, localPos,
                mod.BlockR, mod.BlockG, mod.BlockB, mod.IsRemoval);
        }
        continue;
    }

    // Target has its own lighting job in flight? Defer the mod; it is drained right
    // after that job's merge (otherwise the merge would overwrite it — Bug 08 path 2).

    // Otherwise apply to the loaded neighbor via CrossChunkLightModApplier.
}

// Batch save all vanishing neighbor sunlight columns
foreach (var kvp in _droppedLightUpdates)
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
/// Verifies that all 8 horizontal neighbors (cardinal + diagonal) of a chunk exist,
/// have completely finished terrain generation, and are populated with voxel data.
/// Out-of-bounds chunks (beyond world limits) are treated as intrinsically "ready".
/// </summary>
public bool AreNeighborsDataReady(ChunkCoord coord)
{
    // Check all 8 horizontal neighbors to prevent light leaks into ungenerated chunks.
    foreach (Vector3Int offset in VoxelData.AllNeighborOffsets)
    {
        ChunkCoord neighborCoord = coord.Neighbor(offset.x, offset.z);

        // Neighbors outside the world will never exist — treat as ready.
        if (!IsChunkInWorld(neighborCoord)) continue;

        // Still actively generating terrain data.
        if (JobManager.GenerationJobs.ContainsKey(neighborCoord))
        {
            return false;
        }

        // Chunk hasn't been created yet, or exists but terrain isn't populated.
        if (!worldData.Chunks.TryGetValue(neighborCoord.ToVoxelOrigin(), out ChunkData neighborData) ||
            !neighborData.IsPopulated)
        {
            return false;
        }
    }

    // All neighbors exist and are populated.
    return true;
}
```

**Note:** The check covers all 8 horizontal neighbors (diagonals included) because lighting jobs copy data from diagonal neighbors too. It is intentionally weaker than the meshing gate `AreNeighborsReadyAndLit` — see `CHUNK_LIFECYCLE_PIPELINE.md` for the gate hierarchy.

**Edge Case Handling:**  
An edge chunk at (0, 50) only waits for its in-world neighbors. Out-of-bounds neighbors (e.g. West (-1,50)) are automatically "ready".

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

**Manual Save (Implemented — `World.SaveWorldData()`):**

* Automatic on world exit
* Player Controlled: quick-save input action (`SaveWorldPressed` in `Player.cs`)
* Pause menu "Save" / "Save & Quit" buttons (`PauseMenuController.cs`)
* Saves all modified chunks asynchronously, then writes `level.dat` + pending stores

---

## 9. Performance Analysis

### 9.1. Achieved Metrics

| Operation                      | Target  | Measured | Status |
|--------------------------------|---------|----------|--------|
| Chunk Save (Main Thread)       | < 1ms   | ~0.3ms   | ✅      |
| Chunk Load (Async)             | < 5ms   | ~3ms     | ✅      |
| Lighting Restoration           | < 2ms   | ~1ms     | ✅      |
| Memory (Active Chunks)         | < 1000  | ~500-800 | ✅      |
| File Size (Per Chunk, Deflate) | < 100KB | ~50KB    | ✅      |

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
- [x] **AOT World Migration System** (save versions v1 → v11; see `AOT_WORLD_MIGRATION_SYSTEM.md`)
- [x] **Versioned region addressing** (`RegionAddressCodec` V1/V2 + region repack migration)
- [x] **PlayerStateManager** (position, rotation, capabilities, inventory, cursor item)
- [x] **Pending blocklight store** (`pending_blocklight.bin`, Bug 08)
- [x] **RGB blocklight + LightData serialization** (chunk format v5-v7)

### ⚠️ Partial / In Progress

- [ ] Chunk prioritization system
- [ ] ProcessPendingInitialLighting retry system
- [ ] Auto-save interval

### 📋 Planned

- [ ] Increased region file write throughput
- [ ] Incremental section saves
- [ ] Chunk streaming/prediction
- [ ] Region file defragmentation

---

## 14. Appendix

### A. File Locations Quick Reference

| Component               | File Path                                                   |
|-------------------------|-------------------------------------------------------------|
| SaveSystem              | `Assets/Scripts/SaveSystem.cs`                              |
| ChunkSerializer         | `Assets/Scripts/Serialization/ChunkSerializer.cs`           |
| ChunkStorageManager     | `Assets/Scripts/Serialization/ChunkStorageManager.cs`       |
| RegionFile              | `Assets/Scripts/Serialization/RegionFile.cs`                |
| RegionAddressCodec      | `Assets/Scripts/Helpers/RegionAddressCodec.cs`              |
| ModificationManager     | `Assets/Scripts/Serialization/ModificationManager.cs`       |
| LightingStateManager    | `Assets/Scripts/Serialization/LightingStateManager.cs`      |
| CompressionFactory      | `Assets/Scripts/Serialization/CompressionFactory.cs`        |
| SaveDataTypes (DTOs)    | `Assets/Scripts/Serialization/SaveDataTypes.cs`             |
| SerializationBufferPool | `Assets/Scripts/Serialization/SerializationBufferPool.cs`   |
| AOT Migration System    | `Assets/Scripts/Serialization/Migration/` (manager + steps) |
| WorldData               | `Assets/Scripts/Data/WorldData.cs`                          |
| ChunkData               | `Assets/Scripts/Data/ChunkData.cs`                          |
| World                   | `Assets/Scripts/World.cs`                                   |
| WorldJobManager         | `Assets/Scripts/WorldJobManager.cs`                         |

### B. Save File Structure Example

```
Saves/
└── My World/
    ├── level.dat                    (JSON metadata + player state)
    ├── pending_mods.bin             (VoxelMod queue)
    ├── pending_lighting.bin         (Sunlight column queue)
    ├── pending_blocklight.bin       (Cross-chunk blocklight mods; absent if nothing pending)
    └── Region/
        ├── r.0.0.bin                (up to 1024 chunks)
        ├── r.0.1.bin
        ├── r.1.0.bin
        └── ...
```

Pre-migration backups are created as sibling world folders named `{WorldName}_Backup_v{sourceVersion}_{timestamp}` and are hidden from the world selector.

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
* **v1.7** - Synced with codebase: sector-based region file layout, versioned region addressing (`RegionAddressCodec` V1/V2), chunk format v7 (flag-based sections, uniform-sky optimization, RGB light queues), v5+ pending mods Meta byte, `pending_lighting.bin` rename + `pending_blocklight.bin` format, save snapshotting/cancellation, 8-neighbor data-ready gate, completed player state & AOT migration checklist items
* **v1.8** - Renamed `CompressionAlgorithm.GZip` → `Deflate` for accuracy (value 1 has always been raw headerless DEFLATE, not GZip). On-disk value 1 unchanged, so existing saves stay byte-compatible — source-only rename, no format bump/migration (MT-6)
* **v1.9** - CP-6 save-boundary durability: `SaveChunkAsync` surfaces `ChunkSaveResult`, failed saves route their snapshot to the failed-save retry registry (per-frame drain + reload guard + quit flush) instead of silently losing session edits (F5). Review hardening in the same change: quit-canceled saves stage their snapshot too (closes the manual-save-then-quit hole), zero-length serialization is `FailedPermanent` (no retry loop), flush retains retryable entries, single shared write core. No format change — failure-path bookkeeping only

---

**Last Updated:** 2026-07-22  
**Next Review:** Chunk prioritization or Defragmentation
