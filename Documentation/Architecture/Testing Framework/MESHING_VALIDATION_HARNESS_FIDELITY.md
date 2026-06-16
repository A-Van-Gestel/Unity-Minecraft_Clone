# Meshing Validation Harness — Fidelity Boundary & Extension Backlog

**Status:** 🚧 **DRAFT planning note** (not yet an accepted backlog — pending review)
**Created:** 2026-06-16
**Scope:** `Assets/Editor/Validation/Meshing/` — the `MeshingValidationSuite` + `MeshingTestWorld` +
`MeshOracle` + `MeshAssert` + `TestMeshBlockPalette` harness (menu item
**`Minecraft Clone/Dev/Validate Meshing`**).
**Sibling:** [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](LIGHTING_VALIDATION_HARNESS_FIDELITY.md) — same
document shape; the meshing suite was built test-first as that suite's younger sibling.

---

## 1. Why this document exists

The meshing validation suite (baselines **B1–B8**, all green) runs **real production code**: it executes
the actual `Jobs.MeshGenerationJob` synchronously (`job.Run()`) over a synthetic single chunk and asserts
its `MeshDataJobOutput`. It is the regression guard that lets the `MR-*` performance findings in
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

> **Two output members are *not* in the trusted core.** `MeshDataJobOutput.SectionStats` (per-section
> vertex/triangle index ranges) is written by the job but checked by neither `StructuralInvariants` nor
> `OutputsEqual` (→ **MH-9**), and `InterleavedStream3` (the Normals+light GPU-upload vertex stream) is built
> by `MeshPostProcessJob`, so it is **empty** in the harness and therefore unobservable here (→ **MH-5**).

---

## 3. Blind spots & the phased extension backlog

Gap IDs are `MH-#`, matching the analysis numbering that produced this note. Each entry states what is blind,
which `MR-*` item it gates, what to build, and effort. The phase ordering is value-for-prerequisite: a phase's
items unblock the optimization wave that depends on them.

### Phase 0 — In-PR quick wins (no new system; not prerequisites)

These are small enough to land in the same PR as the optimization they guard. Listed so they aren't mistaken
for blockers.

#### MH-1 — No bounds-extent assertion · **IN-PR** · gates **MR-4**

- **Blind:** the suite never checks the spatial extent of the emitted geometry. `MR-4` replaces the
  per-section `RecalculateBounds()` with a constant `Bounds`; its correctness criterion is "every emitted
  vertex lies within the section cell," which is directly derivable from `MeshDataJobOutput.Vertices` but has
  no assertion today.
- **Build:** `MeshAssert.BoundsWithin(label, o, min, max)` — compute the vertex AABB, assert it is contained
  in the section's unit-cell-derived box. Add to B2/B4 and any custom-mesh scenario.
- **Effort:** 🟢 trivial. **Note:** the MR-4 *change* lives in `SectionRenderer`, not the job — this assertion
  proves the *premise* (geometry fits the constant bounds); the renderer-side change still needs MH-6.

#### MH-2 — No pooled-output stale-data guard · **IN-PR** · gates **MR-6** (pooling variant)

- **Blind:** every `Run()` allocates a fresh `MeshDataJobOutput`. The `MR-6` *pre-size* variant is already
  covered (vertex-count + `OutputsEqual` prove output unchanged), but the *pool the output struct* variant
  introduces a reuse-across-jobs lifecycle where a `Clear()`-but-not-fully-reset buffer could leak stale
  vertices — exactly the failure class B8 guards for the fluid neighbor buffer.
- **Build:** a scenario that runs scene A then scene B through **one** reused output instance and asserts B's
  result is byte-identical to a fresh-buffer B run. Reuses existing `OutputsEqual`.
- **Effort:** 🟢 trivial (once the pooling API exists to test against).

#### MH-9 — `SectionStats` per-section ranges are never asserted · **IN-PR** · gates per-section refactors, **MR-4** (bounds-in-stats)

- **Blind:** `MeshGenerationJob` writes `MeshDataJobOutput.SectionStats` — the per-section vertex/triangle
  start+count ranges `SectionRenderer` uses to slice submeshes — but `StructuralInvariants` checks only global
  stream lengths and triangle-index ranges, never that the section ranges tile the streams without gap or
  overlap. A refactor that mis-partitions sections (MR-5/MR-6 work, or MR-4's proposed per-section bounds added
  to `MeshSectionStats`) passes green.
- **Build:** extend `MeshAssert.StructuralInvariants` to assert the `SectionStats` ranges are contiguous,
  non-overlapping, and sum to the stream lengths.
- **Effort:** 🟢 trivial.

### Phase 1 — Value oracles (unblock MR-2; prerequisite for MR-8)

The suite checks UV / color / light streams **only for run-to-run equality**, never against an expected value,
and runs with `SmoothLighting.Off` (light map zeroed). The streams MR-2 re-encodes are therefore unvalidated.

#### MH-3 — No smooth-lighting *value* coverage · **OPEN** · gates **MR-2**, prereq for **MR-8**

- **Blind:** `MeshingTestWorld.Run()` defaults to `SmoothLightingQuality.Off` with a zeroed light map, so the
  `LightData` (`Color32`, the `TexCoord1` smooth-light stream) carries no meaningful value. `MR-2`'s explicit
  acceptance criterion is "the smooth-lighting encoding in TexCoord1 must be preserved exactly" — there is no
  way to assert that today. `MR-8`'s merge predicate ("merge only faces with identical corner light") also
  needs real per-corner light values to test.
- **Build:** (a) populate the in-chunk light map and expose a `Run(SmoothLightingQuality.High)` path in the
  harness; (b) a per-vertex light-value oracle/assertion (`MeshAssert.LightDataMatches`) pinning expected
  corner light for a known lit configuration. Pairs naturally with placing a lamp/sky-exposed column.
- **Effort:** 🟡 medium. **Risk note:** the light-value oracle must be hand-derived, not a copy of the
  engine's packing, or it inherits the A4-class shared-assumption blind spot the lighting suite documents.

#### MH-4 — No UV / texture *value* oracle · **OPEN** · gates **MR-2**, prereq for **MR-8**

- **Blind:** UVs are compared only by `OutputsEqual` (determinism). The palette gives each face a distinct
  texture index (Back=0 … Right=5) *so a regression could surface*, but nothing asserts the emitted UV equals
  the expected atlas coordinate for a given face/texture. `MR-2` may shift the UV layout; `MR-8` (greedy)
  requires `Texture2DArray` UV.z layer + `frac()` tiling semantics that have no oracle.
- **Build:** `MeshOracle.ExpectedFaceUVs(face, textureID, uvQuarterTurnsCW)` + `MeshAssert.UVsMatch`, extending
  the existing per-face compare in `CompareCubeFacesToOracle` to also check the UV stream.
- **Effort:** 🟡 medium.

### Phase 2 — Pipeline-stage coverage (unblock MR-5; enable MR-3/MR-4 renderer side)

#### MH-5 — `MeshPostProcessJob` / section-space output is never run · **OPEN** · gates **MR-5**, prereq for **MR-2**

- **Blind:** the harness asserts the **chunk-space** `MeshGenerationJob` output and stops there. The
  chunk-space → section-space coordinate rewrite (`MeshPostProcessJob`, run via `Schedule().Complete()` in
  `Chunk.ApplyMeshData`) is entirely unguarded. `MR-5` moves *where* that job runs (chained on the mesh handle
  on a worker thread vs. a blocking main-thread `Complete()`); proving "where" doesn't change "what" requires a
  baseline on the post-processed section-space output, which does not exist.
- **Also gates MR-2:** `MeshPostProcessJob` is where `InterleavedStream3` (the interleaved Normal+light
  `NormalLightVertex` GPU-upload stream) is assembled — so the very vertex format MR-2 restructures is partly
  built in this unguarded stage and is **empty** in the harness today. MR-2 cannot be fully baselined until this
  stream is produced and asserted here, on top of MH-3/MH-4.
- **Build:** extend `MeshingTestWorld.Run()` with an opt-in flag that chains and runs `MeshPostProcessJob`,
  plus assertions on section-space coordinates, on `InterleavedStream3` (= the interleave of `Normals` +
  `LightData`), and an equality check (chained vs. separate run produce identical output). This also
  retroactively closes the currently-dark post-process stage.
- **Effort:** 🟡 medium.

#### MH-6 — No `SectionRenderer` apply-path harness · **OPEN** · gates **MR-3**, renderer side of **MR-4**

- **Blind:** `MR-3` (cache 7 material-combination arrays, assign `sharedMaterials` only on change) and the
  *applied* side of `MR-4` (assign constant `Mesh.bounds`) live in `SectionRenderer.UpdateMeshNative`, a
  `MonoBehaviour` path the meshing-*job* suite never instantiates. They are structurally unreachable from the
  current harness.
- **Build:** a separate, lightweight renderer fixture asserting (a) material-bitmask → `sharedMaterials`
  selection and the no-reassign-when-unchanged behavior, and (b) the constant bounds assignment. This is a
  different harness from the job suite — flag it as such so it isn't bolted onto `MeshingTestWorld`.
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

| Phase | Gap  | Finding                                              | Gates                       | Status | Effort |
|-------|------|------------------------------------------------------|-----------------------------|--------|--------|
| 0     | MH-1 | Bounds-extent assertion                              | MR-4 (premise)              | IN-PR  | 🟢     |
| 0     | MH-2 | Pooled-output stale-data guard                       | MR-6 (pool variant)         | IN-PR  | 🟢     |
| 0     | MH-9 | `SectionStats` per-section ranges asserted           | per-section refactors; MR-4 | IN-PR  | 🟢     |
| 1     | MH-3 | Smooth-lighting *value* coverage                     | MR-2; prereq MR-8           | OPEN   | 🟡     |
| 1     | MH-4 | UV / texture *value* oracle                          | MR-2; prereq MR-8           | OPEN   | 🟡     |
| 2     | MH-5 | `MeshPostProcessJob` / section-space output coverage | MR-5                        | OPEN   | 🟡     |
| 2     | MH-6 | `SectionRenderer` apply-path harness                 | MR-3; MR-4 (renderer)       | OPEN   | 🟡     |
| 3     | MH-7 | Custom/cross-mesh + lava palette & oracle            | MR-4 caveat; blind spot     | OPEN   | 🟡     |
| 4     | MH-8 | Merge-invariant geometry oracle                      | MR-8                        | OPEN   | 🔴     |

### MR-item readiness at a glance

| MR item                   | Baselinable today?   | Needs first                                                                                        |
|---------------------------|----------------------|----------------------------------------------------------------------------------------------------|
| MR-2 (vertex format)      | ❌                    | MH-3 + MH-4 + MH-5 (`InterleavedStream3` is post-process-built)                                    |
| MR-3 (material caching)   | ❌                    | MH-6                                                                                               |
| MR-4 (constant bounds)    | ⚠️ partial           | MH-1 (premise) + MH-6 (renderer); MH-7 custom-mesh caveat; MH-9 if bounds move into `SectionStats` |
| MR-5 (chain post-process) | ❌                    | MH-5                                                                                               |
| MR-6 (pre-size / pool)    | ✅ pre-size / ⚠️ pool | MH-2 (pool variant only)                                                                           |
| MR-8 (greedy meshing)     | ❌                    | MH-8 + MH-3 + MH-4 (and its own design doc)                                                        |
| MR-9 (clouds)             | ❌                    | out of scope (separate harness)                                                                    |

> Only MH-3/MH-4/MH-5 (MR-2), MH-5 (MR-5), MH-6 (MR-3), and MH-8 (MR-8) are **hard prerequisites** — a baseline
> cannot be written without them. MH-1, MH-2, MH-7, and MH-9 are better built *alongside* their optimization
> than ahead of it.

---

## 6. Cross-references

- Optimization backlog the gaps gate: [PERFORMANCE_IMPROVEMENTS_REPORT.md](../../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) (§Meshing & Rendering, §Verification)
- Sibling harness doc & status-tag conventions: [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
- Meshing architecture: [SUB_CHUNK_MESHING_ARCHITECTURE.md](../SUB_CHUNK_MESHING_ARCHITECTURE.md)
- Harness file map, API cheat sheet & MR-* guard pattern: `.agents/skills/validation-driven-bugfix/references/meshing-suite.md`
- Test-first workflow (lifecycle, taxonomy, pitfalls): `.agents/skills/validation-driven-bugfix/SKILL.md`
- Harness source: `Assets/Editor/Validation/Meshing/`
