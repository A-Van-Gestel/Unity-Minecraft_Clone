# Core Data Structures

**Audited:** 2026-08-17, at commit `aad0527c` (branch `feat/world-scaling`). Verified in code, not assumed:
`Data/VoxelState.cs`, `Data/ChunkData.cs`, `Data/ChunkSection.cs`, `Data/WorldData.cs`, `Data/VoxelMod.cs`,
`Data/JobData/JobDataManager.cs`, `Data/NativeData/FluidVertexTemplatesNativeData.cs`, `Chunk.cs`,
`SectionRenderer.cs`, `Helpers/ChunkMath.cs`, `Jobs/BurstData/BurstVoxelDataBitMapping.cs`,
`Jobs/BurstData/LightBitMapping.cs`, `Jobs/Generators/StandardChunkGenerator.cs`.

This document outlines the primary data structures used to represent the game world. The design prioritizes memory efficiency, cache-friendliness, and compatibility with Unity's C# Job System and Burst Compiler.

## 1. The Voxel: `uint _packedData`

The fundamental unit of the world is the voxel. Instead of using a large class or struct with many fields, all data for a single voxel is bit-packed into a single 32-bit unsigned integer (`uint`). This is the most critical optimization in the project.

### Bit Layout

The `uint` is structured as follows (from least significant bit to most significant):

| Bits    | Size    | Range     | Purpose                                              |
|---------|---------|-----------|------------------------------------------------------|
| `0-15`  | 16 bits | `0-65535` | **Block ID** (Supports 65k block types)              |
| `16-23` | 8 bits  | —         | **Reserved** (zeroed, available for future metadata) |
| `24-31` | 8 bits  | `0-255`   | **Metadata** (Shared / Context Sensitive)            |

The masks and shifts are declared once, in `BurstVoxelDataBitMapping`: `ID_MASK = 0x0000FFFF` /
`ID_SHIFT = 0`, and `META_MASK = 0xFF000000` / `META_SHIFT = 24`.

> **History:** Bits 16-23 previously stored sunlight (16-19) and blocklight (20-23) levels. As of RGB Lighting - Phase B (save v10, chunk format v7), all light data lives in a separate `ushort[] LightData` array per section (see §2.1). The freed bits are reserved for future metadata expansion (biome tint, damage state, block variant, etc.).

### Metadata Usage (Context Sensitive)

Bits `24-31` are a flexible storage space. The **authoritative** interpretation is the target block's
`MetadataSchema`, declared per block in `BlockDatabase` — see
[PER_BLOCK_METADATA_SCHEMAS.md](PER_BLOCK_METADATA_SCHEMAS.md). The legacy (schema-less) interpretation
below is what the `*Legacy` helpers on `BurstVoxelDataBitMapping` encode, and remains the meaning for
blocks that declare no schema:

1. **Fluids:** the lower 4 bits (`META_VAL_FLUID_MASK = 0xF`) hold the **fluid level**. Bit 3 is the
   *falling* flag (`FLUID_FALLING_FLAG = 8`); the lower 3 bits are the **effective level**
   (`FLUID_EFFECTIVE_LEVEL_MASK = 0x7`), extracted via `GetEffectiveFluidLevel`.
2. **Solids:** the lower 3 bits (`META_VAL_ORIENT_MASK = 0x7`) hold the **orientation** (storage index;
   see `VoxelOrientation`).
3. **Remaining bits:** the upper 4 bits (solids: upper 5) are unused by the legacy encoding and are the
   room a block's `MetadataSchema` allocates from.

### Access and Manipulation

Direct bitwise operations are error-prone. Instead, all interactions with the packed `uint` are handled by these helpers:

- **`VoxelState.cs`**: A struct that wraps the `uint`, for use on the **main thread**. Exposes `ID`,
  `Meta`, and the legacy `Orientation` / `FluidLevel` views, plus the schema-aware
  `GetOrientation(schema)` / `SetOrientation(value, schema)` / `GetFluidLevel(schema)` /
  `SetFluidLevel(value, schema)` pairs. `Properties` resolves the block's `BlockType` via
  `World.Instance.BlockTypes[ID]` — the managed reference that keeps this type out of jobs. Light data is
  not reachable from here at all; it lives on the section's `LightData[]` (see `LightBitMapping`).
- **`BurstVoxelDataBitMapping.cs`**: A Burst-compatible static class for block ID and metadata bit-mapping. Used exclusively within **Jobs** and Burst-compiled methods. This separation is crucial because `VoxelState` has references to managed code (`World.Instance`) that cannot be used in a job.
- **`LightBitMapping.cs`**: A Burst-compatible static class for light data bit-mapping on the `ushort LightData[]` array. The `ushort` packs four 4-bit channels — `[Sky:0-3][BlockR:4-7][BlockG:8-11][BlockB:12-15]` — and the class provides `GetSkyLight`, `SetSkyLight`, `GetBlocklightR/G/B`, `SetBlocklightR/G/B`, and `PackLightData` helpers.

## 2. The Chunk Hierarchy

The world is divided into 16x128x16 chunks. To optimize memory usage and rendering performance, chunks are further subdivided into vertical **Sections**.

The dimensional constants live in `ChunkMath`, which aliases the authoritative `VoxelData` world
dimensions so there is one declaration site (CP-7/F8): `CHUNK_WIDTH`, `CHUNK_HEIGHT`,
`SECTION_SIZE = 16`, `SECTION_VOLUME = 4096`, `SECTIONS_PER_CHUNK = 8`, `CHUNK_VOLUME = 32768`.

### 2.1. `ChunkSection.cs` (The Storage Unit)

This class represents a 16x16x16 cube of voxels. It acts as the atomic unit of storage, and is pooled
(`ConcurrentDynamicPool<ChunkSection>`, reset via `Reset()`).

- **`uint[] voxels`**: A flat array of `4096` integers ($16^3$). Stores block ID + metadata (no light data).
- **`ushort[] LightData`**: A parallel flat array of `4096` unsigned shorts. Stores all light channels: `[Sky:4][BlockR:4][BlockG:4][BlockB:4]`. Accessed via `LightBitMapping` helpers. This is the sole authority for light values — the `uint` voxel carries no light bits.
- **`int nonAirCount`**: Tracks how many blocks are not Air. `IsEmpty => nonAirCount == 0`, used to quickly skip empty sections during processing.
- **`int opaqueCount`**: Tracks how many blocks are fully opaque. `IsFullySolid => opaqueCount >= SECTION_VOLUME`, used to identify fully solid underground sections.
- **`int emissiveCount`**: Tracks how many blocks emit light, so a section with no emitters can be skipped by blocklight work.

All three counters are recomputed together by `RecalculateCounts(blockTypes)`.

### 2.2. `ChunkData.cs` (The Data Container)

This is a plain C# class that acts as the data container for a full map column. It is serializable, and is
pooled (`ConcurrentDynamicPool<ChunkData>`, reset via `Reset(Vector2Int)`) — every transient field it
carries needs a matching reset, per `.agents/rules/pool-reset-safety.md`.

- **`ChunkSection[] sections`**: An array of sections (8 sections for the 128-block high world).
    - *Optimization:* Indexing logic handles the translation from global Y to Section Index. A `null` slot is an untouched (all-air) section.
- **`ushort[] heightMap`**: A 1D array (`16x16`) storing the Y-coordinate of the highest opaque block in each column. This is critical for sky light calculation speed.
- **`byte[] SectionUniformSkyLevel`**: A per-section compact-light shortcut. `0x00–0x0F` means the whole section is uniform at that sky level with zero blocklight; `0xFF` (`UNIFORM_SKY_NONE`) means "no shortcut — read the real `LightData`". It covers both null sections (empty sky above terrain) and non-null ones (pitch-black underground).
- **Active-voxel buckets**: voxels with active behaviors are held in per-behavior-family `NativeHashSet<int>` sets (`BehaviorFamily.Grass` / `BehaviorFamily.Fluid`), keyed by the flat chunk index. They are `[NonSerialized]`, lazily created, and re-derived on load — never persisted. The `ActiveVoxels` enumerable projects them back to positions for main-thread callers.
- **Lighting state**: the BFS queues (`SunlightBfsQueue` / `BlocklightBfsQueue`, both `Queue<LightQueueNode>`) plus the pipeline flags `NeedsInitialLighting`, `HasLightChangesToProcess`, and `NeedsEdgeCheck`. Those three are properties, not fields: setting one to `true` fires the static `OnLightWorkFlagged` callback that registers the chunk in `World`'s dirty set. `RemainingEdgeCheckRounds` bounds edge-check convergence.
- **`int LifecycleEpoch`**: a monotonic counter **incremented** (never zeroed) on recycle, so in-flight jobs can detect that the slot they were scheduled against has since been reused.

### 2.3. `Chunk.cs` (The Visual Manager)

This is a regular C# class that represents the chunk in the live game scene. It **does not** hold mesh data or behavior state directly.

- **`ChunkCoord Coord` / `Vector3 UnityPosition`**: the chunk's index-space coordinate and its render-space origin — two distinct coordinate spaces, kept as separate values per WS-4 (see [Guides/COORDINATE_SPACES_GUIDE.md](../Guides/COORDINATE_SPACES_GUIDE.md)).
- **`GameObject ChunkGameObject`**: The parent container in the scene.
- **`SectionRenderer[] _sectionRenderers`**: A list of helper objects, each managing the visual representation of one `ChunkSection`.
- **`bool HasMeshApplied`**: whether this lifecycle has had mesh data applied yet.
- **`ChunkLoadAnimation _loadAnimation` / `bool _hasPlayedLoadAnimation`**: the one-shot rise-from-underground animation, played at most once per chunk lifecycle.

`Chunk` is pooled (`DynamicPool<Chunk>`, `Reset(ChunkCoord)` / `Release()`). Active-voxel tracking lives on
`ChunkData` (§2.2), not here.

### 2.4. `SectionRenderer.cs` (The Renderer)

A helper class responsible for the visual output of a single section. See
[SUB_CHUNK_MESHING_ARCHITECTURE.md](SUB_CHUNK_MESHING_ARCHITECTURE.md) §4 for the full apply path.

- **`GameObject GameObject`**: Child of the Chunk object, positioned at `sectionIndex * SECTION_SIZE`.
- **`MeshFilter` / `MeshRenderer`**: Standard Unity components.
- **`static readonly VertexAttributeDescriptor[] Layout`**: the single source of truth for the section vertex format (MR-2, 32 B/vertex across 4 streams). The editor chunk-preview window uploads against this same descriptor rather than keeping a second copy.
- **Advanced Mesh API**: Uses `Mesh.SetVertexBufferData` and `Mesh.SetSubMeshes` to upload mesh data via `NativeArray` slices, avoiding memory allocation during updates.
- **Two-axis visibility**: it owns the *"has geometry"* axis (`GameObject.SetActive`) only; the *"occlusion-culled"* axis (`MeshRenderer.forceRenderingOff`) is written exclusively through `SetOcclusionCulled`. See §3.2 of the meshing doc.

## 3. The World: `WorldData` and `World`

These two classes manage the collection of all chunks.

### `WorldData.cs` (The Data)

This class represents the entire save file state.

- **`Dictionary<Vector2Int, ChunkData> _chunks`**: The master collection of all loaded `ChunkData`, indexed by the chunk's voxel-space origin. Exposed read-only as `Chunks` (`IReadOnlyDictionary`), plus the allocation-free `ChunkValues` / `ChunkKeys` views for hot paths. Structural changes go **only** through the dedicated mutators (add / remove / `ClearChunks`), which bump a topology version so the VQ-1 last-chunk query cache can never go stale silently.
- **`HashSet<ChunkData> ModifiedChunks`**: Tracks chunks that need to be saved to disk.
- **`Dictionary<Vector2Int, HashSet<Vector2Int>> SunlightRecalculationQueue`**: A bucketed queue (Chunk Coordinate -> List of Local Columns) tracking vertical columns that require a full sky light recalculation (e.g., after a block placement blocks the sky).

### `World.cs` (The Orchestrator)

The central `MonoBehaviour` singleton.

- Manages the `Player`, `WorldData`, and `WorldJobManager`.
- Handles `CheckViewDistance()` to load/unload chunks.
- Coordinates the main update loop (Tick updates, Job processing, Modification queue).
- Drives the floating origin, but does not hold it: the anchor state lives in the **static** `WorldOrigin` (`OriginChunk` / `OriginVoxel`, private setters, mutated only through `SetOrigin`). `World` decides *when* to re-anchor — it tests `WorldOrigin.ShouldReanchor(PlayerChunkCoord)` at the top of `Update`, before anything consumes the frame's positions, and calls `ShiftOrigin` (also reachable via `ForceOriginReanchor`, which the `/origin` command uses). The shift then re-anchors the scene's objects: every `Chunk`, plus the voxel visualizer and the clouds. Unity render space and voxel world space diverge as the player travels, and conversions go only through `WorldOrigin`/`ChunkMath` (see [WORLD_SCALING_FLOATING_ORIGIN.md](WORLD_SCALING_FLOATING_ORIGIN.md) and [Guides/COORDINATE_SPACES_GUIDE.md](../Guides/COORDINATE_SPACES_GUIDE.md)).

## 4. Job-Safe Data Structures

Jobs cannot access managed data like `BlockType[]` or `StandardBiomeAttributes[]`. We create a "mirrored" set of blittable data (structs) at startup stored in persistent `NativeArrays`.

### 4.1. Block & Mesh Data (`JobDataManager.cs`)

`JobDataManager` holds the **globally shared**, world-type-agnostic job data and disposes it on shutdown:

- **`NativeArray<BlockTypeJobData> BlockTypesJobData`**: per-block properties like solidity, opacity, fluid type, and texture IDs.
- **`NativeArray<CustomMeshData> / <CustomFaceData> / <CustomVertData> / <int>`**: a flattened representation of custom block models (e.g., non-cubes), letting the meshing jobs render complex shapes without managed objects.

It is constructed by `JobDataManagerFactory` from the `BlockDatabase`.

### 4.2. World Generation Data

Generation data is **not** on `JobDataManager` — each `IChunkGenerator` owns the native mirrors of its own
world type's authoring assets, so an unused world type costs nothing. For the standard generator
(`StandardChunkGenerator`) that means `NativeArray<StandardBiomeAttributesJobData>`,
`StandardTerrainLayerJobData`, `StandardLodeJobData`, `StandardCaveLayerJobData`, and
`StructurePoolEntryJobData`, alongside the per-biome `FastNoiseLite` and `BurstSpline` arrays that drive
terrain shape, caves, strata, lodes, and flora. `LegacyChunkGenerator` keeps its own
`LegacyBiomeAttributesJobData` equivalent. See
[World Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md](World%20Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md).

### 4.3. Fluid Data (`FluidVertexTemplatesNativeData.cs`)

- **`NativeArray<float> WaterVertexTemplates`**: Pre-computed height values for water levels (0-15).
- **`NativeArray<float> LavaVertexTemplates`**: Pre-computed height values for lava levels.
- *Usage:* These arrays are passed to the meshing jobs to calculate fluid surface slopes efficiently.

## 5. Transient Interaction Data

### `VoxelMod` (Struct)

Represents a request to change a block in the world. It is used to decouple the Main Thread (Input/Game Logic) from the Chunk Data state.

- **`Vector3Int GlobalPosition`**: the absolute voxel-space block position (Burst callers use the `int3` constructor overload rather than building a `Vector3Int` in job code).
- **`ushort ID`**: the block to place.
- **`byte Meta`**: the raw 8-bit metadata byte, schema-agnostic. It replaced the earlier separate `Orientation` + `FluidLevel` fields (PER_BLOCK_METADATA_SCHEMAS §7.4); its interpretation is resolved from the target block's `MetadataSchema` at replay time.
- **`bool ImmediateUpdate`**: whether the edit must be applied without waiting for the batched pass.
- **`ReplacementRule Rule`**: placement override logic (e.g., `OnlyReplaceAir`, `ForcePlace`). `Default` defers to the Block Tag system.
- **`VoxelModSource Source`**: which context produced the mod (`Live` for player/behavior/replayed edits, `WorldGen` for generation-time expansion). It selects *which* `canReplaceTags` field the `Default` rule consults.
- **Usage:** Added to `World._modifications` queue. Processed at the end of the frame to ensure thread safety and batching of mesh rebuilds.
