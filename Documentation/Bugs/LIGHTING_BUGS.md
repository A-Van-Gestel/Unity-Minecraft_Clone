# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

> All previously listed lighting bugs (01–04) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

## Bug 05: Persistent Chunk-Border Shadow Patches in Dense Biomes

**Severity:** Medium
**Status:** Open / Partially mitigated

**Description:**
Persistent dark/shadow patches occur primarily in visually dense areas (e.g., repeating overlapping tree canopies in forest biomes), typically originating near the borders between freshly generated chunks.
While cave systems spanning chunk borders are now correctly lit with the iterative edge-checking rounds fix, dense forested areas still occasionally fail to converge and leave permanent shadow patches that only resolve upon a full world reload
or partially resolve upon forcing light updates (eg: Removing leaves blocks).

**Root Cause Suspected:**
During generation, if two chunks both stabilize their initial lighting passes with stale neighbor snapshot data (e.g., both border columns report 0 sunlight), the standard `CheckEdges` logic doesn't register a discrepancy to correct.
We implemented iterative `RemainingEdgeCheckRounds = 2` to combat this, but dense overhead coverage appears to require more passes or a different convergence triggering algorithm across multi-chunk bounds. 

---

## Bug 06: Diagonal Shadow Artifacts on Smooth-Lit Legacy Rotated Blocks

**Severity:** Low (cosmetic)
**Status:** Open

**Description:**
With smooth lighting enabled, flat terrain surfaces (especially visible on sand/desert biomes) exhibit diagonal shadow lines forming a subtle zigzag or checkerboard pattern. The artifacts follow the quad triangulation diagonal and are most visible on large, uniformly lit horizontal surfaces viewed at a shallow angle.

**Root Cause:**
`GenerateStandardCubeWithLegacyOrientation` computes corner-averaged light values using the world face index `p` but emits vertices using the translated face index `translatedP` (which accounts for the block's Y-axis texture rotation). When the block has a non-zero yaw, the 4 corner light values are assigned to vertex positions that don't match the world positions they were sampled for. This causes the anisotropy fix (quad diagonal flip) in `GenerateStandardCubeFace` to compare mismatched diagonal pairs, sometimes choosing the wrong triangulation diagonal.

**Affected Blocks:**
All blocks using `MetadataSchema.HorizontalOnly` (stone, dirt, sand, gravel, and most terrain blocks) and `Legacy` schema blocks — i.e., the majority of visible surfaces.

**Fix Strategy:**
Permute the corner light values `(l0, l1, l2, l3)` to match the rotated vertex positions before passing them to `GenerateStandardCubeFace`. The permutation depends on the face direction (top/bottom vs side) and the yaw (0°, 90°, 180°, 270°). Top/bottom faces need a corner rotation matching the yaw; side faces need remapping based on which canonical face was selected by `translatedP`. See also: [Design doc Section 2.5.4](../Design/SMOOTH_AND_RGB_LIGHTING.md#254-legacy-rotated-blocks).
