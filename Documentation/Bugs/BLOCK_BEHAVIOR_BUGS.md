# Known Block Behavior related bugs

This document outlines **open** bugs related to block behaviors (grass spreading, fluid simulation, etc.). Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

## 01. Fluid horizontal flow condition is slightly wrong

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (line 334)

The condition for allowing horizontal fluid flow evaluates to `true` if the neighbor is either non-solid or any fluid block. This means a solid fluid block (if one existed) would incorrectly pass, and water can be adjacent to lava without triggering interaction logic — the spread
is silently skipped rather than triggering a reaction (see also `FLUID_BUGS.md #04`).

---

## 02. Block Behavior Separation

**Severity:** Future Architecture  
**Files:** `BlockBehavior.cs`

Need to combine `Behave` and `Active` logic, or split active collections by block type.  
**Impact:** Performance bottleneck on main thread.

---

## 03. Additional Light Sources

**Severity:** Feature  
**Files:** Block Data

Add more block light sources other than lava.
eg: glowstone, torches, etc. Maybe also dedicated debug lights for each light level

---

## 04. Custom Mesh Collision Support

**Severity:** Feature  
**Files:** Physics/collision system, `BlockType`, Block Editor

All custom mesh blocks currently use a **full-block collision box** regardless of their actual shape (e.g., half-slabs collide as full cubes). This needs a proper collision system with two tiers:

1. **Generic collision from mesh data** — For simple custom meshes (half-slabs, stairs), derive collision geometry directly from the visual mesh data. Should work out of the box without per-block configuration.
2. **Simplified collision override** — For complex custom meshes with high polygon counts, provide an optional `CollisionMeshData` field on the block type that allows authors to specify a simpler collision hull (e.g., a box, a wedge) independent of the visual mesh.

**Additional requirements:**

- Collision geometry must be **rotation-aware** — rotated through the same `float3x3` matrix used for rendering (see `BurstCustomMeshRotationUtility`)
- Consider **caching** rotated collision data per orientation rather than rotating per physics query
- Profile impact on physics step time with high custom mesh density (prefer convex shapes)

**Editor / visualization tooling:**

- Block Editor integration to assign and preview collision meshes alongside visual meshes
- In-game debug visualization (e.g., wireframe overlay) to inspect collision bounds per block
- Visual distinction between "uses visual mesh as collision" vs "has custom collision override"

**Design document:** [SUB_VOXEL_COLLISION_SYSTEM.md](../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md)

---

## 05. Residual `Vector3Int`→`Vector3` far-coordinate reads on unguarded float query APIs

**Severity:** Suspected (unconfirmed — found by code audit, not reproduced)  
**Status:** Open — logged 2026-08-17 while fixing FLUID #17, as the "is that class fully closed?" sweep that fix did not perform.  
**Files:** `ChunkData.cs` (`GetState`), `Placement/PlacementController.cs` (:169), `DebugScreen.cs` (:299), `World.cs` (:4194), `WorldData.cs` (`GetVoxelState(Vector3)`)

**Description:**

FLUID #17 fixed the two float-typed helpers on the *wake* path (`World.GetChunkFromVector3`,
`Chunk.GetVoxelPositionInChunkFromGlobalVector3`). It did **not** sweep the rest of the float query surface, and
the same implicit `Vector3Int`→`Vector3` conversion is still live at several call sites. Each loses integer
precision past ±2²⁴ exactly as #17 did.

**Why no tripwire catches these:** `WorldData.AssertWithinFloatPrecision` is called from
`GetChunkCoordFor(Vector3)` only. `WorldData.GetVoxelState(Vector3)` does **not** route through it — it calls
`IsVoxelInWorld` (a Y-only bounds test since WS-3, so XZ magnitude is never inspected) and then
`Mathf.FloorToInt` directly. Every site below is therefore silent.

**Sites found (by descending stakes):**

1. **`ChunkData.GetState(Vector3Int localPos)` — `ChunkData.cs:1524`.** When the position is outside this chunk it
   *constructs* a `Vector3` from integers (`new Vector3(localPos.x + Position.x, …)`) and calls
   `worldData.GetVoxelState`. This is the **cross-chunk neighbour read used by `BlockBehavior.Behave`**, so it is
   the read-side twin of the wake-side bug #17 fixed. Interior reads stay integer; only seam-crossing reads take
   the float path. Note the Burst fluid path (TG-4 Phase 4b) uses the Y-band halo instead, so the exposure is
   mainly the managed path (grass, and the fluid fallback) — **needs confirming, not assuming**.
2. **`PlacementController.cs:169`** — `Vector3Int placeVoxel` passed to `World.IsCellOccupiedForPlacement(Vector3)`,
   which forwards to `GetVoxelState(Vector3)`. The player's placement occupancy veto could consult the wrong cell
   at far coordinates. (The adjacent `IsVoxelInWorld`/`IsVoxelInsideBorder` calls on the same line are unaffected —
   both are Y/border tests.)
3. **`World.cs:4194`** — visualization path; `worldBlockOrigin` is already a `Vector3`, so this is float-native
   rather than a converted integer, but it shares the precision ceiling.
4. **`DebugScreen.cs:299`** — `_world.GetVoxelState(Vector3Int targetVoxel)`. Cosmetic (debug HUD readout only).

**Suggested fix:** the same shape that worked for #17 — give `GetVoxelState` / `IsCellOccupiedForPlacement` a
`Vector3Int` overload (or retype where no float caller exists), and change `ChunkData.GetState`'s out-of-chunk
branch to hand integers across instead of building a `Vector3`. Prefer *deleting* the float parameter wherever no
genuine float caller exists — that converts the class from "fixed once" to "impossible", which is what made the
#17 fix durable.

**Do not assume severity from the sites alone.** None of these have been reproduced in-game; the onset is graded
(one-voxel error just past ±2²⁴, growing with magnitude — see `_FIXED_BUGS.md` Fluid #17), so bracketing is the
first diagnostic step for each.

---
