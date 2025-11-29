# Core Data Structures

This document outlines the primary data structures used to represent the game world. The design prioritizes memory efficiency, cache-friendliness, and compatibility with Unity's C# Job System and Burst Compiler.

## 1. The Voxel: `uint _packedData`

The fundamental unit of the world is the voxel. Instead of using a large class or struct with many fields, all data for a single voxel is bit-packed into a single 32-bit unsigned integer (`uint`). This is the most critical optimization in the project.

### Bit Layout

The `uint` is structured as follows (from least significant bit to most significant):

| Bits    | Size    | Range     | Purpose                                   |
|---------|---------|-----------|-------------------------------------------|
| `0-15`  | 16 bits | `0-65535` | **Block ID** (Supports 65k block types)   |
| `16-19` | 4 bits  | `0-15`    | **Sunlight Level**                        |
| `20-23` | 4 bits  | `0-15`    | **Blocklight Level**                      |
| `24-31` | 8 bits  | `0-255`   | **Metadata** (Shared / Context Sensitive) |

### Metadata Usage (Context Sensitive)

Bits `24-31` are a flexible storage space. Their interpretation depends on the **Block ID**:

1. **Fluids:** If the Block ID corresponds to a fluid, the lower 4 bits of the Metadata (`0-15`) represent the **Fluid Level**.
2. **Solids:** If the Block ID corresponds to a solid block, the lower 3 bits (`0-7`) represent the **Orientation** (North, East, South, West, Up, Down).
3. **Future Use:** The upper 4-5 bits of the metadata are currently unused and available for future features (e.g., variation indices, damage states).

### Access and Manipulation

Direct bitwise operations are error-prone. Instead, all interactions with the packed `uint` are handled by two static helper classes:

- **`VoxelState.cs`**: A struct that wraps the `uint`. It provides high-level properties (`.id`, `.light`, `.orientation`) for use on the **main thread**. It also contains helper properties like `.Properties` which can look up the block's `BlockType` from the world's block array.
- **`BurstVoxelDataBitMapping.cs`**: A Burst-compatible static class containing the exact same bit-mapping logic. It is used exclusively within **Jobs** and Burst-compiled methods. This separation is crucial because `VoxelState` has references to managed code (`World.Instance`)
  that cannot be used in a job.

## 2. The Chunk Hierarchy

The world is divided into 16x128x16 chunks. To optimize memory usage and rendering performance, chunks are further subdivided into vertical **Sections**.

### 2.1. `ChunkSection.cs` (The Storage Unit)

This class represents a 16x16x16 cube of voxels. It acts as the atomic unit of storage.

- **`uint[] voxels`**: A flat array of `4096` integers ($16^3$).
- **`int nonAirCount`**: Tracks how many blocks are not Air. Used to quickly skip empty sections during processing.
- **`int opaqueCount`**: Tracks how many blocks are fully opaque. Used to identify fully solid underground sections.

### 2.2. `ChunkData.cs` (The Data Container)

This is a plain C# class that acts as the data container for a full map column. It is serializable.

- **`ChunkSection[] sections`**: An array of sections (e.g., 8 sections for a 128-block high world).
    - *Optimization:* Indexing logic handles the translation from global Y to Section Index.
- **`byte[] heightMap`**: A 1D array (`16x16`) storing the Y-coordinate of the highest light-obstructing block in each column. This is critical for sunlight calculation speed.
- **Lighting Queues**: Contains the queues for pending light updates (`Queue<LightQueueNode>`).

### 2.3. `Chunk.cs` (The Visual Manager)

This is a regular C# class that represents the chunk in the live game scene. It **does not** hold mesh data directly anymore.

- **`GameObject _chunkObject`**: The parent container in the scene.
- **`SectionRenderer[] _sectionRenderers`**: A list of helper objects, each managing the visual representation of one `ChunkSection`.
- **`List<Vector3Int> _activeVoxels`**: A list of local positions for voxels that have active behaviors (e.g., grass spreading, fluid flowing).

### 2.4. `SectionRenderer.cs` (The Renderer)

A helper class responsible for the visual output of a single section.

- **`GameObject gameObject`**: Child of the Chunk object.
- **`MeshFilter` / `MeshRenderer`**: Standard Unity components.
- **Advanced Mesh API**: Uses `Mesh.SetVertexBufferData` and `Mesh.SetSubMeshes` to upload mesh data via `NativeArray` slices, avoiding memory allocation during updates.

## 3. The World: `WorldData` and `World`

These two classes manage the collection of all chunks.

### `WorldData.cs` (The Data)

This class represents the entire save file state.

- **`Dictionary<Vector2Int, ChunkData> Chunks`**: The master collection of all loaded `ChunkData`, indexed by coordinate.
- **`HashSet<ChunkData> ModifiedChunks`**: Tracks chunks that need to be saved to disk.
- **`Dictionary<Vector2Int, HashSet<Vector2Int>> SunlightRecalculationQueue`**: A bucketed queue (Chunk Coordinate -> List of Local Columns) tracking vertical columns that require a full sunlight recalculation (e.g., after a block placement blocks the sky).

### `World.cs` (The Orchestrator)

The central `MonoBehaviour` singleton.

- Manages the `Player`, `WorldData`, and `WorldJobManager`.
- Handles `CheckViewDistance()` to load/unload chunks.
- Coordinates the main update loop (Tick updates, Job processing, Modification queue).

## 4. Job-Safe Data Structures (`JobDataManager.cs`)

Jobs cannot access managed data like `BlockType[]` or `BiomeAttributes[]`. We create a "mirrored" set of blittable data (structs) at startup stored in persistent `NativeArrays`.

### 4.1. Block & Mesh Data

- **`BlockTypeJobData`**: A struct containing properties like `isSolid`, `opacity`, `fluidType`, and texture IDs.
- **`CustomMeshData` / `CustomFaceData` / `CustomVertData`**: A flattened representation of custom block models (e.g., non-cubes). This allows the `MeshGenerationJob` to render complex shapes without managed objects.

### 4.2. World Generation Data

- **`BiomeAttributesJobData`**: Contains generation parameters (scale, terrain height, surface blocks) for biomes.
- **`LodeJobData`**: Flattened array of all ore lodes for all biomes, indexed via offsets in the Biome struct.

### 4.3. Fluid Data (`FluidVertexTemplatesNativeData.cs`)

- **`NativeArray<float> WaterVertexTemplates`**: Pre-computed height values for water levels (0-15).
- **`NativeArray<float> LavaVertexTemplates`**: Pre-computed height values for lava levels.
- *Usage:* These arrays are passed to the `MeshGenerationJob` to calculate fluid surface slopes efficiently.

### 4.4. Manager

- **`JobDataManager.cs`**: Holds the `NativeArray<T>` collections for all the above. It is responsible for allocation on startup and disposal on shutdown.

## 5. Transient Interaction Data

### `VoxelMod` (Struct)

Represents a request to change a block in the world. It is used to decouple the Main Thread (Input/Game Logic) from the Chunk Data state.

- **Fields:** `GlobalPosition`, `ID`, `Orientation`, `FluidLevel`, `ImmediateUpdate`.
- **`ReplacementRule` (Enum)**: Defines logic for overriding placement (e.g., `OnlyReplaceAir`, `ForcePlace`).
- **Usage:** Added to `World._modifications` queue. Processed at the end of the frame to ensure thread safety and batching of mesh rebuilds.