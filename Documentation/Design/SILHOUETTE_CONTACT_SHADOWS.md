# Silhouette-Based Contact-Shadow Ambient Occlusion (SS-*)

**Version:** 1.2  
**Date:** 2026-08-09  
**Status:** Proposed design — not implemented.  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> The engine's ambient occlusion darkens a surface by *averaging in the light of the cells around
> it*, weighted by how much of each cell an occluder's volume fills. That model has two structural
> limits the `VO-*` arc measured but could not fix: a coverage fraction over a sub-cell box is
> **linear** across the cell for an axis-aligned slab, so subdividing the face reproduces the corner
> blend it replaced (`VOXEL_OCCLUSION_REFACTOR.md` **F18**); and the four-cell average weights an
> occluder by a **product** of two per-axis ramps, whose isocontours are hyperbolic — which is why
> the AO around an isolated cube reads as a round blob instead of a rectangle.
>
> **The decision this document settles: replace the *shape* of the occlusion signal with a distance
> field to the occluder's silhouette, while keeping the occlusion *primitive* an AABB-versus-AABB
> query.** The silhouette of a rotated volume on a face's plane is exactly the touch test and the two
> perpendicular extents that `BurstOcclusionUtility.GetFaceCoverage` already computes — it returns
> their *area*, this returns the *rectangle*. So the model stays shape-agnostic by construction: a
> fence post or any other single-AABB custom mesh needs no code of its own, which is the owner's
> hard requirement. `VO-9b`'s sub-quad tessellation is the substrate; nothing about it is redesigned.
>
> **Settled by the owner 2026-08-09 (§4 D1/D2/D3):** Euclidean distance to the silhouette,
> a `(1 − t)²` falloff, and **the silhouette field replaces the coverage fraction outright** — the
> AO path stops asking "how much volume fills this box" and asks only "how far is this point from
> something standing on the surface". Working that replacement through arithmetically produced the
> result that de-risks it: **at a cell corner with a fully-occluding neighbour the new model reduces
> to the old one exactly**, so ordinary full-cube terrain is unchanged until a phase deliberately
> subdivides it. Only `D7` (whether to subdivide faces next to *full* cubes, which is what delivers
> the second observation) remains open. Every visual phase still needs in-game sign-off.

**Audited:** 2026-08-09, at commit `d6df199a` (branch `feat/world-scaling`).
Read this session, in full or in the relevant regions: `Jobs/MeshGenerationJob.cs`
(`SampleFacePoint`, `SampleCornerPoint`, `PrepareFaceSampling`, `TangentSpan`,
`SampleNeighborLight`, `ShadeSubVertex`, `DirectOpenFractionAt`,
`EmitTessellatedStandardCubeFace`, `CalculateCornerLights`, `ResolveFaceSampleCell`, and every
`CalculateCornerLights` call site); `Jobs/BurstData/BurstOcclusionUtility.cs`
(`RotateLocalBounds`, `GetFaceCoverage`, `GetOctantCoverage`, `GetRegionCoverage`);
`Jobs/BurstData/LightAttenuation.cs` (the ambient-occlusion entry points and
`FullCoverageThreshold`); `Helpers/VoxelMeshHelper.cs` (`FaceQuad`, `GetStandardCubeFaceQuad`,
`GetSubQuad`, `EmitFaceQuad`, `BlendCornerLight`/`BilinearLerpLight`, `GetCornerUV`,
`GenerateStandardCubeFace`, `EmitQuadTriangles`);
`Assets/Editor/Validation/Meshing/MeshingValidationSuite.SubCellShading.cs` (**B49**) and
`MeshingValidationSuite.CornerOcclusion.cs` (`TopFaceCornerSun`);
`Assets/Editor/Validation/Meshing/Framework/TestCustomMeshLibrary.cs` and
`TestMeshBlockPalette.cs`; `Data/BlockCollisionBounds.cs`.
Numbers quoted from `VOXEL_OCCLUSION_REFACTOR.md` are that document's **measured** values and are
cited as such; everything this document derives from the code is marked *derived* and carries the
arithmetic, so `SS-0` can confirm it. No profiler capture was taken (`VO-8`'s waiver stands, §8).

**Amended:** 2026-08-09 — **D1, D2 and D3 decided by the owner, and D3's specification was corrected
in the process.** The Option C offered at decision time — "the four-cell blend reverts to a plain
light average, and a single global `(1 − s·SS)` factor supplies all darkening" — is **wrong**, and
§4 D3 records why in full: a bounded `[0,1]` occlusion field with one strength constant cannot
reproduce both "one occluder darkens to `191`" and "four occluders darken to `0`", so it would have
flattened every deep AO configuration (a 1×1 pit floor would have gone `64 → 191`). The correct form
gives each of the four cells meeting at a shaded point its own quarter share of the occlusion budget,
which reproduces both extremes and, at a cell corner with binary occlusion, is **algebraically
identical to today's blend**. That result removes both objections originally filed against Option C
(§4 D3) and keeps `SS-2` at 🟡. §5 and §6.4 are rewritten to match.

**Relationship to other documents:**

- [`VOXEL_OCCLUSION_REFACTOR.md`](VOXEL_OCCLUSION_REFACTOR.md) — **the parent.** Its §7 extension
  roadmap files this work; its **F18** is the finding that makes it necessary, its **VO-9a**
  (`GetRegionCoverage` / `SampleFacePoint`) is the query this consumes, and its **VO-9b**
  (`SUB_CELL_TESSELLATION`, baseline **B49**) is the substrate. **That arc is closed** — this
  document adds no `VO-*` phases.
- [`../Architecture/SMOOTH_AND_RGB_LIGHTING.md`](../Architecture/SMOOTH_AND_RGB_LIGHTING.md) — owns
  the smooth-lighting/AO description this changes; every visual phase below doc-syncs its AO section.
- [`../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md`](../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md) —
  owns the shape descriptor the silhouette is derived from. Its **single-AABB** limitation is
  inherited verbatim (§1.2), and `VQ-4` still owns compound shapes.
- [`../Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
  — records the fixture gaps `SS-0` closes (a non-linear-coverage occluder, and a sub-vertex-field
  probe).
- [`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`](VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md) —
  **`VX-8` is the orthogonal half of this problem, not a competitor.** It moves *where* shading is
  stored (per-pixel, off the vertices); this design fixes *what the occlusion value is*. `VX-1`'s
  occupancy volume also offers D7 a third answer (§4 D7, §10).
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — **`MR-8`** (greedy
  meshing). This design is **merge-neutral**, and its tessellation gate selects exactly the faces
  `MR-8` cannot merge; see that entry for the partition argument.
- [`../Bugs/MESHING_BUGS.md`](../Bugs/MESHING_BUGS.md) — **Bug M04** (radiating star streaks) is
  open and explicitly **out of scope** here; see §1.2.

---

## 1. Goals & non-goals

### 1.1 Goals

1. **A vertical slab standing on a block casts a contact shadow onto the still-visible half of that
   block's top face** — the owner's first observation, and the request `VO-9` set out to deliver.
   Today that half runs a straight `255 → 223` ramp where the shadow should reach roughly `191` at
   the slab and fall off quickly (`VOXEL_OCCLUSION_REFACTOR.md`, VO-9 restatement).
2. **Ambient occlusion follows the occluder's rectangular footprint, not a round blob** — the
   owner's second observation, seen most clearly around an isolated full cube.
3. **No per-shape code.** A fence post, a non-half custom mesh, or any future single-AABB shape must
   work through the same arithmetic. The existing primitive (`GetRegionCoverage`, an AABB-vs-AABB
   fill fraction) is shape-agnostic; this design **preserves that property** by deriving the
   silhouette from the same rotated AABB rather than replacing the primitive (§4 D4).
4. **A subdivided face and an ordinary face agree wherever they meet.** `VO-9a` achieved this by
   fixing corner values in place; this design necessarily moves them, so it must own the consequence
   with a stricter invariant that implies the same guarantee (§4 D6).
5. **Ordinary full-cube terrain is bit-identical until a phase deliberately changes it** — the
   claim that keeps `B11` and every standard-cube baseline meaningful, and that separates the cheap
   phases from the expensive one (§9).

### 1.2 Non-goals (versioned)

| Not doing                                                     | Why / where it lives                                                                                                                                                             |
|---------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Re-designing `VO-9b`'s subdivision substrate                  | Committed, gated, and guarded by **B49**. This design *consumes* it. `SUB_CELL_TESSELLATION` is a named constant, so a phase that wants a different density changes one number and re-measures §8's vertex cost. |
| Adding `VO-*` phases                                          | That arc is closed (VO-0…VO-6, VO-8, VO-9a, VO-9b executed; VO-7 descoped).                                                                                                       |
| `MESHING_BUGS.md` **Bug M04** (radiating star streaks)        | Deferred to its own session; its entry records the decisive diagnostic. Noted here only because `VO-9b` made `EmitQuadTriangles`' anisotropy-aware split run **per sub-quad**, so any M04 work must be judged against sub-quad diagonals, not face diagonals. |
| Compound (multi-AABB) shapes — stairs, L-shapes, wedges       | Inherited from the shape model, owned by **`VQ-4`**. A stair's silhouette under this design is its *enclosing* rectangle, which is an over-estimate; §10 records the seam that keeps `VQ-4` cheap to land later. |
| A per-face AO texture / lightmap, or per-pixel analytic AO     | Rejected during `VO-9` against the arbitrary-custom-mesh requirement (atlas, UV allocation, upload bandwidth, a shader change, and `MR-2`'s packed vertex format). Not re-opened. A sampled-per-vertex model remains the prerequisite for that route if it is ever revisited. |
| Directional/positional shadows from a light direction          | This is **ambient** occlusion — the shadow is symmetric around the occluder. A sun-direction term is a different feature (`RF-1` day/night owns light direction).                  |
| Profiler capture as a gate on the cheap phases                 | `VO-8`'s perf-measurement waiver stands. `SS-3` is the one phase that plausibly makes meshing a bottleneck, and it carries a measurement proposal (§8, §9).                        |

---

## 2. Current state

### 2.1 What the shading model actually is

There is no separate "AO term" in this engine. A shading value at a point on a face is a **weighted
average of four cells' light values**, where each cell's *value* is attenuated by how much of a
sub-box of that cell an occluder fills, and each cell's *weight* is that cell's share of a
one-cell-wide box centred on the sample point (`MeshGenerationJob.SampleFacePoint`,
`TangentSpan`). Darkening is emergent: an occluding cell contributes `0`, pulling the average down.

| # | Element                        | Code (anchors — re-verify before editing)                                | Behaviour today                                                             |
|---|--------------------------------|--------------------------------------------------------------------------|------------------------------------------------------------------------------|
| 1 | Occlusion primitive            | `BurstOcclusionUtility.GetRegionCoverage`                                | AABB-vs-AABB fill fraction, normalized by the region's own volume            |
| 2 | Face-level primitive           | `BurstOcclusionUtility.GetFaceCoverage`                                  | Touch test on the normal axis + product of the two perpendicular extents      |
| 3 | Gated entry point              | `LightAttenuation.AmbientOcclusionRegionCoverage`                        | `!IsOpaque → 0`; `!HasCustomBounds → 1`; else rotate + `GetRegionCoverage`   |
| 4 | Sample point → cells + weights | `MeshGenerationJob.TangentSpan`, `SAMPLE_BOX_HALF_EXTENT = 0.5`          | Box is one cell wide → exactly two cells per tangent axis, four cells total  |
| 5 | Per-face constants             | `MeshGenerationJob.PrepareFaceSampling`                                  | Direct cell, normal axis, which half is in front (Bug M03), raw direct light |
| 6 | Corner shading                 | `SampleCornerPoint` → `SampleFacePoint`                                  | The box straddles the corner: four cells at weight `0.25` each               |
| 7 | Sub-vertex shading             | `ShadeSubVertex`, `DirectOpenFractionAt`                                  | Ring blended from the face's four corner values; **only the direct cell** is re-evaluated per point |
| 8 | Subdivision + its gate         | `EmitTessellatedStandardCubeFace`, `SUB_CELL_TESSELLATION = 4`, `hasPartialOccluder` | 4×4 sub-quads when a sampled cell holds an opaque block with custom bounds |
| 9 | Emission                       | `VoxelMeshHelper.GetStandardCubeFaceQuad` / `GetSubQuad` / `EmitFaceQuad` | One source of truth for a standard-cube face's corners, shared with the undivided path |

**Where subdivision does and does not reach (verified by call-site grep this session).**
`EmitTessellatedStandardCubeFace` is called from exactly two sites — `MeshGenerationJob.cs:721`
(legacy-orientation standard cubes) and `:955` (`EmitStandardCubeFaceIfVisible`). The other five
`CalculateCornerLights` call sites — the fluid paths (`:356`, `:380`, `:389`) and **both custom-mesh
paths** (`:539` schema-aware, `:610` legacy) — emit one quad per face and blend it through
`VoxelMeshHelper.BilinearLerpLight`. **A slab's own faces therefore have no sub-cell shading
resolution at all**, which §9's `SS-4` addresses and which every earlier phase must state as a known
limit rather than discover in game.

### 2.2 Observation 1, mechanically — and why the substrate is inert

For a floor cell's `+Y` face with a vertical slab `0x03` in the cell above, `VO-9b` measured the
sub-vertex profile across the visible half as `255 / 234 / 225 / 213 / 191` against the undivided
face's `255 / 239 / 223 / 207 / 191` — no useful difference (`VOXEL_OCCLUSION_REFACTOR.md` **F18**).

The reason is arithmetic, not a defect. `DirectOpenFractionAt` asks
`GetRegionCoverage(slabVolume, box)` where the box is one cell wide and clipped to the direct cell.
For an occluder whose boundary is a single plane cutting the cell — which is exactly what an
axis-aligned half slab is — the overlap volume is a **linear** function of the box's position, so
the open fraction sweeps linearly from 1 to 0 across the cell. Bilinear interpolation of the two
corner values *is* that same linear ramp. Sub-cell sampling can only carry information where
coverage is non-linear in the cell — a fence post, a stair, any shape not spanning a clean half.

**The signal is not missing; the model has no room for it.** Coverage measures *how much volume is
in the way*, which for a contact shadow is the wrong question. What a contact shadow measures is
*how far this point is from the thing standing on the surface*.

### 2.3 Observation 2, mechanically — the round blob is a product of two ramps (*derived*)

An isolated full cube standing on a floor darkens each surrounding top face at the corners only, and
the arithmetic of §2.1 fixes which:

- A cube in a face's **direct or side** cell covers its whole sub-box at the two corners on the
  shared edge → those two corners take `255 × ¾ = 191`, the other two stay `255`.
- A cube in a face's **diagonal** cell reaches exactly one corner → one corner at `191`, three at
  `255` (the diagonal term is skipped only when both sides seal, and here they are air).

Over the eight surrounding faces that is `4 × 2 + 4 × 1 = 12` darkened corners out of `8 × 4 = 32`,
which is **exactly the `12 of 32` `VOXEL_OCCLUSION_REFACTOR.md` F17 measured** on the real engine.
The derivation and the measurement agree, so the mechanism below is not a hypothesis:

- On an **edge-adjacent** face the rendered field is a bilinear blend of `(191, 191, 255, 255)` — a
  straight ramp, one full cell wide. Rectangular, but far too wide and too weak.
- On a **diagonal-adjacent** face it is a blend of `(191, 255, 255, 255)` — a `u·v` product, whose
  isocontours are **hyperbolas**. The darkness collapses to a point at the cube's corner and bows
  inward everywhere else.

Stitching those together around the cube gives a shadow that is a full cell wide on the flats and
pinches to a point at the diagonals: the round blob. **No choice of coverage function fixes it**,
because the shape comes from the *weighting* (a separable product), not from the coverage values.
A signal keyed on distance to the occluder's silhouette produces a band of constant width all the
way around instead, which is what "follow the block's rectangular shape" means.

### 2.4 Harness state

- **B49** (`MeshingValidationSuite.SubCellShading.cs`) guards three things: the gate holds (an
  undisturbed floor face is one quad), a reachable face is subdivided, and a subdivided face stays
  on its own corner field. Its tolerance split encodes the reason: `ROUNDING_ALLOWANCE = 1.5` where
  the direct cell is empty and only the ring could drift, `DIRECT_TERM_DRIFT_ALLOWANCE = 32` where
  the direct cell legitimately varies. **Leg 3b's 1.5 is the precise regression guard** for the
  shipped ring-resampling defect, and §6.3 explains why `SS-2` must *rewrite* it rather than loosen it.
- **`TopFaceCornerSun`** (`MeshingValidationSuite.CornerOcclusion.cs:306`) is the corner-located
  probe pattern: it locates a face's corners **by vertex position**, never "the first quad matching
  the region". B42/B46 broke on that assumption when `VO-9b` landed. Any new probe follows it.
- **Fixtures are a fidelity surface (F13).** `TestCustomMeshLibrary.AppendBoxMesh` is parametric on
  **`topY` only** and always spans the full X/Z cell, and `TestMeshBlockPalette.MakeHalfSlab` pairs
  it with `BlockCollisionBounds.BottomHalfSlab`. **There is no fixture whose coverage is non-linear
  in the cell**, so the harness today cannot distinguish "sub-cell sampling works" from "sub-cell
  sampling is inert". `SS-0` closes that.

---

## 3. Findings

| #  | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                       | Addressed by |
|----|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------|
| S1 | **Coverage answers the wrong question for a contact shadow.** A fill fraction is linear across the cell for any occluder bounded by one plane, so no refinement of it can produce a shadow that is dark at the occluder and fades quickly. Restates and generalizes `VO-*` **F18**: F18 says "not for a slab", S1 says "not for any single-plane boundary, which is the common case".                                                                              | §4 D5, SS-2  |
| S2 | **The round blob is a weighting artifact, not a coverage artifact** (*derived in §2.3, and the derivation reproduces F17's measured `12 of 32` exactly*). Four-cell averaging weights an occluder by a product of two per-axis ramps → hyperbolic isocontours. Fixing it needs a different **metric**, not different values.                                                                                                                                        | §4 D1, SS-3  |
| S3 | **The silhouette is already computed — `GetFaceCoverage` throws it away.** That function does a touch test on the normal axis and then multiplies the two perpendicular extents. Those extents *are* the silhouette rectangle; the multiply reduces it to an area. A sibling that returns the rectangle is a few lines and keeps the AABB-vs-AABB primitive intact, so the shape-agnostic property survives untouched.                                              | §4 D4, SS-1  |
| S4 | **The falloff radius is pinned to `1.0` cells — by the gate above and by the F18 defect below.** `hasPartialOccluder` accumulates over the four cells the sample box reaches at each of the four corners, whose union is the full 3×3 in front of the face; that 3×3 spans `[−1, 2]²` in the face's parameter space, so a silhouette in it can lie `0` from the face and one outside it can never lie less than `1` from it. **`R = 1.0` is therefore exactly the radius the existing neighbourhood supports — no more, no less** — and `R > 1` is a scope change, not a tuning knob. It is also a *lower* bound: at `R = 0.5` (this document's v1.0 value, chosen to match `SAMPLE_BOX_HALF_EXTENT`) a wall's occlusion reaches only half a cell, and the interior of a face in an inner corner between two walls computes **255** — numerically the `144 → 255` signature of the shipped F18 defect. Verified across both radii, §4 D2. | §4 D2, SS-2  |
| S5 | **Corner values must move, and "corners do not move" was never the real invariant.** `VO-9a` froze corner values so a subdivided face would agree with an ordinary neighbour along their shared edge. The property that actually delivers that is weaker and survives this design: the shading value is a **pure function of the sample point's position and the block field**, independent of which face emits it and at what density.                              | §4 D6, SS-2  |
| S6 | **Custom-mesh and fluid faces are never subdivided** (verified by call-site grep, §2.1). They will receive the new term at *corner* resolution only. Consistent — position-purity still holds at every shared vertex — but a slab's own top face gets a coarse version of the effect. Must be stated up front; discovering it in game would read as a bug.                                                                                                          | S6 → SS-4    |
| S7 | **B49 leg 3b will go red under `SS-2`, and its *assertion* is what has to change — not its tolerance.** The leg says "a subdivided face stays on the bilinear field of its own corners", which under the chosen replacement (§4 D3) is precisely the property being removed on purpose. Widening the `1.5` bound to accommodate the departure would leave a guard that catches nothing, and the defect it exists for — face interiors lightening toward `255` as the ring's occlusion is lost — would pass straight through it. §6.3 replaces the assertion with the defect's own numeric signature and gives it a control that tessellation, not the shadow, satisfies. | SS-2         |
| S8 | **No harness fixture has non-linear coverage (F13 restated).** `AppendBoxMesh` is parametric on `topY` alone and spans the full X/Z cell. Every meshing fixture is therefore a shape the current model already handles linearly, which is precisely why `VO-9b` could ship visually inert with a green suite. A post fixture — bounds and mesh authored from **one shared constant** so they cannot diverge — is a prerequisite for testing this design at all.       | SS-0         |

---

## 4. Decisions

### D1 — Distance metric: Euclidean ✅ **CHOSEN** (owner, 2026-08-09)

This is the choice that delivers goal 2, and it is purely a matter of how the shadow's corners look.
All three cost the same order of arithmetic. Let `q = |p − c| − h` be the 2D offset of the sample
point from the silhouette rectangle's centre `c` minus its half-extents `h`.

**Option A — Euclidean (L2) distance to the rectangle ✅ CHOSEN.**
`d = length(max(q, 0)) + min(max(q.x, q.y), 0)`.

- ✅ Physically what a contact shadow does: straight along the occluder's edges, quarter-circle
  corners. Constant band width all the way around.
- ✅ The standard signed-distance formulation; well-behaved, no special cases, negative inside.
- ⚠️ The outer corners are rounded, which is the owner's stated concern at decision time: *does this
  re-introduce the circular-single-block look?* **It does not, and the reason is worth pinning
  because it is the opposite artifact.** See the analysis below.

**The rounding concern, answered by the isocontours.** Take the iso-darkness curve at half of peak
around an isolated cube, and measure how far the shadow reaches:

| Model                          | Reach perpendicular from an edge | Reach diagonally from a corner | Shape                                      |
|--------------------------------|:--------------------------------:|:------------------------------:|--------------------------------------------|
| **Today** (separable product)  | `0.5` cells                      | `≈ 1.0` cells                  | **Bulges outward at the diagonals** — the blob |
| **Euclidean SDF**              | `d`                              | `d` (measured from the corner) | Rounded rectangle — a *fillet*, cut inward  |
| Chebyshev                      | `d`                              | `d·√2`                         | Perfect square, mitred                      |

Today's artifact is the shadow reaching **twice as far** at the diagonals as on the flats — the dark
region swells into a disc. Euclidean does the reverse: it takes a *fillet* off the corner of an
otherwise rectangular band. Those are different artifacts in sign, not degrees of the same one.

**And D2's choice shrinks the residual almost to nothing.** The fillet radius at any iso-level is
that level's distance `d`, and under a `(1 − t)²` falloff the darkness is concentrated at small `d`:
at `d = 0.1R` the shadow is at 81% strength with a fillet of `0.05` cells, while the `0.45`-cell
fillet only appears out at `d = 0.9R` where the shadow is at 1% and invisible. **The strongly
shadowed region is very nearly a rectangle; only the faint tail rounds.** A linear falloff would have
spread the rounding evenly across the whole band and made the concern real — the two decisions
cooperate.

**Escape hatch if it still reads round in game.** Generalize the metric to a p-norm,
`d = (|max(q.x,0)|^p + |max(q.y,0)|^p)^(1/p)`; `p = 2` is Euclidean, `p → ∞` is Chebyshev, and
`p = 4` is a squircle with near-square corners and no mitre crease. One named constant, no
re-baselining — §6.4's **B54** asserts band-width uniformity, which every `p` in that range
satisfies. Do not build it speculatively; build it if `SS-3`'s in-game review asks for it.

**Option B — Chebyshev (L∞) distance.** `d = max(q.x, q.y)`.

- ✅ Perfectly rectangular isocontours — the most literal reading of "follow the block's
  rectangular shape", and marginally cheaper (no `length`).
- ❌ A square shadow around a square block reads as a hard mitred corner. At an outside corner two
  shadow bands meet along a 45° crease that is visible as a line, because the gradient is
  discontinuous there.

**Option C — separable product (what exists today).**

- ❌ **This is the artifact.** Listed only to be explicit that it is being replaced, not tuned.

> **Not a taste question, and settled here:** the metric is evaluated against the occluder's
> *silhouette rectangle*, not against the cell. That is what makes a fence post cast a post-shaped
> shadow with no per-shape code (goal 3).

### D2 — Falloff profile and radius: `(1 − t)²` ✅ **CHOSEN** (owner, 2026-08-09)

Let `t = saturate(d / R)` with `R` the contact radius, and `shadow = f(t)`, `f(0) = 1`, `f(1) = 0`.
**`R = 1.0` cells, fixed by S4 from both directions**: it is the largest radius the hoisted 3×3
neighbourhood can answer for, and the smallest that does not lose a wall's occlusion in the face
interior. Treat it as a constant, not a knob.

> ⚠️ **`R = 0.5` was this document's v1.0 value and it is wrong.** It was chosen to match
> `SAMPLE_BOX_HALF_EXTENT`, which is the right constant for a *box-overlap weight* and the wrong one
> for an *occlusion reach*. Worked through both radii on the inner-corner-between-two-walls
> configuration (today renders ≈ `175` at the face centre):
>
> | Sample point            | `R = 0.5` | `R = 1.0` |
> |-------------------------|:---------:|:---------:|
> | face corner `(0, 0)`    | `64`      | `64`      |
> | `(0.25, 0.25)`          | `218`     | `157`     |
> | face centre `(0.5, 0.5)`| **`255`** | `218`     |
>
> The `R = 0.5` column is the `144 → 255` interior-lightening signature of the F18 defect, arrived
> at by a different route. `R = 1.0` keeps the wall's occlusion present across the whole face while
> concentrating it near the wall, which is the requested profile.

**A single wall's band, for calibration** (today: a linear `191 → 255` across one cell):

| `v` (cells from the wall) | 0     | 0.2   | 0.4   | 0.5   | 0.75  | 1.0   |
|---------------------------|-------|-------|-------|-------|-------|-------|
| `R = 1.0`, `(1 − t)²`     | `191` | `214` | `232` | `239` | `251` | `255` |

Same value at contact and at full reach as today, with the darkness pulled toward the wall in
between — "dark and tight at contact, quick fade" against a straight ramp.

| Option                     | `f(t)`                  | Character                                                                                                              |
|----------------------------|-------------------------|------------------------------------------------------------------------------------------------------------------------|
| A — linear                 | `1 − t`                 | Cheapest. Still fixes both observations, because the *metric* (D1) is what makes it rectangular. But it is a ramp, and a ramp of half a cell is what the corner blend already approximates over a full cell — the change would read mostly as "the shadow got tighter". |
| B — smoothstep             | `1 − smoothstep(0,1,t)` | Zero gradient at both ends, so no Mach band at the shadow's outer edge and no crease at contact. The safe default look. |
| **C — concentrated ✅ CHOSEN** | `(1 − t)²`          | Dark and tight against the occluder, quick fade, faint tail. Closest to the "reach about `191` at the wall and fall off quickly" the `VO-9` restatement asked for. Strongest departure from the current look — which is the point.       |

**No separate strength constant.** `f(0) = 1` means an occluder in contact contributes its full
share of the occlusion budget, which under D3 is exactly what a fully-covering occluder contributes
today — so peak depth is preserved *by construction* rather than by tuning a coefficient. (v1.0 of
this section proposed an `s = 0.25` strength; that belonged to D3's superseded formulation and is
gone. Do not reintroduce it: a global strength is precisely what cannot reproduce both the
one-occluder and four-occluder depths — see D3.)

**Why this choice also settles D1's residual risk:** the corner fillet's radius scales with the
iso-level's distance, and `(1 − t)²` puts nearly all the darkness at small distances. The rounding
therefore lands in the invisible tail rather than in the shadow proper (D1, table and following
paragraph).

### D3 — Add or replace: **the silhouette field replaces the coverage fraction** ✅ **CHOSEN** (owner, 2026-08-09)

> ⚠️ **The chosen option's *specification* was wrong when it was chosen, and is corrected here.**
> The owner picked "Option C — the silhouette field becomes the occlusion channel" on the strength
> of it being the cleaner long-term model, accepting a stated risk. Working the replacement through
> arithmetically afterwards found that the form written down — *a plain light average multiplied by
> a single global `(1 − s·SS)` factor* — **does not work**, and that the correct form is both simpler
> and much lower-risk than the one the decision was made against. The verdict stands; the mechanism
> below replaces the description. This is not a re-litigation, it is the arithmetic the v1.0 section
> asserted without doing.

**Why the global-factor form fails.** A single occlusion field bounded to `[0, 1]` with one strength
constant `s` cannot reproduce the two depths the engine already has:

| Configuration                            | Today (measured / cited)                | Global `(1 − s·SS)` with `s = 0.25` |
|------------------------------------------|-----------------------------------------|-------------------------------------|
| One occluder at a corner                 | `255 → 191`                             | `191` ✅                            |
| Inner corner between two walls           | `144` (`VO-*` F18)                      | `191` ❌                            |
| 1×1 pit floor                            | `64` (`VO-*` B47's note)                | `191` ❌                            |

Raising `s` to reach `64` would make a *single* slab darken to `64` as well. The flaw is structural:
occlusion is a *share of the hemisphere*, so it must **accumulate per occluder**, and a lone global
`max()` (or any single-strength factor) caps it at one occluder's worth. Every deep AO configuration
in the world would have flattened.

**The correct form — each cell owns a quarter of the occlusion budget.** Keep the existing four-cell
structure and swap what "occluded" means, per cell:

```
open_i(p) = 1 − f( dist(p, silhouette_i) / R )        // was: 1 − GetRegionCoverage(cell_i, box(p))
out(p)    = Σ_i  w_i(p) · open_i(p) · L_i
```

`w_i` are the existing box-overlap weights and `L_i` the existing raw light values — **neither
changes**. The only substitution is the occlusion function: a volumetric fill fraction of a box
becomes a distance falloff from a silhouette. Everything the parent arc settled about *which* cells
and *which* face to ask (`OppositeFace`, Bug M03's front-half rule, `VO-6`'s sample cell) is
untouched.

**Why this is a genuine replacement and not Option A in disguise.** Coverage is gone from the AO
path entirely — `AmbientOcclusionRegionCoverage` loses its meshing consumer, `DirectOpenFractionAt`
is deleted rather than layered on, and there is no second darkening term to double-count against.
The blend's own attenuation *is* the occlusion channel; it has simply stopped being volumetric.

**The result that de-risks it: at a cell corner with binary occlusion, this is algebraically
identical to today.** At a corner every `w_i = ¼`, and for a full-cube occluder the corner lies on
its silhouette boundary so `f(0) = 1` (fully occluded), while a non-adjacent cell is `≥ 1` cell away
so `f = 0`. The sum is therefore `¼ · Σ_{open cells} L_i` — exactly today's expression, term for
term, before the same UNorm8 encode. Worked against the three configurations above: one occluder
`¼(0+15+15+15) → 191` ✅, two occluders `→ 128` ✅, three occluders (pit) `→ 64` ✅. **Both
objections filed against Option C in v1.0 dissolve:**

- ~~"Every corner value in the world moves"~~ — they do not move at all where occlusion is binary,
  which is every full-cube configuration. `B11` and the standard-cube family stay green.
- ~~"It re-opens what an opaque cell contributes to a plain light average"~~ — there is no plain
  average. A fully-occluded cell still contributes `open_i = 0`, so `PrepareFaceSampling`'s skip of
  the light read for an opaque direct cell stays valid and `VO-0(c)`'s sky-stamped wall never leaks
  brightness onto the floor beside it.

**What does change, and it is exactly the requested change.** Values move where occlusion is *not*
binary: at sub-vertex positions between corners, and wherever an occluder is partially shadowing
(`0 < f < 1`). That is the sub-cell detail F18 says coverage cannot carry, and it is why the
occlusion becomes rectangular — `f` depends on **distance to the silhouette**, not on a product of
per-axis ramps (S2).

**Option A — additive layer** (rejected). Leave the existing model untouched; multiply the final
value by `(1 − s·SS)`.

- ✅ Smallest blast radius, and reversible phase by phase.
- ❌ **Double darkening.** A wall already darkens the adjacent floor through the blend; the new term
  darkens the same band again — a near-wall corner would go `191 → 143` at `s = 0.25`. Two
  occlusion channels stacked, with no principled way to divide responsibility between them.
- ❌ Leaves coverage in the AO path permanently, so `S1`'s finding is worked around rather than
  fixed.

**Option B — replace the direct term's coverage only** (rejected on analysis, not taste). The direct
cell is the cell in front of the face; the occluders responsible for observation 2 — and for half of
observation 1 — are **ring** cells. A change confined to the direct term cannot reach them, so it
would leave the round blob exactly as it is and deliver a contact shadow only when the occluder
stands on the shaded block itself.

### D4 — Silhouette source: the existing AABB primitive ✅ **CHOSEN**

The silhouette of a placed block on a face's plane is:

1. **Touch test.** The rotated volume must reach the plane on the normal axis — this is
   `GetFaceCoverage`'s `touches` test, asked of the sampled cell's face **pointing back at the
   shaded surface**. That face choice is `D5` of the parent document, signed off 2026-08-08:
   `OppositeFace(meshedFace)`. **Do not re-derive it.**
2. **Rectangle.** `[rotatedMin[a], rotatedMax[a]] × [rotatedMin[b], rotatedMax[b]]` on the two axes
   perpendicular to the normal — the same two extents `GetFaceCoverage` multiplies together.

Rejected: a new authored per-block silhouette descriptor. That is `D1`-of-the-parent's
second-descriptor hazard in a new costume, it needs its own authoring UI and rotation path, and it
would let a block's shadow disagree with its collision volume.

**The consequences that matter, and they are all good:**

- **Shape-agnostic by construction** (goal 3). Still one AABB-vs-AABB question; a fence post authored
  `min = (0.375, 0, 0.375)`, `max = (0.625, 1, 0.625)` yields a `0.25 × 0.25` silhouette in the
  middle of its cell and casts a post-shaped shadow with zero new code.
- **`GetFaceCoverage` becomes the area of the new primitive**, exactly as `GetOctantCoverage` became
  the corner case of `GetRegionCoverage` in `VO-9a`. Same house pattern, same consolidation
  direction — generalize the existing home, never mint a twin.
- **The full-cube fast path survives.** `!HasCustomBounds` → silhouette is the unit square, touching,
  with no rotation. `!IsOpaque` → no silhouette at all, so glass still casts nothing.
- **A "contact" shadow is one that touches.** A *top* slab in the cell above a floor occupies
  `y ∈ [0.5, 1]`, does not reach the floor's plane, and therefore casts nothing — reproducing the
  already-signed-off `255,255,255,255` reading for that case rather than contradicting it.
- **Inherited limitation, stated:** a compound shape's silhouette is its enclosing rectangle, so a
  stair would over-shadow. That is `VQ-4`'s to fix and §10 keeps the seam open.

### D5 — Does this touch the ring? ✅ **CHOSEN: yes, it re-samples the ring — and why that is safe here and was not in `VO-9b`**

The brief on this arc is explicit that any new term must state whether it touches the ring and
justify it, because re-sampling the ring per sub-vertex **shipped once**: it collapsed wall shadows
into a hard band, lightened face interiors (an inner corner's centre went `144 → 255`), and was
caught in game rather than by the suite.

**It touches the ring, necessarily** — the occluders responsible for observation 2, and for a slab
standing on a *neighbouring* cell, are ring cells. Refusing to touch them would reduce this design
to D3's rejected Option B.

**And under D3's replacement it re-samples the ring per sub-vertex, which the `VO-9b` implementation
was explicitly corrected for doing.** That is not a contradiction, because the two do it from
different structures, and the structure is what made the original wrong:

| | The `VO-9b` defect | This design |
|---|---|---|
| Form re-evaluated per sub-vertex | `Σ wᵢ(p) · openᵢ(p) · Lᵢ` — occlusion **entangled with the interpolation weights** | `L(p) × (1 − occ(p))` — light and occlusion **separated** |
| Behaviour at the face centre | `w` collapses to the direct cell, so every ring occluder's contribution vanishes with it → `255` | `w` collapses the same way, but `occ` is weightless and keeps the ring's occlusion → `218` |
| Support | Unbounded within the cell — every ring cell influenced every point at a weight that told it to stop | Zero beyond `R = 1.0` from a silhouette, and `R` is chosen so it does **not** stop early (S4) |
| Seam behaviour | Matched on the shared edge, diverged across it — why a seam-only check stayed green | Pure function of position (D6), so it agrees everywhere, not only on edges |

**The one-line diagnosis of F18, which this design is built on:** the box-overlap weights `wᵢ` exist
to interpolate *light values*, and they collapse to a single cell at the face centre. That is correct
for light and catastrophic for occlusion, so re-sampling a form in which the two are multiplied
together destroys the occlusion. **Decoupling them is what makes per-sub-vertex evaluation safe** —
and it is what a coverage model cannot do, because in a coverage model the occlusion has nowhere to
live except inside those weights.

Consequence for the guard: `B49`'s leg 3b asserted "a subdivided face stays on its own corner
field", which this design **legitimately breaks** — the whole point is that it should not. §6.3
replaces it with an assertion that survives the change and still catches the original defect.

### D6 — What replaces "corner values do not move" ✅ **CHOSEN**

`VO-9a` guaranteed seam consistency by freezing corner values. This design moves them: a corner
within `R` of a silhouette gets darker. The invariant that replaces it, and implies the same
guarantee:

> **Position purity.** The shading value at a point is a function of that point's position and the
> surrounding block field alone — never of which face is being emitted, nor of the density at which
> that face is sampled.

Three obligations follow, and they are load-bearing:

1. **The new term is evaluated on every face, subdivided or not.** It goes inside `SampleFacePoint`
   (which computes corners for both paths) **and** inside `ShadeSubVertex`, through one shared
   function. If only the subdivided path applied it, a tessellated face and its ordinary neighbour
   would disagree at their shared corner — the seam `VO-9a` was designed to prevent.
2. **The two paths must agree at a corner by construction, not by tuning.** At a corner,
   `ShadeSubVertex`'s `ring + direct` equals `SampleFacePoint`'s blend (that is `VO-9a`'s identity);
   both then multiply by the same positional factor, so equality is structural. §6.2 makes it a
   baseline anyway, because "by construction" is how `VO-6`'s wrong half-cell step survived review.
3. **Every point where the term is non-zero must lie on a subdivided face**, or the effect is
   rendered as a straight interpolation between two corners and the whole exercise is pointless.
   S4 shows the existing gate already guarantees this for `R ≤ 0.5` and partial occluders; `SS-3`
   must extend the gate along with the occluder population, in the same phase.

### D7 — Gate scope for full-cube occluders (⏳ **OWNER DECISION**, in `SS-3`)

Observation 2 is about a **full cube**, and `hasPartialOccluder` never trips for one. Delivering
goal 2 therefore means subdividing faces next to ordinary terrain — the only phase in this design
with a real cost. It is deliberately a separate, last phase so the cheap half can ship and be judged
first. The options and their measured anchor are in §8 and the packet in §9; the decision is the
owner's because it trades vertex count for a visual improvement only they can weigh.

**A third answer exists, and it may be the right one: defer observation 2 to the GPU.** Once
`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`'s **VX-1** is resident, its `_VoxelOccupancyVolume`
puts full-cube occupancy on the GPU — and a fragment shader can then tap the 3×3 of occupancy in the
layer in front of its face and evaluate **this design's own distance field per pixel**, with no extra
memory and **zero vertex cost**. That is observation 2 at per-pixel quality and it would make `SS-3`
unnecessary outright.

Two limits keep it from swallowing the whole design, and they are what make the CPU-side phases
worth building first:

- **It cannot deliver observation 1.** The occupancy volume carries no bounds and no rotation, so a
  partial occluder is invisible to it until `VX-5` widens the format. Slabs stay CPU-side.
- **It is not a place to move AO wholesale.** Occlusion is *face-dependent* — the same point on a
  `+Y` and a `−Y` surface asks about opposite layers — so a scalar volume cannot hold it, and six
  directional volumes cost ≈ 157 MB at 2× voxel resolution against VX-1's 3.3 MB. Analytic
  per-pixel evaluation is the only viable GPU route, which is why it works for occupancy (a
  9-tap distance field) and not as a baked channel.

**So D7 has three answers, not two:** pay `SS-3`'s vertex cost; drop observation 2; or defer it to a
per-pixel evaluation on VX-1. **Take the decision with VX-1's status known** — if it is on the near
horizon, deferring is likely better than paying tessellation for geometry the GPU can shade for
free. Recorded as an interlock, not a phase: `VX-1`/`VX-8` own that ID space (§10).

---

## 5. The model

### 5.1 New primitives

```csharp
// Jobs/BurstData/BurstOcclusionUtility.cs — sibling of GetFaceCoverage, whose result is this
// rectangle's area.

/// <summary>
/// SS-1: returns the rectangle a rotated block volume projects onto one of its cell faces — the
/// occluder's silhouette in that face's tangent plane, in block-local <c>[0,1]²</c>.
/// <para>
/// This is <see cref="GetFaceCoverage"/> stopped one step early: that function multiplies the two
/// perpendicular extents into an area, which is what a coverage model needs and what a contact
/// shadow cannot use. Keeping the primitive an AABB projection is what keeps the shading model
/// shape-agnostic — a post, a slab, or any other single-box mesh needs no code of its own.
/// </para>
/// </summary>
/// <param name="rotatedMin">Rotated minimum corner, block-local (from <see cref="RotateLocalBounds"/>).</param>
/// <param name="rotatedMax">Rotated maximum corner, block-local.</param>
/// <param name="faceIndex">Face direction, in <c>VoxelData.FaceChecks</c> order.</param>
/// <param name="rectMin">Silhouette minimum corner, on the two axes perpendicular to the face.</param>
/// <param name="rectMax">Silhouette maximum corner.</param>
/// <returns>True when the volume reaches the face plane; false leaves the rectangle undefined.</returns>
public static bool GetFaceSilhouette(float3 rotatedMin, float3 rotatedMax, int faceIndex,
    out float2 rectMin, out float2 rectMax);
```

```csharp
// Jobs/BurstData/LightAttenuation.cs — same gating as every sibling predicate.
// !IsOpaque        -> false (glass casts no shadow)
// !HasCustomBounds -> the unit square, touching, WITHOUT entering the rotation path
public static bool AmbientOcclusionFaceSilhouette(in BlockTypeJobData block, byte meta,
    int faceIndex, out float2 rectMin, out float2 rectMax);
```

The face asked of a sampled cell is `BurstVoxelData.OppositeFace(meshedFace)` — the parent's `D5`
rule, signed off and not re-derived here.

### 5.2 The shading model

Two position-pure fields, evaluated at every shading point and multiplied:

```
occ(p) = saturate( Σ over the 3×3 cells in front of the face
                     CELL_OCCLUSION_SHARE · Falloff( Distance(p, silhouetteᵢ) / R ) )

L(p)   = Σᵢ wᵢ(p) · openᵢ(p) · Lᵢ                  // the existing four box-reachable cells
         ────────────────────────────
              Σᵢ wᵢ(p) · openᵢ(p)                   // renormalized; guard the zero denominator

out(p) = L(p) · (1 − occ(p))
```

with `openᵢ(p) = 1 − Falloff(Distance(p, silhouetteᵢ) / R)`, `R = 1.0` (D2), `Falloff(t) = (1 − t)²`
(D2), `Distance` the Euclidean signed distance to the silhouette rectangle (D1), and
`CELL_OCCLUSION_SHARE = 0.25`.

**Why `0.25`, and why a sum rather than `max`.** Occlusion is a share of the shaded point's
hemisphere, and the four cells meeting at a face corner divide it in four — which is the existing
model's `¼` weight, made explicit and *detached from the interpolation weights*. It must accumulate:
`max` caps at one occluder's worth, which would flatten every deep configuration (D3's table). The
sum needs no artificial cap either — `R = 1.0` is exactly the reach of the 3×3, so at most four
silhouettes can be at distance `0` from any one point and the total reaches `1` only when a point is
fully enclosed. `saturate` is a safety net, not a load-bearing rule.

**Verified reductions** (these are the design's correctness anchors, and §6.4 pins each as a
baseline leg):

| Configuration                            | Today       | This model |
|------------------------------------------|-------------|------------|
| Corner, 1 fully-occluding neighbour       | `191`       | `191`      |
| Corner, 2 fully-occluding neighbours      | `128`       | `128`      |
| Corner, 3 (a 1×1 pit floor)               | `64`        | `64`       |
| Single wall, at contact / at 1 cell       | `191` / `255` | `191` / `255` |
| Single wall, mid-band (`v = 0.5`)         | `223`       | `239`      |
| Inner corner, face centre                 | `≈ 175`     | `218`      |

The first four rows are **algebraic identities, not approximations**: at a corner every `wᵢ = ¼`
and every `Falloff` is `0` or `1`, so `L(p)·(1 − occ(p))` collapses term-for-term to
`¼ · Σ_{open} Lᵢ` — today's expression. The last two rows are the intended change: the occlusion
concentrates toward the occluder and interiors lighten accordingly. **That lightening is the
trade the `(1 − t)²` profile buys** (D2) — if interiors read too flat in game, the lever is the
exponent, not the model.

### 5.3 Where it is applied

Both paths, through one shared function, satisfying D6:

- `SampleFacePoint` — replaces the `AmbientOcclusionRegionCoverage` calls; emits `occ` and the
  renormalized blend, encoded exactly as today.
- `ShadeSubVertex` — evaluates the same two fields at the sub-vertex. It no longer blends a
  pre-computed ring, and `DirectOpenFractionAt` is **deleted** rather than extended: the direct
  cell has stopped being a special case (D5).

Because both call one function, a corner is equal on both paths structurally, not by tuning.

### 5.4 Data flow, and why it costs no extra voxel fetches

```
PrepareFaceSampling (once per face)
        │
        ├─ existing: direct cell, normal axis, front half, raw direct light
        │
        └─ NEW: BuildFaceSilhouettes  ──▶  the 3×3 of cells in front of the face
                                            for each: fetch state, gate on opacity,
                                            rotate bounds once, project to a rectangle
                                            in the face's own parameter space
                                                    │
                     ┌──────────────────────────────┴──────────────────────────────┐
                     ▼                                                             ▼
            SampleFacePoint (corners)                                    ShadeSubVertex (N×N)
                  L(p) · (1 − occ(p))                                       L(p) · (1 − occ(p))
                                            ── the same function ──
```

**The 3×3 is the set the face already visits.** The four corners between them fetch the direct
cell, all four side cells and all four diagonals — nine cells, today re-fetched with overlap
(the direct cell four times). Hoisting them once per face into a fixed-size stack buffer is
therefore not new I/O; it removes some. Per sub-vertex the work is then pure 2D arithmetic over at
most nine rectangles, with a cheap bounding-box reject — and it is the *only* work, since the
occluder rotations have all been done once per face.

**The hoist is the phase's real structural change**, and it is what makes `ShadeSubVertex` able to
evaluate the ring per sub-vertex at all without the per-point cell fetches that would have made D3's
replacement unaffordable. It is also why `R = 1.0` is free: the 3×3 is exactly the neighbourhood a
radius of one cell can reach (S4).

**Burst shape.** `float2`/`float3` and `Unity.Mathematics` throughout; the silhouette buffer is a
fixed-size blittable struct or `stackalloc` under `[SkipLocalsInit]` (both already used in this
file), never a managed array; no branches that Burst cannot flatten; `math.select` for the metric.
No allocation anywhere in the path.

---

## 6. Testability plan

The traps below are ones this codebase has already paid for. Each is answered by a specific
mechanism, not by "write a good test".

### 6.1 The fixture gap is a prerequisite, not a nicety (S8 / F13)

Nothing in this design is testable against a half slab, because a half slab is exactly the shape
whose coverage is linear (§2.2). `SS-0` adds:

- **A parametric box mesh.** `TestCustomMeshLibrary.AppendBoxMesh` gains full `min`/`max`
  parameters instead of `topY` alone, with the existing half-slab call expressed through it so the
  current fixtures are provably unchanged.
- **A `Post` block type** in `TestMeshBlockPalette` (id 9, `Count` 9 → 10), opacity 15, schema
  `Facing6Roll2` like the slab.
- **One shared bounds constant.** The mesh's `min`/`max` and the block's
  `collisionBounds` (`CollisionBoundsMode.CustomAABB`) are built from the **same** two `Vector3`
  constants. F13 was a fixture whose bounds and geometry disagreed silently for an entire arc;
  making divergence unrepresentable is cheaper than asserting against it.
- **A pre-SS record.** `SS-0` records what the post looks like *before* any shading change — which
  makes `SS-2`'s prove-red a comparison against a measured baseline rather than an argument.

### 6.2 Probes: sub-vertex fields, located by position

`TopFaceCornerSun`'s rule generalizes: **never assume one quad per face**, and never index by quad
order. `SS-0` adds `TopFaceSubVertexField(output, cellX, cellY, cellZ)`, which walks every emitted
vertex, filters by normal and plane, and returns `(u, v) → value` samples keyed by **position** —
the same reading at any tessellation density, and the reading `B49`'s
`AssertInteriorNearCornerField` already takes informally.

### 6.3 The B49 rewrite (S7) — the one place this design must not take the easy road

`SS-2` makes `B49` leg 3b go red, and **the assertion itself is what has to change** — not its
tolerance. Leg 3b says "a subdivided face stays on the bilinear field of its own corners", which
under D3's replacement is exactly the property the design removes on purpose: the interior is now
allowed, and required, to depart from that field. Loosening the `1.5` tolerance to accommodate the
departure would leave a guard that no longer guards anything, and the defect it exists to catch
(interiors lightening toward `255` as the ring's occlusion is lost) would slip straight through a
widened bound.

**What the leg must assert instead is the defect's own signature, directly.** In the walled
inner-corner fixture, the face centre must sit **materially below the unoccluded value** and in the
right order relative to the face's corners:

| Point                     | Today   | This design | The F18 defect |
|---------------------------|---------|-------------|----------------|
| near corner `(0, 0)`      | `64`    | `64`        | `64`           |
| face centre `(0.5, 0.5)`  | `≈ 175` | `218`       | **`255`**      |
| far corner `(1, 1)`       | `255`   | `255`       | `255`          |

So leg 3b becomes: **the face centre is strictly darker than the far corner and strictly lighter
than the near corner, and is bounded away from the unoccluded value** by a margin derived from §5.2
(`≤ 235`, against a predicted `218`). The defect sets it to `255` and reds every clause. This is a
strictly *stronger* guard than the old one, because the old one only ever compared against a field
the defect also satisfied at the seams.

**Its positive control** is the same walled fixture with the diagonal slab **rolled so its solid
half faces away**: the face is still subdivided (the cell still holds a partial occluder, so the
gate still trips) while no silhouette lies within `R` of the probe face, so the centre must return
to the unoccluded value. That control is satisfied by *tessellation* working, not by the *shadow*
working — **F15**'s requirement that a positive control must not be satisfiable by the behaviour
under test.

**Leg 3a's `32` allowance is retired, not re-derived.** It existed to tolerate the direct term's
legitimate linear variation while the rest of the face was pinned to the corner field; with the
corner field no longer the expectation, the leg is replaced by the reduction assertions in §6.4
(`B56`), which pin exact values rather than a drift bound.

### 6.4 Baseline map

Numbering is claimed against the meshing suite's current tip **B49** (Validate All **432**); the
executor re-confirms the tip before writing.

| ID      | Phase  | Asserts                                                                                                                                                       | Prove-red                                                                                                                             |
|---------|--------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------|
| **B50** | SS-1   | The silhouette primitive is shape-derived: full cube → unit square + touching; bottom slab's `+Y` → **not** touching; vertical slab `0x03` → touching, half the cell on `z`, full on `x`; **post** → the central `0.25 × 0.25`. Rolling the slab moves *which* half. | `math.transpose` on the rotation. **Only the asymmetric rows catch it** — `VO-1`'s F10 lesson, where 26 physics baselines stayed green and one occlusion row did the work. The post row and the roll rows are that guard here. |
| **B51** | SS-2   | **The direct answer to F18.** On the floor face under a vertical slab, the sub-vertex nearest the slab's edge is strictly **darker than the linear interpolation of the face's own corner values**, and the far edge is unchanged within rounding. A departure-from-linear assertion, not a predicted constant, so it survives D1/D2 retuning. | Pre-`SS-2` engine fails it by construction (F18 measured the profile as linear). Positive control: replace the slab with air — no departure, and that control is independent of the shadow's shape. |
| **B52** | SS-2   | The **post** casts a shadow whose darkest sub-vertex lies under the post's footprint and which is zero beyond `R` — the shape-agnostic claim (goal 3) asserted on a shape no production block has. | Force the silhouette to the unit square → the shadow stops tracking the footprint and B52 reds while B51 (a half-cell silhouette) survives. |
| **B53** | SS-2   | **Position purity (D6).** A corner emitted by a subdivided face and the same corner emitted by its ordinary neighbour carry the same value; and a subdivided face's corner equals the undivided formula at that point. | Apply the model in `ShadeSubVertex` only → corners disagree across the seam. This is the mutation that reproduces the seam `VO-9a` exists to prevent. |
| **B56** | SS-2   | **The corner reduction — the claim the whole replacement rests on.** With full-cube occluders, a face corner reads `255 / 191 / 128 / 64` for 0 / 1 / 2 / 3 occluding neighbours — **exact values, no tolerance beyond UNorm8 rounding**, because §5.2's collapse is algebraic. Read through `TopFaceCornerSun` on an open floor, one wall, an inner corner, and a 1×1 pit. | Perturb `CELL_OCCLUSION_SHARE` off `0.25`, or swap the sum for a `max` → the 2- and 3-occluder rows red while the 1-occluder row survives, which is the exact signature of D3's rejected global-factor form. |
| **B49′**| SS-2   | Rewritten per §6.3 — the inner-corner face centre is bounded away from the unoccluded value and correctly ordered against the face's corners; new roll-away positive control. | Force the ring's occlusion to vanish in the interior (the historical `VO-9b` mutation) → the centre returns `255` and every clause reds. |
| **B54** | SS-3   | **The shadow follows the rectangle (goal 2 / S2).** Around an isolated full cube, a point beside the cube's *face* at distance `d` and a point beside its *corner* at the same `d` agree within tolerance. A metric assertion, independent of D2's profile. | The pre-`SS-3` engine fails it grossly: `12 of 32` corners darken (§2.3), the corner-adjacent point being far lighter than the edge-adjacent one at equal distance. |
| **B55** | SS-4   | A custom-mesh face (a slab's own top face) is subdivided and carries the same field a standard-cube face would at the same positions.                          | Route custom-mesh faces back through the single-quad path → the field collapses to a bilinear blend and B55 reds while B51 survives.    |

**Baselines that must stay green, and what their staying green claims.** Through `SS-0`, `SS-1` and
`SS-2`: `B11` (uniform smooth-light values) and every standard-cube baseline — full-cube faces are
not subdivided under `SS-2`'s gate, so only their corners are emitted and §5.2's corner reduction
makes those *algebraically* unchanged. **That is the claim the D3 correction bought**, and it is
what keeps `SS-3`'s change attributable. Also `B44`/`B45` (sub-block face light) and `B48` (M02
culling) throughout.

**Baselines expected to move, each owned by its phase:** `B42` (per-orientation ordering) and `B46`
(per-corner octant rolls) assert *corner* values with partial occluders, where the occlusion function
changes from an octant fill fraction to a distance falloff. Both should survive — a slab's silhouette
either contains a given corner or lies half a cell from it, so `f` is still `1` or `0` there — but
this is a **prediction, and `SS-2`'s packet verifies it rather than assuming it**; the `VO-*` arc has
already been caught once by exactly this kind of "it degenerates correctly" reasoning (`VO-6`'s
half-cell step). `B41` (full-cube coverage is binary) guards `AmbientOcclusionOctantCoverage`, which
loses its meshing consumer here; `SS-2` decides whether it retires with the consumer or stays as a
unit guard, and records which. `B47` (the recessed slab, `64`) is a `SS-4` concern — it reads a
custom-mesh face, unsubdivided until then.

---

## 7. Constraint compliance

| Project constraint                              | How this design complies                                                                                                                                                                                                 |
|-------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Nothing is added per voxel. A silhouette is derived from **block-type** bounds plus the metadata byte already in the packed `uint` — the same two inputs coverage reads today.                                             |
| `Assets/Scripts/Jobs/` is 100 % Burst-compatible | `GetFaceSilhouette` is static arithmetic over `float2`/`float3`/`float3x3` in `Unity.Mathematics`; the per-face silhouette buffer is a fixed-size blittable struct or `stackalloc` under `[SkipLocalsInit]`, never a managed array. No virtual calls, no exceptions, no `Mathf`/`System.Math`. |
| Sub-chunk (section) meshing                     | Untouched — no phase changes section partitioning. `SS-3` changes how many quads a section emits, not which section owns them.                                                                                             |
| Async BFS flood-fill lighting                   | **Not touched at all.** This is a meshing-side shading change; `LightAttenuation`'s transport predicates and every BFS site are out of scope. `GetFaceCoverage` keeps feeding transport unchanged (§11 question 5).           |
| Region-based binary serialization               | Mesh output is not persisted. Zero on-disk format change and no version bump in any phase; the tripwire is restated per phase (§9).                                                                                          |
| No LINQ / GC allocations in hot paths           | Allocation-free throughout: the 3×3 hoist **replaces** today's sixteen overlapping cell fetches with nine, and the per-sub-vertex work is pure arithmetic over that buffer. Rotation matrices come from the existing precomputed LUTs. |
| Pooling conventions                             | No new pooled resource. Vertex output continues through the existing `MeshDataJobOutput` pooling (`MR-6`), which `SS-3` stresses in volume but does not change in kind.                                                    |
| `BlockIDs` constants, never raw IDs             | No production code here references block IDs. The `SS-0` fixtures keep the meshing palette's documented test-local-index exemption.                                                                                        |

---

## 8. Cost

**Vertex counts.** `SS-0`, `SS-1`, `SS-2` and `SS-3` emit **exactly the geometry `VO-9b` already
emits** — they change per-vertex values, not the gate, so the world's vertex count does not move by
one. `VO-9b`'s measurement stands as the anchor: a 12×12 floor is 1344 verts plain and **6384 with
nine slabs on it (4.75×)**, confined to faces a partial occluder can reach.

**`SS-3` is the exception, and the only phase where measurement is proposed.** Extending the gate to
full-cube occluders subdivides every face within one cell of a height discontinuity. The count is
`quads = subdividedFaces × N² + plainFaces`, with `N = SUB_CELL_TESSELLATION = 4` (16× per admitted
face; `N = 2` is roughly 1.9× and is the first lever). Flat terrain is unaffected — the layer in
front of a flat floor is air — while broken terrain, caves and built structures pay in proportion
to their silhouette length. That is a genuinely open magnitude, it is a per-chunk mesh-memory and
draw cost as well as a job cost, and it is the first thing in this arc that could plausibly make
meshing a bottleneck. `SS-3`'s packet therefore requires a `perf-benchmark` capture before it
defaults on. `VO-8`'s waiver is not overridden — it covered a per-corner arithmetic change with a
`HasCustomBounds` short-circuit; this is a geometry-count change with none.

**Per-sample arithmetic.** Per face: nine cell fetches (which *replace* today's sixteen overlapping
ones) and at most nine `RotateLocalBounds` calls, of which only cells with `HasCustomBounds` reach
the rotation at all — 37 of 38 block types short-circuit. Per sub-vertex: at most nine
rectangle evaluations, each a bounding-box reject plus roughly ten flops, and one falloff
evaluation — order 100 flops, against the roughly 100 flops one `RotateLocalBounds` already costs.
At `N = 4` that is 25 sub-vertices × ~100 flops ≈ 2.5k flops per subdivided face, the same order as
the four corner samples the face already takes. **Estimates from instruction counts, not measured**
— which is exactly why `SS-3`, where they are multiplied by a much larger face population, does not
get to rely on them.

---

## 9. Phased implementation plan

### Universal regression gate (every phase)

- `dotnet build "Assembly-CSharp.csproj"` **and** `dotnet build "Assembly-CSharp-Editor.csproj"`
  (or one Rider `build_solution_start`, which covers both).
- Suites: **Validate Meshing**, **Validate Occlusion**, **Validate Lighting Engine**, and
  **Validate All** before closing a phase.
- The stale-editor-code gotchas in full: a **new** `.cs` file needs `AssetDatabase.Refresh()` before
  `dotnet build` reports truthfully (it reports a *false green* for a file not yet in the `.csproj`),
  and the reliable readiness gate is the **DLL timestamp**, not `IsCompiling`. When a menu-suite
  result contradicts the analysis, re-run the scenario inline via `Unity_RunCommand`.
- **Serialization tripwire:** zero on-disk change in every phase — mesh output is not persisted and
  no phase touches block-type authoring on disk. If a phase finds it wants a format change or a
  version bump, **stop**, invoke `serialization-migration`, and treat it as a scope change.
- **Nothing ships to default-on without in-game sign-off** for any phase marked behaviour-changing.

| Phase     | Scope                                                              | Effort | Depends on |
|-----------|--------------------------------------------------------------------|:------:|------------|
| **SS-0**  | Harness fixtures + sub-vertex probe + pre-SS record (suite-only)    |   🟢   | —          |
| **SS-1**  | Silhouette primitive, no consumer                                   |   🟢   | SS-0       |
| **SS-2**  | The contact-shadow term, partial occluders (**observation 1**)      |   🟡   | SS-1, D1–D3 |
| **SS-3**  | Extend the gate to full-cube occluders (**observation 2**)          |   🔴   | SS-2, D7   |
| **SS-4**  | Subdivide custom-mesh faces (S6)                                    |   🟡   | SS-2       |

**Minimal standalone-value set: SS-0 → SS-1 → SS-2.** It delivers the owner's first observation, is
provably bit-identical on ordinary terrain, adds no geometry, and leaves the round-blob artifact
(observation 2) for `SS-3` to judge separately with its cost on the table. `SS-4` is completeness
and can land in either order relative to `SS-3`.

---

### SS-0 — Harness fixtures and the sub-vertex probe (🟢, suite-only)

- **Scope:** `TestCustomMeshLibrary.AppendBoxMesh` generalized from `topY` to full `min`/`max`, with
  the existing half-slab call re-expressed through it. `TestMeshBlockPalette` gains `Post` (id 9,
  `Count` 9 → 10, opacity 15, `Facing6Roll2`), whose mesh bounds and `collisionBounds` come from
  **one shared pair of constants** (§6.1). New probe `TopFaceSubVertexField`, positional, following
  `TopFaceCornerSun`. **Does NOT touch production code.**
- **Ordering:** first. `SS-1` and `SS-2`'s baselines are unwritable without the post.
- **Prove-red:** two, both suite-local. (a) Author the post's `collisionBounds` deliberately
  disagreeing with its mesh and confirm the new fixture-agreement assertion reds — this is the F13
  defect made representable and then made impossible. (b) Confirm the existing half-slab fixtures
  are **byte-identical** through the generalized builder (a mesh fingerprint over the current
  meshing scenarios, unchanged) — the "generalization changed nothing" claim.
- **Acceptance:** universal gate; **Validate All 432 unchanged**. No in-game step (no production
  change).
- **Testability gain:** the harness gains a shape whose coverage is **non-linear in the cell**,
  which is the class of fixture `VO-9b` could not be judged against. Record its pre-SS shading field
  in the doc as an `**Amended:**` line — `SS-2`'s prove-red compares against it.
- **Doc-sync (same commit):** `MESHING_VALIDATION_HARNESS_FIDELITY.md` — new fixture entry, the
  shared-constant rule, and the probe.
- **Serialization:** none.

### SS-1 — Silhouette primitive (🟢, no behaviour change)

- **Precondition:** ✅ `SS-0`'s post fixture exists.
- **Scope:** `BurstOcclusionUtility.GetFaceSilhouette` and
  `LightAttenuation.AmbientOcclusionFaceSilhouette` (§5.1). Re-express `GetFaceCoverage` in terms of
  it **if and only if** the result is bit-identical (the area of the returned rectangle); otherwise
  leave `GetFaceCoverage` alone and record why — consolidation must not cost bit-identity.
  **Nothing consumes the new function yet.**
- **Ordering:** before `SS-2`. Independent of everything else.
- **Prove-red:** `math.transpose` on the shared rotation core. Expect **B50's post and roll rows** to
  red and the symmetric rows to stay green — the F10 signature. If B50 stays green under transpose,
  the baseline is not discriminating and must be strengthened before `SS-2` builds on it.
- **Acceptance:** universal gate; Validate All **433**. No in-game step.
- **Testability gain:** "what rectangle does this block project onto that face" becomes a pure,
  suite-callable function — the precondition for every visual assertion below.
- **Doc-sync:** `SUB_VOXEL_COLLISION_SYSTEM.md` §3.2's note that the rotation core is shared gains
  the silhouette consumer.
- **Serialization:** none.

### SS-2 — The contact-shadow term, partial occluders (🟡, behaviour change — observation 1)

- **Precondition:** ✅ D1 (Euclidean), D2 (`(1 − t)²`, `R = 1.0`) and D3 (replacement) decided by the
  owner 2026-08-09. §5.2 is the specification; do not re-derive it from D3's rejected options.
- **Scope:** `BuildFaceSilhouettes` hoisted into `PrepareFaceSampling` (the 3×3 in front of the face,
  into a fixed-size stack buffer — §5.4); the two fields of §5.2 evaluated through **one** shared
  function called from **both** `SampleFacePoint` and `ShadeSubVertex` (D6 obligation 1);
  `R`, `CELL_OCCLUSION_SHARE` and the falloff as named constants carrying §5.2's reductions in their
  docstrings. `DirectOpenFractionAt` is **deleted** — the direct cell stops being a special case, and
  leaving it in place would be a second occlusion path. `AmbientOcclusionRegionCoverage` loses its
  meshing consumer; decide and record whether it retires with `B41` or stays a unit-guarded utility.
  Gate unchanged (`hasPartialOccluder`), so full-cube faces stay unsubdivided. `GetSubQuad`,
  `EmitFaceQuad` and `SUB_CELL_TESSELLATION` are **not** touched.
- **Ordering:** after `SS-1`. Before `SS-3` and `SS-4`, both of which widen its reach.
- **Prove-red:** five mutations, each restored clean.
  1. Zero the occlusion field → **B51, B52** red, everything else green. (Non-vacuous.)
  2. Force the silhouette to the unit square → **B52** red, **B51** green. (The shadow tracks the
     shape, not the cell.)
  3. Apply the model only in `ShadeSubVertex` → **B53** red. (Position purity is real, not assumed.)
  4. Swap the occlusion sum for a `max`, or move `CELL_OCCLUSION_SHARE` off `0.25` → **B56** red on
     its 2- and 3-occluder rows only. This is the mutation that reproduces D3's rejected
     global-factor form, and B56 is the only guard that sees it.
  5. Set `R = 0.5` → **B49′** red with the face centre at `255`. This is the F18 signature reached by
     the radius rather than by the weights, and it is why `R` is a pinned constant and not a knob.
  Plus the standing claim: **B11 and every standard-cube baseline stay green throughout** — full-cube
  faces are unsubdivided here and §5.2's corner reduction is algebraic, so any movement means either
  the gate leaked or the reduction is not what §5.2 claims. Either way the phase stops and the doc is
  corrected before proceeding.
  ⚠️ **Verify, do not assume, that `B42` and `B46` survive** (§6.4). The reasoning that they should
  is the same "it degenerates correctly" shape that hid `VO-6`'s wrong half-cell step for a whole
  phase.
- **Acceptance:** universal gate **+ in-game confirmation, user sign-off required.** Look at, in
  order: (a) the visible half of a block's top face under a vertical slab — the requested contact
  shadow; (b) an inside corner between two walls — the `144 → 255` failure mode's home ground, where
  the centre should read around `218` rather than washing out; (c) whether face interiors generally
  read **too flat**, which is the accepted trade of the `(1 − t)²` profile (§5.2) and whose lever is
  the falloff exponent, not the model; (d) whether an isolated block's shadow still reads round —
  it should improve but not fully resolve until `SS-3`, since the gate here admits only partial
  occluders. A screenshot covers (a) and (b); (c) and (d) need the owner walking terrain.
- **Testability gain:** the meshing suite gains its first assertion that shading carries **sub-cell**
  information, which is the property `VO-9b` shipped a substrate for and could not assert.
- **Doc-sync (same commit):** `SMOOTH_AND_RGB_LIGHTING.md` AO section — the model gains a second
  channel; `MESHING_VALIDATION_HARNESS_FIDELITY.md` — B49's rewritten legs and their new control;
  `VOXEL_OCCLUSION_REFACTOR.md` §7's `SS-*` row via **`docs-sync`** (status only — that arc is
  closed, it is not re-opened).
- **Serialization:** none.

### SS-3 — Extend the gate to full-cube occluders (🔴, behaviour change — observation 2)

- **Precondition:** ⚠️ `SS-2` confirmed in game **and** the owner has decided D7 with §8's cost on
  the table. A contrary decision demotes this phase to a §10 roadmap row; it does not block `SS-4`.
- **Scope:** the gate that `CalculateCornerLights` reports (`hasPartialOccluder`) widens from
  "opaque **with custom bounds**" to "any opaque occluder whose silhouette is within `R` of the
  face". Nothing else changes — the term, the metric and the profile are `SS-2`'s. Consider a
  distinct `SUB_CELL_TESSELLATION` for the full-cube case if the measurement demands it, as a named
  constant with its own docstring, not a magic number.
- **Ordering:** after `SS-2`, and **after** its in-game sign-off specifically — judging a
  whole-world change on top of an unjudged local one confounds both.
- **Prove-red:** **B54** (the shadow follows the rectangle) is red on the pre-`SS-3` engine by
  construction, so this phase gets its prove-red on record for free — the `VO-6`/`KM01a` pattern.
  Then the gate mutation: force the gate always-on → vertex counts explode and the `B49` gate leg
  reds; force it never-on → B54 returns to red. Both restored.
- **Acceptance:** universal gate + **a `perf-benchmark` capture before defaulting on** (§8) + in-game
  confirmation with user sign-off. Expect `B11` and the standard-cube family to move; each movement
  is explained in the packet's record, not silently re-baselined. Ship behind a flag if the
  measurement is marginal, and add the flag to the flag-retirement backlog in the same commit.
- **Testability gain:** the suite gains a *metric* assertion (equal distance ⇒ equal shadow), which
  is orthogonal to every value assertion it has today.
- **Doc-sync:** `SMOOTH_AND_RGB_LIGHTING.md`; a `Documentation/Performance/` report for the capture.
- **Serialization:** none.

### SS-4 — Subdivide custom-mesh faces (🟡, behaviour change — S6)

- **Precondition:** `SS-2` confirmed in game.
- **Scope:** extend subdivision to the two custom-mesh paths (`MeshGenerationJob.cs:539` legacy,
  `:610` schema-aware), so a slab's own faces carry the same resolution as a standard cube's. The
  fluid paths (`:356`, `:380`, `:389`) stay untouched and that exclusion is stated, not implied —
  fluid surfaces have their own height model and their own vertex budget.
- **Ordering:** independent of `SS-3`; either order.
- **Prove-red:** **B55**. Also re-run `B44`/`B45` (sub-block face light) and `B48` (M02 culling)
  explicitly — this phase changes how many quads a custom-mesh face emits, which is exactly the
  assumption `B42`/`B46` broke on when `VO-9b` landed.
  ⚠️ **`ResolveFaceSampleCell` and the interior-face guard are the M03-class trap here.** A
  custom-mesh face may live *inside* its own cell; subdividing it must not re-derive its sample cell
  or its front half per sub-quad — both are per-face facts resolved before the split, and the third
  time this arc inferred geometry from a cell index it shipped a fully black slab.
- **Acceptance:** universal gate + in-game confirmation on a slab-on-slab and a fence-post
  configuration. User sign-off.
- **Testability gain:** closes the last path where shading resolution is pinned to face resolution.
- **Doc-sync:** `SMOOTH_AND_RGB_LIGHTING.md` §2.5.2's custom-mesh claim.
- **Serialization:** none.

---

## 10. Extension roadmap

| Version | Item                                                        | Notes                                                                                                                                                                    |
|---------|-------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| v2      | **Height-attenuated shadows for non-touching occluders**    | Today a volume that does not reach the surface casts nothing (D4), which is correct for a *contact* shadow and reproduces the signed-off top-slab reading. A block hovering a fraction above a surface could cast a softer, wider shadow by widening the silhouette and scaling `s` with the gap. Needs its own sign-off. |
| v2      | **A non-linear combiner for overlapping occluders**         | §5.2 sums the per-cell shares, which is what reproduces today's depths exactly (D3) and is therefore the right choice for a *replacement*. Where several occluders genuinely overlap the same solid angle it slightly over-darkens; `1 − Π(1 − shareᵢ·fᵢ)` is the physical form. Only worth revisiting if a configuration shows it, and it would move `B56`'s multi-occluder rows — so it needs its own sign-off, not a quiet swap. |
| v2      | **Compound (multi-AABB) silhouettes** — stairs, L-shapes    | **Owned by `VQ-4`**; this design interlocks only. `GetFaceSilhouette` should be shaped so a bounds *list* yields a rectangle list without re-cutting the seam, exactly as `VO-1`'s utility was asked to.                        |
| v2      | **Silhouettes for fluid surfaces**                          | Fluids have their own height/flow model; `SS-4` explicitly leaves them out.                                                                                              |
| v3      | **Per-pixel evaluation of this distance field, on `VX-1`'s occupancy volume** | **Interlock, not a phase — `VX-1`/`VX-8` own this ID space.** Delivers observation 2 per-pixel at zero vertex cost and would retire `SS-3` (D7's third answer). Full cubes only until `VX-5` widens occupancy to carry bounds + rotation. Supersedes v1.0's "per-face AO texture" row: a resident volume needs no atlas, no UV allocation, and no change to `MR-2`'s packed vertex format, so the per-face variant is strictly worse and is dropped rather than deferred. |
| —       | **`VX-8` (per-fragment light) does not subsume this design** | Recorded so it is not mistaken for a replacement. `VX-8` moves *where light is stored*; this design fixes *what the occlusion value is*. Hardware trilinear filtering of a voxel-resolution volume **is** the separable product S2 blames for the round blob, and one texel per cell cannot say where inside a cell a slab sits — so moving AO into the volume would bake both observations in permanently. `VX-8`'s own "vertex AO stays vertex-baked" line is correct, and this is the reason. |
| —       | **Adaptive `SUB_CELL_TESSELLATION`**                        | Density chosen per face from the occluder's distance, rather than one constant. Only worth it if `SS-3`'s measurement says the constant is the problem. |

---

## 11. Open questions

1. **D7 — the full-cube gate — is the one decision still open**, and it is what delivers
   observation 2. `SS-3` is blocked on it and on the cost it carries (§8). Resolves as a dated
   `**Amended:**` line here with the verdict marked in §4.
2. **Do face interiors read too flat?** §5.2 predicts a single wall's mid-band at `239` against
   today's `223`, and an inner corner's centre at `218` against `≈ 175` — the accepted cost of the
   `(1 − t)²` profile the owner chose. Resolved by `SS-2`'s acceptance step (c); if it reads flat,
   the lever is the falloff exponent alone, and the answer is recorded here either way.
3. **Do `B42` and `B46` survive `SS-2`?** They pin corner values under partial occluders, where the
   occlusion function changes from an octant fill fraction to a distance falloff. §6.4 predicts they
   do, because a slab's silhouette either contains a corner or lies half a cell from it. `SS-2`
   measures it; a surprise here means §5.2's reduction is narrower than claimed.
4. **What does `SS-3` actually cost on real terrain?** §8 gives the formula and the flat-terrain
   floor but no magnitude for broken terrain, caves, or built structures. Resolved by `SS-3`'s
   `perf-benchmark` capture, which lands as a `Documentation/Performance/` report.
5. **Should `GetFaceCoverage` be re-expressed as the area of `GetFaceSilhouette`, and does
   `AmbientOcclusionOctantCoverage` retire?** Consolidation is the house direction
   (`GetOctantCoverage` → `GetRegionCoverage` is the precedent), but `GetFaceCoverage` feeds light
   transport, where a rounding change moves lighting baselines — so it consolidates only if
   bit-identical. The octant form loses its last consumer in `SS-2`. `SS-1` and `SS-2` decide
   respectively and record which way each went.

---

## Document History

* **v1.2** - **Interlocked with `VX-1`/`VX-8` (the resident light volume) and `MR-8` (greedy meshing) after the owner raised the light-texture route.** No new IDs: the "per-chunk 3D light texture" idea is already tracked as **VX-1** + **VX-8**, and VX-8 already names itself MR-8's escape hatch. Recorded finding: **hardware trilinear filtering of a voxel-resolution volume IS the separable product S2 blames for the round blob**, and one texel per cell cannot locate a slab within its cell — so a light volume reproduces *both* observations rather than fixing either. That is the technical reason VX-8's "vertex AO stays vertex-baked" line is correct, and it makes the two changes orthogonal (VX-8 moves *where* shading lives; this design fixes *what the value is*). **D7 gains a third answer**: defer observation 2 to a per-pixel evaluation of this design's distance field on VX-1's occupancy volume — zero vertex cost, would retire `SS-3`, full cubes only until `VX-5` carries bounds. A six-volume baked alternative is ruled out on face-dependence (≈157 MB at 2x against VX-1's 3.3 MB). §10's v1.0 "per-face AO texture" row is dropped as strictly worse than the resident volume. This design is **merge-neutral** for MR-8 and its tessellation gate partitions the face set against MR-8's mergeable set
* **v1.1** - **D1/D2/D3 decided by the owner — Euclidean distance, a `(1 − t)²` falloff, and the silhouette field *replacing* the coverage fraction — and D3's specification corrected in the process.** The Option C written in v1.0 (a plain light average times one global `(1 − s·SS)` factor) does not work: a bounded occlusion field with a single strength cannot reproduce both `191` for one occluder and `64` for a 1×1 pit, so it would have flattened every deep AO configuration. The correct form gives each of the four cells meeting at a shaded point a fixed quarter share of the occlusion budget and multiplies a renormalized light blend by `(1 − occ)`; **at a cell corner with binary occlusion that is algebraically identical to today** (`255/191/128/64` verified against all four cases), which dissolves both objections v1.0 filed against Option C and keeps `SS-2` at 🟡 with `B11` and the standard-cube family green. A second correction followed from the same arithmetic: **`R = 0.5` is wrong and the radius is `1.0`** — at `0.5` a wall's occlusion dies before mid-face and an inner corner's centre computes `255`, the F18 interior-lightening signature reached by a different route. D5 is rewritten accordingly: this design **does** re-sample the ring per sub-vertex, and it is safe because occlusion is decoupled from the box-overlap weights, which is the one-line diagnosis of F18 itself. §6.3's B49 rewrite changes the *assertion* rather than the tolerance (the corner field is legitimately no longer the expectation), and new baseline **B56** pins the corner reduction as the guard the whole replacement rests on. D1's rounding concern answered with isocontour reach (today's blob bulges outward at diagonals; a Euclidean SDF cuts a fillet inward, and `(1 − t)²` confines it to the invisible tail); a p-norm escape hatch recorded but not built. Only D7 (the full-cube gate) remains open
* **v1.0** - Initial design. Establishes that the `VO-*` arc's coverage model cannot deliver either of the owner's two observations and that both have the same cause: **S1** — a fill fraction is linear across the cell for any occluder bounded by one plane, so sub-cell sampling of it is inert (generalizes `VO-*` F18 beyond slabs); **S2** — the four-cell average weights an occluder by a *product* of two per-axis ramps, giving hyperbolic isocontours, and the derivation reproduces F17's measured `12 of 32` darkened corners exactly. Chosen: derive the occluder's **silhouette rectangle** from the same rotated AABB `GetFaceCoverage` already projects (D4) — coverage is that rectangle's area, so the AABB-vs-AABB primitive and its shape-agnostic property survive intact. The new term **does touch the ring** and D5 states precisely why that is not the `VO-9b` defect (a new bounded attenuation versus a redistributed conserved blend). `VO-9a`'s "corner values do not move" is replaced by the **position-purity** invariant (D6), which implies the same seam guarantee while letting corners darken. Falloff radius is pinned to `0.5` cells by the existing gate's 3×3 reach (**S4**), and `s = 0.25` reproduces today's peak darkening exactly, making the change shape-only. Five phases: SS-0 fixtures (a post — the harness has **no** non-linear-coverage shape today, **S8**), SS-1 primitive, SS-2 the term for partial occluders, SS-3 the full-cube gate (the only phase with a real vertex cost and the only one requiring measurement), SS-4 custom-mesh faces. **B49 leg 3b will go red under SS-2 and §6.3 specifies a rewrite rather than a loosened tolerance**, with a new positive control that is satisfied by tessellation rather than by the shadow (F15). Metric, falloff, add-vs-replace and the full-cube gate are left open as owner decisions

---

**Last Updated:** 2026-08-09  
**Next Review:** when SS-0 starts, or when `VX-1` is scheduled (it changes D7) — D1/D2/D3 are settled and no phase is blocked before SS-3 (D7)
