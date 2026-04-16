---
name: serialization-migration
description: Guides any change that alters the on-disk save format — chunk binary layout, level.dat schema, pending_mods, lighting_pending, or region file structure — through the AOT World Migration system. Use when editing ChunkData serialization, ChunkStorageManager, anything under Assets/Scripts/Serialization/, or when the user asks to add/change/remove a field that ends up on disk.
---

# Serialization Migration Protocol

This engine has a custom region-based binary save system with a formal migration pipeline. Any change that alters on-disk layout without a migration step will silently corrupt existing worlds for every player. The migration system is AOT (runs before the `World` scene loads) — there are specific rules for writing migrations safely.

## When to use this skill

- Adding, removing, or re-typing a field in `ChunkData.cs` or any type it serializes.
- Changing `ChunkStorageManager.cs` read/write paths.
- Modifying anything under `Assets/Scripts/Serialization/` that alters a persisted byte.
- Changes to `level.dat` schema, `pending_mods.bin`, or `lighting_pending.bin`.
- User requests: "save a new field to disk", "upgrade the save format", "add a version bump".

## How to use it

### Step 1 — Read the design document FIRST

Read `@Documentation/Design/AOT_WORLD_MIGRATION_SYSTEM.md`. Key concepts before you write anything:

- **Master version:** `SaveSystem.CURRENT_VERSION` in `level.dat` is the single source of truth. Every format change bumps this.
- **Historical DTOs:** Migration code MUST define its own frozen data structures inside the migration file. It must NOT reference live engine types like `ChunkData`, `ChunkSection`, or `VoxelState` — future rewrites of those types would silently break old migrations.
- **`TargetChunkFormatVersion`:** If the bump only affects `level.dat`, leave this as `null` and the manager skips all chunk I/O. If the bump changes chunk bytes, override it AND write the new version byte as the first byte of the returned chunk array — the manager throws `InvalidDataException` otherwise (fail-fast is intentional).
- **Crash & downgrade safety:** Writes go to temp folders and swap in atomically. Do not bypass this pattern.

### Step 2 — Migration step checklist

For every on-disk change, the following must all land in the same change:

1. Bump `SaveSystem.CURRENT_VERSION`.
2. Create a new `WorldMigrationStep` subclass under `Assets/Scripts/Serialization/Migration/` following the naming convention: `Migration_v{Source}_to_v{Target}_{ShortDescription}.cs`.
3. Define `SourceWorldVersion`, `TargetWorldVersion`, and `Description`.
4. Override only the methods whose data you are changing:
    - `MigrateLevelDat(string oldJson)` — if `level.dat` schema changed.
    - `MigratePendingMods(byte[])` — if pending mods layout changed.
    - `MigratePendingLighting(byte[])` — if pending lighting layout changed.
    - `MigrateChunk(byte[])` — if chunk binary changed. Must also override `TargetChunkFormatVersion` AND write the new version byte as the first byte of the output.
5. Define private, frozen DTOs inside the migration file for BOTH old and new layouts. Do not import live engine types.
6. Register the step with the migration manager (follow the pattern of existing steps).

### Step 3 — Test the round trip

- Manually create or keep a save on the OLD version before running the migrated build.
- Launch and verify the migration prompt runs, backs up the world, and the world loads clean afterwards.
- Confirm the backup was created atomically (temp-swap pattern, not overwrite).
- The first load after migration should NOT trigger the migration again — version stamping at the end is the manager's job, not the step's.

### Step 4 — Never

- Never use `BinaryFormatter`, `JSON`, `XmlSerializer`, or any other ad-hoc serializer for terrain data. Region files use LZ4/GZip-compressed custom binary.
- Never edit an already-shipped migration step after release. Write a new one.
- Never change chunk layout without bumping `TargetChunkFormatVersion` — the first-byte check will throw `InvalidDataException` in production otherwise.

## Cross-reference

- Region file concurrency: `@Documentation/Technical/REGION_FILE_CONCURRENCY.md`
- Overall storage architecture: `@Documentation/Design/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`
- Known serialization bugs: `@Documentation/Bugs/SERIALIZATION_BUGS.md`
