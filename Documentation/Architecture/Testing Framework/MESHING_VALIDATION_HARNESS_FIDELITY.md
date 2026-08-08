# Meshing Validation Harness — Fidelity Boundary & Extension Backlog

**Status:** ✅ **Active backlog** — Wave 1 executed 2026-06-17 (MH-1/MH-4/MH-9 closed), Wave 2 executed 2026-06-18 (MH-5/MH-3 closed), Wave 3 executed 2026-06-18 (MH-6 closed — buildable-now portion), Wave 5 executed 2026-06-21 (MH-10/MH-11 cross-chunk border culling closed); see §6. **Optimizations landed (guarded by this suite):** MR-1, MR-7 (2026-06-15); **MR-3 + MR-4 + MR-5**
(2026-06-18, Wave 1 of the MR-* implementation phase) — MR-3/MR-4 added the build-alongside postconditions **B15** (no-reassign-when-bitmask-unchanged) and **B16** (constant-cell-bounds); MR-6 added **B17** (pooled-output stale guard); the cross-chunk substrate prerequisite added **B18–B21**. Since then FL-1/FL-2 added **B22/B23**, the MP-* orchestration arc added **B24–B27**, MP-5 added **B28–B30**, MP-6 added **B31–B33**, the chunk load-animation toggle regression added **B34–B36**, MP-7 added **B37–B39** (neighbor-map permutation guards, MH-12 —
cardinals via face culling, diagonals via fluid corner geometry, and the shared acquire-site offset table), MH-13 added **B40** (the same guard for the eight neighbor **light** maps), **VO-5** added **B41–B43** (fractional ambient occlusion for partial blocks), **VO-6** added **B44–B45** (sub-block face light sampling, promoted from the `KM01a`/`KM01b` repros of the now-archived Bug M01), **VO-8** added **B46** (per-corner octant occlusion), and the Bug M03 follow-up added **B47** (a recessed partial block must not occlude its own mid-plane face) — **tip is B47 (47 baselines); see §4 for the arc detail.** The suite currently has **no open known-bug scenarios**: `MESHING_BUGS.md` Bugs M02 and M04 are filed without repros.  
**Created:** 2026-06-16 · **Last updated:** 2026-07-26 **Scope:**
`Assets/Editor/Validation/Meshing/` — the `MeshingValidationSuite` + `MeshingTestWorld` +
`MeshOracle` + `MeshAssert` + `TestMeshBlockPalette` harness (menu item **`Minecraft Clone/Dev/Validate Meshing`**). **Sibling:** [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](LIGHTING_VALIDATION_HARNESS_FIDELITY.md) — same document shape; the meshing suite was built test-first as that suite's younger sibling.

---

## 1. Why this document exists

The meshing validation suite (baselines **B1–B11**, all green) runs **real production code**: it executes the actual `Jobs.MeshGenerationJob` synchronously (`job.Run()`) over a synthetic single chunk and asserts its `MeshDataJobOutput` — and, since Wave 2 (MH-5), optionally chains the real `Jobs.MeshPostProcessJob`. It is the regression guard that lets the `MR-*` performance findings in
[PERFORMANCE_IMPROVEMENTS_REPORT.md](../../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) claim
"output-preserving" — it already closed **MR-1** (per-vertex `Quaternion.Euler` hoist, guarded by B1/B4)
and **MR-7** (per-fluid-voxel `Allocator.Temp` arrays, guarded by B7/B8).

It is **blind** wherever it (a) checks a stream only for *determinism* but never against an *expected value*, (b) *omits a pipeline stage*, or (c) lacks a *block shape* in its palette. A green suite does **not** prove correctness in those areas. The `PERFORMANCE_IMPROVEMENTS_REPORT.md` §Verification note already concedes the headline gap:

> *"Fluid/custom-mesh/cross-mesh and UV/light values are not yet oracle-covered — extend the suite before
> optimizing those paths."*

This note enumerates those blind spots **as a prioritized, phased backlog**, because most of the still-open
`MR-*` items cannot be baselined until a specific harness capability is built first. It is the meshing analog of the lighting fidelity doc, written at the point where the open optimizations (not the suite itself) define what to build next.

### How to read the status tags

| Tag                      | Meaning                                                                                    |
|--------------------------|--------------------------------------------------------------------------------------------|
| **OPEN**                 | Gap exists; an optimization in this area cannot yet be baselined (or passes blind).        |
| **IN-PR**                | Trivial enough to build in the same PR as the optimization it guards — not a prerequisite. |
| **CLOSED**               | Addressed; harness now exercises / asserts this area.                                      |
| **WONTFIX (structural)** | Out of scope for a synchronous editor meshing harness by design.                           |

---

## 2. What the harness exercises today (the trusted core)

So the blind spots below are read against a clear baseline of what *is* covered:

- **Real meshing job.** `MeshingTestWorld.Run()` builds the real `MeshGenerationJob` with production-faithful inputs (real water height templates via `FluidMeshData.BuildVertexHeightTemplate`, default sections forcing the per-voxel standard path) and runs it synchronously.
- **Standard-cube geometry oracle.** `MeshOracle.ExpectedStandardCubeFace` independently derives the 4 vertex positions + normal of every cube face × {0,90,180,270}° via `Quaternion.Euler` ground truth (B1 isolated, B4 end-to-end through the job).
- **Structural + determinism invariants.** `MeshAssert.StructuralInvariants` (stream lengths consistent, triangle indices in range, multiple-of-3) and `OutputsEqual` (full byte-for-byte stream equality across two runs — vertices, all three triangle lists, normals, UVs, colors, packed light).
- **Submesh routing.** Opaque (B2), transparent (B6), fluid (B7) faces land in the correct triangle list.
- **Occlusion.** Fully enclosed cube emits nothing (B3); derived face-count assertion with palette-assumption guard.
- **Fluid neighbor-buffer isolation.** B8 — the differential guard that closed MR-7 (shore-mask + full per-vertex quad equality between an isolated and a primer-preceded probe).
- **UV value oracle (Wave 1, MH-4).** `MeshOracle.ExpectedFaceUVs` + `MeshAssert.UVsMatch` pin every standard-cube face's 4 UVs to its texture's independently-derived atlas cell (B2 all 6 faces, B4 all 4 yaws).
- **`SectionStats` tiling (Wave 1, MH-9).** `StructuralInvariants` asserts the per-section ranges tile each stream contiguously; B9 (one cube per section) exercises it across 3 emitting sections.
- **Bounds extent (Wave 1, MH-1).** `MeshAssert.BoundsWithin` asserts every vertex lies inside its section cell (B2/B4) — the premise behind MR-4's constant bounds.
- **Post-process / section-space output (Wave 2, MH-5).** `MeshingTestWorld.Run(PostProcessMode.Separate|Chained)`
  chains the real `MeshPostProcessJob`; B10 asserts the chunk-space → section-space coordinate rewrite (`MeshAssert.SectionSpaceVertices`), that `InterleavedStream3` is the interleave of `Normals`+`LightData`
  (`MeshAssert.InterleavedMatches`), and chained-vs-separate byte equality (the MR-5 guard).
- **Smooth-lighting *values* (Wave 2, MH-3).** `MeshingTestWorld.FillLight` + `Run(SmoothLightingQuality.High)`
  populate a uniform light field; `MeshOracle.ExpectedUniformCornerLight` (hand-derived `17·V`, LUT-independent)
    + `MeshAssert.LightDataMatches` pin the smooth-light encoding (B11: full sun 255, intermediate 119/51).
- **Renderer apply-path (Wave 3, MH-6).** A *separate* fixture (`Framework/SectionRendererTestFixture`) drives the real `SectionRenderer.UpdateMeshNative` in edit mode (reflection-stub `World.Instance` + 3 distinct stub materials) and observes through the public `GameObject`; **B12** pins material-combination selection per submesh-presence bitmask (all 7 combos, opaque→transparent→fluid order), **B13** the empty-section deactivate + no-assign short-circuit, **B14** that `Mesh.bounds` *contain* every emitted vertex (`RendererAssert`). This is a different
  harness from the job suite — see §3 MH-6.

> **The trusted core is now whole for the job + post-process stages.** `InterleavedStream3` (the Normals+light
> GPU-upload vertex stream) *was* empty here because it is built by `MeshPostProcessJob`; it is now produced
> and asserted via the MH-5 opt-in path (→ **MH-5**, CLOSED 2026-06-18). `MeshDataJobOutput.SectionStats`
> (per-section vertex/triangle index ranges) is tile-checked by `StructuralInvariants` (→ **MH-9**, CLOSED
> 2026-06-17). Smooth-light values are oracle-covered for the *uniform* case (→ **MH-3**, CLOSED 2026-06-18);
> distinct-per-corner light values remain a future extension (see §3 MH-3). **AO is now partly covered**:
> VO-5's **B41–B43** assert occlusion *ordering* between orientations over a uniform light field, which
> needs no model of the corner LUT (the A4 trap) but also does not pin absolute corner values — that is
> still the MH-3 extension.
>
> ⚠️ **Fixture authoring is a fidelity surface of its own** (VO-5, finding **F13**). `TestMeshBlockPalette`'s
> half slab carried no `collisionBounds` for its entire existence: a slab in geometry, a full cube in shape.
> Nothing caught it because no meshing code asked a shape question until VO-5, and the sibling *lighting*
> palette had always authored it. A palette field that production authors and the fixture omits is invisible
> until some phase reads it — and then it silently produces plausible, wrong numbers rather than an error.
> `TestCustomMeshLibrary` carries the same exposure for VO-6's face centroid.

---

## 3. Blind spots & the phased extension backlog

Gap IDs are `MH-#`, matching the analysis numbering that produced this note. Each entry states what is blind, which `MR-*` item it gates, what to build, and effort. The phase ordering is value-for-prerequisite: a phase's items unblock the optimization wave that depends on them.

### Phase 0 — In-PR quick wins (no new system; not prerequisites)

These are small enough to land in the same PR as the optimization they guard. Listed so they aren't mistaken for blockers.

#### MH-1 — No bounds-extent assertion · **CLOSED** (2026-06-17) · gates **MR-4**

- **Blind:** the suite never checks the spatial extent of the emitted geometry. `MR-4` replaces the per-section `RecalculateBounds()` with a constant `Bounds`; its correctness criterion is "every emitted vertex lies within the section cell," which is directly derivable from `MeshDataJobOutput.Vertices` but has no assertion today.
- **Build:** `MeshAssert.BoundsWithin(label, o, min, max)` — compute the vertex AABB, assert it is contained in the section's unit-cell-derived box. Add to B2/B4 and any custom-mesh scenario.
- **Closed by:** `MeshAssert.BoundsWithin` + a `SectionCellBounds(pos)` helper in the baseline suite, wired into B2 and B4 (every vertex of the cube must lie inside its section's 16³ cell). The MR-4 *change* still lives in `SectionRenderer`, so this assertion proves only the *premise* (geometry fits the constant bounds); the renderer-side assignment still needs MH-6.
- **Effort:** 🟢 trivial.

#### MH-2 — No pooled-output stale-data guard · **CLOSED** (2026-06-20) · gated **MR-6** (pooling variant)

- **Blind:** every `Run()` allocates a fresh `MeshDataJobOutput`. The `MR-6` *pre-size* variant is already covered (vertex-count + `OutputsEqual` prove output unchanged), but the *pool the output struct* variant introduces a reuse-across-jobs lifecycle where a `Clear()`-but-not-fully-reset buffer could leak stale vertices — exactly the failure class B8 guards for the fluid neighbor buffer.
- **Closed by:** baseline **B17** drives the real `MeshOutputPool` reset path — rent → run scene A →
  `Return` (which `ClearForReuse()`s) → rent the same instance back → run scene B — then asserts the reused buffer is byte-identical (`MeshAssert.OutputsEqual`) to a fresh-buffer scene B run. The hazard is concrete:
  `MeshGenerationJob` *appends* to its output lists and writes triangle indices from a job-local counter that starts at 0, so an uncleared buffer leaks the prior scene's vertices (verified: with the reset disabled B17 fails `Vertices length 120 != 48`). A positive control (scene A's 72 verts ≠ scene B's 48) keeps it non-vacuous. `SectionStats` needs no reset — the job overwrites every section index each run (skipped → `default`).
- **Effort:** 🟢 trivial (built alongside the MR-6 pooling API).

#### MH-9 — `SectionStats` per-section ranges are never asserted · **CLOSED** (2026-06-17) · gates per-section refactors, **MR-4** (bounds-in-stats)

- **Blind:** `MeshGenerationJob` writes `MeshDataJobOutput.SectionStats` — the per-section vertex/triangle start+count ranges `SectionRenderer` uses to slice submeshes — but `StructuralInvariants` checks only global stream lengths and triangle-index ranges, never that the section ranges tile the streams without gap or overlap. A refactor that mis-partitions sections (MR-5/MR-6 work, or MR-4's proposed per-section bounds added to `MeshSectionStats`) passes green.
- **Closed by:** `MeshAssert.StructuralInvariants` now walks `SectionStats` per stream (vertices + all three triangle lists) via `CheckSectionTiling`, asserting every emitting section's `[start, start+count)` range is contiguous, non-overlapping, and sums to the stream length. Zero-count sections (skipped → written as
  `default`) are ignored, matching the job's actual contract. New baseline **B9** places one isolated cube per section (3 emitting sections) so the tiling check is non-vacuous, with a positive control asserting ≥2 sections emitted.
- **Effort:** 🟢 trivial.

### Phase 1 — Value oracles (unblock MR-2; prerequisite for MR-8)

The suite checks UV / color / light streams **only for run-to-run equality**, never against an expected value, and runs with `SmoothLighting.Off` (light map zeroed). The streams MR-2 re-encodes are therefore unvalidated.

#### MH-3 — No smooth-lighting *value* coverage · **CLOSED** (2026-06-18) · gates **MR-2**, prereq for **MR-8**

- **Blind:** `MeshingTestWorld.Run()` defaulted to `SmoothLightingQuality.Off` with a zeroed light map, so the
  `LightData` (`Color32`, the `TexCoord1` smooth-light stream) carried no meaningful value. `MR-2`'s explicit acceptance criterion is "the smooth-lighting encoding in TexCoord1 must be preserved exactly" — there was no way to assert that. `MR-8`'s merge predicate ("merge only faces with identical corner light") also needs real per-corner light values to test.
- **Closed by:** `MeshingTestWorld.FillLight`/`SetLight` populate the in-chunk light map and `Run(SmoothLightingQuality.High)`
  exercises the corner-averaging path. `MeshOracle.ExpectedUniformCornerLight` is a **hand-derived** oracle:
  for a spatially *uniform* light field every one of a corner's 4 samples is equal, so the averaged result is
  `17·V` per channel **independent of which neighbors are sampled** — deriving it never references the engine's
  `CornerOffsets` LUT, avoiding the A4 shared-assumption trap. `MeshAssert.LightDataMatches` + **B11** pin two configs: full sunlight (→ 255 sun) and an intermediate, multi-channel blocklight (R=7→119, G=3→51, proving averaging + UNorm8 rounding + channel order, not a vacuous all-zero/saturated read), with an A≠B positive control proving the populated map drives the output.
- **Scope / future extension:** only the **uniform** (all-corners-equal) case is modelled, which pins the encoding `MR-2` must preserve. **Distinct-per-corner values and AO darkening** (a corner whose diagonal is dropped because both its sides are opaque) are **not yet** covered — predicting which corner darkens requires re-deriving `CornerOffsets`, the A4 trap. A follow-up should add a per-corner oracle that mirrors the side/side/diagonal sampling + AO rule independently (needed to *fully* guard MR-8's equal-corner-light merge predicate). Until then
  MR-8 stays gated on MH-8 + its design doc regardless.
- **Effort:** 🟡 medium.

#### MH-4 — No UV / texture *value* oracle · **CLOSED** (2026-06-17) · gates **MR-2**, prereq for **MR-8**

- **Blind:** UVs are compared only by `OutputsEqual` (determinism). The palette gives each face a distinct texture index (Back=0 … Right=5) *so a regression could surface*, but nothing asserts the emitted UV equals the expected atlas coordinate for a given face/texture. `MR-2` may shift the UV layout; `MR-8` (greedy)
  requires `Texture2DArray` UV.z layer + `frac()` tiling semantics that have no oracle.
- **Closed by:** `MeshOracle.ExpectedFaceUVs(textureID, expectedUVs)` independently re-derives the atlas-cell placement (the math MR-2 may restructure) from the atlas dimensions, and `MeshOracle.ExpectedTextureIDForFace`
  independently re-states the geometry-face → texture selection (a hardcoded copy of the engine's `GetTextureID`
  convention, so a divergence is caught). `MeshAssert.UVsMatch` pins the 4 per-vertex UVs; `CompareCubeFacesToOracle`
  now checks them for all 6 faces of B2 and all 4 yaws of B4 (30 face-UV checks total).
- **Scope note:** the palette emits no UV quarter-turn rotation, so `uvQuarterTurnsCW` is not modelled — a rotated-texture fixture would need its own oracle extension (and the engine's `RotateUvQuarterTurnsCW`
  re-derived independently). The corner-within-cell pattern is hand-defined (BL/TL/BR/TR), not read from the engine's `VoxelUvs` table, so a corruption of that table is caught rather than mirrored.
- **Effort:** 🟡 medium.

### Phase 2 — Pipeline-stage coverage (unblock MR-5; enable MR-3/MR-4 renderer side)

#### MH-5 — `MeshPostProcessJob` / section-space output is never run · **CLOSED** (2026-06-18) · gates **MR-5**, prereq for **MR-2**

- **Blind:** the harness asserted the **chunk-space** `MeshGenerationJob` output and stopped there. The chunk-space → section-space coordinate rewrite (`MeshPostProcessJob`, run via `Schedule().Complete()` in
  `Chunk.ApplyMeshData`) was entirely unguarded. `MR-5` moves *where* that job runs (chained on the mesh handle on a worker thread vs. a blocking main-thread `Complete()`); proving "where" doesn't change "what" requires a baseline on the post-processed section-space output.
- **Also gated MR-2:** `MeshPostProcessJob` is where `InterleavedStream3` (the interleaved Normal+light
  `NormalLightVertex` GPU-upload stream) is assembled — so the very vertex format MR-2 restructures is partly built in this stage and was **empty** in the harness.
- **Closed by:** `MeshingTestWorld.Run(postProcess: PostProcessMode.Separate|Chained)` chains the real
  `MeshPostProcessJob` wired exactly as `Chunk.ApplyMeshData` (`Separate` mirrors production's
  `genJob.Run()` → `postJob.Schedule().Complete()`; `Chained` is the MR-5 shape `postJob.Schedule(genJob.Schedule())`). **B10** asserts (a) section-space coord == chunk-space coord − section origin (`MeshAssert.SectionSpaceVertices`), (b) `InterleavedStream3[i]` == interleave of `Normals[i]`+`LightData[i]` (`MeshAssert.InterleavedMatches`), and (c) chained-vs-separate byte equality (`OutputsEqual` + `MeshAssert.InterleavedStreamsEqual`) — the MR-5 guard. Positive controls: the gen-only run's `InterleavedStream3` is empty (the post stage fills it) and ≥1
  emitting section sits above section 0 (so the y-offset is non-identity).
- **Effort:** 🟡 medium.

#### MH-6 — No `SectionRenderer` apply-path harness · **CLOSED** (2026-06-18, buildable-now portion) · gates **MR-3**, renderer side of **MR-4**

- **Blind:** `MR-3` (cache 7 material-combination arrays, assign `sharedMaterials` only on change) and the *applied* side of `MR-4` (assign constant `Mesh.bounds`) live in `SectionRenderer.UpdateMeshNative`, a
  `MonoBehaviour` path the meshing- *job* suite never instantiates. They were structurally unreachable from the job harness.
- **Closed by:** a *separate* fixture `Framework/SectionRendererTestFixture` (NOT bolted onto `MeshingTestWorld`)
  that instantiates the real `SectionRenderer` and drives `UpdateMeshNative` with tiny synthetic `NativeArray`s (material selection + the active/inactive decision depend only on the three submesh `count` args — no real mesh job needed), observing through the public `GameObject` (`sharedMaterials`, `sharedMesh.bounds`, `activeSelf`).
  `Framework/RendererAssert` adds `MaterialsEqual` + a `BoundsContainAll(Verts)` containment check. Three baselines in `MeshingValidationSuite.Renderer.cs`: **B12** asserts the material array equals the correct combination per submesh-presence bitmask, in opaque → transparent → fluid order, across all 7 non-empty combinations (the load-bearing MR-3 guard); **B13** asserts the empty section (`vertexCount==0`) deactivates the GameObject and leaves `sharedMaterials` untouched; **B14** asserts `Mesh.bounds` *contain* every emitted vertex (a containment
  invariant — stable across MR-4; MH-1 already proved geometry fits the section cell — explicitly **NOT** a tight-AABB equality). Positive controls: B12 proves the 3 stub materials are distinct + two bitmasks yield different arrays; B13 proves a non-empty update activates + assigns (so "inactive + untouched" isn't vacuous); B14 a tripwire proving the containment predicate observes an out-of-bounds vertex.
- **Seam (the blocker):** `UpdateMeshNative` reaches into `World.Instance.{Opaque,Transparent,Liquid}Material`
  (null in edit mode → NRE). Resolved with **option (a) reflection-stub** — reflect the private `World.Instance`
  setter onto an `AddComponent`'d `World` (a plain `MonoBehaviour`, so no `Awake`/`OnEnable`/`OnValidate` runs in edit mode; the setter is driven directly, bypassing `World.Awake`) with a stub `BlockDatabase` holding 3 distinct dummy materials. **Zero production change** (B1–B11 untouched).
- **Build-alongside follow-ups** (landed with the MR-3/MR-4 implementation 2026-06-18, B8/B9/B11 positive-control style): (1) ✅ **B15 — no-reassign-when-bitmask-unchanged** — MR-3's postcondition: prime opaque-only, externally stomp `sharedMaterials` with a sentinel, then a same-bitmask update must leave the sentinel intact (positive control: a changed bitmask overwrites it); (2) ✅ **B16 — bounds == constant section cell** — MR-4's postcondition: `Mesh.bounds` equals the constant 16³ section-cell box (positive control: the probe AABB is strictly smaller,
  so a `RecalculateBounds()`-style tight result would fail). (3) **upgrade the seam to option (b)** — inject `UpdateMeshNative`'s materials (or a cached material-set) instead of reaching into the singleton — **NOT done** in the MR-3/MR-4 PR (the signature was left unchanged, so the reflection stub still applies); still open, do it when the MR-6 pooling work touches the signature anyway.
- **Effort:** 🟡 medium. **Note:** MH-1 covers MR-4's *geometry* premise from job output; MH-6 covers the renderer assignment itself.

### Phase 3 — Palette / shape breadth (close the documented custom/cross/lava blind spot)

#### MH-7 — No custom-mesh / cross-mesh block, no lava fluid · **OPEN** · gates **MR-4** caveat; named blind spot

- **Blind:** `TestMeshBlockPalette` has Air, SolidOpaque, TransparentCube, OrientedOpaque, WaterSource only — **no `RenderShape.Custom`/cross block and no lava fluid.** The custom-mesh job path (`CustomMeshes` /
  `CustomFaces` / `CustomVerts` / `CustomTris`, all empty arrays today) is never exercised, and the
  `LavaVertexTemplates` input is never indexed. This is the exact "custom-mesh/cross-mesh / fluid-value" gap the performance report calls out, and it gates MR-4's "if a custom mesh ever exceeds the unit cell" caveat (you cannot test it without such a block).
- **Build:** add custom-mesh (and cross-shape) entries to the palette with a small custom-mesh oracle, plus a lava fluid entry feeding a real lava height template. Lets B-series scenarios cover the custom/cross/lava routing and geometry the standard oracle can't.
- **Effort:** 🟡 medium (custom-mesh oracle is the bulk of it).
- **Partially closed 2026-08-07 (custom-mesh half only; lava still open).** `Framework/TestCustomMeshLibrary.cs` now builds real flattened `CustomMeshData`/`CustomFaceData`/`CustomVertData`/tri arrays (a parametric box mesh, half slab at `topY = 0.5`) mirroring `JobDataManagerFactory`'s flattening, and `MeshingTestWorld` passes them for real — so the schema-aware custom-mesh path (`GenerateCustomBlockMesh_SchemaAware`) executes in the harness. `TestMeshBlockPalette` gained `HalfSlab` (opacity 15, mirroring production `Stone Half Slab`) and `PartialOpaque` (opacity 7) at ids 7/8. **Face order in the fixture is load-bearing** — the job indexes `BurstVoxelData.FaceChecks[p]` with the face's own array position. Landed as groundwork for [`../../Design/VOXEL_OCCLUSION_REFACTOR.md`](../../Design/VOXEL_OCCLUSION_REFACTOR.md); still no cross-mesh entry and no lava, so MH-7 stays OPEN.
- **Consumes:** the suite's first known-bug scenario, `KM01a` (`MeshingValidationSuite.KnownBugs.cs`), reproducing `MESHING_BUGS.md` Bug M01 — and with it the suite's first `MESHING_BUGS.md` entry. Note the suite now runs **41 scenarios (40 baselines + 1 known-bug)**. `VO-5` is the phase that extends **MH-3** past its documented uniform-field limit toward distinct-per-corner AO values.

### Phase 4 — Structural rebuild (gated behind the MR-8 design doc)

#### MH-8 — Geometry oracle assumes one-quad-per-face; incompatible with greedy meshing · **OPEN** · gates **MR-8**

- **Blind:** *every* geometry assertion assumes one emitted quad per visible face — fixed vertex counts (24/cube), per-quad position matching, and `FindQuadByNormal` assuming a **unique** normal per quad. Greedy meshing (`MR-8`) merges coplanar same-texture same-lighting faces, changing vertex count and breaking all of those primitives at once. The current oracle cannot express the result.
- **Build:** a **merge-invariant** oracle that decomposes emitted (possibly merged) quads back into unit-face coverage and compares the *set of covered unit faces* — each with texture (MH-4), normal, and corner light (MH-3) — independent of how faces were batched. Combined with MH-3 + MH-4 this is the full prerequisite set for greedy meshing, which is itself blocked on its own design doc (`PERFORMANCE_IMPROVEMENTS_REPORT.md` MR-8).
- **Effort:** 🔴 high — a new oracle model, not an extension of the existing one. Do **not** start before the MR-8 design doc exists.

### Phase 5 — Cross-chunk substrate prerequisite (gate **LI-1 → P-2** & **TG-4** Phase 4)

This phase is a different axis from Phases 0–4: it gates not an `MR-*` item but the **halo-padded neighbor-data substrate** shared by **LI-1** (single padded lighting volume), **P-2** (persistent native voxel/light storage, zero-copy jobs — both in
[PERFORMANCE_IMPROVEMENTS_REPORT.md](../../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md)), and **TG-4** Phase 4 (the cross-chunk neighbor view — [BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md](../BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md) §3.2). All three change *how the meshing job receives neighbor voxel data*. The job's border-face culling (cull a boundary face when the neighbor across the border is solid) is the meshing-side consumer of that data — and it is **completely untested today**, so a substrate that under-copies or mis-indexes a neighbor border
plane would produce seam holes / doubled faces with every baseline green. (P-1, the border-slab variant, is the *same* risk surface but is **not** the chosen substrate — see TG-4 §10 "skip P-1"; these baselines guard whichever substrate lands.)

The lighting suite already closed its half of this loop: A1 routes harness input through the shared
`ChunkData.FillJobVoxelMap`/`FillJobLightMap` that `WorldData.FillChunk*ForJob` delegates to, and C1/C2/C3 assert cross-chunk fields against a borderless oracle. The meshing suite now closes **both** sub-gaps below (MH-10/MH-11, **CLOSED 2026-06-21**, baselines B18–B21).

> **No vertical concern:** `MeshGenerationJob` has only 4 cardinal + 4 diagonal neighbor maps, no top/bottom —
> chunks are full 128-high columns, so vertical borders are world edges (always drawn). The halo substrate has
> no vertical-slab case on the meshing side; only the horizontal cardinal/diagonal planes need guarding.

#### MH-10 — Border-face culling never consults a neighbor (consumption gap) · **CLOSED (2026-06-21)** · gated LI-1 / P-2 / TG-4 Ph.4

- **Was:** `MeshingTestWorld.Run` hard-wired all 8 neighbor voxel maps (`NeighborS/N/W/E` + 4 diagonals — named `NeighborBack/Front/Left/Right` until MP-7) to a length-0 `emptyMap`, and every fixture placed blocks in the chunk interior so culling never read a neighbor. The job's "cull this boundary face because the neighbor across the border is solid"
  logic — the meshing-side consumer of all neighbor data — had zero coverage. (Promoted from the §4
  "out of scope" bullet because LI-1/P-2/TG-4 depend on it.)
- **Closed by** (`MeshingValidationSuite.CrossChunk.cs`; harness capability `MeshingTestWorld.SetNeighborEastBlock`
  — a lazily-created, persistent full-`MAP_SIZE` +X neighbor map, opt-in so B1–B17 keep the empty-neighbor behavior). Face counts are hand-derived from the `ShouldDrawFace` contract (no call to the job's predicate; A4-avoidance), guarded by a B3-style palette-assumption check; the prove-red (severing the neighbor from the job) reds **only B19/B21**, confirming non-vacuity:
    - **B18 — neighbor air ⇒ border face drawn** (24 verts). A *populated-air* `NeighborE` proves the map is consulted and air does not cull — distinct from the length-0 "no neighbor → draw" the suite relied on.
    - **B19 — neighbor opaque-solid ⇒ +X face culled** (20 verts; exactly one face fewer than B18). The core culling assertion.
    - **B20 — transparent (renderNeighborFaces) neighbor does not cull** (24 verts) — pins the opaque-vs-transparent predicate against a silent flip.
- **Effort:** 🟡 medium (harness capability + hand-derived face-count oracle).

#### MH-11 — Neighbor input never routed through the production fill path (fill-faithful gap) · **CLOSED (2026-06-21)** · gated LI-1 / P-2 / TG-4 Ph.4

- **Was:** even with MH-10's culling baselines, `MeshingTestWorld` built its maps *directly* — it never called the production `ChunkData.FillJobVoxelMap` (which `WorldData.FillChunkMapForJob` delegates to) that the halo/slab substrate actually rewrites. So MH-10 alone guarded the job's *consumption contract*, not the *fill* that produces the neighbor planes (the meshing analog of the lighting A1 fix).
- **Closed by** `MeshingTestWorld.SetNeighborEastBlockViaProductionFill` — a throwaway `ChunkData` gets the occluder, then `ChunkData.FillJobVoxelMap` produces the +X neighbor map exactly as production does:
    - **B21 — fill-faithful repeat of B19** (20 verts; culled). **Flips red if the halo/slab substrate under-copies or mis-indexes the border plane** — the actual substrate guard.
- **Scope note:** the 4 *diagonal* neighbor maps do not feed culling. They were long described here as feeding
  "only smooth-lighting AO" — **that is wrong, corrected 2026-07-26 (MP-7)**: they also drive **fluid corner geometry**. `GenerateFluidMeshData` unpacks them as `n_NE/n_SE/n_SW/n_NW` and feeds them to
  `GetSmoothedCornerHeight` and `CalculateSymmetricCornerFlow` (`VoxelMeshHelper.cs:746`, `764–780`), **unconditionally — independent of `SmoothLightingQuality`**. So a diagonal fault moves fluid surface vertices, not just shading, and is observable by a geometry oracle without any corner-light oracle. Their fill-faithful analog is still a follow-up; their *routing* is guarded by **B38** (see MH-12).
- **Effort:** 🟡 medium (the fill wiring is the bulk; the baseline reuses B19's geometry).

#### MH-12 — Neighbor maps could be permuted without any baseline noticing · **CLOSED (2026-07-26)** — cardinals B37, diagonals B38

- **Was:** B18–B21 populate only the **+X** map, so they red on any swap that displaces +X but stay green on a swap among the other seven. That is the second half of orchestration finding **F6**: the job's Back/Front/Left/Right fields were mapped onto `NeighborMapSet`'s compass names by a hand-written 16-line wiring table, and a transposed pair there is a cross-chunk seam-culling bug with no red baseline.
- **Closed for the 4 cardinals by** MP-7's `MeshingTestWorld.SetNeighborBlock(CardinalNeighbor, …)` (the single `+X` map generalized to four lazily-created ones; unpopulated directions still pass the length-0
  `emptyMap`, so B1–B36 are behaviorally unchanged) plus:
    - **B37 — every cardinal map reaches its own slot** (80 verts). One isolated opaque cube per cardinal border, **each at a different Y**, with the matching occluder in each neighbor map. Correctly routed, every cube loses exactly its outward face. Under any permutation a probe reads a cell that direction never occupied — wrong Y *and* wrong border plane (+X reads `x=0`, −X reads `x=15`, +Z reads `z=0`, −Z reads `z=15`) — so the face is drawn instead and the count rises.
    - **Prove-red:** swapping `NeighborW`↔`NeighborN` inside the job's own `GetVoxelStateFromLocalPos`
      routing yields `expected 80, got 88` and reds **exactly B37** — B18–B21 stay green, which is the non-vacuity evidence that B37 covers what they structurally cannot.
- **Closed for the 4 diagonals by** **B38** — see below. (The first draft of this entry deferred them as
  "AO-only, gated on MH-3". Both halves of that were wrong; see MH-11's corrected scope note.)
- **B38 — every diagonal map reaches its own slot**, via **fluid corner geometry** rather than culling.
  `GetSmoothedCornerHeight` averages `templates[level]` over the centre plus each same-fluid neighbour, and admits the diagonal term **only when an adjacent cardinal is also fluid** (`VoxelMeshHelper.cs:1148`) — so the fixture puts water on a chunk corner, water in the adjoining cardinal map, and **lower-level** water in the diagonal map (uniform levels would average back to the same height, the same degeneracy MH-3 hit). Each diagonal is exercised at its own Y, so a permuted map is read where that direction holds air, the diagonal term drops out, and
  the corner returns to full height. The assertion is a strict **inequality against a no-diagonal control run**, never template arithmetic — A4-safe.
- **Effort:** 🟢 for the cardinal half; 🟡 for the diagonal half as executed (a cross-chunk cardinal is forced into the fixture by the adjacency gate, and the oracle is a per-corner comparison rather than one count).
- **B39 — the acquire-site table (added by a second review round, 2026-07-26).** B37/B38 guard the *harness → job-field* routing. A **second** direction→offset table sits one layer above, in what was
  `WorldJobManager.AcquireNeighborMaps` — and it feeds **both** the meshing and lighting schedules while **neither** suite executed it (`MeshingTestWorld` and `LightingTestWorld` each build their own
  `NeighborMapSet`; the latter's comment even says it "mirrors production's `AcquireNeighborMaps`"). Extracted to `Helpers/NeighborMapAssembler.Build` behind an explicit `INeighborMapSource` implemented on
  `WorldJobManager` (the `IMeshDrainHost`/`IMeshCompletionHost` pattern — explicit so the `ChunkCoord` wrappers stay out of the class's own overload set beside its private `Vector2Int` originals, and buffer acquisition is not discoverable on a `World.Instance.JobManager` reference; note that this prevents accidents, **not** misuse — `INeighborMapSource` is public, so a deliberate cast still reaches a pooled rent with no matching `Return`). B39 drives it with a fake source that mints a unique marker per call and records which chunk it was minted for, then
  asserts all 16 slots — voxel **and** light — by resolving each slot's marker back to a coordinate. The marker is a **counter, not a packing of the coordinate**: any such packing collides outside a bounded domain, and the light markers narrow to `ushort`, so a far-out center could alias two slots — a silent false green in the very oracle B39 exists to be. **Prove-red is also the proof the gap was real:** transposing N/S inside `Build` reds **only B39**, failing as
  `slot holds the map for ChunkCoord(3, -6), expected ChunkCoord(3, -4)`. As of MP-7's close (39 baselines) the other **38** meshing baselines *and all 88 lighting baselines* stayed green — 126 baselines blind to a swap that would misroute every N/S seam in both pipelines. Re-run against the rewritten oracle on 2026-07-26 (40 baselines): still exactly B39, with 39 + 88 = **127** green.

#### MH-13 — Neighbor **light** map permutation is unguarded · **CLOSED (2026-07-26)** — B40

- **Was:** the 8 `Light*` neighbor fields received the identical hand-written rewiring as the voxel maps, at the same four construction sites (`WorldJobManager.cs:540–547`, `IsolatedJobProbe.cs`,
  `EditorChunkPipelineRunner.cs`, `StartupCalibrationProbe.cs`) plus `Helpers.NeighborMapAssembler.Build`
  (whose light half **is** guarded, by B39) — and **nothing observed them**. `MeshingTestWorld.Run` passed the length-0 `emptyLight` to all eight light slots, and B37/B38 run at `SmoothLightingQuality.Off`, so neither could see a light transposition.
- **Failure scenario:** transpose `LightS`/`LightN` and cross-seam smooth lighting samples the wrong chunk — a visible light discontinuity along every N/S chunk border, with `Validate Meshing` still fully green.
- **Not a live bug:** the wiring was verified correct on both producer and consumer ends (2026-07-26, three independent traces). B40 is a *regression* guard; MP-7 had already lowered the risk at the four construction sites by making them self-checking (`LightS = …LightS`).
- **Closed by** `MeshingTestWorld`'s per-direction light maps — 8 lazily-created `NativeArray<ushort>` fields resolved through per-direction `NeighborLightRef` switches (never an index), with
  `FillNeighborLight(direction, packed)` to brighten one and `EnsureNeighborChunk(direction)` to materialize a neighbor as *loaded but empty and dark*. Uncreated stays length-0, so B1–B39 are behaviorally unchanged (verified: the harness commit alone re-ran **39/39**) — plus:
    - **B40 — every neighbor light map reaches its own slot**, in 8 legs (4 cardinal + 4 diagonal). Each leg materializes all eight neighbor chunks, puts one opaque cube where its corner samples cross the seam under test, and meshes twice at `SmoothLightingQuality.High`: a **control** run with every light map dark, then a run with **only** that direction's map filled to full sky. The assertion is presence-vs-absence of light on the emitted vertices — never a predicted corner value — so the engine's
      `CornerOffsets` LUT and averaging formula stay out of the oracle (the A4 discipline, as in B38).
    - **Why it catches every permutation:** the four cardinal probes sit mid-face and therefore read **exactly one** slot, so any permutation displacing a cardinal reds that leg; a permutation confined to the diagonals moves a diagonal's map outside its probe's read set (a corner probe reads only its own diagonal plus the two adjacent cardinals); and any diagonal→cardinal move necessarily displaces some cardinal. No non-identity permutation of the eight slots survives all eight legs.
    - **The MH-3 uniform-field trick deliberately does not transfer**, and the sketch below needed one correction to work at all. A spatially uniform field would make the corner average permutation- **invariant**; B40's field is asymmetric by construction (one bright neighbor, everything else dark). More importantly, `SampleNeighborLight` resolves the **voxel** state first, and a *missing* neighbor voxel map short-circuits to full sunlight (15) **without ever reading the light map** — so a bright reading can mean "the routing works" *or* "there is no
      neighbor at all". `FillNeighborLight` therefore materializes the whole neighbor chunk (a `/code-review` round closed that hole in the API itself rather than leaving it to caller discipline; `EnsureNeighborChunk` remains the way to model a deliberately **dark** neighbor), and each leg's all-dark **control run** is what mechanically enforces the property — any un-modelled sunlight source reds the control instead of passing silently.
    - **Prove-red:** transposing `LightS`/`LightN` inside the job's own `GetLightDataFromLocalPos` routing reds **exactly B40** — and inside it, exactly the **N and S legs** (`brightest vertex sky = 0`), with the other six legs, the other 39 meshing baselines, and **all 88 lighting baselines** green. Reverted and re-verified at `Validate All` 350.
- **What B40 does *not* observe (the honest boundary):** it drives the **meshing job's own** direction→slot light routing plus the harness wiring into it. It does **not** execute the four production construction sites (those remain guarded only by MP-7's self-checking field names, exactly as B37/B38 leave the voxel side), nor `NeighborhoodLightingJob`'s separate light routing.
- **Residual (open):** the **fill-faithful** light half — the MH-11 analog, routing a neighbor light map through the production `ChunkData.FillJobLightMap` rather than a direct array write — so a halo/slab substrate that under-copies or mis-indexes a border **light** plane is still unguarded (B21 covers only the voxel plane). Small, and best built alongside the substrate work it guards.
- **Effort:** 🟢 as executed (the sketch held once the voxel-map prerequisite was found); the pre-execution estimate was 🟡.

---

## 4. Out of scope (by design)

- **`Clouds.cs` mesh (MR-9).** Cloud meshing is a separate system, not chunk meshing; it would need its own tiny harness. Low value — likely not worth building. **WONTFIX (here)** — track under MR-9 directly.
- **Cross-chunk border-face culling.** ~~No open `MR-*` item depends on border culling; revisit only if one does.~~ **PROMOTED out of "out of scope" → §3 Phase 5 (MH-10/MH-11, CLOSED 2026-06-21, B18–B21)** — LI-1, P-2, and TG-4 Phase 4 all change how the meshing job receives neighbor voxel data, so border culling is a substrate prerequisite, now guarded.
- **True concurrency / Burst scheduling races.** Synchronous `job.Run()` only — mirrors the lighting suite's B3 **WONTFIX (structural)**. MR-5's value (off-main-thread scheduling) is about *where* work runs; the harness verifies output equivalence (MH-5), not the threading itself.
- **Scheduling / drain orchestration (the decision layer).** ~~This is a *job* harness — it starts at the mesh job's inputs; the `ScheduleMeshing` gate composition and the per-frame drain policy are production-only.~~
  **CLOSED for the decision layer (MP-2, 2026-07-24), baselines B24/B25 in this same suite** — the gates are the pure `MeshingScheduleDecision` (B24 decision census) and the drain loop is `MeshDrainPolicy.Drain` (B25 drain policy: quota/window/cap stops, purge, remove-vs-leave, priority order). **MP-3 (2026-07-24) added B26** — the in-flight request policy fix (F1): the shared `MeshingScheduleDecision.DequeuesChunk` mapping leaves an in-flight request queued instead of dropping it, guarded by the pure mapping + a two-frame drain scenario. Owned by
  [MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md](../../Design/MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md), not this job-fidelity doc. **MP-4 (2026-07-25) added B27** — the completion pass (F5): `ProcessMeshJobs` now drives the shared
  `Helpers/JobCompletionPass` skeleton (renamed from `LightingCompletionPass`, generalized with the P-4
  `window` + rotating `startIndex`) through a separate cached driver, and B27 replays that skeleton world-free with a recording fake driver — stage-1 carries over without releasing, stage-2 still releases + enrolls, remove strictly after the merge loop, the window break, and the rotated visit order. **MP-5 (2026-07-25) added B28–B30** — the GS-5 §7.3 **renderer-ownership split** (F3), on the MH-6 renderer fixture rather than the job harness:
  `SectionRenderer.SetOcclusionCulled(bool)` is the codebase's only writer of
  `MeshRenderer.forceRenderingOff`, and the baselines pin the two axes apart — **B28** the apply path never writes the flag (an externally-set flag survives both a non-empty and an empty
  `UpdateMeshNative`), **B29** `Clear()` resets it on pool recycle, **B30** the setter round-trips without touching `activeSelf`. **MP-6 (2026-07-25) added B31–B33** and closed the last uncovered stage, the **draw tail** (F4) — by deleting it. The `ChunksToDraw` queue's only remaining job was triggering the one-shot load animation, so MP-6 moved that into the mesh apply itself; what survives is one branch inside the production
  `MeshCompletionDriver`, which the §8.1 `IMeshCompletionHost` seam finally makes drivable world-free (B27 replays the *skeleton* with a fake driver; B31–B33 replay the *driver* with a fake host). **B31** the apply → animate mapping (a gone chunk discards without animating and still releases — the MR-6 single-release-site invariant, evidence-only until now), **B32** a faulting apply never animates yet still releases and does not abort the pass, **B33** the `_curJob` scratch lifecycle (each release gets its own job; the scratch is cleared, so an
  out-of-sequence hook cannot double-return the previous job's pooled buffers — the 2026-07-25 code-review finding that no baseline could observe before). Suite tip is now **B33**
  (33 baselines). **B34–B36 (2026-07-25) went one step further and stood a real `Chunk` up after all.** MP-6's note said the chunk-side behavior could not be reached because it needs a `GameObject`; that turned out to be too pessimistic — `SectionRenderer`'s constructor is `World.Instance`-free and resolves materials only in
  `UpdateMeshNative`, so `ChunkLoadAnimationTestFixture` can construct a real `Chunk` with nothing but a stub
  `World` + `Settings`. The baselines guard the `enableChunkLoadAnimations` **toggle regression**
  (`_FIXED_BUGS.md` Chunk Management #08, introduced 2026-04-09 and unnoticed for ~3.5 months): **B34** a chunk built while animations were off still animates once the setting is enabled mid-session, **B35** the mid-session-added component is seeded relative to the chunk instead of lerping to the world origin, **B36**
  construction-time controls.
  > **What is still not covered here:** the `Reset` path's lazy creation (it needs `worldData.RequestChunk`,
  > which the fixture deliberately does not stand up), and the real pause-menu toggle in play mode. The
  > one-shot latch itself is exercised only indirectly. In-game confirmation remains the acceptance path.

  **MP-7 (2026-07-26) added B37–B38** — the neighbor-map **permutation guards** (MH-12 below), F6's coverage half: **B37** catches any permutation of the 4 cardinal maps through border-face culling, **B38** any permutation of the 4 diagonals through fluid corner height (the diagonals never reach culling, so they needed a geometry probe instead). A `/code-review` round on MP-7 also split out **MH-13** — the 8 `Light*`
  maps, identically rewired and still unguarded — and a second round added **B39** for the *acquire-site*
  offset table that feeds both the meshing and lighting schedules. **MH-13 then closed (2026-07-26) as B40** — the light twin of B37/B38, and the first baseline to run `MeshingTestWorld` with populated neighbor **light** maps at all. Suite tip is now **B40** (40 baselines).

---

## 5. Phased backlog snapshot

| Phase | Gap   | Finding                                               | Gates                       | Status | Effort |
|-------|-------|-------------------------------------------------------|-----------------------------|--------|--------|
| 0     | MH-1  | Bounds-extent assertion                               | MR-4 (premise)              | CLOSED | 🟢     |
| 0     | MH-2  | Pooled-output stale-data guard                        | MR-6 (pool variant)         | CLOSED | 🟢     |
| 0     | MH-9  | `SectionStats` per-section ranges asserted            | per-section refactors; MR-4 | CLOSED | 🟢     |
| 1     | MH-3  | Smooth-lighting *value* coverage (uniform)            | MR-2; prereq MR-8           | CLOSED | 🟡     |
| 1     | MH-4  | UV / texture *value* oracle                           | MR-2; prereq MR-8           | CLOSED | 🟡     |
| 2     | MH-5  | `MeshPostProcessJob` / section-space output coverage  | MR-5                        | CLOSED | 🟡     |
| 2     | MH-6  | `SectionRenderer` apply-path harness                  | MR-3; MR-4 (renderer)       | CLOSED | 🟡     |
| 3     | MH-7  | Custom/cross-mesh + lava palette & oracle             | MR-4 caveat; blind spot     | OPEN   | 🟡     |
| 4     | MH-8  | Merge-invariant geometry oracle                       | MR-8                        | OPEN   | 🔴     |
| 5     | MH-10 | Border-face culling consumption (B18–B20)             | **LI-1 / P-2 / TG-4 Ph.4**  | CLOSED | 🟡     |
| 5     | MH-11 | Neighbor fill-faithful via production path (B21)      | **LI-1 / P-2 / TG-4 Ph.4**  | CLOSED | 🟡     |
| 5     | MH-12 | Voxel-map permutation — cardinals (B37)               | MP-7 / F6                   | CLOSED | 🟢     |
| 5     | MH-12 | Voxel-map permutation — diagonals, fluid corner (B38) | MP-7 / F6                   | CLOSED | 🟡     |
| 5     | MH-12 | Acquire-site offset table, voxel + light (B39)        | MP-7 review 2; mesh+light   | CLOSED | 🟡     |
| 5     | MH-13 | **Light**-map permutation, all 8 (B40)                | regression guard            | CLOSED | 🟢     |
| 5     | —     | Fill-faithful **light** plane (MH-11 analog)          | halo/slab substrate         | OPEN   | 🟢     |

> **Wave 1 (2026-06-17):** MH-9, MH-1, MH-4 closed (baselines B1–B9 green, one commit each).  
> **Wave 2 (2026-06-18):** MH-5 (B10) + MH-3 (B11) closed (baselines B1–B11 green, one commit each).  
> **Wave 3 (2026-06-18):** MH-6 (B12–B14) closed — buildable-now portion (baselines B1–B14 green, one commit).
> **MR-* implementation phase, Wave 1 (2026-06-18):** MR-3 + MR-4 + MR-5 landed against this suite; MR-3/MR-4
> added the build-alongside postconditions **B15** (no-reassign) + **B16** (constant-cell-bounds) → baselines
> **B1–B16** green. The only remaining hard *harness* prerequisite is **MH-8** (MR-8). MH-7 is best built
> alongside the custom/cross/lava work it guards.  
> **MR-6 (2026-06-20):** pre-size + pool the `MeshDataJobOutput` buffers landed against this suite; **MH-2**
> closed with build-alongside baseline **B17** (a pooled output reused across two scenes == a fresh buffer) →
> baselines **B1–B17** green.

### MR-item readiness at a glance

| MR item                   | Baselinable today?            | Needs first                                                                                                                                             |
|---------------------------|-------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|
| MR-2 (vertex format)      | ✅                            | ~~MH-3 + MH-4 + MH-5~~ ✅ all done (encoding pinned; distinct-corner light is a future MH-3 extension)                                                  |
| MR-3 (material caching)   | ✅ **IMPLEMENTED 2026-06-18** | ~~MH-6~~ ✅ (B12 material-combo) + ~~no-reassign postcondition~~ ✅ **B15**                                                                             |
| MR-4 (constant bounds)    | ✅ **IMPLEMENTED 2026-06-18** | ~~MH-1 (premise)~~ ✅ + ~~MH-6 (renderer, B14 containment)~~ ✅ + ~~constant-cell-bounds postcondition~~ ✅ **B16**; MH-7 custom-mesh caveat still open |
| MR-5 (chain post-process) | ✅ **IMPLEMENTED 2026-06-18** | ~~MH-5~~ ✅ done (B10 chained-vs-separate equality)                                                                                                     |
| MR-6 (pre-size / pool)    | ✅ **IMPLEMENTED 2026-06-20** | ~~MH-2 (pool variant)~~ ✅ **B17** (reused==fresh stale-data guard)                                                                                     |
| MR-8 (greedy meshing)     | ❌                            | MH-8 + a per-corner MH-3 extension (and its own design doc); ~~MH-4~~ ✅ done                                                                           |
| MR-9 (clouds)             | ❌                            | out of scope (separate harness)                                                                                                                         |

> After Wave 3, **MR-2 is fully baselinable** (MH-3 + MH-4 + MH-5 ✅), **MR-5 is unblocked** (MH-5 ✅), and
> **MR-3 + the renderer side of MR-4 are baselinable** (MH-6 ✅ — B12/B14). The only remaining **hard
> prerequisite** is MH-8 (MR-8) — a baseline cannot be written without it. MH-2 and MH-7 are still better built
> *alongside* their optimization than ahead of it. (MH-1, MH-3, MH-4, MH-5, MH-6, MH-9 are now CLOSED; MH-8/MR-8
> additionally want a per-corner light oracle beyond MH-3's uniform case.)

---

## 6. Execution waves (sequencing plan)

This is the recommended order to build the remaining gaps, grouped into **waves** — each wave is a coherent set that leaves the suite green and unblocks a named optimization. A wave is a *sequencing* layer on top of the phases in §3; phases say "what depends on what", waves say "do these next, in this order". Build every item **test-first** per the `validation-driven-bugfix` skill, one **commit per MH-#**, with **all baselines green**
after each commit and a final docs-sync commit flipping the closed items' status here + in the skill ref.

> **Cold-start checklist for any wave** (matches how Wave 1 was executed):
> 1. `dotnet build "Assembly-CSharp-Editor.csproj"` after edits.
> 2. In the live Editor: `CompilationPipeline.RequestScriptCompilation()` (via `Unity_RunCommand`, fully
>    qualify the type — the MCP wrapper namespace shadows `CompilationPipeline`), then poll
>    `Unity_ManageEditor → GetState` until `IsCompiling == false`. A bare `dotnet build` does **not** make the
>    Editor re-run the menu suite (stale-code trap — see [[feedback-editor-validation-workflow]]).
> 3. Run `Minecraft Clone/Dev/Validate Meshing` (menu item), read the console, confirm
>    `ALL N MESHING BASELINE TESTS PASSED`.
> 4. Every new differential/value baseline needs a **positive control** so it can't pass vacuously (the B8/B9
>    pattern). Editor-test code is exempt from the `Assets/Scripts/Jobs/` Burst rules.

### Wave 1 — derivable-from-output guards · ✅ DONE (2026-06-17)

MH-9 (`SectionStats` tiling), MH-1 (bounds extent), MH-4 (UV value oracle). One commit each, baselines **B1–B9** green. These needed **no** harness-infrastructure change — all derivable from the existing chunk-space `MeshGenerationJob` output. Wave 2 onward is different: each item needs a **new run path or a second job** before a baseline can exist.

### Wave 2 — unblock MR-2 + MR-5 (job-suite depth) · ✅ DONE (2026-06-18)

Theme: make the two most-blocked job-side optimizations baselinable. Order: **MH-5 first** (lower risk, wider unblock), **MH-3 second** (riskiest; completes MR-2). Executed in that order — MH-5 (B10) then MH-3 (B11), one commit each, baselines **B1–B11** green after each. MH-3 landed the **uniform-field** corner-light oracle only (the encoding MR-2 needs); the per-corner/AO extension is deferred (see §3 MH-3).

1. **MH-5 — run `MeshPostProcessJob` + light up `InterleavedStream3`** (gates MR-5; half of MR-2).
    - *Investigate first:* read `MeshPostProcessJob` + `Chunk.ApplyMeshData`'s `Schedule().Complete()` wiring to learn its exact inputs / in-place semantics (this is the one real unknown).
    - *Build:* opt-in flag on `MeshingTestWorld.Run(...)` (e.g. `runPostProcess: true`) that chains
      `MeshGenerationJob` → `MeshPostProcessJob` and exposes the post-processed output.
    - *Baseline (≈B10):* (a) section-space coord == chunk-space coord − section origin; (b) `InterleavedStream3[i]`
      == interleave of `Normals[i]` + `LightData[i]`; (c) **chained-vs-separate equality** (the MR-5 guard:
      worker-handle chain vs. blocking `Complete()` produce byte-identical output — MR-7/B8-style differential).
    - *Risk:* 🟢 low (equality/structural, no hand-derived value oracle).
2. **MH-3 — smooth-lighting *value* oracle** (completes MR-2; prereq MR-8).
    - *Investigate first:* read `CalculateCornerLights` + the AO/light-averaging path — **do not copy it** into the oracle (A4-class shared-assumption trap, called out in §3 MH-3).
    - *Build:* populate `MeshingTestWorld`'s in-chunk light map + expose `Run(SmoothLightingQuality.High)`; add
      `MeshAssert.LightDataMatches` + a **hand-derived** corner-light oracle.
    - *Baseline:* a deliberately trivial, hand-computable lit config (e.g. one sky-exposed top face = full sunlight; a face against a lamp = known blocklight) so expected values are derivable by hand.
    - *Risk:* 🔴 highest in the wave — keep the lit config simple enough to derive independently.

   **After Wave 2, MR-2 is fully baselinable** (MH-3 + MH-4 ✅ + MH-5) and **MR-5 is unblocked** (MH-5).

### Wave 3 — renderer apply-path (separate harness) · ✅ DONE (2026-06-18)

3. **MH-6 — `SectionRenderer` apply-path fixture** (gates MR-3; renderer side of MR-4) · **CLOSED** (buildable-now portion). A **separate** fixture `SectionRendererTestFixture` (reflection-stub `World.Instance` seam, zero production change), NOT bolted onto `MeshingTestWorld`. Baselines **B12–B14** green: material-combination per submesh-presence bitmask (the MR-3 guard), empty-section deactivate + no-assign, and `Mesh.bounds`-contain-all (the MR-4 renderer containment premise; MH-1 proved the geometry premise from job output). The
   **no-reassign-when-unchanged** (MR-3) and **constant-cell-bounds** (MR-4) postconditions landed with the MR-3/MR-4 implementation as **B15** + **B16** (2026-06-18). Upgrading the seam to option (b) production injection was **NOT** taken (the `UpdateMeshNative` signature was left unchanged, so the reflection stub still applies) — still open for whenever the MR-6 pooling work touches the signature.

### MR-* implementation phase — Wave 1 (apply-path quick wins) · ✅ DONE (2026-06-18)

The first wave of actually *implementing* the MR-* optimizations against the now-ready suite: **MR-3**
(material-combination caching, guarded by B12 + new B15), **MR-4** (constant section-cell bounds, guarded by B14 + new B16), **MR-5** (chain `MeshPostProcessJob` at schedule time, guarded by B10). All land in
`SectionRenderer.cs` / `WorldJobManager.cs` / `Chunk.cs`; baselines **B1–B16** green; in-game render confirmed.

### MR-* implementation phase — Wave 2 (mesh-output buffers) · ✅ DONE (2026-06-20)

**MR-2** (32 B/vertex packed format, 2026-06-20) then **MR-6** (pre-size + pool the `MeshDataJobOutput`
buffers, 2026-06-20). MR-6 lands in `Data/JobData.cs` (pre-size ctor + `FromPool` flag + `ClearForReuse`),
`Helpers/MeshOutputPool.cs` (new, mirrors `ChunkJobArrayPool`), `WorldJobManager.cs` (rent at `ScheduleMeshing`, return centrally in `ProcessMeshJobs` — symmetric with the input release, so `Chunk` stays pool-agnostic), and
`Chunk.cs` (`ApplyMeshData` no longer disposes the output). Build-alongside guard **MH-2** closed as **B17** → baselines **B1–B17** green.

### Build-alongside-the-optimization (not standalone waves)

- **MH-2 — pooled-output stale-data guard** (MR-6 pool variant). ✅ **CLOSED 2026-06-20** as **B17** — built in the MR-6 PR against the live `MeshOutputPool` API. Reuses `OutputsEqual`.
- **MH-7 — custom/cross-mesh + lava palette & oracle** (MR-4 caveat; named blind spot). 🟡 medium (custom-mesh oracle is the bulk). No open MR *blocks* on it beyond MR-4's "custom mesh exceeds the unit cell" caveat — build it alongside the custom/cross/lava work it guards.

### Gated — do not start yet

- **MH-8 — merge-invariant geometry oracle** (MR-8 greedy meshing). 🔴 high — a new oracle model (decompose merged quads back to unit-face coverage), **not** an extension of the existing one. **Blocked on the MR-8 design doc** (`PERFORMANCE_IMPROVEMENTS_REPORT.md` MR-8). Needs MH-4 ✅ and MH-3 ✅ (but MR-8's equal-corner-light merge predicate additionally wants the **per-corner / AO** MH-3 extension, beyond the uniform case shipped in B11).

### Out of scope

`Clouds.cs` (MR-9), cross-chunk border culling, and true concurrency/Burst races — see §4.

---

## 7. Cross-references

- Optimization backlog the gaps gate: [PERFORMANCE_IMPROVEMENTS_REPORT.md](../../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) (§Meshing & Rendering, §Verification)
- Sibling harness doc & status-tag conventions: [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
- Meshing architecture: [SUB_CHUNK_MESHING_ARCHITECTURE.md](../SUB_CHUNK_MESHING_ARCHITECTURE.md)
- Harness file map, API cheat sheet & MR-* guard pattern: `.agents/skills/validation-driven-bugfix/references/meshing-suite.md`
- Test-first workflow (lifecycle, taxonomy, pitfalls): `.agents/skills/validation-driven-bugfix/SKILL.md`
- Harness source: `Assets/Editor/Validation/Meshing/`
