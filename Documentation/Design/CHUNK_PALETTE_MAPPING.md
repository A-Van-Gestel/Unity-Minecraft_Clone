# Design Document: Chunk Palette Mapping (Draft)

**Version:** 1.1 (Draft)  
**Target:** Unity 6.4 (Mono Backend, Burst/DOTS Compatible)  
**Context:** Voxel Engine Serialization & Data Architecture

---

## 1. The Problem: Hardcoded Block IDs and Array Order Dependency

Currently, blocks are identified in chunk data by a raw `ushort` ID (e.g., `2` for Grass). This ID implicitly corresponds to the index of the block within the `BlockDatabase.blockTypes` array. 

This direct mapping creates fundamental technical debt:
- **Save File Fragility**: Inserting a new block into the middle of the `blockTypes` array shifts the index of all subsequent blocks. This corrupts all existing save files, as their stored chunk data will now point to incorrect blocks.
- **Magic Numbers**: Engine code relies on magic numbers (like `id == 2`), which becomes difficult to maintain when the database grows. *(Addressed separately by the Block ID Code Generator — see implementation plan.)*
- **Modding limitations**: If multiple developers or mods try to add blocks, there is no way to resolve index conflicts.
- **Pending Modifications**: `pending_mods.bin` stores raw `ushort` IDs in `VoxelMod` structs. If blocks are reordered, queued modifications from a previous session silently target wrong blocks.

## 2. Proposed Solution: Chunk Palette Mapping

To permanently decouple save files from the Unity `BlockDatabase` array order, we will implement **Palette Mapping**. 

### Concept
Instead of a chunk storing global block IDs (the array index), each chunk stores three things:
1. **A Local Palette**: A header that maps an internal "Local ID" (0, 1, 2...) to a globally unique "String ID" (e.g., `minecraft-clone:grass`).
2. **The Voxel Data Array**: The actual voxel array stored on disk uses the *Local ID*, not the global struct index.
3. **A Runtime Mapping Table**: When the game boots, it generates a global mapping table of `String ID` -> `Current BlockDatabase Array Index`.

### The Hydration Process (Disk -> RAM)
When a chunk is loaded from disk:
1. The chunk reads its Palette and maps each stored `String ID` to the `Current BlockDatabase Array Index`.
2. It generates a temporary fast-translation array: `LocalToGlobalMap[Local ID] = Current Database Index`.
3. As the chunk's voxel data is extracted into the `ChunkData` memory representation, the stored IDs are converted from Local IDs to Global Runtime IDs using the `LocalToGlobalMap`.

### The Dehydration Process (RAM -> Disk)
When a chunk is saved to disk:
- **Option A (Scan on Save):** Scan 4096 voxels per section × 8 sections = 32,768 voxels to build a `HashSet<ushort>` of unique IDs, then construct the palette. Simple to implement but has a per-save cost.
- **Option B (Incremental Tracking):** Maintain a dirty-tracked palette per `ChunkData`, updated incrementally when `ModifyVoxel` is called. Eliminates the scan entirely but adds complexity to every voxel write path.

> [!NOTE]
> The choice between Option A and Option B should be benchmarked during implementation. Option A is simpler and may be "fast enough" given that saves are already I/O-bound. Option B is the optimization path if profiling shows the scan is a bottleneck.

### Data-Oriented & Burst Requirements (Critical)
By translating the IDs *during the disk load boundary*, the active memory representation of a chunk (the `VoxelState` and raw `uint` arrays inside `ChunkData.cs`) still uses raw integers (the current global sequence). 

This guarantees that **the Burst Compiler `[BurstCompile]` and Job System retain maximum performance**, as they never have to execute string lookups, equality checks, or dictionary hashes inside tight loops. They only ever see clean `ushort` integers.

## 3. Advantages

- **100% Save File Compatibility**: Reordering blocks in the Inspector, deleting blocks, or inserting blocks in the middle of the array will never break old saves. The game will seamlessly map the old strings to the new active indices.
- **Fallback States**: If a chunk contains a `String ID` that no longer exists in the `BlockDatabase` (e.g., a mod was uninstalled), the pipeline can safely fallback the unknown ID to `Air` or an `UnknownBlock` placeholder integer.
- **Pending Mods Safety**: `pending_mods.bin` would also store string IDs instead of raw indices, making queued modifications immune to reordering.

## 4. Prerequisites & Dependencies

### AOT Migration Step (Required)
Introducing palettes is a **chunk format version bump** that requires a `WorldMigrationStep` (see `Documentation/Design/AOT_WORLD_MIGRATION_SYSTEM.md`). Existing saves store raw global IDs with no palette header. The migration step must:
1. Read the old format (raw `ushort` IDs with no palette).
2. Build a palette from a **frozen snapshot** of the `BlockDatabase` at migration time (embedded in the migration file as a historical DTO, per the AOT authoring guidelines).
3. Rewrite chunks in the new palette format.

> [!IMPORTANT]
> The frozen snapshot must be hardcoded into the migration file itself (not read from the live `BlockDatabase`), following the "True DTO" pattern established in `Migration_v1_to_v2_RemoveNeedsLight.cs`. This ensures the migration remains correct even if the database changes after shipping.

### `pending_mods.bin` Migration
`pending_mods.bin` stores `VoxelMod` structs with raw `ushort ID` fields. The same migration step must also convert these to use string IDs (or a parallel palette), so queued modifications survive a block reorder.

### `BlockType.uniqueId` Field
A new `string uniqueId` field must be added to `BlockType` (e.g., `"core:grass"`). To prevent human error:
- An Editor `OnValidate()` or validation script should check for duplicates and empty strings.
- Consider auto-generating the initial value from `blockName` (sanitized to lowercase, spaces replaced with underscores, prefixed with `core:`).

## 5. Implementation Steps (High-Level)

1. **Add Unique Identifiers**: Add `string uniqueId` to `BlockType`. Add Editor validation.
2. **Global ID Table Generation**: On startup, build `Dictionary<string, ushort>` mapping string IDs to current `blockTypes` indices.
3. **Change Chunk Save Format** (new chunk format version):
   - Write palette length + string entries.
   - Write voxel data using palette-local indices.
4. **Write AOT Migration Step**: Convert old-format chunks (no palette) to new-format (with palette) using a frozen DTO snapshot.
5. **Migrate `pending_mods.bin`**: Convert stored raw IDs to string-backed IDs.
6. **Serialization Pipeline (Load)**: Read palette → Map strings to global runtime `ushort` → Remap voxel array.
7. **Serialization Pipeline (Save)**: Build local palette → Convert runtime array to local indices → Write.

## 6. Future Enhancements (Out of Scope)

- **Variable Bit-Width Packing**: For chunks with very few unique blocks (e.g., mostly air, stone, and dirt), the Local IDs could be bit-packed down to 2 or 4 bits per voxel instead of a full 16-bit ushort, massively increasing Region File compression ratios (similar to modern Minecraft's chunk format). This would require a second format version bump and is a separate feature.
