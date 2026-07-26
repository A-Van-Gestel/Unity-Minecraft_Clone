# Advanced Visibility Culling (Graph Connectivity)

**Version:** 1.0
**Date:** 2026-07-26
**Status:** **Proposed design — not implemented.** Phases 0 **and 0.5** are complete — the §7.3 renderer
ownership split, the last hard prerequisite, shipped 2026-07-25 as MP-5 — but the culler itself
(Phases 1–3: connectivity masks, BFS traversal, integration) is unbuilt.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> How to stop rendering sealed-off underground geometry that frustum culling cannot reject. **The
> pivotal decision: cull by asking "can air flow from the camera to this section?" — a per-section
> connectivity mask plus a BFS traversal over the section graph — rather than any count- or
> depth-based heuristic**, because a voxel world's caves make every heuristic both over-cull (visible
> holes) and under-cull. Masks are derived data, never persisted, so no save-format change exists
> anywhere in this design.

- **Status:** Planned (Phases 0 **and 0.5** complete — the §7.3 ownership split, the last hard prerequisite, shipped 2026-07-25 as MP-5; Phases 1–3 open)
- **Current Implementation:** Standard Frustum Culling + Empty Section Skipping
- **Target:** Advanced Occlusion Culling via Graph Traversal
- **Context:** Solving the "Underground Overdraw" problem where caves render while the player is on the surface.



**Audited:** the §5 phase table and §7 corrections were last re-verified 2026-07-25 when MP-5 shipped
Phase 0.5. **Tracked as `GS-5`** in
[`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md), which carries the
effort/risk/benefit rating; this document is the design authority.

**Relationship to other documents:**

- [`../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md)
  — the section meshing this culls, and the renderer-ownership contract Phase 0.5 established.
- [`../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`](../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md)
  — precedent for the "derived data, no save bump" position taken in §8.
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — taller worlds (Tier A) multiply subsurface
  section counts, so this system's value grows with world height; store masks per-section, not
  per-chunk-column, so cubic chunks (Tier C) inherit them unchanged.

---

## 1. The Problem: Frustum vs. Occlusion

Unity's built-in **Frustum Culling** only checks if an object's bounding box is inside the camera's view cone. It does **not** check if the object is hidden behind other opaque objects.

In a voxel game, massive amounts of geometry (caves) exist directly below the surface. Since the camera often looks slightly downward, these caves are "in the frustum." Without Occlusion Culling, the engine renders the entire underground world, causing significant GPU overhead.

## 2. The Solution: Graph-Based Traversal

We cannot use Unity's baked Occlusion Culling because our world is procedural and destructible. We must implement a dynamic **Graph Traversal** algorithm.

### Core Concept

Instead of asking "Is this chunk in the camera view?", we ask: **"Can the air flow from the camera's position to this chunk?"**

If a cave system is completely sealed off by solid blocks (or just not connected to the current hollow area the player is standing in), the traversal algorithm will never reach it. If the algorithm doesn't reach it, we disable the renderer.

## 3. Data Structure: The Connectivity Mask

To make this efficient, we cannot Flood Fill the entire world every frame. Instead, we pre-calculate a **Connectivity Graph** for every `ChunkSection` during the `MeshGenerationJob`.

We reduce the complex 4096-voxel section into a simple question:
*"If I enter this section from the Top face, can I exit via the East face?"*

### 3.1. The Data Type (`ConnectivityMask`)

We track connections between all 6 faces (Up, Down, North, South, East, West). There are 6 Entry points and 6 Exit points. Data requirement: 6 bitmasks (one for each entry face), each containing 6 bits (one for each exit). Total: 36 bits. Fits in a single `ulong`.

```csharp
public struct SectionConnectivity
{
    // A bitmask representing reachability.
    // If we enter from Face A, which other Faces can we reach?
    // Usage: (VisibilityMask >> (EntryFaceIndex * 6)) & (1 << ExitFaceIndex)
    public ulong VisibilityMask; 
    
    // Quick flag: Is this section completely empty (Air)? 
    // If so, all faces connect to all faces (Mask = ~0).
    public bool IsEmpty;
}
```

### 3.2. Generation Logic (Inside `MeshGenerationJob`)

When generating a mesh, we run a localized **Union-Find** or **Flood Fill** on the 16x16x16 voxel grid.

1. Identify all Air voxels touching the **Top** boundary. Group them into a "Set".
2. Identify all Air voxels touching **Bottom**, **North**, etc.
3. Flood fill through the air voxels.
4. If the "Top Set" merges with the "Bottom Set", we set the `Top->Bottom` bit in the mask to 1.

## 4. Runtime Logic: The Render Loop (BFS)

Every frame (or every time the camera moves a significant distance), we run a Breadth-First Search.

**Manager:** `VisibilityManager.cs`

1. **Disable All:** Mark all `SectionRenderers` as invisible (logically, don't actually toggle GameObjects yet to avoid flicker).
2. **Start Node:** Find the `ChunkSection` containing the Camera. Mark it Visible.
3. **Queue:** Push the Camera Section to a `Queue`.
4. **Loop:**
    * Pop Section `S`.
    * For each neighbor `N` (Up, Down, N, E, S, W):
        * **Check 1 (Frustum):** Is `N` inside the Camera Frustum? If No, Skip.
        * **Check 2 (Backtracking):** Have we already visited `N`? If Yes, Skip.
        * **Check 3 (Connectivity):**
            * We are trying to exit `S` via `Face F`.
            * Does `S.Connectivity.Mask` say that `EntryFace` connects to `Face F`?
            * *Note: For the Camera Section, we assume we can reach all faces.*
    * If all checks pass:
        * Mark `N` Visible.
        * Record which face we entered `N` from (for the next iteration's connectivity check).
        * Enqueue `N`.

5. **Apply:** Iterate all chunks.
    * If `Section.Visible == true` AND `GameObject.activeSelf == false` -> Enable.
    * If `Section.Visible == false` AND `GameObject.activeSelf == true` -> Disable.

## 5. Implementation Roadmap

### Phase 0: Prerequisites (Completed)

- [x] **Section Rendering:** Switched from Monolithic Chunks to `SectionRenderer` (`Chunk.cs`).
- [x] **Data Tracking:** Implemented `IsEmpty` and `IsFullySolid` tracking in `ChunkSection` and `SectionJobData`.
- [x] **Generation Optimization:** Implemented Empty Section skipping in `MeshGenerationJob`.

### Phase 0.5: Renderer-Ownership Split (✅ Completed 2026-07-25 — see §7.3)

- [x] **Ownership split:** occlusion visibility uses `MeshRenderer.forceRenderingOff` via
  `SectionRenderer.SetOcclusionCulled(bool)` — the only code that *sets* that flag, reserved exclusively for the future `VisibilityManager` (`Clear()` is the one other writer, **reset-only**
  — see the §7.3 note); `GameObject.SetActive` stays owned by
  `SectionRenderer` ("has geometry"). Shipped as phase **MP-5** of
  [MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md), with the renderer-fixture baselines this section did not specify: **B28** (the apply path never writes the flag — asserted over both a non-empty and an empty update), **B29** (`Clear()` resets it to false on pool recycle — the §7.5 conservative "render" default), **B30**
  (`SetOcclusionCulled` round-trips without touching `activeSelf`). *Production sets the flag nowhere yet — the culler in Phases 2–3 is its first caller.*

### Phase 1: Data Generation (The Hard Part)

- [ ] **Data Structure:** Create `SectionConnectivity` struct in `JobData.cs`.
- [ ] **Storage:** Add a `NativeArray<SectionConnectivity>` to `ChunkData`.
- [ ] **Job Implementation:** Create a Burst-compiled `ConnectivityCalculatorJob` (or integrate into `MeshGenerationJob`).
    * **Algorithm:** Run a 16x16x16 Flood Fill to determine face-to-face reachability.
    * **Output:** Compress results into a `ulong` mask (6 entry faces * 6 exit faces).

### Phase 2: The Visibility Manager

- [ ] **Manager Class:** Create `VisibilityManager` (MonoBehaviour).
- [ ] **BFS Job:** Implement the BFS traversal loop using `NativeQueue` and `Burst`.
    * **Input:** `NativeHashMap<ChunkCoord, ConnectivityData>` (Global lookup of all loaded chunks).
    * **Input:** Camera Frustum Planes.
    * **Output:** A `NativeList<ChunkSectionID>` (or coordinate + index) of sections to enable.

### Phase 3: Integration

- [ ] **World Hook:** Update `World.cs` to feed data to the `VisibilityManager` every frame.
- [ ] **Renderer Update:** Drive `SectionRenderer.SetOcclusionCulled(bool)` from the BFS result — **not** `SetActive`, which stays the "has geometry" axis (§7.3, shipped in Phase 0.5).

## 6. Edge Cases & Optimizations

* **The "Transparent Block" Issue:** Glass allows visibility but might block movement. The connectivity check must treat Transparent blocks as "Air" for visibility purposes.
* **Latency:** If the BFS is too slow to run every frame, run it every 5 frames or in a background job. The visual artifact would be "chunks popping in" when turning corners rapidly.
* **Spatial Hashing:** To feed the Job System, we need a fast way to look up neighbor connectivity data. A flat `NativeHashMap` of `(ChunkCoord -> Data)` is best.

---

## Recommendation

Do **not** try to hack this into the existing `MeshGenerationJob` as a quick fix. This requires a dedicated system. The current "Empty Section" optimization is a great first step. The architecture above is the correct next step to solve the specific "Surface walking over caves"
performance issue.

---

## 7. Correctness Analysis (added 2026-06-12) — why the previous attempt corrupted, and why the §4 BFS as written would too

A previous implementation attempt (gating underground sections on air counts / "access to the section above relative to the player") produced major rendering corruption and was removed. This section documents the failure modes so the next attempt avoids them. **Tracked in
`PERFORMANCE_IMPROVEMENTS_REPORT.md` as `GS-5`.**

### 7.1 Count-based heuristics are unsound — no amount of tuning fixes them

`nonAirCount` / `opaqueCount` (which exist today in `ChunkSection` — Phase 0 ✅) are scalar summaries. Visibility is a **topology** question: a section can be 90% air yet completely sealed (a roofed cavern), or 99% solid with one tunnel through it (visible). Any rule built from counts must therefore both **over-cull (holes in the world — the observed corruption)** and under-cull. The counts remain useful as fast-path shortcuts (`IsEmpty` → all faces connect; `IsFullySolid` → no faces connect — both already exact), but the general case requires the
per-face connectivity mask of §3. There is no sound shortcut that avoids computing it.

### 7.2 The naive BFS has two known correctness traps

The §4 pseudocode ("record which face we entered N from") reproduces the first one:

1. **Single-entry-face under-approximation (over-culling → holes).** A section is often reachable via *multiple* entry faces with different exit sets. If BFS marks it visited on first reach via face A, a later path arriving via face B never propagates B's exits — sections beyond it get wrongly culled. **Fix:** per-section visited state must be the *accumulated set of entry faces*
   (a 6-bit mask). Re-enqueue the section whenever a new entry face is added to its set; propagate the union of exits of all accumulated entries. The BFS converges quickly because each section re-enqueues at most 6 times.
2. **Missing direction restriction (over-rendering, and interaction bugs with frustum checks).**
   The canonical algorithm (Tommaso Checchi's "cave culling" used by Minecraft) additionally tracks, per BFS path, the set of axis directions already traveled and **never steps opposite to a direction in that set** (e.g. a path that has gone +X may never step −X). This prevents the search from wrapping around behind solid walls. It is a *conservative* restriction: it can only over-render slightly, never over-cull — the safe direction for a culling bug to fail.

### 7.3 Ownership conflict with existing renderer toggling (a likely source of the old corruption)

Today **three owners** flip section visibility: `SectionRenderer.UpdateMeshNative` calls
`SetActive(false/true)` based on vertex count, `SectionRenderer.Clear()` deactivates on pool recycle, and `Chunk.Reset` clears all renderers. A visibility manager that also toggles
`SetActive` races all three — e.g. a remesh re-activating a section the culler decided is hidden, or the culler re-activating a section whose mesh was just cleared (garbage/stale mesh on screen =
"corruption").

**Fix — separate the two axes of visibility onto different mechanisms** *(✅ shipped 2026-07-25 as MP-5 / Phase 0.5; the seam below exists in code, with `SectionRenderer.SetOcclusionCulled(bool)` as the sole writer and B28–B30 pinning the invariants)*:

- *"Has geometry"* stays on `GameObject.SetActive` (owned by `SectionRenderer`, as today).
- *"Occlusion-culled"* uses `MeshRenderer.forceRenderingOff` (or `renderer.enabled`), owned **exclusively** by the `VisibilityManager`. The two never write each other's flag, so any interleaving of remesh/cull events composes correctly. (`forceRenderingOff` is preferred: it is designed for exactly this, costs nothing, and does not dirty transforms or trigger
  `OnEnable`-style work the way `SetActive` does.)

> ⚠️ **The one exception the culler must know about (added 2026-07-25 with MP-5).**
> `SectionRenderer.Clear()` — the pool-recycle path — also writes `forceRenderingOff`, but only ever
> **resets it to false**, never sets it. That is required by pool-reset safety and is the conservative
> §7.5 direction (a recycled section renders rather than inheriting a stale culled state). The
> consequence for Phase 2/3: **a `VisibilityManager` that memoizes its own culled-set must re-issue
> `SetOcclusionCulled` after a section's chunk is recycled** — otherwise it believes a section is
> hidden that `Clear()` already un-hid. The failure direction is over-rendering, never a hole.
> (MP-5 deliberately shipped *without* a cached mirror inside `SectionRenderer` so that the renderer
> never holds a second, separately-stale copy of this state; the memo lives in the manager, which is
> also what a later GS-6/BRG presentation layer would own.)

### 7.4 Staleness rule

Connectivity masks are computed from the same voxel snapshot as the mesh (Phase 1 integrates the flood fill into `MeshGenerationJob`). The mask must be **published in the same main-thread step that applies the mesh** (`ProcessMeshJobs` → `ApplyMeshData`), never earlier — otherwise the culler can run with a mask describing geometry that is not on screen yet (or vice versa), which flickers holes during edits. While a section has a mesh job in flight, the culler should use the *old* mask (consistent with the old on-screen mesh).

> **Where that step lives today (MP-4/MP-6, 2026-07-25).** The path is now `ProcessMeshJobs` →
> `Helpers/JobCompletionPass` → `Helpers/MeshCompletionDriver.MergeJob` →
> `IMeshCompletionHost.TryApplyMesh` → `Chunk.ApplyMeshData`. It is still exactly one apply site, and MP-6
> established it as the place per-chunk **presentation** side-effects hang off: the chunk load animation
> now fires there, paired to the apply that earned it (a discarded result never animates). **Publish the
> mask the same way** — inside that hook, gated on the apply having succeeded. Two conveniences fall out:
> the `IMeshCompletionHost` seam means a validation baseline can observe the publish with a fake host (the
> B31–B33 pattern), and MP-3 guarantees a rebuild requested mid-flight stays queued, so the mask refresh
> that request implies is never silently dropped.

### 7.5 Conservative defaults (make every unknown render)

- Unloaded / not-yet-meshed neighbor → treat as fully connected (visible beyond).
- The camera's own section and its 26 neighbors → always visible (also covers the camera sitting exactly on a section boundary).
- Sections containing active fluid mods or block-edit effects in the last N frames → visible (cheap insurance during churn).
- On any BFS budget overrun or missing data → fall back to "everything visible" for that frame, never "everything hidden".

### 7.6 Simplification: drop the frustum check from the BFS

§4's per-step frustum check makes the visible set depend on camera *rotation*, forcing re-runs on look-around (fast mouse movement = BFS every frame) and adding a second source of pop-in bugs. Recommended: compute a **position-only PVS** (potentially visible set) — connectivity from the camera section, no frustum — and let Unity's existing per-renderer frustum culling handle direction. The PVS then only needs recomputation when (a) the camera crosses a section boundary, or (b) a remesh publishes a changed connectivity mask in the currently-visible set.
Both are rare events compared to per-frame; rotation costs nothing. Add the frustum check back later only if profiling shows the PVS is too large (it shouldn't be — underground it is tiny, and on the surface frustum culling already does the work).

## 8. Performance expectations & prerequisites

- **Expected win:** while underground or walking over cave systems, the majority of subsurface sections stop rendering — saving draw calls (2 per section: opaque + transparent submeshes), vertex work, and Unity culling overhead. This is the single largest *rendering* win available after the GPU items in `PERFORMANCE_IMPROVEMENTS_REPORT.md` (see `GS-*`), and it compounds with them. On tile-based mobile GPUs the overdraw itself is partly absorbed by early-Z, so the measured win is mostly draw-call/vertex/CPU-culling — still large with thousands of section
  renderers.
- **Flood-fill cost:** one 16³ flood fill per meshed section inside the already-running
  `MeshGenerationJob` — a 4096-bit visited mask is 64 `ulong`s (512 B, stackalloc), seeded per face; Burst-friendly; expected well under 5% of current mesh job time. Per §3, `IsEmpty`/
  `IsFullySolid` sections skip the fill entirely (exact masks known).
- **No save-format impact:** masks are derived data, recomputed on mesh. Do not persist them. ✅
- **Prerequisites / links:**
    - `PERFORMANCE_IMPROVEMENTS_REPORT.md` `MR-3`/`MR-4` touch the same `SectionRenderer` code — land them first or together to avoid churn. *(Both ✅ done 2026-06-18.)*
    - The §7.3 ownership split is a hard prerequisite — implement it as its own small PR before the culler exists, it is independently harmless. *(✅ **Done 2026-07-25** as MP-5 — Phase 0.5 in §5. `SectionRenderer.SetOcclusionCulled(bool)` is the seam and the only setter; the culler is its first caller. Note §7.3's exception: `Clear()` resets the flag on recycle, so a memoizing manager must re-issue.)*
    - `PERFORMANCE_IMPROVEMENTS_REPORT.md` `GS-6` (BatchRendererGroup conversion) would replace the per-renderer `forceRenderingOff` mechanism with culling-callback visibility indices. Design the
      `VisibilityManager` to *output a visible-section set* consumed by a thin, swappable presentation layer (today: `forceRenderingOff` toggles; under BRG: the culling callback), so the culler survives a later BRG conversion unchanged — and if GS-6 is ever scheduled, decide its ordering against this system first.
    - `WORLD_SCALING_ANALYSIS.md`: taller worlds (Tier A) multiply subsurface section counts — this system's value grows ~linearly with world height, and cubic chunks (Tier C) would consume the same per-section masks unchanged. Design the mask storage per-section (not per-chunk-column)
      so it carries over.
- **Verification:** the corruption mode to test for is *holes* (over-culling): fly/noclip through cave networks while toggling a debug overlay that renders culled sections in wireframe; any visible-through-hole means an entry-face or staleness bug. Add a `VisibilityManager` debug stat (sections culled / total) to the `DebugScreen` to quantify the win.

---

## Document History

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.0** - Mandatory header completed (2026-07-26): `Version`/`Date`/`Target`, the **summary
  blockquote this document never had**, an `Audited`/`GS-5` tracking line and a relationship list. The
  existing bullet-style `Status`/`Current Implementation`/`Context` block is kept directly below as the
  at-a-glance state. No design content changed. First versioned edition.
* *(2026-07-25, `f443903e` · `d3012337`)* - **Phase 0.5 completed by MP-5**: `SectionRenderer.SetOcclusionCulled`
  became the only *setter* of `forceRenderingOff`, removing the three-owner `SetActive` surface the
  culler would otherwise have landed on. §7.3 narrowed accordingly (`Clear()` is reset-only), and the
  MP-6 draw-tail retirement removed the stale-visibility-actor class §7.3 warned about.
* *(2026-07-06, `0a12036a`)* - MP-* orchestration plan scheduled the §7.3 ownership split as its own
  phase, making it an explicit prerequisite rather than an assumption.
* *(2026-07-02, `7f75338a`)* - Tracked as `GS-5` in the performance backlog, with blocker status recorded.
* *(2026-06-12, `cff7265e`)* - Added the correctness analysis of the cave-culling BFS (§7) — the
  entry-face accumulation and staleness rules that make the traversal sound.
* *(2025-11-29, `16aa7e4c`)* - Originated with the section-based meshing work that introduced empty-section
  skipping, the "Current Implementation" this design extends.

---

**Last Updated:** 2026-07-26 (header completed; Phases 1–3 still open)
**Next Review:** when `GS-5` Phase 1 is scheduled — re-verify §5's contract first (Phase 0.5 is closed,
and §4.3's single apply site is now also the load-animation trigger point).
