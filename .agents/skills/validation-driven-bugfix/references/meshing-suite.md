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
| `MeshingValidationSuite.Baseline.cs` | `B1`–`B11` regression scenarios (must stay green) + probe helpers (`CollectProbeQuads`, `CompareCubeFacesToOracle`, `FindQuadByNormal`, `SectionCellBounds`) |
| `Framework/MeshingTestWorld.cs`      | Harness core: a single synthetic chunk (`uint` voxel map + `ushort` light map + test palette), runs the **real** `MeshGenerationJob` via `job.Run()`, optionally chains the **real** `MeshPostProcessJob` (`PostProcessMode`, MH-5), exposes `MeshDataJobOutput` |
| `Framework/MeshOracle.cs`            | Independent spec for standard-cube face geometry (4 verts + normal), UV atlas cells (MH-4), and uniform smooth-light corner values (`ExpectedUniformCornerLight`, MH-3); reuses `VoxelHelper` face-translation tables |
| `Framework/MeshAssert.cs`            | `QuadMatchesOracle`, `VertexCount`, `StructuralInvariants`, `OutputsEqual`, `BoundsWithin`, `UVsMatch`, `SectionSpaceVertices`, `InterleavedMatches`, `InterleavedStreamsEqual`, `LightDataMatches`, `IsTrue` — all with bounded per-element diffs; `VertexEpsilon = 1e-4f` |
| `Framework/TestMeshBlockPalette.cs`  | Synthetic fixtures: Air(0), SolidOpaque(1), TransparentCube(2, renderNeighborFaces), OrientedOpaque(3, HorizontalOnly), WaterSource(4, WaterLike/8 flow)     |
| `Framework/SectionRendererTestFixture.cs` | **MH-6 renderer fixture** (separate from `MeshingTestWorld`): reflection-stub `World.Instance` + 3 distinct dummy materials so `UpdateMeshNative`'s material lookup resolves in edit mode (zero production change); drives the real `SectionRenderer.UpdateMeshNative` and observes via the public `GameObject` (`SharedMaterials`/`MeshBounds`/`IsActive`); `Dispose` restores `Instance` + `DestroyImmediate`s |
| `Framework/RendererAssert.cs`        | MH-6 renderer assertions: `MaterialsEqual` (ordered, by-reference identity), `BoundsContainAll`/`BoundsContainAllVerts` (containment predicate, no tightness)  |
| `MeshingValidationSuite.Renderer.cs` | `B12`–`B14` renderer apply-path baselines (MH-6), registered via the `AddRendererScenarios` partial called from `RunAll`                                       |

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
> border-face culling is untested) and has no custom/cross-mesh block or lava in the palette. Wave 1
> (2026-06-17) closed MH-1 (bounds), MH-4 (UV values), MH-9 (`SectionStats` tiling); Wave 2 (2026-06-18) closed
> MH-5 (`MeshPostProcessJob` / section-space + `InterleavedStream3`, via the opt-in `PostProcessMode` path) and
> MH-3 (smooth-light *values* — but only the **uniform-field** case; **distinct-per-corner / AO darkening
> values are still un-modelled**, a future MH-3 extension); Wave 3 (2026-06-18) closed MH-6 (the `SectionRenderer`
> apply-path, a **separate** fixture — B12–B14). The forward **execution-wave plan** (§6 — Waves 1–3 DONE;
> MH-2/MH-7 build-alongside; MH-8 gated) and the per-gap detail (`MH-2/7/8`) are in
> [MESHING_VALIDATION_HARNESS_FIDELITY.md](../../../../Documentation/Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).

## Harness API cheat sheet

```csharp
using MeshingTestWorld world = new MeshingTestWorld();   // all-air chunk + test palette
// Authoring (chunk-local coords; place in the INTERIOR so empty neighbor maps never influence culling):
world.SetBlock(x, y, z, TestMeshBlockPalette.SolidOpaque);          // optional `meta:` byte
world.SetBlock(x, y, z, TestMeshBlockPalette.OrientedOpaque, meta: yaw); // HorizontalOnly: 0=N,1=S,2=W,3=E
world.Clear();                                                       // reset every voxel to Air (reuse across yaws); does NOT touch light
world.FillLight(LightBitMapping.PackLightData(sky:15, blockR:0, blockG:0, blockB:0)); // uniform light field (MH-3)
world.SetLight(x, y, z, packed);                                    // single-voxel packed light
// Execution (runs the REAL MeshGenerationJob synchronously):
MeshDataJobOutput o = world.Run();                                  // defaults SmoothLightingQuality.Off
MeshDataJobOutput o = world.Run(SmoothLightingQuality.High);        // smooth-light corner values (MH-3)
MeshDataJobOutput o = world.Run(postProcess: PostProcessMode.Separate); // + REAL MeshPostProcessJob, production wiring (MH-5)
MeshDataJobOutput o = world.Run(postProcess: PostProcessMode.Chained);  // + post chained on the gen handle (MR-5 shape)
// MeshDataJobOutput streams (per-vertex unless noted):
//   o.Vertices (Vector3) · o.Normals (Vector3) · o.Uvs (Vector4: xy atlas/flow, zw fluid shore-push)
//   o.Colors (Color: fluid encodes type/shore here, white otherwise) · o.LightData (Color32, TexCoord1 UNorm8)
//   o.Triangles · o.TransparentTriangles · o.FluidTriangles (submesh index lists)
//   o.SectionStats (per-section vert/tri start+count ranges) — tile-checked by StructuralInvariants (MH-9)
//   o.InterleavedStream3 (Normals+LightData for GPU upload) — EMPTY unless run with a PostProcessMode (MH-5)
// Assertions:
MeshAssert.VertexCount("label", o, 24);
MeshAssert.StructuralInvariants("label", o);                        // stream lengths consistent, tris in range & %3, + SectionStats tile each stream (MH-9)
MeshAssert.OutputsEqual("label", a, b);                             // full byte-for-byte determinism guard (does NOT cover InterleavedStream3)
MeshAssert.BoundsWithin("label", o, min, max);                      // every vertex inside the box (MH-1; use SectionCellBounds(pos) for a section cell)
MeshAssert.UVsMatch("label", o.Uvs, startVert, expectedUVs);        // 4 face UVs vs MeshOracle.ExpectedFaceUVs (MH-4)
MeshAssert.SectionSpaceVertices("label", o, chunkSpaceVerts, ChunkMath.SECTION_SIZE); // post-process y-offset rewrite (MH-5)
MeshAssert.InterleavedMatches("label", o);                          // InterleavedStream3 == interleave(Normals, LightData) (MH-5)
MeshAssert.InterleavedStreamsEqual("label", a, b);                  // InterleavedStream3 chained==separate (MH-5; pair with OutputsEqual)
MeshAssert.LightDataMatches("label", o, expectedColor32);           // every vert's smooth-light value (MH-3; uniform field)
CompareCubeFacesToOracle("label", o, pos, orientation, rotation, in blockData); // matches each of 6 faces to MeshOracle by normal, checks geometry + UVs
```

`MeshOracle` helpers: `ExpectedStandardCubeFace(face, rotation, pos, verts, out normal)`,
`ExpectedFaceUVs(textureID, expectedUVs)` (MH-4, independent atlas-cell math),
`ExpectedTextureIDForFace(in blockData, faceIndex)` (MH-4, independent `GetTextureID` convention copy),
`ExpectedUniformCornerLight(sky, r, g, b)` (MH-3, hand-derived `17·V` for a uniform field — LUT-independent, NOT a `CalculateCornerLights` copy),
`LegacyOrientationForYaw(yaw)`, `RotationForYaw(yaw)`, `TranslatedFace(worldFace, orientation)`.

## Worked examples to copy from

- **Plain geometry-vs-oracle:** `B2` (single opaque cube → 24 verts → 6 faces match oracle by normal).
- **End-to-end through metadata path:** `B4` (HorizontalOnly oriented cube, loop 4 yaws via `Clear()` + `SetBlock(meta:yaw)`, compare to the rotated oracle — the MR-1 guard).
- **Isolated-math differential:** `B1` (calls `VoxelMeshHelper.GenerateStandardCubeFace` directly for 6 faces × 4 angles vs `MeshOracle` — tightest MR-1 guard, no surrounding job).
- **Occlusion / no-geometry:** `B3` (cube fully shelled by opaque → derived vertex count, with a palette-assumption guard so a fixture edit fails loudly instead of silently invalidating the magic constant).
- **Submesh routing:** `B6` (transparent → all faces in `TransparentTriangles`, opaque list empty), `B7` (pure-water pool → all in `FluidTriangles`).
- **Determinism:** `B5` (mesh the same mixed scene in two worlds → `OutputsEqual`).
- **Reused-buffer hazard differential (the MR-7 guard):** `B8` — an air-surrounded fluid probe must emit byte-identical geometry whether or not solid-encased fluid "primers" were meshed *before* it (lower flattened index). Pattern for any "hoisted/pooled buffer must reset between iterations" optimization: a reference run, a primed differential run, and a positive control proving the tripwire can actually observe a leak (here a wall-boxed probe whose shore mask MUST be non-zero).
- **Post-process / scheduling-move differential (the MR-5 guard):** `B10` — cubes in non-zero sections, meshed with `PostProcessMode.Separate` (production) vs `Chained` (MR-5 shape) must be byte-identical (incl. `InterleavedStream3`), plus section-space coord rewrite + interleave checks. Positive controls: gen-only `InterleavedStream3` is empty (post fills it) and ≥1 section above section 0 (offset non-identity).
- **Smooth-light *value* oracle (MH-3, uniform field):** `B11` — uniform `FillLight` + `Run(High)` over an isolated cube; every vert's `LightData` must equal `MeshOracle.ExpectedUniformCornerLight` (`17·V`). Two configs (full sun 255, intermediate 119/51) with an A≠B positive control. The uniform field makes the oracle independent of the engine's `CornerOffsets` sampling LUT (A4-trap avoidance).
- **Renderer material-combination guard (MH-6 / MR-3):** `B12` — `SectionRendererTestFixture` drives `UpdateMeshNative` for all 7 submesh-presence bitmasks; `sharedMaterials` must equal the present submeshes' materials in opaque→transparent→fluid order. Positive controls: the 3 stub materials are distinct + two bitmasks (opaque-only vs fluid-only) yield different arrays (so ordering can't pass with aliased materials).
- **Renderer empty-section short-circuit (MH-6):** `B13` — `vertexCount==0` deactivates the GameObject and leaves `sharedMaterials` untouched (primed transparent-only stays put). Positive control: a fresh fixture given a non-empty update activates AND assigns a different material (proving "inactive + untouched" isn't vacuous).
- **Renderer bounds containment (MH-6 / MR-4 premise):** `B14` — after a non-empty update, `sharedMesh.bounds` must CONTAIN every fed vertex (containment, NOT a tight AABB — survives MR-4's constant-cell bounds). Positive control / tripwire: a too-small box reports a vertex outside (the predicate can observe out-of-bounds), a generous box contains all.

## MR-* regression-guard pattern (the suite's main job)

1. Capture the meshing benchmark baseline (`Performance/README.md`) for the pattern the optimization targets.
2. Ensure the relevant baseline(s) are green **before** the change (the oracle encodes current truth).
3. Apply the optimization. Keep **all** baselines green — a red baseline means the "output-preserving" claim is false.
4. Re-benchmark; record the before/after in the report's MR entry (mark DONE even if the win is within noise, so a dead-end idea isn't re-proposed — see MR-1's record).
5. If the path you're optimizing is a **blind spot** (fluid/custom/cross geometry, UV/light values, post-process, renderer), build the guard from `MESHING_VALIDATION_HARNESS_FIDELITY.md`'s `MH-*` backlog **first** — a green suite over an un-asserted stream proves nothing.

## Meshing-specific gotchas

- **Interior placement only.** Neighbor chunk maps are empty (`Length == 0` = "no neighbor" = face drawn). A block on a chunk border would have its border faces drawn unconditionally; no scenario should rely on cross-chunk culling until that's modelled (MH blind spot).
- **`SmoothLightingQuality.Off` is the default** with a zeroed light map. For smooth-light value tests use `FillLight(...)` + `Run(SmoothLightingQuality.High)` and the MH-3 oracle (`MeshOracle.ExpectedUniformCornerLight`) — but only a **uniform** light field is oracle-covered (distinct-per-corner / AO values are NOT, a future MH-3 extension; predicting which corner darkens needs the engine's `CornerOffsets` LUT = A4 trap). `o.Colors` *values* (fluid type/shore) are still only determinism-checked. UVs are value-checked via MH-4.
- **Test palette IDs are local array indices, NOT `BlockIDs`.** This is deliberate (deterministic under `BlockDatabase.asset` edits, can express shapes the real DB lacks) and does not violate the `BlockIDs`-constants rule — same exemption as the lighting suite's `TestBlockPalette`.
- **Compare faces by normal, not emission order** (`FindQuadByNormal`): an isolated cube emits 6 quads with 6 distinct axis normals, so a future reorder of the face loop can't silently misalign the comparison. (This same assumption is exactly what greedy meshing / MR-8 breaks — see MH-8.)
- **Fluid needs the real height template.** `MeshingTestWorld` builds the 16-entry water template; an empty array would index out of range in the fluid path. There is no lava template (MH-7).
- **`MeshPostProcessJob` runs only on the opt-in path** (MH-5). A plain `Run()` asserts **chunk-space** coordinates and leaves `o.InterleavedStream3` **empty**; pass `PostProcessMode.Separate`/`Chained` to chain the real post-process (section-space rewrite + populated `InterleavedStream3`). Note `OutputsEqual` does **not** compare `InterleavedStream3` — use `InterleavedStreamsEqual` alongside it for a full post-processed equality.
- **`o.SectionStats` is now tile-checked (MH-9).** `StructuralInvariants` walks the per-section vert/tri start+count ranges and asserts they tile each stream contiguously (B9 exercises multi-section). A refactor that mis-partitions sections now fails. Note skipped/empty sections are written as `default` (start 0 / count 0) and are correctly ignored — the check chains only count > 0 sections.
- **The renderer apply-path uses a SEPARATE fixture (MH-6), not `MeshingTestWorld`.** `SectionRendererTestFixture` reflection-stubs the private `World.Instance` setter onto an `AddComponent`'d `World` with 3 distinct dummy materials, so `UpdateMeshNative`'s `World.Instance.{Opaque,Transparent,Liquid}Material` lookup resolves in edit mode (it NREs otherwise). `World` is a plain `MonoBehaviour`, so `AddComponent` runs no lifecycle and `Awake` never fires — zero production change. Material selection + active/inactive depend only on the three submesh `count` args, so the synthetic `NativeArray`s need no real geometry. **Build-alongside follow-ups** (not yet baselined, can't be pre-optimization): no-reassign-when-bitmask-unchanged (MR-3), bounds==constant-section-cell (MR-4), and upgrading the seam to option (b) production injection.
