# Sub-Voxel Collision System

**Status:** Implemented (Automated Tests Pending) — core runtime collision, placement API separation, Block Editor authoring, editor preview, and in-game collision-bounds debug visualization are fully implemented and playtested. Automated regression tests remain outstanding.
**Target Engine:** Unity 6.4+  
**Dependencies:** Phase 4 Custom Mesh Rotation (`BurstCustomMeshRotationUtility`)  
**Related:** `VoxelRigidbody.cs`, `World.CheckPhysicsCollision()`, `World.IsCellOccupiedForPlacement()`, `BlockType`, Block Editor
**Last Reviewed:** May 2026

## Revision History

| Date       | Change                                                                                                                                                                                                                                                                                 |
|------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 2026-04-30 | Initial draft                                                                                                                                                                                                                                                                          |
| 2026-04-30 | Revision 1 (Codex): separated occupancy vs physics APIs, replaced point-probe solver with AABB-vs-AABB contact queries, narrowed scope to rectangular sub-blocks, added `CollisionBoundsMode` enum, fixed Burst marshaling                                                             |
| 2026-04-30 | Revision 2 (Codex): axis-specific contact queries, swept AABB tunneling guard, corrected step-up logic, schema-aware rotation path, placement API semantics, `IsEffectivelyFullBlock` validation, `BlockTypeJobData` consumer clarification, 90° permutation matrix terminology        |
| 2026-04-30 | Revision 3 (Codex): direction-aware physics query, downward sweep for step-up, dynamic tunneling threshold from min collision thickness, coarse placement API disclaimer, replaceable tag gap noted, deferred `BlockTypeJobData` collision fields, corrected rotated component wording |
| 2026-04-30 | Revision 4 (Codex): direction-specific multi-contact aggregation, step-up preserves horizontal velocity, substepping-only tunneling guard (removed union-scan from API), fixed stale pseudocode signatures                                                                             |
| 2026-05-01 | Implementation status update: core runtime/editor system is implemented; document wording moved to present tense and remaining gaps called out explicitly.                                                                                                                             |
| 2026-05-01 | Implementation Complete: runtime `DebugVisualizationMode.CollisionBounds` implementation finished and optimized. All features (except automated tests) are complete.                                                                                                                   |

## 1. Executive Summary

The engine supports sub-voxel collision for rectangular custom-mesh blocks. Blocks can define per-block-type collision bounds, rotated by the block's metadata orientation, while preserving the performance characteristics of a voxel grid.

Custom mesh blocks such as half-slabs no longer need to collide as full 1×1×1 cubes. The player can stand on the authored collision surface and move through empty space outside that surface.

> **Scope limitation**: Phase 6 implements **single-AABB rectangular sub-blocks** only (half-slabs, quarter-slabs, pillars). Stairs, L-shapes, and wedges require multi-AABB compound shapes and are deferred to a future phase (see §7).

## 2. Previous Problem Analysis

### 2.1. Previous Collision Pipeline

```
VoxelRigidbody.CalculateVelocity()
  ├── CheckDownSpeed(y)        → 4 corner point checks at feet
  ├── CheckUpSpeed(y)          → 4 corner point checks at head
  └── CheckHorizontalCollision(dx, dz) → sweep 2 edge points per voxel height step
        └── World.CheckForCollision(Vector3 pos)
              └── GetVoxelState(pos) → bool (isSolid && !isFluid)
```

The previous collision path used **binary whole-voxel tests**: "is the voxel at `floor(pos)` solid?" It had no concept of *where within the voxel* the point fell.

### 2.2. Specific Failures

| Scenario                                       | Implemented Behavior        | Previous Behavior                      |
|------------------------------------------------|-----------------------------|----------------------------------------|
| Standing on a bottom half-slab                 | Player stands at Y + 0.5    | Player stands at Y + 1.0 (full block)  |
| Walking through the empty top of a bottom slab | Player passes through       | Player blocked by invisible wall       |
| Rotated slab (e.g., wall-slab facing East)     | Collision on East half only | Full block collision                   |
| Walking from half-slab to full block           | Smooth step-up of 0.5       | Already at full height, no step needed |
| Two adjacent differently-rotated slabs         | Fills the full block space  | Over-sized collision, can't enter gap  |

### 2.3. Resolved Dual Semantics

`World.CheckForCollision(Vector3)` previously served **two distinct purposes**:

1. **Voxel occupancy** (`PlayerInteraction.cs:260`): "Is this voxel cell occupied?" — used for block placement validation. Should return `true` for any solid block at a grid position, regardless of sub-voxel shape. A half-slab still *occupies* the cell, even if the query point is in the empty half.

2. **Physics collision** (`VoxelRigidbody.cs`): "Does the entity's body overlap solid geometry at this point?" — used for movement resolution. Must account for sub-voxel shape.

The implementation separates these concerns. Placement preview uses `World.IsCellOccupiedForPlacement(Vector3)`, while entity movement uses `World.CheckPhysicsCollision(Bounds, axis, directionSign, out CollisionContact)`.

## 3. Design

### 3.1. Collision Shape Data Model

Each block type defines a **collision bounds mode** via a serialized enum, with an optional AABB override:

```csharp
/// <summary>
/// Determines how collision bounds are computed for a block type.
/// </summary>
public enum CollisionBoundsMode : byte
{
    /// <summary>Standard 1×1×1 cube collision. No sub-voxel checks.</summary>
    FullBlock = 0,
    
    /// <summary>Custom AABB specified by Min/Max in local block space.</summary>
    CustomAABB = 1,
    
    /// <summary>Derive AABB from the visual mesh's bounding box (editor-time).</summary>
    MatchVisualMesh = 2,
}

[Serializable]
public struct BlockCollisionBounds
{
    /// <summary>How collision bounds are determined.</summary>
    public CollisionBoundsMode mode;
    
    /// <summary>Minimum corner in local block space (0,0,0 = block origin).
    /// Only used when Mode is CustomAABB or MatchVisualMesh.</summary>
    public Vector3 min;
    
    /// <summary>Maximum corner in local block space (1,1,1 = block far corner).
    /// Only used when Mode is CustomAABB or MatchVisualMesh.</summary>
    public Vector3 max;
    
    /// <summary>True if collision bounds differ from the full block.
    /// Derived from Mode AND actual bounds values — not serialized independently.
    /// A CustomAABB with min=(0,0,0) max=(1,1,1) is treated as full-block.</summary>
    public bool HasCustomBounds => mode != CollisionBoundsMode.FullBlock
                                && !IsEffectivelyFullBlock;
    
    /// <summary>True if Min/Max are equal to full-block bounds, regardless of Mode.
    /// Prevents false-positive sub-voxel checks for misconfigured custom AABBs.</summary>
    public bool IsEffectivelyFullBlock =>
        min == Vector3.zero && max == Vector3.one;
    
    /// <summary>Full-block bounds (default for solid blocks).</summary>
    public static readonly BlockCollisionBounds FullBlock = new()
    {
        mode = CollisionBoundsMode.FullBlock,
        min = Vector3.zero,
        max = Vector3.one
    };
    
    /// <summary>Bottom half-slab bounds.</summary>
    public static readonly BlockCollisionBounds BottomHalfSlab = new()
    {
        mode = CollisionBoundsMode.CustomAABB,
        min = Vector3.zero,
        max = new Vector3(1f, 0.5f, 1f)
    };
}
```

**Key design decisions:**

- **`HasCustomBounds` is derived, not serialized.** It is computed from both `mode` AND actual `min`/`max` values — a `CustomAABB` whose bounds equal the full block `(0,0,0)→(1,1,1)` still takes the fast path. Editor validation prevents invalid saved bounds. `MatchVisualMesh` bounds are populated from the generated mesh through the Block Editor's editor-time derivation action.
- **AABB, not mesh-based**: Collision shapes are always axis-aligned boxes *before rotation*. Mesh-based collision is too expensive for per-frame queries across potentially hundreds of voxels.
- **Single AABB per block type**: Phase 6 explicitly targets rectangular sub-blocks only. Stairs and wedges are deferred (see §7).
- **Presets**: Common shapes (FullBlock, BottomHalfSlab, TopHalfSlab, BottomQuarterSlab) are provided as static presets selectable in the Block Editor.
- **Editor migration**: Existing `BlockDatabase.asset` entries default to `FullBlock` mode. The Block Editor must validate bounds on save, not hand-edit the asset.

### 3.2. Rotation-Aware Collision

The collision bounds are defined in the block's **canonical (unrotated) local space**. At query time, the block's AABB is rotated into world space by transforming its 8 corners through the rotation matrix, then computing the world-space AABB of the result.

**Obtaining the rotation matrix**: The physics path MUST use the same schema-aware dispatch as rendering. This means calling `BurstCustomMeshRotationUtility.GetRotationMatrix(schema, meta, defaultMeta)` — NOT `VoxelState.Orientation`, which returns `0` for `Axis3` blocks. The `schema` and `defaultMeta` come from `BlockType`/`BlockTypeJobData`, and `meta` is decoded from the packed voxel data via `BurstVoxelDataBitMapping.GetMeta(packedData)`.

```csharp
/// <summary>
/// Returns the world-space AABB of a block's collision shape after rotation.
/// </summary>
public static Bounds GetRotatedWorldBounds(
    Vector3Int blockOrigin, BlockCollisionBounds localBounds,
    float3x3 rotationMatrix)
{
    float3 center = new float3(0.5f, 0.5f, 0.5f);
    float3 localMin = (float3)localBounds.Min;
    float3 localMax = (float3)localBounds.Max;
    
    // Rotate all 8 corners of the local AABB through the block's rotation matrix
    // and find the enclosing world-space AABB.
    float3 worldMin = new float3(float.MaxValue);
    float3 worldMax = new float3(float.MinValue);
    
    for (int i = 0; i < 8; i++)
    {
        float3 corner = new float3(
            (i & 1) == 0 ? localMin.x : localMax.x,
            (i & 2) == 0 ? localMin.y : localMax.y,
            (i & 4) == 0 ? localMin.z : localMax.z);
        
        float3 rotated = math.mul(rotationMatrix, corner - center) + center;
        worldMin = math.min(worldMin, rotated);
        worldMax = math.max(worldMax, rotated);
    }
    
    // Offset to world position
    float3 origin = new float3(blockOrigin.x, blockOrigin.y, blockOrigin.z);
    return new Bounds(
        (Vector3)((worldMin + worldMax) * 0.5f + origin),
        (Vector3)(worldMax - worldMin));
}
```

Since all rotation matrices are **90° signed permutation matrices** (not merely orthogonal — they are specifically axis-aligned 90° rotations with det=+1), the rotated AABB components are always **exact permutations/reflections of the authored bounds values** — no irrational values or floating-point drift. For a half-slab `(0,0,0)→(1,0.5,1)`, rotating 90° around X produces `(0,0,0)→(1,1,0.5)` exactly.

### 3.3. Separated Collision APIs

Collision handling is separated into **three** distinct APIs:

```csharp
// === API 1: Block Placement Occupancy (coarse check) ===

/// <summary>
/// Coarse check: returns true if the voxel cell is occupied by a solid, non-fluid
/// block. Sub-voxel shape is irrelevant — a half-slab still "occupies" its cell.
/// </summary>
/// <remarks>
/// WARNING: This is a COARSE preview check only. It does NOT account for
/// replaceable blocks (BlockTags.REPLACEABLE) or incoming-vs-existing
/// replacement rules. The authoritative placement validation remains in
/// the existing placement pipeline (PlayerInteraction / World.ModifyVoxel),
/// which checks placementCanReplaceTags and incoming block compatibility.
///
/// The player placement-permission gate is World.CanPlayerPlaceAt(Vector3Int,
/// BlockType), which composes this occupancy check + world bounds + the
/// REQUIRES_SUPPORT rule (a support-needing block, e.g. grass blades, is
/// rejected unless the cell below ProvidesSupport — see PlacementResolver.
/// HasRequiredSupport). Replaceability is still resolved separately upstream
/// in PlacementResolver before the place cell is passed here.
/// </remarks>
public bool IsCellOccupiedForPlacement(Vector3 pos)
{
    VoxelState? voxel = worldData.GetVoxelState(pos);
    if (!voxel.HasValue) return false;
    ushort id = voxel.Value.ID;
    if (id == BlockIDs.Air) return false;
    BlockType props = blockDatabase.blockTypes[id];
    // Coarse check: solid + non-fluid. Does NOT check BlockTags.REPLACEABLE.
    // Full replacement logic lives in the placement pipeline.
    return props.isSolid && props.fluidType == FluidType.None;
}

// === API 2: Raycast Hit Detection (unchanged) ===
// World.CheckForVoxel(Vector3, includeFluids, includeNonSolid) — already has
// the correct fluid/non-solid semantics. No changes needed.

// === API 3: Physics Collision (sub-voxel aware, axis + direction) ===

/// <summary>
/// Tests whether an entity AABB overlaps any solid collision geometry along a
/// specific movement axis and direction. Aggregates across all overlapping blocks
/// and returns the correction that fully resolves ALL overlaps on this axis.
/// </summary>
/// <param name="entityBounds">The entity's predicted world-space AABB.</param>
/// <param name="axis">The movement axis to resolve (0=X, 1=Y, 2=Z).</param>
/// <param name="directionSign">+1 for positive movement, -1 for negative.
/// Determines which face of the block AABB to resolve against.
/// For axis=1, directionSign=-1 (falling) resolves against block top;
/// directionSign=+1 (jumping) resolves against block bottom.</param>
/// <param name="contact">If overlap detected, contains axis-specific resolution.</param>
/// <returns>True if there is any overlap on the specified axis.</returns>
public bool CheckPhysicsCollision(
    Bounds entityBounds, int axis, int directionSign, out CollisionContact contact)
{
    // 1. Determine which voxel cells the entity AABB overlaps (grid scan)
    //    Caller is responsible for substepping large velocities (§3.4.4);
    //    this method only tests the provided AABB as-is.
    // 2. For each occupied solid cell:
    //    a. Full-block fast path: test entity AABB vs full 1×1×1 cube
    //    b. Custom bounds: get rotated world AABB via GetRotatedWorldBounds
    //       using BurstCustomMeshRotationUtility.GetRotationMatrix(schema, meta, defaultMeta)
    //    c. Compute penetration on the requested axis + direction only
    //       e.g., axis=1 dir=-1: correction = blockBounds.max.y - entityBounds.min.y
    //             axis=1 dir=+1: correction = blockBounds.min.y - entityBounds.max.y
    // 3. Aggregate across ALL overlapping blocks on this axis+direction:
    //    - dir=-1 (falling):   choose HIGHEST blockBounds.max  (entity rests on tallest support)
    //    - dir=+1 (jumping):   choose LOWEST  blockBounds.min  (entity stops at first ceiling)
    //    - dir=-1 (horiz):     choose HIGHEST blockBounds.max  (nearest blocking face)
    //    - dir=+1 (horiz):     choose LOWEST  blockBounds.min  (nearest blocking face)
    //    i.e., always pick the contact that produces the LARGEST absolute correction,
    //    which fully resolves ALL overlaps on this axis, not just the shallowest one.
}
```

**`CollisionContact` struct:**

```csharp
/// <summary>
/// Contact information from a physics collision query.
/// </summary>
public struct CollisionContact
{
    /// <summary>Whether a collision was detected on the queried axis.</summary>
    public bool Hit;
    
    /// <summary>The signed correction to apply on the queried axis.
    /// Positive = entity should move in +axis direction to exit overlap.</summary>
    public float Correction;
    
    /// <summary>The world-space coordinate of the contact face on the queried axis.
    /// For Y-down: top surface of the block shape. For X+: left face. Etc.</summary>
    public float ContactFace;
}
```

**Caller migration:**

| Caller                                    | Previous API               | Implemented API                          | Reason                                    |
|-------------------------------------------|----------------------------|------------------------------------------|-------------------------------------------|
| `VoxelRigidbody.CheckDownSpeed`           | `CheckForCollision(point)` | `CheckPhysicsCollision(bounds, 1, -1)`   | Y-down: resolve against block top face    |
| `VoxelRigidbody.CheckUpSpeed`             | `CheckForCollision(point)` | `CheckPhysicsCollision(bounds, 1, +1)`   | Y-up: resolve against block bottom face   |
| `VoxelRigidbody.CheckHorizontalCollision` | `CheckForCollision(point)` | `CheckPhysicsCollision(bounds, 0/2, ±1)` | Per-axis X or Z with movement sign        |
| `PlayerInteraction.cs:260` (placement)    | `CheckForCollision(pos)`   | `IsCellOccupiedForPlacement(pos)`        | Coarse grid-occupancy (see API 1 remarks) |
| `World.CheckForVoxel` (raycast)           | unchanged                  | unchanged                                | Already has fluid/non-solid parameters    |

### 3.4. VoxelRigidbody Physics Solver

The previous solver used **point probes** (4 corners at specific heights). That approach was fundamentally incompatible with sub-voxel shapes — a top slab occupying `0.5..1.0` could be entirely missed by probes at `collisionPadding` and `collisionPadding + 1.0`. It also risked regressing the previously fixed "sweep across full entity height" bug (see `_FIXED_BUGS.md #526`).

The implemented solver uses AABB-vs-AABB contact queries instead of point probes.

#### 3.4.1. Solver Architecture

The base fallback resolution order is **preserved**: Z → X → Y. Step-up is a **pre-pass** that probes whether horizontal movement is blocked and, if so, attempts to clear the obstruction by lifting the entity before any horizontal corrections are committed. If the step-up fails, the solver falls through to the normal Z → X → Y sequence. The Y axis resolves last to set `IsGrounded` correctly.

```
VoxelRigidbody.CalculateVelocity()
  ├── Substep if displacement > maxStep (§3.4.4)
  ├── Build entity AABB from position + extents
  ├── Predict future AABB (position + velocity)
  ├── TryStepUp() → if X or Z would block, test at +stepHeight FIRST (see §3.4.3)
  ├── ResolveAxis(Z) → CheckPhysicsCollision(futureAABB, 2, zSign) → apply correction
  ├── ResolveAxis(X) → CheckPhysicsCollision(futureAABB, 0, xSign) → apply correction
  └── ResolveAxis(Y) → CheckPhysicsCollision(futureAABB, 1, ySign) → apply correction
        └── World.CheckPhysicsCollision(Bounds, axis, directionSign, out CollisionContact)
              ├── Grid scan: which voxel cells does the AABB overlap?
              ├── Per cell: full-block fast path or rotated AABB overlap test
              └── Aggregate: pick contact that fully resolves ALL overlaps on this axis
```

Each axis query returns only the correction needed **on that specific axis+direction**. When multiple blocks overlap the entity on the same axis, the query aggregates by picking the contact that produces the **largest absolute correction** — this ensures ALL overlaps on that axis are resolved in one pass (e.g., standing on two adjacent half-slabs at different heights, the entity rests on the tallest one). The solver never picks a "global deepest" contact across axes — each axis is independent.

Each axis is resolved independently using the entity's full AABB, not corner points. The overlap test is a standard AABB-vs-AABB intersection:

```csharp
// AABB overlap test
bool overlaps = entityBounds.min.x < blockBounds.max.x
             && entityBounds.max.x > blockBounds.min.x
             && entityBounds.min.y < blockBounds.max.y
             && entityBounds.max.y > blockBounds.min.y
             && entityBounds.min.z < blockBounds.max.z
             && entityBounds.max.z > blockBounds.min.z;
```

This eliminates the probe-skipping failure mode entirely — if any part of the entity AABB overlaps any part of the block AABB, it's detected.

#### 3.4.2. Vertical Resolution (Ground/Ceiling Snap)

```csharp
// Previous full-block assumption:
return Mathf.Floor(y) + 1f - pos.y;

// Implemented axis + direction contact query:
if (_world.CheckPhysicsCollision(predictedAABB, axis: 1, directionSign: -1, out CollisionContact contact))
{
    // directionSign=-1 (falling) resolves against block top face
    return contact.Correction;
}
```

For a bottom half-slab at block Y=5, `contact.ContactFace` = `5.5f`, `contact.Correction` = `5.5f - pos.y`. For a full block, `contact.ContactFace` = `6.0f`.

#### 3.4.3. Step-Up Logic

Walking from a half-slab (Y+0.5) onto a full block (Y+1.0) requires step-up. The step-up attempt runs **before** committing any horizontal corrections, so the original desired velocity is preserved on success:

```csharp
// Step-up is attempted BEFORE horizontal resolution. The original desired
// velocity is preserved so the entity actually moves onto the block.
// Only probe axes that have non-zero velocity to avoid false blocks.

bool zBlocked = false;
bool xBlocked = false;
int zSign = 0, xSign = 0;

if (velocity.z != 0f)
{
    zSign = velocity.z > 0 ? 1 : -1;
    zBlocked = _world.CheckPhysicsCollision(futureAABB, axis: 2, zSign, out _);
}
if (velocity.x != 0f)
{
    xSign = velocity.x > 0 ? 1 : -1;
    xBlocked = _world.CheckPhysicsCollision(futureAABB, axis: 0, xSign, out _);
}
bool horizontalBlocked = zBlocked || xBlocked;

// 2. If blocked and grounded, attempt step-up with ORIGINAL velocity
if (horizontalBlocked && IsGrounded)
{
    // a. Lift the entity AABB by stepHeight, using the ORIGINAL uncorrected futureAABB
    Bounds liftedAABB = futureAABB;
    liftedAABB.center += Vector3.up * stepHeight;
    
    // b. Re-test horizontal movement at the lifted height (only axes with movement)
    bool clearsAtStep = true;
    if (velocity.x != 0f)
        clearsAtStep &= !_world.CheckPhysicsCollision(liftedAABB, axis: 0, xSign, out _);
    if (velocity.z != 0f)
        clearsAtStep &= !_world.CheckPhysicsCollision(liftedAABB, axis: 2, zSign, out _);
    
    // c. If clear, sweep DOWNWARD to find highest support surface
    if (clearsAtStep)
    {
        Bounds sweepAABB = liftedAABB;
        sweepAABB.Expand(new Vector3(0, stepHeight, 0));
        sweepAABB.center -= new Vector3(0, stepHeight * 0.5f, 0);
        
        if (_world.CheckPhysicsCollision(sweepAABB, axis: 1, directionSign: -1, out var groundContact))
            pos.y = groundContact.ContactFace;
        else
            pos.y = liftedAABB.center.y - liftedAABB.extents.y;
        
        // SUCCESS: horizontal velocity is preserved as-is (no correction applied).
        // The entity moves onto the block with the original X/Z velocity.
        horizontalBlocked = false;
    }
}

// 3. If step-up failed (or wasn't attempted), resolve horizontal normally
if (horizontalBlocked)
{
    if (zBlocked)
    {
        _world.CheckPhysicsCollision(futureAABB, axis: 2, zSign, out var zContact);
        ApplyCorrection(ref velocity.z, zContact);
    }
    if (xBlocked)
    {
        _world.CheckPhysicsCollision(futureAABB, axis: 0, xSign, out var xContact);
        ApplyCorrection(ref velocity.x, xContact);
    }
}
```

Key invariant: step-up probes the **original** desired position. If the step clears, horizontal velocity is not modified — the entity slides onto the block. Only if step-up fails does the solver fall through to normal horizontal correction.

#### 3.4.4. Tunneling Prevention

The previous full-block system was mostly immune to tunneling because every voxel was 1m thick and the entity moved < 1m per frame at normal speeds. Sub-voxel shapes (0.25m quarter-slabs) quarter the minimum collidable thickness, and the adjustable `flyingSpeed` in `VoxelRigidbody` can produce large displacements.

**Mitigation**: The solver **substeps** large displacements. `CheckPhysicsCollision` itself only tests the provided AABB as-is — it does not perform swept/union scans internally. Tunneling prevention is handled by `VoxelRigidbody`:

```csharp
// Derive maxStep from the minimum supported collision thickness.
// For quarter-slabs (0.25m), maxStep must be < 0.25/2 = 0.125m.
// This constant should be updated if thinner collision shapes are added.
const float MIN_COLLISION_THICKNESS = 0.25f; // Quarter-slab
float maxStep = MIN_COLLISION_THICKNESS * 0.5f; // 0.125m

float displacement = velocity.magnitude * Time.fixedDeltaTime;
if (displacement > maxStep)
{
    int substeps = Mathf.CeilToInt(displacement / maxStep);
    Vector3 subVelocity = velocity / substeps;
    for (int i = 0; i < substeps; i++)
    {
        // Each substep runs the full TryStepUp → Z → X → Y resolution
        // with the fractional velocity. Position is accumulated between substeps.
        ResolveMovement(subVelocity);
    }
}
else
{
    ResolveMovement(velocity);
}
```

This ensures thin sub-voxel shapes are never skipped, even at high velocities. The `CheckPhysicsCollision` API remains simple (single AABB in, contact out).

### 3.5. Data Ownership

Collision bounds are stored in `BlockType` (managed, editor-serialized) as the **source of truth**. The physics query runs on the main thread and reads from `BlockType` directly.

**`BlockTypeJobData` does NOT receive collision fields in Phase 6.** Adding unused fields to this hot shared struct would widen it for all Burst meshing jobs without a consumer. If collision queries are later moved to a Burst job (e.g., for NPC physics), collision data should be added as a **separate `NativeArray<BlockCollisionJobData>`** passed alongside `BlockTypeJobData`, not embedded in it.

## 4. Editor Tooling

### 4.1. Block Editor Integration

The Block Editor provides a **Collision Bounds** section with:

- **Mode selector**: `Full Block` (default) | `Custom AABB` | `Match Visual Mesh` (auto-derive from mesh bounds)
- **Min/Max Vector3 fields** when `Custom AABB` is selected
- **Preset dropdown**: `Bottom Half Slab`, `Top Half Slab`, `Quarter Slab`, `Full Block`
- **Live wireframe preview** overlaid on the mesh preview showing the collision bounds in a distinct color (e.g., green wireframe)
- **Validation on save**: ensures `Min < Max`, values within `[0,1]`, and `MatchVisualMesh` entries have populated bounds

### 4.2. In-Game Debug Visualization

`DebugVisualizationMode.CollisionBounds` renders runtime wireframe AABBs for solid blocks in visible chunks:

- Green wireframes show standard full-block collision.
- Yellow wireframes show custom collision bounds.
- Red wireframes show custom bounds whose visual mesh vertices extend beyond the authored collision AABB, indicating a potential clip-through mismatch.

## 5. Implementation Phases

### Phase 6a — Data Model & Editor

- [x] Add `CollisionBoundsMode` enum
- [x] Add `BlockCollisionBounds` struct with `mode`, `min`, `max`, derived `HasCustomBounds`
- [x] Add `collisionBounds` field to `BlockType` with `FullBlock` default
- [x] ~~`BlockTypeJobData` collision fields deferred~~ — no Burst consumer in Phase 6
- [x] Add collision bounds editor UI to Block Editor with validation
- [x] Editor migration: existing `BlockDatabase.asset` entries initialize to `FullBlock`
- [x] Configure half-slab blocks with sub-voxel bounds

### Phase 6b — Separated APIs & AABB Queries

- [x] Add `World.IsCellOccupiedForPlacement(Vector3)` — coarse grid-only check (document limitation vs `BlockTags.REPLACEABLE`)
- [x] Migrate `PlayerInteraction.cs` placement check to `IsCellOccupiedForPlacement`
- [x] Preserve existing `World.CheckForVoxel` (raycast) unchanged
- [x] Add `CollisionContact` struct with `Hit`, `Correction`, and `ContactFace`
- [x] Implement `World.CheckPhysicsCollision(Bounds, axis, directionSign, out CollisionContact)` — direction-aware
- [x] Implement direction-specific multi-contact aggregation (largest absolute correction resolves all overlaps)
- [x] Implement `GetRotatedWorldBounds` using `BurstCustomMeshRotationUtility.GetRotationMatrix`
- [x] Full-block fast path (skip rotation for `FullBlock` mode and `IsEffectivelyFullBlock`)
- [ ] Unit tests: AABB overlap for unrotated and rotated bounds, occupancy vs physics separation

### Phase 6c — VoxelRigidbody Solver Rewrite

- [x] Replace point-probe pattern with AABB-vs-AABB contact queries
- [x] Preserve Z → X → Y resolution order
- [x] Implement per-axis resolution using `CheckPhysicsCollision(bounds, axis, directionSign)`
- [x] Replace `Mathf.Floor + 1` ground snap with `contact.ContactFace`
- [x] Add `stepHeight` parameter and step-up BEFORE horizontal commit (preserve velocity on success)
- [x] Add caller-side tunneling substep with `MIN_COLLISION_THICKNESS`-derived maxStep
- [x] Handle edge cases through the implemented solver: slab-to-full transitions, adjacent rotated slabs, falling onto slabs
- [ ] Regression test: verify existing full-block movement is unchanged
- [ ] Regression test: verify "sweep across full entity height" bug (#526) does not reappear
- [ ] Regression test: verify no tunneling through quarter-slabs at max flying speed
- [ ] Regression test: multi-contact aggregation (entity on two half-slabs at different heights)
- [ ] Regression test: horizontal velocity preserved after successful step-up
- [ ] Regression test: step-up from half-slab to full block correctly finds support
- [x] Extensive playtesting of movement scenarios

### Phase 6d — Editor & Debug Tooling

- [x] Block Editor collision wireframe preview overlay
- [x] `DebugVisualizationMode.CollisionBounds` for in-game visualization
- [x] "Match Visual Mesh" editor-time derive option

## 6. Performance Considerations

| Concern                               | Mitigation                                                                                                                                                                                                 |
|---------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| AABB overlap per candidate voxel cell | Full-block fast path (majority of blocks) skips rotation and uses integer grid check. Sub-voxel AABB test only for blocks with `HasCustomBounds`.                                                          |
| Grid scan range                       | Entity AABB typically spans 2-4 voxel cells per axis. Maximum ~64 cells for a 4×4×4 scan — trivial.                                                                                                        |
| Rotated AABB computation              | Inline 8-corner rotation + min/max avoids managed allocations in the physics path. For 90° multiples, result is exact integers/halves. Could pre-cache per block-type × orientation if profiling warrants. |
| Solver call frequency                 | `FixedUpdate` at 50Hz, 1 entity. AABB tests are branchless arithmetic — negligible.                                                                                                                        |
| Tunneling substeps                    | At 50Hz with max flying speed ~20m/s, displacement ≈ 0.4m/frame. With `maxStep=0.125m`, worst case = 4 substeps. Negligible.                                                                               |

## 7. Limitations & Future Work

- **Single AABB per block type (Phase 6 scope)**: Phase 6 explicitly targets rectangular sub-blocks only (half-slabs, quarter-slabs, pillars). Complex shapes require compound collision:
    - **Stairs**: 2 AABBs (bottom tread + top tread). Future `CompoundCollisionBounds` with `NativeArray<BlockCollisionBounds>`.
    - **Wedges**: AABB approximation only (the diagonal is not representable). Accept over-sized collision or implement OBB/triangle queries.
    - **L-shapes**: 2+ AABBs.
- **No per-voxel collision variation**: All instances of a block type share the same collision shape (modulo rotation). Blocks that change shape based on neighbors (e.g., fence posts connecting) would need runtime collision computation.
- **No mesh-based collision**: We intentionally avoid using the visual mesh as collision geometry. The per-frame AABB-overlap pattern is incompatible with arbitrary triangle meshes at voxel density. For blocks needing precise collision, the AABB can be oversized with visual details protruding — an acceptable trade-off.
- **Automated collision regression tests are pending**: core behavior has been playtested, but the AABB query and movement edge cases listed in §5 still need test coverage.
