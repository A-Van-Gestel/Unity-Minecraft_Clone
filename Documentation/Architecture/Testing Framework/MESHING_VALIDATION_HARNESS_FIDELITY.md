# Meshing Validation Harness — Fidelity Boundary & Extension Backlog

**Status:** ✅ **Active backlog** — Wave 1 executed 2026-06-17 (MH-1/MH-4/MH-9 closed), Wave 2 executed
2026-06-18 (MH-5/MH-3 closed), Wave 3 executed 2026-06-18 (MH-6 closed — buildable-now portion); see §6.
**Created:** 2026-06-16 · **Last updated:** 2026-06-18
**Scope:** `Assets/Editor/Validation/Meshing/` — the `MeshingValidationSuite` + `MeshingTestWorld` +
`MeshOracle` + `MeshAssert` + `TestMeshBlockPalette` harness (menu item
**`Minecraft Clone/Dev/Validate Meshing`**).
**Sibling:** [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](LIGHTING_VALIDATION_HARNESS_FIDELITY.md) — same
document shape; the meshing suite was built test-first as that suite's younger sibling.

---

## 1. Why this document exists

The meshing validation suite (baselines **B1–B11**, all green) runs **real production code**: it executes
the actual `Jobs.MeshGenerationJob` synchronously (`job.Run()`) over a synthetic single chunk and asserts
its `MeshDataJobOutput` — and, since Wave 2 (MH-5), optionally chains the real `Jobs.MeshPostProcessJob`. It
is the regression guard that lets the `MR-*` performance findings in
[PERFORMANCE_IMPROVEMENTS_REPORT.md](../../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) claim
"output-preserving" — it already closed **MR-1** (per-vertex `Quaternion.Euler` hoist, guarded by B1/B4)
and **MR-7** (per-fluid-voxel `Allocator.Temp` arrays, guarded by B7/B8).

It is **blind** wherever it (a) checks a stream only for *determinism* but never against an *expected
value*, (b) *omits a pipeline stage*, or (c) lacks a *block shape* in its palette. A green suite does
**not** prove correctness in those areas. The `PERFORMANCE_IMPROVEMENTS_REPORT.md` §Verification note
already concedes the headline gap:

> *"Fluid/custom-mesh/cross-mesh and UV/light values are not yet oracle-covered — extend the suite before
> optimizing those paths."*

This note enumerates those blind spots **as a prioritized, phased backlog**, because most of the still-open
`MR-*` items cannot be baselined until a specific harness capability is built first. It is the meshing
analog of the lighting fidelity doc, written at the point where the open optimizations (not the suite
itself) define what to build next.

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

- **Real meshing job.** `MeshingTestWorld.Run()` builds the real `MeshGenerationJob` with production-faithful
  inputs (real water height templates via `FluidMeshData.BuildVertexHeightTemplate`, default sections forcing
  the per-voxel standard path) and runs it synchronously.
- **Standard-cube geometry oracle.** `MeshOracle.ExpectedStandardCubeFace` independently derives the 4 vertex
  positions + normal of every cube face × {0,90,180,270}° via `Quaternion.Euler` ground truth (B1 isolated,
  B4 end-to-end through the job).
- **Structural + determinism invariants.** `MeshAssert.StructuralInvariants` (stream lengths consistent,
  triangle indices in range, multiple-of-3) and `OutputsEqual` (full byte-for-byte stream equality across two
  runs — vertices, all three triangle lists, normals, UVs, colors, packed light).
- **Submesh routing.** Opaque (B2), transparent (B6), fluid (B7) faces land in the correct triangle list.
- **Occlusion.** Fully enclosed cube emits nothing (B3); derived face-count assertion with palette-assumption
  guard.
- **Fluid neighbor-buffer isolation.** B8 — the differential guard that closed MR-7 (shore-mask + full
  per-vertex quad equality between an isolated and a primer-preceded probe).
- **UV value oracle (Wave 1, MH-4).** `MeshOracle.ExpectedFaceUVs` + `MeshAssert.UVsMatch` pin every
  standard-cube face's 4 UVs to its texture's independently-derived atlas cell (B2 all 6 faces, B4 all 4 yaws).
- **`SectionStats` tiling (Wave 1, MH-9).** `StructuralInvariants` asserts the per-section ranges tile each
  stream contiguously; B9 (one cube per section) exercises it across 3 emitting sections.
- **Bounds extent (Wave 1, MH-1).** `MeshAssert.BoundsWithin` asserts every vertex lies inside its section
  cell (B2/B4) — the premise behind MR-4's constant bounds.
- **Post-process / section-space output (Wave 2, MH-5).** `MeshingTestWorld.Run(PostProcessMode.Separate|Chained)`
  chains the real `MeshPostProcessJob`; B10 asserts the chunk-space → section-space coordinate rewrite
  (`MeshAssert.SectionSpaceVertices`), that `InterleavedStream3` is the interleave of `Normals`+`LightData`
  (`MeshAssert.InterleavedMatches`), and chained-vs-separate byte equality (the MR-5 guard).
- **Smooth-lighting *values* (Wave 2, MH-3).** `MeshingTestWorld.FillLight` + `Run(SmoothLightingQuality.High)`
  populate a uniform light field; `MeshOracle.ExpectedUniformCornerLight` (hand-derived `17·V`, LUT-independent)
  + `MeshAssert.LightDataMatches` pin the smooth-light encoding (B11: full sun 255, intermediate 119/51).
- **Renderer apply-path (Wave 3, MH-6).** A *separate* fixture (`Framework/SectionRendererTestFixture`) drives the
  real `SectionRenderer.UpdateMeshNative` in edit mode (reflection-stub `World.Instance` + 3 distinct stub
  materials) and observes through the public `GameObject`; **B12** pins material-combination selection per
  submesh-presence bitmask (all 7 combos, opaque→transparent→fluid order), **B13** the empty-section deactivate +
  no-assign short-circuit, **B14** that `Mesh.bounds` *contain* every emitted vertex (`RendererAssert`). This is a
  different harness from the job suite — see §3 MH-6.

> **The trusted core is now whole for the job + post-process stages.** `InterleavedStream3` (the Normals+light
> GPU-upload vertex stream) *was* empty here because it is built by `MeshPostProcessJob`; it is now produced
> and asserted via the MH-5 opt-in path (→ **MH-5**, CLOSED 2026-06-18). `MeshDataJobOutput.SectionStats`
> (per-section vertex/triangle index ranges) is tile-checked by `StructuralInvariants` (→ **MH-9**, CLOSED
> 2026-06-17). Smooth-light values are oracle-covered for the *uniform* case (→ **MH-3**, CLOSED 2026-06-18);
> distinct-per-corner / AO values remain a future extension (see §3 MH-3).

---

## 3. Blind spots & the phased extension backlog

Gap IDs are `MH-#`, matching the analysis numbering that produced this note. Each entry states what is blind,
which `MR-*` item it gates, what to build, and effort. The phase ordering is value-for-prerequisite: a phase's
items unblock the optimization wave that depends on them.

### Phase 0 — In-PR quick wins (no new system; not prerequisites)

These are small enough to land in the same PR as the optimization they guard. Listed so they aren't mistaken
for blockers.

#### MH-1 — No bounds-extent assertion · **CLOSED** (2026-06-17) · gates **MR-4**

- **Blind:** the suite never checks the spatial extent of the emitted geometry. `MR-4` replaces the
  per-section `RecalculateBounds()` with a constant `Bounds`; its correctness criterion is "every emitted
  vertex lies within the section cell," which is directly derivable from `MeshDataJobOutput.Vertices` but has
  no assertion today.
- **Build:** `MeshAssert.BoundsWithin(label, o, min, max)` — compute the vertex AABB, assert it is contained
  in the section's unit-cell-derived box. Add to B2/B4 and any custom-mesh scenario.
- **Closed by:** `MeshAssert.BoundsWithin` + a `SectionCellBounds(pos)` helper in the baseline suite, wired
  into B2 and B4 (every vertex of the cube must lie inside its section's 16³ cell). The MR-4 *change* still
  lives in `SectionRenderer`, so this assertion proves only the *premise* (geometry fits the constant
  bounds); the renderer-side assignment still needs MH-6.
- **Effort:** 🟢 trivial.

#### MH-2 — No pooled-output stale-data guard · **IN-PR** · gates **MR-6** (pooling variant)

- **Blind:** every `Run()` allocates a fresh `MeshDataJobOutput`. The `MR-6` *pre-size* variant is already
  covered (vertex-count + `OutputsEqual` prove output unchanged), but the *pool the output struct* variant
  introduces a reuse-across-jobs lifecycle where a `Clear()`-but-not-fully-reset buffer could leak stale
  vertices — exactly the failure class B8 guards for the fluid neighbor buffer.
- **Build:** a scenario that runs scene A then scene B through **one** reused output instance and asserts B's
  result is byte-identical to a fresh-buffer B run. Reuses existing `OutputsEqual`.
- **Effort:** 🟢 trivial (once the pooling API exists to test against).

#### MH-9 — `SectionStats` per-section ranges are never asserted · **CLOSED** (2026-06-17) · gates per-section refactors, **MR-4** (bounds-in-stats)

- **Blind:** `MeshGenerationJob` writes `MeshDataJobOutput.SectionStats` — the per-section vertex/triangle
  start+count ranges `SectionRenderer` uses to slice submeshes — but `StructuralInvariants` checks only global
  stream lengths and triangle-index ranges, never that the section ranges tile the streams without gap or
  overlap. A refactor that mis-partitions sections (MR-5/MR-6 work, or MR-4's proposed per-section bounds added
  to `MeshSectionStats`) passes green.
- **Closed by:** `MeshAssert.StructuralInvariants` now walks `SectionStats` per stream (vertices + all three
  triangle lists) via `CheckSectionTiling`, asserting every emitting section's `[start, start+count)` range is
  contiguous, non-overlapping, and sums to the stream length. Zero-count sections (skipped → written as
  `default`) are ignored, matching the job's actual contract. New baseline **B9** places one isolated cube per
  section (3 emitting sections) so the tiling check is non-vacuous, with a positive control asserting ≥2
  sections emitted.
- **Effort:** 🟢 trivial.

### Phase 1 — Value oracles (unblock MR-2; prerequisite for MR-8)

The suite checks UV / color / light streams **only for run-to-run equality**, never against an expected value,
and runs with `SmoothLighting.Off` (light map zeroed). The streams MR-2 re-encodes are therefore unvalidated.

#### MH-3 — No smooth-lighting *value* coverage · **CLOSED** (2026-06-18) · gates **MR-2**, prereq for **MR-8**

- **Blind:** `MeshingTestWorld.Run()` defaulted to `SmoothLightingQuality.Off` with a zeroed light map, so the
  `LightData` (`Color32`, the `TexCoord1` smooth-light stream) carried no meaningful value. `MR-2`'s explicit
  acceptance criterion is "the smooth-lighting encoding in TexCoord1 must be preserved exactly" — there was no
  way to assert that. `MR-8`'s merge predicate ("merge only faces with identical corner light") also needs
  real per-corner light values to test.
- **Closed by:** `MeshingTestWorld.FillLight`/`SetLight` populate the in-chunk light map and `Run(SmoothLightingQuality.High)`
  exercises the corner-averaging path. `MeshOracle.ExpectedUniformCornerLight` is a **hand-derived** oracle:
  for a spatially *uniform* light field every one of a corner's 4 samples is equal, so the averaged result is
  `17·V` per channel **independent of which neighbors are sampled** — deriving it never references the engine's
  `CornerOffsets` LUT, avoiding the A4 shared-assumption trap. `MeshAssert.LightDataMatches` + **B11** pin two
  configs: full sunlight (→ 255 sun) and an intermediate, multi-channel blocklight (R=7→119, G=3→51, proving
  averaging + UNorm8 rounding + channel order, not a vacuous all-zero/saturated read), with an A≠B positive
  control proving the populated map drives the output.
- **Scope / future extension:** only the **uniform** (all-corners-equal) case is modelled, which pins the
  encoding `MR-2` must preserve. **Distinct-per-corner values and AO darkening** (a corner whose diagonal is
  dropped because both its sides are opaque) are **not yet** covered — predicting which corner darkens requires
  re-deriving `CornerOffsets`, the A4 trap. A follow-up should add a per-corner oracle that mirrors the
  side/side/diagonal sampling + AO rule independently (needed to *fully* guard MR-8's equal-corner-light merge
  predicate). Until then MR-8 stays gated on MH-8 + its design doc regardless.
- **Effort:** 🟡 medium.

#### MH-4 — No UV / texture *value* oracle · **CLOSED** (2026-06-17) · gates **MR-2**, prereq for **MR-8**

- **Blind:** UVs are compared only by `OutputsEqual` (determinism). The palette gives each face a distinct
  texture index (Back=0 … Right=5) *so a regression could surface*, but nothing asserts the emitted UV equals
  the expected atlas coordinate for a given face/texture. `MR-2` may shift the UV layout; `MR-8` (greedy)
  requires `Texture2DArray` UV.z layer + `frac()` tiling semantics that have no oracle.
- **Closed by:** `MeshOracle.ExpectedFaceUVs(textureID, expectedUVs)` independently re-derives the atlas-cell
  placement (the math MR-2 may restructure) from the atlas dimensions, and `MeshOracle.ExpectedTextureIDForFace`
  independently re-states the geometry-face → texture selection (a hardcoded copy of the engine's `GetTextureID`
  convention, so a divergence is caught). `MeshAssert.UVsMatch` pins the 4 per-vertex UVs; `CompareCubeFacesToOracle`
  now checks them for all 6 faces of B2 and all 4 yaws of B4 (30 face-UV checks total).
- **Scope note:** the palette emits no UV quarter-turn rotation, so `uvQuarterTurnsCW` is not modelled — a
  rotated-texture fixture would need its own oracle extension (and the engine's `RotateUvQuarterTurnsCW`
  re-derived independently). The corner-within-cell pattern is hand-defined (BL/TL/BR/TR), not read from the
  engine's `VoxelUvs` table, so a corruption of that table is caught rather than mirrored.
- **Effort:** 🟡 medium.

### Phase 2 — Pipeline-stage coverage (unblock MR-5; enable MR-3/MR-4 renderer side)

#### MH-5 — `MeshPostProcessJob` / section-space output is never run · **CLOSED** (2026-06-18) · gates **MR-5**, prereq for **MR-2**

- **Blind:** the harness asserted the **chunk-space** `MeshGenerationJob` output and stopped there. The
  chunk-space → section-space coordinate rewrite (`MeshPostProcessJob`, run via `Schedule().Complete()` in
  `Chunk.ApplyMeshData`) was entirely unguarded. `MR-5` moves *where* that job runs (chained on the mesh handle
  on a worker thread vs. a blocking main-thread `Complete()`); proving "where" doesn't change "what" requires a
  baseline on the post-processed section-space output.
- **Also gated MR-2:** `MeshPostProcessJob` is where `InterleavedStream3` (the interleaved Normal+light
  `NormalLightVertex` GPU-upload stream) is assembled — so the very vertex format MR-2 restructures is partly
  built in this stage and was **empty** in the harness.
- **Closed by:** `MeshingTestWorld.Run(postProcess: PostProcessMode.Separate|Chained)` chains the real
  `MeshPostProcessJob` wired exactly as `Chunk.ApplyMeshData` (`Separate` mirrors production's
  `genJob.Run()` → `postJob.Schedule().Complete()`; `Chained` is the MR-5 shape `postJob.Schedule(genJob.Schedule())`).
  **B10** asserts (a) section-space coord == chunk-space coord − section origin (`MeshAssert.SectionSpaceVertices`),
  (b) `InterleavedStream3[i]` == interleave of `Normals[i]`+`LightData[i]` (`MeshAssert.InterleavedMatches`), and
  (c) chained-vs-separate byte equality (`OutputsEqual` + `MeshAssert.InterleavedStreamsEqual`) — the MR-5 guard.
  Positive controls: the gen-only run's `InterleavedStream3` is empty (the post stage fills it) and ≥1 emitting
  section sits above section 0 (so the y-offset is non-identity).
- **Effort:** 🟡 medium.

#### MH-6 — No `SectionRenderer` apply-path harness · **CLOSED** (2026-06-18, buildable-now portion) · gates **MR-3**, renderer side of **MR-4**

- **Blind:** `MR-3` (cache 7 material-combination arrays, assign `sharedMaterials` only on change) and the
  *applied* side of `MR-4` (assign constant `Mesh.bounds`) live in `SectionRenderer.UpdateMeshNative`, a
  `MonoBehaviour` path the meshing-*job* suite never instantiates. They were structurally unreachable from the
  job harness.
- **Closed by:** a *separate* fixture `Framework/SectionRendererTestFixture` (NOT bolted onto `MeshingTestWorld`)
  that instantiates the real `SectionRenderer` and drives `UpdateMeshNative` with tiny synthetic `NativeArray`s
  (material selection + the active/inactive decision depend only on the three submesh `count` args — no real mesh
  job needed), observing through the public `GameObject` (`sharedMaterials`, `sharedMesh.bounds`, `activeSelf`).
  `Framework/RendererAssert` adds `MaterialsEqual` + a `BoundsContainAll(Verts)` containment check. Three baselines
  in `MeshingValidationSuite.Renderer.cs`: **B12** asserts the material array equals the correct combination per
  submesh-presence bitmask, in opaque → transparent → fluid order, across all 7 non-empty combinations (the
  load-bearing MR-3 guard); **B13** asserts the empty section (`vertexCount==0`) deactivates the GameObject and
  leaves `sharedMaterials` untouched; **B14** asserts `Mesh.bounds` *contain* every emitted vertex (a containment
  invariant — stable across MR-4; MH-1 already proved geometry fits the section cell — explicitly **NOT** a
  tight-AABB equality). Positive controls: B12 proves the 3 stub materials are distinct + two bitmasks yield
  different arrays; B13 proves a non-empty update activates + assigns (so "inactive + untouched" isn't vacuous);
  B14 a tripwire proving the containment predicate observes an out-of-bounds vertex.
- **Seam (the blocker):** `UpdateMeshNative` reaches into `World.Instance.{Opaque,Transparent,Liquid}Material`
  (null in edit mode → NRE). Resolved with **option (a) reflection-stub** — reflect the private `World.Instance`
  setter onto an `AddComponent`'d `World` (a plain `MonoBehaviour`, so no `Awake`/`OnEnable`/`OnValidate` runs in
  edit mode; the setter is driven directly, bypassing `World.Awake`) with a stub `BlockDatabase` holding 3 distinct
  dummy materials. **Zero production change** (B1–B11 untouched).
- **Build-alongside follow-ups** (NOT baselinable pre-optimization — land them in the MR-3/MR-4 PR, B8/B9/B11
  positive-control style): (1) **no-reassign-when-bitmask-unchanged** — MR-3's new postcondition (assert
  `sharedMaterials` is not reassigned across two updates with the same present-submesh bitmask) once the 7
  combinations are cached; (2) **bounds == constant section cell** — MR-4's new postcondition (assert `Mesh.bounds`
  equals the constant section-cell box) once `RecalculateBounds()` is replaced; (3) **upgrade the seam to option
  (b)** — inject `UpdateMeshNative`'s materials (or a cached material-set) instead of reaching into the singleton,
  the long-term cleaner architecture this reflection stub stands in for (do it when MR-3 / the MR-6 pooling work
  touches the signature anyway).
- **Effort:** 🟡 medium. **Note:** MH-1 covers MR-4's *geometry* premise from job output; MH-6 covers the
  renderer assignment itself.

### Phase 3 — Palette / shape breadth (close the documented custom/cross/lava blind spot)

#### MH-7 — No custom-mesh / cross-mesh block, no lava fluid · **OPEN** · gates **MR-4** caveat; named blind spot

- **Blind:** `TestMeshBlockPalette` has Air, SolidOpaque, TransparentCube, OrientedOpaque, WaterSource only —
  **no `RenderShape.Custom`/cross block and no lava fluid.** The custom-mesh job path (`CustomMeshes` /
  `CustomFaces` / `CustomVerts` / `CustomTris`, all empty arrays today) is never exercised, and the
  `LavaVertexTemplates` input is never indexed. This is the exact "custom-mesh/cross-mesh / fluid-value" gap the
  performance report calls out, and it gates MR-4's "if a custom mesh ever exceeds the unit cell" caveat (you
  cannot test it without such a block).
- **Build:** add custom-mesh (and cross-shape) entries to the palette with a small custom-mesh oracle, plus a
  lava fluid entry feeding a real lava height template. Lets B-series scenarios cover the custom/cross/lava
  routing and geometry the standard oracle can't.
- **Effort:** 🟡 medium (custom-mesh oracle is the bulk of it).

### Phase 4 — Structural rebuild (gated behind the MR-8 design doc)

#### MH-8 — Geometry oracle assumes one-quad-per-face; incompatible with greedy meshing · **OPEN** · gates **MR-8**

- **Blind:** *every* geometry assertion assumes one emitted quad per visible face — fixed vertex counts
  (24/cube), per-quad position matching, and `FindQuadByNormal` assuming a **unique** normal per quad. Greedy
  meshing (`MR-8`) merges coplanar same-texture same-lighting faces, changing vertex count and breaking all of
  those primitives at once. The current oracle cannot express the result.
- **Build:** a **merge-invariant** oracle that decomposes emitted (possibly merged) quads back into unit-face
  coverage and compares the *set of covered unit faces* — each with texture (MH-4), normal, and corner light
  (MH-3) — independent of how faces were batched. Combined with MH-3 + MH-4 this is the full prerequisite set
  for greedy meshing, which is itself blocked on its own design doc (`PERFORMANCE_IMPROVEMENTS_REPORT.md` MR-8).
- **Effort:** 🔴 high — a new oracle model, not an extension of the existing one. Do **not** start before the
  MR-8 design doc exists.

---

## 4. Out of scope (by design)

- **`Clouds.cs` mesh (MR-9).** Cloud meshing is a separate system, not chunk meshing; it would need its own
  tiny harness. Low value — likely not worth building. **WONTFIX (here)** — track under MR-9 directly.
- **Cross-chunk border-face culling.** Neighbor chunk maps are intentionally empty (`MeshingTestWorld` places
  blocks in the interior so culling only consults in-chunk neighbors). No open `MR-*` item depends on border
  culling; revisit only if one does. **OPEN · LOW.**
- **True concurrency / Burst scheduling races.** Synchronous `job.Run()` only — mirrors the lighting suite's
  B3 **WONTFIX (structural)**. MR-5's value (off-main-thread scheduling) is about *where* work runs; the
  harness verifies output equivalence (MH-5), not the threading itself.

---

## 5. Phased backlog snapshot

| Phase | Gap  | Finding                                              | Gates                       | Status         | Effort |
|-------|------|------------------------------------------------------|-----------------------------|----------------|--------|
| 0     | MH-1 | Bounds-extent assertion                              | MR-4 (premise)              | CLOSED         | 🟢     |
| 0     | MH-2 | Pooled-output stale-data guard                       | MR-6 (pool variant)         | IN-PR          | 🟢     |
| 0     | MH-9 | `SectionStats` per-section ranges asserted           | per-section refactors; MR-4 | CLOSED         | 🟢     |
| 1     | MH-3 | Smooth-lighting *value* coverage (uniform)           | MR-2; prereq MR-8           | CLOSED         | 🟡     |
| 1     | MH-4 | UV / texture *value* oracle                          | MR-2; prereq MR-8           | CLOSED         | 🟡     |
| 2     | MH-5 | `MeshPostProcessJob` / section-space output coverage | MR-5                        | CLOSED         | 🟡     |
| 2     | MH-6 | `SectionRenderer` apply-path harness                 | MR-3; MR-4 (renderer)       | CLOSED         | 🟡     |
| 3     | MH-7 | Custom/cross-mesh + lava palette & oracle            | MR-4 caveat; blind spot     | OPEN           | 🟡     |
| 4     | MH-8 | Merge-invariant geometry oracle                      | MR-8                        | OPEN           | 🔴     |

> **Wave 1 (2026-06-17):** MH-9, MH-1, MH-4 closed (baselines B1–B9 green, one commit each).
> **Wave 2 (2026-06-18):** MH-5 (B10) + MH-3 (B11) closed (baselines B1–B11 green, one commit each).
> **Wave 3 (2026-06-18):** MH-6 (B12–B14) closed — buildable-now portion (baselines B1–B14 green, one commit).
> The only remaining hard prerequisite is **MH-8** (MR-8). MH-2 stays deferred until the MR-6 pool API exists;
> MH-7 is best built alongside the custom/cross/lava work it guards.

### MR-item readiness at a glance

| MR item                   | Baselinable today?   | Needs first                                                                                            |
|---------------------------|----------------------|--------------------------------------------------------------------------------------------------------|
| MR-2 (vertex format)      | ✅                    | ~~MH-3 + MH-4 + MH-5~~ ✅ all done (encoding pinned; distinct-corner light is a future MH-3 extension)  |
| MR-3 (material caching)   | ✅                    | ~~MH-6~~ ✅ done (B12 material-combo guard; no-reassign postcondition is build-alongside)                |
| MR-4 (constant bounds)    | ✅                    | ~~MH-1 (premise)~~ ✅ + ~~MH-6 (renderer)~~ ✅ done (B14 containment); constant-cell-bounds postcondition build-alongside; MH-7 custom-mesh caveat |
| MR-5 (chain post-process) | ✅                    | ~~MH-5~~ ✅ done (B10 chained-vs-separate equality)                                                     |
| MR-6 (pre-size / pool)    | ✅ pre-size / ⚠️ pool | MH-2 (pool variant only)                                                                               |
| MR-8 (greedy meshing)     | ❌                    | MH-8 + a per-corner MH-3 extension (and its own design doc); ~~MH-4~~ ✅ done                           |
| MR-9 (clouds)             | ❌                    | out of scope (separate harness)                                                                        |

> After Wave 3, **MR-2 is fully baselinable** (MH-3 + MH-4 + MH-5 ✅), **MR-5 is unblocked** (MH-5 ✅), and
> **MR-3 + the renderer side of MR-4 are baselinable** (MH-6 ✅ — B12/B14). The only remaining **hard
> prerequisite** is MH-8 (MR-8) — a baseline cannot be written without it. MH-2 and MH-7 are still better built
> *alongside* their optimization than ahead of it. (MH-1, MH-3, MH-4, MH-5, MH-6, MH-9 are now CLOSED; MH-8/MR-8
> additionally want a per-corner light oracle beyond MH-3's uniform case.)

---

## 6. Execution waves (sequencing plan)

This is the recommended order to build the remaining gaps, grouped into **waves** — each wave is a coherent set
that leaves the suite green and unblocks a named optimization. A wave is a *sequencing* layer on top of the
phases in §3; phases say "what depends on what", waves say "do these next, in this order". Build every item
**test-first** per the `validation-driven-bugfix` skill, one **commit per MH-#**, with **all baselines green**
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

MH-9 (`SectionStats` tiling), MH-1 (bounds extent), MH-4 (UV value oracle). One commit each, baselines
**B1–B9** green. These needed **no** harness-infrastructure change — all derivable from the existing
chunk-space `MeshGenerationJob` output. Wave 2 onward is different: each item needs a **new run path or a
second job** before a baseline can exist.

### Wave 2 — unblock MR-2 + MR-5 (job-suite depth) · ✅ DONE (2026-06-18)

Theme: make the two most-blocked job-side optimizations baselinable. Order: **MH-5 first** (lower risk, wider
unblock), **MH-3 second** (riskiest; completes MR-2). Executed in that order — MH-5 (B10) then MH-3 (B11), one
commit each, baselines **B1–B11** green after each. MH-3 landed the **uniform-field** corner-light oracle only
(the encoding MR-2 needs); the per-corner/AO extension is deferred (see §3 MH-3).

1. **MH-5 — run `MeshPostProcessJob` + light up `InterleavedStream3`** (gates MR-5; half of MR-2).
   - *Investigate first:* read `MeshPostProcessJob` + `Chunk.ApplyMeshData`'s `Schedule().Complete()` wiring to
     learn its exact inputs / in-place semantics (this is the one real unknown).
   - *Build:* opt-in flag on `MeshingTestWorld.Run(...)` (e.g. `runPostProcess: true`) that chains
     `MeshGenerationJob` → `MeshPostProcessJob` and exposes the post-processed output.
   - *Baseline (≈B10):* (a) section-space coord == chunk-space coord − section origin; (b) `InterleavedStream3[i]`
     == interleave of `Normals[i]` + `LightData[i]`; (c) **chained-vs-separate equality** (the MR-5 guard:
     worker-handle chain vs. blocking `Complete()` produce byte-identical output — MR-7/B8-style differential).
   - *Risk:* 🟢 low (equality/structural, no hand-derived value oracle).
2. **MH-3 — smooth-lighting *value* oracle** (completes MR-2; prereq MR-8).
   - *Investigate first:* read `CalculateCornerLights` + the AO/light-averaging path — **do not copy it** into
     the oracle (A4-class shared-assumption trap, called out in §3 MH-3).
   - *Build:* populate `MeshingTestWorld`'s in-chunk light map + expose `Run(SmoothLightingQuality.High)`; add
     `MeshAssert.LightDataMatches` + a **hand-derived** corner-light oracle.
   - *Baseline:* a deliberately trivial, hand-computable lit config (e.g. one sky-exposed top face = full
     sunlight; a face against a lamp = known blocklight) so expected values are derivable by hand.
   - *Risk:* 🔴 highest in the wave — keep the lit config simple enough to derive independently.

   **After Wave 2, MR-2 is fully baselinable** (MH-3 + MH-4 ✅ + MH-5) and **MR-5 is unblocked** (MH-5).

### Wave 3 — renderer apply-path (separate harness) · ✅ DONE (2026-06-18)

3. **MH-6 — `SectionRenderer` apply-path fixture** (gates MR-3; renderer side of MR-4) · **CLOSED** (buildable-now
   portion). A **separate** fixture `SectionRendererTestFixture` (reflection-stub `World.Instance` seam, zero
   production change), NOT bolted onto `MeshingTestWorld`. Baselines **B12–B14** green: material-combination per
   submesh-presence bitmask (the MR-3 guard), empty-section deactivate + no-assign, and `Mesh.bounds`-contain-all
   (the MR-4 renderer containment premise; MH-1 proved the geometry premise from job output). The
   **no-reassign-when-unchanged** (MR-3) and **constant-cell-bounds** (MR-4) postconditions — plus upgrading the
   seam to option (b) production injection — are **build-alongside** follow-ups for the MR-3/MR-4 PR (see §3 MH-6),
   since they assert the post-optimization behavior and cannot be baselined ahead of it.

### Build-alongside-the-optimization (not standalone waves)

- **MH-2 — pooled-output stale-data guard** (MR-6 pool variant). 🟢 trivial, but **cannot be built until the
  MR-6 pool API exists** to test against. Build it in the same PR as that optimization. Reuses `OutputsEqual`.
- **MH-7 — custom/cross-mesh + lava palette & oracle** (MR-4 caveat; named blind spot). 🟡 medium (custom-mesh
  oracle is the bulk). No open MR *blocks* on it beyond MR-4's "custom mesh exceeds the unit cell" caveat —
  build it alongside the custom/cross/lava work it guards.

### Gated — do not start yet

- **MH-8 — merge-invariant geometry oracle** (MR-8 greedy meshing). 🔴 high — a new oracle model (decompose
  merged quads back to unit-face coverage), **not** an extension of the existing one. **Blocked on the MR-8
  design doc** (`PERFORMANCE_IMPROVEMENTS_REPORT.md` MR-8). Needs MH-4 ✅ and MH-3 ✅ (but MR-8's equal-corner-light
  merge predicate additionally wants the **per-corner / AO** MH-3 extension, beyond the uniform case shipped in B11).

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
