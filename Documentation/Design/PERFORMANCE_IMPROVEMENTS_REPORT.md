# Performance Improvements Report

> The single master backlog for **all open runtime performance improvements** in the VoxelEngine.
> Every finding shows, at a glance: the affected system, implementation effort, regression risk,
> expected benefit, and whether it can affect world-generation determinism (seed) or the on-disk
> save format.
>
> Status: **Open backlog.** Items are removed (archived) when implemented and verified.

**Last audited:** 2026-06-12, at commit `39c92ef` (branch `feat/Modular-World-Generation-&-World-Types`).
**Implementation status synced:** 2026-06-20, at commit `ea2aec0` — all Meshing & Rendering items
except MR-8 (greedy meshing) are now closed and in-game confirmed (MR-1 through MR-7, MR-9).
Findings are from static code review unless stated otherwise — capture a baseline per
`Documentation/Performance/README.md` before implementing the larger items.

**Audit scope note (second pass, 2026-06-12):** the `GS-*` (GPU & Shaders) and `OM-*` (CPU-starved
device / OOM hardening) sections were added after a second review pass targeting two gaps: shader/GPU
cost was previously unexamined, and the engine's behavior on CPU-starved hardware (e.g. midrange
Android) where work production outpaces consumption until the process is killed out-of-memory —
observed during benchmark/stress runs with fast movement. The `OM-*` items are the *consumption-side
and ceiling-side* complement to `P-4` (production-side backpressure in the pipeline doc §3): P-4
stops over-scheduling, OM-* makes sure that even when the backlog wins, the result is degradation
instead of a crash.

**Relationship to other documents:**

- `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` — deep-dive analysis of the chunk generation → lighting →
  meshing *pipeline* (per-job copies, backpressure, edge-check cascade), including implementation and
  incident history. Its open items are **summarized in the master table below (IDs `P-*`)** but their
  full analysis stays in that document — read it before implementing any `P-*` item.
- `CODEBASE_IMPROVEMENTS.md` — non-performance modernization backlog (API cleanups). All performance
  items formerly tracked there have been **absorbed into this report** (IDs noted per entry).
- `Documentation/Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md` — historical record of completed items.
- `Guides/GENERAL_OPTIMIZATION_GUIDE.md` — the *techniques* reference (pooling, stackalloc, inlining).
  This report tracks *specific instances* in the codebase where those techniques are not yet applied.
- `WORLD_SCALING_ANALYSIS.md` — architectural analysis for world height/depth increases, negative
  quadrants / infinite XZ, cubic chunks, and floating origin. Several items in this report (`P-2`,
  `P-4`, `LI-1`, `OM-1`/`OM-2`) are prerequisites for that work and should be designed with its
  requirements in mind (3D-keyed, halo-padded storage; height-parameterized budgets) — see its §6.

---

## Legend

| Field       | Values                                                                                                                                                        |
|-------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                                           |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants, lighting semantics, or shaders) |
| **Benefit** | 🟢 High (measurable frame-time/GC win in normal play) · 🟡 Medium (situational or smaller win) · ⚪ Low (cleanliness/scalability, negligible today)            |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ — see entry (changes some runtime-deterministic behavior, but never terrain)                   |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill)                |

> **Seed-breaking note:** None of the items in this report modify world-generation noise, biome
> selection, structure placement, or any code in the generation jobs. **No item can change the
> terrain produced by a given seed.** The ⚠️ markers under *Seed* flag changes to *runtime* RNG or
> lighting determinism only, with details in the entry.

---

## Master summary table

### Meshing & Rendering

| ID     | Finding                                                           | Effort | Risk | Benefit | Seed | Save |
|--------|-------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| MR-1 ✅ | Per-vertex `Quaternion.Euler` in standard cube face generation    |   🟢   |  🟢  |   🟡¹   |  ✅   |  ✅   |
| MR-2 ✅ | 60-byte vertex format with a near-constant 16-byte color stream   |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| MR-3 ✅ | `new Material[3]` + `sharedMaterials` set per section mesh update |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-4 ✅ | `RecalculateBounds()` per section update despite known bounds     |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-5 ✅ | `MeshPostProcessJob` blocks the main thread per chunk apply       |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-6 ✅ | Mesh output `NativeList`s start at default capacity               |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| MR-7 ✅ | Per-fluid-voxel `Allocator.Temp` arrays in the meshing job        |   🟢   |  🟢  |   🟢²   |  ✅   |  ✅   |
| MR-8   | Greedy meshing (coplanar quad merging)                            |   🔴   |  🔴  |   🟢    |  ✅   |  ✅   |
| MR-9 ✅ | `Clouds.cs` legacy mesh API with `.ToArray()`                     |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |

> ¹ MR-1 benefit downgraded 🟢→🟡 after measurement: implemented and suite-guarded, but the
> throughput delta is within the benchmark's noise floor — a correctness/cleanliness win, not a
> measurable speedup. See the MR-1 detail section for the before/after table.
>
> ² MR-7 benefit confirmed 🟢 by measurement: **−18% on the fluid pattern** (1365 → 1115 μs/chunk),
> controls flat — a real fluid-path win. See the MR-7 detail section.

### Lighting

| ID   | Finding                                                                                                                                          | Effort | Risk | Benefit | Seed | Save |
|------|--------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| LI-1 | ✅ Branchy 9-map dispatch + hashmap cache → halo-padded volume; layout validated, **shipped net-positive via P-2 Phase 1** (worker-thread gather) |   🟡   |  🟡  |   🟢    |  ⚠️  |  ✅   |

### Tick & Gameplay

| ID     | Finding                                                                                | Effort | Risk | Benefit | Seed | Save |
|--------|----------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| TG-1   | Double voxel lookup + float-path cross-chunk queries per tick                          |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| TG-2 ✅ | `OnDataPopulated` full-chunk scan through managed `BlockType`s                         |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| TG-3 ✅ | `UnityEngine.Random` → `Unity.Mathematics.Random` in behaviors                         |   🟢   |  🟢  |   🟡    |  ⚠️  |  ✅   |
| TG-4   | `BlockBehavior` data separation (ECS/DOTS pattern)                                     |   🔴   |  🔴  |   🟢    |  ✅   |  ✅   |
| TG-5   | `BlockBehavior` Burst function pointers (lighter alt. to TG-4)                         |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |
| TG-6   | Per-chunk `ActiveVoxels` `NativeList<int>` alloc/free churn — pool it (TG-2 follow-up) |   🟡   |  🟡  |   🟡    |  ✅   |  ✅   |

### Main Thread & Miscellaneous

| ID   | Finding                                                    | Effort | Risk | Benefit | Seed | Save |
|------|------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| MT-1 | `List.Insert(0)` / `RemoveAt(i)` O(n) mesh priority queue  |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| MT-2 | Light scheduler snapshots the full dirty set every frame   |   🟢   |  🟡  |   🟡    |  ✅   |  ✅   |
| MT-3 | `DebugScreen` intermediate string allocations per refresh  |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| MT-4 | Startup `List.Contains`/`.IndexOf` O(n) custom-mesh lookup |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| MT-5 | Startup `.ToArray()` intermediates feeding `NativeArray`   |   🟢   |  🟢  |    ⚪    |  ✅   |  ✅   |
| MT-6 | `CompressionFactory` "GZip" actually writes raw Deflate    |   🟢   |  🟢  |    ⚪    |  ✅   |  ⚠️  |

### GPU & Shaders

| ID   | Finding                                                                           | Effort | Risk | Benefit | Seed | Save |
|------|-----------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| GS-1 | Liquid shader: per-pixel procedural 3D simplex FBM (up to ~30 snoise calls/px)    |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| GS-2 | URP Opaque Texture required globally; `SampleSceneColor` even with refraction off |   🟢   |  🟡  |   🟢    |  ✅   |  ✅   |
| GS-3 | Voxel lighting math (4× `pow`) runs per-fragment on per-vertex data               |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| GS-4 | Render pipeline tier audit: shadow variants, TwoSided casting, MSAA, render scale |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| GS-5 | Section occlusion culling (underground sections render despite being sealed)      |   🔴   |  🟡  |   🟢    |  ✅   |  ✅   |

### CPU-Starved Device / OOM Hardening

| ID   | Finding                                                                               | Effort | Risk | Benefit | Seed | Save |
|------|---------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| OM-1 | All budgets/caps are desktop-tuned absolute constants — no device-tier scaling        |   🟢   |  🟢  |   🟢    |  ✅   |  ✅   |
| OM-2 | No memory-pressure response: `Application.lowMemory` unused, no resident-chunk budget |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| OM-3 | Unbounded concurrent chunk saves on mass unload (one `Task` per chunk)                |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |

### Chunk Pipeline (deep-dive in `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`)

These remain fully documented in the pipeline analysis — the table is reproduced here so this report
is the single at-a-glance view. **Read that document (and the `chunk-lifecycle` skill) before
implementing any of these.**

| ID  | Finding (doc section)                                                                                                                                                                                                                                  | Effort | Risk  | Benefit | Seed |   Save    |
|-----|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|:-----:|:-------:|:----:|:---------:|
| P-1 | Border-slab copies instead of full-volume snapshots (§1.2)                                                                                                                                                                                             |   🟡   |  🟡   |   🟢    |  ✅   |     ✅     |
| P-2 | ✅ Worker-thread gather (Layer 1) **SHIPPED 2026-06-22** (banks the LI-1 win, −34/−50 % vs LI-1 POST) + optional persistent zero-copy storage (Layer 2, §1.3, 🔴 profiler-gated — **not** triggered) — **[design doc](PERSISTENT_CHUNK_STORAGE_P2.md)** |  ✅→🔴  | ✅→🔴  |   🟢    |  ✅   |     ✅     |
| P-3 | Jobified lighting merge in `ApplyLightingJobResult` (§2)                                                                                                                                                                                               |   🟡   |  🟡   |   🟢    |  ✅   |     ✅     |
| P-4 | Backpressure: in-flight caps, out-of-range discard, time budgets, panic gate (§3)                                                                                                                                                                      |   🟡   | 🟡→🔴 |   🟢    |  ✅   |     ✅     |
| P-5 | "Lighting stable" save bit to skip edge checks on load (§4.4)                                                                                                                                                                                          |   🟡   |  🟡   |   🟢    |  ✅   | ⚠️ Format |
| P-6 | Smaller observations: O(n) removals, fail-safe scan counter, draw-queue trickle (§5)                                                                                                                                                                   |   🟢   |  🟢   |   🟡    |  ✅   |     ✅     |

---

## Detailed findings — Meshing & Rendering

### MR-1. ✅ DONE (2026-06-15) — Per-vertex `Quaternion.Euler` in standard cube face generation

> **Closed:** implemented, suite-guarded (`B1`/`B4`), benchmarked, and visually confirmed in-game
> (rotated blocks orient correctly at all yaws). Outcome: **marginal — throughput delta within the
> benchmark noise floor**; kept as a correctness/cleanliness win, not a measured speedup. Retained
> here (not deleted) so the dead-end "hoist for a big win" idea isn't re-proposed. Full record below.

**Observed:** `VoxelMeshHelper.GenerateStandardCubeFace` (`VoxelMeshHelper.cs` ~line 194) computes
`Quaternion.Euler(0, rotation, 0)` and a quaternion-vector multiply **inside the 4-vertex loop**,
for **every face of every standard cube voxel** — including the overwhelming majority of blocks
where `rotation == 0`. That is trigonometry plus quaternion math per vertex, in the hottest loop of
the engine. (The remarks in `MeshGenerationJob.GenerateVoxelMeshData` already note precomputed
rotation variants as a Phase 2b idea for *custom meshes*; the standard-cube cost was untracked.)

**Recommendation:**

1. Branch once per face on `rotation == 0` and use the raw vertex position (no math at all) — this
   covers nearly all terrain.
2. For rotated blocks, hoist the rotation out of the vertex loop and use a precomputed `float3x3`
   per cardinal rotation (0/90/180/270) instead of `Quaternion.Euler`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — localized to one helper, mechanical change.
> - **Risk:** 🟢 Low — verify rotated blocks (e.g. stairs/logs equivalents) still orient correctly.
> - **Benefit:** 🟡 Low/measured — correctness/cleanliness win; throughput delta is below the
    > benchmark's noise floor (see Status). The original "🟢 High — the benchmark will show it" estimate
    > was **not borne out**: oriented blocks are a small fraction of realistic chunks and the per-vertex
    > transcendental is tiny against total meshing cost.
> - **Seed/Save:** ✅ / ✅.

> **Status (2026-06-15): implemented, validated, and benchmarked — effect within noise.**
> The per-vertex `Quaternion.Euler` was hoisted out of `GenerateStandardCubeFace`: `rotation == 0`
> now takes a no-math fast path, and oriented blocks multiply by a single precomputed `float3x3`
> built once per face. Output preservation is guarded by the new **Meshing Validation Suite**
> (`Minecraft Clone/Dev/Validate Meshing`): `B1` asserts the rotated-vertex math is identical to the
> `Quaternion.Euler` ground truth for all 6 faces × {0,90,180,270}°, and `B4` asserts the same
> end-to-end through the real `MeshGenerationJob` for all 4 yaws. All baselines green before and
> after the change.
>
> **Benchmark (player build, IL2CPP, i9-9900K, 156 chunks × 100 runs):** before vs after, on the two
> rotation-exercising patterns —
>
> | Pattern | Before μs/chunk | After μs/chunk | Δ | Notes |
> |---|---|---|---|---|
> | `Solid` *(control)* | 282.1 | 275.6 | −2.3% | tiny run (43→40 ms), noisy |
> | `Checkerboard` *(control)* | 4416.7 | 4365.4 | −1.2% | high-sample, stable |
> | `OrientedCubes` | 288.5 | 243.6 | −15.6% | tiny run (45→38 ms), **not credible** |
> | `OrientedCheckerboard` | 4423.1 | 4365.4 | −1.3% | high-sample, stable |
>
> The whole report drifted ~1–2% faster between runs (system/build variance; near-identical
> wall-clock). The eye-catching −15.6% on `OrientedCubes` is **measurement noise, not MR-1**: (1) its
> high-sample twin `OrientedCheckerboard` — oriented blocks *at scale* — moved only −1.3%, identical
> to the control `Checkerboard`; (2) `OrientedCubes` is a sub-50 ms run where one 1 ms timer tick is
> ~2.6%; (3) post-change `OrientedCubes` (243.6) reads *faster than* `Solid` (275.6), which is
> physically impossible for the rotation path (fast path can at best tie), proving these two patterns'
> absolute numbers aren't comparable. **Net: no reliably measurable throughput change at this
> harness's resolution.** MR-1 is kept as a correctness/cleanliness improvement, permanently guarded
> by `B1`/`B4` against regression.
>
> **Remaining:** in-game visual confirmation of rotated blocks (logs/pillars/directional). Once
> confirmed, this entry may be removed — but note its conclusion is "marginal, keep for hygiene,"
> not "speedup landed."

---

### MR-2. ✅ DONE (2026-06-20) — 60-byte vertex format with a near-constant color stream

> **Closed:** implemented, suite-guarded, in-game confirmed, and measured. The packed layout keeps
> Position at `Float32x3` (fluids carry sub-block surface heights; half precision risked visible
> cracks) and repacks the rest: TexCoord0 → `Float16x4` (8 B), Color → `UNorm8x4` (4 B), Normal →
> `SNorm8x4` (4 B); TexCoord1 (smooth light) is **unchanged** (B11-pinned, byte-identical). **60 B → 32 B
> /vertex.** The GPU unpacks half/unorm/snorm to floats transparently, so the only shader change was
> `LiquidCore.hlsl` recovering the fluid type via `color.r * 255` (it now rides a UNorm8 channel). The
> normal is packed off the main thread in `MeshPostProcessJob` via `PackedNormal` (the writers still emit
> full-precision `Vector3` normals). `SectionRenderer.Layout` is the single shared source of truth for
> the descriptor (the editor preview window references it). Guarded by the full `Validate Meshing` suite
> (B11 proves TexCoord1 stayed byte-identical; B2/B4 UVs under a half tolerance; B5/B10 determinism on
> the packed normal).
>
> **Measured (IL2CPP, before [`MESHING_MR2_2026_06_19_BASELINE.md`](../Performance/MESHING_MR2_2026_06_19_BASELINE.md)
> `0e453e0` → after [`MESHING_MR2_2026_06_20_AFTER_BASELINE.md`](../Performance/MESHING_MR2_2026_06_20_AFTER_BASELINE.md)
> `0e82130`):** vertex **upload −57 %** (1576 → 676 µs/chunk; bytes 15.94 → 8.50 MB; rate 10113 →
> 12571 MB/s — the stride shrink also lifted throughput, so it beat the −47 % byte ratio). **Bonus:** the
> smaller writer buffers (`Uvs` 16→8 B, `Colors` 16→4 B) cut *generation* 25–30 % on the dense
> patterns (Checkerboard/Transparent/MixedTerrain), wall-clock −25 %. **Trade-off:** Fluid generation
> **+6.4 %** (over the 5 % budget, accepted) — the fluid mesher computes UVs per-vertex and now does
> `float→half` conversions; ~74 µs/chunk, dwarfed by the ~900 µs/chunk upload win. Budget for the Fluid
> pattern is treated as intentionally moved for MR-2 (see the after-baseline doc).

**Observed:** `SectionRenderer.s_layout` declares Position `Float32x3` (12 B) + TexCoord0
`Float32x4` (16 B) + Color `Float32x4` (16 B) + Normal `Float32x3` (12 B) + TexCoord1 `UNorm8x4`
(4 B) = **60 bytes per vertex**. But:

- The Color stream is `new Color(1,1,1,1)` for **every non-fluid vertex** — only fluid faces encode
  data there (liquid type, shore mask).
- TexCoord0's `zw` components are fluid-only (shore push); zeroed for everything else.
- Normals are one of ~10 axis/diagonal directions — they don't need 12 bytes of float precision.

**Recommendation:** Split the fluid-only attributes out of the opaque/transparent submesh layout
(fluids already render in their own submesh with their own material), or at minimum: Color →
`UNorm8x4` (4 B), Normal → `UNorm8x4`-encoded direction or an index decoded in the shader. A
realistic target is **~32 bytes/vertex (−45%)**, which cuts `SetVertexBufferData` upload time,
`NativeList` memory in every meshing job, and GPU memory/bandwidth proportionally.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — vertex layout, `MeshDataJobOutput`, meshing job writers, and all three
    > shaders (opaque/transparent/fluid) change together.
> - **Risk:** 🟡 Medium — shader/layout mismatches fail visibly; smooth lighting encoding in
    > TexCoord1 must be preserved exactly.
> - **Benefit:** 🟢 High — under chunk streaming, vertex upload is a recurring main-thread cost and
    > this nearly halves it.
> - **Seed/Save:** ✅ / ✅.

---

### MR-3. ✅ DONE (2026-06-18) — Managed allocations per section mesh update

> **Closed:** implemented and suite-guarded. `UpdateMeshNative` now picks from 8 cached `Material[]`
> combinations by submesh-presence bitmask (`EnsureMaterialCacheCurrent`) and assigns
> `sharedMaterials` **only when the bitmask or cache version changed** since the section's last update —
> no per-update `Material[]` allocation, no redundant renderer-state write. A static cache-version
> counter covers a global material swap; the per-section `_lastMaterialMask`/`_lastMaterialCacheVersion`
> are reset in `Clear()` (pool-reset-safety). Guarded by **B12** (combination-per-bitmask) and the new
> **B15** (no-reassign-when-bitmask-unchanged, sentinel-survival). All baselines green; in-game render
> confirmed.

**Observed:** `SectionRenderer.UpdateMeshNative` (`SectionRenderer.cs` ~line 84) allocates
`new Material[3]`, potentially `Array.Resize`s it, and assigns `_meshRenderer.sharedMaterials` on
**every mesh update of every section** — 8 sections per chunk, up to 10 mesh jobs per frame. That is
GC garbage plus a renderer-state update in the hot apply path, even when the material set didn't
change.

**Recommendation:** There are only 7 possible material combinations (any non-empty subset of
{opaque, transparent, fluid}). Cache 7 static `Material[]` arrays once, pick by bitmask, and only
assign `sharedMaterials` when the combination actually changed since the last update.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — materials are global singletons from `World.Instance`.
> - **Benefit:** 🟡 Medium — removes steady GC churn during chunk streaming (exactly the class of
    > hot-path allocation `GENERAL_OPTIMIZATION_GUIDE.md §5` forbids).
> - **Seed/Save:** ✅ / ✅.

---

### MR-4. ✅ DONE (2026-06-18) — `RecalculateBounds()` per section update despite known bounds

> **Closed:** implemented and suite-guarded. `UpdateMeshNative`'s per-update `_mesh.RecalculateBounds()`
> vertex scan is replaced by a constant `s_sectionBounds` (16³ section cell, center (8,8,8)) assigned
> each update — O(1) instead of O(verts). Guarded by **B14** (bounds contain all emitted vertices —
> survives the change) and the new **B16** (bounds *equal* the constant section cell). The "custom mesh
> exceeds the unit cell" caveat is still open via **MH-7** (no custom/cross/lava block in the palette
> yet) — current blocks all stay inside the cell, confirmed in-game. All baselines green.

**Observed:** `UpdateMeshNative` passes `MeshUpdateFlags.DontRecalculateBounds` to every buffer
upload, then ends with `_mesh.RecalculateBounds()` (`SectionRenderer.cs` ~line 110) — a full
main-thread scan over all vertices of the section, per update.

**Recommendation:** A section's geometry is confined to its 16×16×16 cell (fluid surface heights
and cross meshes stay inside block bounds). Assign a constant
`_mesh.bounds = new Bounds(center: 8,8,8, size: 16,16,16)` once. If custom block meshes are ever
allowed to exceed the cell, compute min/max in the meshing job per section (almost free there) and
pass it through `MeshSectionStats`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — verify no custom mesh asset exceeds the unit cell; oversized bounds are safe
    > (slightly conservative culling), undersized bounds cause visible popping.
> - **Benefit:** 🟡 Medium — removes a per-section main-thread vertex scan from the apply path.
> - **Seed/Save:** ✅ / ✅.

---

### MR-5. ✅ DONE (2026-06-18) — `MeshPostProcessJob` blocks the main thread per chunk apply

> **Closed:** implemented and suite-guarded. The chunk-space → section-space rewrite + `InterleavedStream3`
> assembly now chains onto the mesh job at schedule time in `WorldJobManager.ScheduleMeshing`
> (`postJob.Schedule(job.Schedule())`) instead of `Schedule().Complete()` inside `Chunk.ApplyMeshData`.
> By the time `ProcessMeshJobs` completes the combined handle the post-process has already run on a
> worker thread; `ApplyMeshData` only uploads buffers. Guarded by **B10** (chained-vs-separate byte
> equality, incl. `InterleavedStream3`). All baselines green; in-game render confirmed.

**Observed:** `Chunk.ApplyMeshData` (`Chunk.cs` ~line 334) runs
`postProcessJob.Schedule().Complete()` — a synchronous main-thread stall for the chunk-space →
section-space coordinate rewrite — once per completed mesh job, inside the frame's apply budget.

**Recommendation:** Chain `MeshPostProcessJob` onto the mesh job handle at schedule time in
`WorldJobManager.ScheduleMeshing` (`Handle = postJob.Schedule(meshJobHandle)`). By the time
`ProcessMeshJobs` sees the handle completed, the post-process has already run on a worker thread,
and `ApplyMeshData` only uploads buffers.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — move the job construction; `MeshingJobData.Handle` already carries the
    > combined handle pattern.
> - **Risk:** 🟢 Low — the post-process job only touches the output buffers, which already live
    > until `ProcessMeshJobs`.
> - **Benefit:** 🟡 Medium — removes a fixed main-thread cost per mesh completion (up to 10/frame).
> - **Seed/Save:** ✅ / ✅.

---

### MR-6. ✅ IMPLEMENTED (2026-06-20) — Mesh output `NativeList`s start at default capacity

> **Closed:** pre-size **and** pool implemented in one PR, suite-guarded by **B17** (MH-2 pooled-output
> stale-data guard), built against MR-2's final 32 B/vertex layout. Benchmarked (IL2CPP) — see
> [`MESHING_MR6_2026_06_20_AFTER_BASELINE.md`](../Performance/MESHING_MR6_2026_06_20_AFTER_BASELINE.md).
> **Generation: no regression on any pattern** (0 to −5 %, high-vertex patterns moving most as expected
> from reduced realloc — but the upload pass, which MR-6 does not touch, drifted +12 % run-to-run, so the
> generation deltas sit within this run's noise floor; the firm result is "flat, no regression," and the
> Fluid path returned to its pre-MR-2 level, absorbing the ~6 % MR-2 had moved). The **pre-size table**
> shows a **bimodal** output distribution (light ~2 048 verts vs dense 163 k–393 k), so the
> `DefaultVertexCapacity = 24576` hint was **kept low on purpose** — pooling retention self-tunes each
> buffer to its densest chunk, making the constant a cold-start hint and the low value memory-optimal.
> **Pooling's actual win** (eliminating ~10 Persistent native alloc/frees per chunk in steady state) is a
> runtime allocation-rate reduction the per-iteration-allocating benchmark does not measure — confirm via
> in-game profiler GC capture.

**Observed:** `MeshDataJobOutput` (`JobData.cs`) creates all 9 output lists with the
default initial capacity. A typical surface chunk emits tens of thousands of vertices, so every
meshing job pays a chain of grow → reallocate → memcpy cycles inside the job; and the whole struct is
allocated then disposed (Persistent) per chunk, adding native alloc/free churn.

**Recommendation:** Pre-size with a sensible initial capacity (e.g. vertices ≈ 16–24k, triangles
proportional — derive from the meshing benchmark's median), or carry forward the chunk's previous
mesh size as the estimate. Optionally pool whole `MeshDataJobOutput` instances alongside
`ChunkJobArrayPool` so the capacity survives across jobs (note: `NativeList` retains capacity on
`Clear()`, so pooling fully amortizes growth).

> **Impact Analysis:**
> - **Effort:** 🟢 Low (pre-size) → 🟡 Medium (pool the output struct).
> - **Risk:** 🟢 Low — over-sizing only costs memory; pooling must respect the existing
    > "dispose after `ApplyMeshData`" lifecycle.
> - **Benefit:** 🟡 Medium — removes hidden reallocation/memcpy from every meshing job.
> - **Seed/Save:** ✅ / ✅.

> **Status (2026-06-20): implemented, suite-green (B1–B17).**
> **(a) Pre-size.** `MeshDataJobOutput`'s constructor now seeds every per-vertex / per-triangle
> `NativeList` from named capacity constants (`DefaultVertexCapacity = 24576`, opaque tris ×1.5,
> secondary tris 4096) — a typical surface chunk no longer reallocates inside the job. The benchmark and
> editor/preview paths get this for free (a clean pre-size measurement, no pooling involved). The hint
> targets the median, not the dense-Checkerboard worst case (~278k verts); pooling amortizes the rest.
>
> **(b) Pool.** New `Helpers/MeshOutputPool.cs` (mirrors `ChunkJobArrayPool`: `Rent`/`Return(in …)` +
> a `MeshDataJobOutput.FromPool` flag) pools whole output structs for the runtime path.
> `WorldJobManager.ScheduleMeshing` rents instead of `new`-ing; the output is returned **centrally in
> `ProcessMeshJobs`** right after `Chunk.ApplyMeshData` uploads it — symmetric with the existing input
> release (`ReleaseMeshingJobInputs`), so `Chunk` stays pool-agnostic and `ApplyMeshData` no longer owns
> native-memory lifecycle. `NativeList` retains capacity across `Clear()`, so after warm-up no meshing
> job reallocates its output buffers and the per-chunk Persistent alloc/free is eliminated.
>
> **(c) Reset safety.** `MeshOutputPool.Return` calls `MeshDataJobOutput.ClearForReuse()` (clears the 9
> lists, retains capacity) before re-pooling — mandatory because `MeshGenerationJob` *appends* and never
> clears. `SectionStats` is intentionally not reset (overwritten every run). Guarded by **B17** (a
> pooled buffer reused across two scenes == a fresh buffer); verified red→green (reset off → B17 fails
> `Vertices length 120 != 48`; reset on → all 17 green).

---

### MR-7. ✅ DONE (2026-06-15) — Per-fluid-voxel `Allocator.Temp` arrays in the meshing job

> **Closed:** implemented, suite-guarded (`B7`/`B8`), and benchmarked with a **real measured win** —
> **−18% on the fluid pattern** (1365 → 1115 μs/chunk). Full record below; `MR-7b` (stackalloc, no threading) logged as a deeper future option.

**Observed:** `MeshGenerationJob.GenerateVoxelMeshData` (`MeshGenerationJob.cs` ~line 320) allocates
`new NativeArray<OptionalVoxelState>(14, Allocator.Temp)` + `new NativeArray<ushort>(14, Temp)` and
disposes both **per fluid voxel**. An ocean chunk does this thousands of times per job. Temp
allocations are cheap, but not free at that frequency.

**Recommendation:** Hoist both 14-element buffers to `Execute()` scope and reuse them across voxels
(they are fully rewritten per voxel), or replace with fixed-size struct buffers
(`FixedList`/`stackalloc`-style) since the size is a compile-time constant.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — buffers are fully overwritten per voxel; no stale-data hazard.
> - **Benefit:** 🟡 Medium — fluid-heavy chunks (oceans, lakes) mesh measurably faster.
> - **Seed/Save:** ✅ / ✅.

> **Status (2026-06-15): implemented, suite-green, benchmarked — measured win.**
> The neighbor scratch arrays were hoisted from per-fluid-voxel to a single `Allocator.Temp`
> allocation per `Execute()` (sized by `s_fluidNeighborOffsets.Length`), threaded as `ref` params
> through `IterateStandardSection`/`IterateSolidSection` → `ProcessVoxel` → `GenerateVoxelMeshData`.
> The fill loop now writes every slot unconditionally (`… ? new OptionalVoxelState(…) : default`) so
> the reused buffer carries no stale neighbor — bit-identical to the old fresh-per-voxel behavior.
> Output preservation is guarded by the **Meshing Validation Suite** `B8` (full probe-output
> differential across a scene where wall-encased fluids prime all neighbor slots before an
> air-surrounded probe) and `B7` (fluid determinism); all 8 baselines green before and after, so no
> in-game visual check is needed (the differential proves byte-identical fluid output).
>
> **Benchmark (player build, IL2CPP, safety checks ON, i9-9900K, 156 chunks × 100 runs):** before
> (pre-MR-7) vs after, WithDiagonals column —
>
> | Pattern | Before μs/chunk | After μs/chunk | Δ | Role |
> |---|---|---|---|---|
> | **Fluid** | 1365.4 | 1115.4 | **−18.3%** | target |
> | Checkerboard | 4365.4 | 4391.0 | +0.6% | control (stable) |
> | OrientedCheckerboard | 4365.4 | 4384.6 | +0.4% | control (stable) |
> | Transparent | 5179.5 | 5205.1 | +0.5% | control (stable) |
> | MixedTerrain | 2384.6 | 2339.7 | −1.9% | control (stable) |
>
> Only the fluid pattern moved; every high-sample control stayed within ±2% noise, so the −18% is a
> genuine fluid-path win, not drift. **Caveat:** the benchmark runs with Burst **safety checks
> enabled**, so part of the gain is `NativeArray` safety-handle setup/teardown that a shipping
> (safety-off) build wouldn't fully pay — the real-world delta is smaller but still positive (the
> bump-allocator calls and per-voxel churn are eliminated regardless). The noisy sub-50 ms `Solid`/
> `OrientedCubes` micro-patterns are not used for attribution.
>
> **Future (deeper) option — MR-7b:** the scratch is still a `NativeArray<Allocator.Temp>` threaded as
> `ref` through four methods, and the per-`Execute` allocation fires even on chunks with no fluid.
> `OptionalVoxelState` is blittable and the slot count is a compile-time constant, so a `stackalloc` /
> `FixedList` scratch local inside the fluid branch would need **zero threading** and **zero
> allocation**. Deferred because it ripples into `VoxelMeshHelper.GenerateFluidMeshData`'s signature
> (and its fluid-helper chain) — `in NativeArray<OptionalVoxelState>` → `ReadOnlySpan`/pointer — with
> Burst's finicky `Span` support; a bigger, riskier change than the throughput win justifies right now.

---

### MR-8. Greedy meshing (coplanar quad merging)

**Observed:** The mesher emits one quad per visible voxel face. Merging coplanar, same-texture,
same-lighting faces into larger quads ("greedy meshing") typically cuts opaque vertex counts by
**60–90%** in natural terrain — the largest structural meshing win available, and previously absent
from every design document.

**Constraints specific to this engine:**

- **Per-vertex smooth lighting** is the hard one: merged quads interpolate light across the merged
  area, which is wrong unless (a) merging is restricted to faces with identical corner light values
  (still merges large uniform areas — most of the win), or (b) lighting moves out of vertex data
  into a per-chunk 3D light texture sampled per-pixel (bigger refactor, also improves light quality).
- **Texture atlas UVs** can't tile across a merged quad. Requires `Texture2DArray` (UV.z = layer
  index, fragment-side `frac()` tiling) — a shader + atlas build change.
- The anisotropy quad-flip (`EmitQuadTriangles`) and AO/light diagonal logic must be re-derived for
  merged quads.
- Sub-chunk section stats (`MeshSectionStats`) and the visibility-culling connectivity work
  (`VISIBILITY_CULLING_ARCHITECTURE.md`) are unaffected — merging happens within a section.

**Recommendation:** Treat as a phased design doc of its own when picked up: Phase 1 opaque cubes
with flat lighting + texture arrays; Phase 2 smooth-lighting-aware merge predicate. Capture a
meshing baseline first (`Performance/README.md`).

> **Impact Analysis:**
> - **Effort:** 🔴 High — mesher core, shaders, atlas pipeline.
> - **Risk:** 🔴 High — visual regressions (lighting seams, texture tiling) are easy to introduce.
> - **Benefit:** 🟢 High — vertex/index counts drop by more than half; helps CPU meshing time, upload
    > bandwidth, GPU vertex load, and memory simultaneously.
> - **Seed/Save:** ✅ / ✅ — purely visual; voxel data unchanged.

---

### MR-9. `Clouds.cs` — legacy mesh API with `.ToArray()` — ✅ IMPLEMENTED (2026-06-20)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §2.2.)*

> **Implemented:** Both mesh-build sites (`CreateFastCloudMesh`, `CreateFancyCloudMesh`) now assign
> via `mesh.SetVertices(list)` / `mesh.SetTriangles(list, 0)` / `mesh.SetNormals(list)` instead of
> the three `.ToArray()` round-trips — no temporary managed arrays per cloud-tile (re)generation,
> byte-identical mesh output. The `new List<>()` allocations were left in place: the build methods
> run only at init and on cloud-style change (via `Initialize`/`Reinitialize`), not per frame
> (`UpdateClouds` only moves transforms), so hoisting them to fields buys no steady-state GC win.

**Observed:** Cloud mesh generation builds `List<Vector3>`/`List<int>` and assigns via
`mesh.vertices = vertices.ToArray()` etc. (`Clouds.cs` ~lines 210–212, 266–268) — three temporary
managed arrays per cloud tile creation.

**Recommendation:** Use `mesh.SetVertices(list)` / `mesh.SetTriangles(list, 0)` /
`mesh.SetNormals(list)` (accept `List<T>` directly), or the NativeArray mesh API for parity with
`SectionRenderer`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — direct API substitution.
> - **Risk:** 🟢 Low — cloud meshes are visually simple.
> - **Benefit:** 🟡 Medium — eliminates GC spikes during cloud tile (re)generation.
> - **Seed/Save:** ✅ / ✅.

---

## Detailed findings — Lighting

### LI-1. ✅ DONE (2026-06-22) — Branchy 9-map dispatch + hashmap cache in the BFS inner loop

> **➡️ UPDATE (2026-06-22): the layout SHIPPED net-positive via P-2 Phase 1** (worker-thread gather, commit
> `e3e1635`) — −34 % to −50 % vs the LI-1 POST full-timing below. The "NOT shipped standalone" rationale in
> this section is the *standalone* (gather-on-main-thread) decision and is retained as the motivation for
> Phase 1. Result: [`Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md).

> **Closed: implemented, bit-identical, suite-guarded, benchmarked — but NOT shipped standalone.**
> The halo-padded layout is a **validated 2.4–3× in-job BFS win**, but the per-chunk **on-demand gather**
> that feeds it costs ~2.6× the old 9-map prep on the main thread, so standalone schedule-time cost is
> flat-to-worse on every scenario except the most BFS-bound. **The validated layout is folded into P-2**
> (persistent halo-padded storage), where the data is already padded and the gather cost vanishes — keeping
> the BFS win for free. The LI-1 branch is the proven foundation for P-2: branch-free accessors,
> `LIGHTING_HALO = MAX_LIGHTING_BFS_REACH = 2`, the gather/extract transcoders, and 47 lighting baselines
> guarding bit-identity across the halo seam. Full numbers + decision:
> [`Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md`](../Performance/LIGHTING_LI1_2026_06_22_BENCHMARK.md).
> Retained here (not deleted) so the "halo helps → just ship it" idea isn't re-proposed without the gather
> caveat. Key correction from this work: the doc's suggested **1-voxel halo is a correctness bug** — the
> sunlight-darkening path reads ±2 (edges *and* diagonal corners), so **halo = 2** (20×128×20). Full record below.

**Observed:** Every `GetLightData` / `GetPackedData` call inside `NeighborhoodLightingJob`
(`NeighborhoodLightingJob.cs` ~lines 814–891) walks an up-to-9-way branch tree to select the correct
neighbor array (own / N / S / E / W / NE / NW / SE / SW), and any boundary position additionally
pays a `NativeHashMap<long, ulong>` lookup for the write-through cache. This runs **per neighbor,
per BFS node** — millions of times per lighting job — and defeats Burst vectorization in the
innermost loop.

**Recommendation:** Build the job input as a **single padded volume** instead of 9 separate maps —
e.g. an 18×128×18 array with a 1-voxel halo (sufficient for face-neighbor BFS reads), or
48×128×48 if deep cross-chunk propagation reads beyond the halo. The inner loop becomes a
branch-free flat index, and the read side of the write-through hashmap cache disappears (writes to
the halo become plain array writes, harvested into `CrossChunkLightMods` at the end).

**Trade-off note:** This *increases* schedule-time copy work, which runs counter to
`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md §1.2` (copy *less* per job). They optimize different
costs: §1.2 attacks main-thread schedule time, LI-1 attacks in-job BFS time. The right call needs a
benchmark of both — and the long-term resolution is §1.3/P-2 (persistent native storage), which can
satisfy both if the persistent layout itself is halo-padded.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — touches job input layout, `FillChunkLightMapForJob` fill paths, and the
    > pool (`ChunkJobArrayPool` buffer sizes change).
> - **Risk:** 🟡 Medium — light output must be **bit-identical** before/after; validate with
    > `LightingJobBenchmark` and a fixed-seed world diff of light maps.
> - **Benefit:** 🟢 High — directly attacks lighting job self-time, the engine's dominant background
    > cost during streaming.
> - **Seed/Save:** ⚠️ Seed-safe for terrain, but lighting results **must** remain deterministic and
    > identical — any divergence re-dirties the edge-check cascade (§4 of the pipeline doc) on old
    > saves. Treat "identical light output" as a hard acceptance criterion. / ✅ no format change.

> **Validation prerequisite (cross-border darkening coverage).** "Bit-identical light output" only has
> teeth on the seam if the suite actually exercises a *darkening* wave crossing a chunk border — the
> halo's hardest read. The lighting suite covers cross-chunk *brightening* fuzz (C1/C2, B40–B44) and now the
> *darkening* quadrant too:
> [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
> **C3 (B54/B55, CLOSED 2026-06-21)** — keep it green when freezing any halo-vs-9-map diff for LI-1.

---

## Detailed findings — Tick & Gameplay

### TG-1. Double voxel lookup + float-path cross-chunk queries in the tick loop

**Observed:** Two compounding costs in the active-voxel tick path:

1. `Chunk.TickUpdate` (`Chunk.cs` ~lines 220–237) calls `BlockBehavior.Behave(...)` **and then**
   `BlockBehavior.Active(...)` for every active voxel — each re-fetches the voxel and re-probes the
   same neighbors. The TODO at `Chunk.cs:226` already acknowledges the duplication.
2. Every neighbor probe that crosses a chunk border goes `ChunkData.GetState` →
   `new Vector3` (float) → `WorldData.GetVoxelState` → `IsVoxelInWorld` float compares →
   `Mathf.FloorToInt` ×3 → dictionary lookup (`ChunkData.cs` ~line 840). For fluid simulation —
   where active voxels cluster at chunk borders by nature — this is the hot path, and it also boxes
   through `VoxelState?` nullables and managed `BlockType` property lookups.

**Recommendation:**

1. Make `Behave` return (or out-param) a "still active" flag so the separate `Active` pass
   disappears.
2. Add an integer-math cross-chunk path: `ChunkData` caches its 4 cardinal neighbor `ChunkData`
   references (invalidated on load/unload), and border probes resolve via
   `neighbor.GetVoxel(x & 15, y, z & 15)`-style integer wrapping without touching `Vector3`,
   `Mathf`, or the world dictionary.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — `BlockBehavior` API change plus a neighbor-reference lifecycle (must be
    > cleared in `ChunkData.Reset()` per pool-reset-safety rules).
> - **Risk:** 🟡 Medium — fluid behavior must be verified unchanged (fluid bugs have history here);
    > stale neighbor references after pool recycle would corrupt simulation.
> - **Benefit:** 🟢 High whenever fluids/grass are active at scale — per-tick cost drops by roughly
    > half from item 1 alone, more near borders from item 2.
> - **Seed/Save:** ✅ / ✅.

---

### TG-2. ✅ DONE (2026-06-20) — `OnDataPopulated` full-chunk scan through managed `BlockType` objects

> **Closed:** implemented and differential-verified. Both halves of the recommendation shipped:
> - **Jobified emission (generation path).** A new single-threaded Burst `ActiveVoxelScanJob`
    > (`Assets/Scripts/Jobs/ActiveVoxelScanJob.cs`) runs as the *final* generation pass — scheduled
    > after the cave-isolation filter in `StandardChunkGenerator.ScheduleGeneration` so it reads the
    > finalized voxel map. It walks the map once and appends the flat chunk index
    > (`ChunkMath.GetFlattenedIndexInChunk` convention) of every voxel whose `BlockTypeJobData.IsActive`
    > is set into a new `GenerationJobData.ActiveVoxels` (`NativeList<int>`). On the main thread,
    > `WorldJobManager.ProcessGenerationJobs` STAGE 1 calls `Chunk.RegisterActiveVoxelsFromJob`, which
    > unpacks each index (`ChunkMath.GetLocalPositionFromFlattenedIndex`, the new inverse helper) and
    > registers it — copying a short list instead of dereferencing managed `BlockType` objects up to
    > 32k times per chunk.
> - **Bitmask fallback scan (load + reset-replay paths).** `World.PrepareGlobalJobData` now builds a
    > flat `bool[] World.IsActiveById`. `Chunk.OnDataPopulated` keeps its section-skipping scan but
    > indexes that array instead of `World.Instance.BlockTypes[id].isActive` — a flat read, no object
    > deref. This path serves only **load-from-save** (`World.LoadOrGenerateChunk` → `PopulateFromSave`)
    > and **pool-recycle replay** (`Chunk.Reset` when `ChunkData.IsPopulated`), where no generation job
    > runs. Active voxels are deliberately **not persisted** (see the serialization architecture doc),
    > so these paths must always rescan — the jobified list is unavailable there. Generators that do not
    > run the scan pass (e.g. the legacy generator) leave `ActiveVoxels` uncreated, and STAGE 1 falls
    > back to this scan.
>
> **Verified:** a differential editor check generated chunks (sea level raised to flood them with
> active water) and confirmed the jobified active set is identical — same local positions — to a
> managed full scan of the same finalized map (10k–13k active voxels/chunk, zero set difference),
> plus a synthetic placed-vs-emitted round-trip (6/6, exact). No existing validation suite covers
> active voxels, so the check was a throwaway `[MenuItem]` (RunCommand execution is currently down on
> the dev machine; the bridge `Unity_ManageMenuItem` was used instead) and removed afterward.
>
> **Measured** (editor A/B microbenchmark — `Assets/Editor/Benchmarking/ActiveVoxelScanBenchmark.cs`,
> menu `Minecraft Clone/Benchmarks/Active-Voxel Scan (TG-2)`; 100 chunks × 5 batches, seed 1337,
> Standard world type; best batch-mean µs/chunk over the *same* finalized voxel data). Four scans:
> `T_old` = original managed-deref full scan; `T_bitmask` = current `OnDataPopulated` flat-`bool[]`
> scan (load/replay path); `T_register` = `RegisterActiveVoxelsFromJob` unpacking the job's list
> (new generation main-thread cost); `T_job` = `ActiveVoxelScanJob` Burst time (now off the main
> thread). `T_job` is measured via `.Run()` so it carries scheduling overhead and **overstates** the
> real per-chunk worker cost — the point is only that it is *off* the main thread, not added to it.
>
> | Scan | Land chunk (0 actives) | Flooded chunk (~12k actives) |
> |---|--:|--:|
> | `T_old` (managed deref, all 32k voxels) | 37.7 µs | 400.7 µs |
> | `T_bitmask` (flat `bool[]`, all 32k voxels) | 33.3 µs | 396.0 µs |
> | `T_register` (unpack job list only) | **0.04 µs** | 366.7 µs |
> | `T_job` (Burst, off main thread) | 87.7 µs | 112.7 µs |
>
> - **Part A (generation path) — main-thread cost.** A normal land chunk previously spent **~37.7 µs**
    > iterating all 32 768 voxels on the main thread to find ~0 active blocks (pure overhead); that is
    > now **~0.04 µs** — the scan moved to a Burst job that overlaps the generation jobs already in
    > flight. The reduction is largest exactly where it matters in normal play (sparse actives).
> - **Part B (load/replay path).** Flat `bool[]` vs the managed deref is **~13 % faster** on the scan
    > itself (37.7 → 33.3 µs); free, and the only path available for saves (actives aren't persisted).
> - **Honest caveat.** For *active-heavy* chunks the Part A main-thread reduction shrinks to ~10 %
    > (400.7 → 366.7 µs) because the bottleneck there is `Chunk.AddActiveVoxel` — the
    > `HashSet<Vector3Int>` inserts (~366 µs for 12k actives), which **both** versions pay. The scan
    > over all 32k voxels is only ~32 µs. So if active-heavy chunks ever profile hot, the next target is
    > the active-voxel *container/population* (cf. TG-1, TG-4), not the scan.

**Observed:** `Chunk.OnDataPopulated` (`Chunk.cs` ~lines 177–205) scans every voxel of every
non-empty section on the main thread when a chunk's data arrives, dereferencing
`World.Instance.BlockTypes[id].isActive` — a managed class array → object → field chain per voxel
(up to 32k per chunk) with poor cache behavior.

**Recommendation:** Precompute a `bool[]` (or 64-bit bitmask array) of "is active" per block ID once
at startup and index that instead — flat, cache-friendly, no object dereference. Longer term, emit
the active-voxel list from the generation job itself (it already touches every voxel in Burst) so
the main thread only copies a short list.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (bitmask) → 🟡 Medium (jobified emission).
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — reduces the activation stutter when chunks stream in.
> - **Seed/Save:** ✅ / ✅.

---

### TG-3. ✅ DONE (2026-06-20) — `UnityEngine.Random` → `Unity.Mathematics.Random` in block behaviors

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §2.3.)*

> **Closed:** Replaced `UnityEngine.Random` with a **local** seeded `Unity.Mathematics.Random` struct
> at every behavior-tick call site (no shared/static RNG state → inherently thread-safe and Burst-ready).
> Seeds are nonzero via `math.max(1u, math.hash(new int3(globalPos)) ^ (uint)(tickSalt * 0x9E3779B1u))`,
> salted by a new monotonic `World._tickCounter` (exposed as `World.TickCounter`, incremented once per
> tick pass in `ProcessTickUpdates`, reset on world load) so rolls vary **per voxel AND per tick** — a
> position-only seed would freeze grass spread / lock lava viscosity forever. BOTH paths were converted:
> grass spread (`BlockBehavior.cs`, three rolls sharing one rng) and lava viscosity / Bug 08 staggering
> (`BlockBehavior.Fluids.cs`, `HandleFluidSpread`). This **unblocks TG-4/TG-5** (jobifying behaviors).
> ⚠️ **Seed note:** the **runtime RNG sequence changes** — grass-spread and lava patterns differ from the
> old implementation for the same world. Cosmetic only; terrain worldgen RNG is untouched; no
> save/migration impact.

**Observed:** `BlockBehavior.cs` uses `UnityEngine.Random` (globally locked, not Burst-compatible)
in the grass-spread tick path. `ChunkLoadAnimation.cs` / `Toolbar.cs` also use it, but only in cold
initialization code (low priority).

**Recommendation:** Use `Unity.Mathematics.Random` seeded per-chunk or per-tick in
`BlockBehavior.cs`. Deterministic, thread-safe, Burst-compilable — a prerequisite for TG-4/TG-5.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — removes global lock contention; unblocks Burst compilation of behaviors.
> - **Seed/Save:** ⚠️ Seed-safe for terrain (worldgen RNG is untouched), but the **runtime RNG
    > sequence changes**: grass-spread and similar behavior patterns will differ from the old
    > implementation for the same world. Cosmetic only — no save/migration impact. / ✅.

---

### TG-4. `BlockBehavior` data separation (ECS/DOTS pattern)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §6.1.)*

> **Detailed design:** [TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md](TG4_BLOCK_BEHAVIOR_DATA_SEPARATION.md) —
> phased plan (BH-D1 infra → per-family storage split → grass Burst → fluid Burst → parallelize + Tier-2),
> with the BH-D1 old-vs-new differential slotted into each phase gate.
>
> **Status (2026-06-22): Phases 0–1 SHIPPED, Phases 2–4 profile-gated.** Phase 0 (BH-D1 differential infra) +
> Phase 1 (per-family `NativeHashSet<int>` active-voxel buckets — landed on **`ChunkData`**, not `Chunk`; tick
> orchestration stays on `Chunk`) are in-game confirmed, suite 11/11 green. The new runtime buckets are
> pool-retained (no per-recycle churn — **TG-6-aligned**, but TG-6's own target, the `GenerationJobData.ActiveVoxels`
> hand-off list, is untouched). Phases 2–3 (grass/fluid Burst) and Phase 4 (parallelize + cross-chunk) are not
> started; the TG-4-vs-TG-5 fork is decided by profiling the tick under heavy fluid sim (see the design doc's §5
> decision framework). The 🔴/🔴 ratings below cover the *remaining* Phases 2–4.

**Observed:** All ticking voxels (fluids, grass, future behaviors) flow through one monolithic
collection and a central `switch` in `BlockBehavior`. As behavior types grow, this forces a single
main-thread tick loop iterating unrelated voxel types.

**Recommendation:** Split active voxels by behavior type into dedicated native collections
(e.g. `_activeFluids`, `_activeGrass`) so each behavior runs as its own independent Burst job —
cache-local, parallelizable, and off the main thread.

> **Impact Analysis:**
> - **Effort:** 🔴 High — re-architects the tick pump and active-voxel registration.
> - **Risk:** 🔴 High — touches the core world ticking engine; fluid parity testing required.
> - **Benefit:** 🟢 High — scales across cores; the only path that gets ticking fully off the main
    > thread. Subsumes TG-1 if done wholesale (TG-1 is the incremental version).
> - **Seed/Save:** ✅ / ✅.

**Parity guard (prerequisite):** the "fluid parity testing required" note above is satisfied by the
behavior-tick validation harness in
[BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md)
— **built (Waves 0–2, 8 baselines green, 2026-06-21)**; land the old-vs-new differential baseline (BH-D1) in the
TG-4 PR itself. The harness's seam table (S1–S5) also enumerates the exact `World.Instance` couplings this split
must sever.

---

### TG-5. `BlockBehavior` Burst function pointers (lighter alternative to TG-4)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §6.2.)*

**Observed/Recommendation:** If TG-4 is overkill, replace the central `switch` with a
`Unity.Burst.FunctionPointer<T>` registry indexed by voxel ID. Keeps a single active-voxel
collection while decoupling behavior logic and enabling Burst-compiled dispatch.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — function-pointer initialization at Burst startup.
> - **Risk:** 🟡 Medium — mismanaged Burst function pointers hard-crash.
> - **Benefit:** 🟡 Medium — decoupling + Burst dispatch, without TG-4's parallelism win.
> - **Seed/Save:** ✅ / ✅.

**Parity guard (prerequisite):** same as TG-4 — guard the function-pointer dispatch swap with the behavior-tick
harness ([BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md))
and the BH-D1 old-vs-new differential. Decoupling the `switch` into a registry must produce a byte-identical `VoxelMod`
stream tick-for-tick.

---

### TG-6. Per-chunk `ActiveVoxels` `NativeList<int>` alloc/free churn — pool it (TG-2 follow-up)

*(Surfaced by the 2026-06-21 behavior-suite review, finding #4.)*

**Observed:** TG-2's jobified emission allocates a fresh `NativeList<int>` per chunk generation —
`new NativeList<int>(StandardChunkGenerator.ActiveVoxelPresizeCapacity, Allocator.Persistent)` (2048 ⇒
8 KB) in `StandardChunkGenerator.ScheduleGeneration`, stored in `GenerationJobData.ActiveVoxels`, and
freed per chunk in `GenerationJobData.Dispose`. During streaming this is per-chunk Persistent
allocate-and-free churn — exactly the repeated-allocation pattern CLAUDE.md says to pool — and the 8 KB
is reserved up front even for the common sparse-actives chunk (which emits ~0 indices).

**Recommendation:** Pool the list, mirroring **MR-6**'s `MeshOutputPool`. `NativeList` retains its
allocated capacity across `Clear()`, so a warmed pool also removes the realloc-and-copy growth a
water-heavy chunk (thousands of source voxels) otherwise pays inside the scan. Rent in
`ScheduleGeneration`; at the consume site (`WorldJobManager.ProcessGenerationJobs` STAGE 1, after
`RegisterActiveVoxelsFromJob`) return the list (cleared, capacity-retained) to the pool **instead of**
letting `GenerationJobData.Dispose` free it — the same split `MeshingJobData.Output` / `_meshOutputPool`
already uses. At shutdown, return each in-flight job's list, then dispose the pool.

**Wiring considerations (why this is its own change, not a quick edit):**

- The pool reference must reach the generator, so `IChunkGenerator.ScheduleGeneration` (a
  multi-implementer surface — `StandardChunkGenerator` + the legacy generator, which leaves the list
  *uncreated* and so never rents) gains a pool dependency.
- `GenerationJobData.Dispose` must stop disposing `ActiveVoxels` once it is pool-owned; the central
  return site becomes the sole release path. This interacts with the dispose-path no-leak invariant now
  documented on `GenerationJobData.Dispose` — the pooled list's lifecycle moves to the pool.
- Native-container lifetime is the risk surface: a list returned before its generation `JobHandle` has
  `Complete()`d is a use-after-free. The return must sit strictly after STAGE-1 consumption (it already
  does, post-`Complete()`).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — pool type + threading it through the generator interface + the dispose-path split.
> - **Risk:** 🟡 Medium — native-container lifetime / use-after-free; the pipeline has deadlock history
    > (see the chunk-lifecycle invariants), so the return site must be exact.
> - **Benefit:** 🟡 Medium — removes per-chunk 8 KB Persistent alloc/free during streaming and the
    > realloc growth on active-heavy chunks once the pool warms; no main-thread tick cost change.
> - **Seed/Save:** ✅ / ✅ — active voxels are not persisted; pooling is an internal allocation concern.

**Measurement gate (per MR-6 discipline):** ship this only with a before/after benchmark — the
`ActiveVoxelScanBenchmark` (menu `Minecraft Clone/Benchmarks/Active-Voxel Scan (TG-2)`) already exists
and its `LIST_CAPACITY` is pinned to `ActiveVoxelPresizeCapacity` — plus an IL2CPP re-capture, exactly as
MR-6's pooling was validated.

**Already closed (the rest of review finding #4):** the `2048` magic number is extracted to
`StandardChunkGenerator.ActiveVoxelPresizeCapacity` (the benchmark pins to it, no drift), and the
dispose-path audit found only two `GenerationJobs` eviction sites (completion drain + shutdown), both of
which `Dispose` — now guarded by the no-leak invariant comment on `GenerationJobData.Dispose`. Pooling is
the sole remaining open item.

---

## Detailed findings — Main Thread & Miscellaneous

### MT-1. `List.Insert(0)` / `RemoveAt(i)` — O(n) mesh priority queue

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §3.1; overlaps pipeline doc §5.1.)*

**Observed:** The meshing pipeline uses `List<Chunk> _chunksToBuildMesh` as a priority queue —
`Insert(0, chunk)` and mid-list `RemoveAt(i)` are O(n) shifts (`World.cs`, scheduling loop ~line
1270 and the insert/remove sites around lines ~1022/1033/1607, plus unload paths at ~2156). With a
large backlog (exactly the §3 cascade scenario) this goes quadratic.

**Recommendation:** Replace with a real priority structure — `PriorityQueue<Chunk, int>` keyed by
distance, or two queues (priority/normal) if only front-insertion matters. Keep the companion
`HashSet` for dedup.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — iteration/removal patterns around the list must adapt.
> - **Risk:** 🟡 Medium — meshing order affects visual pop-in; test streaming visually.
> - **Benefit:** 🟢 High under backlog; modest in calm play.
> - **Seed/Save:** ✅ / ✅.

---

### MT-2. Light scheduler snapshots the full dirty set every frame

**Observed:** `World.Update` (`World.cs` ~lines 1171–1256) copies the entire
`_chunksNeedingLightWork` set into a pooled list every frame and iterates all of it — even when
`maxLightJobsPerFrame` is exhausted after the first few entries, and even for chunks whose
neighbor-readiness gates will fail identically to last frame. Cheap in calm play; O(dirty) per
frame during exactly the backlog scenarios where frames are already slow (compounds pipeline §3).

**Recommendation:** Split the dirty set into "gate-ready" and "waiting" subsets: chunks enter
gate-ready when the event that could unblock them occurs (neighbor populated / neighbor lit —
hooks already exist at those transitions). The per-frame loop then iterates only schedulable work
and stops at the throttle. ⚠ Respect the flag-pairing invariants in
`CHUNK_LIFECYCLE_PIPELINE.md` — the current full rescan doubles as a self-heal (see also the
1-second fail-safe scan, pipeline doc §5.2), so keep that fail-safe in place.

> **Impact Analysis:**
> - **Effort:** 🟢 Low→🟡 Medium depending on how event-driven the ready set becomes.
> - **Risk:** 🟡 Medium — a chunk that never enters the ready set stalls lighting (deadlock
    > history!); the fail-safe scan must remain as backstop.
> - **Benefit:** 🟡 Medium — trims fixed per-frame overhead precisely when FPS is lowest.
> - **Seed/Save:** ✅ / ✅.

---

### MT-3. `DebugScreen` intermediate string allocations per refresh

**Observed:** Despite the cached `StringBuilder`s, each refresh allocates dozens of temporaries:
`.ToString()` calls on numbers feeding `Append` (`DebugScreen.cs` ~lines 383–396), plus `$"..."`
interpolation inside `AppendLine(...)`. Only costs while the debug screen is visible.

**Recommendation:** Use the numeric `Append(int)`/`Append(float)` overloads and replace
interpolated `AppendLine($"...")` with chained `Append` calls. Zero-alloc refresh is achievable.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (tedious but mechanical).
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ Low — debug-only; worth doing so the debug overlay doesn't distort GC profiling.
> - **Seed/Save:** ✅ / ✅.

---

### MT-4. Startup `List.Contains` / `.IndexOf` — O(n) custom-mesh lookup

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §3.2.)*

**Observed:** `World.PrepareGlobalJobData` collects unique custom meshes into a `List` and searches
with `.Contains()` / `.IndexOf()` — O(n) each (`World.cs` ~lines 1338–1346). Startup-only.

**Recommendation:** `Dictionary<VoxelMeshData, int>` mapping mesh → index; O(1) both ways.

> **Impact Analysis:** Effort 🟢 / Risk 🟢 / Benefit ⚪ (startup-only, scales with block DB growth).
> **Seed/Save:** ✅ / ✅.

---

### MT-5. Startup `.ToArray()` intermediates feeding `NativeArray`

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §4.2.)*

**Observed:** `new NativeArray<T>(list.ToArray(), Allocator.Persistent)` ×4 in
`World.PrepareGlobalJobData` (`World.cs` ~lines 1384–1387) — temporary managed arrays immediately
discarded.

**Recommendation:** Allocate the `NativeArray` at `list.Count` and fill via `CopyFrom`/loop, or
build in a `NativeList<T>` from the start.

> **Impact Analysis:** Effort 🟢 / Risk 🟢 / Benefit ⚪ (startup-only).
> **Seed/Save:** ✅ / ✅.

---

### MT-6. `CompressionFactory` "GZip" actually writes raw Deflate

**Observed:** `CompressionFactory.CreateOutputStream`/`CreateInputStream`
(`CompressionFactory.cs` ~lines 65–66, 93–94) construct `DeflateStream` for
`CompressionAlgorithm.GZip`. Not a performance bug (Deflate is the same codec minus the GZip
header/CRC), but the label is wrong: payloads tagged "GZip" on disk are **raw Deflate**, which will
bite any future external tool, migration, or interop that trusts the name.

**Recommendation:** Do **not** "fix" this by swapping to `GZipStream` — that silently breaks every
existing save written with the current code (the fallback path when LZ4 is unavailable). Instead:
rename the enum member to `Deflate` (save formats store the enum value, not the name — verify
before renaming) or document the discrepancy at the enum and in
`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`. If true GZip is ever wanted, add it as
a **new** enum value via the AOT migration protocol.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (documentation/rename).
> - **Risk:** 🟢 Low if rename-only; 🔴 High if anyone changes the stream class — hence this entry.
> - **Benefit:** ⚪ Low — correctness/clarity insurance, no runtime change.
> - **Seed/Save:** ✅ / ⚠️ **Save-format sensitive** — the bytes must not change without a format
    > version bump + migration step (`serialization-migration` skill).

---

## Detailed findings — GPU & Shaders

### GS-1. Liquid shader: per-pixel procedural 3D simplex FBM

**Observed:** `LiquidCore.hlsl` evaluates Ashima-style 3D simplex noise (`snoise`, ~60+ ALU ops
each) in **FBM loops per fragment**. At the High tier with dual-phase and refraction, one water
pixel evaluates roughly: 2 phases × (wave FBM 4-oct + ripple FBM 4-oct + stream FBM 3-oct) plus
2 × 3-oct refraction-normal FBMs ≈ **25–30 `snoise` calls per pixel**. Lava is comparable (plus
crust/spark FBMs). An ocean or lava lake covering half the screen is by far the most expensive
thing the GPU does — on a midrange Android GPU this alone can blow the entire frame budget.

The existing quality-tier keywords (`_FLUID_QUALITY_LOW/MED`, refraction opt-out) are the right
mechanism and already help, but even the Low tier pays 2-oct procedural simplex per pixel, and the
tier system reduces octaves rather than changing the *kind* of work.

**Recommendation (in increasing effort):**

1. **Pre-baked noise textures.** Replace procedural `snoise` FBM with 1–2 samples of a tiling,
   pre-baked FBM noise texture (scrolled/blended exactly like the current coordinates — the
   dual-phase flow-mapping logic is unchanged, only the noise *source* changes). Texture fetches
   are what mobile GPUs are good at; this typically cuts liquid fragment cost by 5–10×. A small
   3D texture (or 2 blended 2D samples to fake the third dimension) preserves the "boiling"
   vertical animation. The bake can be generated offline via `Tools/Python/` or an editor tool.
2. **Derive refraction normals from existing results.** The two extra FBM evaluations per phase
   (`normal_dx`/`normal_dz` finite differences) can come from the noise texture's precomputed
   gradient channels (RGBA: value + xy-gradient) for free instead of 2 more FBM evaluations.
3. **Cheaper dual-phase.** With texture-based noise, consider whether the Low tier can drop to a
   single phase with a time-sliced texture swap, removing the 2× multiplier entirely.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — noise bake asset + shader change; tier macros stay.
> - **Risk:** 🟡 Medium — visual character of water/lava will shift slightly (tile period,
    > gradient quality); needs eyes-on comparison per tier.
> - **Benefit:** 🟢 High — largest single GPU win available; transforms the worst-case mobile frame.
> - **Seed/Save:** ✅ / ✅.

---

### GS-2. Opaque Texture required globally; scene color sampled even without refraction

**Observed:** Two compounding costs:

1. The URP asset (`Assets/settings/Rendering/VoxelEngine-URP-Asset.asset`) sets
   `m_RequireOpaqueTexture: 1` globally — URP performs a **full-screen color copy every frame**,
   whether or not any liquid is visible. On mobile tile-based GPUs this also forces a render-target
   resolve/store, one of the most expensive operations on those architectures.
2. `UberLiquidShader.shader` calls `SampleSceneColor(distortedUV)` and composites manually via
   `lerp(background, color, alpha)` **even when `_FLUID_REFRACTION_OFF` is set** — with refraction
   off, `distortedUV` is just the undistorted screen UV, so the manual composite is mathematically
   equivalent to standard hardware alpha blending and the opaque texture isn't needed at all.

**Recommendation:** When refraction is off (which should be the mobile default), switch the liquid
pass to hardware alpha blending (`Blend SrcAlpha OneMinusSrcAlpha`, output alpha = the current lerp
factor) inside the `_FLUID_REFRACTION_OFF` variant — no `SampleSceneColor`, no opaque-texture
dependency. Then toggle `UniversalRenderPipelineAsset.supportsCameraOpaqueTexture` from
`GraphicsSettingsController` so the full-screen copy only exists when the refraction tier is
active. (Note `m_OpaqueDownsampling` is already set — keep downsampled opaque texture for the
refraction-on path; refracted water doesn't need full resolution.)

> **Impact Analysis:**
> - **Effort:** 🟢 Low — one shader variant + a settings hook.
> - **Risk:** 🟡 Medium — blending semantics for overlapping fluid faces must be checked (the
    > current manual composite reads pre-liquid opaque color; hardware blending composites over
    > whatever is in the framebuffer, including other transparent geometry — verify against the
    > transparent-blocks submesh ordering).
> - **Benefit:** 🟢 High on mobile — removes a per-frame full-screen copy + resolve; also a real
    > win on desktop at high resolutions.
> - **Seed/Save:** ✅ / ✅.

---

### GS-3. Voxel lighting math runs per-fragment on purely per-vertex data

**Observed:** `ApplyVoxelLightingRGB` (`VoxelLighting.hlsl`) computes 4 independent shade curves,
each ending in `pow(x, 2.2)` — **4 `pow` calls per fragment** in the opaque, transparent, and
liquid shaders. Every input (per-vertex light data + global uniforms) is available in the vertex
shader; only the final `color * multiplier` needs the fragment stage.

**Recommendation:** Compute the sun multiplier (`sunShadow * skyColor`) and block multiplier
(`half3` of the three channel shadows) in the vertex shader and interpolate them; the fragment
does `col.rgb *= max(sunContrib, blockContrib)` (or interpolate the combined `max` directly —
verify the visual difference across a face is acceptable; interpolating the two contributions
separately and taking `max` per-pixel is the closer match). Pixels vastly outnumber vertices in
voxel scenes, so this moves the `pow` chain to the cheap stage.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — shared include + V2F struct change.
> - **Risk:** 🟢 Low — minor interpolation differences across large faces; compare side-by-side
    > with the `DEBUG_LIGHTDATA` view.
> - **Benefit:** 🟡 Medium — meaningful fragment ALU reduction on mobile; small on desktop.
> - **Seed/Save:** ✅ / ✅.

---

### GS-4. Render pipeline tier audit (shadows, MSAA, render scale, shadow casting mode)

**Observed (current URP asset + code state):**

- `m_MainLightShadowsSupported: 1` with `m_ShadowDistance: 0` — shadows never *render* (distance
  0), but the support flag still compiles shadow shader variants and keeps the shadow-map keyword
  plumbing active. If this is permanent (the voxel sky-light system replaces shadows), set
  supported = 0 to strip variants; if shadows are ever enabled, note that…
- `SectionRenderer` sets `ShadowCastingMode.TwoSided` on **every section** — with shadows actually
  on, the entire voxel world would render twice-sided into a 2048 shadow map; that needs its own
  tiered decision (e.g. shadows only from a small radius, or baked/none on mobile).
- `m_MSAA: 2` — MSAA on a voxel world of opaque cubes buys little; on mobile it costs bandwidth
  (though tilers handle it relatively well). Should be a quality-tier setting, not baked into the
  asset.
- `m_RenderScale: 1` — no resolution scaling hook for mobile; exposing render scale in
  `GraphicsSettingsController` is the single most effective GPU lever on phones.

**Recommendation:** Make these per-tier: a mobile URP asset (or runtime overrides via
`UniversalRenderPipelineAsset` properties) with shadows-unsupported, MSAA off/2×, render scale
exposed as a setting, plus the GS-2 opaque-texture toggle. Desktop keeps the current values.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — settings/asset configuration, no engine code.
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — variant stripping (build size + load time), bandwidth savings, and a
    > render-scale escape hatch on weak GPUs.
> - **Seed/Save:** ✅ / ✅.

---

### GS-5. Section occlusion culling — underground sections render despite being sealed

**Observed:** Fully empty sky sections are already skipped (no mesh, GameObject disabled), but
**every meshed subsurface section renders** even when completely sealed from the camera by solid
terrain — the "underground overdraw" problem. While walking on the surface above cave systems
(or being inside one), the majority of rendered sections are invisible. A previous count-based
attempt ("render only if connected to the section above, relative to the player") caused major
rendering corruption and was removed — scalar air/opaque counts cannot represent connectivity
topology, so any count heuristic both over-culls (holes) and under-culls. The sound solution is
graph-connectivity culling per `VISIBILITY_CULLING_ARCHITECTURE.md`, whose Phase 0 prerequisites
(section renderers, `nonAirCount`/`opaqueCount`, empty-section skipping) are complete; Phases 1–3
are open.

**Recommendation:** Implement the design doc's connectivity-mask + BFS architecture **with the
corrections in its new §7** (added alongside this entry): accumulated entry-face sets instead of
single-entry visited marks, Checchi direction restriction, `forceRenderingOff` ownership split
from `SetActive` (the likely cause of the old corruption), mask publication synchronized with mesh
apply, conservative defaults, and a position-only PVS without per-step frustum checks. Expected
win: the largest single rendering-side improvement available (draw calls, vertex work, Unity
culling overhead scale with loaded sections), growing further with taller worlds
(`WORLD_SCALING_ANALYSIS.md` Tier A) and carrying over unchanged to cubic chunks (Tier C).

> **Impact Analysis:**
> - **Effort:** 🔴 High — dedicated system (in-job flood fill + visibility manager + ownership
    > refactor), though cleanly phased in the design doc.
> - **Risk:** 🟡 Medium — over-culling bugs are visible holes; §7's rules + debug overlay make
    > them testable. Conservative failure direction (over-render) is designed in.
> - **Benefit:** 🟢 High — most subsurface sections stop rendering in normal play.
> - **Seed/Save:** ✅ / ✅ — masks are derived data, never persisted.

---

## Detailed findings — CPU-Starved Device / OOM Hardening

> **Context:** on a fast desktop (i9-9900K class), production and consumption rates stay roughly
> balanced and the documented §3 weaknesses rarely bite. On CPU-starved hardware (midrange Android),
> the same constants produce the observed failure: fast movement schedules work faster than it can
> drain, every queue grows, pinned chunks can't unload, and the OS kills the process out-of-memory.
> `P-4` (pipeline doc §3) addresses the *production* side. These items add the missing *scaling,
> ceiling, and emergency* layers. All three should be considered prerequisites for shipping on
> Android.

### OM-1. All budgets and caps are desktop-tuned absolute constants

**Observed:** Every throughput and retention knob is a fixed number chosen on desktop hardware:
`maxLightJobsPerFrame = 32`, `maxMeshRebuildsPerFrame = 10`, in-flight mesh cap `20` (hardcoded in
`World.Update`), `ChunkJobArrayPool` retention `512` buffers/type (**≈ 96 MB absolute worst case**
— sized for desktop concurrency per the pipeline doc §1.1 notes), pool prune targets, and default
view/load distances. None of them consult the device. A phone with 3–4 GB of RAM and 4 slow cores
gets the same in-flight memory envelope as a 64 GB desktop — and *lower* caps are actually needed
there twice over: less RAM to hold the backlog **and** fewer cores to drain it.

**Recommendation:** Introduce a device-tier profile resolved once at startup from
`SystemInfo.systemMemorySize`, `SystemInfo.processorCount`, and platform: it scales the per-frame
job budgets, the in-flight job caps, `ChunkJobArrayPool` retention (e.g. `min(512, f(memory))`),
pool prune targets, and clamps the maximum selectable view distance. Per-frame budgets should also
become time-based rather than count-based where P-4 lands (the two compose: tier sets the budget,
P-4 enforces it per-second instead of per-frame).

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a profile struct + plumbing into existing constants.
> - **Risk:** 🟢 Low — conservative tiers can only under-use fast devices until tuned.
> - **Benefit:** 🟢 High on mobile — shrinks every queue and pool ceiling to what the device can
    > actually drain and hold.
> - **Seed/Save:** ✅ / ✅.

---

### OM-2. No memory-pressure response: `Application.lowMemory` unused, no resident budget

**Observed:** Nothing in the codebase subscribes to `Application.lowMemory` (Unity's callback for
the OS memory-pressure signal on Android/iOS), and no system tracks total resident chunk memory.
The engine's only ceiling is "whatever the unloader manages to free" — and the unloader is exactly
what the documented §3.3 pinning problem disables under load. When the backlog wins, there is no
last line of defense between "degraded" and "killed by the OS".

**Recommendation:** Two layers:

1. **Resident-chunk budget (proactive).** Track loaded `ChunkData` count (a cheap proxy for memory;
   optionally refine with per-chunk section counts) against a tier-derived budget (OM-1). Crossing
   the budget triggers the §3.5 panic gate *keyed on memory, not queue length*: stop scheduling new
   generation, shrink the effective load radius, and let consumption catch up. This generalizes the
   pipeline doc's panic gate into the resource that actually kills the process.
2. **`Application.lowMemory` handler (reactive).** On the OS signal: halt generation scheduling,
   force the unload pass with a reduced radius (honoring pipeline invariants — prefer the §3.3 fix
   of persisting pending light columns so pinned chunks become unloadable), set all pool retention
   targets to zero and prune immediately, then `GC.Collect()` + `Resources.UnloadUnusedAssets()`.
   ⚠ Force-unload paths MUST go through the existing unload machinery — bypassing the
   `wouldStrandNeighbor` / pending-lighting checks trades an OOM crash for a lighting deadlock
   (see `chunk-lifecycle` skill).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the budget/gate is simple; the emergency unload must respect pipeline
    > invariants, which is where the care goes.
> - **Risk:** 🟡 Medium — interacts with the deadlock-prone unload gates; test with the benchmark
    > stress run on a memory-capped device/emulator.
> - **Benefit:** 🟢 High — converts the observed hard crash into a visible degradation (shorter
    > view distance, slower streaming).
> - **Seed/Save:** ✅ / ✅.

---

### OM-3. Unbounded concurrent chunk saves on mass unload

**Observed:** `World.UnloadChunks` fires `StorageManager.SaveChunkAsync(data, …)` for every
unloaded chunk (`World.cs` ~line 1986; same pattern at ~3135), each of which snapshots the chunk
and queues a `Task.Run` to the ThreadPool. During fast movement, a single unload pass can launch
**hundreds of concurrent save tasks**: each holds a pooled snapshot until its turn (a memory spike
proportional to the burst, on top of the already-stressed heap), and the ThreadPool spawns/queues
threads that compete with Unity's job workers for the few cores a CPU-starved device has — slowing
down exactly the lighting/meshing drain that the backlog needs.

**Recommendation:** Replace fire-and-forget saves with a **bounded producer-consumer save queue**:
a fixed small number of writer workers (1–2; region files are lock-serialized anyway per
`REGION_FILE_CONCURRENCY.md`, so more writers mostly just contend) consuming from a channel with a
bounded snapshot count. When the bound is hit, defer the unload of further chunks to the next frame
(natural backpressure — the chunk simply stays loaded a little longer) rather than queueing
unboundedly. Shutdown flushes the queue synchronously (the existing cancellation-token path
already models this).

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a save-queue service in `ChunkStorageManager` + unload-path change.
> - **Risk:** 🟡 Medium — must not lose saves on quit/crash (flush ordering), and deferred unload
    > must not fight the OM-2 emergency path (emergency mode should raise the writer count/priority,
    > not bypass the queue).
> - **Benefit:** 🟢 High on weak CPUs — caps the unload-burst memory spike and stops ThreadPool
    > oversubscription from starving the job system.
> - **Seed/Save:** ✅ / ✅ — same bytes written, only scheduling changes.

---

## Suggested implementation order

Grouped into waves by value-for-effort; within a wave, order is free. Capture the relevant
benchmark baseline (`Performance/README.md`) before each wave that touches meshing or lighting.

1. **Quick wins, near-zero risk (one sitting each):**
   ~~MR-1 (Euler hoist) ✅ done — marginal~~, ~~MR-5 ✅ done — chain post-process~~, ~~MR-3 + MR-4 ✅ done — SectionRenderer~~, ~~MR-6 ✅ done — pre-size + pool~~, ~~MR-7 ✅ done — −18% fluid~~,
   ~~MR-9 ✅ done — clouds SetVertices/SetTriangles/SetNormals~~, ~~TG-2 ✅ done — jobified emission + bitmask fallback~~, ~~TG-3 ✅ done — seeded Unity.Mathematics.Random (grass + lava)~~. Remaining: MT-4, MT-5, MT-3. MT-6 (doc/rename only).
   GPU side: GS-3 (vertex-stage lighting) and GS-4 (pipeline tier audit) belong here too.
2. **Android-survivability wave (prerequisite for shipping on weak hardware):**
   OM-1 (device-tier scaling) → P-4 backpressure (pipeline doc §3 — production side) →
   OM-2 (memory budget + `lowMemory` handler) → OM-3 (bounded save queue) →
   GS-2 (opaque-texture opt-out — the biggest mobile GPU lever after GS-1).
3. **Pipeline stabilization (from the pipeline doc, already ordered there):**
   P-5 stable-save bit (⚠️ save migration) → P-3 jobified merge.
4. **Benchmark-gated structural work:**
   ~~MR-2 ✅ done — vertex format (60 B → 32 B/vertex, upload −57%)~~.
   TG-6 (pool the per-chunk `ActiveVoxels` `NativeList` — small, independent; benchmark via `ActiveVoxelScanBenchmark`) →
   GS-1 (baked-noise liquid shader) →
   ~~LI-1 ✅ done — padded lighting volume; layout validated (2.4–3× in-job BFS) but on-demand gather is the cost → NOT shipped standalone, folded into P-2~~ →
   TG-1 (tick path) or directly TG-4 if committing to the full split.
   The GS-5 §7.3 ownership split (`forceRenderingOff` vs `SetActive`) is a small, independently
   harmless PR — now unblocked (MR-3/MR-4 done); do it early so GS-5 stays unblocked.
5. **Long-horizon architecture:**
   **P-2 Layer 1 (worker-thread gather) ✅ SHIPPED 2026-06-22 — banks the LI-1 win net-positive ([benchmark](../Performance/LIGHTING_P2_PHASE1_2026_06_22_BENCHMARK.md)); P-2 Layer 2 (persistent zero-copy storage) remains 🔴 profiler-gated, not triggered ([design](PERSISTENT_CHUNK_STORAGE_P2.md))** →
   GS-5 (section occlusion culling — phased plan in `VISIBILITY_CULLING_ARCHITECTURE.md` §5+§7) →
   MR-8 (greedy meshing — own design doc first).

---

## Verification

- **Benchmarks:** `MeshGenerationBenchmark` for MR-*, `LightingJobBenchmark` for LI-1/P-3,
  `ChunkGenerationBenchmark` as a regression canary (no item here should move it).
- **Meshing correctness (regression guard for MR-*):** the **Meshing Validation Suite**
  (`Minecraft Clone/Dev/Validate Meshing`, `Assets/Editor/Validation/Meshing/`) asserts that an
  output-preserving meshing optimization does not change the generated geometry — it runs the real
  `MeshGenerationJob` against a standard-cube geometry oracle plus structural/determinism invariants.
  Capture-free: keep all baselines green through any MR-* change. Built test-first per the
  `validation-driven-bugfix` skill (the lighting suite's sibling). Fluid/custom-mesh/cross-mesh and
  UV/light *values* are not yet oracle-covered — extend the suite before optimizing those paths.
  **Which harness capability each open MR-* item needs first** (and the phased build order) is
  catalogued in
  [`Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md):
  e.g. MR-2 needs light/UV value oracles, MR-5 needs `MeshPostProcessJob` section-space coverage,
  MR-3 needs a `SectionRenderer` apply-path harness, MR-8 needs a merge-invariant oracle.
- **GC:** Profiler GC-allocation capture during sustained streaming (fly in a straight line at max
  speed) before/after waves 1 and 3 — MR-3/MR-9/TG-3/MT-* should drive steady-state allocations to
  ~zero outside debug UI.
- **Determinism:** For LI-1 and P-3: dump light maps for a fixed-seed test world before/after and
  diff — must be byte-identical. For TG-3: confirm worldgen output unchanged (it must be — the
  change is runtime-only); grass-spread pattern differences are expected and acceptable.
- **Visual:** MR-1/MR-2/MR-4 visual checks (rotated blocks, fluid rendering, section-culling
  bounds, smooth-lighting gradients) are **confirmed in-game**. MR-8 still needs eyes-on checks
  when implemented (merged-quad lighting seams, texture tiling). GS-1/GS-3 need side-by-side
  comparisons per quality tier (water/lava character, lighting gradients via `DEBUG_LIGHTDATA`).
- **GPU:** For GS-*: profile with the Frame Debugger + platform GPU profiler (Android GPU
  Inspector / Snapdragon Profiler on device) — record liquid-pass GPU time over a water-heavy view
  and total frame bandwidth before/after GS-1/GS-2. Desktop GPU timings will *understate* the
  opaque-texture and ALU wins; only on-device numbers count for mobile decisions.
- **OOM stress test:** For OM-*: run the benchmark fast-movement scenario on the weakest target
  device (or a memory-capped Android emulator). Pass criteria: resident memory plateaus instead of
  climbing, `GenerationJobs`/dirty-set counts stay bounded, no `lowMemory`-driven crash, and the
  failure mode under sustained overload is reduced view distance — not process death.
