---
name: serialization-safety
description: Save-format safety rules that fire when editing serialization code or ChunkData. Prevents silent world corruption.
trigger: glob
glob: "{Assets/Scripts/Serialization/**/*.cs,Assets/Scripts/Data/ChunkData.cs}"
paths:
  - "Assets/Scripts/Serialization/**/*.cs"
  - "Assets/Scripts/Data/ChunkData.cs"
---

# Serialization Safety Rules

Changes to these files can silently corrupt every player's saved world. Every edit must be paired with a migration step or explicitly confirmed as layout-safe.

## Before editing, ask yourself

1. **Does this change alter the on-disk byte layout?** Adding, removing, reordering, or retyping a serialized field = yes.
2. **Does this change alter `ChunkData` fields that get written to region files?** Changing `sections`, `heightMap`, voxel packing bits = yes.
3. If the answer to either is yes, you MUST follow the AOT migration protocol below.

## AOT migration protocol (mandatory for layout changes)

1. **Bump `SaveSystem.CURRENT_VERSION`.**
2. **Create a new migration step** under `Assets/Scripts/Serialization/Migration/Steps/` following the naming convention: `Migration_v{Source}_to_v{Target}_{ShortDescription}.cs`.
3. **Define frozen DTOs** inside the migration file for both old and new layouts. Never import live engine types (`ChunkData`, `ChunkSection`, `VoxelState`) — future rewrites would silently break the migration.
4. **Override only the methods whose data you changed** (`MigrateLevelDat`, `MigrateChunk`, `MigratePendingMods`, `MigratePendingLighting`).
5. If overriding `MigrateChunk`, also override `TargetChunkFormatVersion` AND write the new version byte as the first byte of the returned array — the manager throws `InvalidDataException` otherwise (intentional fail-fast).

## Never

- Never use `BinaryFormatter`, `JSON`, `XmlSerializer`, or any ad-hoc serializer for terrain data.
- Never edit an already-shipped migration step. Write a new one.
- Never change chunk layout without bumping `TargetChunkFormatVersion`.

## Reference

- Full migration design: `@Documentation/Architecture/AOT_WORLD_MIGRATION_SYSTEM.md`
- Storage architecture: `@Documentation/Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`
- Region file concurrency: `@Documentation/Design/REGION_FILE_CONCURRENCY.md`
- Known serialization bugs: `@Documentation/Bugs/SERIALIZATION_BUGS.md`
