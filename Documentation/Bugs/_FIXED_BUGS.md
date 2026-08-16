# Fixed Bugs Archive

This file consolidates all bugs that have been resolved. Entries are moved here from their respective bug files when the fix is confirmed working. Each entry is kept for historical reference and to document why the problem occurred and how it was solved.

---

## Lighting

### ~~01. Ghost lighting at chunk borders~~

**Severity:** Bug  
**Files:** `NeighborhoodLightingJob.cs`, `WorldJobManager.cs`  
**Fixed:** March 2026

**Symptom:** After blocking sunlight access through a vertical tunnel, a bright spot of ~light level 5 persisted on chunk borders and did not fully darken, even after the sky was completely sealed.

**Root Cause:** A cross-chunk read-after-write hazard in `NeighborhoodLightingJob`. `PropagateDarkness` could not modify neighbor chunk data (marked `[ReadOnly]`), so `PropagateLight` read stale data and re-spread light, undoing the darkness propagation.

**Fix:** Implemented a write-through cache (`NativeHashMap<long, uint>`) inside `Execute()`. `SetLight` writes to the cache for neighbor positions; `GetPackedData` checks the cache before the ReadOnly arrays. This ensures all light changes within a single job execution are immediately visible to subsequent reads.

---

### ~~02. Light leakage on chunk corners~~

**Severity:** Bug  
**Files:** `World.cs` — `AreNeighborsReadyAndLit`, `VoxelData.cs`  
**Fixed:** March 2026

**Symptom:** Digging and then sealing a vertical tunnel at a chunk corner left the tunnel fully lit even after blocking all skylight access.

**Root Cause:** `AreNeighborsReadyAndLit` only checked the 4 cardinal neighbors. Mesh and lighting jobs copy data from all 8 neighbors (including diagonals), so a diagonal neighbor running a lighting job could provide stale data.

**Fix:** Extended `AreNeighborsReadyAndLit` to also iterate `VoxelData.DiagonalNeighborOffsets` and apply the same stability checks (no active generation/lighting jobs, no pending light changes) to all 4 diagonal neighbors.

---

### ~~03. Cross-chunk sunlight modification skips blocks at `currentSunlight == 15`~~

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (lines 569–574)  
**Fixed:** March 2026

**Symptom:** Placing a block that should cast a shadow across a chunk border sometimes had no effect on the neighbor's sunlight.

**Root Cause:** The code skipped any cross-chunk sunlight modification where the target voxel already had sunlight level 15 and the incoming value was lower — intended to ignore stale data, but also silenced legitimate darkening operations.

**Fix:** Removed the unconditional skip. The stale-data scenario is now handled by proper job sequencing and the write-through cache.

---

### ~~04. `ModifyVoxel` heightmap downward-scan uses `IsOpaque` instead of `IsLightObstructing`~~

**Severity:** Bug  
**Files:** `ChunkData.cs` — `ModifyVoxel` (lines 335–351)  
**Fixed:** March 2026

**Symptom:** Removing a block that was transparent to rendering but opaque to light produced an incorrect new heightmap value, corrupting sunlight in that column.

**Root Cause:** Case 2 of the heightmap update (block removal) scanned downward checking `IsOpaque` while Case 1 (block placement) correctly used `IsLightObstructing`. A block transparent to rendering but blocking to light would be missed by the scan.

**Fix:** Changed the Case 2 downward scan in `ModifyVoxel` to use `IsLightObstructing`, matching Case 1.

---

### ~~05. Diagonal neighbors not checked by `AreNeighborsReadyAndLit`~~

**Severity:** Bug  
**Files:** `World.cs` — `AreNeighborsReadyAndLit`, `VoxelData.cs`  
**Fixed:** March 2026

**Note:** Resolved as part of the fix for Light Leakage #02 above. `VoxelData.DiagonalNeighborOffsets` was added and the check was extended.

---

### ~~06. `ApplyLightingJobResult` creates sections without updating opaque/non-air counts correctly~~

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ApplyLightingJobResult`  
**Fixed:** March 2026

**Symptom:** Internal chunk section voxel counts get desynchronized because light-only air blocks were being counted as solid. **Root Cause:** When the lighting job result contains non-zero voxel data for a null section (light in previously empty air), a new `ChunkSection` is created and populated. `RecalculateCounts` counted these air voxels as non-air because `data != 0`. **Fix:**

1. `RecalculateCounts` and `RecalculateNonAirCount` now check for solidity using `(data & ID_MASK) != 0` to properly ignore light-only air voxels.
2. Added an `isNewSection` flag to `ApplyLightingJobResult` to completely skip `RecalculateCounts` for freshly created light-only sections, as their pool-reset counts `(0, 0)` are already correct.

---

### ~~07. `ProcessLightingJobs` logs every frame when any dropped updates exist~~

**Severity:** Improvement  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs`  
**Fixed:** March 2026

**Symptom:** Performance hitching and extreme console log spam. **Root Cause:** The logging statement fired every frame that `_droppedLightUpdates` had entries. Since the collection is rebuilt each iteration, in busy worlds this effectively logged every frame. **Fix:** Gated the log behind `enableDiagnosticLogs`, replaced LINQ `.Sum()` with a manual loop to prevent allocations, and removed the unused `using System.Linq` directive.

---

### ~~08. Underwater cross-chunk shadow wall artifacts~~

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs`  
**Fixed:** April 2026

**Symptom:** Generating new chunks next to water bodies (or logging in dynamically) caused a 1-voxel wide vertical "wall of shadow" at the exact chunk boundary spanning beneath the water surface, despite correct lighting values everywhere else.

**Root Cause:** A data race condition in chunk boundary lighting. When a chunk ran its lighting job, the `RecalculateSunlightForColumn` correctly evaluated direct downward light (e.g., `sunlight=15` through water). However, the neighboring chunk's lighting job (running asynchronously or later) evaluated the border block horizontally using BFS on a *stale snapshot* of the chunk (where the original sunlight value was `0`). This BFS evaluated a weakened light value (`15 - 1 distance - 2 water opacity = 12`) and generated a `CrossChunkLightMod`. This
cross-chunk mod then overwrote the correct column value back on the main thread because the guard checking cross-chunk modifications was too narrow.

**Fix:** Broadened the safeguard in `ProcessLightingJobs` (`WorldJobManager.cs` line 518). Replaced the rigid `heightmap` check with a general principle:
cross-chunk BFS modifications that evaluate a non-zero sunlight level *must never lower* the current target's sunlight level. This correctly delegates authoritative column values to the chunk that actually owns them.

---

### ~~09. Diagonal Shadow Artifacts on Smooth-Lit Legacy Rotated Blocks~~

**Severity:** Low (cosmetic)  
**Fixed:** June 2026 **Status:** Resolved

**Description:**
With smooth lighting enabled, flat terrain surfaces (especially visible on sand/desert biomes) exhibit diagonal shadow lines forming a subtle zigzag or checkerboard pattern. The artifacts follow the quad triangulation diagonal and are most visible on large, uniformly lit horizontal surfaces viewed at a shallow angle.

**Root Cause:**
`GenerateStandardCubeWithLegacyOrientation` computes corner-averaged light values using the world face index `p` but emits vertices using the translated face index `translatedP` (which accounts for the block's Y-axis texture rotation). For side faces this is inherently correct because `GetTranslatedFaceIndex` remaps to a face whose vertex ordering, after rotation, aligns with the world corner positions. But for top/bottom faces (`translatedP == p`), the vertices are rotated while the corner light values are not, assigning lights to wrong vertex positions
and causing the anisotropy fix to choose wrong triangulation diagonals.

**Fix:**
Added `PermuteCornerLightsForYRotation` in `MeshGenerationJob.cs` which permutes `(l0, l1, l2, l3)` for top/bottom faces based on the Y rotation step count (0°/90°/180°/270°). The permutation was derived by tracing each vertex's post-rotation world position back to the corner offset LUT index it corresponds to. Called immediately after `CalculateCornerLights` and before `GenerateStandardCubeFace` in the `GenerateStandardCubeWithLegacyOrientation` smooth-lighting branch. See
also: [Architecture doc Section 2.5.4](../Architecture/SMOOTH_AND_RGB_LIGHTING.md#254-legacy-rotated-blocks).

---

### ~~10. Blocklight leaks into opaque volumes (woken surface-lit opaque voxels become BFS sources)~~

**Severity:** Medium (visual-only inside solid terrain, but corrupted saved light data and compounded with every nearby edit)  
**Fixed:** June 2026 (was Bug 09 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game via the BlockLight `VoxelDebugVisualization`; guarded by validation suite baseline **B9** (promoted from known-bug repro scenario K09)  
**Files:** `NeighborhoodLightingJob.cs` — `PropagateLightRGB`; `ChunkData.cs` — `ModifyVoxel` neighbor wake-up (~lines 497–512, wakes lit neighbors without an opacity check)

**History:** Suspected since the original multithreaded/job-based lighting rewrite (visible in the BlockLight `VoxelDebugVisualization` mode), but never documented because lava was the only blocklight source and work focused on sky lighting. First captured deterministically by the validation suite's K07 oracle diffs (light stamped multiple voxels deep into the stone floor), then isolated as an independent defect.

**Symptoms:**
Blocklight values appeared *inside* opaque volumes, deeper than the legitimate 1-voxel surface stamp, triggered by any block edit adjacent to a lit opaque surface. Worse than a single-voxel creep: once an interior voxel was lit and re-entered a BFS queue (wake-ups, re-spread), the missing guard let it propagate *laterally within the solid layer* — the K09 repro showed a decaying light trail (12 → 1) running ~22 voxels through the stone floor at depth 2, radiating from beneath a single torch after one place/break edit. Visible in the BlockLight debug
visualization; baked into saved region light data.

**Root Cause:**
Opaque voxels legitimately *receive* surface light (`source - 1`) but must never *propagate* it. The sunlight path enforces this (`PropagateLight`: `if (sourceProps.IsOpaque) return;`) — **the RGB blocklight path had no such guard**. Normally opaque voxels are never enqueued as sources, but `ChunkData.ModifyVoxel`'s neighbor wake-up enqueues ANY neighbor with `GetMaxBlocklight > 0`, including surface-lit opaque voxels. The job's seeding loop then saw `anyIncreased` (current surface light > wake node's old 0) and fed the opaque voxel into
`PropagateLightRGB`, which stamped `surface - 1` into the next layer of solid blocks.

**Fix:**
Added the missing opaque-source guard at the top of `PropagateLightRGB`, mirroring the sunlight path — with an exemption for emissive opaque blocks (lamps/glowstone must radiate their own emission): `if (sourceProps.IsOpaque && !sourceProps.IsLightSource) return;`. The `ModifyVoxel` wake-up loop was left unchanged (the job-side guard is the robust fix since wake nodes can also originate from cross-chunk applies and serialized queues). Baseline scenario **B9** asserts the containment invariant (no blocklight anywhere below the floor's surface layer across
the whole grid); it deliberately did not run a full oracle compare because the same edit also tripped Bug 07's cross-border removal/re-spread loss — the full-field oracle compare was restored after the Bug 07 fix (June 2026).

---

### ~~11. Cross-chunk emissive sources produce a hard cut-off (or flicker) at the chunk border~~

**Severity:** High **Fixed:** June 2026 (was Bug 07 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game (the hard cut-off no longer reproduces in a new world; the flicker required the cut-off defect, so it is resolved with it). Guarded by validation suite baselines **B10/B11/B12** (promoted from known-bug repro scenarios K07a/K07b/K07c); tripwire baseline **B7** (the blocklight removal race that depended on the old force-clear) stayed green through the fix. **Confidence:** Confirmed — the harness reproduced all three reported symptoms through the real job + the shared mod-apply logic, including light corruption
stamped into opaque floor voxels during the ping-pong. **Files:** `NeighborhoodLightingJob.cs` — `Execute` (BlocklightBfsQueue seeding), `PropagateDarknessRGB`, `PropagateDarkness`, `PropagateLightRGB`, `CheckEdgeVoxelRGB`; `Helpers/CrossChunkLightModApplier.cs` — `ComputeBlocklight`; `WorldJobManager.cs` — `ProcessLightingJobs` (blocklight mod application)

**Symptoms (user-confirmed in game):**
Placing an emissive block in chunk A directly against the border of chunk B, while chunk B contains its own emissive source whose light bleeds into A, produced a hard cut-off between the two light fields exactly at the chunk border (each side showed only its own chunk's source). Depending on configuration the border could instead flicker indefinitely. After a world reload the two sources blended correctly — until **any** light update near the border re-triggered the artifact. Pre-dated the RGB upgrade; RGB colors just made it visible.

**Root Cause (two compounding defects):**

1. **Cross-chunk uplift mods were re-interpreted as block-removal events by the receiving chunk.** The job's seeding logic treated **any** node at a non-emissive block with `OldBlock > 0` as "block was broken" and force-cleared the voxel to (0,0,0), then launched a darkness wave with the old values. Cross-chunk applies violated the wake-up convention whenever the target voxel already had light (exactly the two-sources-at-the-border case): the uplift from chunk A was wiped before it could spread into B, **and** B's own legitimate light near the border was
   eaten by a spurious removal wave.
2. **Removal re-spread seeds across the border were dropped.** In `PropagateDarknessRGB` (and the sunlight twin), when the darkness wave met a voxel whose light came from an *independent* source across the chunk border, the re-spread seed was discarded by the `IsInCenterChunk` guard, so light removed on one side was never restored from the neighbor's contribution.

**Why it flickered:** B's spurious removal wave emitted darkness mods back into A; A's next job re-placed its own light and emitted uplift mods back into B; each uplift was again destructively re-interpreted (defect 1) → mutual ping-pong, with both chunks rescheduling lighting jobs and rebuilding meshes every round. When the ping-pong damped out, the residual state was the static cut-off. A *secondary contributor*: the per-channel mod-apply guard let a *zero* channel from a stale-snapshot placement mod pass through as a darkness removal, clearing
channels owned by an independent source the emitting job never saw.

**Fix (four parts):**

1. *Defect 1 (destructive seeding):* the job's blocklight seeding is now per-channel — a channel is force-cleared only when it still holds exactly its pre-change value (`cur == old > 0`, the block-change signature); emission is stamped via per-channel max. `CrossChunkLightModApplier` wake nodes report `old = 0` for channels that didn't lose light (pure uplift ⇒ `anyIncreased` re-spread) and real old values only for genuinely lowered channels.
2. *Defect 2 (dropped re-spread):* when a darkness wave meets an independent source across the border, `PropagateDarkness`/`PropagateDarknessRGB` now pull the neighbor's attenuated contribution back into the just-darkened center voxel (via `CheckEdgeVoxel`/`CheckEdgeVoxelRGB`) instead of silently dropping the seed.
3. *Secondary (zero-channel pass-through):* `LightModification` gained an `IsRemoval` flag (job-output only — NOT in the save format); placement mods can now only raise channels, only genuine removal mods may zero them.
4. *Collateral engine bugs found by the repros:* (a) the `ushort.MaxValue` light sentinel collided with a legitimate fully-lit voxel (sky 15 + RGB 15,15,15 = 0xFFFF) — e.g. a white max-emission lamp on a sunlit surface neither propagated on place nor cleared on break; the RGB paths now bounds-check via `GetPackedData` and the redundant light-sentinel checks were removed. (b) An opaque emissive re-radiated *received* surface light; opaque sources now propagate only their own emission (spec rule mirrored in the validation oracle).

---

### ~~12. Broken emissive blocks leave permanent "ghost" blocklight (cross-chunk removal loss)~~

**Severity:** Medium–High (ghost values got baked into saved region data — permanent world corruption until manually disturbed)  
**Fixed:** June 2026 (was Bug 08 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — path 2 (in-flight overwrite race) confirmed in-game via flowing lava at a chunk border while constant water updates kept the neighboring chunk's lighting jobs in flight, plus deterministic suite repro; guarded by validation suite baseline **B13** (promoted from known-bug scenario K08a) with tripwire **B7** (the blocklight twin of the race) green throughout. Path 1 (unloaded-neighbor degradation) is verified by code inspection only — no in-game repro is practical without command support (requires breaking a border lamp while its
neighbor is unloaded), and the harness has no unload/save/load mirror. **Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (defer/drain, per-channel dropped-mod handling), `ApplyCrossChunkLightMod`, `DrainDeferredCrossChunkMods`, `DegradeDeferredCrossChunkMods`; `Serialization/LightingStateManager.cs` — pending-blocklight store + `pending_blocklight.bin`; `World.cs` — `LoadOrGenerateChunk` replay; `Editor/Validation/Lighting/Framework/LightingTestWorld.cs` — defer/drain mirror

**Symptoms (user-confirmed in game):**
Breaking an emissive block sometimes left its light behind permanently; no later update removed it. The ghost data lived in a *neighboring* chunk of the broken block.

**Root Cause (two independent loss paths for removal information):**

1. **Blocklight mods targeting unloaded/unpopulated chunks were degraded to sunlight-only column recalcs.** `ProcessLightingJobs` recorded only the affected *column* into the pending store (`pending_lighting.bin`), which feeds `RecalculateSunlightForColumn` — sky channel only. The RGB removal (and uplift) information was permanently discarded; a lamp at a border illuminates up to ~14 voxels into the neighbor, so this triggered easily at render-distance edges and during chunk streaming.
2. **`ApplyLightingJobResult`'s full-LightMap overwrite raced with mods applied during the job's flight.** The merge explicitly accepted that mods applied to live data mid-flight "may be temporarily lost", deferring to edge-check convergence — but edge checks only run during initial generation and are add-only, so a lost *removal* was permanent. For sunlight the loss was total (the reverted voxel made the wake node a no-op); blocklight self-healed only via the seeding force-clear (the mechanism guarded by B7).

**Fix (one part per loss path):**

1. *Path 2 (in-flight overwrite race):* cross-chunk mods targeting a chunk with its own unprocessed lighting job in flight are deferred (`_deferredCrossChunkMods`, pooled lists) and drained immediately after that chunk's merge, through the same shared `CrossChunkLightModApplier` path — wake nodes flag the chunk for another pass automatically. Targets that vanish mid-flight degrade their deferred mods to the persisted pending stores; shutdown releases them. The defer/drain is mirrored in the validation harness, so every wave-parallel scenario exercises
   it.
2. *Path 1 (unloaded-neighbor degradation):* blocklight mods targeting unloaded/unpopulated chunks are persisted in full (local position + RGB + `IsRemoval`, last write per voxel wins) to a NEW self-describing `pending_blocklight.bin` — a separate file, so no save-format migration was needed. On load-from-disk they replay through `CrossChunkLightModApplier.ComputeBlocklight` exactly like the live path; freshly *generated* chunks discard their pending mods (initial lighting recomputes from current neighbor truth). Sunlight mods keep the column-recalc
   degradation, which is authoritative for the sky channel.

---

### ~~13. Generated emissive blocks never seed the blocklight BFS (initial lighting)~~

**Severity:** Medium **Fixed:** June 2026 (was Bug 06 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game in a freshly generated world (the red-debug-lamp-as-forest-surface setup: generated lamps now illuminate their surroundings immediately at generation time). Guarded by validation suite baseline **B14** (promoted from known-bug repro scenario K06). ⚠️ *Old-world caveat:* worlds saved BEFORE this fix already carry the stamped-but-unpropagated lamp voxels in their light data, so the fix's trigger (stored light below emission) never fires for them — those lamps stay dark until a nearby block update wakes them, exactly
the pre-fix behavior. Only newly generated chunks are healed. **Confidence:** Confirmed — reproduced in-game (June 2026) by setting the forest biome's surface block to the red debug lamp: no block lighting was generated at all (the masking by fluid-simulation light updates only applies to flowing lava). **Files:** `NeighborhoodLightingJob.cs` — `SyncEmissionToLightArray`, `Execute` (queue seeding); `Chunk.cs` — `OnDataPopulated`; `World.cs` — initial lighting scheduling (`RecalculateSunLightLight`)

**Description:**
A chunk's initial lighting pass seeded only **sunlight**: `RecalculateSunLightLight()` enqueues all 256 columns into the sunlight recalc queue, and the `BlocklightBfsQueue` is empty for a freshly generated chunk. Inside the job, `SyncEmissionToLightArray` stamped each emissive block's RGB emission into its own `LightMap` cell — but **never enqueued those positions into the blocklight placement queue**, so the emission was not propagated to surrounding voxels. `Chunk.OnDataPopulated` only registers active voxels; it does not queue light updates either.

**Impact (user-confirmed in game):** A generated emissive block illuminated only its own voxel; surrounding air stayed dark until *some* block update near it woke the BFS (e.g. lava flow `ModifyVoxel` calls, or a player edit). Confirmed with the red debug lamp as a biome surface block; confined non-flowing lava pools and future emissive blocks in structures (glowstone etc.) hit the same gap.

**Fix (June 2026):** `SyncEmissionToLightArray` now takes the job-local blocklight placement queue and enqueues every position whose emission it stamps, so the stamped emission propagates within the same job (and reaches neighbors via the normal cross-chunk mods). The stamp condition (stored light below emission) is self-limiting: once propagated, the voxel holds at least its emission, so later job runs neither stamp nor enqueue — zero steady-state overhead for emissive-dense chunks (lava oceans). The index→position conversion runs only on the rare stamp
path, keeping the scan linear.

---

### ~~14. Cross-chunk edge check leaks light out of opaque border blocks~~

**Severity:** Low-Medium **Fixed:** June 2026 (was Bug 10 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed via the validation suite (repros flipped red→green, all baselines green). Promoted to validation baselines **B43** (sunlight) and **B44** (blocklight) from known-bug repros K10a/K10b. **Confidence:** Confirmed by the validation framework; not separately reproduced in-game, but **no regression observed in-game** after the fix. Likely in-game manifestation: the diagonal over-bright / light-decrease band seen along chunk borders **at world height** in the ChunkBorder debug VoxelVisualization mode (the opaque heightmap
surface sits exactly on the borders there, so its surface light leaked one voxel across each border — producing the cross-border diagonal pattern). **Found by:** the Bug-05 dense-canopy geometry fuzz (`LightingValidationSuite.Bug05Canopy.cs`), whose opaque under-canopy dividers sit on chunk borders and triggered the leak — the fuzz did not reproduce Bug 05 but surfaced this distinct defect. **Files:** `NeighborhoodLightingJob.cs` — `CheckEdgeVoxel`, `CheckEdgeVoxelRGB` (+ call sites in `CheckEdges` and `PropagateDarkness`)

**Description:**
Voxels just inside a chunk border ended up **over-bright** (more light than the borderless-correct value) when an *opaque* block sat on the adjacent chunk's matching border voxel. The edge-reconciliation pass (`NeighborhoodLightingJob.CheckEdges`) read the cross-chunk neighbor's stored light and propagated an attenuated copy into the center chunk **without checking whether that neighbor was opaque** — so an opaque wall's *received surface light* (which it must never re-transmit) leaked across the chunk boundary. Because the edge check is add-only (
`CheckEdgeVoxel` can only raise light, never lower it), the surplus was never reconciled away and persisted until a full relight. This is the **inverse artifact** of Bug 05 (over-bright instead of shadowed), explicitly anticipated in Bug 05's June-2026 observation.

**Root Cause:**
`CheckEdgeVoxel` (sunlight) guarded `centerProps.IsOpaque` but not the *neighbor's* opacity, and `CheckEdgeVoxelRGB` (blocklight) propagated the opaque neighbor's *stored* channels rather than its emission. The in-chunk propagators do the opposite — `PropagateLight` returns early on an opaque source and `PropagateLightRGB` substitutes an opaque source's own emission for its stored light. The cross-chunk edge path omitted the symmetric guard, so opaque blocks acted as sunlight sources (and received-surface-blocklight sources) across chunk borders.

**Fix (June 2026):**

- `CheckEdgeVoxel`: now receives the neighbor's packed voxel and returns early when the neighbor is opaque (an opaque block has no transmissible sky light — mirror of the `PropagateLight` source guard).
- `CheckEdgeVoxelRGB`: when the neighbor is opaque, seeds from its **emission** (`EmissionR/G/B`) rather than its stored blocklight — mirror of the `PropagateLightRGB` opaque-source rule, so opaque *emissive* blocks (lamps) still illuminate across borders (guarded by baselines B5/B10) while opaque non-emissive blocks transmit nothing.

---

### ~~15. Initial-load sunlight removal/re-placement oscillation across chunk seams (reload non-convergence)~~

**Severity:** Medium-High **Fixed:** June 2026 (was Bug 11 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game (the world that stalled now loads quickly with no stuck-light logging) AND via the validation suite (repro flipped red→green, all baselines green). Promoted to validation baseline **B48** from known-bug repro K11a. **Confidence:** Confirmed in-game and by the validation framework. **Found by:** a user-reported `ForceCompleteDataJobsCoroutine exceeded max iterations` error on reloading a recently-played world; root-caused with the gated `[LightingDiag]` startup-convergence instrumentation added to
`World.ForceCompleteDataJobsCoroutine` / `WorldJobManager.ProcessLightingJobs`. **Files:** `CrossChunkLightModApplier.cs` (`ComputeSunlight`, new `InChunkSunlightSupport`), `WorldJobManager.cs` (`ApplyCrossChunkLightMod` apply site + diagnostics), `World.cs` (startup convergence diagnostics).

**Description:**
Loading a recently-played, saved world (created → moved around → saved → reload) could stall the synchronous startup lighting pass until its safety watchdog tripped:

```
ForceCompleteDataJobsCoroutine exceeded max iterations (N) during Lighting Phase. Forcing exit.
Remaining jobs: Lighting(0). Pending chunks: InitialLight(0), LightChanges(M), EdgeChecks(0)
```

A small cluster of chunks never reached a lighting fixpoint: every sweep their `NeighborhoodLightingJob` reported `IsStable = false` and emitted cross-chunk sunlight mods, so `HasLightChangesToProcess` was re-set immediately after the jobs drained. The world still loaded (the coroutine force-exits and `Update()` continues), but the initial load was slow (tens of seconds) and the console spammed the error. Freshly generated worlds were unaffected.

**Root Cause (confirmed via `[LightingDiag]` instrumentation):**
A **stale-snapshot sunlight removal/re-placement 2-cycle** across chunk seams. Diagnostics on a stuck load showed, every sweep: `unstable = <clusterSize>`, `edgeRecycle = 0`, and a perfectly balanced `eff[sunPl=K, sunRm=K]` (blocklight uninvolved), with the same voxel flipping forever (`Sun rm @(x,y,z) sky 10->0`).

Each sweep all of a chunk's neighbor lighting jobs run against **schedule-time snapshots** (the `LightN/LightE/...` maps), one sweep stale. Where skylight is **mutually supported across a seam** (both sides feed the same border column) and a chunk loads with a persisted in-flight darkness node (`ChunkSerializer.ReadLightQueue` restores `SunlightBfsQueue` — the chunk was serialized mid-darkness-wave), one chunk's `PropagateDarkness` clears the across-border voxel (`SetSunlight(..., 0)` → removal mod, which `CrossChunkLightModApplier.ComputeSunlight`
applied **unconditionally**) while the neighbor — working from a stale snapshot where the source still looks present — re-places it and pushes back. Neither job sees the other's current state in the same sweep, so removal and placement cancel forever and the border settles one level below the oracle. The re-placement is correct (the value is genuinely supported); the removal is the spurious actor, driven by the stale view. Reload-specific because a fresh world fills skylight top-down consistently (no removal waves), whereas a reloaded world restores
per-chunk BFS queues captured mid-propagation, giving adjacent chunks inconsistent border skylight — the seed for the stable cycle.

**Fix (June 2026):**
`CrossChunkLightModApplier.ComputeSunlight` now vetoes a cross-chunk sunlight **removal** (level 0) when a neighbor *inside the receiving chunk* independently supports the current value (`InChunkSunlightSupport(chunk, localPos) >= currentSunlight`). The in-chunk side is the only data the receiver can trust — the cross-chunk side is exactly the stale source of the bad removal. A genuinely dependent voxel (no in-chunk support) still clears, so legitimate cross-chunk darkness (B3 roof shadow, B43/B44 opaque-border) is preserved. Both apply sites —
production `WorldJobManager.ApplyCrossChunkLightMod` and harness `LightingTestWorld.ApplyModToChunk` — compute the in-chunk support and pass it to the shared applier, so the decision is never duplicated. Guarded by baseline **B48** (two symmetric sky shafts feeding a seam, both seam chunks seeded with a stale reload removal, run wave-parallel → converges and matches the borderless oracle; before the fix it pinned the seam at sky 4 instead of 5 and never converged).

---

### ~~16. Over-bright cross-seam sunlight loop survives source removal~~

**Severity:** Medium **Fixed:** June 2026 (was Bug 12 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed via the validation suite (repro flipped red→green, all baselines green). Promoted to validation baseline **B53** from known-bug repro K12a, with over-correction tripwire **B50** and completeness baselines **B51** (asymmetric two-shaft) / **B52** (multi-hop ring), grouped in `Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug12.cs`. **Confidence:** Oracle-confirmed (validation framework). **Never observed in-game** — the artifact was identified from a harness oracle mismatch, not a player
report — so confirmation rests on the borderless oracle rather than an in-game sighting. **Found by:** a harness oracle mismatch while building baseline **B49** (code-review finding 3): the end-to-end source-removal scenario could not isolate the opacity guard *because* this loop swallowed the removal, so B49 was implemented as a direct decision-logic test instead. **Files:** `NeighborhoodLightingJob.cs` (`PropagateDarkness` cross-seam removal emit + `IsVerticallySkyLit`, `EmitCrossChunkSunlightRemoval`, `LocalToGlobal`, per-job dedup); adjudicated by
the reused Bug-11 veto `CrossChunkLightModApplier.ComputeSunlight` / `InChunkSunlightSupport`.

**Description:**
When a sunlight source that feeds a chunk-seam voxel is removed — e.g. a block placed that roofs a sky shaft, or a sky-feeding block broken near a chunk border — and that seam voxel *also* has a (weaker) in-chunk light path, the cross-seam light can fail to be removed. The seam voxel and its neighbor across the boundary end up **mutually supporting each other** (each is lit "by" the other), so the removal flood-fill never finds a real source to trace the darkness back to. The result is a **stable-but-wrong** over-bright field: the removed source's
contribution lingers across the seam (and everything downstream of it stays correspondingly too bright) until a full world reload — or an unrelated nearby edit re-triggers the pass.

This is the over-bright ("inverse") counterpart to **Bug 05**'s shadow patches, and the concrete mechanism behind the inverse-artifact note in that entry. It is the *static* form of the defect: unlike Bug 11 (which oscillated), this converges and stays converged at the wrong value.

**Root Cause:**
The sunlight removal pass (`NeighborhoodLightingJob.PropagateDarkness`) clears a voxel's light by identifying neighbors that were lit **by** it (`neighborSky == thisOldSky − cost`) and removing them. A 2-cycle that straddles a chunk boundary — voxel A (chunk X seam) lit from voxel B (chunk Y seam) and B lit from A — has, once the genuine external source is gone, **no removal initiator**: each side reads the other's still-high value as legitimate support. Because cross-chunk mods are computed against schedule-time snapshots, neither side ever observes the
other dropping first, so each re-places the light it just removed from the other's stale value, settling into an over-bright fixed point. `CheckEdgeVoxel` cannot correct it — it only **adds** missing light, never removes over-bright (see Bug 05's note).

> **Not** the Bug 11 veto. The in-chunk-support veto (`ComputeSunlight` / `InChunkSunlightSupport`) guards the opposite direction — it prevents a *spurious* removal of a genuinely-supported voxel. Bug 12 is a *legitimate* removal that never initiates. The two are independent: the veto is not the **cause**. (The fix below does *reuse* the existing veto as the safe adjudicator for the new cross-seam removal it emits — but the veto alone, without that emission, never fired here because no removal mod was ever sent across the seam.)

**Reproduction (observed in the lighting harness):**

1. Build a 1-wide roofed corridor that crosses a chunk seam, lit by a single sky shaft on one side; arrange a weaker in-chunk path to the seam voxel as well.
2. Run initial lighting to convergence (matches the borderless oracle).
3. Roof the shaft (`PlaceBlock` stone over it) to remove the dominant cross-seam source, then run to convergence again.
4. **Observed:** the seam voxel and the voxels downstream of it remain ~2 levels brighter than the borderless oracle — the removed source's contribution never clears. The field is stable ("converged") but does not match the oracle.

**Relationship to other bugs:**

- **Bug 05** — the dark counterpart (under-lit seam patches); shares the add-only `CheckEdgeVoxel` limitation as a contributing factor.
- **Bug 09** — cross-chunk *blocklight* delivery race; different channel and mechanism (delivery drop vs. sourceless loop).
- **Bug 11** (fixed) — cross-seam sunlight removal/re-placement *oscillation*; the in-chunk-support veto. Distinct: over-removal vs. this under-removal.

**Validation suite (June 2026):** unlike Bug 05 / Bug 09 (not synchronously reproducible), this **does** reproduce deterministically in the synchronous harness, captured test-first as known-bug scenario K12a and now promoted to baseline **B53** (`Baselines/LightingValidationSuite.Baseline.Bug12.cs`, run via `Minecraft Clone/Dev/Validate Lighting Engine`). B53 builds a 1-wide roofed corridor straddling the `x15|16` seam, lit by a single sky shaft that opens **both** shared seam columns (so the two seam voxels are mutually equal at sky 15 — each appears
lit "by" the other). After initial convergence (matches the oracle), it roofs both seam columns — one `PlaceBlock` per chunk, so both seam chunks carry a sunlight column recalc into the **same** wave — and runs the grid **wave-parallel** (`RunWaveToConvergence`, production's concurrent-job / schedule-time-snapshot model). Before the fix the field converged to a **stable** state (the static defect, not Bug 11's oscillation) that did **not** match the oracle: the seam pinned at its pre-roof value minus one (14) and stayed bright downstream while the
borderless oracle was dark. (A single-side shaft + single roof edit, or sequential `RunToConvergence`, does **not** reproduce — the simultaneous same-wave perturbation of both seam chunks is required, mirroring how B48 forces the sibling Bug 11.)

**Fix (June 2026):** the missing removal *initiator* is now supplied across the seam. In `NeighborhoodLightingJob.PropagateDarkness` (sunlight), when a darkness wave reaches a cross-chunk neighbor sitting at **exactly** the removed level — the 2-cycle signature — the job now emits a cross-chunk sunlight **removal mod** for that neighbor (in addition to the existing Bug-07 pull-back re-light). The neighbor's chunk re-evaluates the removal through the existing Bug-11 veto (`CrossChunkLightModApplier.ComputeSunlight` / `InChunkSunlightSupport`): an in-chunk
source that still independently supports the value (e.g. a horizontal shaft) **keeps** it, while a value that was only the stale mutual-support loop **clears** — so the two seam voxels collapse to the oracle within a couple of waves instead of pinning over-bright. Two guards keep it surgical and prevent over-correction (caught in development by **B50**): the emission fires only when the neighbor is **neither fully opaque** (an opaque wall/floor only stores non-propagating surface light, never participates in a light loop) **nor directly sky-exposed** (
`IsVerticallySkyLit` — a voxel receiving full vertical sunlight is independently lit). Without those, an ordinary sky-lit border voxel would be spuriously cleared whenever any shadow's darkness wave reached a chunk seam. The emit-only helper appends the modification without touching the job's write-through cache, so the in-job pull-back still reads the unchanged snapshot, and dedups per job (a darkness wave can reach the same neighbor from many removal nodes; one mod suffices, so duplicates are skipped to keep the mod list from growing by O (wavefront)).
No save-format or veto-signature change.

**Scope (June 2026 completeness investigation):** the `== removed level` emit targets specifically the **symmetric mutually-equal** seam — the only configuration that stalls, because neither side has a removal initiator. Asymmetric and multi-hop cross-seam loops were probed (two shafts at unequal distance; a ring corridor crossing the seam twice) and **converge correctly even with the fix neutered**: any level gradient has a strictly-lower side, which the existing `PropagateDarkness` "neighbor `<` removed level" branch already removes (it emits a removal
via `SetSunlight`'s cross-chunk path). These are pinned as completeness baselines **B51**/ **B52** (general convergence guards, not fix tripwires — they stay green pre-fix).

---

### ~~17. Large suspended opaque slab never settles (oscillating cross-chunk skylight)~~

**Severity:** Medium **Fixed:** July 2026 (was Bug 13 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game (fluid-stress run with an opaque `BlockIDs.Stone` floor at
`REGION_CHUNKS ≥ 3`: the previously-endless flashing between the placed floor and fluid slabs is gone, the substrate settle completes, and higher region counts no longer hang) and via the validation suite (repro flipped red→green with all baselines green). Promoted to baselines **B56–B59** from known-bug repros K13a–K13d, grouped in
`Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug13Slab.cs` (B58 dynamic-stamp is the primary fix tripwire — the deterministic pre-fix live-lock). **Found by:** player report from the fluid-stress benchmark; first faithful repro built as roadmap item **AS-1**
([LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)) — the sync-repro analysis that revised the earlier "needs the async wave" assumption. **Files:** `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` (`CrossChunkSunlightSupport`, the extended Bug-11 veto), `Assets/Scripts/WorldJobManager.cs` (`ApplyCrossChunkLightMod` + emitter-carrying `DeferredLightMod`), mirrored in `Assets/Editor/Validation/Lighting/Framework/LightingTestWorld.cs`.

**Description:**
A large, flat **opaque** block layer (opacity 15) suspended in otherwise sky-lit air and **spanning a contiguous multi-chunk region** never reached a stable lighting state. The columns directly under the slab are shadowed while the surrounding air stays full-bright, so light spills in from the slab's perimeter and forms a cross-chunk skylight gradient beneath it. That gradient **oscillated / never converged**: the lighting jobs kept re-scheduling so
`WorldJobManager.HasActiveJobs` never returned to `false`, and in the scene view the slab's lit surfaces visibly **flickered** (light values churning frame-to-frame) rather than settling. Distinct from Bug 05 (wrong-but-static shadow patches): here the system reached **no fixed point at all** — a live-lock, not a static artifact.

**Root Cause (confirmed 2026-07-04, via the repro + oscillation probe + emit-neuter attribution):**
Not the edge-check rounds as originally suspected — a **mutual-removal machine between the Bug 12 cross-seam removal initiator and the Bug 11 veto's in-chunk-only support model**, at the slab region's interior seams. The under-slab gradient is *perimeter-fed*: a border voxel V in slab chunk A holds sky 14 supplied across a *different* seam by the sky-lit ring chunk — support the Bug 11 veto (`InChunkSunlightSupport`) cannot see, because it deliberately credits only in-chunk neighbors (V's in-chunk best is 13 < 14 → no veto). Meanwhile the adjacent slab
chunk B's darkness wave sees V at exactly the removed level, not sky-lit — the Bug 12 mutual-2-cycle signature — and emits a removal at it. The removal applies (no veto), V's chunk re-lights V via the seam pull-back from the ring's live 15, the re-spread crosses back into B as uplift mods, B's next pass emits the same removal again: a **period-2 live-lock** (the probe showed the light field hash-repeating with a cycle length of 1–2 while work stayed pending, across all 8 ring chunks of the slab, y 11–99). Neutering the Bug 12 emit converged the repro in
2 frames — the attribution test.

**Fix (July 2026):** the Bug 11 veto's support model was extended to match reality: independent support is now the max of (a) in-chunk neighbors (unchanged) and (b) **live cross-chunk neighbors in chunks other than the emitter** (`CrossChunkLightModApplier.CrossChunkSunlightSupport`). Live main-thread data is trustworthy — staleness was only ever a property of the *emitting job's snapshot* — and excluding the emitting chunk preserves Bug 12's collapse of genuine sourceless seam loops (the emitter is exactly the possibly-stale mutual-loop side, and that
loop pair has no third-party feed, so B53 stays guarded). Deferred cross-chunk mods now carry their emitter's origin (`WorldJobManager.DeferredLightMod`, mirrored in the harness) so the exclusion survives the defer/drain path. The perimeter-fed seam voxel is now vetoed instead of cleared, the counter-wave never launches, and the machine winds down. An emitter-side snapshot guard on the Bug 12 emit was tried first and rejected: it also suppressed load-bearing initiators whose "supporter" was itself ghost light, worsening the stale-ghost residue (see the
open **Bug 14**, the terminating over-bright sibling this repro also surfaced).

**Validation suite:** first faithful repro was synchronous (AS-1, 2026-07-04): the B58 geometry (grid 5, slab = center 3×3 chunks inside a sky-lit 16-chunk ring — the harness grid edge is the world edge, so an inset slab is required to model the perimeter feed) live-locked under unlimited-budget and single-slot scheduling with a proven period-2 field cycle; the seeded-shuffle sweep (B59) live-locked on other seeds. Generation-wave variants (B56/B57) were green pre-fix — the live-lock required the dynamic player-edit stamp against an established bright
field, not the initial wave. Post-fix: all four green with every prior baseline green (including B48/B50–B55, the Bug 11/12 family the extended veto touches).

---

### ~~18. Stale-snapshot cross-chunk sunlight ghost light survives dynamic multi-chunk darkening~~

**Severity:** Medium **Fixed:** July 2026 (was Bug 14 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game (fluid-stress run with an opaque Stone floor: the under-slab shadows begin patchy while the slab chunks are being stamped but now converge to correct shadow **before** the water cap is placed — previously they stayed patchy until that later edit rescued them) and via the validation suite (repro flipped red→green, all baselines green). Promoted to baselines **B60/B61** in
`Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug14Ghost.cs` (B61 = the promoted K14a seed-1 repro; B60 = the halo-node claim-contract guard from the hotfix), and the B59 sweep was upgraded to assert the borderless oracle across its full 75-seed space (previously termination-only because of this bug). **Found by:** the Bug 13 (AS-1) seeded sweep — the same slab repro's terminating over-bright exit — and observed in-game during Bug 13's fix confirmation (patchy under-slab shadows that only settled after a later mass edit).  
**Files:** `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` (`PullBackClaim` + the recording pull-back in
`PropagateDarkness`), `Assets/Scripts/WorldJobManager.cs` (`VerifyPullBackClaims`, `LastStalePullBacksCleared`),
`Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` (`PullBackClaimStillSupported`), mirrored in
`Assets/Editor/Validation/Lighting/Framework/LightingTestWorld.cs`.

**Description:**
When a large multi-chunk region darkened dynamically (e.g. an opaque slab stamped across several chunks of sky-lit air) while lighting jobs interleaved under budgeted, out-of-order scheduling, chunks could settle into a **stable but massively over-bright field**: stale "ghost" skylight survived under the slab (up to +14 vs the borderless oracle) across tens of thousands of voxels. The pipeline terminated normally — no pending light work, no flicker — so nothing ever re-examined the region; the ghost persisted until a full relight (world reload) or an
unrelated nearby edit. The **terminating sibling of Bug 13** (Lighting #17): the same AS-1 slab repro exposed both, Bug 13 as the non-terminating exit, this defect as the over-bright terminating exit.

**Root Cause (confirmed by neuter attribution):**
A job that ran concurrently with its neighbors' darkening re-lit its side of a seam from its **schedule-time snapshot** of the neighbor — the `PropagateDarkness` seam pull-back (`CheckEdgeVoxel`), whose value the placement BFS then re-spread through the chunk interior and across further seams as uplift mods. If the neighbor darkened after the snapshot, the re-lit gradient was sourceless, and no mechanism ever initiated a removal at a voxel nobody touched again: `CheckEdgeVoxel` is add-only (the Bug 05 note), the Bug 12 initiator only fires during an
active darkness wave, and the ghost chunk's own job ended stable. Attribution (seed-1 case): neutering cross-chunk sun **uplift mods** only reduced the residue 57.6k → 46.9k voxels, but neutering the **pull-back**
eliminated it entirely — the pull-back was the sole root; stale uplifts merely re-spread its ghost. The pull-back itself is load-bearing and could not be removed: with it neutered, B46 (pending-replay re-brighten), B50 (no black spot at a roofed seam), and B58 (the perimeter-fed under-slab gradient, 171k voxels under-bright) all go red.

**Fix (July 2026):** trust-but-verify. The pull-back stays exactly as is inside the Burst job, but every pull-back write is recorded as a `PullBackClaim` (center voxel, trusted neighbor voxel, written sky level) in a new job output list. At merge time — after `ApplyJobLightMap` and the deferred-mod drain — the main thread re-verifies each claim against the neighbor's **live** data (`WorldJobManager.VerifyPullBackClaims`, mirrored in the harness): a superseded claim (the voxel no longer holds the written value) is skipped; a claim the live neighbor still
supports (`CrossChunkLightModApplier.PullBackClaimStillSupported`, the exact `CheckEdgeVoxel`
write condition against live values) is kept, so fresh snapshots verify for free; an unverifiable claim (neighbor absent/unloaded) is kept conservatively; a **stale** claim is routed through the standard cross-chunk sunlight-removal veto with the claimed neighbor's chunk as the excluded emitter — a voxel with other genuine support survives, a sourceless one clears and wakes the chunk for the corrective darkness wave. Diagnostics:
`WorldJobManager.LastStalePullBacksCleared`. A stale- **uplift** veto was considered and deliberately NOT added:
attribution showed uplift ghosts are strictly downstream of pull-back ghosts, and the inbound-removal ordering through the defer/drain path self-heals genuine uplift staleness.

**Hotfix (same day, first in-game test):** the initial fix crashed real worlds with per-frame
`ObjectDisposedException` spam from `ProcessLightingJobs`. The column-recalc **shadow-caster** check (`RecalculateSunlightForColumn`) seeds darkness nodes at the highest block's horizontal neighbors *without* an in-center guard, so a border column legitimately starts a wave at local x/z = −1/16 — and a pull-back during such a **halo node**'s wave recorded a claim whose "center" position lies outside the chunk. The verifier then indexed the chunk with that position; the resulting exception aborted the whole `ProcessLightingJobs` pass after some jobs were
already released but before the end-of-pass `LightingJobs.Remove`, so every later frame re-touched disposed containers (note: this abort-cascades-into-spam shape is a pre-existing fragility of the pass, not specific to claims). Fixed twofold: claims are only recorded for center voxels (`IsInCenterChunk` guard in the job — a halo pull-back surfaces as a cross-chunk uplift mod instead), and both verifiers defensively skip any out-of-bounds claim so a malformed claim can never cascade again. Guarded by baseline **B60** (border shadow-caster halo-node
geometry; asserts the cross-border wave fires + converges on the oracle). The flat suite worlds never produced a border shadow caster, which is how this slipped past 52 green scenarios — real terrain hits that branch constantly.

**Validation suite:** deterministic repro K14a (grid-3 full-grid slab stamped under the pinned seed-1 schedule:
budget 2, cadence 1, shuffled completion — settled ~57.6k voxels over-bright pre-fix, worst +14 sky) flipped red→green and was promoted to **B61**; a 75-seed oracle-asserting sweep over both slab geometries came back fully clean and is now permanent as the upgraded **B59**; **B60** pins the hotfix's claim contract.

---

### ~~19. Cross-chunk sunlight surface stamp permanently lost after a border-column edit~~

**Severity:** Medium **Fixed:** July 2026 (was Bug 15 in `LIGHTING_BUGS.md`)  
**Status:** Resolved — confirmed in-game (hand-built 2-thick seam wall whose face voxels' only air exposure is across the boundary: in the pre-fix build the cap placement dropped the stored face values to 0 — visible in the F3 readout and as black columns in the F7 `VoxelDebugVisualization` sky view — while the fixed build holds 14, with 13 on faces fed by a dimmed 14-column, exactly the spec) and via the validation suite (K15b/K15c red→green, fuzz stamp seeds 0/9/12/19 green, all baselines green). Promoted to baselines **B62/B63** in
`Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug15Stamp.cs`. **Found by:** the HF-3 border-heightmap fuzz (K15a), on its very first seed. The fuzz's one remaining red (seed 14) is **Bug 05's edge-round exhaustion**, a different mechanism — see the Bug 05 entry in
`LIGHTING_BUGS.md` for the first faithful synchronous repro this fuzz also produced. **Files:** `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` (`CheckEdgeVoxel`, `CheckEdgeVoxelRGB`, BFS seeding,
`PullBackDimmerCrossSeamStamp`, `SampleSnapshotSkyLight`), `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs`
(`PullBackClaimStillSupported`).

**Description:**
At a chunk-border height step (a cliff face on the seam), the opaque face voxels carry the sunlight **surface stamp** (`source − 1`, the receive-but-don't-propagate rule pinned by baseline B39) fed by the *neighbor chunk's*
lit border air. An opacity-changing edit higher in the same border column triggered that column's sunlight recalculation, which wiped those stamps — and nothing ever re-applied them: the field converged (no pending work)
with the seam faces at sky 0 where the oracle and the engine's own generation wave put 14. Permanent until a full relight or an unrelated nearby edit flooded the seam again — the healing profile that made it the prime candidate mechanism for Bug 05's dense-biome border shadows (decoration VoxelMods use the same border-column edit path). In-chunk-fed stamps always recovered (the recalc's re-spread revisits in-chunk air); only exclusively cross-seam-fed stamps died, because every cross-seam re-derivation path (`CheckEdgeVoxel`/`CheckEdgeVoxelRGB`)
hard-refused opaque centers. **Visual severity turned out low**: the mesher shades faces from the adjacent air voxels, not the opaque voxel's own stored stamp, so the corruption was invisible in normal rendering (it showed in the F7 stored-light view) — but it corrupted the light field that oracle comparisons, future features, and smooth-lighting samples read.

**Fix (2026-07-05, five parts):**

1. `CheckEdgeVoxel` no longer refuses an opaque center: it receives the surface stamp (`source − 1`), written but never enqueued — the in-chunk opaque-surface rule extended across the seam.
2. `CheckEdgeVoxelRGB` — the same change per RGB channel.
3. `CrossChunkLightModApplier.PullBackClaimStillSupported` mirrors the new write condition (a fully-opaque center's claim is supported by `liveNeighborSky − 1`), keeping Bug-14 claim verification from clearing legitimate stamps.
4. The sun BFS seeding re-spreads an unchanged-but-lit edit node (an opacity-only change — e.g. breaking a stone-top block whose air keeps its old 15 — exposes faces that were never stamped; the in-chunk case).
5. **Residual fix** (`PullBackDimmerCrossSeamStamp` + `SampleSnapshotSkyLight`): an order-dependent residual (4 of 25 fuzz seeds) survived parts 1–4 — trace attribution showed a job with a *fresh* snapshot wiping the stamp internally: its wake-node darkness wave (old level 14) treated the dimmer live feed (10) as a child, zeroed the feed's halo copy (the removal mod was vetoed remotely — the feed had real support), and every re-derivation path then read the zeroed halo; a second same-job wave re-zeroed the stamp after the first re-derivation. Now a
   darkness wave meeting a dimmer or already-zeroed cross-seam neighbor re-derives a fully-opaque center's stamp from the pre-zero (or pristine-`[ReadOnly]`-snapshot) value — write-no-enqueue, recorded as a `PullBackClaim` and adjudicated against live data at merge (surviving feed → kept; dead feed → stale → cleared through the removal veto). Opaque-only because stamps cannot propagate: a stale write is one voxel, never a spreading ghost.

**Attempted and REJECTED:** the first dimmer-arm form routed through `CheckEdgeVoxel`'s attenuation — inverted from what was needed: `Attenuate` yields 0 for a fully-opaque center (never fired for the stamps)
while it re-lit AND enqueued *transparent* centers from dimmer stale neighbors, regressing B59/B61 with spreading over-bright ghosts (2497 voxels +12 sky). Part 5 is the corrected form: opaque-only, stamp rule, write-no-enqueue, claim-verified.

**Validation suite:** distilled repros K15b (seam cliff + cap edit, sun) and K15c (seam wall + torch break, RGB)
flipped red→green and were promoted to **B62/B63**; the border-heightmap fuzz that found the bug remains now baseline **B64** (its seed 14 also drove the Bug 05 fix — see Lighting #20 below).

---

### ~~20. Persistent chunk-border shadow patches in dense biomes (post-edit edge-round exhaustion)~~

**Severity:** Medium **Fixed:** July 2026 (was Bug 05 in `LIGHTING_BUGS.md` — the oldest open lighting bug)  
**Status:** Resolved — confirmed in-game (a freshly generated dense-forest world no longer shows the persistent dark patches under overlapping canopies near freshly-generated chunk borders that previously needed a world reload to clear) and via the validation suite (K15a seed 14 red→green, all baselines green). Promoted from known-bug repro **K15a** to baseline **B64**
(`Assets/Editor/Validation/Lighting/LightingValidationSuite.BorderHeightFuzz.cs`). **Found by:** long-standing player-reported symptom (dense forest biomes); the first faithful synchronous repro was the HF-3 border-heightmap fuzz's seed 14 (2026-07-05), after the geometry-axis fuzzes (B8 diagonal well, B42 dense-canopy) both converged and the "not synchronously reproducible" verdict had stood since June

2026.

**Files:** `Assets/Scripts/Data/ChunkData.cs` (`ModifyVoxel` border-column edge-check re-grant,
`BORDER_EDIT_EDGE_CHECK_ROUNDS`); mirrored in the harness (`LightingTestWorld.Builder.cs`,
`LightingTestWorld.RunReGrantedEdgeCheckRound`, `LightingFrameSimulator.RunToConvergence`).

**Description:**
Persistent dark/shadow patches near the borders of freshly generated chunks in visually dense areas (overlapping forest canopies), which only resolved on a full world reload or when an unrelated nearby edit forced a light update. The geometry axis was exhausted without a repro (B8 diagonal-well slab and the B42 dense-canopy fuzz both converge within the production 2 edge-check rounds); the faithful repro lived on the **post-edit** axis instead.

**Root Cause:**
Each chunk starts with `RemainingEdgeCheckRounds = 2`, both consumed during the initial generation wave. A later **border-column opacity edit** (dense-biome decoration VoxelMods use this exact path) whose column recalc re-spreads against stale cross-seam snapshots can leave a transparent border voxel 1–2 sky levels under the borderless oracle — a converged, **stable-but-dark** field with no pending work. Edge checks are the only corrector for under-bright border light (add-only, §3.6/§3.7), and after generation there was no round left to run one.
Classifier-proven: exactly one edge-check round over the settled field heals it (seed 14 diff: engine sky 12/11/11/12 vs oracle 13 at `(21,21..24,15)`, the column of companion edit
`place@(21,37,15)` under a z=16-seam overhang).

**Fix (July 2026):** the **re-arm** direction. `ChunkData.ModifyVoxel`, on an opacity-changing edit whose column is a chunk-border column (local x/z in {0,15}), tops `RemainingEdgeCheckRounds` back up to
`BORDER_EDIT_EDGE_CHECK_ROUNDS` (= 1). The existing stabilization machinery (`WorldJobManager.ProcessLightingJobs` re-arm + `TriggerNeighborEdgeChecks` + the `AreNeighborsReadyAndLit`
edge-check gate) then re-runs the reconciling border check on the settled field — no other engine change. Add-only and bounded by the counter, so it cannot livelock (B58/B59 stayed green); `RemainingEdgeCheckRounds`
is `[NonSerialized]` and already reset in `ChunkData.Reset()`, so no pool-reset or save-format work. The transparent-center pull-back direction (Bug 15's machinery) was **not** taken — re-lighting transparent centers from dimmer stale neighbors spreads over-bright ghosts (rejected, see #19).

**Rejected mid-fix (harness):** a per-completion re-arm in the frame simulator ran the edge check mid-churn, and the field settled back to its under-report after the round was spent. The reconciling round must read the **settled** field: production gets that from its neighbor-stability edge-gate plus the only-increase mod guard that protects a healed border, while the harness models it by consuming the re-granted round at grid quiescence (`RunReGrantedEdgeCheckRound`, consistent with how `RunInitialLighting*` already drive generation edge rounds as a
post-convergence loop). Exact per-frame edge-check scheduling in the harness remains AS-2.

**Validation suite:** K15a seed 14 red→green, promoted to baseline **B64** (25 seeds/suite run, 200 nightly) — one varied-heightmap-at-seam geometry axis now guards both Bug 15 (all seeds) and Bug 05 (seed 14). Prove-red:
neutering the re-grant re-reds seed 14 and only seed 14. All 56 baselines green.

---

### ~~21. Runaway lighting job loop → OOM after breaking a blocklight source near a chunk border~~

**Severity:** High (editor/game/OS crash via out-of-memory)  
**Fixed:** July 2026 (was Bug 16 in `LIGHTING_BUGS.md`; found, root-caused, fixed and confirmed 2026-07-11)  
**Status:** Resolved — confirmed in-game (20+ RGB blocklight place/break cycles in the same world and location where the OOM originally struck, no freeze/memory growth/`[LightingJob DIAG]` errors) and via the validation suite. Promoted from known-bug repro **K16a** to baseline **B87**, alongside the simple-form guard/over-correction tripwire **B86**
(`Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug16Runaway.cs`).

**Files:** `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` — `PropagateDarknessRGB` (removal-node enqueue, the fix site) + the `MAX_BFS_NODES_PER_PASS` fail-safe added during diagnosis (now permanent).

**Description:**
Breaking a blocklight source near a chunk border — most reliably while testing RGB blending (multiple different-colored lamps with overlapping cross-seam gradients, in enclosed spaces such as caves or underwater) — could freeze the lighting pipeline for the affected chunk (s) while memory grew monotonically until the editor/game/OS crashed from OOM. Observed ~5 times over ~2 months (since the RGB lighting overhaul), always in Mono editor play mode; the mechanism is backend-independent (plain `.Run()` reproduces), so IL2CPP was equally exposed.

**Root Cause (confirmed via harness repro + in-job near-cap node dumps):**
An **infinite per-channel removal cycle inside a single job's blocklight darkness phase**, from two interacting behaviors:

1. **Removal-node channel contamination** (`PropagateDarknessRGB`): when the darkness wave zeroed *any*
   channel of a neighbor, the re-enqueued removal node carried the neighbor's **full pre-zero RGB** — including channels that were *not* removed because they belong to a different, still-live lamp's gradient. Those contaminated values acted as removal thresholds downstream, turning one lamp's removal wave into a cross-gradient remover.
2. **Restore-to-constant mid-phase re-lights**: the darkness-phase seam pull-back (`CheckEdgeVoxelRGB`, the Bug 07 defect-2 fix) restores just-darkened border cells from cross-seam halo cells that are never themselves zeroed (they keep hitting the respread branch).

Per-channel alternation (zero V on R, re-light V, zero V on G, …) plus restore-to-constant defeated the BFS's per-channel strict-decrease termination guarantee. The near-cap dump captured the loop verbatim: the removal queue cycling the *same* stacked border cells with the *same* pre-zero values forever — `(15,65,15)
rgb=(3,2,0) → (15,66,15) rgb=(0,1,0) → (15,64,15) rgb=(2,3,0) → repeat` — at a four-chunk corner, in jobs emitting only 1–6 cross-chunk mods (pure in-chunk churn). The runaway grew the job-internal `Allocator.Temp`
BFS queues without bound inside one `Execute()` (Editor.log: Burst-job NREs at the native allocator,
`ALLOC_DEFAULT` 22.6 GB, repeated failed 1.7 GB `BlockDoublingLinearAllocator` doublings, fatal OOM — twice during bisection). A clean break cannot arm the cycle (monotone gradients — guarded green as B86); it needs the non-monotone mixed-channel plateau state built by *interrupted reconciliation* (edits landing while stale-snapshot jobs are in flight), matching the in-game rapid place/break trigger.

**Fix (July 2026):** two parts, both in `NeighborhoodLightingJob.cs`:

1. **Removal-node channel masking** (root cause): `PropagateDarknessRGB` masks each re-enqueued removal node to the channels this visit actually zeroed — a kept channel belongs to a different source's gradient. With the mask, any pull-back-refueled removal chain stays in one channel with strictly decreasing values, so the cycle is structurally impossible.
2. **Permanent fail-safe**: the bisection work cap (`MAX_BFS_NODES_PER_PASS`, 200k, shared across the seed + phase loops) stays: a runaway pass aborts loudly (console error + near-cap node dump; `IsStable` stays false so the chunk retries) — a future termination regression degrades to bounded, visible churn instead of an OOM crash. Documented in `LIGHTING_SYSTEM_OVERVIEW.md` §3.3.

**Spawned follow-up:** the same repro exposed an independent second defect — a small sourceless over-bright RGB island that survives because RGB blocklight lacked the sky channel's removal machinery — filed as **Bug 17** and fixed July 2026 (entry #22 below); B87's oracle assertion (which carried a dated exemption for exactly that residue class) has been restored to a plain oracle compare, and K17a promoted to **B88**.

**Validation suite:** K16a red→green on the fix (no fail-safe aborts + convergence + oracle up to the Bug 17 exemption at the time), promoted to **B87**; B86 and all other baselines stayed green throughout.

---

### ~~22. Sourceless RGB ghost-light island survives interrupted cross-seam removal (no RGB removal veto)~~

**Severity:** Low-Medium (visual artifact: a faint sourceless color tint — observed R ≤ 3/15 — near chunk seams after rapid lamp place/break; stable, but self-heals on any full relight, e.g. save/reload)  
**Fixed:** July 2026 (was Bug 17 in `LIGHTING_BUGS.md`; found 2026-07-11 as Bug 16's post-fix residue, fixed and oracle-confirmed 2026-07-12)  
**Status:** Resolved — confirmed via the validation suite (repro K17a flipped red→green, all baselines green); oracle-only confirmation (never knowingly observed in-game — in-game footprint would be a faint colored tint near a seam that disappears on reload, per the Bug 12 archive precedent, #16). Promoted from known-bug repro **K17a** to baseline **B88** (`Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug16Runaway.cs`, which shares the Bug 16 geometry + cycling recipe); B87's plain oracle compare restored in the same
change.

**Files:** `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` — `ComputeBlocklight` per-channel removal veto + `InChunkBlocklightSupport` / `CrossChunkBlocklightSupport`; `Assets/Scripts/WorldJobManager.cs` +
`Assets/Editor/Validation/Lighting/Framework/LightingTestWorld.cs` — the block-removal support wiring in the cross-chunk apply path (`ApplyCrossChunkLightMod` / `ApplyModToChunk`) + cached `_blockEmission` lookup.

**Description:**
The Bug 16 interrupted-cycling recipe (rapid lamp place/break under load, with stale-snapshot jobs in flight)
left a small **orphaned over-bright RGB island** near a chunk seam: light whose source was gone, disconnected from any live gradient by zero-valued cells (K17a: ~24 voxels, R 1–3, straddling the z31|32 seam), which nothing ever corrected. RGB blocklight had **none** of the sky channel's cross-chunk removal machinery.

**Root Cause (confirmed via instrumented attribution):** the planter is a **stale in-flight lighting job**
re-instating pre-break red — both through its full-`LightData` center merge and through the cross-chunk RGB **uplift/placement mods** it emitted from a pre-break snapshot — which ordinary `PropagateLightRGB` re-spread then fanned outward. It survived because the RGB cross-chunk **removal** path had no independent-support veto:
without it, a darkness-wave removal mod computed against a stale snapshot **over-cleared** channels that a live independent source still backed, and the receiver re-lit them — the same stale-snapshot cross-seam removal/re-light **oscillation** the sky channel's Bug 11/13 veto exists to break. The darkness-phase
`CheckEdgeVoxelRGB` pull-back (the other candidate planter) was ruled out: cycle 0 of the recipe self-heals despite it, and neutering the RGB claim path changed nothing.

**Fix (July 2026):** mirror the sky **Bug 11/13 independent-support veto** to RGB per channel. A cross-chunk RGB *removal* mod now consults the strongest attenuated blocklight an independent in-chunk neighbor (`InChunkBlocklightSupport`) or a live third-party cross-chunk neighbor in a chunk other than the emitter (`CrossChunkBlocklightSupport`) still supplies; `ComputeBlocklight` keeps a channel a live source still backs instead of clearing it to 0. This breaks the oscillation, so the removal completes and the field converges to the borderless oracle
with no orphan. (An opaque neighbor contributes only its own emission, mirroring
`PropagateLightRGB`'s opaque-source arm.) Only the veto was needed; the Bug 12 initiator and Bug 14 claim-verification analogs were **not** required for the observed class and were deliberately not added (they remain sky-only). Documented in `LIGHTING_SYSTEM_OVERVIEW.md` §2.1 / §3.7.

**Validation suite:** K17a red→green on the fix, promoted to **B88**; prove-red confirmed (neutering the veto re-plants the 24-voxel ghost, and only B88 goes red); B86 (over-correction tripwire), B50, and the Bug 07/09 family stayed green. 80 baselines total.

---

### ~~23. Sourceless RGB blocklight loop survives when equal-color seam sources are removed in the same wave~~

**Severity:** Medium (visual artifact: a stable, sourceless over-bright colored residue straddling a chunk seam after equal-color lamps are removed together; self-heals on any full relight — the RGB mirror of Bug 12's sky loop)  
**Fixed:** July 2026 (was Bug 18 in `LIGHTING_BUGS.md`; predicted by the post-Bug-17 sky↔RGB parity audit
[fidelity finding C10], then reproduced deterministically, fixed, and oracle-confirmed 2026-07-12)  
**Status:** Resolved — confirmed via the validation suite (repro K18a flipped red→green, all baselines green, prove-red confirms only the guard reds when the initiator is neutered). Oracle-only confirmation (the borderless
`LightingOracle` is trusted as ground truth; never knowingly observed in-game, per the Bug 12/17 colored-residue precedent). Promoted from known-bug repro **K18a** to baseline **B90**
(`Assets/Editor/Validation/Lighting/LightingValidationSuite.C10RgbLoop.cs`).

**Files:** `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` — new `EmitCrossChunkBlocklightRemoval` + its call from `PropagateDarknessRGB` (the 2-cycle-signature emit) + the `emittedBlockRemovals` dedup set threaded through `PropagateDarkness`.

**Description:**
When two **equal-color** blocklight sources on opposite sides of a chunk seam mutually lit the two shared border columns and both were removed in the same lighting wave (each chunk's schedule-time snapshot still showing the *other* side lit), the seam did **not** darken. It dropped by exactly one level and then **locked** as a stable-but-wrong over-bright residue, sustained purely by the two seam voxels mutually supporting each other across the boundary. The RGB (blocklight) twin of **Bug 12** (the sky sourceless cross-seam loop) and the same over-bright
class as **Bug 17**, but with a distinct root cause — a missing removal *initiator*, not a missing *veto*.

**Root Cause (confirmed via harness repro + per-voxel attribution):**
The sky↔RGB removal-machinery parity gap after Bug 17. Sky light has a cross-seam removal **initiator**
(`EmitCrossChunkSunlightRemoval`, the Bug 12 fix): when a darkness wave meets a cross-chunk neighbor at *exactly* the removed level (the 2-cycle signature) and the neighbor is not independently supported, it emits a removal mod that starts collapse from the other side. RGB blocklight had **no counterpart** —
`EmitCrossChunkSunlightRemoval` hard-codes `Channel = Sun`, and `PropagateDarknessRGB` never emitted a cross-seam removal initiator. So the symmetric mutually-equal seam had no node to begin removal from (each side re-lit from the other's stale snapshot), and the Bug 17 independent-support **veto**
(`CrossChunkLightModApplier.ComputeBlocklight`), added to *protect* legitimately-supported channels, actively **protected the stale mutual support** — the over-protection tension Bug 13 resolved on the sky side. Fidelity finding **C12** (the RGB darkness-phase pull-back's lack of claim verification) was proven a non-cause: an asymmetric held-flight probe self-heals (baseline B89), matching the Bug 17 note that "neutering the RGB claim path changed nothing".

**Fix (July 2026):** mirror the Bug 12 cross-seam removal **initiator** to RGB per channel. When a blocklight darkness wave meets a cross-seam neighbor with a channel at exactly the removed level (`nX == node.LightX`) on a non-opaque neighbor, `PropagateDarknessRGB` emits a cross-chunk blocklight removal mod (`EmitCrossChunkBlocklightRemoval`, zeroed channels + `IsRemoval`, deduped per neighbor). The receiving chunk adjudicates it per channel through the **existing** Bug 17 veto: a channel a live independent source still backs is kept, the stale loop
clears. No apply-side change was needed (that infra shipped with Bug 17). The Bug 14 claim-verification analog remained unnecessary and was not added (C12 self-heal, B89) — mirroring Bug 17 scoping itself to the veto only. Documented in `LIGHTING_SYSTEM_OVERVIEW.md` §3.7. Bug 16's removal-node channel masking and the permanent `MAX_BFS_NODES_PER_PASS` fail-safe are preserved.

**Validation suite:** K18a red→green on the fix, promoted to **B90**; prove-red confirmed (neutering the emit reds only B90 — the ~38-voxel over-bright red residue returns, worst R13 at the seam); B86–B88 (Bug 16/17 over-correction tripwires), the Bug 12 sky family B50–B53, and B89 (C12 self-heal) all stayed green. 82 baselines total.

---

### ~~24. Far-Lands sunlight column recalc crash — negative heightmap index beyond ±2²⁴~~

**Severity:** Low (only reachable past the noise-degradation threshold)  
**Files:** `WorldData.cs`, `WorldJobManager.cs`, `Helpers/SunlightColumnRouting.cs` (new), `ChunkCoord.cs`, `World.cs`  
**Reported:** July 2026 (logged 2026-07-18 during CMD-2 `/teleport` far verification, ±2×10⁷)  
**Fixed:** July 2026 (2026-07-19, in-game confirmed same day — including at the ±2³¹ edge)

**Symptom:** Teleporting beyond ±2²⁴ voxels produced repeated Burst `IndexOutOfRangeException`s from
`NeighborhoodLightingJob.RecalculateSunlightForColumn` (heightmap index `−16`/`−32`) while far chunks lit, plus
`[LIGHTING] Merging ... faulted` errors from `ApplyCrossChunkLightMod` (out-of-range locals). HF-2 fault isolation contained it (no cascade), but the far chunks' sunlight was wrong/incomplete.

**Root Cause:** `WorldData.QueueSunlightRecalculation` derived its queue key via
`GetChunkCoordFor(new Vector3(columnPos.x, 0, columnPos.y))` — an int→float round-trip that loses integer precision past ±2²⁴ (float granularity 2 at 2×10⁷). Border columns rounded across the chunk boundary and landed in the **adjacent chunk's** queue bucket; the job-build drain then subtracted the *wrong* chunk's origin in exact int math, producing chunk-local columns like `z = −1/−2` → heightmap index `−16`/`−32`. The same implicit
`Vector3Int`→`Vector3` conversion fed the float query APIs at 11 more sites (`ApplyCrossChunkLightMod` and siblings), all silently wrong-chunking past ±2²⁴.

**Fix:** Integer column math end-to-end. New shared seam unit `Helpers.SunlightColumnRouting` (pure `ChunkMath`
shift math, exact to ±2³¹) used by `QueueSunlightRecalculation`, the `WorldJobManager` job-build drain, and orphan-column persistence; integer `Vector3Int` overloads on `WorldData.GetChunkCoordFor` /
`GetLocalVoxelPositionInChunk` / `EnsureChunkExists` and `ChunkCoord.FromVoxelPosition` — overload resolution auto-captures every integer call site (exhaustiveness proven with a temporary `[Obsolete(error)]` poison build:
only genuine-float callers remain) — plus a latched dev-build ±2²⁴ tripwire on the float query paths so a future integer-valued-but-float-typed caller fails loudly. The absolute **±2³¹ edge** arithmetic-wraparound class is documented-only by decision (see `WORLD_SCALING_FLOATING_ORIGIN.md` §9).

**Validation suite:** `LightingTestWorld` gained far-anchor support (`anchorChunk` ctor param, integer-exact harness plumbing) and `QueueFullSunlightRecalcViaGlobalRouting`, which crosses the production routing seam — closing fidelity gap **A6** (the harness's local-column seeding had assumed the production round-trip was
"semantically identical"; it was not). Baselines **B95** (routing integrity at identity / ±2²⁴-boundary / ±2×10⁷ anchors; prove-red: 95 lost + 184 out-of-range columns per far anchor pre-fix) and **B96** (far-anchor differential twin — bit-identical field at +2×10⁷ vs identity). Lighting suite 86→88, Validate All 279/279.

---

### ~~25. Sealed partial-block light shaft leaves its sky column permanently lit~~

**Severity:** Medium  
**Files:** `Jobs/BurstData/LightAttenuation.cs`, `Data/ChunkData.cs`, `Data/IBlockObstruction.cs`, `Data/JobData.cs`, `Jobs/StandardChunkGenerationJob.cs`, `Legacy/LegacyChunkGenerationJob.cs`, plus the harness heightmap builder, the oracle column scan, and `ChunkPreview3DWindow`  
**Reported:** August 2026 (2026-08-08, found in the harness while authoring baseline B105 for `VO-4`)  
**Fixed:** August 2026 (2026-08-08)

**Symptom:** A vertical half slab admits an undimmed sky column through its open half (`VO-3`, baseline
B104). Sealing that shaft did not darken the column beneath it — it stayed at 15 forever, stable, 4 levels
above the borderless oracle (550 divergent voxels in a single-chunk room, converged in 2 frames).

**Reachability (why this was latent, and why it still had to be fixed):** the stuck column needs the shaft
sealed *without an opacity change* — in-place rotation, or a same-opacity direct overwrite. Neither is
player-reachable today: a normal seal is break → place (opacity 15→0→15), which changed opacity at both
steps and always worked. So that half was reproducible only in the harness and goes live the day in-place
rotation ships. The **wrong heightmap** underneath it was not latent: a column topped by a vertical slab
registered that slab as its highest light-obstructing block with no edit involved at all.

**Root Cause (classified by differential controls, not inferred):** two independent parts, and fixing only
the first left the bug fully intact.

1. `IsLightObstructing` is `Opacity > 0`, and a half slab is authored `opacity = 15`, so the slab already
   sat in the heightmap; sealing it never moved the heightmap, so `RecalculateSunlightForColumn` — the sole
   authority for sky-light *removal* — never re-ran. `PropagateDarkness` could not finish the job either: it
   unwinds light by following exact `neighbor == old − cost` chains, which a flat 15 column does not have.
2. `ChunkData.ModifyVoxel` queued the column recalculation only when `newProps.opacity != oldProps.opacity`.
   Sealing a slab with another opacity-15 block — or merely rotating it, which changes no block id at all —
   passes that test unchanged, so nothing recomputed even once the heightmap was correct.

The controls pin part 1 to partial blocks specifically: a **Glass** shaft (full cube, opacity 0, carries an
equally undimmed 15 column but is *not* light-obstructing, so its heightmap moved) always darkened
correctly, as did an attenuating **Water** shaft. So it was neither "undimmed columns cannot be removed" nor
"opaque shafts cannot be removed", but precisely *light-obstructing by opacity while transmitting by shape*.

`VO-3` had deliberately left `IsLightObstructing` whole-block, reasoning that "the BFS carries the undimmed
column down anyway, so the field is correct; the heightmap merely stays conservative". True for **placement**,
false for **removal** — a conservative heightmap also means that column's removal authority never fires.

**Fix:** new `LightAttenuation.ObstructsSkyColumn(block, meta)` = `EntryOpacity(top) > 0 || ExitBlocked(bottom)`
replaces `IsLightObstructing` at every heightmap site (the column enters a cell through its top face and
leaves through its bottom, so both must be tested), and `ModifyVoxel`'s recalculation trigger now also fires
when a cell's sky-column obstruction changes — strictly additive, so every edit that queued a recalculation
before still does. Bit-identical for full cubes: `EntryOpacity` short-circuits on `HasCustomBounds` and
`ExitBlocked` can never fire, so no full-cube world's heightmap changes by a single entry. `IsLightObstructing`
is kept as a legitimate whole-block "has any opacity" test, with a docstring warning against reusing it for a
heightmap. **This is the same defect class as archived Lighting #04** — an opacity-valued predicate standing
in for the column question in the same function.

**Serialization:** `heightMap` is persisted, but only its values change and only for columns containing
vertical slabs; layout untouched, so no migration or version bump. A stale persisted heightmap self-heals on
the next edit in that column.

**Validation suite:** repro `K21a` promoted to baseline **B107**, which asserts five legs against the
borderless oracle — Glass and Water shaft controls, slab sealed with an opaque cube, slab sealed **by
rotation alone** (the case an opacity-valued trigger cannot see), and the reverse direction: standing a flat
slab upright re-lights the column (11 → 15), guarding against "fix this by making every partial block
obstruct", which would trade a stuck-lit column for a stuck-dark one. Validate All 423/423.
**B107 is the only thing that can catch a regression here until in-place rotation ships — do not remove it
as "untested in practice".** In-game pass 2026-08-08 confirmed no regression around vertical or horizontal
slabs; it could not confirm the fix itself, because the defect's live trigger does not exist yet.

---

## Meshing

### ~~M02. A custom mesh's mid-plane face is culled by the block-boundary neighbor~~

**Severity:** Medium  
**Reported:** August 2026  
**Fixed:** August 2026  
**Status:** Resolved — confirmed in game 2026-08-08. Guarded by baseline **B48** in `MeshingValidationSuite.SubBlockCulling.cs`.  
**Found:** 2026-08-08, while verifying VO-6 (predicted from reading the culling path, then confirmed in
the harness). Confirmed visible in game by the owner the same day — a player-facing hole, not a
harness-only artifact.

**Description:**
Both custom-mesh paths decided face visibility with
`ShouldDrawFace(voxelProps, GetVoxelStateFromLocalPos(pos + rotatedOffset))` — the block-boundary
neighbor — regardless of where the face actually sits inside the cell. That is right for a boundary
face and wrong for an interior one: a half slab's mid-plane face is half a block *inside* its own
cell, with the cell's open half between it and that neighbor, so a full block beyond the gap cannot
occlude it.

Confirmed numerically: a slab at `Facing6Roll2` meta `0x03` emits its mid-plane face at `z = 8.50`
when isolated, and **emitted nothing** once a solid block was placed at `(8, 8, 7)` — a full cell away.
The surface is genuinely visible through the slab's open half from a grazing angle, so this rendered
as a hole rather than a hidden face.

This was the culling twin of [Bug M01](#m01-sub-block-custom-mesh-faces-sample-smooth-light-from-the-wrong-cell)
(the same confusion in the *lighting* sample, fixed by VO-6) and it survived that fix deliberately:
VO-6 moved only the light sample, because changing culling changes emitted vertex counts.

**Root cause and fix:** `MeshGenerationJob.ResolveFaceSampleCell` — added by VO-6 — already returns the
cell a face actually looks into. Both custom-mesh paths now resolve it *before* the visibility test and
cull against that cell instead of `pos + rotatedOffset`.

**The interior-face guard is load-bearing.** Asking the resolved cell directly would ask an interior
face's *own* cell whether it occludes — i.e. ask the block about itself. The rule that makes it correct
is structural: one block per cell, so nothing but this block can occupy the open half in front of an
interior face, and such a face can never be occluded. It survived without the guard only by accident,
because `Stone Half Slab` is authored `renderNeighborFaces: 1`; a custom mesh authored without that flag
would have lost every mid-plane face. This is the same class of error as M03 below — geometry inferred
from a cell index, in a world where VO-6 made face positions stop coinciding with the grid.

**Why no existing baseline moved.** The fix changes emitted vertex counts (B48's own fixtures go 5 quads
to 6), and this entry was filed rather than fixed on exactly that basis. In the event, all 429 prior
baselines stayed green — because none of them placed a solid block against a slab's mid-plane face. The
sweep was clean for want of coverage, not for want of blast radius, which is the same suite gap shape
that let M03 ship. B48 is what closes it.

**Reproduction Steps:**
Place a `Stone Half Slab` rotated to vertical against a solid wall so its large face points at the
wall, then look along the wall. The slab's large face was missing.

**Repro scenario:** `B48` (promoted from `KM02`) — five legs, covering both signs of the mid-plane
normal, the counter-assertion that boundary faces still cull, and the fully walled-in configuration in
both orientations.

**Testing environment:** Editor, August 2026.

---

### ~~M03. A recessed partial block's mid-plane face renders black (it occludes itself)~~

**Severity:** High — plainly visible, and a regression against pre-VO-6 rendering.  
**Reported:** August 2026  
**Fixed:** August 2026  
**Status:** Resolved — confirmed in game 2026-08-08. Guarded by baseline **B47** in `MeshingValidationSuite.CornerOcclusion.cs`. **Introduced by VO-6** (commit `6fbcebcc`) and fixed before that arc shipped further.  
**Found:** 2026-08-08, owner's in-game visual review of the VO-* arc.

**Description:**
A half slab embedded in a floor — its mid-plane top face recessed below the surrounding surface —
renders **fully black** under smooth lighting. With smooth lighting off it renders normally, so this is
purely the ambient-occlusion term.

Reproduced in the harness under a **uniform light field of sky 15**, which rules out light propagation
as a contributor:

| Configuration                 | Slab's mid-plane top face |
|-------------------------------|---------------------------|
| slab isolated                 | `191,191,191,191`         |
| **slab embedded in a floor**  | **`0,0,0,0`**             |

**Root cause — one error, two symptoms.** VO-6 correctly made a mid-plane face take its light from the
cell the face looks into, which for such a face is the block's **own** cell. VO-8 then selects, per
corner, the octant of the sampled cell that touches the corner's vertex. But the corner vertex used is
derived from the *re-based* `blockPos`, so it lands on the cell boundary — half a block below the face's
actual plane. The octant chosen is therefore the half of the cell **behind** the surface:

1. **The block occludes its own face.** The isolated reading proves it arithmetically:
   `191 = (0 + 15 + 15 + 15) / 4 × 17`. Three ring samples contribute full light; the direct sample —
   the block itself — contributes zero. The slab's solid half sits in the selected octant, so it answers
   "yes, I block this corner" about its own surface.
2. **The ring is evaluated at the wrong height.** With the octant taken below the face plane, the ring
   neighbours are sampled through the floor's solid material rather than through the open space the face
   actually faces. Embedded, all three ring samples read as fully covered — combined with (1), all four
   samples are zero and the face is black.

**This predates VO-8.** Under VO-5's per-face rule the direct coverage was `cov(−Y)` = 1 for a bottom
slab, giving the same zero. VO-8 changed *which* octant is asked, not the fact that the block is asked
about itself. The defect arrived with VO-6's sample-cell move.

**Why the suite did not catch it:** `B44`/`B45` exercise an **isolated** slab, the one configuration
where self-occlusion is survivable (191 rather than 0). No baseline covers a recessed or embedded
partial block — the ordinary real-world case.

**Fix (August 2026):** the octant's **normal axis** is now resolved from the face itself rather than from
the corner vertex. `CalculateCornerLights` takes a `faceIsInteriorToSampleCell` flag — true exactly when
VO-6 resolved the sample cell to the emitting block's own cell — and picks the half the normal points
*into* for an interior face, the half it points *away from* for a boundary face. The perpendicular axes
still come from the corner vertex, so boundary faces are untouched. One change, both symptoms:

| Configuration | Before | After |
|---|---|---|
| slab isolated  | `191` | `255` — nothing occludes it, so nothing should darken it |
| slab embedded  | `0`   | `64`  |

**`64` is the right answer, not a tuned one:** an equivalent 1×1 pit floor built from full blocks measures
`64,64,64,64` too. The recessed slab is no longer a special case — it shades exactly like any other
one-block recess.

**Reproduction Steps:**
Place a `Stone Half Slab` flat in a floor so its top face is recessed half a block, with open sky above.
Enable smooth lighting.

**Repro scenario:** `KM03a` (meshing suite) — landed red, now green. Asserts both the symptom (the face is
not fully black under a uniform sky-15 field) and the mechanism (brightening the slab's own cell must move
that face — under the bug its contribution was multiplied by zero). Uses the opacity-15
`HalfSlab` fixture deliberately: the AO query is gated on `IsOpaque`, so a sub-15 slab never
self-occludes and would not reproduce this.

**Testing environment:** Editor, smooth lighting High, August 2026.

---

---

### ~~M01. Sub-block custom-mesh faces sample smooth light from the wrong cell~~

**Severity:** Medium  
**Reported:** August 2026  
**Fixed:** August 2026  
**Status:** Resolved — confirmed in game 2026-08-08. Guarded by baselines **B44** (rotated, `−Z` mid-plane face) and **B45** (unrotated, `+Y`) in `MeshingValidationSuite.SubBlockFaceLight.cs`.  
**Related:** [`LIGHTING_BUGS.md`](./LIGHTING_BUGS.md) Bug 20 (the lighting-model half of the same visual artifact, fixed first by VO-3/VO-4)

**Description:**
`MeshGenerationJob.GenerateCustomBlockMesh_SchemaAware` computed one smooth-light corner quad per emitted face via
`CalculateCornerLights(worldFace, pos, …)`. That quad was defined entirely by the block cell `pos` and the world
direction `rotatedOffset`: it read the direct light at `pos + rotatedOffset` and the eight-cell ambient-occlusion ring
around **that** cell.

That is correct only for a face lying on the block boundary. A custom mesh's *interior* faces do not — a half slab's
large face sits at the mesh's mid-plane, half a block inside its own cell. For such a face the direct light should come
from the cell in front of the surface (the block's own cell, whose remaining half is open) and the AO ring should be
centred on that same cell; both were taken a full block further away.

The per-vertex bilinear blend *within* the face was never at fault — `GetCornerUV` was verified against the
corner-offset LUT that `BurstVoxelData.BuildCornerOffsetLUT` generates, corner by corner, for all six faces, and the
documented `l0↔(0,0), l1↔(0,1), l2↔(1,0), l3↔(1,1)` mapping holds exactly. The defect was the *source quad*.

**Why rotation made it visible.** Unrotated, a bottom slab's mid-plane face points +Y and the cell above it usually
carries light similar to the slab's own cell, so the error was nearly invisible. Rotated to vertical (`Facing6Roll2`
facing 3, rolls 0–3), the mid-plane face points at a *horizontal* neighbour whose light and occlusion ring have nothing
to do with what is in front of the surface — and each roll aims it at a different neighbour.

**Root cause / fix ([`VOXEL_OCCLUSION_REFACTOR.md`](../Design/VOXEL_OCCLUSION_REFACTOR.md) VO-6):**
`CustomFaceData.Centroid` (the face's mean vertex position, precomputed at load in `JobDataManagerFactory` and mirrored
in `TestCustomMeshLibrary`) plus `MeshGenerationJob.ResolveFaceSampleCell`, which steps a short distance off the
rotated centroid along the face normal and takes the cell that lands in. Applied to the schema-aware path, the legacy
path, and the flat-light path. The pre-fetched `directNeighbor` moved with the ring, so the direct term and the ambient
ring cannot end up sampling different cells.

**⚠️ The step size is load-bearing, and the original plan got it wrong.** The plan specified
`floor(pos + rotatedFaceCentroid + 0.5 · rotatedNormal)`. A *half*-cell step puts a mid-plane face at exactly
`0.5 ± 0.5` — a cell boundary — where `floor` breaks the tie toward the own cell for a negative normal and toward the
neighbour for a positive one. The shipped value is `0.25`; any step strictly inside `(0, 0.5)` works. **Do not
"simplify" it back to half a cell.**

**This is why there are two baselines.** `B44` (promoted from `KM01a`) uses meta `0x03`, a negative-normal case, so it
flips green even on the broken half-cell formula. Verified by mutation: with the half-cell step, **B44 passes and B45
fails**. A fix validated on the original single repro would have shipped working rotated slabs and untouched unrotated
ones. Generalizable: *a single repro of a sign-symmetric defect only ever tests one sign.*

**Test-design lesson — a positive control must not be satisfiable by the behaviour under test.** `KM01a`'s original leg
C asserted "brightening the block-boundary neighbour must move the mid-plane face", documented as proof the probe
worked. That coupling *was* the defect: once the sampling cell became correct the boundary neighbour rightly stopped
affecting the face, so the control failed exactly when the fix succeeded and briefly masked the flip. The promoted
baselines control on "raising the whole light field moves the face" instead, and the old control became a real
assertion (the boundary neighbour must *not* reach a mid-plane face).

**Documentation conflict, resolved:** `Architecture/SMOOTH_AND_RGB_LIGHTING.md` §2.5.2 claimed this path *"correctly
handles sub-block geometry (half slabs, fences, stairs)"*. True of the bilinear blend, false of the sampled quad; §2.5.2
now documents the sampling rule and the step-size constraint.

**Still open:** [`MESHING_BUGS.md`](./MESHING_BUGS.md) **Bug M02** — the same wrong-cell confusion in the *culling*
decision, which VO-6 deliberately did not touch because it changes emitted vertex counts.

**Testing environment:** Editor, smooth lighting enabled, August 2026.

---

## Fluid

### ~~01. Cross-chunk fluid simulation stops at chunk borders~~

**Severity:** Bug  
**Files:** `Chunk.cs` — `Reset`, `World.cs` — `ApplyModifications`  
**Fixed:** March 2026

**Symptom:** Water would flow one block into a neighboring chunk and then stop, leaving a dead-end fluid tile that did not continue spreading.

**Root Cause:** During the initial world load (`StartWorld`), `ChunkData` for a chunk could be fully populated before the corresponding `Chunk` GameObject was retrieved from the pool. `Chunk.Reset()` did not call `OnDataPopulated()` when the data was already populated, so active voxels (e.g., water source blocks) placed during generation were never registered in `_activeVoxels`, and fluid simulation never started for them.

**Fix:**

- Added a check in `Chunk.Reset()`: if `ChunkData.IsPopulated` is already true when the chunk is reset, `OnDataPopulated()` is called immediately.
- The noisy `[FLUID DEBUG]` warning in `World.ApplyModifications()` is now gated on `_isWorldLoaded` to suppress expected noise during the initial load phase.

---

### ~~02. Downward flow creates infinite source blocks (waterfalls don't drain)~~

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`, `Active`; `FluidDataGenerator.cs`; `FluidMeshData.cs`  
**Fixed:** March 2026

**Symptom:** Placing a single water source block at height Y and then removing it left the entire waterfall column permanently, because every block in the column had become an independent infinite source. Additionally, even after an initial fix (FluidLevel=1), falling water spread ~60 blocks on landing because level 1 still allowed 6 levels of horizontal spread.

**Root Cause:** `HandleFluidFlow` Rule 1 created downward flow without properly encoding the upstream level. In Minecraft Beta 1.3.2, falling fluid uses metadata `>= 8` (the "falling flag"), with the lower 3 bits carrying the effective upstream level. When a falling block lands, horizontal spread starts from `effectiveLevel + 1`, not from level 1.

**Fix (Minecraft-style falling metadata):**

1. **Falling flag encoding:** Added `FALLING_FLAG = 8`, `IsFalling()`, `GetEffectiveLevel()`, `MakeFalling()` helpers to `BlockBehavior.cs`.
2. **Step A (Gravity):** Downward flow now places `MakeFalling(effectiveLevel)` — e.g., a source (level 0) falling becomes FluidLevel 8, a level-2 flow falling becomes FluidLevel 10.
3. **Step B (Settle):** A falling block that lands on solid ground converts to its non-falling effective level before spreading horizontally.
4. **Step C (Horizontal spread):** Spread starts from `effectiveLevel + 1`, not from the raw FluidLevel.
5. **`Active()` updated** with 6 reasons including falling-aware drain checks.
6. **`FluidDataGenerator.cs`:** Template indices 8–15 now generate as `1.0f` (full block height for falling columns). Added validation that `flowLevels <= 8`.

---

### ~~03. Side face rendering between different fluid levels (Internal Water Grids)~~

**Severity:** Visual Artifact / Performance  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs` — fluid face cull logic)  
**Fixed:** March 2026

**Symptom:** Side faces between fluid voxels of different fluid levels (or fully submerged voxels) were incorrectly rendering full geometric quads from `y=0` to the surface height, causing massive internal overdraw and visible overlapping grid seams inside bodies of water.  
**Root Cause:** The mesher conservatively rendered faces between differing fluid levels to prevent gaps near waterfalls, and failed to account for fully submerged neighbor blocks sharing a `1.0f` ceiling.  
**Fix / Implementation:** We eliminated 100% of internal horizontal fluid faces and perfectly sealed vertical gaps using a three-part logic upgrade:

1. **Mathematical Symmetry:** Exploited `GetSmoothedCornerHeight` to pre-calculate the exact surface heights of shared neighbor edges. If a fluid block is adjacent to another, it now draws a "curtain" face extending *only* from the neighbor's sloped surface height up to our surface height, erasing geometry that would have plunged internally into the neighbor's volume.
2. **Submerged Culling:** Expanded the Burst neighbor lookup to include `above_N, above_S, etc.`. If both the current block and the neighbor are submerged (`neighborHasFluidAbove`), their top edges mathematically align at `1.0f`. There is zero gap to fill, so the face is completely culled.
3. **Waterfall Preservation:** Added an `isFullHeight` fallback. Waterfalls erupting from under solid stone ceilings (`hasFluidAbove == false` but `template == 1.0f`) now correctly recognize their full height, overriding the shallow puddle cull to draw a perfect gap-filler face down to the surface below.

---

### ~~05. 7x7 Horizontal Spreading Cube in Mid-Air~~

**Severity:** Gameplay / Physics bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** When a source block is placed on top of an elevated surface (like a tree), the fluid flows outwards and sometimes incorrectly spawns horizontal spreading blocks in mid-air (forming a floating 7x7 water grid) instead of accurately checking if those spread locations have ground support below them.

**Root Cause:** The `isSupportedBelow` check during horizontal spreading incorrectly allowed fluid to spread if the block below was the *same fluid type*, regardless of whether it was falling or solid. Additionally, falling waterfall columns and pulsating decay loops contributed to broken spread distances.

**Fix:**

1. Aligned horizontal spread gating with Minecraft logic (fluids only spread horizontally if supported by a solid block, preventing mid-air grids).
2. Resolved infinite decay loops by treating falling blocks as level 0 when calculating expected support levels.
3. Added a `waterfallsMaxSpread` configuration toggle in `BlockType.cs` to let the user select between Minecraft parity (infinite waterfall spread) and physics-based volume conservation.

---

### ~~07. Missing Source Block Regeneration (Infinite Water)~~

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`  
**Fixed:** March 2026

**Symptom:** In Minecraft Beta 1.3.2, two or more adjacent water source blocks resting on solid ground spontaneously regenerate an empty air block between them into a new source block. This core "infinite water" mechanic was missing from the engine.

**Root Cause:** The `BlockBehavior.Fluids.cs` decay pass successfully gathered environmental constraints, but had no logic to construct new infinite sources out of thin air, meaning water was strictly finite.

**Fix:**

1. Added an `infiniteSourceRegeneration = false` flag to `BlockType.cs` to allow toggling this for specific fluids (e.g., Water = true, Lava = false).
2. Integrated the horizontal source-counting logic directly into `CalculateExpectedFluidLevel` when looking for cross-chunk neighbors.
3. If `>= 2` native source blocks (level 0) are horizontally adjacent, and the block below is either solid ground or another level 0 source block, the block organically overrides its target decay to `0`, successfully generating an infinite source pool without extra overhead or allocations.

---

### ~~08. Missing Lava Viscosity Randomization~~

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`  
**Fixed:** March 2026

**Symptom:** Lava spread horizontally at the exact same deterministic rate as water, lacking its thick, random flow pattern.

**Root Cause:** Fluid flow evaluation always proceeded at 100% chance for all fluids.

**Fix:** Added a `spreadChance` configuration `float` (0.0 - 1.0) to `BlockType.cs` and exposed it in the `BlockEditorWindow`. Inside `HandleFluidSpread`, `UnityEngine.Random.value` is rolled against this chance; if it fails, the horizontal spread step is cleanly aborted for that tick.

---

### ~~10. Missing Dynamic Flow Direction Texturing~~

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`  
**Fixed:** March 2026

**Symptom:** Water did not visually animate in the direction it was physically spreading, relying on a static world-space liquid shader. **Fix / Implementation:** Implemented flow derivative math in `VoxelMeshHelper.cs` (`CalculateCornerFlow` and `ProjectFlowToSideFace`). This calculates a 2D XZ flow vector based on surrounding fluid height differentials and maps it to the UV channels of the generated mesh. The `UberLiquidShader` receives this vector (`localFlowVector`) and uses a dual-phase crossfading technique to scroll the noise and simulate
continuous directional flow. *(Note: As tracked in FLUID_BUGS.md #16, this is currently functionally operational but the math could be further refined in the future to eliminate surface stretching.)*

---

### ~~11. Missing Unique Textures for Falling Fluids (Waterfalls)~~

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`  
**Fixed:** March 2026

**Symptom:** Falling fluid blocks (waterfalls) used the exact same visual properties as resting horizontal fluids. **Fix / Implementation:** In `VoxelMeshHelper.cs`, the mesh generation job now explicitly checks if the voxel has the falling flag (`fluidLevel >= 8`). If true, instead of calculating complex horizontal flow math, it bypasses the evaluation and forces a strict, high-velocity downward flow vector (`new Vector2(0f, 1.5f)`). The `UberLiquidShader` natively interprets this large V-axis vector, resulting in a fast-scrolling, distinct vertical
waterfall effect that properly blends with horizontal pools at its base.

---

### ~~06. Missing Optimal Flow Direction Pathfinding~~

**Severity:** Missing Feature  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Water spread outward in a uniform diamond shape, oblivious to nearby holes or drops. **Root Cause:** The simulation lacked Minecraft's recursive `calculateFlowCost` terrain scanner. **Fix:** Injected a dot-net 2.1 zero-allocation Breadth-First-Search iterative pathfinder using Unity's `NativeQueue` and bitmasks to determine the optimal downhill path.

---

### ~~09. Severed waterfalls cause infinite decay loops~~

**Severity:** Bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Breaking a source block with a waterfall beneath it left floating, non-decaying waterfall columns that indefinitely supplied water to adjacent blocks. **Root Cause:** `CalculateExpectedFluidLevel` allowed orphaned waterfall blocks to act as level-0 support for each other, establishing a self-sustaining loop. **Fix:** Added an `isFedFromAbove` check. Now, if a falling fluid block is cut off from the stream above, it immediately decays to air or regular decaying fluid, ending the loop.

### ~~18. Fluids flow into unpopulated placeholder chunks → chunk-aligned "flowing water" seams on ocean surfaces~~

**Severity:** High (visible world corruption, persisted to disk)  
**Files:** `Jobs/FluidBurstTicker.cs` (`PrepareNeighbors`), `Data/WorldData.cs` (`TryGetVoxel`), `Data/ChunkData.cs` (`FillJobVoxelMap`, `GetVoxel`), `World.cs` (`ApplyModifications`), `WorldJobManager.cs` (pending-mod replay)  
**Fixed:** July 2026  
**Status:** Resolved — confirmed in-game (new chunks no longer ring); guarded by baselines **BH-B8** (Burst path) and **BH-B9** (managed path) in `Validate Behavior`, promoted from this bug's own `K18a`/`K18b` repros (unrelated to the Lighting suite's identically-named `K18a`).

**Symptom:** Straight, chunk-boundary-aligned lines of *flowing* water (fluid level 1) on otherwise flat ocean surfaces, tracing rectangular outlines that follow chunk seams. Produced off-screen in the load-distance buffer during chunk streaming — most reliably after load → quick unload → reload churn — and surviving a reload of the area because they were written into `pending_mods.bin`.

**Root Cause:** A chunk present in `WorldData.Chunks` but **not yet populated** — the placeholder `GetOrCreatePlaceholder` reserves for every load-distance coord before its terrain job lands — read back as a full column of `Air` instead of "no data", on **both** fluid read paths:

- `ChunkData.FillJobVoxelMap` zero-fills null sections, and `FluidBurstTicker.PrepareNeighbors` accepted any non-null neighbor, so the Burst halo received `Air` rather than the `uint.MaxValue` missing-neighbor sentinel.
- `ChunkData.GetVoxel` returns 0 for a null section and `WorldData.TryGetVoxel` did not check `IsPopulated`, so the managed path saw the same phantom air.

The chain that turned that into a persistent artifact:

1. Ocean water at a chunk border is a *source*, so it is active and `canSpreadHorizontally`. Across the seam it saw air, and because the placeholder's column *below* also read air, the flow pathfinder scored that direction as an immediate drop — making it the single optimal direction.
2. Every border water voxel emitted a level-1 `VoxelMod` into the placeholder, once per tick, at every Y where water touched the seam.
3. `World.ApplyModifications` correctly refuses to write into an unpopulated chunk and routed the mods to `ModManager.AddPendingMod` — which **persists** them to `pending_mods.bin`.
4. When that chunk finally generated, `WorldJobManager.ProcessGenerationJobs` replayed the pending mods with `chunkData.ModifyVoxel`, stamping flowing water over the freshly generated ocean surface. The replay runs *after* the active-voxel scan and bypasses `ApplyModifications`' placement rules entirely.

Lighting already guarded against this (`WorldData.QueueLightUpdate` and the meshing/lighting neighbor gates all test `IsPopulated`); the fluid tick had no equivalent gate — `World.TickChunksParallel` ticks any chunk with a non-empty active bucket regardless of neighbor state.

**Fix:** Treat "present but unpopulated" as *missing* at both read boundaries:

- `FluidBurstTicker.PrepareNeighbors` requires `neighbor.IsPopulated`; otherwise the neighbor slot points at the empty buffer, which the gather sentinel-fills → border reads resolve to void.
- `WorldData.TryGetVoxel` resolves populated chunks only (checked live, after the last-chunk cache, so a placeholder that generates later starts resolving on the next query).

**No migration was written — deliberate (July 2026).** The fix stops new emissions but does not undo pre-fix writes, and the in-game run settled the open question: **already-affected chunks do NOT self-heal.** (The pre-fix reasoning that `infiniteSourceRegeneration` would decay a level-1 stamp back to a source did not hold in practice — do not re-derive it as a reason to skip a cleanup elsewhere.) A cleanup pass was still declined because exposure is limited to local dev builds; the single external tester takes a full release build every 2–3 weeks, so no shipped save carries the artifact. Existing dev worlds keep their seams. If that calculus changes, a cleanup needs two parts: dropping queued fluid mods from `pending_mods.bin`, and repairing seams already saved into region files.

**Follow-on:** Fluid §19 below (quiesced seam fluids are never re-woken when the neighbor populates) — pre-existing, but this fix widened its exposure by moving the placeholder ring onto the void-read path. Fixed separately; both entries are archived here.

### ~~19. Quiesced seam fluids are never re-woken when the neighbor chunk populates~~

**Severity:** Low (narrow geometry; self-corrected on any adjacent edit)  
**Files:** `Helpers/SeamWakeDecision.cs` (new), `World.cs` (`WakeSeamBehaviorNeighborhood`), `WorldJobManager.cs` (`ProcessGenerationJobs` completed-job sweep), `Chunk.cs` (`RegisterActiveVoxelsFromJob` / `OnDataPopulated`)  
**Fixed:** July 2026  
**Status:** Resolved — guarded by baseline **BH-B10** (`Validate Behavior`), prove-red confirmed. **Never observed in-game**, before or after: it was found by code review of Fluid §18 and fixed from analysis, so there is no visual confirmation to obtain — the prove-red baseline is the evidence.

**Symptom:** Chunk B generates an opening at a seam at the same Y as chunk A's water — an ocean-floor cave mouth, an underwater cliff face below sea level — and A's water never flows in. The pocket stays dry until the player breaks or places a block next to it.

**Root Cause:** A read of an unpopulated chunk resolves to *void*, and a void read satisfies no spread test, so a fluid whose only flow-receptive direction was a not-yet-loaded neighbor evaluated inactive and left `ActiveFluidsBucket` on its first tick. Nothing re-registered it: population registers only the **newly populated chunk's own** active voxels, and the sole cross-chunk wake (`ApplyModifications` step 4) requires an applied modification 6-adjacent to the sleeping cell.

Pre-existing rather than introduced — a genuinely absent neighbor has always read as void — but the Fluid §18 fix widened the exposure by moving the whole load-distance placeholder ring onto that same path.

**Fix:** `World.WakeSeamBehaviorNeighborhood`, the behavior-tick sibling of `PromoteLightWorkNeighborhood`, fired from the same two population sites. For each of the 4 cardinal neighbors that is already `IsPopulated`, it re-registers the active-behavior voxels in the 1-deep slab facing the seam.

Two properties keep it cheap and correct:

- **One cell deep, cardinal only.** Activity depends solely on a voxel's ±1 reads (`IsFluidActive`); the ≤4-cell flow pathfinder only ranks directions among already-valid ones, and a diagonal chunk is never an immediate neighbor. Deeper or diagonal waking would be dead work.
- **Re-registration is safe.** A woken voxel that is still stable re-evaluates inactive on the next tick and drops back out, so the wake cannot accumulate. It is family-agnostic (routes through `ChunkData.AddActiveVoxel`), so grass — same `GetState` path, same gap — is fixed by the same pass, guarded by **BH-B11**.
- **The gate samples two rows, not one.** A cell across the seam admits when it is non-solid, is `BlockIDs.Dirt`, *or* when the cell one row **above** it is — grass's up-diagonal spread target is dirt, which is solid, so a same-Y-only gate would silently stop waking grass at seams. The y−1 grass path (`IsDirtNextToAir`) needs air at the same Y, which already admits, so no third sample is needed.

Sections are walked whole, so an all-air span costs one null check rather than 256 reads. **The pass is not itself budgeted:** it runs in `ProcessGenerationJobs`' completed-job sweep, *after* the `modsBudget`/window checks, so P-4 §3.4 bounds it only indirectly — via how many chunks are admitted per frame — and the ms ceiling never measures the wake. Measured (editor-Mono screening, `Documentation/Performance/SEAM_WAKE_FLUID19_2026-07-27_BENCHMARK.md`): **~78 µs per ocean population, ~5.8 µs per land/grass population** (the gate is 13.5× on the latter and does nothing for the former); the extra tick over the woken voxels is not measured, and an IL2CPP fill-load capture is still outstanding.

**Harness note (two false greens caught by the prove-red):** BH-B10 first passed with the fix *neutered*, twice, for two independent reasons — `BehaviorTestWorld` evicts a voxel that went inactive only on the *next* tick's bucket sync (so the scenario read last tick's stale bucket as "woken"), and the harness never registered its **center** chunk in the stub `WorldData` (so the production pass looked the chunk up by coord and silently found nothing). Both are fixed in the harness; a green BH-B10 now means the production pass actually ran.


### ~~20. Fluid surfaces degrade to a near-uniform color with distance from the world centre~~

**Reported:** August 2026  
**Fixed:** August 2026  
**Status:** Resolved — in-game confirmed at spawn, the surrounding region and the ±2³¹ border; near-identical
surfaces at all three, with no re-anchor snapping observed.  
**Files:** `Assets/Shaders/Includes/LiquidCore.hlsl`, `Assets/Scripts/Helpers/LiquidNoiseOrigin.cs` (new),
`Assets/Scripts/World.cs` (`AnchorOrigin`)

**Description:**

Water surfaces progressively lost their surface detail the further the player was from the world centre: the
wave/ripple pattern flattened and the surface trended toward a single uniform color, with foam disappearing. The
degradation was gradual and monotone in distance, not a cliff. Near the ±2³¹ edge the endpoint had already been
recorded as "flat blue". Lava shares the code path.

**A second symptom, found during the bracketing run and not in the original report: the surface _flickered_.**
From ~3×10⁵ blocks out the water visibly strobed frame to frame rather than merely looking flat — large regions
snapping between values instead of showing travelling waves. This was the *first* symptom to appear, well before
the flattening was obvious, and considerably more objectionable than the flattening it preceded.

**Measured onset (supersedes the derived "~1e5–1e6" estimate the entry was filed with):**

Bracketed in-game on `feat/world-scaling`, editor/Mono, fresh world seed 20260816. Method: an identical sealed
16×16 stone basin of player-placed water built at each site via `World.PlaceBlockCommand`, `/time set noon` +
`/time freeze` for constant lighting, one fixed camera pose, 1280×720 render of the game camera. "Detail" is the
RMS difference between horizontally adjacent pixels over a fixed 400×200 band of pure water — a direct measure of
the fine grain the quantization destroys, and one that (unlike variance) actually falls as the field goes
piecewise-constant.

| Distance | `ULP(D)` (blocks) | detail | vs spawn | Observed |
|---------:|------------------:|-------:|---------:|----------|
|        0 |            1.2e-7 | 0.01309 |     100% | reference |
|      1e4 |             0.001 | 0.01310 |     100% | indistinguishable from spawn |
|      1e5 |             0.008 | 0.01362 |     104% | indistinguishable from spawn |
|      3e5 |             0.031 | 0.01252 |      96% | **flicker begins**; detail still intact |
|      1e6 |             0.063 | 0.01060 |      81% | fine ripple grain gone |
|      3e6 |             0.25  | 0.01014 |      77% | coarse blotches only |
|      1e7 |             1.0   | 0.00706 |      54% | soft, near-featureless |

**Onset ≈ 3×10⁵ blocks** (first symptom: flicker), **≈1×10⁶** for unmistakable loss of detail; nothing measurable
at or below 1×10⁵. Cross-checked against an independent float32 port of `snoise`/`fbm`/`EvaluateWater`: the
fraction of horizontally adjacent samples collapsing onto an identical value is 0% up to 1e5, 35% at 3e5, 70% at
1e6, 98% at 1e7 — the same knee, derived independently of the renderer.

**Root cause:**

`LiquidNoisePos` reconstructed an *absolute* voxel-space position, `worldPos + _WorldOriginOffset`, and fed it
straight into `fbm`/`snoise`. `_WorldOriginOffset` was `OriginVoxel`, so its magnitude was the player's distance
from the world centre, passed raw. The float32 ULP of that sum is about `D * 1.2e-7` **blocks** at distance `D`;
once it exceeds the on-screen pixel footprint, neighbouring pixels quantize onto the same noise input and
`combined_noise` stops varying across them. Because that single value modulates both the color `lerp` and
`foamAmt`, the surface flattened and lost foam together.

Note the onset is **not** a function of the authored scales, as the entry originally reasoned. The rounding
happens at the `worldPos + offset` sum, *before* any `* _WaveScale` multiply, so the spatial resolution of the
noise input is `ULP(D)` blocks at every call site. The scales control which features die first and hence how
severe it looks at a given distance — not when quantization starts.

Same defect class as the FL-1 foliage sway freeze (`VoxelCommon.hlsl`), which was the temporal symptom of the
same idiom and was fixed separately.

**Fix:**

The origin is reduced onto a period **the noise already has**. The noise function itself is unmodified.

- The shipped `snoise` folds its lattice index through `mod289`, making the field repeat every 289 lattice cells —
  **867 units of sample input**, since the 1/3 simplex skew turns a one-axis shift of 867 into a lattice shift of
  `(289, 289, 4×289)`. Measured, not assumed: shifting by 867 leaves the field unchanged to float rounding
  (1.1e-3), while 289 and 578 change it by the full output range (~1.67).
- `Helpers/LiquidNoiseOrigin.cs` reduces `OriginVoxel` modulo `PeriodBlocks` = 1734 blocks (two intrinsic periods)
  in integer math, to the representative **nearest zero**, published by `World.AnchorOrigin` as
  `_LiquidNoiseOrigin`. `_WorldOriginOffset` is retired.
- `LiquidCore.hlsl` routes every noise sample through `LiquidNoisePos(worldPos, scale)`, which applies the
  reduction and snaps the scale so `PeriodBlocks × scale` is a whole multiple of 867 — the condition for the
  reduced origin to sample the *same* field value rather than a similar one. Snap step 0.5; every shipped scale
  default (2, 4, 5, 10, 15) already satisfies it, so nothing moves at defaults.

Result: at 10⁷ blocks the reduced origin is `(22, 0, 6)` — as small as at spawn.

| Distance | foam | mean | sd | detail | pre-fix detail |
|---------:|-----:|-----:|---:|-------:|---------------:|
| 0 | 0.039% | 0.70241 | 0.01696 | 0.01305 | 0.01309 |
| 1e6 | 0.041% | 0.70233 | 0.01704 | 0.01310 | 0.01060 |
| 1e7 | 0.005% | 0.70222 | 0.01341 | 0.01125 | 0.00706 |

Spawn unchanged on every statistic; 1e6 statistically indistinguishable from spawn; 1e7 recovered to 86%.
Guarded by 4 Chunk Math baselines (suite 51 → 55); `Validate All` 494/494 across 22 suites.

**Four things learned the hard way — the most valuable part of this entry:**

1. **Do not make the field periodic by wrapping the lattice index.** The first implementation did exactly that.
   It tiled, it was continuous across the seam, and its baselines were green — and it still produced a **visible
   diagonal seam** in play, because a lattice-wrapped field only equals the original *inside one period*. The
   boundary is the plane `4z + x + y = P`, a line of slope −1/4 in XZ. User-reported, then reproduced
   numerically: the two fields are bit-identical up to z = 740 at y = 101 and diverge from z = 740 onward,
   exactly the predicted boundary.
2. **Tiling and continuity are the wrong properties to verify for an origin reduction.** Both were verified for
   the failed attempt and both passed. The property that actually matters is **"reducing the origin by a full
   period is a no-op"** — measured for the shipped fix as a uniform ~6e-3 difference with *no* spatial dependence.
   Test that.
3. **Wrap to the representative nearest zero, not into `[0, P)`.** Both are congruent, so it looks like a free
   choice; it is not. `snoise` recovers its within-cell coordinate as `v - i`, a subtraction of two large similar
   numbers, so accuracy falls with magnitude. A `[0, P)` wrap sent spawn's origin of −16 to +3056 and cost ~3% of
   a cell at the finest octave — enough to raise foam at spawn from 0.039% to 0.693%. Only an A/B against the
   pre-fix shader at an identical scene caught it.
4. **A visible artifact can hide in a capture its author reads as clean.** The seam and the grid were present in
   screenshots reported as showing no problem; the user saw them, the author did not. When a user says an
   artifact is there, believe the report over the reading of the image.

**Limitation:** the scale snap floors at one step (0.5), so a slider set below that — `_NoiseScale` reaches 0.1 —
is raised to 0.5. Pinned by a baseline rather than left to be discovered.

**Residual — the _time_ axis of the same defect class is untouched (open, deliberately deferred).** This fix
bounds the *distance* term only. `wave_p`/`ripple_p` still add a raw `_Time.y * _WaveSpeed` to their Y coordinate,
and lava's `t_boil` likewise, all of which grow without bound exactly as an absolute coordinate grows with
distance. Pre-existing, not introduced here; the FL-1 sway fix bounded both axes, this one did not need to for the
reported symptom.

*Measured* threshold — the metric is animation **resolution** (how many representable steps the sample coordinate
advances per frame at 120 fps), not spatial precision, which is what an earlier draft of this note wrongly quoted:

| Uptime | wave steps/frame | ripple steps/frame |
|-------:|-----------------:|-------------------:|
| 1 h | 27.3 | 20.5 |
| 10 h | 3.4 | 2.6 |
| 20 h | 1.7 | 1.3 |
| 40 h | 0.9 | 0.6 |
| 100 h | 0.2 | 0.3 |

Below ~1 step per frame the surface holds the same value across consecutive frames — the same "steps, then
freezes" symptom FL-1 had. So the onset is **20–40 h**, not 100 h. It is bounded in practice by `_Time.y` being
`Time.timeSinceLevelLoad`, which **resets on every non-additive scene load** — this project loads `Scenes/World`
with `LoadSceneMode.Single`, so the clock restarts each time a world is entered, and reaching 20 h needs one
continuous session in one world.

**Why it is not fixed here.** The obvious in-shader `fmod` does not work: `_Time.y` is already coarse at large
`t`, so reducing downstream fixes the coordinate magnitude but not the clock's own resolution. A correct fix is
the `FoliagePhase` pattern — accumulate in `double` on the CPU and push a bounded phase — but each *effective*
rate needs its **own** reduction (`_WaveSpeed` 0.4, `_RippleSpeed` 1.2, lava `_Speed` 0.3 and its ×0.8 / ×0.5
variants), because scaling an already-reduced phase does not survive the reduction. A single shared wrapped clock
does not work either: bounding one clock for all five rates forces a period so large it reinstates the precision
loss it was meant to remove, and snapping the rates instead collapses lava's ×0.8 and ×0.5 drifts onto the same
value. So it needs 3–5 pushed phases, the material speeds read from C#, and a fallback path for
`FluidPreviewShader`, which shares this include and has no `World` to drive it. That is a scoped change, not a
patch — deferred rather than rushed into the file that produced two regressions the day this shipped.

⚠️ **The flicker is still not measurable automatically.** A "render twice one frame apart" metric is confounded by
the ordinary wave animation, and a "nudge the camera 0.01 blocks and diff" metric was *tried and rejected*: it
reports RMS 0.0116 / worst 0.46 at **spawn** versus 0.0077 / 0.51 at 1e7, i.e. it cannot tell them apart, because
moving the camera resamples the surface at every distance. Any future far-origin baseline intending to guard the
flicker needs a metric validated against the D=0 control first.

**A world-scene capture rig now exists**, as a play-mode script rather than a suite: launch a fixed-seed world,
`/fly` + `/noclip`, `/time set noon` + `/time freeze`, `/teleport`, build a sealed basin with
`World.PlaceBlockCommand`, set player rotation + camera pitch directly, then render `Camera.main` into a
`RenderTexture` and `ReadPixels`. That is the scene half of the far-origin render baseline
`WORLD_SCALING_FLOATING_ORIGIN.md` §9 asks for; what it still lacks is a metric proven to separate near from far.

**Not part of this bug:** the FL-1 foliage sway freeze (same root idiom, fixed separately); the fluid *simulation*
failing to reactivate at far coordinates (FLUID #17, a CPU-side integer-routing issue); terrain generation noise
precision past ±2²⁴ (the FNL rider, `WORLD_SCALING_IMPLEMENTATION.md` §6); and the striped cloud *field* near
±2³¹ (`WORLD_SCALING_FLOATING_ORIGIN.md` §9 — CPU-side pattern generation, not this shader path; cloud *drift* is
unaffected because `Clouds.cs` wraps it).

---

## Chunk Management

### ~~01. `ChunkCoord` integer division truncates negative coordinates incorrectly~~

**Severity:** Bug (latent)  
**Files:** `Chunk.cs` — `ChunkCoord`  
**Fixed:** March 2026

**Root Cause:** `FromVoxelOrigin` and `FromWorldPosition` used raw integer division (`pos.x / VoxelData.ChunkWidth`) without `Mathf.FloorToInt`. In C#, integer division truncates toward zero instead of stepping down to the correct negative grid coordinate.

**Fix:** Replaced the integer division in the `Vector2Int` and `Vector3Int` constructors with floating-point division coupled with `Mathf.FloorToInt`.

---

### ~~02. `ChunkCoord.GetHashCode()` has high collision potential~~

**Severity:** Improvement  
**Files:** `ChunkCoord.cs` — `GetHashCode`  
**Fixed:** Early 2026

**Root Cause:** The hash function `31 * X + 17 * Z` produces a very small hash space and has poor distribution for typical chunk coordinate ranges.

**Fix:** Replaced with `HashCode.Combine(X, Z)`.

---

### ~~03. `Tick()` iterates `_activeChunks` which can be modified concurrently~~

**Severity:** Bug  
**Files:** `World.cs` — `Tick`  
**Fixed:** Early 2026

**Root Cause:** The `Tick` coroutine used a `foreach` over `_activeChunks`, which could be modified by `CheckViewDistance` between `WaitForSeconds` yields, causing `InvalidOperationException`.

**Fix:** Changed the tick loop to iterate over a snapshot copy of the active chunks set.

---

### ~~04. `PrepareJobData` is called for soon-to-be-destroyed World instances~~

**Severity:** Bug  
**Files:** `World.cs` — `Awake`  
**Fixed:** Early 2026

**Root Cause:** `PrepareJobData()` was called outside the Singleton else-branch and ran for both the surviving and the duplicate (about to be destroyed) World instances, leaking NativeArray memory.

**Fix:** Moved `PrepareJobData()` inside the else-branch so it only runs for the surviving instance.

---

### ~~05. Unreachable diagnostic check in `UnloadChunks`~~

**Severity:** Code Quality  
**Files:** `World.cs` — `UnloadChunks`  
**Fixed:** Early 2026

**Root Cause:** Dead code — the same `IsAwaitingMainThreadProcess` condition was already checked above and caused a `continue`, making the second check unreachable.

**Fix:** Removed the unreachable block.

---

### ~~06. Chunk load animation adds `GetComponent` on every activation and loops on modify~~

**Severity:** Improvement (performance) & Bug **Files:** `Chunk.cs` — `PlayChunkLoadAnimation`, `ChunkLoadAnimation.cs`  
**Fixed:** March 2026

**Root Cause:** `GetComponent<ChunkLoadAnimation>()` was called on each pool activation. A primitive boolean flag was previously attempted, but it could become stale if the component were destroyed. Furthermore, applying animations from the pool caused a 1-frame visual flash, and subsequent chunk modifications re-triggered the upward animation natively since it hooked into mesh generation.

**Fix:**

- Used a cached `_loadAnimation` reference inside `Chunk.cs` utilizing Unity's overriden `== null` safety check rather than a primitive boolean flag.
- Refactored `ChunkLoadAnimation` to take an absolute target position (`ResetToUnderground`) to prevent infinite vertical offsets.
- Added a `_hasPlayedLoadAnimation` flag to `Chunk.cs` to guarantee animations don't re-trigger when local voxel modifications request a mesh rebuild.

---

### ~~07. `_chunksToBuildMesh` uses `List.Remove()` / `Insert(0)` / `RemoveAt(i)` which are O (n)~~

**Severity:** Improvement (performance)  
**Files:** `World.cs` — `RequestChunkMeshRebuild`, mesh scheduling loop, `CheckViewDistance`, `UnloadChunks`; new `Helpers/MeshBuildQueue.cs`  
**Fixed:** July 2026 (MT-1)

**Root Cause:** The mesh-rebuild queue was a `List<Chunk>` used as a priority queue. Front-insertion for immediate requests (`Insert(0)`), mid-list removal in the scheduling drain (`RemoveAt(i)`), and by-value removal on unload (`Remove(chunk)`) are all O (n) shifts/scans. Under a large meshing backlog (rapid player movement, streaming) the per-frame drain went quadratic. The companion `HashSet` only made *duplicate detection* O (1); the ordered list operations stayed slow.

**Fix:** Replaced the list + set pair with `Helpers/MeshBuildQueue.cs` — a pooled intrusive doubly-linked list (parallel `next`/`prev`/`chunk`/`coord` arrays threaded by a free-list) plus a `coord → slot` map serving both dedup and O (1) removal. Every operation is now O (1): immediate enqueue links at the head (LIFO), normal at the tail (FIFO), the drain removes the current node via a mutating struct enumerator, and unload removes by coordinate. Ordering is bit-identical to the old list (all immediates ahead of all normals; retain-on-not-ready
preserved), and slot recycling makes the queue zero-GC in steady state.

---

### ~~08. Enabling chunk load animations mid-session does nothing (toggle regression)~~

**Severity:** Bug (regression)  
**Files:** `Chunk.cs` — constructor, `Reset`, `PlayChunkLoadAnimation`, new `EnsureLoadAnimation`  
**Introduced:** 2026-04-09 (`a41834b8`) · **Fixed:** July 2026

**Symptom:** Starting a world with **Enable Chunk Load Animations** off and switching it on mid-session had no effect — chunks kept popping in instantly. The reverse (starting on, toggling off, toggling back on) worked fine, which made the setting look intermittently broken rather than broken.

**Root Cause:** `a41834b8` ("Fixed: All 'major' code issues reported by the Unity Project Auditor tool")
**over-corrected**. The auditor flagged runtime `AddComponent` (boxing/GC), so the commit replaced the lazy get-or-add of `ChunkLoadAnimation` with a **construction-time-only** add behind
`if (settings.enableChunkLoadAnimations)`, and added `&& _loadAnimation != null` to every use site. Because the pool **reuses `Chunk` instances**, a chunk built while the setting was off had `_loadAnimation == null`
for the rest of its life and no code path could ever create one. The pre-regression version also seeded a mid-game-added component with `ResetToUnderground` — that line went too.

Two things kept it hidden for ~3.5 months: the setting is **off by default**, and the failure is **inconsistent rather than total** — chunks constructed *after* the toggle (as the pool grows) do animate, so a casual check can show it "working".

**Fix:** New `Chunk.EnsureLoadAnimation()` restores creation on demand — `AddComponent` **plus** the
`ResetToUnderground` seed — called from `Reset` and `PlayChunkLoadAnimation` when the setting is on. The constructor still pre-adds, so the common path is unchanged. This does **not** reintroduce what the auditor flagged: the cached field short-circuits it, so it runs at most **once per `Chunk` instance, ever**, never per pool activation. `Reanchor` deliberately keeps snapping when no component exists — a floating-origin re-base has no reason to create one.

> **Without the seed the fix is still wrong, silently:** a freshly added component has no target position, so
> the chunk lerps toward the **world origin** instead of rising into place. Baseline B35 exists specifically
> to red on that.

**Confirmed in-game:** 2026-07-25 — starting a world with animations off and enabling them mid-session now animates newly streamed/recycled chunks correctly.

**Guard:** meshing suite **B34** (toggle mid-session → component appears and is enabled), **B35** (a mid-session add is parked underground relative to the chunk's own position, not at the origin), **B36**
(controls: on-at-construction still pre-adds; off still snaps and creates nothing), via the new
`ChunkLoadAnimationTestFixture`. Prove-red confirmed: restoring the exact pre-fix guard reds B34+B35; removing only the seed reds B35 alone with `got (0.00, 0.00, 0.00)`; removing the constructor pre-add reds only B36.

**Lesson:** a static-analyzer fix that removes a *capability* to remove a *cost* needs the capability re-proven. The cheap correct form here was "cache it", not "only ever build it once, at construction".

---

## Player

### ~~01. Mouse input uses `Time.timeScale` instead of frame-rate independent delta~~

**Severity:** Quirk  
**Files:** `Player.cs` — `Update`  
**Fixed:** March 2026

**Symptom:** Camera rotation scaled by `Time.timeScale` instead of using the raw mouse delta, making sensitivity inconsistent at different frame rates and freezing look when `timeScale = 0`.  
**Root Cause:** Legacy `Input.GetAxis("Mouse X/Y")` baked in its own sensitivity, masking the issue. After migrating to the new Input System, `Mouse.delta` provides raw per-frame deltas; multiplying by `timeScale` was doubly wrong.  
**Fix:** Removed `Time.timeScale` from the rotation calculations entirely. The `MOUSE_DELTA_SCALE` constant in `InputManager.cs` (0.1f) already normalizes raw deltas to match the legacy sensitivity feel.

---

### ~~03. Player falls through the world during loading~~

**Severity:** Bug  
**Files:** `Player.cs` — `FixedUpdate`, `World.cs`  
**Fixed:** March 2026

**Symptom:** On some loads, the player would briefly fall through terrain before chunks finished meshing.

**Root Cause:** `FixedUpdate` applied gravity and movement before `StartWorld` finished loading and meshing the spawn chunks.

**Fix:**

- Exposed `_isWorldLoaded` as `public bool IsWorldLoaded => _isWorldLoaded` on `World`.
- `FixedUpdate` in `Player.cs` now returns immediately if `!_world.IsWorldLoaded`.

---

### ~~04. `GameObject.Find("Main Camera")` is fragile~~

**Severity:** Improvement  
**Files:** `Player.cs` — `Start`, `LoadSaveData`  
**Fixed:** March 2026

**Root Cause:** Camera was resolved by string name — renaming the GameObject would silently break the player controller.

**Fix:** Replaced both occurrences with `Camera.main?.transform`.

---

### ~~05. `Application.Quit()` on Escape is not editor-safe~~

**Severity:** Improvement  
**Files:** `Player.cs` — `GetPlayerInputs`  
**Fixed:** March 2026

**Root Cause:** `Application.Quit()` is a no-op in the Unity Editor, which meant pressing Escape during editor play-mode did nothing.

**Fix:** Wrapped with `#if UNITY_EDITOR` / `#else`: the editor path calls `UnityEditor.EditorApplication.isPlaying = false`, while the build path calls `Application.Quit()`.

---

### ~~World-gen replacement tags leak into player placement~~

**Severity:** Bug  
**Files:** `BlockType.cs`, `BlockTagPreset.cs`, `PlacementRules.cs`, `VoxelMod.cs`, `World.cs` (`ApplyModifications`), `WorldJobManager.cs`, `PlacementResolver.cs`, `BlockDatabase.asset`  
**Validation:** `Minecraft Clone/Dev/Validate Placement` (`PlacementValidationSuite`) — baselines + the promoted regression guards (Coal Ore / Directional Block / Oak Log land-on-top + the `placementCanReplaceTags` data audit).  
**Fixed:** June 2026

**Symptom:** Some blocks could not be placed **on top of** others. Holding Coal Ore, the cursor tunneled through stone (and replaced it); Directional Block tunneled through nearly every solid; Oak Log tunneled through leaves.

**Root Cause:** A single `BlockType.canReplaceTags` field served two unrelated consumers: the **world-gen** write gate in `World.ApplyModifications` (where a structure log legitimately overwrites leaves while stacking, ore replaces stone, etc.) **and** player placement (the `PlacementResolver` raycast skip-mask + replace decision). The shipping values were tuned for generation, so structural tags (`ROCK`, `LEAVES`, the Directional Block's near-everything mask) leaked into the player's hand and made the placement ray pass through those surfaces.

**Fix:** Split `canReplaceTags` into `worldGenCanReplaceTags` (carried the old values via `[FormerlySerializedAs]`) and `placementCanReplaceTags`. `BlockTagUtility.CanReplace` now takes the mask explicitly, with `CanReplaceForWorldGen` / `CanReplaceForPlacement` wrappers; `VoxelMod.Source` (`Live` vs `WorldGen`, stamped on generation mods in `WorldJobManager`) routes the `ApplyModifications` Default-rule gate to the right field. The player placement mask was retuned to the soft set `REPLACEABLE | LIQUID` for all blocks — notably **dropping `PLANT`**,
which also tags solid Oak Leaves (every genuinely replaceable plant is also `REPLACEABLE`, so leaves are now buildable-on). World-gen masks were left untouched, so generation behavior is byte-identical.

---

### ~~REQUIRES_SUPPORT blocks can be placed without support~~

**Severity:** Bug  
**Files:** `PlayerInteraction.cs`, `World.cs` (`CanPlayerPlaceAt`), `PlacementResolver.cs`  
**Validation:** `Minecraft Clone/Dev/Validate Placement` (`PlacementValidationSuite`) — promoted regression guards "Grass Blades cannot be placed floating above water" + synthetic "REQUIRES_SUPPORT block rejected above a non-supporting block", plus over-rejection tripwire baselines.  
**Fixed:** June 2026

**Symptom:** A block tagged `REQUIRES_SUPPORT` (e.g. Grass Blades, id 22) could be placed even when the cell directly below provided no support — most visibly, grass blades placed floating on top of water.

**Root Cause:** The `REQUIRES_SUPPORT` system was implemented only as a *reactive break-cascade* in `World.ApplyModifications` (when an existing supporting block becomes non-solid, the block above it is broken). There was **no placement-time gate** — the player placement permission checked only world bounds + cell occupancy, never that a `REQUIRES_SUPPORT` block had a support-providing block beneath it.

**Fix:** Extracted the player placement-permission decision into `World.CanPlayerPlaceAt(placeCell, placedBlock)` (shared by `PlayerInteraction` and the placement validation harness). It now rejects a `REQUIRES_SUPPORT` block unless the cell directly below `ProvidesSupport`, via the pure `PlacementResolver.HasRequiredSupport`. The placement highlight reflects the rejection. Code-only — Grass Blades was already correctly tagged.

---

### ~~03. Far-Lands Voxel Modification Broken — Placement/Breaking Fail Near the ±2³¹ Edge~~

**Severity:** Low (far-lands only; normal-play range unaffected)  
**Files:** `PlacementController.cs`, `PlayerInteraction.cs`, `World.cs` (`ApplyModifications`), `Commands/` (`/setblock`)  
**Reported:** July 2026 (logged 2026-07-19 during the Bug 19 far-coordinate verification session, BEFORE commit `ed8cb69` landed)  
**Fixed:** July 2026 (resolved by `ed8cb69`; re-test confirmed 2026-07-19 — no code change of its own)

**Symptom (observed pre-`ed8cb69`):** Near the ±2³¹ voxel edge (possibly earlier): the placement highlight never rendered (only the breakage highlight), breaking did nothing, placing decremented the hotbar without changing the world (the `VoxelMod` enqueued at interaction time, then misrouted at apply time), and `/setblock` placed its block at a drifted cell.

**Root Cause:** The same int→float round-trip class as lighting Bug 19 (`#24` above), on the mod-application seams `ed8cb69` fixed: `World.ApplyModifications`' chunk routing (`World.cs:2421`) and the mod local-position derivations (`World.cs:922/:2500`) went through implicit `Vector3Int`→`Vector3` conversions that lose integer precision past ±2²⁴ — an integer-correct `VoxelMod` was routed to the adjacent chunk's bucket and/or given a wrong local cell, which explains all four symptoms including the `/setblock` "drift" (the command path itself is integer
end-to-end). The original observations may also have been compounded by chunks persisted in a broken state on the pre-fix test world.

**Verification (fresh world, editor, 2026-07-19):** breaking, placing, and highlights all correct at +16,800,000, +2×10⁷, +2,147,000,000, and +2,147,483,500 (where edits even land correctly in the quadrant-wrapped chunk — the §9 documented-only edge class); no `WorldData.AssertWithinFloatPrecision` tripwire hits. New far-coordinate observations from the re-test that are NOT this bug: natural-fluid reactivation failure (logged as an open entry in `FLUID_BUGS.md`) and near-edge cosmetic/perf items (added to
`WORLD_SCALING_FLOATING_ORIGIN.md` §9).

---

## World Generation & Data

### ~~02. `ProcessGenerationJobs` always uses `biomes[0]` for flora generation~~

**Severity:** Bug **Files:** `WorldJobManager.cs` -> (Now handled by `IChunkGenerator.ExpandFlora`)  
**Fixed:** April 2026

**Symptom:** In a multi-biome world, trees in non-default biomes received incorrect height distributions because the code hardcoded `_world.biomes[0].minHeight`. **Root Cause:** During chunk generation, tree flora placement logic always evaluated the 0-th index biome definition rather than checking the local biome at the tree's physical coordinates. **Fix:** Resolved during the Modular World Generation refactor. `WorldJobManager` now delegates this to `IChunkGenerator.ExpandFlora()`, which internally resolves the correctly mapped biome per local
coordinate before handing off constraints to the `Structure` generator.

---

### ~~04. `heightMap` uses `byte` which limits world height to 255~~

**Severity:** Improvement  
**Files:** `ChunkData.cs` — `heightMap`, `ModifyVoxel`; `WorldJobManager.cs`  
**Fixed:** March 2026

**Symptom:** The heightmap was stored as `byte[]`, limiting tracked height to 0–255. If chunk height were ever increased beyond 255, heights would silently truncate.

**Fix:** Changed `heightMap` to `ushort[]` (0–65535) across the generation and lighting pipeline (`ChunkData`, `GenerationJobData`, `LightingJobData`). Added region serialization backwards compatibility to upgrade V1 chunks (`byte[]`) to V2 chunks (`ushort[]`).

---

### ~~03. `ModifyVoxel` heightmap scan uses `IsOpaque` instead of `IsLightObstructing`~~

**Severity:** Bug  
**Files:** `ChunkData.cs` — `ModifyVoxel`  
**Fixed:** March 2026

**Note:** Same fix as Lighting #04 above. The inconsistency was in `ChunkData.ModifyVoxel`'s Case 2 downward scan.

---

### ~~05. `RequestNeighborMeshRebuilds` used chunk coordinates as world-space offsets~~

**Severity:** Bug  
**Files:** `World.cs` — `RequestNeighborMeshRebuilds`, `QueueNeighborRebuild`  
**Fixed:** During `ChunkCoord` refactoring (before March 2026)

**Resolution:** Resolved during the `ChunkCoord` refactoring. `RequestNeighborMeshRebuilds` now uses `chunkCoord.Neighbor(dx, dz)`, and `QueueNeighborRebuild` calls `.ToVoxelOrigin()` internally.

---

### ~~06. `WorldData.GetLocalVoxelPositionInChunk` uses `(int)` cast instead of `FloorToInt`~~

**Severity:** Bug (latent)  
**Files:** `WorldData.cs` — `GetLocalVoxelPositionInChunk`  
**Fixed:** March 2026

**Symptom:** Yielded incorrect chunk boundary voxel references for negative world coordinates. **Root Cause:** The method used an `(int)` cast which truncates toward zero, producing wrong results for negative coordinates. **Fix:** Both `x` and `z` components now use `Mathf.FloorToInt()`. The `y` component retains the `(int)` cast since Y is always non-negative.

---

### ~~TODO: 2D Cross-Section Preview missing flora / structure rendering~~

**Severity:** Feature Gap  
**Files:** `Assets/Editor/WorldTools/WorldGenPreviewWindow.CrossSection.cs` — `EvaluateColumn()`, `CheckFloraSpawnPoint()`  
**Fixed:** May 2026

**Original issue:** `EvaluateColumn()` replicated the runtime `StandardChunkGenerationJob` logic per block column but skipped flora and structure placement entirely. All flora and structures were absent from the X-Y, Z-Y, and X-Z panels.

**Fix:** `EvaluateColumn()` now outputs `floraSurfaceY` and `floraBiomeIdx` per column. A new `CheckFloraSpawnPoint()` method evaluates `StructurePoolEntry` grid election per-column and renders structure template blocks inline on all three cross-section panels. Cross-chunk structures are excluded since the preview only renders 2D slices.

---

### ~~TODO: 3D Chunk Preview missing flora / structure rendering~~

**Severity:** Feature Gap  
**Files:** `Assets/Editor/WorldTools/ChunkPreview3DWindow.Pipeline.cs` — `ExpandStructuresAndApplyMods()`  
**Fixed:** May 2026

**Original issue:** The `ChunkPreview3DWindow` used runtime Burst jobs for terrain generation, lighting, and meshing, but skipped structure expansion after generation. `StructureSpawnMarker`s emitted by `StandardChunkGenerationJob` were disposed unused.

**Fix:** Implemented `ExpandStructuresAndApplyMods()` which dequeues `StructureSpawnMarker`s after generation completes, calls `IChunkGenerator.ExpandStructure()` on the main thread to produce `VoxelMod`s, and applies each modification to the correct chunk's `NativeArray<uint>` map using coordinate translation (global position → chunk origin + local offset). Cross-chunk `VoxelMod`s (structures spanning chunk boundaries) are routed to neighbor chunk maps via `ApplyVoxelModToMap`. Heightmaps are recomputed before the lighting phase begins.

---

## Block Behavior

### ~~04. Fluid `FluidLevel` set redundantly in `HandleFluidFlow`~~

**Severity:** Code Quality  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`  
**Fixed:** March 2026

**Root Cause:** `FluidLevel` was set once inside the object initializer and a second time directly on the variable immediately after — harmless but clearly a copy-paste oversight.

**Fix:** Removed the redundant second assignment.

---

### ~~05. Hardcoded Block IDs throughout the codebase~~

**Severity:** Improvement  
**Files:** `BlockBehavior.cs`, `BlockBehavior.Grass.cs`, `BlockBehavior.Fluids.cs`, `Structure.cs`, `MeshGenerationJob.cs`, `World.cs`, `PlayerInteraction.cs`, `LightingJobBenchmark.cs`  
**Fixed:** March 2026

**Symptom:** Block IDs like Grass (`2`), Dirt (`3`), Air (`0`), Stone (`1`), Oak Log (`14`), Oak Leaves (`15`), Cactus (`16`), and Lava (`20`) were hardcoded as magic numbers across 8 files.  
**Impact:** Any change to the `BlockDatabase` array order would silently break all logic referencing these IDs.  
**Fix:**

1. Created `Assets/Editor/BlockIdGenerator.cs` — an Editor tool (`Minecraft Clone > Generate Block IDs`) that reads the `BlockDatabase` and auto-generates `Assets/Scripts/Data/BlockIDs.cs` containing `public const ushort` fields for every block type.
2. Replaced all 26 hardcoded block ID instances across 8 files with `BlockIDs.*` constants.
3. Added a `BlockDatabaseChangeDetector` asset postprocessor that warns when the database changes and the generated file may be stale.
4. Generator enforces the **Air = 0 architectural invariant** with a hard-assert, refusing to write the file if `blockTypes[0]` is not "Air".

**Note:** This resolves magic numbers in code but changing the `BlockDatabase` order still breaks save files. The long-term fix is the Chunk Palette Mapping system (see `Documentation/Design/CHUNK_PALETTE_MAPPING.md`).

---

### ~~01. `BlockBehavior.s_mods` is a shared static list (thread safety / reentrancy hazard)~~

**Severity:** Bug (latent)  
**Files:** `BlockBehavior.cs`  
**Fixed:** March 2026

**Symptom:** Race condition if block behavior is evaluated across multiple threads, leading to data corruption as `Behave()` overwrites the returned reusable metadata list. **Root Cause:** The `Behave` method used a single shared static `List<VoxelMod>` (`s_mods`) that was cleared and reused on every call. **Fix:** Replaced the single static list with a `[ThreadStatic]` lazily instantiated backing list mapped to a `Mods` property. This guarantees every thread hitting `Behave()` gets its own private, zero-allocation reusable list.

---

## Player & Input

### ~~01. Collision only checks at two height levels (feet and +1)~~

**Severity:** Bug  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`  
**Fixed:** March 2026

**Root Cause:** The horizontal collision properties only checked at two Y levels. **Fix:** Encapsulated player physics into `VoxelRigidbody` and implemented dynamic AABB sweeping across the entity's full `entityHeight`.

---

### ~~02. Collision checks don't account for player width in the cross-axis~~

**Severity:** Bug  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`  
**Fixed:** March 2026

**Root Cause:** Each directional collision check sampled a single line, causing clipping on diagonal movement. **Fix:** `VoxelRigidbody` now sweeps an axis-aligned bounding box face dynamically.

---

### ~~03. Raycast-based block placement can be incorrect on exact voxel edges~~

**Severity:** Bug (latent)  
**Files:** `PlayerInteraction.cs` — `RaycastForVoxel`  
**Fixed:** March 2026

**Root Cause:** The block placement used modulo `% 1` which failed on negative coordinates. **Fix:** Rewrote `RaycastForVoxel` to use a robust previous-step integer coordinate tracking method.

---

### ~~04. Block placement overlap check only covers 2 voxels of player height~~

**Severity:** Bug  
**Files:** `PlayerInteraction.cs` — `PlaceCursorBlocks`  
**Fixed:** March 2026

**Root Cause:** The placement validity check only checked `y` and `y+1`, ignoring actual `playerHeight`. **Fix:** Replaced hardcoded checks with a dynamic AABB intersection check against the placed voxel's bounds.

---

### ~~05. `collisionPadding` shrinks AABB incorrectly causing erratic collision behavior~~

**Severity:** Bug  
**Files:** `Physics/VoxelRigidbody.cs`  
**Fixed:** March 2026

**Symptom:** Setting `collisionPadding` to non-zero values caused erratic collision behavior (e.g., random teleports if negative, or not affecting actual collision distance correctly if positive). Corners of voxels intersected the collision bounding box.  
**Root Cause:** Bounding box continuous widths and radius calculations were loosely defined and sometimes conflated, leading to scalar errors during AABB sweep tests.  
**Fix:** Introduced unified `CollisionHalfWidthX` and `CollisionHalfDepthZ` properties as the single source of truth, removing `* 0.5f` redundant calculations and properly incorporating `collisionPadding`.

---

### ~~06. Block placement disabled too early due to overly strict player AABB intersection~~

**Severity:** Bug  
**Files:** `PlayerInteraction.cs`  
**Fixed:** March 2026

**Symptom:** The player could not replace a broken block if they were standing close to the wall (e.g., 1/3 of a block away) because the placement check overlapped with the player's collision bounds too safely.  
**Root Cause:** `PlayerInteraction` used `VoxelRigidbody.collisionWidthX` and depth without accurately accounting for padding or using consistent extents.  
**Fix:** Refactored the overlap calculation in `PlayerInteraction.cs` to accurately use the new `CollisionHalfWidthX` and `CollisionHalfDepthZ` properties.

---

### ~~07. Hardcoded collision epsilon `0.001f` causing readability issues~~

**Severity:** Code Quality  
**Files:** `Physics/VoxelRigidbody.cs`  
**Fixed:** March 2026

**Root Cause:** The collision snapping logic used hardcoded `0.001f` magic numbers across properties.  
**Fix:** Centralized with a `COLLISION_EPSILON` constant.

---

### ~~08. Player embeds in a block after landing and can no longer jump~~

**Severity:** High — the player is stranded; escape requires a debug capability (flight/noclip)  
**Fixed:** August 2026  
**Status:** Resolved — confirmed in game 2026-08-03. *Confirmation caveat: the in-game session was short (a minute or two of repeated falls) against a bug that fired on roughly a third of fast landings, so the durable evidence is the deterministic harness repro, not the play session.*  
**Guarded by:** `B20`–`B23` in the `NS-4` suite (`Minecraft Clone/Dev/Validate Physics Solver`), promoted from the `K04a`–`K04d` reproductions; `B18`/`B19` bound the fix from the other side.  
**Files:** `Assets/Scripts/Physics/VoxelRigidbody.cs` (`ResolveMovement`), `World.CheckPhysicsCollision`

**Description (user-observed in game, 2026-08-03):**
After landing from a fall the player can end up **stuck inside a block**. While stuck, **jump input is
refused** — not merely ineffective. The only known escape is to enable flight/noclip, fly up, and land
softly. Reported as intermittent ("sometimes"), and **correlated with higher fall speed**.

**Reproduction Steps (in game):**

1. Fall from a height — the higher the fall speed, the more often it triggers.
2. Land on solid ground.
3. Occasionally the player settles embedded in the block rather than on top of it.
4. Press jump — nothing happens, repeatedly.
5. Enable flight/noclip, ascend, disable flight, land gently → normal behavior returns.

**Deterministic repro:** `B20`–`B23` (`PhysicsSolverValidationSuite.Baseline.cs`), red against the unfixed solver. The trigger condition is the
gap remaining between the feet and the surface at the *start* of the landing tick: under one substep length it sticks,
at or above it does not. Measured at terminal fall speed, gaps of 0.02 / 0.05 / 0.08 stick and 0.086 upward do not —
roughly a third of fast landings, which is the reported "sometimes".

**Root cause (confirmed by harness instrumentation, 2026-08-03).** Three shipped behaviors in a chain:

1. `World.CheckPhysicsCollision`'s overlap test is **strict** (`min.y < blockMax.y`), so a body the vertical resolve
   has snapped *flush* onto a surface does not overlap it.
2. Therefore `ResolveMovement`'s zero-vertical-movement branch — which probed the un-extended AABB downward — could
   only ever ground an **embedded** body. A correctly-resting body read as airborne. Measured: the flush AABB's
   down-probe returns `Hit = false`; the same AABB extended 0.002 returns `Hit = true, correction = 0.000999`.
3. `CalculateVelocity` calls `ResolveMovement` once per substep and every call **reassigns** `IsGrounded`, so the
   tick's verdict is whatever the *last* substep decided. After contact, the next substep resolves to exactly zero
   (which latches `subMove.y` to zero), and each substep after that takes the branch from (2) and clears the verdict.

Self-sustaining, hence "never recovers": a not-grounded body never gets `_verticalMomentum` zeroed
(`CalculateVelocity`'s `IsGrounded && _verticalMomentum < 0` guard), so momentum stays pinned at the gravity clamp, so
every later tick is large enough to substep and re-enters the same pattern. A *gentle* landing is a single
un-substepped resolve, which grounds correctly — that is why the reported flight escape works.

**Fix:** the zero-vertical-movement branch now probes `GROUND_PROBE_SKIN` (`2 × COLLISION_EPSILON`) below the feet, so
a resting body registers the surface it is standing on. Each substep still evaluates the body's real current position,
so a body that leaves its support mid-tick correctly ends airborne (`B19` guards that; `B18` guards the probe depth
against grounding a hovering body). The `Correction > -0.01f` guard was dropped: for a downward Y query the correction
is positive whenever the AABB overlaps, so the test was unreachable.

**Narrowing (the original static-analysis leads, kept for the record — see the confirmations/refutations below):**

- `VoxelRigidbody.RequestJump()` is the **only** jump entry point and gates solely on
  `IsGrounded && !isFlying`. "Jump refused" therefore means **`IsGrounded == false`** while the player is
  visually at rest. This is a solver *state* problem, not an input-layer one.
- `IsGrounded` is written in only four places, all in the vertical resolve at the end of `ResolveMovement`
  (`IsGrounded = groundedByStep`, the `ySign < 0` hit, and the zero-vertical-movement branch) plus the
  `FixedUpdate` reset. That is a small, tractable surface to instrument.
- The zero-vertical-movement branch grounds on `groundContact.Hit && groundContact.Correction > -0.01f` —
  a bare threshold worth logging alongside the actual correction when the bug fires.
- Fall speed as the aggravator points at the substepping / tunneling guard
  (`SUB_VOXEL_COLLISION_SYSTEM.md` §3.4.4, `MIN_COLLISION_THICKNESS`-derived max step): a displacement
  large enough to need substeps is exactly the high-speed case.
- The workaround restoring normal behavior (rather than a reload being required) suggests **transient
  solver state**, not corrupted world data.

**What instrumentation confirmed and refuted (harness, 2026-08-03):**

- ✅ **Confirmed:** jump refusal is `IsGrounded == false`, a solver *state* problem, not input-layer. `RequestJump`
  was driven directly and refused to latch.
- ✅ **Confirmed:** the zero-vertical-movement branch is the deciding write site, and the substep chain is how a fast
  landing reaches it. Per-substep dump: substep 0 lands (`grounded = true`), substep 1 resolves to exactly `0`
  (`grounded = true`, latches `subMove.y`), substep 2 takes the zero-branch (`grounded = false`).
- ✅ **Confirmed:** transient solver state, not world data — a single gentle tick restores the verdict.
- ❌ **Refuted:** "the higher the fall speed, the more often" is not the whole story. What matters is the *residual
  gap* at the landing tick's start, not the speed as such; speed only widens the window by adding substeps.
- ❌ **Refuted: the player is never literally embedded by this mechanism.** Across every run the body settled at the
  solver's stand-off *above* the surface (`feet − top = +0.001`), never below it. §04 is a grounded-**state** defect;
  the wording "embeds in a block" describes how the refusal feels, not the geometry. If a genuinely sunken body is
  ever observed in game, that is `PLAYER_BUGS.md` §05 or §01, not this entry.

**Relationship to the still-open `PLAYER_BUGS.md` entries:** this and §01 are both "collision gets stuck", but they
are now known to be different mechanisms — this one was a state defect on open ground with correct geometry, while §01
is geometric and triggers in tight spaces. §05 (embedded-body ejection) is the better candidate for a shared root cause
with §01.

---

## Architecture & Configuration

### ~~01. Settings logic is duplicated and tightly coupled to World.cs~~

**Severity:** Code Quality  
**Files:** `World.cs`, `SettingsManager.cs`, `TitleMenu.cs`, `WorldSelectMenu.cs`  
**Fixed:** March 2026

**Symptom:** JSON Settings creation and loading logic was duplicated across multiple scripts.  
**Root Cause:** `Settings` was previously nested inside `World.cs` and lacked a dedicated serialization controller.  
**Fix:** Extracted `Settings` into a standalone POCO and created a centralized `SettingsManager` that handles loading, saving, and Editor-specific DB refresh functionality. The duplicated load logic was removed from all caller scripts.

---

## Serialization & Storage

### ~~03. NativeCompressions LZ4Stream is asymmetric and hangs forever on its own output~~

**Severity:** CRITICAL — world loads hang the entire system at 100% CPU  
**Files:** `CompressionFactory.cs` (`LZ4Stream` usage), `ChunkSerializer.Deserialize`, `MigrationManager.Compress/Decompress`  
**Library:** `NativeCompressions.LZ4` 0.6.1 (added 2026-06-10 with the Android work)  
**Discovered:** 2026-06-11, diagnosed via startup heartbeat + step tracing + standalone PowerShell repro.  
**Fixed:** June 2026  
**Status:** Resolved — reverted to 0.6.0, fail-fast guard added, affected save restored from backup. Version pin documented in `LIBRARY_BUGS.md`.

**Symptom:** Loading any world saved (or migrated) after the switch to NativeCompressions hangs at
"Loading/Generating initial chunks" forever. All `LoadChunkAsync` ThreadPool tasks enter
`ChunkSerializer.Deserialize` and never return (`finished=0`); the ThreadPool injects ~1 new thread/second, each of which also wedges; CPU climbs until the system is unresponsive.

**Root cause (two stacked library defects, reproduced OUTSIDE Unity in a 3-line repro):**

1. **Asymmetric formats.** `LZ4Stream(CompressionMode.Compress)` writes raw **block** format (`[int32 blockSize][LZ4 block]`, e.g. `25 00 00 00 FF 04 ...`), but
   `LZ4Stream(CompressionMode.Decompress)` only parses the LZ4 **frame** format (magic `04 22 4D 18`). The library cannot round-trip its own stream output.
2. **Hang instead of error.** On non-frame input, `LZ4Stream.Read` spins forever at 100% CPU instead of throwing — so the engine's "corrupt chunk → log warning → regenerate" recovery path never triggers.

**Why loads used to work:** pre-existing saves were written by the previous LZ4 implementation in frame format, which the decompressor DOES parse. The first re-save/migration through NativeCompressions (e.g. the v7 migration of `Test Ocean`, 2026-06-11 18:05) converted payloads to the unreadable block format, bricking the world.

**Evidence:**

- `Test Ocean_Backup_v7_20260611_180526/Region/r.0.2.bin` chunks: `04 22 4D 18 ...` (frame, valid — Python `lz4.frame.decompress` succeeds, 131,630 bytes, chunk version 4).
- Live `Test Ocean/Region/r.0.2.bin` chunks: `06 03 00 00 BF 07 ...` (block-size-prefixed, all 1015 chunks).
- Standalone repro: compress 1,900 bytes via `LZ4Stream` → 45 bytes starting `25 00 00 00`; reading it back with `LZ4Stream(Decompress)` spins a pwsh process at 100% CPU indefinitely.

**Regression origin:** the **0.6.0 → 0.6.1 upgrade** (done 2026-06-10 alongside the Android runtime additions). Verified by standalone round-trip tests of both versions' DLLs outside Unity:

| Version | `LZ4Stream(Compress)` writes          | Round-trips own output?       |
|---------|---------------------------------------|-------------------------------|
| 0.6.0   | LZ4 **frame** (`04 22 4D 18 ...`)     | ✅ Yes                        |
| 0.6.1   | raw **block** (`[int32 size][block]`) | ❌ Decompressor hangs forever |

**Fix (2026-06-11, confirmed by user):**

1. **Reverted all 6 NativeCompressions packages to 0.6.0** (`Assets/packages.config` +
   `Assets/Packages/` folders rebuilt with preserved `.meta` GUIDs/import settings).
2. **Fail-fast guard:** `CompressionFactory.ValidateLz4FrameMagic` now verifies the LZ4 frame magic before constructing the decompressor. Non-frame payloads throw `InvalidDataException`
   immediately, which `ChunkSerializer.Deserialize` already converts into the
   "corrupt chunk → warn → regenerate" recovery path. A hang of this class cannot recur silently.
3. **Save repair:** live `Test Ocean` (block-format, unreadable) moved aside to
   `Test Ocean_BROKEN_lz4block_20260611`; restored from the intact pre-migration backup
   `Test Ocean_Backup_v7_20260611_180526` (frame format verified). The v7 migration re-ran on next load using the fixed (0.6.0) compressor.

**Follow-ups (tracked in `LIBRARY_BUGS.md` — "NativeCompressions (LZ4) — Version pinned to 0.6.0"):**
do not upgrade past 0.6.0 until fixed upstream; consider the explicit `LZ4Encoder`/`LZ4Decoder`
frame API or switching to `K4os.Compression.LZ4`; any world saved while 0.6.1 was installed (2026-06-10 → 2026-06-11) contains unreadable chunks that now regenerate instead of hanging.

---

### ~~04. `ChunkSerializer.Serialize` throws `ObjectDisposedException` with `CompressionAlgorithm.None` (worlds on None could never save)~~

**Severity:** Bug (total save failure for one supported setting)  
**Files:** `ChunkSerializer.cs` — `Serialize`, `CompressionFactory.cs` — `CreateOutputStream`  
**Fixed:** 2026-07-22 (found by the CP-3 `Validate Deserialization Robustness` suite's first run)

**Symptom:** Every chunk save with `saveCompression = None` threw
`ObjectDisposedException: Cannot access a closed Stream` and wrote nothing — a world configured with the documented "raw bytes" debugging algorithm could not persist any chunk.

**Root Cause:** For `None`, `CompressionFactory.CreateOutputStream` returns the destination
`MemoryStream` **itself** (there is no wrapper to create), but `Serialize` disposed the returned stream in a `using` despite passing `leaveOpen: true` — closing the MemoryStream before the
`memoryStream.Position` read that computes the payload length. Deflate/LZ4 were unaffected (their wrappers are separate objects, and disposing them is required to flush).

**Fix:** `Serialize` now mirrors `Deserialize`'s existing identity guard: the compression stream is disposed in a `finally` only when it is not the underlying MemoryStream (`if (compressionStream != memoryStream) compressionStream.Dispose();`), preserving the dispose-to-flush behavior for real compression wrappers. Guarded by the Deserialization Robustness suite (B1 round-trips with `None`).

---

## User Interface

### ~~02. Shortcut Info Panel~~

**Severity:** Feature  
**Files:** `HelpMenuController.cs`, `PauseMenuController.cs`  
**Fixed:** May 2026

**Symptom:** Users did not know about keyboard shortcuts (F3 Debug Screen, F6 Noclip, etc.).  
**Fix:** Implemented a Help menu accessible from the pause screen that displays all available keyboard shortcuts and controls.

---

### ~~03. Block Name Visibility~~

**Severity:** Feature  
**Files:** `BlockTooltipBuilder.cs`, `UIItemSlot.cs`, `Settings.cs`  
**Fixed:** May 2026

**Symptom:** No block name displayed when hovering over items in the hotbar or inventory.  
**Fix:** Implemented a configurable tooltip system with three detail levels (Name Only, Standard, Technical) controlled by `Settings.itemTooltipDetail`. Tooltips appear on hover via `TooltipTrigger` and `TooltipPanel`.

---

### ~~04. Settings Page Overhaul~~

**Severity:** Feature  
**Files:** `SettingsUIGenerator.cs`, `SettingFieldAttribute.cs`, `SettingsMenuController.cs`, `SettingsUIPrefabLibrary.cs`  
**Fixed:** May 2026

**Symptom:** Settings page was minimal with no tabs or advanced configuration.  
**Fix:** Built a reflection-based auto-generated Settings UI. Fields annotated with `[SettingField(tab)]` are automatically discovered, sorted, and rendered as the appropriate control type (Toggle, Slider, Dropdown, InputField, Button). Supports 7 tabs (General, Controls, Graphics, World, Performance, Benchmark, Dev), `[Header]` sections, tooltips, `DebugOnly` visibility gating, and `[InitializationField]` locking during gameplay.

---

### ~~06. In-game Pause Screen~~

**Severity:** Feature  
**Files:** `PauseMenuController.cs`, `WorldUIManager.cs`  
**Fixed:** May 2026

**Symptom:** No way to pause, access settings, or quit to main menu during gameplay.  
**Fix:** Implemented `PauseMenuController` with Resume, Settings (in-game mode with locked initialization fields), Help, Save & Quit to Main Menu, and Save & Quit to Desktop options. Settings menu reuse is handled via `SettingsMenuController.IsInGame` flag.

---


### ~~06. UI Blur Panels Are Opaque and Cannot Stack~~

**Severity:** Bug  
**Reported:** August 2026  
**Fixed:** August 2026  
**Files:** `Assets/Shaders/MaskedUIBlur.shader` (root cause), `Assets/Scenes/World.unity` (five authored
colors), `Assets/Scripts/UI/WorldUIManager.cs` (Escape chain),
`Assets/Scripts/Benchmarks/BenchmarkUIBuilder.cs` (same defect, built in code)

**Symptom:**

Every `Custom/MaskedUIBlur` panel rendered **fully opaque**, regardless of the alpha it was authored
with. Two visible consequences:

1. **A blurred panel hid whatever UI was beneath it.** With the pause menu open, the creative inventory
   was invisible — not closed, *painted over* by the full-screen pause backdrop.
2. **Blurred panels could not stack.** An opaque blur panel is a hole back to the pre-UI frame: it
   replaces those pixels with world blur instead of compositing over what is already there. The
   benchmark HUD therefore appeared to float *above* the pause menu, showing un-dimmed world where the
   surrounding screen was dimmed.

**Repro:** open the creative inventory, then open the pause menu — the inventory vanished. Or run a
benchmark and pause mid-run — the HUD strip stayed visibly lighter than the dimmed screen around it.

**Root cause:**

`MaskedUIBlur.shader` declared `appdata_t` with only `POSITION` and `TEXCOORD0` — there was **no
`COLOR` semantic**, so the UI vertex color was structurally unreachable by the shader. Output alpha came
solely from `_MainTex.a`, and all five scene panels have `m_Sprite: {fileID: 0}`, so `_MainTex` was the
default white texture and alpha was **always 1**. The panels were nonetheless authored
`m_Color = (0, 0, 0, 0.72)` — the classic "dim the screen 72 %" overlay, silently promoted to 100 % by
the shader. The shader also declared no `_Stencil*` and no `UNITY_UI_CLIP_RECT` / `_ClipRect`, so a
blurred graphic inside a `Mask` or `RectMask2D` did not clip at all.

The stacking half had a second contributing cause: `UIBlurRendererFeature` captures `_UIBlurTexture` at
`RenderPassEvent.AfterRenderingTransparents`, **before any ScreenSpaceOverlay canvas draws**. Every blur
panel in the frame samples the same UI-free snapshot, so an opaque one cannot show anything drawn
between it and the world. That half is intrinsic to a single-capture design and was never going to be
fixed by compositing.

**Fix:**

- **Shader** (`36b74204`): gave `MaskedUIBlur` the standard UI material surface — a `COLOR` semantic and
  full RGBA vertex-color multiply, `_Stencil*` + `_ColorMask` (so `Mask` works), `UNITY_UI_CLIP_RECT` +
  `_ClipRect` via a locally-declared `Get2DClipping` (so `RectMask2D` works), and `UNITY_UI_ALPHACLIP`.
  Interpolators went 2 → 3 (4 with clipping), still within `#pragma target 3.5`.
- **Scene** (`36b74204`): re-authored the five panel colors from `(0, 0, 0, 0.72)` to opaque white. The
  black RGB was a leftover from the Built-in-RP GrabPass version and would have rendered a solid black
  panel once vertex color started multiplying.
- **Policy, not translucency, for the two stacking symptoms.** Every panel ships at **alpha 1**: alpha is
  not transparency here but the fraction of *un-blurred* screen that leaks through, so lowering it
  destroys the frost without revealing the UI underneath. Instead:
    - the inventory-behind-pause symptom was closed by the Escape chain (`8c002371`) — Escape now
      dismisses the inventory first and only opens the pause menu on a second press, which together
      with the existing `!IsPauseMenuOpen` gate on the inventory toggle makes "both open at once"
      unreachable;
    - the benchmark-HUD symptom was closed by sorting order (RUF-2) — the HUD canvas moved from
      `sortingOrder` 100 to **-10**, below the scene UI canvas, so full-screen menus cover it.
- **Validation** (`36b74204`): a new rendered-pixel suite, `Minecraft Clone/Dev/Validate UI Blur Render`
  (5 baselines), pins the consumer's compositing contract — vertex-color tint and fade, both material
  tints, and clip-rect exclusion — independently of the producer.

**Lessons that outlived the fix:** the authoring rules this bug established live in
[`../Architecture/UI_BLUR_BACKDROP_SYSTEM.md`](../Architecture/UI_BLUR_BACKDROP_SYSTEM.md) §4 (alpha is
sharp-bleed, not transparency; material colors are gamma-converted but vertex colors are not; RGB must
be white). The harness also proved that `Graphics.DrawMeshNow` silently draws nothing depending on
ambient GL state — it passed standalone and drew nothing inside `Validate All` — so editor render
harnesses must use an explicit `CommandBuffer`.

**Distinct from `#05`,** which concerns the blur *producer* and its resolution dependence, and remains
open.

---
