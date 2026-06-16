# Reference Implementation: the Meshing Validation Suite

Everything lives under `Assets/Editor/Validation/Meshing/`. Menu item: **`Minecraft Clone/Dev/Validate Meshing`**.

> **Different use case from the lighting suite.** This suite's primary job is a **performance-refactor
> regression guard** for the `MR-*` items in `Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`, not
> (yet) bug reproduction. The oracle therefore encodes the **current pre-optimization ground truth** that an
> output-preserving optimization must not change (e.g. B1/B4 pin the rotated-vertex math against
> `Quaternion.Euler`, the formula MR-1 replaced). It has no known-bug (`K`) scenarios yet — `AddKnownBugScenarios`
> is registered but empty, and there is no `MESHING_BUGS.md` (create one only when the first meshing bug is
> documented test-first). All scenarios are baselines today.

## File map

| File                                 | Role                                                                                                                                                         |
|--------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `MeshingValidationSuite.cs`          | Runner: `Scenario` struct (`Name`, `Func<bool> Run`, `KnownBugId`), partial-method registration, try/catch per scenario, categorized summary                 |
| `MeshingValidationSuite.Baseline.cs` | `B1`–`B8` regression scenarios (must stay green) + probe helpers (`CollectProbeQuads`, `CompareCubeFacesToOracle`, `FindQuadByNormal`)                       |
| `Framework/MeshingTestWorld.cs`      | Harness core: a single synthetic chunk (`uint` voxel map + test palette), runs the **real** `MeshGenerationJob` via `job.Run()`, exposes `MeshDataJobOutput` |
| `Framework/MeshOracle.cs`            | Independent spec for standard-cube face geometry (4 verts + normal) per face × rotation; reuses `VoxelHelper` face-translation tables                        |
| `Framework/MeshAssert.cs`            | `QuadMatchesOracle`, `VertexCount`, `StructuralInvariants`, `OutputsEqual`, `IsTrue` — all with bounded per-element diffs; `VertexEpsilon = 1e-4f`           |
| `Framework/TestMeshBlockPalette.cs`  | Synthetic fixtures: Air(0), SolidOpaque(1), TransparentCube(2, renderNeighborFaces), OrientedOpaque(3, HorizontalOnly), WaterSource(4, WaterLike/8 flow)     |

Namespace: suite = `Editor.Validation.Meshing`, framework = `Editor.Validation.Meshing.Framework`.

## The unit under test

`Jobs.MeshGenerationJob` (the standard-cube + fluid path) runs **for real** — no reimplementation. The
harness mirrors the production / `MeshGenerationBenchmark` wiring exactly: default sections
(`IsEmpty=false`, `IsFullySolid=false`) force the per-voxel iteration path; light maps + neighbor/custom
arrays are constructed-but-empty just as the benchmark leaves them; the water height template is the **real**
16-entry table built from `FluidMeshData.BuildVertexHeightTemplate` (the same source of truth
`FluidDataGenerator` bakes into the asset), so the fluid path indexes exactly what it does in production.

> ⚠️ **Before authoring a scenario, know the harness's blind spots.** A green suite does NOT prove an
> un-modelled area is correct. The harness runs **interior blocks only** (neighbor chunk maps are empty, so
> border-face culling is untested), defaults to `SmoothLightingQuality.Off` (so UV/color/**light values** are
> only checked for run-to-run *determinism*, never against an expected value), stops at the **chunk-space**
> `MeshGenerationJob` output (`MeshPostProcessJob`'s section-space rewrite is never run), and has no
> custom/cross-mesh block or lava in the palette. The phased plan to close these (`MH-1..MH-8`, keyed to which
> `MR-*` item each gates) is in
> [MESHING_VALIDATION_HARNESS_FIDELITY.md](../../../../Documentation/Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).

## Harness API cheat sheet

```csharp
using MeshingTestWorld world = new MeshingTestWorld();   // all-air chunk + test palette
// Authoring (chunk-local coords; place in the INTERIOR so empty neighbor maps never influence culling):
world.SetBlock(x, y, z, TestMeshBlockPalette.SolidOpaque);          // optional `meta:` byte
world.SetBlock(x, y, z, TestMeshBlockPalette.OrientedOpaque, meta: yaw); // HorizontalOnly: 0=N,1=S,2=W,3=E
world.Clear();                                                       // reset every voxel to Air (reuse across yaws)
// Execution (runs the REAL MeshGenerationJob synchronously):
MeshDataJobOutput o = world.Run();                                  // defaults SmoothLightingQuality.Off
// MeshDataJobOutput streams (per-vertex unless noted):
//   o.Vertices (Vector3) · o.Normals (Vector3) · o.Uvs (Vector4: xy atlas/flow, zw fluid shore-push)
//   o.Colors (Color: fluid encodes type/shore here, white otherwise) · o.LightData (Color32, TexCoord1 UNorm8)
//   o.Triangles · o.TransparentTriangles · o.FluidTriangles (submesh index lists)
//   o.SectionStats (per-section vert/tri start+count ranges) — populated, but NOT asserted by the suite
//   o.InterleavedStream3 (Normals+LightData for GPU upload) — built by MeshPostProcessJob, so EMPTY here
// Assertions:
MeshAssert.VertexCount("label", o, 24);
MeshAssert.StructuralInvariants("label", o);                        // stream lengths consistent, tris in range & %3
MeshAssert.OutputsEqual("label", a, b);                             // full byte-for-byte determinism guard
CompareCubeFacesToOracle("label", o, pos, orientation, rotation);   // matches each of 6 faces to MeshOracle by normal
```

`MeshOracle` helpers: `ExpectedStandardCubeFace(face, rotation, pos, verts, out normal)`,
`LegacyOrientationForYaw(yaw)`, `RotationForYaw(yaw)`, `TranslatedFace(worldFace, orientation)`.

## Worked examples to copy from

- **Plain geometry-vs-oracle:** `B2` (single opaque cube → 24 verts → 6 faces match oracle by normal).
- **End-to-end through metadata path:** `B4` (HorizontalOnly oriented cube, loop 4 yaws via `Clear()` + `SetBlock(meta:yaw)`, compare to the rotated oracle — the MR-1 guard).
- **Isolated-math differential:** `B1` (calls `VoxelMeshHelper.GenerateStandardCubeFace` directly for 6 faces × 4 angles vs `MeshOracle` — tightest MR-1 guard, no surrounding job).
- **Occlusion / no-geometry:** `B3` (cube fully shelled by opaque → derived vertex count, with a palette-assumption guard so a fixture edit fails loudly instead of silently invalidating the magic constant).
- **Submesh routing:** `B6` (transparent → all faces in `TransparentTriangles`, opaque list empty), `B7` (pure-water pool → all in `FluidTriangles`).
- **Determinism:** `B5` (mesh the same mixed scene in two worlds → `OutputsEqual`).
- **Reused-buffer hazard differential (the MR-7 guard):** `B8` — an air-surrounded fluid probe must emit byte-identical geometry whether or not solid-encased fluid "primers" were meshed *before* it (lower flattened index). Pattern for any "hoisted/pooled buffer must reset between iterations" optimization: a reference run, a primed differential run, and a positive control proving the tripwire can actually observe a leak (here a wall-boxed probe whose shore mask MUST be non-zero).

## MR-* regression-guard pattern (the suite's main job)

1. Capture the meshing benchmark baseline (`Performance/README.md`) for the pattern the optimization targets.
2. Ensure the relevant baseline(s) are green **before** the change (the oracle encodes current truth).
3. Apply the optimization. Keep **all** baselines green — a red baseline means the "output-preserving" claim is false.
4. Re-benchmark; record the before/after in the report's MR entry (mark DONE even if the win is within noise, so a dead-end idea isn't re-proposed — see MR-1's record).
5. If the path you're optimizing is a **blind spot** (fluid/custom/cross geometry, UV/light values, post-process, renderer), build the guard from `MESHING_VALIDATION_HARNESS_FIDELITY.md`'s `MH-*` backlog **first** — a green suite over an un-asserted stream proves nothing.

## Meshing-specific gotchas

- **Interior placement only.** Neighbor chunk maps are empty (`Length == 0` = "no neighbor" = face drawn). A block on a chunk border would have its border faces drawn unconditionally; no scenario should rely on cross-chunk culling until that's modelled (MH blind spot).
- **`SmoothLightingQuality.Off` is the default** and zeroes the light map — geometry is light-independent there, but it means `o.LightData` / `o.Colors` / `o.Uvs` *values* are meaningless to assert. `OutputsEqual` only proves they're *deterministic*, not *correct*. Validating values needs the MH-3/MH-4 oracles.
- **Test palette IDs are local array indices, NOT `BlockIDs`.** This is deliberate (deterministic under `BlockDatabase.asset` edits, can express shapes the real DB lacks) and does not violate the `BlockIDs`-constants rule — same exemption as the lighting suite's `TestBlockPalette`.
- **Compare faces by normal, not emission order** (`FindQuadByNormal`): an isolated cube emits 6 quads with 6 distinct axis normals, so a future reorder of the face loop can't silently misalign the comparison. (This same assumption is exactly what greedy meshing / MR-8 breaks — see MH-8.)
- **Fluid needs the real height template.** `MeshingTestWorld` builds the 16-entry water template; an empty array would index out of range in the fluid path. There is no lava template (MH-7).
- **`MeshPostProcessJob` is not run** — the suite asserts chunk-space coordinates. Don't assume section-space output is guarded; the interleaved GPU-upload stream is built there too, so `o.InterleavedStream3` is **empty** in the harness (MH-5).
- **`o.SectionStats` is populated but unasserted.** `MeshGenerationJob` writes per-section vert/tri start+count ranges, but neither `StructuralInvariants` nor `OutputsEqual` checks them — a refactor that mis-partitions sections passes green (MH-9).
