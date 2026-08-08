# Known Meshing related bugs

This document outlines **open** bugs related to the current meshing implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Meshing`
> (`Assets/Editor/Validation/Meshing/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry;
> **Bug M02 has none yet** — write one before fixing it.
>
> Harness blind spots that limit what a green suite proves are tracked in
> [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).

---

## Bug M02: A Custom Mesh's Mid-Plane Face Is Culled by the Block-Boundary Neighbor

**Severity:** Medium  
**Status:** Open  
**Found:** 2026-08-08, while verifying VO-6 (predicted from reading the culling path, then confirmed).

**Description:**
Both custom-mesh paths decide face visibility with
`ShouldDrawFace(voxelProps, GetVoxelStateFromLocalPos(pos + rotatedOffset))` — the block-boundary
neighbor — regardless of where the face actually sits inside the cell. That is right for a boundary
face and wrong for an interior one: a half slab's mid-plane face is half a block *inside* its own
cell, with the cell's open half between it and that neighbor, so a full block beyond the gap cannot
occlude it.

Confirmed numerically: a slab at `Facing6Roll2` meta `0x03` emits its mid-plane face at `z = 8.50`
when isolated, and **emits nothing** once a solid block is placed at `(8, 8, 7)` — a full cell away.
The surface is genuinely visible through the slab's open half from a grazing angle, so this renders
as a hole rather than a hidden face.

**This is the culling twin of [Bug M01](./_FIXED_BUGS.md#meshing)** (the same confusion in the
*lighting* sample, fixed by VO-6 and archived) and it
survived the VO-6 fix deliberately: VO-6 moved only the light sample, because changing culling changes
emitted vertex counts and would move a large number of meshing baselines in one step.

**The fix is the same derivation VO-6 already added** — `MeshGenerationJob.ResolveFaceSampleCell`
returns the cell a face actually looks into, and the cull check wants that same cell rather than
`pos + rotatedOffset`. The reason it is filed instead of applied: the blast radius is geometric, so it
needs its own prove-red plus a sweep of the standard-cube and custom-mesh geometry baselines.

**Reproduction Steps:**
Place a `Stone Half Slab` rotated to vertical against a solid wall so its large face points at the
wall, then look along the wall. The slab's large face is missing.

**Repro scenario:** none yet — write one before fixing.

**Testing environment:** Editor, August 2026.
