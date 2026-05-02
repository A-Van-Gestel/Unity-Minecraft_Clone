# Design Document: Per-Block Metadata Schemas

**Version:** 1.2 (Draft)  
**Date:** 2026-04-24  
**Status:** Proposed  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production, Burst/DOTS Compatible)  
**Context:** Packed voxel metadata, expanded block orientation, and BlockDatabase-driven authoring

---

## 1. Problem Statement

The current voxel format stores all per-voxel data inside a single packed `uint`:

- `16 bits` block ID
- `4 bits` sunlight
- `4 bits` blocklight
- `8 bits` metadata

This is already efficient and should be preserved. The current limitation is not the size of the voxel, but the fact that the metadata field is interpreted too globally.

Today, the engine treats metadata as:

- **Fluids:** lower 4 bits = fluid level
- **Solids:** lower 3 bits = orientation

That model works for simple cases, but it breaks down once different block families need different metadata semantics:

- Logs and fallen tree trunks only need an **axis** (`X`, `Y`, `Z`)
- Directional blocks may need **6 facings**
- Some asymmetric blocks may eventually need **facing + roll**
- Other blocks may want **variation**, **damage**, or another block-specific state

The main motivating use case right now is **fallen tree trunks**. Their mesh/texture orientation should support sideways placement or structure generation without forcing the engine into a heavyweight "full orientation for every block" design.

## 2. Current Research Summary

### 2.1. Current Packed Layout

The current packed voxel layout is documented in `Documentation/Architecture/DATA_STRUCTURES.md` and implemented in `Assets/Scripts/Jobs/BurstData/BurstVoxelDataBitMapping.cs`.

| Bits    | Size    | Purpose    |
|---------|---------|------------|
| `0-15`  | 16 bits | Block ID   |
| `16-19` | 4 bits  | Sunlight   |
| `20-23` | 4 bits  | Blocklight |
| `24-31` | 8 bits  | Metadata   |

Important finding: the engine already reserves the full `8-bit` metadata byte, but most current solid-block use cases only consume the lower `3 bits`.

### 2.2. Current Orientation Reality

The codebase currently has a mismatch between **storage capacity** and **runtime support**:

- The metadata packer can already encode `6` face directions in `3 bits`
- The main-thread/runtime helpers still behave mostly like a `4`-way cardinal Y-rotation system
- Meshing currently rotates geometry around the **Y axis only**
- Structure rotation is also currently **Y-only**
- Player block placement only emits the current `4` cardinal horizontal directions

So the current bottleneck is not "we need more than 32 bits per voxel." The real bottleneck is "the metadata schema is too hardcoded, and the runtime only supports a subset of the orientations the storage could already represent."

### 2.3. Bit Cost Comparison

| Requirement                         | States | Bits Needed | Notes                                             |
|-------------------------------------|--------|-------------|---------------------------------------------------|
| 4-way cardinal yaw                  | 4      | 2           | Current player-facing placement model             |
| Axis only (`X`, `Y`, `Z`)           | 3      | 2           | Ideal for logs, pillars, fallen trunks            |
| 6 facings                           | 6      | 3           | Already fits in current solid metadata budget     |
| 20-state "reduced full orientation" | 20     | 5           | Still needs 5 bits                                |
| 24-state full cube rotation         | 24     | 5           | Still fits inside the current 8-bit metadata byte |

Important conclusion: reducing the orientation scope from `24` states to roughly `20` states does **not** save a bit. Both still need `5 bits`.

The real bit savings come from using a **block-appropriate schema**:

- Logs do not need 20 or 24 states
- Logs only need `3` axis states
- That means `2 bits` is enough for the fallen-tree use case

### 2.4. Specific Fallen Log Finding

For sideways tree trunks, the correct abstraction is usually **axis**, not full orientation:

- `Y axis` = upright trunk
- `X axis` = east/west fallen trunk
- `Z axis` = north/south fallen trunk

This is much cheaper and cleaner than assigning every log a universal 5-bit orientation model.

### 2.5. Largest Implementation Cost: Meshing Rewrite

The design previously understated the cost of the meshing changes.

For the current codebase, the largest implementation cost is not bit-packing or migration logic. It is the fact that both runtime and editor meshing are currently built around **Y-axis-only rotation**:

- `VoxelMeshHelper` rotates vertices with `Quaternion.Euler(0, rotation, 0)`
- `MeshGenerationJob` only asks for a Y rotation angle
- the Block Editor preview path uses `EditorMeshGenerator`

Supporting `Axis3` correctly means:

- runtime meshing can no longer treat orientation as "just a Y rotation angle"
- editor preview meshing and runtime meshing must be updated in lockstep
- side/top UV remapping for logs and similar blocks must be axis-aware
- the preferred implementation is **precomputed orientation variants / lookup tables**, not per-voxel quaternion work inside Burst hot paths

This should be treated as the primary engineering cost center of Phase 2.

## 3. Design Goals

- Preserve the packed voxel as a single `uint`
- Keep chunk section raw size unchanged
- Support sideways logs and other block-specific orientation needs
- Provide full migration support for existing worlds so runtime voxel state never mixes legacy and new metadata semantics
- Avoid wasting bits on blocks that do not need a rich metadata model
- Keep all hot-path decoding Burst-safe
- Make metadata interpretation configurable through the current block authoring pipeline
- Leave room for future block-specific metadata such as variation or damage

## 4. Non-Goals

- Arbitrary quaternion rotation per voxel
- Per-voxel managed objects or reference types
- Widening the base voxel beyond `uint`
- Generic "everything supports full 24-state orientation" as the default for every block
- Moving metadata interpretation into ScriptableObject lookups inside Burst jobs

## 5. Proposed Solution: Per-Block Metadata Schemas

### 5.1. Core Idea

Keep the packed voxel layout exactly as it is today, but stop treating the metadata byte as one universal meaning for all blocks.

Instead:

1. Each block type declares a **metadata schema**
2. The raw voxel continues to store a plain `8-bit` metadata value
3. The block's schema determines how those bits are interpreted
4. The schema definition is mirrored into job-safe block data so Burst jobs never perform managed lookups

This changes the problem from:

> "How do we fit every possible block behavior into one universal orientation field?"

to:

> "How does each block family interpret the same 8-bit metadata byte?"

That is a much better fit for a voxel engine with heterogeneous block behavior.

### 5.2. Recommended Initial Schema Set

The engine should begin with a small set of built-in schemas:

| Schema           | Bits Used | Meaning                          | Recommended Use                                                                   |
|------------------|-----------|----------------------------------|-----------------------------------------------------------------------------------|
| `None`           | `0`       | Metadata unused, must remain `0` | Truly orientation-less blocks (Air, decorative panels, plants that don't rotate)  |
| `FluidLevel4`    | `0-3`     | Fluid level `0-15`               | Water, lava                                                                       |
| `HorizontalOnly` | `0-1`     | 4-way yaw (`0=N, 1=S, 2=W, 3=E`) | Ordinary solid cubes that benefit from yaw variety to break up repeating textures |
| `Axis3`          | `0-1`     | `Y`, `X`, `Z` axis               | Logs, pillars, fallen trunks                                                      |
| `Facing6`        | `0-2`     | 6 face directions                | Directional blocks that care about up/down                                        |
| `Facing6Roll2`   | `0-4`     | facing + 4-way roll              | Fully asymmetric mountable / custom meshes                                        |

The most important immediate wins are `Axis3` (logs) and `HorizontalOnly` (formalises the 4-way yaw rotation that ordinary cubes already use today). `HorizontalOnly`'s bit layout is intentionally aligned with the legacy v3-chunk orientation storage indices for the four horizontal cases (storage index `0=N, 1=S, 2=W, 3=E`), so a v5→v6 migration of an ordinary cube is a pure schema relabel — zero byte rewrites for the typical case. See §9.5.E for the migration mapping.

### 5.3. Frozen Bit Layouts

The bit layout for every shipped schema must be frozen explicitly. Migration steps and future schema changes must not rely on inferred meanings.

| Schema           | Frozen Bit Layout                                                  |
|------------------|--------------------------------------------------------------------|
| `None`           | all bits `0`                                                       |
| `FluidLevel4`    | bits `0-3` = fluid level, bits `4-7` reserved                      |
| `HorizontalOnly` | bits `0-1` = 4-way yaw (`0=N, 1=S, 2=W, 3=E`), bits `2-7` reserved |
| `Axis3`          | bits `0-1` = axis, bits `2-7` reserved                             |
| `Facing6`        | bits `0-2` = facing, bits `3-7` reserved                           |
| `Facing6Roll2`   | bits `0-2` = facing, bits `3-4` = roll, bits `5-7` reserved        |

Frozen `Facing6Roll2` encoding:

- `facing` uses values `0-5`
- `roll` uses values `0-3`
- raw metadata value is `(facing & 0x07) | ((roll & 0x03) << 3)`

The encoder must mask `facing` to 3 bits before OR'ing. An unmasked illegal facing of `6` or `7` would silently clobber bit `3` of the roll field.

This encoding must be reused consistently by:

- runtime helpers
- editor tooling
- migration DTOs
- tests

### 5.4. Reserve Schema Enum Ranges

To reduce future save-compatibility drift, the schema enum should reserve value ranges intentionally:

- `0-31` = core engine schemas
- `32-63` = experimental / editor-only schemas
- `64-255` = reserved for future use

This does not add runtime cost, but it makes future expansion safer and more explicit.

### 5.5. Why Per-Block Schemas Beat a Universal Full-Orientation Model

If every solid block were forced into a universal `Facing6Roll2` format:

- many blocks would waste metadata bits
- authoring would become more confusing
- future metadata features would become harder to layer in

With per-block schemas:

- logs use `2 bits`
- simple directional blocks use `3 bits`
- only the rare truly asymmetric blocks use `5 bits`
- unused bits remain available within that block's schema budget for future expansion

This gives better long-term flexibility without increasing voxel size.

## 6. Authoring Model: BlockType / BlockDatabase

For the current codebase, the correct authoring location is the existing `BlockType` entries inside `BlockDatabase`.

This is the current equivalent of what might loosely be called "VoxelProperties."

### 6.1. Recommended Authoring Fields

Add metadata-related configuration directly to `BlockType`:

```csharp
public enum MetadataSchema : byte
{
    None = 0,
    FluidLevel4 = 1,
    Axis3 = 2,
    Facing6 = 3,
    Facing6Roll2 = 4,
}

public enum PlacementMetadataMode : byte
{
    None = 0,
    PlayerYawCardinal = 1,
    PlayerLookAxis = 2,
    // Reserved: 3 = SurfaceFacing (orient toward the surface the block was placed against).
    // Not included in the initial implementation; add only when a block actually needs it.
}
```

Recommended `BlockType` additions:

```csharp
public MetadataSchema metadataSchema = MetadataSchema.None;
public PlacementMetadataMode placementMetadataMode = PlacementMetadataMode.None;
public byte defaultMetadata = 0;
```

These fields should then be mirrored into `BlockTypeJobData`.

### 6.2. Why `BlockType` Is the Right Place

This keeps the configuration:

- authorable in the existing `BlockDatabase.asset`
- stable and explicit per block
- easy to validate in editor tooling
- easy to mirror into `BlockTypeJobData` as tiny blittable fields

This is preferable to a purely global hardcoded switch on block ID.

### 6.3. Structure-Time Metadata Authoring

For fallen trunks and similar authored content, structures should be able to stamp metadata directly rather than relying entirely on runtime placement logic.

Recommended direction:

- structure authoring data should support an optional per-voxel metadata override
- log/trunk structures should be able to author `Axis3.X` or `Axis3.Z` directly
- this should apply both to ScriptableObject-authored structures and any future structure editor tooling

That avoids burying schema-specific authoring logic inside world generation code.

### 6.4. Optional Future Enhancement: Shared Schema Presets

If the number of metadata configurations grows, the project could later add a `BlockDatabase`-level preset list such as:

- `MetadataSchemaDefinition[] schemas`
- each `BlockType` stores a small schema preset index

That is not required for the first implementation. The recommended starting point is **direct enum fields on `BlockType`**, because it is simpler and lower risk.

## 7. Runtime Model Changes

### 7.1. Stop Hardcoding Orientation vs Fluid in the Core Bit Mapper

`BurstVoxelDataBitMapping` should remain the authoritative raw bit packer, but it should no longer assume that all solids mean "orientation" and all fluids mean "fluid level" at the top-level API.

Recommended direction:

- keep `GetMeta()` / `SetMeta()`
- keep raw pack/unpack of the metadata byte
- move schema-specific interpretation into dedicated helpers

The current `PackVoxelData(..., bool isFluid)` shape is not compatible with the schema-driven model.

Recommended replacement:

```csharp
public static uint PackVoxelData(ushort id, byte sunLight, byte blockLight, byte meta)
```

Then schema-aware callers become responsible for computing the correct raw metadata byte before packing.

For example:

- `VoxelMetadataUtility` for main-thread code
- `BurstVoxelMetadataUtility` for jobs

This makes the low-level packer schema-agnostic and removes the misleading `isFluid` branch from the core API.

Callsite blast radius note:

- every existing caller of the old 6-argument `PackVoxelData(..., isFluid)` must be updated in the same Phase 1 step
- the signature change is breaking, so Phase 1 must not leave the build red between callsite updates
- audit all call sites (generation jobs, chunk deserialization, structure placement, player placement, migration code) before landing the new signature

### 7.2. Add Raw Metadata Access to `VoxelState`

The current `VoxelState.Orientation` and `VoxelState.FluidLevel` properties become too narrow once blocks can choose different metadata schemas.

Recommended addition:

```csharp
public byte Meta { get; set; }
```

Then schema-aware helpers can decode `Meta` according to the block's `BlockType` / `BlockTypeJobData`.

Compatibility note:

- `Orientation` and `FluidLevel` should only exist as schema-aware compatibility accessors during the transition
- they must decode through the block's configured schema, not by blindly slicing raw bits
- shipped runtime code should not branch on "legacy world" versus "new world" once the migration has completed

Recommended sunset rule:

- keep them only while migrating call sites
- mark them for removal once schema-aware APIs have replaced direct usage across runtime and editor code

### 7.3. Mirror Schema Data into `BlockTypeJobData`

The following data should be available in jobs without managed lookups:

- `MetadataSchema`
- `PlacementMetadataMode` if jobs need it
- `DefaultMetadata`

This keeps all schema interpretation Burst-safe.

### 7.4. Collapse `VoxelMod`'s Dual Metadata Fields into a Single `Meta` Byte

`VoxelMod` today stores orientation and fluid level as two separate fields:

- `Orientation`
- `FluidLevel`

That duplication is incompatible with the schema-driven model. It keeps two competing interpretations alive after the rest of the engine has moved to raw metadata.

Recommended change — **only the two metadata fields are affected**:

- replace `Orientation` and `FluidLevel` with a single `byte Meta`
- all other `VoxelMod` fields (`GlobalPosition`, `ID`, `ImmediateUpdate`, `Rule`, etc.) remain unchanged
- the serialized layout of `pending_mods.bin` shrinks by one byte per mod and loses its dual legacy fields

This change should land in the same world-version migration that normalizes chunk voxel metadata.

That means:

- `VoxelMod` becomes schema-agnostic
- `pending_mods.bin` stops storing dual legacy fields
- the design avoids carrying old orientation/fluid semantics forward in one remaining data structure

### 7.5. Precomputed Decode LUT for Burst

To avoid schema branching on every voxel in meshing and other hot paths, the runtime should precompute a decode lookup table during BlockDatabase -> `BlockTypeJobData` build.

Recommended shape:

- table indexed by `(schemaIndex << 8) | metaByte`
- output is a tiny fixed decoded payload for the current job family

Examples:

- axis decode LUT for `Axis3`
- facing decode LUT for `Facing6`
- facing+roll decode LUT for `Facing6Roll2`

This keeps per-voxel runtime decoding constant-time and branch-light.

The LUT is also the defense against invalid metadata — invalid or reserved-bit entries must resolve to the block's `defaultMetadata` **inside the LUT itself**, not at every call site. §7.6's defensive validation applies to authoring-time and LUT-build time; once the LUT is built, hot-path code may read it unconditionally without re-validating.

Hot-reload note:

- changing `BlockType.metadataSchema` or `defaultMetadata` in the editor during Play mode invalidates any in-flight LUT reads from Burst jobs
- the main thread must either gate BlockDatabase edits behind a pause of chunk/meshing jobs, or atomically swap the LUT only at a safe sync point
- hot-reload safety is not required in the first shipping cut but should be tracked as a known limitation until addressed

### 7.6. Runtime Safety for Invalid Metadata

Editor validation is necessary but not sufficient.

The runtime should also defend against invalid metadata values by:

- clamping or normalizing `defaultMetadata` when building `BlockTypeJobData`
- baking "invalid meta -> block default" entries directly into the LUT described in §7.5, so hot-path decoders never see raw invalid values
- asserting in development builds when migration or serialization code receives an invalid raw value (hot-path jobs should rely on the LUT instead of per-voxel asserts)
- falling back to the normalized block default when corruption is detected

This prevents invalid authoring data from silently propagating into Burst jobs, and keeps defensive logic at the system boundaries (LUT build, migration, deserialization) rather than scattered across hot loops.

## 8. Orientation Strategy by Block Family

### 8.1. Logs and Fallen Tree Trunks

Use `Axis3`.

Suggested axis encoding:

| Value | Meaning |
|-------|---------|
| `0`   | Y axis  |
| `1`   | X axis  |
| `2`   | Z axis  |

This is the best immediate fit for fallen logs generated by structures.

Meshing note:

- do not rotate each voxel dynamically with a quaternion in the hot path
- precompute `X`, `Y`, and `Z` orientation variants
- freeze the per-axis texture and face remapping rules
- update runtime meshing and Block Editor preview meshing together

### 8.2. Simple Directional Blocks

Use `Facing6` when the block needs a front direction and may be placed on walls, ceilings, or floors.

This costs `3 bits`.

### 8.3. Fully Asymmetric Mountable Blocks

Use `Facing6Roll2` only for blocks where:

- the forward direction matters
- a secondary roll around that forward axis also matters
- floor/ceiling placement must preserve player yaw

This costs `5 bits`.

It should be the exception, not the default.

### 8.4. Placement Authoring Modes

`PlacementMetadataMode.PlayerLookAxis` is the first mode that cannot rely on the current 4-way player yaw state.

It requires:

- reading the player's camera look vector
- selecting the dominant axis from the full 3D look direction
- authoring `Axis3.X`, `Axis3.Y`, or `Axis3.Z` from that dominant axis

This should be called out explicitly because it is the first non-yaw placement path in the engine.

## 9. Save Format and Migration Impact

### 9.1. Raw Chunk Format Impact

If we keep the packed voxel as `uint`, then:

- section voxel payload size does not change
- chunk section size does not change
- region compression characteristics remain fundamentally the same

That means this design does **not** require a chunk payload expansion.

### 9.2. Semantic Migration Requirement

Even though the raw byte size stays the same, changing the metadata meaning for an existing block still requires migration.

Example:

- if `OakLog` currently stores legacy orientation-style values
- and we reinterpret that same metadata as `Axis3`
- existing saved logs may be decoded differently unless the values are transformed

This proposal therefore requires a **full AOT migration step** for any shipped world version where existing persisted voxel metadata is reinterpreted under the new schema system.

Shipped behavior must be:

1. old saved chunks are migrated once
2. old `pending_mods.bin` data is migrated once
3. after migration, all loaded voxel state uses only the new metadata schema meanings
4. runtime code does **not** keep dual legacy/new interpretation branches around indefinitely

This is important to prevent subtle future bugs where:

- some voxels in memory still behave like legacy orientation values
- newer voxels use schema-aware metadata
- helpers like `VoxelState`, meshing, placement, or serialization accidentally mix both interpretations

The engine should not carry that ambiguity forward. The migration step is the normalization boundary.

### 9.3. Required Migration Strategy

The migration must follow the AOT World Migration system:

1. bump `SaveSystem.CURRENT_VERSION`
2. add a new `WorldMigrationStep`
3. use frozen historical DTOs inside the migration file
4. fully parse and rewrite chunk payloads rather than treating unknown bytes as opaque remainder
5. migrate `pending_mods.bin` in the same version step

If the chunk byte layout itself changes, the migration must also set `TargetChunkFormatVersion` and rewrite the chunk payload with the new version byte as documented in `Documentation/Architecture/AOT_WORLD_MIGRATION_SYSTEM.md`.

If the raw chunk payload stays `uint[4096]` and only the metadata meaning changes, the migration may still need to rewrite every packed voxel value for affected block IDs so that persisted data is normalized into the new schema representation.

The migration must not switch on live `BlockIDs` constants or live `BlockDatabase` array indices. Those are not stable historical identifiers.

Instead, each migration step must embed a frozen snapshot of:

- old world-version `ushort` block ID -> historical block identity (see below)
- old world-version `ushort` block ID -> historical metadata schema

This is required to survive block order changes over time.

#### Definition: block identity

Throughout this document, **block identity** means the stable string key used in `BlockDatabase` (e.g. `"oak_log"`, not the runtime `ushort` ID and not the display name).

- the `ushort` ID is an index into `BlockDatabase` and shifts when blocks are added, removed, or reordered
- the string key is stable across reordering and safe to embed in migrations, fingerprints, and frozen DTOs
- all references to "block identity" in §9.3 (frozen snapshot) and §9.4 (schema fingerprint) mean this stable string key
- if `BlockDatabase` does not currently expose a stable string key per block, Phase 1 must add one before any migration logic depends on it

### 9.4. Schema Fingerprint Guard

The save should store a compact schema fingerprint in `level.dat` describing the metadata schema assignment for the block table as of that world version.

Recommended content (one entry per block):

- block identity (stable string key, per §9.3)
- schema enum
- default metadata

Recommended load rule:

- if the current block schema configuration no longer matches the saved fingerprint, refuse silent load and require either:
    - a new migration step, or
    - an explicit editor-only override for non-shipping dev worlds

This prevents schema churn from silently corrupting saves during development.

#### Dev-override UX

The dev override must never ship to release builds. Recommended mechanism:

- gate the override behind `#if UNITY_EDITOR` (or an equivalent editor-only guard) so release builds cannot take this path at all
- when an editor session opens a world whose fingerprint no longer matches the current configuration, show a modal listing exactly which blocks drifted (identity, old schema, new schema)
- modal offers three choices:
    - **Cancel load** — default, safe choice
    - **Scaffold migration step** — generates a `WorldMigrationStep` skeleton at the next save version with mapping table stubs for the drifted blocks
    - **Force load (dev only)** — rewrites the fingerprint in `level.dat` to the current configuration; marks the world with a `dev_override_applied` flag so it is visibly tainted and cannot be mistaken for a migrated world
- the tainted flag prevents a force-loaded dev world from being treated as a valid migration test baseline

### 9.5. Legacy Orientation Mapping Rules

The migration should include explicit deterministic mapping tables for every affected block family.

The current legacy orientation domain is:

| Legacy Value | Meaning |
|--------------|---------|
| `0`          | South   |
| `1`          | North   |
| `2`          | Top     |
| `3`          | Bottom  |
| `4`          | West    |
| `5`          | East    |

These are the values exposed by the current runtime helpers, not the internal packed index representation.

#### 9.5.A. Legacy -> `Axis3`

If an existing directional log block is converted from legacy orientation semantics to `Axis3`, the compatibility mapping should be:

| Legacy Meaning         | New Axis |
|------------------------|----------|
| North / South          | `Z`      |
| East / West            | `X`      |
| Top / Bottom / default | `Y`      |

This is the expected mapping for:

- `OakLog`
- future pillar/log blocks migrated from the current legacy orientation model
- any block whose real semantic state is "axis" rather than "front face"

#### 9.5.B. Legacy -> `Facing6`

If an existing block is migrated to `Facing6`, the mapping is direct:

| Legacy Meaning | New Facing |
|----------------|------------|
| South          | South      |
| North          | North      |
| Top            | Top        |
| Bottom         | Bottom     |
| West           | West       |
| East           | East       |

#### 9.5.C. Legacy -> `Facing6Roll2`

If an existing block is migrated to `Facing6Roll2`, the legacy system does not contain roll information.

Required rule:

- map the legacy facing directly
- assign roll = `0`

This preserves the best possible orientation while acknowledging that the old format simply did not encode the extra degrees of freedom.

#### 9.5.D. Unsupported or Invalid Legacy Values

The migration must also define a hard fallback path for any unexpected legacy state:

1. if the legacy value is invalid or out of range, use the block's configured `defaultMetadata`
2. if the target schema cannot represent the old value exactly, map to the closest supported semantic value
3. if no safe semantic mapping exists, fall back to the block's default orientation/metadata

This fallback path should ideally never trigger for healthy data, but it is still required for corruption resistance and long-term maintainability.

Recommended examples:

- invalid log orientation -> `Axis3.Y`
- invalid `Facing6` value -> block's default facing
- missing roll information when migrating to `Facing6Roll2` -> roll `0`

#### 9.5.E. Legacy -> `HorizontalOnly`

`HorizontalOnly`'s bit layout is intentionally aligned with the legacy v3-chunk orientation storage indices for the four horizontal cases, so the migration is the identity for those:

| Legacy Storage Index | Legacy Meaning | New `HorizontalOnly` Value |
|----------------------|----------------|----------------------------|
| `0`                  | Front / North  | `0` (North)                |
| `1`                  | Back / South   | `1` (South)                |
| `2`                  | Left / West    | `2` (West)                 |
| `3`                  | Right / East   | `3` (East)                 |
| `4`                  | Top            | `0` (clamped to North)     |
| `5`                  | Bottom         | `0` (clamped to North)     |
| `6`, `7`             | invalid        | `0` (clamped to North)     |

Top/Bottom (storage indices 4 and 5) are never sensible for an ordinary cube — the block has no "top" or "bottom" face that differs from the others — so they clamp to the North default. This is the §9.5.D fallback rule applied per-schema.

### 9.6. Schema-to-Schema Migration Rule

This design must support not only legacy -> new migration, but also future schema reassignments such as:

- `Axis3` -> `Facing6`
- `Facing6` -> `Facing6Roll2`
- `None` -> `Axis3`

Required rule:

- every shipped `BlockType.metadataSchema` change must be accompanied by either:
    - a new migration step with explicit mapping logic, or
    - an editor assertion that no persisted worlds depend on the old schema

Without this rule, later schema changes will silently reinterpret saved bytes and corrupt worlds.

### 9.7. Migration Scope: Chunks and Pending Mods

`pending_mods.bin` stores orientation-like data separately in `VoxelMod`.

If an existing block changes schema meaning, the migration must rewrite:

- persisted chunk voxel metadata
- `pending_mods.bin` voxel metadata payloads
- the serialized `VoxelMod` layout itself, if the engine collapses to a single `Meta` byte

The rule is simple:

> If a block's persisted metadata meaning changes, every persisted representation of that block must be normalized in the same world-version migration.

That prevents a migrated world from loading normalized chunks while later replaying stale legacy pending modifications back into the world.

### 9.8. Migration Telemetry and Fallback Counters

Fallbacks during migration should not be silent.

The migration step should accumulate counters such as:

- migrated voxels per affected block identity
- migrated pending mods per affected block identity
- unexpected legacy value count per block identity
- defaulted-to-block-default count per block identity

These counters should be surfaced in a post-migration report so corruption or mapping mistakes do not masquerade as success.

### 9.9. No Long-Term Runtime Compatibility Layer

This document recommends against a permanent runtime compatibility layer such as:

- "if save version < X, decode old orientation"
- "if block was placed pre-migration, interpret metadata differently"
- "keep both legacy and new semantics alive inside `VoxelState` forever"

That approach would create exactly the mixed semantic model we want to avoid.

A short-lived development-only compatibility shim can be acceptable while implementation is in progress, but the shipped architecture should rely on:

- a one-time AOT migration
- normalized persisted data
- a single runtime metadata interpretation model

## 10. Compression Analysis

### 10.1. What Should Not Be Compressed Further Right Now

The following fields are already effectively dense:

- `Sunlight` = `4 bits`
- `Blocklight` = `4 bits`
- `FluidLevel` = `4 bits` for the current fluid model

Shrinking those further is not realistic without redesigning their gameplay systems.

### 10.2. The Overprovisioned Field

The most overprovisioned field is the `16-bit` block ID, because the current project only has a small number of blocks.

However, shrinking block IDs in RAM is **not recommended** because:

- it reduces future block-count headroom
- it complicates Burst and serialization assumptions
- it is a much riskier architectural trade than using the already-reserved metadata byte more intelligently

### 10.3. Better Compression Strategy

The recommended strategy is:

- keep the base `uint`
- use per-block schemas to make metadata denser semantically
- reserve widening or sidecar storage only for rare future cases

If the engine ever has exceptional blocks that truly outgrow `8 bits` of metadata, the better long-term answer is likely:

- a sparse sidecar store for those exceptional blocks only

not:

- making every voxel in the world larger

## 11. Proposed Implementation Phases

### Phase 1: Metadata Schema Plumbing

1. Add `MetadataSchema`, `PlacementMetadataMode`, and `defaultMetadata` to `BlockType`
2. Reserve schema enum ranges and freeze the raw bit layout of every shipped schema
3. Mirror schema fields into `BlockTypeJobData`
4. Add raw `Meta` accessors to `VoxelState` and replace the low-level `PackVoxelData(..., isFluid)` API with schema-agnostic raw-meta packing
5. Introduce schema-aware utility helpers and Burst decode LUT generation
6. Define the frozen historical block ID snapshot and legacy-orientation mapping tables the migration step will use
7. Replace `VoxelMod`'s dual metadata fields with a single `Meta` byte in the design and serialization plan

### Phase 2: Axis-Based Orientation Support

1. Implement `Axis3` decoding/encoding
2. Rewrite runtime and editor meshing to support precomputed `X`, `Y`, and `Z` variants with correct face/UV remapping
3. Update placement logic to support dominant-axis authoring from player look direction
4. Update structure generation and structure authoring tools to stamp axis metadata explicitly where needed
5. Use this for fallen tree trunk generation first
6. Write the AOT migration step for affected chunk voxels and pending mods, including telemetry counters and the `VoxelMod` serialization rewrite

### Phase 3: Full Directional Support

1. Add `Facing6` placement and rendering rules
2. Update helper logic that is currently hardcoded to 4-way yaw
3. Update structure rotation logic where blocks require vertical facings
4. Add schema fingerprint checks so future schema changes cannot silently load without migration

### Phase 4: Advanced Rich Orientation

1. Add `Facing6Roll2` only for blocks that truly need it
2. Add editor validation for invalid combinations
3. Add schema-to-schema migration mappings before shipping any block that uses it
4. Benchmark hot-path decode changes against a pre-schema baseline captured before Phase 1:
    - meshing job throughput (voxels/ms or chunks/frame) must not regress by more than `5%`
    - lighting and chunk generation jobs must not regress by more than `2%` (they touch metadata much less)
    - any regression above those budgets blocks the feature from shipping until the LUT, meshing variant cache, or schema decode path is profiled and fixed

## 12. Validation Rules

The editor should validate the authoring configuration in `BlockDatabase`:

- fluid blocks must use `FluidLevel4`
- non-fluid blocks should not use `FluidLevel4`
- `defaultMetadata` must fit the selected schema
- `None` should default to `0`
- `Axis3` should only allow values `0-2`
- `Facing6` should only allow values `0-5`
- `Facing6Roll2` should only allow facing `0-5` and roll `0-3`
- warn if a block selects an expensive orientation schema but its mesh/textures are fully symmetric

These are authoring-time concerns and should be caught before runtime.

## 13. Testing Requirements

The schema system should not ship without mapping and migration tests.

Required coverage:

- raw metadata round-trip tests per schema
- legacy orientation byte -> schema-aware decoded state tests
- chunk voxel migration tests for affected block identities
- `pending_mods.bin` migration tests for the `VoxelMod` rewrite
- render-orientation tests for `Axis3` logs
- explicit tests covering the current non-identity face mapping in `BurstVoxelDataBitMapping`
- migration idempotence round-trip: load a pre-migration world, run migration, save back to disk, re-load — no further migration must run, and the re-loaded chunk/pending-mod byte output must be identical to the post-migration save. This is the canonical sentinel test for migration correctness.

The most important failure to guard against is mixing:

- migrations that operate on raw storage bits
- helpers that operate on decoded world-facing values

Those are not the same thing in the current codebase.

## 14. Editor Tooling Integrations

Recommended editor integrations:

- Block Editor schema picker bound to `metadataSchema`
- live bit-usage meter per block, such as `uses 2/8 bits`
- preview mode that cycles all valid metadata states for the chosen schema
- schema-change guard dialog when a shipped block ID changes schema
- validator window or import-time validation that runs the rules from Section 12
- structure editor support for explicit per-voxel axis / metadata authoring

The goal is to make schema costs and schema behavior visible at author time rather than only at runtime.

## 15. In-Game Debug Integrations

Recommended runtime debug additions:

- `DebugScreen` should show raw metadata plus decoded schema view
- targeted-block gizmos should show axis / facing arrows for non-trivial schemas
- post-migration reporting should surface fallback counters and migrated block counts

Example debug lines, one per schema:

```text
Meta: 0x02 (00000010) | Schema: Axis3        | Axis: Z                  | ReservedBits: 000000
Meta: 0x04 (00000100) | Schema: Facing6      | Facing: Top              | ReservedBits: 00000
Meta: 0x0A (00001010) | Schema: Facing6Roll2 | Facing: Top  | Roll: 1   | ReservedBits: 000
Meta: 0x07 (00000111) | Schema: FluidLevel4  | FluidLevel: 7            | ReservedBits: 0000
```

"ReservedBits" should render the literal high-order bits so bugs that accidentally write into reserved space are immediately visible.

These tools will catch metadata misinterpretation bugs much faster than reading raw saves.

## 16. Open Questions

1. Should `VoxelState.Orientation` remain as a compatibility API, or should all callers migrate to schema-aware metadata helpers?
2. Do we want `Facing6Roll2` in the first implementation, or should it be deferred until a concrete block actually needs it?
3. Should schema configuration live directly on `BlockType` permanently, or should the project later support reusable `BlockDatabase` schema presets?
4. Do we want the schema fingerprint to live entirely in `level.dat`, or should a copy also be embedded in future chunk-palette metadata once palette mapping exists?
5. When should editor hot-reload of `BlockType.metadataSchema` / `defaultMetadata` during Play mode become safe (per §7.5)? The first shipping cut can accept "pause Play mode before editing," but a long-term answer (atomic LUT swap, invalidate gate, or disallow) should be picked before this feature sees heavy authoring use.

## 17. Recommendation

The recommended path is:

- **Do not widen the voxel**
- **Do not adopt a universal full-orientation format for every block**
- **Do introduce per-block metadata schemas**
- **Start with `Axis3` for logs/fallen trunks**
- **Author schema selection through `BlockType` inside `BlockDatabase`**
- **Require a one-time AOT migration for all existing persisted voxel metadata affected by schema changes**
- **Normalize chunks and pending mods together so runtime voxel state only ever sees the new semantics**

This solves the current fallen-tree requirement cleanly, preserves Burst-friendly packed voxels, and leaves the engine with a much better long-term path for future metadata growth.
