# Core Data Structures

This document outlines the primary data structures used to represent the game world. The design prioritizes memory efficiency, cache-friendliness, and compatibility with Unity's C# Job System and Burst Compiler.

## 1. The Voxel: `uint _packedData`

The fundamental unit of the world is the voxel. Instead of using a large class or struct with many fields, all data for a single voxel is bit-packed into a single 32-bit unsigned integer (`uint`). This is the most critical optimization in the project.

### Bit Layout

The `uint` is structured as follows (from least significant bit to most significant):

| Bits    | Size    | Range   | Purpose              |
|---------|---------|---------|----------------------|
| `0-7`   | 8 bits  | `0-255` | **Block ID**         |
| `8-11`  | 4 bits  | `0-15`  | **Sunlight Level**   |
| `12-15` | 4 bits  | `0-15`  | **Blocklight Level** |
| `16-17` | 2 bits  | `0-3`   | **Orientation**      |
| `18-21` | 4 bits  | `0-15`  | **Fluid Level**      |
| `22-31` | 10 bits | -       | *Reserved*           |

### Access and Manipulation

Direct bitwise operations are error-prone. Instead, all interactions with the packed `uint` are handled by two static helper classes:

- **`VoxelState.cs`**: A struct that wraps the `uint`. It provides high-level properties (`.id`, `.light`, `.orientation`) for use on the **main thread**. It also contains helper properties like `.Properties` which can look up the block's `BlockType` from the world's block array.
- **`BurstVoxelDataBitMapping.cs`**: A Burst-compatible static class containing the exact same bit-mapping logic. It is used exclusively within **Jobs** and Burst-compiled methods. This separation is crucial because `VoxelState` has references to managed code (`World.Instance`)
  that cannot be used in a job.

## 2. The Chunk: `ChunkData` and `Chunk`

The world is divided into 16x128x16 sections called chunks. The logic and data for chunks are split into two classes:

### `ChunkData.cs` (The Data)

This is a plain C# class that acts as the pure data container for a chunk. It is designed to be serializable so it can be saved to disk.

- **`uint[] map`**: A flat, 1D array of `524,288` unsigned integers (`16*128*16`). This is the core data store for all voxels in the chunk. Voxels are indexed using the formula `x + width * (y + height * z)`.
- **`byte[] heightMap`**: A smaller 1D array (`16*16`) that stores the Y-coordinate of the highest light-obstructing block in each column. This is a critical optimization for sunlight calculations.
- **Lighting Queues**: Contains the main-thread `Queue<LightQueueNode>` for pending sunlight and blocklight updates.

### `Chunk.cs` (The Logic & Representation)

This is a regular C# class (not a `MonoBehaviour`) that represents the chunk in the live game scene.

- **`GameObject _chunkObject`**: The actual `GameObject` in the Unity scene, holding the `MeshFilter` and `MeshRenderer`.
- **Reference to `ChunkData`**: Each `Chunk` instance holds a reference to its corresponding `ChunkData`. This separates the visual representation from the underlying data.
- **`List<Vector3Int> _activeVoxels`**: A list of local positions for voxels that have active behaviors (e.g., grass spreading, fluid flowing) and need to be ticked on a timer.

## 3. The World: `WorldData` and `World`

These two classes manage the collection of all chunks.

### `WorldData.cs` (The Data)

This class represents the entire save file.

- **`Dictionary<Vector2Int, ChunkData> Chunks`**: The master collection of all loaded `ChunkData` in the world, indexed by their coordinate (e.g., `(0, 0)`, `(16, 0)`).
- **`HashSet<ChunkData> ModifiedChunks`**: A set used to track which chunks have changed and need to be saved to disk.

### `World.cs` (The Orchestrator)

This is the central `MonoBehaviour` singleton that drives everything.

- Manages the `Player` and `WorldData`.
- Manages the collection of `Chunk` GameObjects.
- Handles loading/unloading chunks as the player moves.
- Owns the `WorldJobManager`, which is responsible for scheduling all jobs (generation, lighting, meshing).
- Processes the modification queue (`_modifications`) to apply changes to the world state.

## 4. Job-Safe Data Structures (`JobDataManager.cs`)

Jobs cannot access managed data like `BlockType[]` (which contains sprites, strings, and other managed objects). To solve this, we create a "mirrored" set of data at startup that is job-safe.

- **`BlockTypeJobData`**: A struct containing only the blittable data from a `BlockType` class (e.g., `isSolid`, `opacity`, texture IDs).
- **`JobDataManager.cs`**: A class that holds persistent `NativeArray<T>` collections of this job-safe data (e.g., `NativeArray<BlockTypeJobData>`).
- These `NativeArray`s are created once when the game starts and are disposed of when the game closes. They are passed to jobs with the `[ReadOnly]` attribute to allow for maximum parallelization.