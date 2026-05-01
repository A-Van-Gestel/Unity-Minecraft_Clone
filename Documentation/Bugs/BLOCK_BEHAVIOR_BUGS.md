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

**Design document:** [SUB_VOXEL_COLLISION_SYSTEM.md](../Design/SUB_VOXEL_COLLISION_SYSTEM.md)

---
