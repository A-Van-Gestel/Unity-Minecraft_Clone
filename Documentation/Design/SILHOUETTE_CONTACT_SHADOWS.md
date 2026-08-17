# Silhouette-Based Contact-Shadow Ambient Occlusion (SS-*)

**Version:** 2.5  
**Date:** 2026-08-09  
**Status:** **`SS-0`…`SS-3a` implemented and confirmed in game (2026-08-09).** `SS-3` ships behind a
default-**OFF** Graphics setting (`Full-Block Contact Shadows`) by owner decision — the standing verdict is
*too flat*, not a performance concern, and its capture is waived. **`SS-4` (custom-mesh faces) not started.**  
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
| S1 | **Coverage answers the wrong question for a contact shadow.** A fill fraction over a sub-cell box varies near-linearly across the cell for an occluder bounded by one plane, so no refinement of it can produce a shadow that is dark at the occluder and fades quickly. Restates and generalizes `VO-*` **F18**: F18 says "not for a slab", S1 says "not for any single-plane boundary, which is the common case". ⚠️ **"Linear" is approximate — see S9**, which measures the actual departure and identifies the sharper property. | §4 D5, SS-2  |
| S2 | **The round blob is a weighting artifact, not a coverage artifact** (*derived in §2.3, and the derivation reproduces F17's measured `12 of 32` exactly*). Four-cell averaging weights an occluder by a product of two per-axis ramps → hyperbolic isocontours. Fixing it needs a different **metric**, not different values.                                                                                                                                        | §4 D1, SS-3  |
| S3 | **The silhouette is already computed — `GetFaceCoverage` throws it away.** That function does a touch test on the normal axis and then multiplies the two perpendicular extents. Those extents *are* the silhouette rectangle; the multiply reduces it to an area. A sibling that returns the rectangle is a few lines and keeps the AABB-vs-AABB primitive intact, so the shape-agnostic property survives untouched.                                              | §4 D4, SS-1  |
| S4 | **The falloff radius is pinned to `1.0` cells — by the gate above and by the F18 defect below.** `hasPartialOccluder` accumulates over the four cells the sample box reaches at each of the four corners, whose union is the full 3×3 in front of the face; that 3×3 spans `[−1, 2]²` in the face's parameter space, so a silhouette in it can lie `0` from the face and one outside it can never lie less than `1` from it. **`R = 1.0` is therefore exactly the radius the existing neighbourhood supports — no more, no less** — and `R > 1` is a scope change, not a tuning knob. It is also a *lower* bound: at `R = 0.5` (this document's v1.0 value, chosen to match `SAMPLE_BOX_HALF_EXTENT`) a wall's occlusion reaches only half a cell, and the interior of a face in an inner corner between two walls computes **255** — numerically the `144 → 255` signature of the shipped F18 defect. Verified across both radii, §4 D2. | §4 D2, SS-2  |
| S5 | **Corner values must move, and "corners do not move" was never the real invariant.** `VO-9a` froze corner values so a subdivided face would agree with an ordinary neighbour along their shared edge. The property that actually delivers that is weaker and survives this design: the shading value is a **pure function of the sample point's position and the block field**, independent of which face emits it and at what density.                              | §4 D6, SS-2  |
| S6 | **Custom-mesh and fluid faces are never subdivided** (verified by call-site grep, §2.1). They will receive the new term at *corner* resolution only. Consistent — position-purity still holds at every shared vertex — but a slab's own top face gets a coarse version of the effect. Must be stated up front; discovering it in game would read as a bug.                                                                                                          | S6 → SS-4    |
| S7 | **B49 leg 3b will go red under `SS-2`, and its *assertion* is what has to change — not its tolerance.** The leg says "a subdivided face stays on the bilinear field of its own corners", which under the chosen replacement (§4 D3) is precisely the property being removed on purpose. Widening the `1.5` bound to accommodate the departure would leave a guard that catches nothing, and the defect it exists for — face interiors lightening toward `255` as the ring's occlusion is lost — would pass straight through it. §6.3 replaces the assertion with the defect's own numeric signature and gives it a control that tessellation, not the shadow, satisfies. | SS-2         |
| S9 | **⚠️ Measured by `SS-0`, and it corrects S1's phrasing: the post is not "more non-linear" than the slab — it is *non-monotonic*, and that is the property that matters.** Sweeping the mesher's own query across a cell, the vertical slab departs from an endpoint-linear fit by **0.083** and the post by only **0.038** — the slab is the *more* non-linear of the two, the opposite of what this document predicted. The post's sweep instead *reverses direction* (`0.062 → 0.100 → 0.083 → 0.071 → 0.062 …`), which no interpolation of two corner values can reproduce at any density, while a monotonic ramp very nearly can. The cause is that `GetRegionCoverage` **normalizes by the query region's own volume**: near a cell edge the region is clipped and shrinks, inflating the fraction and producing a *rise* where distance to the occluder says there should be a fall. Coverage is not a mildly-wrong distance field; it is not a distance field at all. | Recorded; `B50` asserts monotonicity, not linearity |
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

### D7 — Gate scope for full-cube occluders ✅ **DECIDED (owner, 2026-08-09): build `SS-3` now, per-pixel is the destination**

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
per-pixel evaluation on VX-1. Recorded as an interlock, not a phase: `VX-1`/`VX-8` own that ID space
(§10).

#### The decision, and the research it rests on (2026-08-09)

✅ **Build `SS-3` now (route A). Route B — per-pixel on `VX-1`'s volumes — is the final destination,
not a competitor.** `SS-3` is not throwaway work under that plan: route B needs a far-field fallback
by construction (below), and `SS-3` *is* that fallback.

A research pass compared four routes. Two findings moved the decision, and both are corrections to
what this section assumed when it was written:

**1. Route B needs the *light* volume, not just the occupancy volume — because of `SS-2a`.** This
section was written when the model was "a light mean times `(1 − occ)`", where a per-pixel occlusion
factor could multiply an interpolated vertex light. `SS-2a`'s second fix changed that: occlusion now
enters the **light weights** (`out = Σ wᵢ(1−sᵢ)Lᵢ`, §5.2), which is exactly what stops the two
factors double-counting. A fragment therefore needs **per-cell light**, so route B depends on
`VX-1`'s `_VoxelLightVolume` as well — roughly 9 occupancy taps plus 4 light taps per opaque
fragment. The "no extra memory, zero vertex cost" claim survives; the "occupancy alone is enough"
claim does not.

**2. Route B has an AO horizon, and AO tolerates one far worse than fog does.** `VX-1`'s default
volume spans ≈ 160 voxels — a **5-chunk radius**, which is exactly today's default view distance and
well short of the 10 and 20 that `FP-4` swept. Beyond the volume there is no occupancy to tap, so
every corner shadow would pop off at a fixed radius. Fog degrades gracefully to height fog; AO does
not degrade, it vanishes. **The owner's steer is that the volume should be view-distance aware**, and
that is filed against `VX-1` (see that entry for the quadratic memory it implies and the cascade
answer). Either way the far field needs vertex-baked AO — which is `SS-3`.

**Measured cost of `SS-3`, replacing this section's "genuinely open magnitude":** see §8. Between
**3.1× and 4.7× vertices at `N = 4`**, or **1.4×–1.7× at `N = 2`**, with flat ground unaffected at
exactly 0 %. `N` is left open for the packet's measurement, with `N = 2` the expected answer.

**A cost route B carries that nothing else does: the suite goes blind.** The meshing suite asserts
*mesh contents*. Move AO to the fragment shader and `B41`–`B49`, `B56`, `B57`, `B58` and the `VO-*`
corner baselines are all testing values that no longer exist in the mesh, with no golden-image or
shader-level harness to replace them. Given this arc's record — three defects, every one caught in
game and none by the suite — that is a heavier price than it first looks, and it is a reason to keep
a CPU path alive rather than retire one.

#### Two further routes, recorded so they are not re-derived

| Route | What it is | Verdict |
|-------|------------|---------|
| **C — per-vertex occupancy bitmask, evaluated per pixel** | The AO field on a full-cube face is a pure function of `(face-local uv, 8-neighbour occupancy)` — **8 bits**, and the vertex format has room (`Normal` is `SNorm8×4` with an unused `w`; opaque blocks write `Color` pure white). The fragment derives its face-local position from the voxel-space position it can already compute and evaluates §5.2 analytically. No volume, no `VX-1`, no radius, no upload latency, ~100 flops/pixel. | ❌ **Not now.** Without per-cell light at the fragment it can only apply a *separable* occlusion factor, which is the approximation `B58` exists to catch — it would drive that baseline red. It is also incompatible with `MR-8`: a merged quad spans cells with different masks. Kept on record because it is the only zero-cost route with **no** `VX-1` dependency, and it becomes interesting if route B stalls. |
| **D — URP's screen-space AO renderer feature** | One checkbox; the renderer currently has no renderer features at all. | ❌ **Rejected.** Screen-space and view-dependent, haloes on voxel edges, cannot produce a voxel-exact contact shadow, and does nothing for observation 1. A different effect that happens to darken corners, not a substitute for this model. |

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
occ(p) = saturate( Σ over the 4 tangent QUADRANTS around p
                     QUADRANT_OCCLUSION_SHARE · Falloff( Distance(p, nearest silhouette in q) / R ) )

L(p)   = Σᵢ wᵢ(p) · openᵢ(p) · Lᵢ                  // the existing four box-reachable cells
         ────────────────────────────
              Σᵢ wᵢ(p) · openᵢ(p)                   // renormalized; guard the zero denominator

out(p) = L(p) · (1 − occ(p))
```

with `openᵢ(p) = 1 − Falloff(Distance(p, silhouetteᵢ) / R)` **per cell** (the light mean's weights
stay a per-cell question), `R = 1.0` (D2), `Falloff(t) = (1 − t)²` (D2), `Distance` the Euclidean
distance to the silhouette rectangle (D1), and `QUADRANT_OCCLUSION_SHARE = 0.25`.

> ⚠️ **Corrected in `SS-2a`, and this one is the whole reason ordinary terrain moved: `openᵢ` above is
> the *visibility* term, and the light mean must be weighted by it.** `SS-2` shipped the mean weighted
> by a per-block "holds usable ambient light" flag instead. Those two agree exactly while every
> occluder is **opaque** — an opaque cell both occludes and holds no usable light — so the substitution
> looked sound and every baseline agreed. It breaks on the **sealed diagonal**, which is *air*: it
> holds light, so it fed the mean at full weight while the corner seal simultaneously counted it as
> fully occluding. Its light was credited and debited at once, and at a concave corner the hidden cell
> is the darkest one around, so real corners rendered **up to twice as dark as this model claims**.
>
> Weighting the mean by `wᵢ · openᵢ` makes the reduction far stronger than the one this section
> originally claimed. The kernel weights sum to one, so the visible weight **is** `1 − occ` at a
> corner, and the renormalization cancels the `(1 − occ)` factor outright:
>
> ```
> out(p) = [ Σ wᵢ openᵢ Lᵢ / Σ wᵢ openᵢ ] · (1 − occ)  =  Σ wᵢ · openᵢ · Lᵢ
> ```
>
> — which is *exactly* the expression the pre-`SS-2` engine evaluated, **for an arbitrary light field
> rather than only a uniform one**. The renormalized form survives only to handle the degenerate case
> below. Baseline **B58** pins it.
>
> **The guard it needs:** where the kernel collapses onto a single cell that occludes — a face centre
> under a slab standing on it — the visible weight is zero and the mean is undefined. Fall back to the
> unshadowed mean there; the occlusion term already carries the darkening, and a zero renders the face
> black. That black face is the defect `SS-2` hit and mis-diagnosed as "light must not be weighted by
> the per-point shadow": true of an *unrenormalized* weighting, false of this one.

> ⚠️ **Corrected during `SS-2` execution: the two-occluder row read `128` in v1.3–v1.4 and the real
> value is `64`.** Classic voxel AO darkens a corner *fully* once both flanking cells are solid,
> whatever sits diagonally — the diagonal quadrant is not visible from that corner at all, because the
> two walls meeting there stand between them. The engine has always done this (`SampleNeighborLight`'s
> `sidesSealCorner` test); this document's table simply had not accounted for it. A model that treats
> the nine cells as independent lightens **every inside corner in the world** from `64` to `127`.
> The rule is preserved as `shadow[diag] = max(own, sideA · sideB)` — a smooth form of the original
> boolean test, so a partial occluder half-seals a corner instead of switching it. **The combiner is a
> product, and `SS-2a` is what that costs to get wrong**: `SS-2` shipped `min`, which is also an
> identity at a corner but holds the seal at *full strength* along the whole diagonal, sealing open
> floor a cell away from the corner. Measured, the seal's contribution half a cell out ran `16` light
> units against `16` in the corner itself — flat. The product decays it to `4` while the corner holds
> at `63`. Baseline **B57** pins both ends.

> ⚠️ **Corrected in `SS-3a`: the sum is over the four QUADRANTS around the point, not over the nine
> cells.** At a cell corner the two are the same thing — the four cells meeting there *are* the four
> quadrants — which is why the per-cell form reproduced every corner value, passed every baseline and
> shipped. Away from a corner they diverge, and the per-cell form reads the **grid** rather than the
> **geometry**: a straight wall arrives as three separate cell silhouettes, so at a cell seam two of
> them touch the point (`0.25 + 0.25`) while mid-cell only one touches and the others sit half a cell
> away (`0.25 + 2 × 0.0625`). Same wall, different answer — measured `128` at the seams against `159`
> mid-cell, a scallop at every seam along every wall in the world.
>
> **The seams were right and the mid-cell samples were wrong.** Before sub-cell shading that edge had
> only its two corner samples, both `128`, and the GPU interpolated a uniform band; the interior
> samples are what disagreed with the corners the model already had. Binning by direction restores
> the agreement: a quadrant is darkened by the *nearest* silhouette covering area in it, so a wall
> passing the point fills the same two quadrants wherever along it the point sits.
>
> **A silhouette that merely touches a quadrant boundary covers none of it** — a neighbouring cell's
> edge passing exactly through the shaded point must not darken the quadrant behind it, or every
> occluder would darken all four. `QUADRANT_AREA_EPSILON` rejects the zero-area clip.
>
> **The corner seal stays per-cell and is applied to both readings.** Which cell is diagonal to which
> is a fact about cells, not directions, and the identity that keeps a corner matching the pre-`SS-2`
> model holds only while the cell and quadrant readings agree there (`B58`).
>
> **The deliberate consequence, accepted by the owner with the numbers on the table:** an isolated
> block's contact shadow deepens at the middle of its edge, `191 → 128`, because a block touching you
> along a whole edge fills two quadrants where the per-cell form charged it a single quarter. Its
> *corners* still read `191`. This is a visible change on every free-standing block, and it is the
> price of the wall being right.

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
| Corner, 2 fully-occluding neighbours      | `64`        | `64`       |
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

Numbering was claimed against the meshing suite's tip at authoring time, **B49** (Validate All
**432**); the executor re-confirms the tip before writing. **As shipped through `SS-2a` the tip is
`B58` (53 meshing baselines, Validate All 437)** — `B51`–`B55` were never written: `SS-2` folded
their claims into rewritten `B46`/`B49` plus the new `B56`, and `B54`/`B55` remain owed by `SS-3` and
`SS-4`.

| ID      | Phase  | Asserts                                                                                                                                                       | Prove-red                                                                                                                             |
|---------|--------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------|
| **Occl. B6** | SS-1 | The silhouette primitive is shape-derived: full cube → unit square + touching; bottom slab's `+Y` → **not** touching; vertical slab `0x03` → touching, half the cell on `z`, full on `x`; **post** → the central `0.25 × 0.25`. Rolling the slab moves *which* half. | `math.transpose` on the rotation. **Only the asymmetric rows catch it** — `VO-1`'s F10 lesson, where 26 physics baselines stayed green and one occlusion row did the work. The post row and the roll rows are that guard here. |
| **B51** | SS-2   | **The direct answer to F18.** On the floor face under a vertical slab, the sub-vertex nearest the slab's edge is strictly **darker than the linear interpolation of the face's own corner values**, and the far edge is unchanged within rounding. A departure-from-linear assertion, not a predicted constant, so it survives D1/D2 retuning. | Pre-`SS-2` engine fails it by construction (F18 measured the profile as linear). Positive control: replace the slab with air — no departure, and that control is independent of the shadow's shape. |
| **B52** | SS-2   | The **post** casts a shadow whose darkest sub-vertex lies under the post's footprint and which is zero beyond `R` — the shape-agnostic claim (goal 3) asserted on a shape no production block has. | Force the silhouette to the unit square → the shadow stops tracking the footprint and B52 reds while B51 (a half-cell silhouette) survives. |
| **B53** | SS-2   | **Position purity (D6).** A corner emitted by a subdivided face and the same corner emitted by its ordinary neighbour carry the same value; and a subdivided face's corner equals the undivided formula at that point. | Apply the model in `ShadeSubVertex` only → corners disagree across the seam. This is the mutation that reproduces the seam `VO-9a` exists to prevent. |
| **B56** | SS-2   | **The corner reduction — the claim the whole replacement rests on.** With full-cube occluders, a face corner reads `255 / 191 / 128 / 64` for 0 / 1 / 2 / 3 occluding neighbours — **exact values, no tolerance beyond UNorm8 rounding**, because §5.2's collapse is algebraic. Read through `TopFaceCornerSun` on an open floor, one wall, an inner corner, and a 1×1 pit. | Perturb `CELL_OCCLUSION_SHARE` off `0.25`, or swap the sum for a `max` → the 2- and 3-occluder rows red while the 1-occluder row survives, which is the exact signature of D3's rejected global-factor form. |
| **B49′**| SS-2   | Rewritten per §6.3 — the inner-corner face centre is bounded away from the unoccluded value and correctly ordered against the face's corners; new roll-away positive control. | Force the ring's occlusion to vanish in the interior (the historical `VO-9b` mutation) → the centre returns `255` and every clause reds. |
| **B57** | SS-2a  | **The corner seal is local.** A four-configuration differential (both walls / either / neither) isolates what the second wall adds beyond two independent walls, and asserts it is at full strength in the corner *and* materially weaker half a cell out along the diagonal. The first suite scenario to read the field **between** a face's corners and its centre, which is where SS-2a's artifact lived. | Both directions, both executed: the shipped `min` combiner reds the locality leg alone; deleting the seal reds the corner leg *and* B56. Neither leg is satisfiable by the other's failure mode. |
| **B58** | SS-2a  | **A cell the seal hides does not also feed the light average**, asserted under the suite's **first non-uniform light field**. Two legs: darkening the hidden diagonal must not move a *sealed* corner, and must still move an *open* one. | Weight the light mean by the kernel alone (what SS-2 shipped) → B58 reds alone, `64 → 32`, with all 52 others green. The open-corner leg is the F15 control: a model that simply dropped the diagonal would satisfy the first leg and fail this one. |
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
to their silhouette length. It is a per-chunk mesh-memory and draw cost as well as a job cost, and it is
the first thing in this arc that could plausibly make meshing a bottleneck. `SS-3`'s packet
therefore requires a `perf-benchmark` capture before it defaults on.

**Measured 2026-08-09, replacing this section's original "genuinely open magnitude".** Mesh a
fixture, then ask of every emitted quad whether any of the eight ring cells in the layer in front of
it is solid — `SS-3`'s gate at `R = 1`. Vertex projections are `plain × 4 + subdivided × N² × 4`:

| Geometry                          | Faces `SS-3` admits | Vertices at `N = 4` | at `N = 2` |
|-----------------------------------|--------------------:|--------------------:|-----------:|
| Flat ground                       | **0 %**             | 1.00×               | 1.00×      |
| Rolling terrain (Perlin, gentle)  | 13.8 %              | **3.07×**           | 1.41×      |
| Rolling terrain (Perlin, rough)   | 24.3 %              | **4.65×**           | 1.73×      |
| Built room (walls on a floor)     | 16.0 %              | 3.40×               | 1.48×      |

The flat row confirms this section's own claim exactly. **`N = 2` is the expected answer** — a
quarter of the cost, and with a `(1 − t)²` falloff evaluated at the half-cell midpoint the shade
still concentrates in the half-cell against the occluder, which is what observation 2 asks for.
Caves are unmeasured and will sit above the rough-terrain row; the packet's capture decides. `VO-8`'s waiver is not overridden — it covered a per-corner arithmetic change with a
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
| ~~**SS-0**~~ | ✅ Harness fixtures + sub-vertex probe + pre-SS record (suite-only) |   🟢   | —          |
| ~~**SS-1**~~ | ✅ Silhouette primitive, no consumer                                |   🟢   | SS-0       |
| ~~**SS-2**~~  | ✅ Contact-shadow term, partial occluders (**observation 1**) — rejected on its first in-game pass, carried by `SS-2a` |   🟡   | SS-1, D1–D3 |
| ~~**SS-2a**~~ | ✅ Fix the corner-darkening artifact SS-2 introduced — confirmed in game with SS-3/SS-3a |   🟡   | SS-2       |
| ~~**SS-3**~~ | ✅ Extend the gate to full-cube occluders (**observation 2**) — shipped **default-off** on taste; capture **waived** (owner: cost is not the concern) |   🔴   | SS-2a, D7  |
| ~~**SS-3a**~~ | ✅ Bin occlusion by direction, not by cell — confirmed in game; one residual accepted (§10) |   🟡   | SS-3       |
| **SS-4**  | Subdivide custom-mesh faces (S6)                                    |   🟡   | SS-2a      |

**Minimal standalone-value set: SS-0 → SS-1 → SS-2.** It delivers the owner's first observation, is
provably bit-identical on ordinary terrain, adds no geometry, and leaves the round-blob artifact
(observation 2) for `SS-3` to judge separately with its cost on the table. `SS-4` is completeness
and can land in either order relative to `SS-3`.

---

### SS-0 — Harness fixtures and the sub-vertex probe (🟢, suite-only) · ✅ **EXECUTED 2026-08-09**

**What landed.** `TestCustomMeshLibrary.AppendBoxMesh` takes full `min`/`max` instead of `topY`;
`HalfSlabBounds` / `PostBounds` are exposed as `BlockCollisionBounds` values and the geometry is
built **from those same values**, which `TestMeshBlockPalette.MakeCustomBox` (generalizing
`MakeHalfSlab`) then assigns to the block — so a fixture's shape and its authored volume are **one
value used twice**, and F13's divergence is unrepresentable rather than merely asserted against.
New `Post` block (id 9, `Count` 9 → 10). New positional probe `TopFaceSubVertexField` +
`SubVertexSample`, with `B49`'s `AssertInteriorNearCornerField` re-routed through it (B49 staying
green is what proves that extraction behaviour-preserving). Baseline **B50** in the new
`MeshingValidationSuite.MeshFixtures.cs`; meshing suite **49 → 50 baselines, all green**.

**Prove-red, both by mutation and both reverted.**

| Mutation                                                         | Result                                                                 |
|------------------------------------------------------------------|------------------------------------------------------------------------|
| Post authored with the slab's bounds (breaks the shared value)   | **B50 red and only B50** (49/1), both legs, diagnostic naming block 9  |
| `PostBounds` widened to a full-width bar (shape stays consistent) | **B50's monotonicity leg red alone** — leg 1 green, since one value still feeds both sides |

The second mutation is the one that matters: it shows leg 2 discriminates on the *shape* of the
fixture rather than riding on leg 1's agreement check.

**Pre-SS record** (floor block's `+Y` face, sun out of 255, `SmoothLightingQuality.High`):

| Occupant of the cell above | Floor's top face                        | Quads |
|----------------------------|-----------------------------------------|-------|
| nothing                    | `255` flat                              | 1     |
| **post**                   | `251` … `247`                           | 16    |
| vertical slab `0x03`       | `255 / 234 / 225 / 213 / 191` across `z` | 16    |

Three things this pins down, none of which were certain before:

1. **The slab row reproduces `VO-*` F18's published profile exactly**, which cross-checks the new
   fixture and probe against the measurement this whole design is built on.
2. **A solid quarter-cell column standing directly on a face darkens it by 4–8 units out of 255 —
   about 3%**, against the slab's 25%. A fence post currently casts essentially no shadow, and that
   is the single clearest statement of what `SS-2` has to fix.
3. **The post already trips `VO-9b`'s tessellation gate** (16 quads, not 1), so `SS-2` needs no gate
   change to reach it — the substrate is in place for exactly the shape it could not shade.

**Scope (as planned).** `TestCustomMeshLibrary.AppendBoxMesh` generalized from `topY` to full `min`/`max`, with
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

### SS-1 — Silhouette primitive (🟢, no behaviour change) · ✅ **EXECUTED 2026-08-09**

**What landed.** `BurstOcclusionUtility.GetFaceSilhouette` (touch test + the rectangle on the two
axes perpendicular to the face, saturated to the cell) and
`LightAttenuation.AmbientOcclusionFaceSilhouette` with the same gating as every sibling predicate —
`!IsOpaque` → no silhouette, `!HasCustomBounds` → the unit square without entering the rotation path.
**Nothing consumes either yet.** Baseline **B6** in the **Occlusion** suite (5 → 6); meshing
untouched at 50.

> **Baseline numbering, corrected against the plan.** §6.4 assigned this phase *meshing* `B50`, but
> `SS-0` consumed that number during execution, and on reflection a pure-function test of a shape
> primitive belongs in the **Occlusion** suite `VO-1` built for exactly this layer — it needs no mesh,
> no world fixture, and no job run. It is therefore **Occlusion B6**, and §6.4's remaining rows are
> unaffected.

**`GetFaceCoverage` was deliberately NOT re-expressed through the new primitive** — a decision §11
question 4 left to this phase. D4 anticipated the consolidation (coverage *is* the silhouette's
area), and it would be a one-line change. Against it: `GetFaceCoverage` feeds **light transport**,
where `FaceBlocksLight` thresholds it at `>= 1 − 1e-4`, so a last-ulp difference between
`saturate(max − min)` and `saturate(max) − saturate(min)` could flip a face from blocking to open and
move lighting baselines — to save one multiply. **The risk the consolidation was meant to remove is
drift between two implementations, and a baseline removes that just as well**: B6 asserts the
silhouette's area equals `GetFaceCoverage` **bitwise** (`!=`, not an epsilon) across every fixture,
face and orientation. Guarded rather than merged, following the same "agreement" pattern as `B5`.

**Prove-red — `math.transpose` on the shared rotation core, reverted clean.** The F10 signature
reproduced exactly:

| Guard                          | Under transpose | Note                                                                 |
|--------------------------------|-----------------|----------------------------------------------------------------------|
| Occlusion `B1` (identity)      | green           | Identity is its own transpose                                        |
| Occlusion `B3` (full cube)     | green           | Symmetric                                                            |
| Occlusion `B4` (structural)    | green           | One-full/one-empty/opposite is transpose-invariant                   |
| Occlusion `B5` (managed==core) | green           | Both sides share the core, so agreement cannot see a core bug        |
| Occlusion **`B2`**             | **RED**         | Faces 0/1 swapped — the pre-existing guard                           |
| Occlusion **`B6`**             | **RED**         | Back/Front touch flipped, TOP silhouette on the wrong half, and **all four rolls collapsed to one rectangle** |
| Meshing **`B46`**              | **RED**         | Downstream of the same core (per-roll corner shading goes flat)      |

**The post rows stayed green under the mutation, and that is the finding.** At meta `0x00` the post
is transpose-invariant, so B6's discrimination comes entirely from its **roll** leg — the packet's
warning that a non-discriminating B6 must be strengthened before `SS-2` builds on it is satisfied by
that leg alone, not by the post. Keep it: a future edit that drops the roll assertion for "the post
covers the interesting shape" would silently remove the only rotation guard this baseline has.

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

### SS-2 — The contact-shadow term, partial occluders (🟡, behaviour change — observation 1) · ⚠️ **CODE COMPLETE 2026-08-09 — IN-GAME REVIEW FOUND A DEFECT (SS-2a), NOT SIGNED OFF**

> ⚠️ **IN-GAME REVIEW 2026-08-09: REJECTED — a corner-darkening artifact.** The owner reviewed a
> walled enclosure with a snow floor and reported that SS-2 *"re-introduced the corner darkening
> artifact"*, visible at the **top-left and top-right** of the enclosure: dark wedges spreading
> diagonally out of the concave corners where two walls meet, across floor that should be open.
> **SS-2 is not signed off, and `SS-3`/`SS-4` are blocked behind fixing it** — see the `SS-2a` packet
> below. The suite did not catch it: all 435 baselines are green, because every scenario reads a
> face's corners or its own interior, and this artifact lives in the *field between* a corner and the
> open floor a cell away.

**What landed.** `BurstOcclusionUtility.GetPlaneSilhouette` (the general form of `SS-1`'s face
silhouette, against an arbitrary plane through the cell) + `LightAttenuation.AmbientOcclusionPlaneSilhouette`,
`ContactShadowFalloff`, `ContactShadowRadius`, `CellOcclusionShare`. In `MeshGenerationJob`:
`PrepareFaceSampling` hoists the 3×3 of cells in front of a face once (silhouettes + light + the
`hasPartialOccluder` gate) into a `stackalloc` span; `ShadePoint` replaces `SampleFacePoint` /
`SampleCornerPoint` / `ShadeSubVertex` as the **single** shading function for corners and sub-vertices
alike; `DirectOpenFractionAt`, `SampleNeighborLight` and `Weigh` are deleted. Coverage is gone from
the AO path entirely. Baseline **B56**; **B46** and **B49** rewritten (below). Validate All **434**.

**Measured — the model does what it was designed to do.**

| Configuration                                   | Before `SS-2`                   | After                          |
|-------------------------------------------------|---------------------------------|--------------------------------|
| Corner, 0 / 1 / 2 / 3 occluding neighbours       | `255 / 191 / 64 / 64`           | `255 / 191 / 64 / 64` (exact)  |
| **Post** standing on a face                     | `251 … 247` (≈3 %)              | **`191` under its footprint**, `241` at the far corners |
| Vertical slab, across the visible half           | `255 / 234 / 225 / 213 / 191`   | `239 / … / 191` (shadow reaches the whole face) |
| Inner corner between two walls, face centre      | `≈127` (bilinear)               | `191`                          |

The post is the headline: a shape that previously cast **essentially nothing** now shades to the same
depth a full cube does, with no per-shape code — goal 3, measured.

> ⚠️ **Two model errors were found by measurement before any baseline was written.** Both are recorded
> because each would have shipped as a visible defect.
>
> 1. **The face centre under a slab rendered `0`.** The light mean was weighted by each cell's
>    *point-wise shadow*, so where the interpolation kernel collapses onto a single occluding cell there
>    was no light source left at all. Fixed by weighting the light mean by a **per-cell** "holds usable
>    ambient light" flag (`!IsFullyOpaqueCell`) instead — which is also the cleaner separation D3 asks
>    for, since the per-point shadow now appears only in the occlusion term.
> 2. **Inside corners lightened from `64` to `127`** — the missing corner seal, see §5.2's corrected
>    table.
>
> Both were invisible to the suite as it stood; both were caught by measuring the model's own
> predictions against the engine before trusting them.

**Bug M03 re-introduced and re-fixed during execution.** The interior-face touch test asked only
"does the volume reach the shaded plane", which a half slab's own volume does *from below* — so a
recessed slab rendered **fully black** again, exactly as in `VO-8`. `GetPlaneSilhouette` now requires
the volume to reach the plane **and** have extent on the shaded side. Caught by **B47**, which is the
whole reason that baseline exists.

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
- **Prove-red (executed 2026-08-09, each restored clean):**

  | Mutation | Result |
  |----------|--------|
  | Occlusion sum → `max` | **B56 red on its 2- and 3-occluder rows only** (0/1 stay correct), plus B49. This is D3's rejected global-factor form, and B56 is the only guard that names it. |
  | `ContactShadowRadius` 1.0 → 0.5 | **B49 red** with the inner-corner centre at **255 with and without walls** — the F18 interior-lightening signature, reached through the radius instead of the weights. B56 unaffected, so the two guards are orthogonal. |

  Both mutations also confirm the baselines are not vacuous. **B56 did not exist when SS-2 began**: the
  `max` mutation flattened every inside corner in the world and only tripped a slab-specific scenario
  indirectly, which is precisely the gap the plan predicted and B56 closes.

- **Prove-red (as planned):** five mutations, each restored clean.
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
- **Baselines rewritten, with their assertions changed rather than loosened.** **B46**'s "exactly two
  corners darkened, two at full `255`" no longer discriminates: an occluder half a cell away now shades
  the far corners slightly too (`239`), so "how many corners are below 255" became 4. It asserts **how
  many carry the strongest darkening** instead — still 2, still 4 the moment occlusion turns
  face-uniform, and independent of the falloff radius. **B49**'s leg 3b asserted the subdivided face
  stayed on its corner field, which `SS-2` removes on purpose; it is now a **differential** — the same
  face with and without the walls beside it — which assumes nothing about corner indexing, profile or
  radius, and which the F18 defect drives to zero. §6.3 called for exactly this rewrite.
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

### SS-2a — Fix the corner-darkening artifact (🟡, behaviour change) · ✅ **FIXED 2026-08-09 — AWAITING IN-GAME CONFIRMATION**

**Symptom (owner, in game, 2026-08-09).** Concave corners — where two walls meet — cast a dark wedge
that spreads diagonally across open floor instead of darkening only the corner itself. Reported as
the *"corner darkening artifact"* re-introduced by SS-2.

> ✅ **The suspicion below was confirmed and the fix is one line: the seal's combiner is a product,
> not a `min`.** What follows is the packet as filed, then the record of what was measured.

**Leading suspicion, stated as a suspicion.** `MeshGenerationJob.ApplyCornerSeal`. §5.2's corner seal
reproduces classic voxel AO's rule that a corner flanked by two solid cells is fully dark whatever
sits diagonally — correct, and `B56`'s 2- and 3-occluder rows depend on it (without it they read
`127` instead of `64`). But SS-2 implemented it as the *continuous* form
`shadow[diag] = max(own, min(sideA, sideB))`, evaluated at **every** sample point over
**distance-attenuated** shadows, and that generalization is what is unproven:

- The original rule is binary and only ever evaluated **at a cell corner**, where "both sides are
  solid" really does mean the diagonal quadrant is invisible from that point.
- The continuous form fires wherever a point is within `ContactShadowRadius` of two perpendicular
  occluders, which at `R = 1.0` is a band a full cell wide around every concave corner. There it adds
  up to `CellOcclusionShare` of occlusion attributed to a cell that is **air and plainly visible from
  that point** — the shape and placement of the reported wedges.

**Decisive diagnostic — run this before designing a fix.** Disable `ApplyCornerSeal` entirely and
look in game.

- Wedges **gone** ⇒ the seal's generalization is the cause. `B56`'s 2/3-occluder rows will go red at
  `127`, which is the *expected* red and confirms the diagnostic bit rather than contradicting it.
- Wedges **remain** ⇒ the cause is the radius/falloff itself (two walls each contributing
  `0.25 · f(d)` over a one-cell reach), and the lever is `ContactShadowRadius` or the profile, not
  the seal. Re-open §4 D2 with the owner in that case.

**The constraint any fix must satisfy, and it is a real tension.** The seal cannot simply be deleted:
`B56` pins `64` at a sealed corner, which is the pre-SS-2 behaviour and the whole basis of §5.2's
"reduces exactly to the old model" claim. So the fix must **keep the corner value and stop it
spreading** — for example by restricting the seal to the region where it is geometrically justified
(the point lying inside the wedge between the two occluders) rather than applying it wherever both
are merely within range. Do not resolve this by loosening `B56`.

**What was measured, and how the diagnostic was answered.** The packet's decisive diagnostic asks for
an in-game look with the seal disabled. It was answered *numerically* instead, which is strictly
sharper and did not need the game: the fixture is a concave corner built from two single-cell walls,
run in **four configurations** — both walls, either alone, neither — so that

```
excess(p) = sun(A only) + sun(B only) − sun(both) − sun(neither)
```

isolates exactly what the second wall adds *beyond two independent walls*. Nothing but the seal
produces it; the falloff profile, the radius, the gate-tripping slab and the light field all appear in
every configuration and cancel. (The light mean is a clean constant here — the wall cells are fully
opaque, so they hold no ambient light and drop out of the blend — which makes the excess exactly
proportional to occlusion.)

The field came back as `63.75 · min(u², v²)`, in light units over the face's parameter square:

| `v` ↓ / `u` → | `0.00` | `0.25` | `0.50` | `0.75` | `1.00` |
|---------------|-------:|-------:|-------:|-------:|-------:|
| **`1.00`**    | 0      | 4      | 16     | 35     | **63** |
| **`0.75`**    | 0      | 4      | 16     | 36     | 35     |
| **`0.50`**    | 0      | 4      | **16** | **16** | **16** |
| **`0.25`**    | 0      | 0      | 1      | 2      | 4      |

**Read the `v = 0.50` row.** The seal is *flat* from the corner out to half a cell along the
diagonal — a point standing in open floor is sealed exactly as hard as one wedged into the corner.
That is the wedge, and `min` has a second signature that explains its hard edge: it is
non-differentiable where its two arguments cross, i.e. precisely along the diagonal `u = v`, so the
field carries a crease radiating out of every concave corner.

**The fix.** `shadow[diag] = max(own, sideA · sideB)`. A product is the natural smooth conjunction of
"both sides hide the diagonal", where `min` is the hardest one; it is an identity at a cell corner
(every argument is `0` or `1`), so **`B56` is untouched**, and it decays with distance in both
tangent directions instead of one. Re-measured, the excess field is `63.75 · u²v²`: `63` in the
corner, `16` against a wall, `4` half a cell out along the diagonal, and no crease.

**A related deviation, deliberately not acted on.** With the seal correct, `SS-2`'s interior is still
*lighter* than the pre-`SS-2` bilinear ramp everywhere except the corner itself — on the diagonal,
`147` against `124` at `u = v = 0.75` — because a `(1 − t)²` falloff concentrates a shadow near its
occluder where the GPU's interpolation of corner values was linear. That is `D2` working as chosen,
not a defect, and §5.2 already names the exponent as the lever if interiors read too flat. **It is
also the one thing the in-game check should look at**: were the corners to still read wrong after this
fix, the exponent — not the seal — is the remaining suspect, and `D2` re-opens with the owner.

### The second defect, found only after the first fix went in game

The corner-seal fix above was correct and necessary, and the artifact **survived it**. The second
in-game report named the same corners, and the analysis that followed is the more important of the
two — because it invalidates a claim this document had been making since `SS-2`.

**Every block in the reported scene is a full cube** (`Snow`, `Grass`, `Dirt`, `Stone` are all
`RenderShape.Cube` / `FullBlock`, verified against `BlockDatabase.asset`). Nothing there is
subdivided, and at a cell corner every silhouette sits at distance exactly `0` or `1`. By §5.2's
reduction, ordinary terrain **cannot** have moved — which is what `B56` asserts and what this document
claimed. The contradiction was the finding: **the reduction holds only when the light field is
uniform**, and every AO scenario in the meshing suite fills light uniformly (`MH-3`'s documented
harness limit). Measured on plain full cubes at a sealed corner, varying only the hidden diagonal
cell's sky light:

| Sky light in the hidden diagonal cell | Engine (as `SS-2` shipped) | Pre-`SS-2` model |
|---------------------------------------|---------------------------:|-----------------:|
| `15` (as bright as the open air)      | `64`                       | `64`             |
| `9`                                   | `51`                       | `64`             |
| `3`                                   | `38`                       | `64`             |
| `0`                                   | `32`                       | `64`             |

The cause and the fix are in §5.2's second correction block: the light mean must be weighted by the
same visibility the occlusion term uses. **The lesson generalizes past this design** — when a model is
split into two factors that were previously one expression, the split is only sound where the two
factors partition the same set, and "opaque" versus "occluded" stopped being the same set the moment
the seal began occluding air.

- **Precondition:** none — `SS-2` is committed (`fd588e57`) and this fixes it in place.
- **Ordering:** **before** `SS-3` and `SS-4`. Both widen the same field; judging either on top of a
  known artifact would confound them.
- **Prove-red (executed 2026-08-09, each mutation restored clean):** new baseline **B57**, authored
  and observed red *before* the engine was touched. It reads the four-configuration excess at three
  points and asserts two things at once:

  | Mutation | Result |
  |----------|--------|
  | The shipped `min` combiner | **B57's locality leg red, alone** — a fall-off of `0` against the `6` it guards — with all 51 other meshing baselines green. This is the defect itself, on record. |
  | Seal deleted (`sealStrength = 0`) | **B56 red at `127` *and* B57's corner leg red.** The cheapest wrong fix is blocked by the suite, in both directions. |
  | Light mean weighted by the kernel alone (as `SS-2` shipped) | **B58 red, alone** — the sealed corner moves `64 → 32` as the hidden cell darkens — with **all 52 others green**, which is the measurement of how invisible this defect was to a uniform-light suite. B58's own positive control stayed green throughout. |

  The second mutation is the F15 control: the locality leg alone is satisfied by deleting the seal,
  the corner leg alone is satisfied by the defect, and only the pair states "keep the corner value,
  stop it spreading". Note that **B57's corner leg stayed green under the defect** — it had to, or it
  would not be an independent control.
- **Acceptance:** ✅ universal gate — **Validate All 437/437 across 18 suites**, both assemblies
  clean. ⏳ **in-game confirmation on the same enclosure still required**, and it carries the four
  `SS-2` acceptance readings that were never reached (§ the `SS-2` packet's acceptance list) plus one
  of its own: the concave corners of a walled enclosure show a corner shadow, not a diagonal wedge.
- **Serialization:** none.

### SS-3 — Extend the gate to full-cube occluders (🔴, behaviour change — observation 2) · ✅ **SHIPPED BEHIND A SETTING 2026-08-09 — DEFAULT OFF, AWAITING A CAPTURE**

- **Precondition:** ⚠️ `SS-2` + `SS-2a` confirmed in game. ✅ **D7 is decided (2026-08-09): build
  this phase now, with route B as the destination it later falls back for** — so this is permanent
  infrastructure, not a stopgap to be deleted when `VX-1` lands. §8's measured cost was on the table
  for that decision.
- **Scope:** the gate that `CalculateCornerLights` reports (`hasPartialOccluder`) widens from
  "opaque **with custom bounds**" to "any opaque occluder whose silhouette is within `R` of the
  face". Nothing else changes — the term, the metric and the profile are `SS-2`'s. Consider a
  distinct `SUB_CELL_TESSELLATION` for the full-cube case — §8's measurement says **expect to need
  it, with `N = 2` the likely value** (1.4×–1.7× vertices against `N = 4`'s 3.1×–4.7×). A named
  constant with its own docstring, not a magic number.
**What landed.** `MeshGenerationJob.PrepareFaceSampling` now reports an **`int tessellation`** —
1, `FULL_CUBE_SUB_CELL_TESSELLATION` (2) or `SUB_CELL_TESSELLATION` (4) — instead of a
`hasPartialOccluder` boolean, and `EmitTessellatedStandardCubeFace` takes the density as a parameter.
A face is admitted at density 2 when **any** of the nine hoisted cells casts a silhouette and the new
`FullCubeContactShadows` job flag is set; a partial occluder still wins at density 4; a face nothing
reaches stays a single quad. Behind a Graphics setting, **`Full-Block Contact Shadows`, default off**
(`SettingsManager.fullBlockContactShadows` → `WorldJobManager`), applied **live** — `World`'s
`HandleSettingChanged` re-requests a mesh rebuild for every active chunk when it moves, the same hook
`smoothLighting` uses, because the setting is read only inside the mesh job and here changes the
geometry rather than only the shading values. The harness gained the same opt-in
(`MeshingTestWorld.Run(..., fullCubeContactShadows:)`, default off), so **no pre-SS-3 baseline moved**.
New baseline **B54**. Validate All **438**.

**Measured cost — projection and reality agreed exactly.** §8's numbers were a projection from a face
census; running the real gate reproduces them to the digit:

| Geometry                         | Projected (§8) | Measured, flag on |
|----------------------------------|---------------:|------------------:|
| Flat ground                      | 1.00×          | **1.00×**         |
| Rolling terrain (gentle)         | 1.41×          | **1.41×**         |
| Rolling terrain (rough)          | 1.73×          | **1.73×**         |
| Built room                       | 1.48×          | **1.48×**         |

**Why `N = 2` and not 4.** A partial occluder's edge can sit anywhere inside its cell, which is the
resolution problem `VO-9b` was built for. A full cube's silhouette **is** its cell — there is no edge
position to resolve, only a falloff — so the extra density buys nothing but vertices, and the cost
goes as `N²`. `FULL_CUBE_SUB_CELL_TESSELLATION` is a named constant with that reasoning on it.

- **Ordering:** after `SS-2`, and **after** its in-game sign-off specifically — judging a
  whole-world change on top of an unjudged local one confounds both.
- **Prove-red (executed 2026-08-09, both mutations restored clean):**

  | Mutation | Result |
  |----------|--------|
  | Gate **never** on (the pre-`SS-3` engine) | **B54 red, alone** — "the floor face beside a full cube emitted 1 quad", so there is nowhere to read a metric. This is the packet's predicted free prove-red. |
  | Gate **always** on (ignoring both the flag and the occluder test) | **B11, B49's gate leg and B56 red** — precisely the standard-cube family this packet predicted would move, and all three for the same reason: each asserts the *undivided* path. Nothing else moved. |

  **B54's own shape matters more than its greenness.** Leg 1 asserts the face is subdivided at all —
  that is the leg the first mutation reds. Leg 2 is the suite's **first metric assertion**: it sweeps
  all eight floor cells around a lone cube and checks every sub-vertex against a closed form derived
  from §5.2, `occ = 0.25·(1 − d)²` in the Euclidean distance `d` to the silhouette — with an
  anti-vacuity guard requiring **off-axis** samples, since the diagonal samples are the only ones that
  tell a Euclidean metric from a separable one (finding S2). Leg 2 alone would pass on the pre-`SS-3`
  engine, where the only samples are corners at `d = 0` or `1` and every model agrees; leg 1 alone
  would pass on a subdivided face shaded by any rule at all.
- **Acceptance:** ✅ universal gate — **Validate All 438/438**, both assemblies clean, and **no
  existing baseline moved** (the flag is off by default on both the shipped and harness paths, so the
  standard-cube family is untouched rather than re-baselined). ✅ **Confirmed in game 2026-08-09**,
  and the owner called it a visual improvement.
- ⚠️ **The default stays OFF, and that is a settled decision — not an outstanding task.** The reason
  is **purely stylistic: with the setting on, the result reads too flat for the owner's taste.**
  Performance played **no part** in it — the IL2CPP capture was waived because cost is not the
  concern at this point, which is the same ground D7 was decided on. **Do not flip the default
  because the capture box is unticked, and do not reopen it with a performance argument** — it
  reopens on looks, or not at all (§11 question 7).
- **Flag retirement:** `fullBlockContactShadows` is a **quality setting, not a migration flag** — it
  is expected to stay as a user-facing toggle the way `Smooth Lighting` does, so it does **not** enter
  the flag-retirement backlog. What may retire is its *default*, once a capture justifies flipping it
  on.
- **Testability gain:** the suite gains a *metric* assertion (equal distance ⇒ equal shadow), which
  is orthogonal to every value assertion it has today.
- **Doc-sync:** `SMOOTH_AND_RGB_LIGHTING.md`; a `Documentation/Performance/` report for the capture.
- **Serialization:** none.

### SS-3a — Bin occlusion by direction, not by cell (🟡, behaviour change) · ✅ **FIXED 2026-08-09 — AWAITING IN-GAME CONFIRMATION**

**Symptom (owner, in game, 2026-08-09, with `SS-3` enabled).** A dark dash at every cell seam along
every wall — the shading changes as you walk along a flat wall, though the wall does not.

**Measured.** Floor row against a wall: `128` at the seams, `159` mid-cell — a **31-unit** scallop.
Half a cell out: `223` / `228`. One cell out: uniform `255`.

**The defect predates `SS-3`.** The same fixture built from *partial* occluders (a run of vertical
slabs, `SS-3` off) rippled `223 / 228` — 5 units, live since `SS-2`, unnoticed. `SS-3` did not
introduce the mechanism; it applied the model to every wall in the world and multiplied the amplitude
six-fold. **The cause and the fix are in §5.2's second correction block.**

**What landed.** `ShadePoint` now keeps two readings of the same nine silhouettes: `shadow[9]` per
**cell** (feeding the corner seal and the light-mean weights, both cell questions) and `quadrant[4]`
per **direction** (feeding the occlusion sum). `ClipToQuadrant` clips a silhouette to a quadrant and
rejects zero-area clips; `LightAttenuation.CellOcclusionShare` is renamed
**`QuadrantOcclusionShare`** to stop the old reading being re-derived from the name.

| Configuration                                | Before `SS-3a`      | After              |
|----------------------------------------------|---------------------|--------------------|
| Wall base, seam → centre                     | `128` → `159`       | **`128` → `128`**  |
| Half a cell out                              | `223` → `228`       | **`223` → `223`**  |
| Slab run (`SS-3` off), seam → centre         | `223` → `228`       | **`223` → `223`**  |
| Corner reduction (0/1/2/3 occluders)         | `255/191/64/64`     | `255/191/64/64`    |
| Lone cube, middle of its edge                | `191`               | **`128`** (intended) |

- **Precondition:** none — fixes `SS-2`'s model in place, under `SS-3`'s already-shipped gate.
- **Prove-red (executed 2026-08-09):** new baseline **B59** — walk the wall base and the row half a
  cell out and require both flat — red first at **31** and **5** units, with its own positive control
  green (the wall must actually cast, and its shadow must end within a cell, or "uniform" would mean
  "absent").
- **B54 was rewritten, not loosened, and the reason is the interesting part.** It asserted that
  shading is a function of distance *alone*, against `occ = 0.25·(1 − d)²`. The quadrant model
  falsifies that: beside a block's face the block fills two quadrants, beside its corner one — equal
  distance, unequal occlusion, and correctly so. The old assertion encoded **circular isocontours**,
  which was never what finding S2 claims. The assertion moved to the two properties S2 *is* about and
  which depend on the metric alone: the shadow **reaches equally far in every direction**, and it
  **never deepens with distance** within a direction. Values stay pinned by `B56`/`B57`/`B58`/`B59`.
- **Acceptance:** ✅ universal gate — Validate All **439/439**, both assemblies clean. ✅ confirmed in
  game 2026-08-09: *"that indeed fixed most of the artifacts"*.
- ⚠️ **Known residual, accepted by the owner for now: a step at a silhouette's edge.** One artifact
  survives, reported between two vertical slabs, and it is a limitation of this fix rather than a
  leftover of the old one. A quadrant is covered or not — a binary test — so coverage flips
  discontinuously where an occluder's edge crosses the sample point. Measured on a floor between two
  slabs one cell apart, reading along the row: `191 · 128 · 128 · 128 · 191 · 215 · 223 · 215 · 191 · 128`
  — `128` under the slab's footprint, stepping to `191` **exactly at its boundary**, a 63-unit jump.
  A slab's footprint edge coincides with a cell boundary, so it reads as a light line at a voxel
  border. The same step appears, smaller, at the end of a run of slabs (`223` → `239`). The fix is
  the angular-coverage refinement in §10, deliberately **not** built here.
- **Cost:** the occlusion term goes from 9 distance evaluations per sample to at most 9 × 4 clipped
  ones, pruned by the zero-area test (most cells touch one or two quadrants). Folded into `SS-3`'s
  outstanding capture rather than measured separately.
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
| v3      | ✅ **Per-pixel evaluation of this distance field, on `VX-1`'s volumes — THE DESTINATION** | **Interlock, not a phase — `VX-1`/`VX-8` own this ID space.** Owner-endorsed 2026-08-09 as where this design ends up: observation 2 at per-pixel quality, zero vertex cost, and the only route that retires `MR-8`'s *AO* merge constraint (analytic evaluation is per fragment, so a merged quad is fine — unlike a filtered baked channel, see the `VX-8` row below). Needs **both** of `VX-1`'s volumes, not just occupancy: post-`SS-2a` the occlusion enters the light weights, so a fragment needs per-cell light too (D7). Full cubes only until `VX-5` widens occupancy to carry bounds + rotation, so `SS-2`/`SS-4` stay CPU-side regardless. **Does not delete `SS-3`** — the volume is finite, so the far field keeps vertex-baked AO. Supersedes v1.0's "per-face AO texture" row: a resident volume needs no atlas, no UV allocation, and no change to `MR-2`'s packed vertex format, so the per-face variant is strictly worse and is dropped rather than deferred. |
| —       | **`VX-8` (per-fragment light) does not subsume this design** | Recorded so it is not mistaken for a replacement. `VX-8` moves *where light is stored*; this design fixes *what the occlusion value is*. Hardware trilinear filtering of a voxel-resolution volume **is** the separable product S2 blames for the round blob, and one texel per cell cannot say where inside a cell a slab sits — so moving AO into the volume would bake both observations in permanently. `VX-8`'s own "vertex AO stays vertex-baked" line is correct, and this is the reason. |
| v2      | **Weight a quadrant by the angular fraction an occluder covers** | The named fix for two things at once, and the highest-value item on this list. `SS-3a` bins occlusion by direction but decides coverage with a **binary** in-quadrant test, which **steps discontinuously** where a silhouette's edge crosses the sample point — the known residual in the `SS-3a` packet. Weighting each quadrant by the angular fraction its nearest occluder actually subtends makes coverage continuous across a silhouette edge, and lands an isolated block's edge between today's `191` and `128` instead of at the extreme. ⚠️ **It is not the answer to §11 question 7's "too flat"** — a smoother field is if anything a flatter one; do not build this expecting it to change the default-off decision. It moves every AO value in the world, so it needs its own phase, its own prove-red and its own sign-off. |
| —       | **Adaptive `SUB_CELL_TESSELLATION`**                        | Density chosen per face from the occluder's distance, rather than one constant. Only worth it if `SS-3`'s measurement says the constant is the problem. |

---

## 11. Open questions

1. ~~**D7 — the full-cube gate — is the one decision still open.**~~ — **RESOLVED 2026-08-09**
   (§4 D7): build `SS-3`, with per-pixel on `VX-1` as the destination. Shipped, confirmed in game,
   and left **default-off on taste** — see the `SS-3` packet.
2. **Do face interiors read too flat? — YES, and it is why `SS-3` ships default-off** (owner,
   2026-08-09). This question was asked before any of it was built and it turned out to be the right
   one. Note what it is *not*: the "too dark" readings reported along the way were **bugs**, not a
   taste verdict — `SS-2a`'s light double-count and `SS-3a`'s per-cell banding — and both are fixed.
   Do not carry "the owner thinks it is too dark" forward; the standing verdict is **too flat**.
   Levers in §11 question 7.
3. **Do `B42` and `B46` survive `SS-2`?** They pin corner values under partial occluders, where the
   occlusion function changes from an octant fill fraction to a distance falloff. §6.4 predicts they
   do, because a slab's silhouette either contains a corner or lies half a cell from it. `SS-2`
   measures it; a surprise here means §5.2's reduction is narrower than claimed.
4. ~~**What does `SS-3` actually cost on real terrain?**~~ — **VERTEX COST MEASURED** (§8:
   1.00× flat, 1.41×–1.73× terrain, 1.48× built); the **frame-time capture was waived** because
   **cost is not the owner's concern at this point** — not because the vertex numbers settled it.
   Caves remain unmeasured. Re-open only if the phase is ever proposed for default-on *and* someone
   has a performance reason to care.
5. ~~**Should `GetFaceCoverage` be re-expressed as the area of `GetFaceSilhouette`?**~~ —
   **RESOLVED by `SS-1`: no, guarded instead.** It feeds light transport, where a last-ulp change
   could flip `FaceBlocksLight`'s threshold, and the drift the consolidation would have prevented is
   prevented just as well by B6's bitwise area assertion.
7. **`SS-3` reads too flat — which lever?** (owner verdict, 2026-08-09; the reason the setting is
   opt-in.) Unresolved, and deliberately not guessed at here. The candidate levers are the falloff
   **exponent** (D2's `(1 − t)²` — a steeper profile concentrates the shadow and raises contrast),
   the **radius** `R` (shorter = tighter, punchier), and `QUADRANT_OCCLUSION_SHARE` (deeper overall).
   ⚠️ **§10's angular-coverage refinement is not this lever** — it makes the field *smoother*, so if
   anything it flattens further. It is filed for the silhouette-edge step, and the two must not be
   conflated. Any change here moves shading world-wide and needs its own phase and sign-off.
6. ~~**`AmbientOcclusionOctantCoverage` and `AmbientOcclusionRegionCoverage` are now TEST-ONLY.**~~ —
   **RESOLVED 2026-08-09: deleted**, together with their `GetOctantCoverage` / `GetRegionCoverage`
   backings in `BurstOcclusionUtility`. `GetFaceCoverage` is untouched and still feeds light transport.
   The two baselines were handled differently, and the split is the transferable part:
   - **`B41` retargeted, not deleted.** Its claim — a block without custom bounds occludes all of its
     cell or none of it, on every face and orientation — is still a live guarantee; only the function
     expressing it changed, so it now sweeps `AmbientOcclusionPlaneSilhouette`.
   - **`B50`'s coverage leg deleted.** Its subject was the removed function's *own* behaviour (finding
     **S9**), and that finding is recorded here in prose. Re-proving a property of deleted code every
     run guards nothing. Leg 1 (F13 bounds-match-geometry) is untouched.
   - **Rule:** when a baseline's *subject* disappears, ask whether the **claim** survives on the new
     code path — retarget if it does, delete if the claim was about the removed mechanism itself, and
     check the finding it produced lives in a document either way.

   ⚠️ **A claim made and then measured false during this cleanup, recorded so it is not repeated:**
   `B41` was briefly documented as the *only* guard on the opacity gate. Neutering that gate reds
   `B41` **and ten other baselines**, because every air cell then shadows. `B41`'s real distinct value
   is that it reads the primitive directly and sweeps the **whole palette**, where the shading
   baselines only ever place three block types.

---

## Document History

* **v2.3** - Open questions closed out: D7 (1) and the `SS-3` cost (4) marked resolved, and a new question 6 files `AmbientOcclusionOctantCoverage`/`AmbientOcclusionRegionCoverage` as **test-only** — `SS-2` removed their production consumer and, contrary to what question 5 promised, never recorded it
* **v2.5** - **Dead coverage code deleted** (§11 question 6 resolved). `AmbientOcclusionOctantCoverage`, `AmbientOcclusionRegionCoverage` and their `GetOctantCoverage`/`GetRegionCoverage` backings are gone — `SS-2` removed their production consumer and only two baselines kept them alive. **`B41` was retargeted onto `AmbientOcclusionPlaneSilhouette`** (its claim survives the model change; only the function expressing it moved) and **`B50`'s coverage leg was deleted** (its subject was the removed function's own behaviour — finding S9 — which is recorded in prose). `GetFaceCoverage` untouched: light transport still uses it. Validate All **439/439**, suite count unchanged. Also recorded: a claim that `B41` was the *only* guard on the opacity gate, made and then measured false in the same pass — neutering the gate reds eleven baselines, and B41's real value is reading the primitive directly across the whole palette
* **v2.4** - **Corrected the record on why `SS-3` is default-off, and closed §11.** The standing verdict is **too flat**, not too dark — the dark readings reported during the arc were the `SS-2a` light double-count and the `SS-3a` per-cell banding, both fixed, and carrying them forward as a taste verdict would have sent the next session after the wrong lever. **Performance played no part** in the decision or in waiving the capture (owner: cost is not the concern at this point), so the phase does not reopen on a performance argument. New §11 question 7 lists the honest levers for flatness (falloff exponent, radius, share) and warns that §10's angular-coverage row is **not** one of them — a smoother field is a flatter one. Also filed: `AmbientOcclusionOctantCoverage`, `AmbientOcclusionRegionCoverage` and their `GetOctantCoverage`/`GetRegionCoverage` backings are **test-only** since SS-2 removed their consumer, kept alive by B41 and B50's coverage legs
* **v2.2** - **`SS-3` and `SS-3a` both confirmed in game; `SS-3` stays default-OFF by owner decision.** The capture is **waived** because cost is not the owner's concern at this point, and the setting stays opt-in on **taste**: with it on the result reads **too flat**. (Corrected in v2.4 — v2.2 first recorded this as "too dark", which was a misreading: the dark readings were `SS-2a`/`SS-3a` bugs, since fixed.) Recorded explicitly so nobody flips the default on the grounds that the capture box is unticked. **One residual accepted:** a **step at a silhouette's edge**, reported between two vertical slabs and measured at 63 units (`128` under a slab's footprint → `191` exactly at its boundary), a limitation of `SS-3a`'s binary in-quadrant test rather than a leftover of the per-cell defect. Its fix is a new §10 v2 row — **weight a quadrant by the angular fraction its occluder subtends**. (v2.4 corrects the rest of this sentence: that row addresses the *step*, and is explicitly **not** the answer to the flatness behind the default-off decision.)
* **v2.1** - **`SS-3a`: occlusion is summed over the four QUADRANTS around a point, not over the nine cells.** In game, `SS-3` showed a dark dash at every cell seam along every wall; measured, the wall base read `128` at seams against `159` mid-cell. The per-cell sum reads the **grid**, not the geometry — a straight wall arrives as three separate cell silhouettes, and how many of them touch the sample point depends on where the seams fall. At a cell corner cells and quadrants coincide, which is why the per-cell form reproduced every corner value and shipped. **The seams were the correct value**: pre-SS-3 that edge had only its two `128` corners with the GPU interpolating between them, so it was the *interior* samples that disagreed with the corners. **The defect predates SS-3** — the same fixture in slabs rippled 5 units, live since SS-2. Fix: `quadrant[4]` alongside `shadow[9]`, a quadrant darkened by the nearest silhouette *covering area* in it (a silhouette merely touching a quadrant boundary covers none of it), with the corner seal staying per-cell and applied to both readings so `B58`'s identity survives. `CellOcclusionShare` → **`QuadrantOcclusionShare`**. Walls are now flat at `128 / 223 / 255`, and the SS-2 slab path is flat too. **Accepted look change:** a lone block's contact shadow deepens at the middle of its edge, `191 → 128` (its corners stay `191`) — a block touching you along a whole edge fills two quadrants, not one quarter. New baseline **B59** (wall uniformity), red first at 31 units. **`B54` rewritten, not loosened**: its "equal distance ⇒ equal shadow" premise is *false* under the correct model and encoded circular isocontours, which is not what S2 claims — it now asserts reach and ordering, both metric-only. Validate All **439**
* **v2.0** - **`SS-3` shipped behind a default-off Graphics setting (`Full-Block Contact Shadows`).** The gate now reports an `int tessellation` rather than a boolean, admitting a face at **density 2** when any of the nine hoisted cells casts a silhouette — `FULL_CUBE_SUB_CELL_TESSELLATION`, half of the partial-occluder density, because a full cube's silhouette *is* its cell so there is no sub-cell edge to resolve and the cost goes as `N²`. **§8's projected cost proved exact when measured against the real gate** (1.00× / 1.41× / 1.73× / 1.48×). New baseline **B54**, the suite's **first metric assertion**: every sub-vertex around a lone cube is checked against the closed form `occ = 0.25·(1 − d)²` derived from §5.2, with an anti-vacuity guard demanding off-axis samples, since only the diagonals separate a Euclidean metric from a separable one (S2). Prove-red both ways: gate-never-on reds B54 alone (the pre-SS-3 engine, the packet's predicted free prove-red); gate-always-on reds **B11, B49's gate leg and B56** — exactly the standard-cube family predicted, all three asserting the undivided path. **No existing baseline moved**, because the flag defaults off on both the shipped and harness paths. Validate All **438**. Still owed before the default flips: an IL2CPP `perf-benchmark` capture and in-game sign-off
* **v1.9** - **D7 decided (owner): build `SS-3` now, per-pixel on `VX-1` is the destination**, after a research pass over four routes. `SS-3` is **not** throwaway under that plan — route B's volume is finite, so the far field keeps vertex-baked AO and `SS-3` becomes its fallback. Two corrections to D7's own assumptions came out of the research: (1) **route B needs `VX-1`'s light volume too, not just occupancy** — `SS-2a` moved occlusion into the light weights, so a per-pixel occlusion factor over an interpolated vertex light no longer reproduces the model (≈ 9 occupancy + 4 light taps per fragment); (2) **route B has an AO horizon** at the volume radius (`VX-1`'s default ≈ 5 chunks, against view distances of 10 and 20 in `FP-4`'s sweep) and AO does not degrade gracefully the way fog does — the owner's steer that the volume be **view-distance aware** is filed against `VX-1`, along with the quadratic memory that implies and the `MR-8` vertex saving that could offset it. **§8's "genuinely open magnitude" is now measured**: `SS-3` admits 0 % of faces on flat ground, 13.8–24.3 % on rolling terrain and 16.0 % in a built room → **3.1×–4.7× vertices at `N = 4`, 1.4×–1.7× at `N = 2`**, making `N = 2` the expected answer. Also recorded so they are not re-derived: **route C** (an 8-bit neighbour-occupancy mask in the spare `Normal.w`, evaluated per pixel — zero cost and no `VX-1` dependency, but only a separable approximation, so `B58` would red it, and incompatible with `MR-8`) and **route D** (URP's screen-space AO — rejected). And a cost nothing else carries: moving AO off the mesh makes the meshing suite **blind** to it, with no golden-image harness to replace `B41`–`B58`
* **v1.8** - **SS-2a, second defect: the light mean must be weighted by visibility, not by a per-block "holds light" flag.** The product-seal fix was correct and the artifact survived it in game. Every block in the reported scene is a **full cube**, so §5.2's reduction says ordinary terrain cannot have moved — and that contradiction is the finding: **the reduction holds only under a uniform light field**, which is what every AO scenario in the suite fills (`MH-3`). Measured at a sealed corner on plain full cubes, the engine read `64 / 51 / 38 / 32` as the hidden diagonal cell's sky went `15 / 9 / 3 / 0`, where the pre-SS-2 model reads `64` throughout. Cause: `SS-2` split one expression into "light mean × `(1 − occ)`" and took the mean over cells that *hold* light — identical to the occluded set while occluders are opaque, and wrong the moment the **seal occludes air**, which is credited and debited at once. Weighting the mean by `wᵢ·openᵢ` makes the two factors cancel (`Σw·open = 1 − occ` at a corner), so the model now collapses to the pre-SS-2 expression **for an arbitrary light field** — a strictly stronger reduction than §5.2 originally claimed. Needs one guard: fall back to the unshadowed mean where the kernel sees nothing, which is the black-face case SS-2 mis-diagnosed as "light must not be weighted by the per-point shadow" (true unrenormalized, false renormalized). New baseline **B58**, red first at `64 → 32` with **all 52 others green** — the measure of how invisible this was. Validate All **437**
* **v1.7** - **SS-2a fixed: the corner seal's combiner is a product, not a `min`.** The suspicion filed in v1.6 was confirmed, and answered by measurement rather than by eye: a four-configuration differential (both walls / either alone / neither) isolates the seal from the falloff, the radius and the light field, and showed its contribution running **flat at 16 light units from the corner out to half a cell along the diagonal** — the wedge — with a crease along `u = v` where `min`'s two arguments cross. The product decays it to `4` while holding `63` in the corner, is an identity at a cell corner so **B56 is untouched**, and is the natural smooth conjunction of "both sides hide the diagonal". New baseline **B57**, authored red first, pins both ends: **the shipped `min` reds its locality leg alone; deleting the seal reds its corner leg *and* B56**, so the cheapest wrong fix is blocked in both directions (F15). Validate All **436**. Recorded but not acted on: with the seal correct, SS-2's interior is still *lighter* than the pre-SS-2 bilinear ramp (147 vs 124 mid-diagonal) because `(1 − t)²` concentrates a shadow near its occluder — that is D2 working as chosen, and the exponent is the remaining suspect if the in-game check still reads wrong
* **v1.6** - **SS-2 rejected in game: a corner-darkening artifact**, dark wedges spreading diagonally out of concave corners across open floor. Filed as **SS-2a**, which now blocks SS-2's sign-off and both later phases. Leading suspicion is `ApplyCornerSeal`: §5.2's corner seal is right at a corner (B56's 2/3-occluder rows depend on it) but SS-2 implemented it as a *continuous* `max(own, min(sideA, sideB))` over distance-attenuated shadows, so at `R = 1.0` it fires across a cell-wide band around every concave corner and attributes occlusion to air that is plainly visible. Decisive diagnostic recorded (disable the seal and look; B56 going red at 127 is the expected confirming red). **All 435 baselines were green throughout** — every scenario reads face corners or face interiors, and this artifact lives in the field between them, so SS-2a must author a probe that can see it before fixing
* **v1.5** - **SS-2 code complete, awaiting in-game confirmation.** Coverage is gone from the AO path: one `ShadePoint` function serves corners and sub-vertices, fed by a 3x3 hoist built once per face; `DirectOpenFractionAt`, `SampleNeighborLight` and `Weigh` deleted. **The corner reduction is exact** (`255/191/64/64`) and the **post now shades to 191 where it managed 251** — a shape that cast essentially nothing, fixed with no per-shape code. **Three defects were found and fixed during execution, none of which the suite could see beforehand:** (1) the face centre under a slab rendered `0`, because the light mean was weighted by the per-point shadow and the kernel collapses onto a single occluding cell there — fixed by weighting on a per-cell "holds usable light" flag, which is also the cleaner separation D3 wants; (2) **inside corners lightened 64 -> 127**, because this document's §5.2 table had the two-occluder row wrong (`128`) and missed classic AO's corner seal — restored as `max(own, min(sideA, sideB))`, the smooth form of the original boolean rule; (3) **Bug M03 re-introduced** — the interior-face touch test let a half slab shadow its own mid-plane face, rendering a recessed slab fully black, caught by B47. New baseline **B56** pins the reduction and is the only guard that sees a `max` combiner flatten every inside corner; **B46** and **B49** had their *assertions* rewritten rather than loosened (B46 now counts corners at the strongest darkening; B49's leg 3b is a with/without-walls differential). Prove-red: sum->max reds B56's 2/3 rows alone; R=0.5 reds B49 with the centre at 255 both ways — the F18 signature via the radius. Validate All **434**
* **v1.4** - **SS-1 executed** (2026-08-09, no behaviour change). `BurstOcclusionUtility.GetFaceSilhouette` + `LightAttenuation.AmbientOcclusionFaceSilhouette`, gated exactly like their siblings; **nothing consumes them yet**. Baseline **Occlusion B6** (5 -> 6) — corrected from the plan's *meshing* B50, which SS-0 consumed and which was the wrong suite anyway: a pure shape-primitive test belongs in the Occlusion suite VO-1 built for this layer. **§11 question 4 resolved: `GetFaceCoverage` is NOT re-expressed through the new primitive** — it feeds light transport, where a last-ulp difference could flip `FaceBlocksLight`'s `>= 1 - 1e-4` threshold, and the drift the consolidation would prevent is prevented just as well by B6 asserting the silhouette's area equals it **bitwise** across every fixture/face/orientation. Guarded, not merged (the B5 pattern). Prove-red by transposing the shared rotation core reproduced the **F10 signature exactly**: B2 and B6 red, B1/B3/B4/B5 green, meshing B46 red downstream — and **the post rows stayed green**, so B6's discrimination rests entirely on its roll leg. Recorded as a do-not-drop
* **v1.3** - **SS-0 executed** (suite-only, 2026-08-09). Fixture shape and authored volume are now **one `BlockCollisionBounds` value used twice**, so F13's divergence is unrepresentable; new `Post` fixture, positional `TopFaceSubVertexField` probe, baseline **B50**; meshing suite 49 -> 50 baselines green, both prove-red mutations reverted (the second reds the monotonicity leg *alone*, proving it discriminates independently). **New finding S9 corrects this document's own S1/**§**6.1 claim**: the post is not "more non-linear" than the slab — measured, the slab departs from an endpoint-linear fit by `0.083` and the post by only `0.038`. The post's distinguishing property is that its sweep is **non-monotonic**, because `GetRegionCoverage` normalizes by the query region's own volume and a region clipped at a cell edge shrinks, inflating the fraction into a rise where distance says fall. Coverage is not a mildly-wrong distance field; it is not one at all. Pre-SS record: a post standing on a face darkens it by **~3%** (255 -> 251/247) against a slab's 25%, and the slab row reproduces F18's published profile exactly — cross-checking the new fixture and probe against the measurement the design rests on. The post already trips VO-9b's gate (16 quads), so **SS-2 needs no gate change to reach it**
* **v1.2** - **Interlocked with `VX-1`/`VX-8` (the resident light volume) and `MR-8` (greedy meshing) after the owner raised the light-texture route.** No new IDs: the "per-chunk 3D light texture" idea is already tracked as **VX-1** + **VX-8**, and VX-8 already names itself MR-8's escape hatch. Recorded finding: **hardware trilinear filtering of a voxel-resolution volume IS the separable product S2 blames for the round blob**, and one texel per cell cannot locate a slab within its cell — so a light volume reproduces *both* observations rather than fixing either. That is the technical reason VX-8's "vertex AO stays vertex-baked" line is correct, and it makes the two changes orthogonal (VX-8 moves *where* shading lives; this design fixes *what the value is*). **D7 gains a third answer**: defer observation 2 to a per-pixel evaluation of this design's distance field on VX-1's occupancy volume — zero vertex cost, would retire `SS-3`, full cubes only until `VX-5` carries bounds. A six-volume baked alternative is ruled out on face-dependence (≈157 MB at 2x against VX-1's 3.3 MB). §10's v1.0 "per-face AO texture" row is dropped as strictly worse than the resident volume. This design is **merge-neutral** for MR-8 and its tessellation gate partitions the face set against MR-8's mergeable set
* **v1.1** - **D1/D2/D3 decided by the owner — Euclidean distance, a `(1 − t)²` falloff, and the silhouette field *replacing* the coverage fraction — and D3's specification corrected in the process.** The Option C written in v1.0 (a plain light average times one global `(1 − s·SS)` factor) does not work: a bounded occlusion field with a single strength cannot reproduce both `191` for one occluder and `64` for a 1×1 pit, so it would have flattened every deep AO configuration. The correct form gives each of the four cells meeting at a shaded point a fixed quarter share of the occlusion budget and multiplies a renormalized light blend by `(1 − occ)`; **at a cell corner with binary occlusion that is algebraically identical to today** (`255/191/128/64` verified against all four cases), which dissolves both objections v1.0 filed against Option C and keeps `SS-2` at 🟡 with `B11` and the standard-cube family green. A second correction followed from the same arithmetic: **`R = 0.5` is wrong and the radius is `1.0`** — at `0.5` a wall's occlusion dies before mid-face and an inner corner's centre computes `255`, the F18 interior-lightening signature reached by a different route. D5 is rewritten accordingly: this design **does** re-sample the ring per sub-vertex, and it is safe because occlusion is decoupled from the box-overlap weights, which is the one-line diagnosis of F18 itself. §6.3's B49 rewrite changes the *assertion* rather than the tolerance (the corner field is legitimately no longer the expectation), and new baseline **B56** pins the corner reduction as the guard the whole replacement rests on. D1's rounding concern answered with isocontour reach (today's blob bulges outward at diagonals; a Euclidean SDF cuts a fillet inward, and `(1 − t)²` confines it to the invisible tail); a p-norm escape hatch recorded but not built. Only D7 (the full-cube gate) remains open
* **v1.0** - Initial design. Establishes that the `VO-*` arc's coverage model cannot deliver either of the owner's two observations and that both have the same cause: **S1** — a fill fraction is linear across the cell for any occluder bounded by one plane, so sub-cell sampling of it is inert (generalizes `VO-*` F18 beyond slabs); **S2** — the four-cell average weights an occluder by a *product* of two per-axis ramps, giving hyperbolic isocontours, and the derivation reproduces F17's measured `12 of 32` darkened corners exactly. Chosen: derive the occluder's **silhouette rectangle** from the same rotated AABB `GetFaceCoverage` already projects (D4) — coverage is that rectangle's area, so the AABB-vs-AABB primitive and its shape-agnostic property survive intact. The new term **does touch the ring** and D5 states precisely why that is not the `VO-9b` defect (a new bounded attenuation versus a redistributed conserved blend). `VO-9a`'s "corner values do not move" is replaced by the **position-purity** invariant (D6), which implies the same seam guarantee while letting corners darken. Falloff radius is pinned to `0.5` cells by the existing gate's 3×3 reach (**S4**), and `s = 0.25` reproduces today's peak darkening exactly, making the change shape-only. Five phases: SS-0 fixtures (a post — the harness has **no** non-linear-coverage shape today, **S8**), SS-1 primitive, SS-2 the term for partial occluders, SS-3 the full-cube gate (the only phase with a real vertex cost and the only one requiring measurement), SS-4 custom-mesh faces. **B49 leg 3b will go red under SS-2 and §6.3 specifies a rewrite rather than a loosened tolerance**, with a new positive control that is satisfied by tessellation rather than by the shadow (F15). Metric, falloff, add-vs-replace and the full-cube gate are left open as owner decisions

---

**Last Updated:** 2026-08-09  
**Next Review:** when `SS-4` (custom-mesh faces) or §10's angular-coverage refinement is scheduled. `SS-0`–`SS-3a` are shipped and confirmed; the `SS-3` default is settled at OFF by owner decision and its capture is waived; D1/D2/D3/D7 are all settled
