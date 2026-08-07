# Known Meshing related bugs

This document outlines **open** bugs related to the current meshing implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Meshing`
> (`Assets/Editor/Validation/Meshing/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.
>
> Harness blind spots that limit what a green suite proves are tracked in
> [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).

---

## Bug M01: Sub-Block Custom-Mesh Faces Sample Smooth Light From the Wrong Cell

**Severity:** Medium  
**Status:** Open  
**Related:** [`LIGHTING_BUGS.md`](./LIGHTING_BUGS.md) Bug 20 (the lighting-model half of the same visual artifact — **Bug 20 must be fixed first**, see "Fix ordering" below)

**Description:**
`MeshGenerationJob.GenerateCustomBlockMesh_SchemaAware` computes one smooth-light corner quad per emitted face via
`CalculateCornerLights(worldFace, pos, …)`. That quad is defined entirely by the block cell `pos` and the world
direction `rotatedOffset`: it reads the direct light at `pos + rotatedOffset` and the eight-cell ambient-occlusion ring
around **that** cell.

This is correct only for a face that lies on the block boundary. A custom mesh's *interior* faces do not — a half slab's
large face sits at the mesh's mid-plane, half a block inside its own cell. For such a face:

- the direct light **should** come from the cell in front of the surface (the block's own cell, whose remaining half is
  open), but is taken from `pos + rotatedOffset`, a full block further away;
- the AO ring **should** be centred on that same cell, but is centred on `pos + rotatedOffset`.

The per-vertex bilinear blend *within* the face is correct — `GetCornerUV` was verified against the corner-offset LUT
that `BurstVoxelData.BuildCornerOffsetLUT` generates, corner by corner, for all six faces, and the documented
`l0↔(0,0), l1↔(0,1), l2↔(1,0), l3↔(1,1)` mapping holds exactly. The defect is the *source quad*, not the interpolation.

**Why rotation makes it visible.** Unrotated, a bottom slab's mid-plane face points +Y and the cell above it usually
carries light similar to the slab's own cell, so the error is nearly invisible. Rotated to vertical (`Facing6Roll2`
facing 3, rolls 0–3), the mid-plane face points at a *horizontal* neighbour whose light and occlusion ring have nothing
to do with what is actually in front of the surface — and each roll aims the face at a different neighbour.

**Documentation conflict:** `Architecture/SMOOTH_AND_RGB_LIGHTING.md` §2.5.2 currently claims this path *"correctly
handles sub-block geometry (half slabs, fences, stairs)"*. That claim is true of the bilinear blend and false of the
sampled quad; it must be corrected when this bug is fixed.

**Reproduction Steps:**
Same world setup as `LIGHTING_BUGS.md` Bug 20 — a one-block pit ringed by four `Stone Half Slab`s at metadata `0x03`,
`0x0B`, `0x13`, `0x1B`, smooth lighting enabled.

**Fix ordering (important):**
The obvious fix — derive the sampling cell from the face's real position, e.g.
`sampleCell = floor(pos + rotatedFaceCentroid + 0.5 · rotatedNormal)`, which degenerates to today's `pos + rotatedOffset`
for boundary faces and yields `pos` for mid-plane faces — **cannot be applied on its own**. Under Bug 20 the slab's own
cell is fully opaque and therefore dark, so sampling it would render every slab black. Bug 20 must be fixed first so
that a partial block's cell carries a real light value.

**Repro scenario:** `KM01a` (meshing suite) — **landed and red**. Confirmed numerically: a slab's
mid-plane face at `z = 8.50` held light 136 when its own cell went 8→15, and moved to 166 when the
cell a block and a half away did. Asserts as a differential (own-cell change must move the face)
rather than a predicted value, so it needs no model of the corner-offset LUT; leg C is a positive
control. Uses the `PartialOpaque` palette fixture so it isolates this mesher defect from Bug 20.

**Fix phase:** [`VOXEL_OCCLUSION_REFACTOR.md`](../Design/VOXEL_OCCLUSION_REFACTOR.md) **VO-6** —
execution packet, including the prove-red (this scenario flips green) and the ordering precondition.

**Testing environment:** Editor, smooth lighting enabled, August 2026.
