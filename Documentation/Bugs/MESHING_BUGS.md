# Known Meshing related bugs

This document outlines **open** bugs related to the current meshing implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Meshing`
> (`Assets/Editor/Validation/Meshing/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry;
> **Bug M04 has none yet** — write one before fixing it.
>
> Harness blind spots that limit what a green suite proves are tracked in
> [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).

---

## Bug M04: Radiating "Star" Brightness Streaks Across Open Floor

**Severity:** Low–Medium (cosmetic, but widespread)  
**Status:** Open — **cause not yet established.** May belong in
[`LIGHTING_BUGS.md`](./LIGHTING_BUGS.md); the diagnostic below decides.  
**Found:** 2026-08-08, owner's in-game visual review.

**Description:**
Large open floor areas show faint brighter streaks radiating outward, several blocks long, forming a
star/fan pattern near placed slabs. Visible only with smooth lighting enabled.

**Ambient occlusion is ruled out.** `BurstVoxelData.BuildCornerOffsetLUT` only ever produces offsets of
±1 per axis, so AO's influence is confined to a block's 26-neighbourhood. It is structurally incapable of
producing streaks spanning several blocks, whatever the coverage model does.

**Remaining candidates, in order of suspicion:**

1. **The sky-light field itself.** A horizontal slab registers in the heightmap
   (`LightAttenuation.ObstructsSkyColumn` — correct, and required by the archived Bug 21), the column
   beneath it is removed, and horizontal BFS re-spread from surrounding lit cells produces diamond/fan
   decrement patterns. The mesher would then render those faithfully. The diagonal, radiating shape is
   characteristic of BFS spreading rather than of any shading term.
2. **Triangle-split seams.** `EmitQuadTriangles`' anisotropy-aware split changes which diagonal a quad is
   cut along; a wrong or inconsistent choice shows as visible diagonal discontinuities where corner
   values differ.

**Decisive diagnostic (run this first):** in play mode, dump `GetSkyLight` for the floor layer and the
air layer above it across the affected area. Uniform 15 ⇒ the cause is in meshing, and candidate 2 is
next. Any variation ⇒ the cause is the lighting engine and this entry moves to `LIGHTING_BUGS.md`.

**Repro scenario:** none yet — the diagnostic above must run first, since it determines which suite the
scenario belongs in.

**Testing environment:** Editor, smooth lighting High, August 2026.

