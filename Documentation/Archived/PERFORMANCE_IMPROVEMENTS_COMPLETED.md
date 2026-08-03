# Performance Improvements — Completed

> **Archived:** 2026-07-26
> **Reason:** `Design/PERFORMANCE_IMPROVEMENTS_REPORT.md` states that items are removed
> (archived) when implemented and verified, but the completed entries had accumulated in it —
> roughly a third of a 2,100-line document described finished work. Their **detail sections**
> live here; their **one-line rows remain in that report's master summary table**, which stays
> the index of the full ID space (so IDs are never recycled) and the landing point for the many
> `MR-*` / `LI-*` / `TG-*` references made from other docs and from code comments.
> Each entry below is reproduced verbatim from the report at the time of archival, including
> its measured before/after numbers.

**See also:** [`../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md)
(the live backlog) · [`CODEBASE_IMPROVEMENTS_COMPLETED.md`](CODEBASE_IMPROVEMENTS_COMPLETED.md)
(the non-performance equivalent).

---

## Meshing & Rendering

### MR-1. ✅ DONE (2026-06-15) — Per-vertex `Quaternion.Euler` in standard cube face generation

> **Closed:** implemented, suite-guarded (`B1`/`B4`), benchmarked, and visually confirmed in-game
> (rotated blocks orient correctly at all yaws). Outcome: **marginal — throughput delta within the
> benchmark noise floor**; kept as a correctness/cleanliness win, not a measured speedup. Retained
> here (not deleted) so the dead-end "hoist for a big win" idea isn't re-proposed. Full record below.

**Observed:** `VoxelMeshHelper.GenerateStandardCubeFace` (`VoxelMeshHelper.cs` ~line 194) computes
`Quaternion.Euler(0, rotation, 0)` and a quaternion-vector multiply **inside the 4-vertex loop**, for **every face of every standard cube voxel** — including the overwhelming majority of blocks where `rotation == 0`. That is trigonometry plus quaternion math per vertex, in the hottest loop of the engine. (The remarks in `MeshGenerationJob.GenerateVoxelMeshData` already note precomputed rotation variants as a Phase 2b idea for *custom meshes*; the standard-cube cost was untracked.)

**Recommendation:**

1. Branch once per face on `rotation == 0` and use the raw vertex position (no math at all) — this covers nearly all terrain.
2. For rotated blocks, hoist the rotation out of the vertex loop and use a precomputed `float3x3`
   per cardinal rotation (0/90/180/270) instead of `Quaternion.Euler`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — localized to one helper, mechanical change.
> - **Risk:** 🟢 Low — verify rotated blocks (e.g. stairs/logs equivalents) still orient correctly.
> - **Benefit:** 🟡 Low/measured — correctness/cleanliness win; throughput delta is below the
>   benchmark's noise floor (see Status). The original "🟢 High — the benchmark will show it" estimate
>   was **not borne out**: oriented blocks are a small fraction of realistic chunks and the per-vertex
>   transcendental is tiny against total meshing cost.
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

- The Color stream is `new Color(1,1,1,1)` for **every non-fluid vertex** — only fluid faces encode data there (liquid type, shore mask).
- TexCoord0's `zw` components are fluid-only (shore push); zeroed for everything else.
- Normals are one of ~10 axis/diagonal directions — they don't need 12 bytes of float precision.

**Recommendation:** Split the fluid-only attributes out of the opaque/transparent submesh layout (fluids already render in their own submesh with their own material), or at minimum: Color →
`UNorm8x4` (4 B), Normal → `UNorm8x4`-encoded direction or an index decoded in the shader. A realistic target is **~32 bytes/vertex (−45%)**, which cuts `SetVertexBufferData` upload time,
`NativeList` memory in every meshing job, and GPU memory/bandwidth proportionally.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — vertex layout, `MeshDataJobOutput`, meshing job writers, and all three
>   shaders (opaque/transparent/fluid) change together.
> - **Risk:** 🟡 Medium — shader/layout mismatches fail visibly; smooth lighting encoding in
>   TexCoord1 must be preserved exactly.
> - **Benefit:** 🟢 High — under chunk streaming, vertex upload is a recurring main-thread cost and
>   this nearly halves it.
> - **Seed/Save:** ✅ / ✅.

---

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
`new Material[3]`, potentially `Array.Resize`s it, and assigns `_meshRenderer.sharedMaterials` on **every mesh update of every section** — 8 sections per chunk, up to 10 mesh jobs per frame. That is GC garbage plus a renderer-state update in the hot apply path, even when the material set didn't change.

**Recommendation:** There are only 7 possible material combinations (any non-empty subset of {opaque, transparent, fluid}). Cache 7 static `Material[]` arrays once, pick by bitmask, and only assign `sharedMaterials` when the combination actually changed since the last update.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — materials are global singletons from `World.Instance`.
> - **Benefit:** 🟡 Medium — removes steady GC churn during chunk streaming (exactly the class of
>   hot-path allocation `GENERAL_OPTIMIZATION_GUIDE.md §5` forbids).
> - **Seed/Save:** ✅ / ✅.

---

---

### MR-4. ✅ DONE (2026-06-18) — `RecalculateBounds()` per section update despite known bounds

> **Closed:** implemented and suite-guarded. `UpdateMeshNative`'s per-update `_mesh.RecalculateBounds()`
> vertex scan is replaced by a constant `s_sectionBounds` (16³ section cell, center (8,8,8)) assigned
> each update — O (1) instead of O (verts). Guarded by **B14** (bounds contain all emitted vertices —
> survives the change) and the new **B16** (bounds *equal* the constant section cell). The "custom mesh
> exceeds the unit cell" caveat is still open via **MH-7** (no custom/cross/lava block in the palette
> yet) — current blocks all stay inside the cell, confirmed in-game. All baselines green.

**Observed:** `UpdateMeshNative` passes `MeshUpdateFlags.DontRecalculateBounds` to every buffer upload, then ends with `_mesh.RecalculateBounds()` (`SectionRenderer.cs` ~line 110) — a full main-thread scan over all vertices of the section, per update.

**Recommendation:** A section's geometry is confined to its 16×16×16 cell (fluid surface heights and cross meshes stay inside block bounds). Assign a constant
`_mesh.bounds = new Bounds(center: 8,8,8, size: 16,16,16)` once. If custom block meshes are ever allowed to exceed the cell, compute min/max in the meshing job per section (almost free there) and pass it through `MeshSectionStats`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — verify no custom mesh asset exceeds the unit cell; oversized bounds are safe
>   (slightly conservative culling), undersized bounds cause visible popping.
> - **Benefit:** 🟡 Medium — removes a per-section main-thread vertex scan from the apply path.
> - **Seed/Save:** ✅ / ✅.

---

---

### MR-5. ✅ DONE (2026-06-18) — `MeshPostProcessJob` blocks the main thread per chunk apply

> **Closed:** implemented and suite-guarded. The chunk-space → section-space rewrite + `InterleavedStream3`
> assembly now chains onto the mesh job at schedule time in `WorldJobManager.ScheduleMeshing`
> (`postJob.Schedule(job.Schedule())`) instead of `Schedule().Complete()` inside `Chunk.ApplyMeshData`.
> By the time `ProcessMeshJobs` completes the combined handle the post-process has already run on a
> worker thread; `ApplyMeshData` only uploads buffers. Guarded by **B10** (chained-vs-separate byte
> equality, incl. `InterleavedStream3`). All baselines green; in-game render confirmed.

**Observed:** `Chunk.ApplyMeshData` (`Chunk.cs` ~line 334) runs
`postProcessJob.Schedule().Complete()` — a synchronous main-thread stall for the chunk-space → section-space coordinate rewrite — once per completed mesh job, inside the frame's apply budget.

**Recommendation:** Chain `MeshPostProcessJob` onto the mesh job handle at schedule time in
`WorldJobManager.ScheduleMeshing` (`Handle = postJob.Schedule(meshJobHandle)`). By the time
`ProcessMeshJobs` sees the handle completed, the post-process has already run on a worker thread, and `ApplyMeshData` only uploads buffers.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — move the job construction; `MeshingJobData.Handle` already carries the
>   combined handle pattern.
> - **Risk:** 🟢 Low — the post-process job only touches the output buffers, which already live
>   until `ProcessMeshJobs`.
> - **Benefit:** 🟡 Medium — removes a fixed main-thread cost per mesh completion (up to 10/frame).
> - **Seed/Save:** ✅ / ✅.

---

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

**Observed:** `MeshDataJobOutput` (`JobData.cs`) creates all 9 output lists with the default initial capacity. A typical surface chunk emits tens of thousands of vertices, so every meshing job pays a chain of grow → reallocate → memcpy cycles inside the job; and the whole struct is allocated then disposed (Persistent) per chunk, adding native alloc/free churn.

**Recommendation:** Pre-size with a sensible initial capacity (e.g. vertices ≈ 16–24k, triangles proportional — derive from the meshing benchmark's median), or carry forward the chunk's previous mesh size as the estimate. Optionally pool whole `MeshDataJobOutput` instances alongside
`ChunkJobArrayPool` so the capacity survives across jobs (note: `NativeList` retains capacity on
`Clear()`, so pooling fully amortizes growth).

> **Impact Analysis:**
> - **Effort:** 🟢 Low (pre-size) → 🟡 Medium (pool the output struct).
> - **Risk:** 🟢 Low — over-sizing only costs memory; pooling must respect the existing
>   "dispose after `ApplyMeshData`" lifecycle.
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

---

### MR-7. ✅ DONE (2026-06-15) — Per-fluid-voxel `Allocator.Temp` arrays in the meshing job

> **Closed:** implemented, suite-guarded (`B7`/`B8`), and benchmarked with a **real measured win** —
> **−18% on the fluid pattern** (1365 → 1115 μs/chunk). Full record below; `MR-7b` (stackalloc, no threading) logged as a deeper future option.

**Observed:** `MeshGenerationJob.GenerateVoxelMeshData` (`MeshGenerationJob.cs` ~line 320) allocates
`new NativeArray<OptionalVoxelState>(14, Allocator.Temp)` + `new NativeArray<ushort>(14, Temp)` and disposes both **per fluid voxel**. An ocean chunk does this thousands of times per job. Temp allocations are cheap, but not free at that frequency.

**Recommendation:** Hoist both 14-element buffers to `Execute()` scope and reuse them across voxels (they are fully rewritten per voxel), or replace with fixed-size struct buffers (`FixedList`/`stackalloc`-style) since the size is a compile-time constant.

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
`mesh.vertices = vertices.ToArray()` etc. (`Clouds.cs` ~lines 210–212, 266–268) — three temporary managed arrays per cloud tile creation.

**Recommendation:** Use `mesh.SetVertices(list)` / `mesh.SetTriangles(list, 0)` /
`mesh.SetNormals(list)` (accept `List<T>` directly), or the NativeArray mesh API for parity with
`SectionRenderer`.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — direct API substitution.
> - **Risk:** 🟢 Low — cloud meshes are visually simple.
> - **Benefit:** 🟡 Medium — eliminates GC spikes during cloud tile (re)generation.
> - **Seed/Save:** ✅ / ✅.

---

---

## Lighting

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
(`NeighborhoodLightingJob.cs` ~lines 814–891) walks an up-to-9-way branch tree to select the correct neighbor array (own / N / S / E / W / NE / NW / SE / SW), and any boundary position additionally pays a `NativeHashMap<long, ulong>` lookup for the write-through cache. This runs **per neighbor, per BFS node** — millions of times per lighting job — and defeats Burst vectorization in the innermost loop.

**Recommendation:** Build the job input as a **single padded volume** instead of 9 separate maps — e.g. an 18×128×18 array with a 1-voxel halo (sufficient for face-neighbor BFS reads), or 48×128×48 if deep cross-chunk propagation reads beyond the halo. The inner loop becomes a branch-free flat index, and the read side of the write-through hashmap cache disappears (writes to the halo become plain array writes, harvested into `CrossChunkLightMods` at the end).

**Trade-off note:** This *increases* schedule-time copy work, which runs counter to
`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md §1.2` (copy *less* per job). They optimize different costs: §1.2 attacks main-thread schedule time, LI-1 attacks in-job BFS time. The right call needs a benchmark of both — and the long-term resolution is §1.3/P-2 (persistent native storage), which can satisfy both if the persistent layout itself is halo-padded.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — touches job input layout, `FillChunkLightMapForJob` fill paths, and the
>   pool (`ChunkJobArrayPool` buffer sizes change).
> - **Risk:** 🟡 Medium — light output must be **bit-identical** before/after; validate with
>   `LightingJobBenchmark` and a fixed-seed world diff of light maps.
> - **Benefit:** 🟢 High — directly attacks lighting job self-time, the engine's dominant background
>   cost during streaming.
> - **Seed/Save:** ⚠️ Seed-safe for terrain, but lighting results **must** remain deterministic and
>   identical — any divergence re-dirties the edge-check cascade (§4 of the pipeline doc) on old
>   saves. Treat "identical light output" as a hard acceptance criterion. / ✅ no format change.

> **Validation prerequisite (cross-border darkening coverage).** "Bit-identical light output" only has
> teeth on the seam if the suite actually exercises a *darkening* wave crossing a chunk border — the
> halo's hardest read. The lighting suite covers cross-chunk *brightening* fuzz (C1/C2, B40–B44) and now the
> *darkening* quadrant too:
> [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
> **C3 (B54/B55, CLOSED 2026-06-21)** — keep it green when freezing any halo-vs-9-map diff for LI-1.

---

---

### LI-2. Halo gather/extract copies the full column height regardless of content

> **✅ IMPLEMENTED 2026-07-11** (`feat/async-lighting-validation-suite`) — shipped default-on behind
> `World.EnableLightingBandGather` (rollback flag, **retired 2026-07-24** after IL2CPP GO-final + soak: the band
> gather is now unconditional on the pooled steady-state path; the harness keeps its own `FullHeight` mode as the
> differential oracle; TempJob startup sweep stays full-height by design). The lighting job
> now gathers/scans/extracts only the derived bottom-anchored Y-band `[0, bandHeight)`; reads above answer virtually
> from a per-chunk uniform-region summary. **Bit-identical** by the `LightingBandDecision` rules (coverage + one
> headroom section, column-recalc, cross-seam consistency), proven by the **B75–B78** banded-vs-full differential
> (incl. the C3 cross-chunk darkening quadrant, a 12-seed fuzz, and a headroom-strip prove-red) + **B71–B74**
> derivation baselines — `Validate Lighting Engine` 70/70. Editor screening: **−31…−75 %** on the gather/scan-dominated
> job shapes (no-op relight, edge check), wave-carrying jobs bounded by the irreducible BFS; never slower on the clean
> floor. **Shippable IL2CPP/in-game frame A/B (confirmed):** settled-streaming frame **−26 %** / Light **−27 %**, flood
> sustained Light **−9 %** with lighting no longer the worst-frame bottleneck (share 61 %→29 %) — a sustained frame win,
> not merely "not slower" — see
> [`Performance/LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md`](../Performance/LIGHTING_LI2_INGAME_IL2CPP_2026-07-11_BENCHMARK.md)
> (editor screening: [`LIGHTING_LI2_2026-07-11_BENCHMARK.md`](../Performance/LIGHTING_LI2_2026-07-11_BENCHMARK.md)). Core:
> `Assets/Scripts/Helpers/LightingBandDecision.cs` + `ChunkData.GetLightingBandTop` + `WorldJobManager.ScheduleLightingUpdate`.
>
> **✅ LI-2b BOTTOM BAND IMPLEMENTED 2026-07-11** (same branch; v1 shipped top-only by scope decision — this closes the
> deferred half). The band is now the full range `[bandMinY, bandHeight)`: rows below an **inert-dark region** (light
> uniformly 0, no emitters) are also skipped, stored as a band-local prefix of the padded volume. Enabler: per-section
> **emissive-presence metadata** (`ChunkSection.emissiveCount`, maintained via the palette-independent
> `Helpers/EmissiveBlockLookup` — runtime-only like `opaqueCount`, **no save-format change**). Bottom rules in
> `LightingBandDecision.DeriveBandMinY`: inert-dark coverage over all 9 chunks (`ChunkData.GetLightingBandBottom`),
> headroom under the lowest queued node, `min(center heightmap) − headroom` (the unbounded downward vertical-sunlight
> rule has no attenuation to lean on), and **any column recalc → 0** (PASS 2 walks to Y=0; no downward full-sky escape
> exists) — so floods/initial lighting stay effectively top-only and the wins accrue on settled-streaming re-lights.
> Cross-seam needs no bottom rule (0-vs-0 is inert); the emissive gate also covers the RGB edge check's opaque-emission
> substitution on cardinal halos. Same single flag (`EnableLightingBandGather`; rollback = full height). **Bit-identical**
> proven by **B83–B85** (bottom differential with an *engagement assertion* — a never-engaging bottom cannot vacuously
> pass — 8-seed deep-floor fuzz, raised-floor prove-red) + **B79–B82** derivation baselines — suite 77/77, in-game
> underground lamp verification. Editor screening
> ([`LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md`](../Performance/LIGHTING_LI2B_BOTTOM_BAND_2026-07-11_BENCHMARK.md)):
> another **−49…−59 % on top of the shipped top band** where the bottom engages (deep/mid floors, no-op relight + edge
> check — combined −70…−73 % vs pre-LI-2 full height), parity where it cannot. **IL2CPP in-game flag A/B (captured
> 2026-07-11): frame-neutral — GO on the not-slower + Tier-A basis** (flood/settled deltas within the session's noise
> floor; flood is recalc-driven so the bottom is 0 there by rule — the engaged wins live in settled-streaming
> re-lights, per the plan), see
> [`Performance/LIGHTING_LI2B_INGAME_IL2CPP_2026-07-11_BENCHMARK.md`](../Performance/LIGHTING_LI2B_INGAME_IL2CPP_2026-07-11_BENCHMARK.md)
> (which also documents that `LightingJobBenchmark` pins full height in both builds — its per-job deltas there and in
> the LI-2 in-game report are the build/session noise floor, not band effects).
> The recommendation below is the as-designed record.

*(Surfaced by the 2026-07-02 third-pass audit. This is the concrete, tracked form of
`WORLD_SCALING_ANALYSIS.md` §2.2's "jobs must become section-ranged" Tier A prerequisite.)*

**Observed:** P-2 Phase 1's worker-thread gather fills the full 20×128×20 halo volume (and the extract walks it back out) for every lighting job, regardless of how much of that height can actually carry light changes. Most columns are vertically dominated by uniform regions — sky above the heightmap (which `SectionUniformSkyLevel` already identifies per section) and unlit/uniform depths — that are copied, seed-scanned, and extracted anyway. The tooling for a bounded copy already exists and is proven: the TG-4 Y-band ships on
`ChunkMath.GatherPaddedRange`, whose `[0,128]` case *is* the full-height case, and its serial fluid A/B cut worst-tick tails −24…−46%. Notably, the fluid Y-band came back frame-neutral in-game precisely because the flood frame is **Light-bound (~66–70%)** — the lighting gather/extract is where the same idea has frame-level payoff, and it is the next open item on the "lighting line" that TG-4's closing analysis pointed at.

**Recommendation:** Bound the lighting gather/extract (and BFS seed scans) to the Y-range that can carry non-uniform light, derived conservatively from: the 3×3 neighborhood's column heightmaps,
`SectionUniformSkyLevel` / per-section `IsEmpty` flags, the Y-extent of the queued BFS nodes, and
`MAX_LIGHTING_BFS_REACH` padding. **This is harder than the fluid band:** sunlight propagates vertically through the whole column and the darkening path reads ±2 across seams — a too-tight band produces exactly the cross-chunk darkening bugs C3 guards against. Treat the band derivation as the design problem; the copy mechanics are done.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — band derivation + plumbing through the P-2 Phase 1 gather; the ranged
>   copy machinery already exists.
> - **Risk:** 🔴 High — lighting semantics; a too-tight band truncates sunlight columns or darkening
>   waves. Hard acceptance criterion: **bit-identical light output**, full lighting suite green
>   (incl. C3 darkening baselines B54/B55) plus a fixed-seed in-game light-map diff.
> - **Benefit:** 🟢 High — attacks the dominant sustained cost (lighting, ~66–70% of flood/ocean
>   frames) and is simultaneously the Tier A scaling prerequisite (640-high columns make
>   full-height copies prohibitive — `WORLD_SCALING_ANALYSIS.md` §2.2).
> - **Seed/Save:** ⚠️ same contract as LI-1 (terrain-safe, but light output must remain identical —
>   any divergence re-dirties the edge-check cascade on old saves) / ✅.

---

---

## Tick & Gameplay

### TG-2. ✅ DONE (2026-06-20) — `OnDataPopulated` full-chunk scan through managed `BlockType` objects

> **Closed:** implemented and differential-verified. Both halves of the recommendation shipped:
> - **Jobified emission (generation path).** A new single-threaded Burst `ActiveVoxelScanJob`
>   (`Assets/Scripts/Jobs/ActiveVoxelScanJob.cs`) runs as the *final* generation pass — scheduled
>   after the cave-isolation filter in `StandardChunkGenerator.ScheduleGeneration` so it reads the
>   finalized voxel map. It walks the map once and appends the flat chunk index
>   (`ChunkMath.GetFlattenedIndexInChunk` convention) of every voxel whose `BlockTypeJobData.IsActive`
>   is set into a new `GenerationJobData.ActiveVoxels` (`NativeList<int>`). On the main thread,
>   `WorldJobManager.ProcessGenerationJobs` STAGE 1 calls `Chunk.RegisterActiveVoxelsFromJob`, which
>   unpacks each index (`ChunkMath.GetLocalPositionFromFlattenedIndex`, the new inverse helper) and
>   registers it — copying a short list instead of dereferencing managed `BlockType` objects up to
>   32k times per chunk.
> - **Bitmask fallback scan (load + reset-replay paths).** `World.PrepareGlobalJobData` now builds a
>   flat `bool[] World.IsActiveById`. `Chunk.OnDataPopulated` keeps its section-skipping scan but
>   indexes that array instead of `World.Instance.BlockTypes[id].isActive` — a flat read, no object
>   deref. This path serves only **load-from-save** (`World.LoadOrGenerateChunk` → `PopulateFromSave`)
>   and **pool-recycle replay** (`Chunk.Reset` when `ChunkData.IsPopulated`), where no generation job
>   runs. Active voxels are deliberately **not persisted** (see the serialization architecture doc),
>   so these paths must always rescan — the jobified list is unavailable there. Generators that do not
>   run the scan pass (e.g. the legacy generator) leave `ActiveVoxels` uncreated, and STAGE 1 falls
>   back to this scan.
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
>   iterating all 32 768 voxels on the main thread to find ~0 active blocks (pure overhead); that is
>   now **~0.04 µs** — the scan moved to a Burst job that overlaps the generation jobs already in
>   flight. The reduction is largest exactly where it matters in normal play (sparse actives).
> - **Part B (load/replay path).** Flat `bool[]` vs the managed deref is **~13 % faster** on the scan
>   itself (37.7 → 33.3 µs); free, and the only path available for saves (actives aren't persisted).
> - **Honest caveat.** For *active-heavy* chunks the Part A main-thread reduction shrinks to ~10 %
>   (400.7 → 366.7 µs) because the bottleneck there is `Chunk.AddActiveVoxel` — the
>   `HashSet<Vector3Int>` inserts (~366 µs for 12k actives), which **both** versions pay. The scan
>   over all 32k voxels is only ~32 µs. So if active-heavy chunks ever profile hot, the next target is
>   the active-voxel *container/population* (cf. TG-1, TG-4), not the scan.

**Observed:** `Chunk.OnDataPopulated` (`Chunk.cs` ~lines 177–205) scans every voxel of every non-empty section on the main thread when a chunk's data arrives, dereferencing
`World.Instance.BlockTypes[id].isActive` — a managed class array → object → field chain per voxel (up to 32k per chunk) with poor cache behavior.

**Recommendation:** Precompute a `bool[]` (or 64-bit bitmask array) of "is active" per block ID once at startup and index that instead — flat, cache-friendly, no object dereference. Longer term, emit the active-voxel list from the generation job itself (it already touches every voxel in Burst) so the main thread only copies a short list.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (bitmask) → 🟡 Medium (jobified emission).
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — reduces the activation stutter when chunks stream in.
> - **Seed/Save:** ✅ / ✅.

---

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
in the grass-spread tick path. `ChunkLoadAnimation.cs` / `Toolbar.cs` also use it, but only in cold initialization code (low priority).

**Recommendation:** Use `Unity.Mathematics.Random` seeded per-chunk or per-tick in
`BlockBehavior.cs`. Deterministic, thread-safe, Burst-compilable — a prerequisite for TG-4/TG-5.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low.
> - **Benefit:** 🟡 Medium — removes global lock contention; unblocks Burst compilation of behaviors.
> - **Seed/Save:** ⚠️ Seed-safe for terrain (worldgen RNG is untouched), but the **runtime RNG
    > sequence changes**: grass-spread and similar behavior patterns will differ from the old
>   implementation for the same world. Cosmetic only — no save/migration impact. / ✅.

---

---

### TG-4. `BlockBehavior` data separation (ECS/DOTS pattern)

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §6.1.)*

> **Detailed design:** [BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md](../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md) —
> phased plan (BH-D1 infra → per-family storage split → grass Burst → fluid Burst → parallelize + Tier-2),
> with the BH-D1 old-vs-new differential slotted into each phase gate.
>
> **Status (2026-07-23): FULLY IMPLEMENTED + CLEANED UP — Phases 0–1 + 3 + 4a + 4b + Y-band SHIPPED; Phase 2
> skipped; the flag-gated-fallback cleanup pass is DONE (2026-07-23) — the parallel Y-band halo tick is now
> unconditional (no rollback flags), harness pruned to the surviving `BH-D1[L|HB]` + Y-band determinism gates,
> Validate All 333/16 green.** Phase 0 (BH-D1 differential infra) + Phase 1
> (per-family `NativeHashSet<int>` active-voxel buckets — landed on **`ChunkData`**, not `Chunk`; tick orchestration
> stays on `Chunk`) are in-game confirmed. **Phase 3** Burst-ticks Tier-1 interior fluids (`FluidTickJob`, border
> managed) gated by `BH-D1[L|F]`; **Phase 4a** parallelizes those interior jobs across chunks
> (`World.ProcessTickUpdatesParallel`, worker-count guarded) gated by a parallel-vs-serial determinism suite + an
> 8-run IL2CPP A/B; **Phase 4b** closes the Tier-2 border — **every** fluid (interior AND border) is Burst-ticked,
> border voxels reading a per-tick **9-snapshot neighbor halo** via the **§4.2 option (b) per-tick local gather**
> (`ChunkMath.GatherPaddedFluidVoxelsBand`), gated by `BH-D1[L|HB]` + a cross-chunk determinism stress + in-game; and the
> **Y-band** (2026-06-27) sizes that gather to the active-fluid Y-extent (height-independent copy,
> `ChunkMath.GatherPaddedFluidVoxelsBand`), gated by `BH-D1[H|HB]`/`[L|HB]` + the Y-band determinism stress +
> in-game. **Phase 2 (grass) skipped** (negligible cost). The
> new runtime buckets are pool-retained (no per-recycle churn — **TG-6-aligned**; TG-6's own target, the
> `GenerationJobData.ActiveVoxels` hand-off list, is now pooled too — shipped 2026-06-27).
>
> **Important — option (b), NOT a P-2 Layer 2 dependency.** Phase 4b deliberately took the **TG-4-local per-tick halo
> gather** (option (b)), so it ships **standalone** with no chunk-storage commitment — TG-4 does **not** depend on
> [P-2 Layer 2](PERSISTENT_CHUNK_STORAGE_P2.md) (persistent zero-copy storage), which stays 🔴 profiler-gated and is a
> *separate, optional* future optimization of the same gather (it would let the halo read neighbor cores zero-copy).
>
> **Net (attribution gates CLOSED across five captures —**
> [`…FLUID_TICK_2026_06_23`](../Performance/BEHAVIOR_TG4_FLUID_TICK_2026_06_23_BENCHMARK.md) (isolated tick
> ~21 ms/tick), [`…FULLWORLD_FLUID_2026_06_23`](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_2026_06_23_BENCHMARK.md)
> (tick owns the **GC-bound ~180 ms dam-break spike**; Phase 3 → ~143 ms; sustained frame **lighting-dominated
> ~66 %**), the [Phase-4a A/B](../Performance/BEHAVIOR_TG4_FULLWORLD_FLUID_PARALLEL_2026-06-24_BENCHMARK.md)
> (interior-parallel shaves a further **~6.6 ms / ~4.6 %** off the spike), the
> [Phase-4b halo A/B](../Performance/BEHAVIOR_TG4_PHASE4B_HALO_AB_2026-06-24_BENCHMARK.md) (Bursting the border makes
> the **tick** 1.70–2.15× faster, GC-spike tail removed), and the
> [Y-band A/B](../Performance/BEHAVIOR_TG4_PHASE4B_YBAND_AB_2026-06-27_BENCHMARK.md) (serial worst-tick tail
> −24–46 %, **frame-neutral** in-game)**): the fluid tick is now fully Burst + parallel with a flat, predictable cost
> — but it was **never the frame bottleneck.** The sustained ocean frame stays **lighting-dominated (~66–70 %)**, so
> ocean smoothness needs the **lighting line** (LI-1 / [P-2](PERSISTENT_CHUNK_STORAGE_P2.md)), not (only) the
> tick. TG-4 removed the stutter *spike* and made the tick scale across cores; the *average* frame cost is the
> lighting engine's to win. The 🔴/🔴 effort/risk ratings below describe the (now-completed) work's nature.

**Observed:** All ticking voxels (fluids, grass, future behaviors) flow through one monolithic collection and a central `switch` in `BlockBehavior`. As behavior types grow, this forces a single main-thread tick loop iterating unrelated voxel types.

**Recommendation:** Split active voxels by behavior type into dedicated native collections (e.g. `_activeFluids`, `_activeGrass`) so each behavior runs as its own independent Burst job — cache-local, parallelizable, and off the main thread.

> **Impact Analysis:**
> - **Effort:** 🔴 High — re-architects the tick pump and active-voxel registration.
> - **Risk:** 🔴 High — touches the core world ticking engine; fluid parity testing required.
> - **Benefit:** 🟢 High — scales across cores; the only path that gets ticking fully off the main
>   thread. Subsumes TG-1 if done wholesale (TG-1 is the incremental version).
> - **Seed/Save:** ✅ / ✅.

**Parity guard (prerequisite):** the "fluid parity testing required" note above is satisfied by the behavior-tick validation harness in
[BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md)
— **built (Waves 0–2, 8 baselines green, 2026-06-21)**; land the old-vs-new differential baseline (BH-D1) in the TG-4 PR itself. The harness's seam table (S1–S5) also enumerates the exact `World.Instance` couplings this split must sever.

---

---

### TG-6 ✅. Per-chunk `ActiveVoxels` `NativeList<int>` alloc/free churn — pool it (TG-2 follow-up)

*(Surfaced by the 2026-06-21 behavior-suite review, finding #4. Shipped 2026-06-27.)*

**Was:** TG-2's jobified emission allocated a fresh `NativeList<int>` per chunk generation —
`new NativeList<int>(StandardChunkGenerator.ActiveVoxelPresizeCapacity, Allocator.Persistent)` (2048 ⇒ 8 KB) in `StandardChunkGenerator.ScheduleGeneration`, stored in `GenerationJobData.ActiveVoxels`, and freed per chunk in `GenerationJobData.Dispose`. During streaming this was per-chunk Persistent allocate-and-free churn — exactly the repeated-allocation pattern CLAUDE.md says to pool — and the 8 KB was reserved up front even for the common sparse-actives chunk (which emits ~0 indices).

**Shipped:** new `Helpers/ActiveVoxelListPool.cs` (mirrors **MR-6**'s `MeshOutputPool`: `Rent`/`Return`/
`Dispose`, `Clear()` on return retains capacity, `MAX_RETAINED` cap self-disposes overflow). `NativeList`
retains its allocated capacity across `Clear()`, so a warmed pool also removes the realloc-and-copy growth a water-heavy chunk (thousands of source voxels) otherwise pays inside the scan.
`IChunkGenerator.ScheduleGeneration` gained an optional `ActiveVoxelListPool` parameter (default `null`):
`WorldJobManager` passes its owned pool on the production path; editor / preview / benchmark callers pass
`null` and keep the fresh-alloc + `Dispose` path. A `GenerationJobData.ActiveVoxelsFromPool` flag routes the release — `Dispose` frees the list only when **not** pool-owned.

**Release-path design (the part that mattered).** The first cut returned the list mid-pipeline at the STAGE-1 consume site; a `/code-review` found that left a **stale handle on the lingering job** (a budget-exhausted job stays enrolled in `GenerationJobs` after STAGE 1), which `WorldJobManager.Dispose`
then **re-returned → double-push → double-dispose** at shutdown. The fix moved the return to a single terminal release helper, `WorldJobManager.ReleaseGenerationJobData` (mirroring `ReleaseLightingJobData` /
`ReleaseMeshingJobInputs`), co-located with `Dispose` at the terminal completion **and** the shutdown loop. Because a job is removed from `GenerationJobs` the instant it reaches terminal completion, and shutdown only releases still-enrolled jobs, each job's list is returned **exactly once** — no stale-handle window. Native-container lifetime is respected: the return sits strictly after `Handle.Complete()`.

> **Impact Analysis (as shipped):**
> - **Effort:** 🟡 Medium — pool type + threading it through the generator interface + the terminal-release split.
> - **Risk:** 🟡 Medium — native-container lifetime / use-after-free (the double-dispose the review caught);
>   de-risked by routing all release through one post-`Complete()` helper.
> - **Benefit:** ⚪ Low — removes per-chunk 8 KB Persistent alloc/free during streaming and the realloc
>   growth on active-heavy chunks once the pool warms, but this is **native** (not GC) churn, sub-µs and
>   mostly off the main thread; frame-neutral by construction (see footnote ³). No tick-path cost change.
> - **Seed/Save:** ✅ / ✅ — active voxels are not persisted; pooling is an internal allocation concern.

**Validation (no dedicated benchmark — by design).** The win is a `Persistent` (native, not GC) alloc that no frame benchmark can resolve above its noise floor, so the gate was reframed from "before/after speedup"
to **no-regression on two IL2CPP harnesses**: the full-world fluid stress pass (`FluidStressPass`) and the isolated tick bench (`FluidTickBenchmark`) both came back frame-neutral across 3 runs each — uniform sub-2% deltas with no code path linking the pooling change to either hot path (settled/flood frame is Light-bound
~69%; the tick path is `Chunk.TickUpdate`, which TG-6 never touches). Neither validates the *win*; together they confirm the refactor (incl. the double-dispose fix) is safe. `ActiveVoxelScanBenchmark` was **not**
extended — it is editor/Mono-only and cannot capture IL2CPP.

The win *is* isolated by the runtime `ChunkGenerationBenchmark`, extended (2026-06-27) with a fresh-vs-pooled leg over Land (sparse) and Ocean (raised sea level → water-heavy, active-list realloc growth) scenarios, 64 chunks/run, and `sched µs/ch` + `free µs/ch` columns narrowed to the main-thread schedule/release window where the per-chunk alloc lives. Across 3 IL2CPP runs the pooled leg shaves a stable **~0.6 µs/ch off schedule (~5%)** and **~0.35 µs/ch off release (~14–17%)** — consistent in sign across all scenario×run combinations — for ~0.95 µs/ch
of main-thread time per chunk. `total ms/ch` (~1.58 ms) shows no leg advantage: it is dominated by the worker-side generation `Complete()`, so the Ocean realloc saving is real but sub-noise against it. The benchmark is retained as a standing generation-path regression guard and comparison-grade fixture for any future dedicated-generation work.

**Also closed (the rest of review finding #4):** the `2048` magic number is extracted to
`StandardChunkGenerator.ActiveVoxelPresizeCapacity` (the benchmark pins to it, no drift), and the dispose-path no-leak invariant is documented on `GenerationJobData.Dispose`.

---

---

## Main Thread & Miscellaneous

### MT-1. `List.Insert(0)` / `RemoveAt(i)` — O (n) mesh priority queue ✅ DONE

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §3.1; overlaps pipeline doc §5.1.)*

**Resolution (2026-07-01):** Replaced the `List<Chunk> _chunksToBuildMesh` + companion
`HashSet<ChunkCoord>` with a single dedicated `Helpers/MeshBuildQueue.cs` — a **pooled intrusive doubly-linked list** (parallel `next`/`prev`/`chunk`/`coord` arrays threaded by a free-list) plus a
`coord → slot` `Dictionary` serving both duplicate rejection and O (1) removal. Every operation is now O (1): immediate enqueue links at the head (newest-first / LIFO — matches the old `Insert(0)`), normal enqueue links at the tail (FIFO — matches `Add`), the scheduling drain removes the current node via a mutating struct `Enumerator` (replaces mid-list `RemoveAt(i)`), and the unload paths remove by coordinate (replaces O (n) `Remove(chunk)`). Ordering is **bit-identical** to the old list (all immediates ahead of all normals; retain-on-not-ready
preserved), and slot recycling makes it zero-GC in steady state. `PriorityQueue<,>` (the distance-keyed option below) was rejected: it is absent from Unity's Mono/.NET Standard 2.1 runtime and supports neither arbitrary removal nor retain-in-place. In-game confirmed; the O (n) unload-removal bug (`CHUNK_MANAGEMENT_BUGS.md #01`) is archived. A **normal→immediate priority promotion** on re-request was identified as a latent behavior gap and kept out of this no-op refactor, then shipped as a separate follow-up (2026-07-01): an immediate re-request of an
already-queued chunk now promotes it to the head (O (1) `MoveToHead` in `TryEnqueue`), so a fresh player edit meshes ahead of streaming work it was previously stuck behind. Guarded by baseline B9 in the Mesh Build Queue suite (prove-red confirmed; B2 narrowed to the surviving normal-dedup no-reorder guarantee).

**Observed:** The meshing pipeline uses `List<Chunk> _chunksToBuildMesh` as a priority queue —
`Insert(0, chunk)` and mid-list `RemoveAt(i)` are O (n) shifts (`World.cs`, scheduling loop ~line 1270 and the insert/remove sites around lines ~1022/1033/1607, plus unload paths at ~2156). With a large backlog (exactly the §3 cascade scenario) this goes quadratic.

**Recommendation:** Replace with a real priority structure — `PriorityQueue<Chunk, int>` keyed by distance, or two queues (priority/normal) if only front-insertion matters. Keep the companion
`HashSet` for dedup.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — iteration/removal patterns around the list must adapt.
> - **Risk:** 🟡 Medium — meshing order affects visual pop-in; test streaming visually.
> - **Benefit:** 🟢 High under backlog; modest in calm play.
> - **Seed/Save:** ✅ / ✅.

---

---

### MT-2. ✅ DONE (2026-07-02) — Light scheduler snapshots the full dirty set every frame

> **Closed:** ready/waiting split shipped and in-game verified. The dirty set now lives in
> `LightWorkScheduler` (`Assets/Scripts/Helpers/LightWorkScheduler.cs`): the per-frame scan iterates
> only a **ready** set, and a chunk whose readiness gate fails (unpopulated, lighting job in-flight,
> or all schedule branches blocked) is parked in a **waiting** set the scan never visits. Parked
> chunks re-enter ready only on the events that can flip their gate — terrain generation completed
> (`ProcessGenerationJobs` removal sweep), disk load hydrated (`PopulateFromSave` in
> `LoadOrGenerateChunk`), lighting job completed (`ProcessLightingJobs` removal sweep), or the chunk's
> own flag transition (staging callback) — via `World.PromoteLightWorkNeighborhood` → move-only 3×3
> `PromoteNeighborhood`. The 1-second fail-safe scan is retained and now also calls `PromoteAll()`, so
> a missed promotion degrades to ≤1 s of latency instead of a permanent stall; under
> `enableDiagnosticLogs` a recurring non-zero fail-safe-promotion count is logged as a missing-hook
> sentinel. **In-game wave-front stress logged zero fail-safe promotions** — every unblock path is
> event-covered, the backstop never fired. Guarded by the `Validate Light Work Scheduler` editor suite
> (9 baselines, prove-red B2/B4 confirmed); `Validate Lighting Engine` stayed 47/47 green. Docs synced:
> `CHUNK_LIFECYCLE_PIPELINE.md` §4/§9.1/§10, `LIGHTING_SYSTEM_OVERVIEW.md` §3.2,
> `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` panic-gate note.

**Observed:** `World.Update` (`World.cs` ~lines 1171–1256) copies the entire
`_chunksNeedingLightWork` set into a pooled list every frame and iterates all of it — even when
`maxLightJobsPerFrame` is exhausted after the first few entries, and even for chunks whose neighbor-readiness gates will fail identically to last frame. Cheap in calm play; O (dirty) per frame during exactly the backlog scenarios where frames are already slow (compounds pipeline §3).

**Recommendation:** Split the dirty set into "gate-ready" and "waiting" subsets: chunks enter gate-ready when the event that could unblock them occurs (neighbor populated / neighbor lit — hooks already exist at those transitions). The per-frame loop then iterates only schedulable work and stops at the throttle. ⚠ Respect the flag-pairing invariants in
`CHUNK_LIFECYCLE_PIPELINE.md` — the current full rescan doubles as a self-heal (see also the 1-second fail-safe scan, pipeline doc §5.2), so keep that fail-safe in place.

> **Impact Analysis:**
> - **Effort:** 🟢 Low→🟡 Medium depending on how event-driven the ready set becomes.
> - **Risk:** 🟡 Medium — a chunk that never enters the ready set stalls lighting (deadlock
>   history!); the fail-safe scan must remain as backstop.
> - **Benefit:** 🟡 Medium — trims fixed per-frame overhead precisely when FPS is lowest.
> - **Seed/Save:** ✅ / ✅.

---

---

### MT-3. ✅ DONE (2026-06-27) — `DebugScreen` intermediate string allocations per refresh

> **Closed:** zero-alloc refresh implemented and in-game verified. All `.ToString()`/`$"..."` sites
> replaced: numeric `Append` overloads + a shared `Helpers/UI/StringBuilderFormat.cs` (`AppendFixed`,
> `AppendFixedPadded`, `AppendIntPadded`, `AppendBytes`, `AppendMs`, `AppendHex2`, `AppendElapsedTime`),
> TMP `SetText(StringBuilder)` at the assignment seam, the constant `graphicsDeviceType` cached once,
> and the `[Flags]` `BlockTags` + `DebugVisualizationMode` enum `ToString()` boxing replaced with
> declaration-order appenders / literal mappers (output-parity confirmed against both enum definitions).
> `World.GetMeshQueueDebugInfo()` → `AppendMeshQueueDebugInfo(StringBuilder)`. `BenchmarkHUD`'s three
> private formatters were folded into the shared helper (single source of truth). Player/IL2CPP builds
> are zero-alloc; under `UNITY_EDITOR` TMP's `SetText` still materializes one inspector string (compiled
> out of player builds).

**Observed:** Despite the cached `StringBuilder`s, each refresh allocates dozens of temporaries:
`.ToString()` calls on numbers feeding `Append` (`DebugScreen.cs` ~lines 383–396), plus `$"..."`
interpolation inside `AppendLine(...)`. Only costs while the debug screen is visible.

**Recommendation:** Use the numeric `Append(int)`/`Append(float)` overloads and replace interpolated `AppendLine($"...")` with chained `Append` calls. Zero-alloc refresh is achievable.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (tedious but mechanical).
> - **Risk:** 🟢 Low.
> - **Benefit:** ⚪ Low — debug-only; worth doing so the debug overlay doesn't distort GC profiling.
> - **Seed/Save:** ✅ / ✅.

---

---

### MT-4. Startup `List.Contains` / `.IndexOf` — O (n) custom-mesh lookup ✅ DONE

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §3.2.)*

**Resolution (2026-07-01):** The flatten logic had since moved out of `World.PrepareGlobalJobData`
into `JobDataManagerFactory.Create` (`JobDataManagerFactory.cs`) — the shared SoT for runtime, editor tools, and the OM-1 calibrator. Added a `Dictionary<VoxelMeshData, int>` (`meshToIndex`) built in Step 1 alongside `uniqueCustomMeshes`, with value == list index. The dedupe check (Step 1) and the mesh→index resolve (Step 4) are now O (1) `ContainsKey`/indexer lookups instead of O (n)
`List.Contains`/`IndexOf`. The list is retained for ordered iteration (Step 2's offset accumulation). Output is byte-identical: same insertion order, and `Dictionary` uses the same
`EqualityComparer<VoxelMeshData>.Default` as the old `List` scans, so dedupe semantics are unchanged.

**Observed:** `World.PrepareGlobalJobData` collects unique custom meshes into a `List` and searches with `.Contains()` / `.IndexOf()` — O (n) each (`World.cs` ~lines 1338–1346). Startup-only.

**Recommendation:** `Dictionary<VoxelMeshData, int>` mapping mesh → index; O (1) both ways.

> **Impact Analysis:** Effort 🟢 / Risk 🟢 / Benefit ⚪ (startup-only, scales with block DB growth).
> **Seed/Save:** ✅ / ✅.

---

---

### MT-5. Startup `.ToArray()` intermediates feeding `NativeArray` ✅ DONE

*(Absorbed from `CODEBASE_IMPROVEMENTS.md` §4.2.)*

**Resolution (2026-07-01):** The flatten logic had since moved out of `World.PrepareGlobalJobData`
into `JobDataManagerFactory.Create` (`JobDataManagerFactory.cs`, Step 3). The four
`new NativeArray<T>(list.ToArray(), Allocator.Persistent)` calls now route through a private
`ToPersistentArray<T>(List<T>)` helper that allocates at `list.Count` and fills via a loop (mirroring the existing `blockTypesJobData` pattern in Step 4) — no throwaway managed array. Copy is element-order- and allocator-identical; startup-only, so no runtime path changed.

**Observed:** `new NativeArray<T>(list.ToArray(), Allocator.Persistent)` ×4 in
`JobDataManagerFactory.Create` (`JobDataManagerFactory.cs` ~lines 75–82) — temporary managed arrays immediately discarded.

**Recommendation:** Allocate the `NativeArray` at `list.Count` and fill via `CopyFrom`/loop, or build in a `NativeList<T>` from the start.

> **Impact Analysis:** Effort 🟢 / Risk 🟢 / Benefit ⚪ (startup-only).
> **Seed/Save:** ✅ / ✅.

---

---

### MT-6. `CompressionFactory` "GZip" actually writes raw Deflate ✅ DONE

**Resolution (2026-07-01):** Renamed enum member `CompressionAlgorithm.GZip` → `Deflate`, keeping the on-disk value `= 1`. Since the region format stores the numeric byte (not the name) and settings persist the enum as an integer via `JsonUtility`, this is a source-only rename with **zero save breakage** — no format-version bump or migration step. All call sites, the settings tooltip, and
`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` (§3.2/§3.3, v1.8) updated. Value `3` is reserved for a *true* GZip codec (header/CRC) should it ever be wanted, added via AOT migration.

**Observed:** `CompressionFactory.CreateOutputStream`/`CreateInputStream`
(`CompressionFactory.cs` ~lines 65–66, 93–94) construct `DeflateStream` for
`CompressionAlgorithm.GZip`. Not a performance bug (Deflate is the same codec minus the GZip header/CRC), but the label is wrong: payloads tagged "GZip" on disk are **raw Deflate**, which will bite any future external tool, migration, or interop that trusts the name.

**Recommendation:** Do **not** "fix" this by swapping to `GZipStream` — that silently breaks every existing save written with the current code (the fallback path when LZ4 is unavailable). Instead:
rename the enum member to `Deflate` (save formats store the enum value, not the name — verify before renaming) or document the discrepancy at the enum and in
`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`. If true GZip is ever wanted, add it as a **new** enum value via the AOT migration protocol.

> **Impact Analysis:**
> - **Effort:** 🟢 Low (documentation/rename).
> - **Risk:** 🟢 Low if rename-only; 🔴 High if anyone changes the stream class — hence this entry.
> - **Benefit:** ⚪ Low — correctness/clarity insurance, no runtime change.
> - **Seed/Save:** ✅ / ⚠️ **Save-format sensitive** — the bytes must not change without a format
>   version bump + migration step (`serialization-migration` skill).

---

---

## Voxel Queries, Interaction & Physics

### VQ-1. `GetVoxelState` float path — duplicated chunk math, nullable + managed deref per query

✅ **SHIPPED 2026-07-12** (`feat/world-scaling`). The runtime-API half of WS-1: shipped once the shift/mask helpers it builds on landed.

**Observed:** `WorldData.GetVoxelState(Vector3)` (`WorldData.cs` ~line 189) costs, per query:
float world-bounds compares (`IsVoxelInWorld`), `GetChunkCoordFor`, a dictionary `TryGetValue`, then
`GetLocalVoxelPositionInChunk` — which **calls `GetChunkCoordFor` again** (the chunk coord is computed twice per query) — plus a `VoxelState?` nullable wrap and (at most callers) a managed
`BlockType` array deref. *(WS-1 had already replaced `GetChunkCoordFor`'s float divides with the
`ChunkMath.WorldToChunk` shift; the live targets at the time of VQ-1 were the **double** computation, the float floor, and the nullable.)* Integer-coordinate callers (`CheckPhysicsCollision` passes
`Vector3Int` voxel positions) round-trip int → float → floored int. Per-frame call volume: the physics solver (12–18 cells × up to 7 sweeps × substeps per FixedUpdate — see PH-1), the placement march (~reach/checkIncrement calls per frame — see VQ-2), pending-mod apply, and the grass tick.

**Shipped:** integer fast path `bool TryGetVoxel(int x, int y, int z, out VoxelState state)` on
`WorldData` (forwarded from `World`), built on the WS-1 `ChunkMath.VoxelToChunk`/`VoxelToLocal`
helpers: one chunk-coord computation, an unsigned-fold integer bounds check, no floats, no nullable, a value-struct `out`. A one-entry last-chunk cache (key + `ChunkData`, main-thread only) turns a same-chunk query burst's dictionary lookup into a `Vector2Int` compare; it is stamped with a **topology version** bumped on every `Chunks` add/remove/clear (`WorldData.InvalidateVoxelQueryCache`), so a pool-recycled chunk can never be served from a stale cached reference. `GetVoxelState(Vector3)`
remains a floor-then-delegate wrapper (float `IsVoxelInWorld` preserved for exact bounds parity). The hot consumers were migrated: `CheckPhysicsCollision`, `PlacementController.Probe`/`CanPlaceAt`, and the pending-mod apply block (break/place-rule/support/neighbor-activation queries). *Out of scope (build on VQ-1): PH-1 gather-once sweeps, VQ-2 DDA march, and the mod-apply block's residual
`GetLocalVoxelPositionInChunk` double-math.*

**Verification:** a float↔int decomposition-parity sweep added to the "Chunk Math" suite proves the new integer path yields the same in-world verdict, chunk origin, and local voxel as the old float path across a fractional sweep straddling the origin and world bounds (teeth confirmed on the negative-fraction in-world verdict); the Placement suite (13 baselines) and Validate All stay green.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — new overload + WS-1 helpers + consumer migration.
> - **Risk:** 🟡 Medium — the float→int floor semantics at negative-fraction boundaries must be
>   preserved exactly (guarded with the parity sweep, same harness as WS-1); the placement
>   suite (13 baselines) covers the interaction consumers.
> - **Benefit:** 🟡 Medium — cuts the constant per-frame query tax for every consumer at once, and
>   removes the last float coordinate path standing in Tier B's way.
> - **Seed/Save:** ✅ / ✅.

---

### VQ-2. Placement ray marches by fixed increment instead of DDA

✅ **SHIPPED 2026-08-03** (`feat/world-scaling`), in-game confirmed (targeting feel unchanged for ordinary aim).

**Observed:** `PlacementController.MarchRay` sampled the ray at fixed `checkIncrement` (0.05) steps, calling
`World.CheckForVoxel` per step — ~159 queries per call at the shipping `reach = 8`, and
`PlayerInteraction.PlaceCursorBlocks` probes **every frame**. Fixed-step sampling also had two correctness
edges: a step could skip a cell clipped diagonally (block-corner misses at any increment), and the entered-face
normal was *derived after the fact* from the hit point's fractional offsets (`FaceNormal`), which could name the
wrong face on near-corner hits — not cosmetic, since `PlayerInteraction.ComputePlacementMeta` feeds that normal
to `Facing6FromHitNormal`, so a wrong face wrote wrong orientation metadata into a **persisted** `VoxelMod`.

**Shipped:** a reusable Amanatides–Woo traversal, `Helpers/VoxelRayDDA` — an allocation-free `struct` stepper
(`Create` + `MoveNext`) that visits exactly the cells the ray crosses, in order, skipping none, and yields the
entered face as the stepped axis negated. `MarchRay` drives it; `FaceNormal`/`CoordinateOffset` are deleted.
Query count at `reach = 8`: **~159 → ≤15** (bound is `reach × (|dx|+|dy|+|dz|)/|d| + 1`). `checkIncrement` is
retired from `PlayerInteraction`, from the `MarchRay`/`Probe` signatures, and from the `World.unity` scene
entry. The traversal is space-agnostic, so the probe still marches in Unity space and converts only the
resulting cell (WS-4). A ray starting **inside** a hittable block crosses no face, so the first cell reports the
face it would have entered through coming from outside — its dominant travel axis negated — chosen because a
zero normal is silently folded to North by `Facing6FromHitNormal`.

**Verification:** the pre-existing placement scenarios **could not gate this change** — all of them probe
straight down through cell centres via `ResolveTopDownPlacement`, and any traversal advancing one cell per axis
step is correct by construction there; `PlacementOutcome` did not even carry the normal. (The original entry
named "the placement validation suite's 13 baselines" as the gate — an accurate count when it was written in the
2026-07-02 audit, grown to 17 by implementation time via the WS-4a and TF-14 additions, but the count was never
the point: none of those scenarios, at either size, could distinguish the two implementations.) Four oblique-ray scenarios were therefore
authored first and **proven red** against the fixed-increment march — corner-graze skip, a seeded 500-ray fuzz
checking no earlier cell on the ray is hittable, per-face entered-normal, and the ray-starts-inside case — plus
a normal/adjacency invariant that held before and after. All four flipped green with the DDA; the original 17
showed **zero diffs** (so no baseline had encoded a sampling artifact, contrary to the entry's warning).
`Validate All`: 375/375 baselines across 16 suites.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — a contained, well-known algorithm; the decision layer above is untouched.
>   Roughly ⅔ of the work was the new oblique-ray coverage, not the traversal.
> - **Risk:** 🟡 Medium — player-facing targeting feel; corner-case behavior changes by design.
> - **Benefit:** ⚪ Low as pure perf (one ray/frame — the query reduction is not measurable in frame
>   time, and no benchmark was captured). The delivered win is correctness + removing a tuning knob;
>   perf becomes real if rays multiply (mobs, projectiles), which is why the traversal is a reusable
>   helper rather than private to the controller.
> - **Seed/Save:** ✅ / ✅.

---

### VQ-3. Interaction raycast ignores sub-voxel `collisionBounds`

✅ **SHIPPED 2026-08-03** (`feat/world-scaling`), in-game confirmed across break highlight, place highlight,
full blocks, half slabs, and slab rotation.

**Observed:** the engine already models sub-voxel geometry — `BlockType.collisionBounds`
(`BlockCollisionBounds`: a per-block AABB in block-local `[0,1]³`, rotated at query time through
`BurstCustomMeshRotationUtility.GetRotationMatrix`) — and both existing consumers honour it: the physics
solver (`World.CheckPhysicsCollision`) and the collision-bounds debug visualization
(`World.GetCollisionBoundsDataForVisualization`), which since 2026-08-03 share one resolver,
`Helpers/BlockCollisionBoundsUtility`. The **interaction ray** does not: `World.IsRayHit`
(`World.cs:4321`) decides purely on block id, tags, `fluidType` and `isSolid`, so
`PlacementController.MarchRay` stops at the first *occupied cell* regardless of whether the ray actually
crossed the block's volume. `Stone Half Slab` (id 17, `MatchVisualMesh`, `max.y = 0.5`, schema
`Facing6Roll2`) therefore collides at half height but targets, highlights, and breaks as a full cube — you
can mine it by aiming at empty air above it. This was a deliberate Phase-6 scope boundary, not an
oversight: `SUB_VOXEL_COLLISION_SYSTEM.md` §3.3 lists "API 2: Raycast Hit Detection (unchanged)" and its
caller-migration table marks `World.CheckForVoxel` untouched — but §7 never recorded the resulting gap.

**Shipped in three steps.**

*1 — a shared bounds resolver.* `Helpers/BlockCollisionBoundsUtility` (space-agnostic: the returned bounds
sit in whatever space the caller's `blockOrigin` is in), with the physics solver and the debug
visualization migrated onto it and `World.GetRotatedLocalBounds` / `GetRotatedWorldBounds` deleted. Those
two had *different* formulas — abs-matrix extent projection vs. 8 rotated corners — proven equivalent over
12,288 combinations (4 schemas × 4 bound shapes × 3 origins × 256 metadata values, worst delta
`0.000E+000`) and unified on the 8-corner form.

*2 — the narrow phase.* `Helpers/RayBoundsIntersection` (closed-form slab test) behind `VoxelRayDDA`'s
broad phase, `World.TryGetRayHit` returning the resolved `VoxelState` for its `Meta`, and `MarchRay`
continuing the traversal past a cell whose block the ray merely passed by. The entered face comes from the
slab entry plane, so it is exact for interior faces such as a bottom slab's top at `y = 0.5`. Placement
arithmetic needed **no change**: the normal is still an outward unit face, so `adjacentCell = hitCell +
normal` still names the cell above for a slab-top hit.

*3 — the highlight boxes.* Both the break highlight and the place preview shape their mesh child to the
block's volume; the preview resolves the held block's orientation through the same private
`ComputePlacementMeta` the real placement uses, so a slab previews as a slab. Each box's authored child
scale is kept as a per-box multiplier rather than assumed — they ship with *different* values (the break
outline is inflated 1.01 to beat z-fighting against the surface it hugs; the place preview is 1.0, drawn in
open air).

**Verification:** 5 scenarios in `PlacementValidationSuite.SubVoxel.cs` on a synthetic half-slab, **3 of
them proven red** against the disabled narrow phase — including one where a block placed against a slab's
top landed *beside* it rather than on it. A straight-down ray onto a bottom slab turned out **not** to
discriminate (cell face and block face are both `+Y`); the discriminating case is an oblique ray entering
the cell through `-X` above the slab and descending onto its top. Highlight parity was checked over 9,472
full-block cases (every shipping block × all 256 metadata values) at **zero drift** — every full-cube block
reproduces its authored transform exactly. Placement 28/28, Validate All 385/385, and the physics golden
master re-checked unchanged once the ray began sharing the resolver.

**Deliberately excluded:** *slab merging* (placing a slab into a slab's free half) — that is placement
policy, not ray geometry, and nobody asked for it. Compound shapes remain `VQ-4`.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — three contained steps; roughly half the work was the resolver extraction and
>   its equivalence proof, not the ray itself.
> - **Risk:** 🟢 Low as shipped — one block has custom bounds, `FullBlock` stayed on a bit-identical fast
>   path, and the placement baselines guarded the unchanged behaviour. ⚠️ For whoever touches this next:
>   physics still has **no standing validation suite** (`VALIDATION_SUITE_COVERAGE_ROADMAP.md` `NS-*`) —
>   the golden master above was a one-off in-session check and is **not committed**. Re-derive it before
>   changing `CheckPhysicsCollision` or the shared resolver.
> - **Benefit:** ⚪ No frame-time change — the win is that sub-voxel blocks became a usable block *category*
>   (slabs, panes, pillars) instead of colliding correctly but targeting wrongly.
> - **Seed/Save:** ✅ / ✅ — `collisionBounds` lives on `BlockDatabase.asset`, not in world saves.

---

---

---

## Validation Suites

### VS-1. Suite-runner scaffolding copy-pasted across all six suites

> **✅ Implemented 2026-07-08 (branch `feat/async-lighting-validation-suite`).** Extracted
> `Assets/Editor/Validation/Framework/`: `ValidationSuiteRunner.Execute(...)` (categorized loop +
> per-scenario/total wall-clock timing), the `ValidationRunResult`/`ScenarioResult` result object,
> the shared `Scenario` struct, and a `KnownBugChannel` enum (`Bug`/`Unimplemented`) that replaces the
> drifting per-suite "archive vs promote" message strings. Each suite now exposes a headless
> `Execute()` returning the result; `[MenuItem] RunAll()` is a thin `void` wrapper. The six suites and
> `ChunkRelativePositionTests` were migrated (shared `Scenario` pulled in per-file via
> `using Scenario = …Framework.Scenario;`) and re-verified to report identical baseline/known-bug
> counts before/after (62/21/15/13/9/9 baselines; ChunkMath now 14, previously a bare pass/fail bool).
> **Remaining (tracked follow-up):** `VoxelMetadataUtilityTests` and `FastNoiseLiteTests` — their
> granular `AssertEqual`/golden-value harnesses don't map cleanly to one-bool-per-scenario. The result
> object was designed to also feed VS-2 (CI exit code + NUnit-XML) and VS-3 (stale-assembly preamble).
>
> **Possible future refinements (tracked, not blocking):**
> - Re-add per-suite header annotations (`(MT-1)`/`(MT-2)`, dropped in the migration) as a structured
>   `Scenario`/suite tag rather than baking them into the display name (noted in `ValidationSuiteRunner.cs`).
> - Optionally hoist the still-duplicated `Check(label, condition)` / `Expect(condition, message)` logging
>   primitives (MeshQueue + LightScheduler + Placement, ~76 call sites) into a shared `ValidationLog` — a
>   separate, bisectable commit, not required for VS-1.
> - Add a per-scenario category tag to `ScenarioResult` so VS-2 can preserve distinctions the current binary
>   baseline/known-bug split flattens (e.g. Placement's data-audit scenarios).
> - Zero-alloc timing: swap the per-scenario `Stopwatch` for `Stopwatch.GetTimestamp()` deltas (noted in
>   `ValidationSuiteRunner.cs`).

**Observed:** Every suite entry file re-declares the same private `Scenario` struct and the same
`RunAll` body — scenario loop, try/catch, baseline vs known-bug counting, colorized summary — as near-byte-identical copies (~90 lines × 6: `LightingValidationSuite.cs`,
`MeshingValidationSuite.cs`, `BehaviorValidationSuite.cs`, `PlacementValidationSuite.cs`,
`MeshBuildQueueValidationSuite.cs`, `LightWorkSchedulerValidationSuite.cs` — diff the first two to see the drift already starting: "may be fixed → archive" vs "may be implemented → promote"). Per-suite `Check(label, condition)` PASS/FAIL logging primitives repeat the same way, and the three standalone test files use a third ad-hoc pattern each. The shared `Framework/` folder already proves the extraction works (`ValidationReflection` was created precisely because two harness copies were drifting; `GoldenMaster` likewise).

**Recommendation:** Extract a `Framework/ValidationSuiteRunner`: public `Scenario` type (name, body, known-bug id), the categorized run loop, the summary formatting, and — while there — **per-scenario and total wall-clock timing** in the summary (today a scenario that becomes pathologically slow gives no signal; the lighting suite's 55 baselines including 50-seed fuzzes would get a per-line ms column for free). Each suite's entry file shrinks to its menu item + suite name + scenario registration. VS-2 and VS-3 then land in one place instead of six.

**Design constraint:** the runner's headless entry must return a **result object**
(baseline pass/fail counts, known-bug repro counts, per-scenario timings) rather than `void`. That one signature is simultaneously VS-2's CI exit-code source, the input for VS-2's NUnit-XML emission, and the future UTF bridge (a thin `[Test]` wrapper per suite — see the framework decision note above), so it must be designed in here rather than retrofitted.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — mechanical but touches all six entry files + three standalone tests.
> - **Risk:** 🟢 Low — behavior-preserving; gate = every suite reports identical verdicts
>   before/after.
> - **Benefit:** ⚪ (dev-time) — ~500 duplicated lines gone, message drift ended, timing signal
>   gained, and the next suite (there will be one — six exist) starts from a real framework.
> - **Seed/Save:** ✅ / ✅.

---

---

### VS-2. Suites are human-in-the-loop only — no aggregate run, no CI entry point

> **✅ Implemented 2026-07-09 (branch `feat/async-lighting-validation-suite`, 5 commits).** On top of VS-1's
> shared runner:
> - **`Framework/ValidationSuiteRegistry`** — an *explicit* hand-maintained list of the standard suites (not
>   attribute/reflection discovery: the failure mode is a compile error, list order is run order, and
>   `ExpectedSuiteCount` is a floor the runner warns against). Adding a suite is one line.
> - **`Framework/ValidationSuiteAggregateRunner`** — the `Minecraft Clone/Dev/Validate All` menu item + a
>   `Run(logToConsole, suites)` core returning an **`AggregateRunResult`** (roll-ups computed from the per-suite
>   results; `Success`, `AnySuiteRanNothing`, `RanNothing`). Each suite's `Execute` was threaded with
>   `logToConsole`/`showProgress` so the aggregate drives one progress bar instead of each inner bar clobbering it.
> - **Isolation guard (the load-bearing part).** The suites share the process-global `World.Instance` singleton
>   (stubbed via reflection by `BehaviorTestWorld`). Sequential aggregation would make a suite order-dependent if
>   one failed to restore it. So the runner snapshots `World.Instance` around every suite and, on a mismatch,
>   **force-restores it (protecting the next suite) and marks the offender failed+untrusted** — a leak becomes a
>   loud, attributed error, never a silent heisenbug. Acceptance gate: `individual == forward == reversed`
>   per-scenario over all suites (**151 baselines** across **8 suites**, byte-identical in every ordering).
> - **`Framework/NUnitXmlWriter`** (behind `IValidationResultWriter`, so JUnit can drop in later) — NUnit3
>   `test-run` XML: baseline pass / known-bug now-passing → `Passed`; baseline fail / thrown / isolation-failed →
>   `Failed` + `<failure>`; known-bug still reproducing → `Inconclusive` + `<reason>`.
> - **`Framework/ValidationFrameworkSelfTest`** — registered as the 8th suite ("Validation Framework"), so
>   `Validate All` re-checks the reporting/guard layer every run. It round-trips the XML writer in-memory and
>   **hard-proves the isolation guard trips on a leak** via a mock guard (no real `World` fabricated).
> - **`Framework/ValidationSuiteCI`** — `RunHeadless()` is the `-executeMethod` batch target (runs the selected
>   suites, writes the XML, `EditorApplication.Exit(0)` only when every baseline passed and no suite ran nothing,
>   else `Exit(1)`; any crash logs and exits 1). `RunSelected(csv)` is the no-exit in-editor path. `-validationSuites
>   "Lighting Engine,Meshing"` selects a subset (case-insensitive, registry-ordered; a single unknown name rejects
>   the whole request so a typo can't launder a partial run).
>
> **Scope / limitations (by design):**
> - **Entry point ≠ live CI.** No CI pipeline or batch scheduler exists yet (none is planned near-term); the
>   immediate consumer is an AI agent calling `RunSelected` via `Unity_RunCommand`. The batch `Exit`/XML path is
>   built for whenever CI lands. Batchmode also needs Unity license activation on any runner.
> - **Aggregate covers the 8 runner-based suites, not all ~15 menu items.** The deep-run/nightly variants
>   (lighting fuzz sweeps, fluid parallel-determinism) and the not-yet-migrated standalone tests
>   (`VoxelMetadataUtility`, `FastNoiseLite`) stay separate — they auto-join the aggregate the moment they return a
>   `ValidationRunResult` and get a registry line (the VS-1 follow-up).
> - **NUnit3 XML is round-trip-checked in-memory, not yet against a live CI parser** (deferred with CI itself).
> - The default results path is `TestResults/validation-results.xml` (a build artifact — add to `.gitignore` when a
>   CI job starts writing it).
>
> **Coverage recording (report item (e)):** left as the documented batchmode CLI recipe
> (`-enableCodeCoverage -coverageOptions "…"`) rather than in-code, since the Code Coverage editor assembly is not
> auto-referenced into `Assembly-CSharp-Editor`; the Burst caveat (coverage instruments IL; Burst jobs only register
> with Burst disabled; numbers reflect editor-Mono) stands.

**Observed:** Running the full regression surface means manually clicking **14 menu items** (six suites, three standalone test files, two nightly fuzz deep-runs, three fluid-determinism variants), reading colorized console output per run. There is no "run everything" aggregate, and no headless mode: `RunAll` returns `void` with console-only results, so
`-batchmode -executeMethod` has nothing to exit non-zero on. Consequences: a cross-cutting change (`ChunkData`, pooling, a `Helpers/` refactor) relies on the developer remembering which suites apply; the 2000-seed nightly fuzzes only run when someone thinks of them.

**Recommendation:** On top of VS-1's shared runner: (a) a `Validate All` menu item running every registered suite with one combined summary (suites self-register with the runner so new ones are included automatically); (b) a CI/headless entry point that runs the same set and calls
`EditorApplication.Exit(1)` on any baseline failure — making scheduled runs (including the nightly fuzz tier) possible without a human; (c) keep the individual menu items for focused iteration; (d) emit an **NUnit-format XML results file** from the same result object (~50 lines: scenario → test-case, known-bug repro → inconclusive) so CI and external tooling consume the verdicts the same way they would UTF output; (e) wrap the headless run in **coverage recording** via the already-installed Code Coverage package (`CodeCoverage.StartRecording()`/
`StopRecording()` in
`UnityEditor.TestTools.CodeCoverage` works outside the Test Runner, or `-enableCodeCoverage
-coverageOptions` on the batchmode invocation). Coverage caveat: coverage instruments IL, so Burst-compiled job code only registers when Burst compilation is disabled for the coverage run — and the numbers reflect editor-Mono execution either way.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — registration list + two entry points over the shared runner.
> - **Risk:** 🟢 Low — additive; individual workflows unchanged.
> - **Benefit:** 🟡 Medium — the regression gate becomes one click for cross-cutting changes and
>   automatable for nightly fuzz depth; "which suites did you run?" stops being a review question.
> - **Seed/Save:** ✅ / ✅.

---

---

### VS-3. No stale-assembly guard — a suite can silently validate stale code

> **✅ Implemented 2026-07-10 (branch `feat/async-lighting-validation-suite`).** Added
> **`Framework/StaleAssemblyGuard`** — a diagnostic preamble wired as the first line of
> `ValidationSuiteRunner.Execute` (the one shared funnel, so every entry point — individual menu items,
> headless single-suite `Execute`, `Validate All`, CI — is covered). The aggregate runner checks once and
> opens a ref-counted `SuppressScope` around its suite loop, so an 8-suite run warns **at most once**, not
> eight times. It never throws and never fails a baseline (verified in-editor: a stale run still returned
> `Success=True` with the warning attached); an IO/resolution failure degrades to an *inconclusive* warning
> rather than a silent false all-clear. Three signals against the two project assemblies
> (`Assembly-CSharp` = production under validation, `Assembly-CSharp-Editor` = the suite code):
> `isCompiling`/`isUpdating`; **source-vs-DLL** (newest `.cs` in an assembly's `CompilationPipeline`
> `sourceFiles` newer than its compiled DLL — the load-bearing signal, since even `isCompiling == false` has
> produced stale runs); and **domain-vs-disk** (on-disk DLL newer than what this domain loaded, captured at
> `[InitializeOnLoadMethod]` — catches recompile-without-reload). A 2 s tolerance absorbs save→compile jitter.
> The pure `Decide(...)` is guarded by **6 self-test scenarios** in the Validation Framework suite
> (fresh / compiling / source-newer / within-tolerance / disk-newer-than-loaded / unresolved-inconclusive),
> bringing that suite to 16 and the aggregate to **159 baselines**. Live-proven: touching a source file's
> mtime into the future (no recompile) fired exactly one stale warning through a real aggregate run.
> **Scope:** warn-only everywhere (the report's diagnostic intent) — the headless/CI exit code stays driven by
> baseline results, not by the staleness heuristic. Two-assembly scope: a future `.asmdef` split would need
> its new assembly added to the guard's list.

**Observed:** A documented operational foot-gun (workflow memory + the `dotnet build` notes in CLAUDE.md): after editing code, the menu-item suites can execute against the *previous* compiled assembly if Unity's script compilation didn't actually run (`dotnet build` alone never recompiles the editor domain; even `IsCompiling == false` has produced stale runs). A green suite on stale code is worse than no run — it launders a regression. Today the only defense is tribal knowledge ("confirm with a fresh `Unity_RunCommand` wave").

**Recommendation:** Make the runner self-checking (one place, via VS-1): at `RunAll` start, warn loudly if `EditorApplication.isCompiling` or if pending script updates exist (`EditorApplication.isUpdating` / `CompilationPipeline` state), and print the validation assembly's load timestamp vs its on-disk `Library/ScriptAssemblies` write time — a mismatch means the loaded code is not the code on disk. Cheap, and it converts the documented gotcha into an automatic, visible warning on every run.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — a preamble in the shared runner.
> - **Risk:** 🟢 Low — diagnostic only; false-positive warnings are acceptable (they prompt a
>   recompile, which is the safe action anyway).
> - **Benefit:** ⚪ (dev-time) — eliminates the "suite passed on stale code" failure mode that has
>   already cost debugging sessions.
> - **Seed/Save:** ✅ / ✅.

---

---

## World Scaling Enablers

### WS-1. Truncating / float-roundtrip chunk coordinate math → `ChunkMath` shift/mask helpers

✅ **SHIPPED 2026-07-12** (`feat/world-scaling`, 4 commits). *(Promoted from `WORLD_SCALING_ANALYSIS.md`
§3.2/§6, which analyzed it but never tracked it in this backlog. It was the only part of the world-scaling work with zero save/seed risk that could ship early and independently.)*

**Observed:** Chunk/region coordinate math mixed three idioms: float-roundtrip floors (`Mathf.FloorToInt((float)x / 16)` — correct today but silently wrong beyond ±2²⁴), truncating integer division (wrong for negative *mid-chunk* coordinates), and ad-hoc correct forms. ~11 of the repo's `FloorToInt` sites are actual chunk/region-coordinate math (the rest are legitimate world→voxel floors — player HUD, cloud tiling, texture-atlas, entity AABB — and were left alone). All-positive coordinates hide the differences today; Tier B (negative quadrants) would
turn every wrong *reachable* site into a silent world-corruption bug.

**Delivered:** Centralized into `ChunkMath` shift/mask helpers (`voxel >> 4`, `voxel & 15`,
`chunk >> 5`, `chunk & 31` — simultaneously the fastest and the only always-correct option, all Burst-safe), migrated every chunk-math call site (`ChunkCoord`, `WorldData`, `World`,
`ChunkRelativePosition`, `RegionAddressCodec.V2`, and the `StandardWormCarverJob` Burst site — a *second* truncation site the original audit didn't list), and guarded them with the "Chunk Math"
validation suite (21 scenarios at ship: floor-div/local/region sweeps over ±2048 incl. boundaries, a power-of-two coupling guard, legacy-parity for positives, and a negative-coordinate teeth case; 45 by 2026-07-22 — the suite has since absorbed the VQ-1 parity sweep, the WS-2/WS-3 bounds + codec round-trip pins, the WS-4a WorldOrigin baselines, and the CP-2/NS-5 region-codec pins).
"Forbid inline chunk math" remains a convention (no analyzer); alignment tests route through
`ChunkMath.IsChunkAligned` (CP-2 close-out).

> **Audit correction (supersedes the original premise):** the `RegionAddressCodec.V2Codec` step-1
> truncation was described as an *"already live"* bug. It is **latent but unreachable**: every encoder
> caller (`ChunkStorageManager` ×3, the v1→v2 migration) passes an exact chunk origin (a multiple of
> 16), and truncating division equals floor division for exact multiples *regardless of sign*.
> Combined with the old step-2 float-floor and step-3 manual `if (lx<0) lx+=32` correction, the V2
> encoder was already correct for every reachable input, including negative origins (verified by an
> old-vs-new sweep: 0 mismatches). So the codec change is a **consistency / future-proofing refactor**
> (removes the truncation that *would* be wrong if a raw mid-chunk voxel were ever passed), **not** a
> live-bug fix. No save-format V3 bump was made — output is byte-identical for all reachable inputs;
> the defensive V3 bump is deferred to Tier B (when negative coords become reachable and it rides the
> border-removal change). *(Superseded in practice: WS-3 made negative coords reachable with **no**
> bump — V2 was already negative-correct, so there was never a buggy on-disk build to detect. The
> NS-5 V1/V2 codec pins — expected values on both signs, ±2³¹-adjacent domain, V1 legacy contract —
> landed 2026-07-22 with the CP-2 close-out as `ChunkMathValidationSuite.RegionCodec.cs`.)*

> **Impact Analysis:**
> - **Effort:** 🟡 Medium — the audit was the work; each individual fix was mechanical.
> - **Risk:** 🟡 Medium — a single wrong mask silently corrupts chunk/region addressing; guarded with
>   an old-vs-new equivalence sweep (positive range byte-identical) before swapping call sites.
> - **Benefit:** ⚪ Low today (removes float conversions from every chunk lookup) — but it is the
>   first Tier B prerequisite and the cheapest insurance against the negative-coordinate bug class.
> - **Seed/Save:** ✅ / ✅ — outputs identical for all-positive coordinates; no version bump shipped.

---

---

