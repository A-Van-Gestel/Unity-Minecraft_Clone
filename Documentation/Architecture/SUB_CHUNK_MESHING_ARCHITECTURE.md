# Sub-Chunk (Section) Meshing Architecture

**Status:** Implemented (Active)  
**Target Engine:** Unity 6.6 (Mono for dev; IL2CPP for production)  
**Context:** The engine renders the world using 16x16x16 `ChunkSection` GameObjects instead of monolithic columns.  
**Audited:** 2026-08-17, at commit `aad0527c` (branch `feat/world-scaling`). Verified in code, not assumed:
`SectionRenderer.cs`, `Chunk.cs`, `WorldJobManager.cs` (`ScheduleMeshing` / `ProcessMeshJobs`),
`Jobs/MeshGenerationJob.cs`, `Jobs/MeshPostProcessJob.cs`, `Helpers/MeshCompletionDriver.cs`,
`Helpers/IMeshCompletionHost.cs`, `Data/JobData.cs` (`MeshDataJobOutput`, `MeshSectionStats`),
`Helpers/MeshingScheduleDecision.cs`.

## 1. Executive Summary

To support increased world height (256+ blocks), eliminate main-thread lag during voxel modifications, and implement robust visibility culling, the rendering architecture uses **Sub-Chunk (Section) Meshes** instead of **Monolithic Column Meshes**.

Instead of generating one massive mesh for a 16x16x128 column, the engine produces independent meshes for each 16x16x16 `ChunkSection`. This aligns the rendering strategy with the underlying data structure and leverages Unity's native culling systems.

Note the split between *work granularity* and *render granularity*: one mesh **job** still processes a whole
chunk column, but it emits per-section offsets so each section's geometry uploads to its own `Mesh`. §4.2
covers why.

## 2. Why This Shape (Problem Analysis)

The earlier "Monolithic" approach and the attempted "Vertical Passability" optimization faced insurmountable architectural flaws:

1. **Scaling Cost (O (N)):** Modifying a single block at Y=5 required regenerating the mesh for the entire column (Y=0 to Y=127). As height increases to 256 or 512, this cost becomes prohibitive, causing frame spikes.
2. **Ineffective Culling:** Unity's Frustum Culling operates on `Renderers`. A tall chunk column is almost always "in view" (e.g., player looking at the bottom, top is off-screen). Unity is forced to submit geometry for the entire column, including parts behind the player or deep underground.
3. **Complex Visibility Logic:** The "Vertical Passability" algorithm attempted to manually calculate occlusion. This was CPU-intensive, complex to maintain, and failed to account for 3D visibility (e.g., viewing a cave from the side).

## 3. Architecture

### 3.1. The "Sub-Chunk" Concept

* **Logical Unit:** `ChunkSection` (16x16x16 voxels) — the storage unit, see [DATA_STRUCTURES.md](DATA_STRUCTURES.md) §2.1.
* **Visual Unit:** one pooled `GameObject` per `ChunkSection`, managed by a `SectionRenderer`.

The `Chunk` class is a **manager**, not a mesh provider: it owns a `SectionRenderer[]` and routes each
section's slice of the job output to the matching renderer.

### 3.2. Rendering Strategy

Each section is an individual `GameObject` carrying a `MeshFilter` + `MeshRenderer`, parented to the chunk
object and offset by `sectionIndex * SECTION_SIZE` in local space.

**Why GameObjects rather than `Graphics.DrawMesh`?**

* **Ease of Use:** Unity's built-in systems (Frustum Culling, Sorting, LODs) work best with standard GameObjects.
* **Performance:** In Unity 6, the overhead of GameObjects is low. With **GPU Instancing** enabled on the material, draw calls are batched efficiently.

> **No colliders are involved.** Sections carry *no* `MeshCollider` — the engine has none anywhere. Physics
> resolves analytically against voxel data instead, via the sub-voxel collision solver
> ([SUB_VOXEL_COLLISION_SYSTEM.md](SUB_VOXEL_COLLISION_SYSTEM.md)), so a remesh never triggers collider
> re-baking.

**Visibility ownership (two axes, GS-5 Phase 0.5 — shipped 2026-07-25):** a section can be hidden for two unrelated reasons, and each has exactly one mechanism and one owner:

| Axis                 | Mechanism                        | Owner                                                                          |
|----------------------|----------------------------------|--------------------------------------------------------------------------------|
| *"Has geometry"*     | `GameObject.SetActive`           | `SectionRenderer` (`UpdateMeshNative`'s vertex-count toggle, `Clear()`)        |
| *"Occlusion-culled"* | `MeshRenderer.forceRenderingOff` | the future `VisibilityManager`, via `SectionRenderer.SetOcclusionCulled(bool)` |

Neither owner writes the other's flag, so remesh and cull events compose in any order — a single shared flag is what made the previous culling attempt render stale geometry. The one exception:
`Clear()` resets **both** axes on pool recycle (reset-only — it never *sets* the occlusion flag), so a memoizing culler must re-issue after a recycle. See
[Design/VISIBILITY_CULLING_ARCHITECTURE.md](../Design/VISIBILITY_CULLING_ARCHITECTURE.md) §7.3.

The occlusion axis is **built but not yet driven**: `SetOcclusionCulled` has no production caller today (the
graph-based culler of §8 is unimplemented), so it is exercised only by the validation suite's B28–B30
baselines. That is deliberate — the contract ships before the consumer so the consumer cannot be the thing
that defines it.

### 3.3. Culling Strategy (The "Natural" Cull)

We do not need complex flood-fill visibility algorithms on the CPU. We rely on two layers of culling:

1. **Generation Culling (Zero-Vertex Check):**
    * If a section is `IsFullySolid` (completely buried) and surrounded by solid neighbors, the meshing job produces **0 vertices**.
    * If a section is `IsEmpty` (air), the meshing job produces **0 vertices**.
    * **Action:** `UpdateMeshNative` deactivates the GameObject on a zero vertex count. No render cost.
2. **Frustum Culling (Unity Native):**
    * Because every 16x16x16 section has its own bounding box, Unity automatically stops rendering sections that are behind the camera, or above/below it (e.g., surface sections are culled when the player is deep underground).

## 4. Technical Implementation

### 4.1. Ownership

**`Chunk`** holds `SectionRenderer[] _sectionRenderers` and `bool HasMeshApplied`; `ApplyMeshData` is the
main-thread entry that slices job output into per-section uploads. Chunks and their section GameObjects are
pooled together (`DynamicPool<Chunk>`).

**`SectionRenderer`** wraps the Unity objects for one section and owns three MR-era apply-path
optimizations, all documented at their declaration sites:

* **`static readonly VertexAttributeDescriptor[] Layout`** (MR-2) — the single source of truth for the
  vertex format, **32 B/vertex across 4 streams**: `Position` Float32×3 (12 B, stream 0), `TexCoord0`
  Float16×4 (8 B, stream 1), `Color` UNorm8×4 (4 B, stream 2), and `Normal` SNorm8×4 + `TexCoord1` UNorm8×4
  interleaved (8 B, stream 3). The editor chunk-preview window uploads against this same array rather than
  keeping a second copy that would drift on the next format change.
* **Cached material combinations** (MR-3) — there are only 8 submesh-presence combinations
  (bit0 = opaque, bit1 = transparent, bit2 = fluid), so the bitmask indexes a prebuilt `Material[]` and
  `sharedMaterials` is reassigned only when the combination (or the cache version) actually changed. No
  `Material[]` allocation in the hot apply path.
* **Constant section bounds** (MR-4) — section geometry stays within its own 16³ cell plus a fixed
  margin (`CrossMeshVariation.MaxCellEscape`, the furthest FL-4's per-voxel flora variation can push a
  border tuft), so a constant padded `Bounds` replaces the per-update `RecalculateBounds()` vertex scan.

### 4.2. Mesh Generation Pipeline (Jobs)

Two chained Burst jobs per chunk, scheduled from `WorldJobManager.ScheduleMeshing`:

**`MeshGenerationJob`** (`IJob`, `FloatMode.Fast`) processes **one whole chunk column** per job, not one
section. Sections are the *output* granularity, not the work granularity: the job writes a single set of
buffers plus a `MeshSectionStats[]` recording each section's vertex and per-submesh triangle ranges. One job
per section would multiply the neighbor-map gathering cost by 8 for no benefit, since a column's sections
share the same neighbor data.

*Inputs:* the center chunk's voxel `Map` and `LightMap`, `SectionJobData[]` (the per-section
`IsEmpty` / `IsFullySolid` flags that let it skip whole sections), the shared `BlockTypeJobData` and
custom-mesh arrays from `JobDataManager`, the fluid vertex templates, **eight** neighbor voxel maps and
eight neighbor light maps (4 cardinal for face culling, 4 diagonal for fluid/AO corner smoothing), the
`ChunkPosition` in **voxel** space (never the Unity transform — jobs must not see the floating origin), and
the `SmoothLighting` / `FullCubeContactShadows` quality switches. `ClipBounds` (`MeshClipBounds.Disabled` in
production) lets the editor preview mesh a partial volume.

*Shading:* beyond face culling and smooth light, the job computes per-corner ambient occlusion from the
**actual authored block volume** rather than whole-cell occupancy (VO-*), and can subdivide a face into an
N×N grid of sub-quads to resolve a partial occluder's silhouette against it (VO-9b), which is what makes
silhouette contact shadows possible (SS-2). Partial occluders always take the finer grid; the
`FullCubeContactShadows` setting extends the same treatment to faces reached only by full cubes (SS-3).
See [SMOOTH_AND_RGB_LIGHTING.md](SMOOTH_AND_RGB_LIGHTING.md) and
[Design/SILHOUETTE_CONTACT_SHADOWS.md](../Design/SILHOUETTE_CONTACT_SHADOWS.md).

**`MeshPostProcessJob`** (MR-5) is chained onto the first handle, so it runs on a worker thread rather than
inside `ApplyMeshData` on the main thread. It rewrites vertex positions from chunk space to section space,
relativizes triangle indices per section, packs the full-precision working normals to SNorm8×4, and
interleaves normal + light into the single stream-3 buffer the GPU consumes.

*Output:* one `MeshDataJobOutput` — `Vertices`, three index lists (`Triangles` / `TransparentTriangles` /
`FluidTriangles`), `Uvs`, `Colors`, `Normals`, `LightData`, `InterleavedStream3`, and `SectionStats`. It is
**rented from a pool and pre-sized** (MR-6), returned in `ProcessMeshJobs` after the upload — never
disposed while the job may still be running.

### 4.3. Scheduling and Completion

`ScheduleMeshing` routes its three gates through the pure `MeshingScheduleDecision` so the runtime and the
validation suite cannot disagree about the policy. In order:

1. **In-flight** — a mesh job already running for this coord. It deliberately does **not** block on a
   running *lighting* job: the mesh job reads an independent voxel snapshot and is re-requested when
   lighting completes, which is what avoids the cross-chunk BFS ping-pong deadlocks in the pipeline's
   history.
2. **Center light-readiness** — skipped entirely when lighting is disabled, since no lighting job would ever
   run to clear the flags.
3. **Neighbor mesh-readiness** (`World.AreNeighborsMeshReady`).

Only `Result.Schedule` dequeues the request; every other result leaves the chunk **queued for retry** on a
later frame (MP-3) rather than dropping it. Each job also records the `ChunkData.LifecycleEpoch` it was
snapshotted against (`TargetEpoch`, MP-4), so a merge can recognize a result whose chunk was recycled
underneath it.

Completion runs through the shared `JobCompletionPass` skeleton, with the mesh-specific side effects in
`MeshCompletionDriver` — a separate object because one class cannot implement
`IJobCompletionDriver<ChunkCoord>` twice, and the lighting pass already holds that slot on
`WorldJobManager`. Every collaborator it touches arrives via `IMeshCompletionHost`, which is what lets the
validation suite drive the production driver against a recording fake host (B31–B33). See
[CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md).

### 4.4. Voxel Modification Workflow

1. **Input:** Player breaks a block at `(x, y, z)`.
2. **Data Update:** the modification queue applies the edit to the section's `uint[]` and flags lighting work.
3. **Re-Mesh:** the chunk is enqueued, `ScheduleMeshing` passes its gates, and the two chained jobs run.
4. **Apply:** `Chunk.ApplyMeshData` takes `NativeArray` views of the shared output buffers once, then walks
   **every** section renderer, slicing each one's range out of them by `MeshSectionStats`. There is no
   per-section dirty check: a section whose `VertexCount` is 0 gets an explicit empty `UpdateMeshNative`
   call (which deactivates its GameObject via the has-geometry axis), and every other section re-uploads.
   The saving comes from the *job* skipping empty/buried sections so their vertex count is 0 — not from the
   apply pass detecting which sections changed. It also does not free the output buffers; `ProcessMeshJobs`
   returns them to the MR-6 pool immediately after this call.
5. **Present:** the same main-thread step then calls `Chunk.TriggerLoadAnimation()` — the one-shot
   rise-from-underground animation, played once per chunk lifecycle — and stamps the FP-1 terminal
   telemetry. Both sit *inside* the apply-succeeded branch: MP-6 pairs the animation to the apply that
   earned it, so a result discarded for a gone chunk can never animate an empty slot. Before MP-6
   (2026-07-25) this went through a `ChunksToDraw` queue drained later in the frame; nothing survives
   across frames now.

## 5. Performance Considerations & Limitations

### 5.1. Draw Calls

* **Risk:** Increasing GameObject count from ~400 (Chunks) to ~6,400 (Sections).
* **Mitigation:**
    1. **Empty Sections:** ~50% of sections (High air, Deep underground) have 0 vertices and exist only as data, not active GameObjects.
    2. **GPU Instancing:** All sections share the same Material. Unity batches them efficiently.
    3. **Static Batching:** Not applicable for dynamic chunks, but Instancing is sufficient.

### 5.2. Memory

* **Risk:** Mesh overhead.
* **Mitigation:** `Mesh` objects in Unity have a small header overhead. Dividing one large vertex buffer into 8 smaller ones does not significantly increase total VRAM usage (vertex count remains roughly the same), and MR-2 cut the per-vertex cost from 60 B to 32 B.

## 6. Path to Cubic Chunks (Future)

This architecture is the prerequisite for Infinite Height / Cubic Chunks. Once implemented, the `Chunk` (Column) class effectively becomes a legacy wrapper.

**Migration to Cubic:**

1. Remove the `[x, z]` chunk indexing.
2. Store `ChunkSection` in a spatial hash `Dictionary<Vector3Int, ChunkSection>`.
3. Load/Unload sections based on distance from a player sphere, not a cylinder.
4. The meshing job would need to become genuinely per-section (§4.2), since a column's shared neighbor
   gather no longer applies.

## 7. Implementation Status (Completed)

- [x] **Data:** `ChunkSection` tracks `IsFullySolid` / `IsEmpty`.
- [x] **Jobs:** `MeshGenerationJob` emits `MeshSectionStats` for granular uploads; `MeshPostProcessJob` chains the space rewrite + packing off the main thread (MR-5).
- [x] **Manager:** `Chunk` manages an array of `SectionRenderer` objects.
- [x] **API:** `SectionRenderer` uses `Mesh.SetVertexBufferData` and `Mesh.SetSubMeshes` for zero-allocation updates, against the packed MR-2 `Layout`.
- [x] **Orchestrator:** `WorldJobManager` gates scheduling on dependencies (`MeshingScheduleDecision`) and completes through `MeshCompletionDriver`.
- [x] **Visibility:** the two-axis ownership contract, with the occlusion axis awaiting its culler.

---

## 8. Advanced Visibility Culling (Next Steps)

The Sub-Chunk architecture lays the foundation for **Graph-Based Visibility Culling** (stopping the rendering of caves when the player is on the surface). Not implemented — the `SectionRenderer` seam of §3.2 is the only part that exists.

For the detailed design and implementation plan of this feature, please refer to:
**[Documentation/Design/VISIBILITY_CULLING_ARCHITECTURE.md](../Design/VISIBILITY_CULLING_ARCHITECTURE.md)**

---

## 9. Testing & Validation

The mesher has an editor validation suite at `Assets/Editor/Validation/Meshing/` (menu **`Minecraft Clone/Dev/Validate Meshing`**). It runs the **real** `MeshGenerationJob` over a synthetic single chunk and asserts the output against an independent standard-cube geometry oracle plus structural/determinism invariants — the regression guard that lets the `MR-*` meshing optimizations in
[PERFORMANCE_IMPROVEMENTS_REPORT.md](../Design/PERFORMANCE_IMPROVEMENTS_REPORT.md) claim "output-preserving"
(it already guards MR-1, MR-2, MR-3, MR-4, MR-5, and MR-7), and that guards the `VO-*` / `SS-*` shading work
([VOXEL_OCCLUSION_REFACTOR.md](../Design/VOXEL_OCCLUSION_REFACTOR.md),
[SILHOUETTE_CONTACT_SHADOWS.md](../Design/SILHOUETTE_CONTACT_SHADOWS.md)).

- **What it covers and its remaining blind spots**, plus the phased `MH-*` extension backlog:
  [Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md](Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md).
- **Harness file map, API cheat sheet, and the MR-* guard pattern** (for authoring scenarios):
  `.agents/skills/validation-driven-bugfix/references/meshing-suite.md`.
