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

**Fix:** Implemented a write-through cache (`NativeHashMap<long, uint>`) inside `Execute()`. `SetLight` writes to the cache for neighbor positions; `GetPackedData` checks the cache before the ReadOnly arrays. This ensures all light changes within a single job execution are
immediately visible to subsequent reads.

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

**Symptom:** Internal chunk section voxel counts get desynchronized because light-only air blocks were being counted as solid.
**Root Cause:** When the lighting job result contains non-zero voxel data for a null section (light in previously empty air), a new `ChunkSection` is created and populated. `RecalculateCounts` counted these air voxels as non-air because `data != 0`.
**Fix:**

1. `RecalculateCounts` and `RecalculateNonAirCount` now check for solidity using `(data & ID_MASK) != 0` to properly ignore light-only air voxels.
2. Added an `isNewSection` flag to `ApplyLightingJobResult` to completely skip `RecalculateCounts` for freshly created light-only sections, as their pool-reset counts `(0, 0)` are already correct.

---

### ~~07. `ProcessLightingJobs` logs every frame when any dropped updates exist~~

**Severity:** Improvement  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs`  
**Fixed:** March 2026

**Symptom:** Performance hitching and extreme console log spam.
**Root Cause:** The logging statement fired every frame that `_droppedLightUpdates` had entries. Since the collection is rebuilt each iteration, in busy worlds this effectively logged every frame.
**Fix:** Gated the log behind `enableDiagnosticLogs`, replaced LINQ `.Sum()` with a manual loop to prevent allocations, and removed the unused `using System.Linq` directive.

---

### ~~08. Underwater cross-chunk shadow wall artifacts~~

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs`  
**Fixed:** April 2026

**Symptom:** Generating new chunks next to water bodies (or logging in dynamically) caused a 1-voxel wide vertical "wall of shadow" at the exact chunk boundary spanning beneath the water surface, despite correct lighting values everywhere else.

**Root Cause:** A data race condition in chunk boundary lighting. When a chunk ran its lighting job, the `RecalculateSunlightForColumn` correctly evaluated direct downward light (e.g., `sunlight=15` through water).
However, the neighboring chunk's lighting job (running asynchronously or later) evaluated the border block horizontally using BFS on a *stale snapshot* of the chunk (where the original sunlight value was `0`).
This BFS evaluated a weakened light value (`15 - 1 distance - 2 water opacity = 12`) and generated a `CrossChunkLightMod`. This cross-chunk mod then overwrote the correct column value back on the main thread because the guard checking cross-chunk modifications was too narrow.

**Fix:** Broadened the safeguard in `ProcessLightingJobs` (`WorldJobManager.cs` line 518). Replaced the rigid `heightmap` check with a general principle:
cross-chunk BFS modifications that evaluate a non-zero sunlight level *must never lower* the current target's sunlight level. This correctly delegates authoritative column values to the chunk that actually owns them.

---

### ~~09. Diagonal Shadow Artifacts on Smooth-Lit Legacy Rotated Blocks~~

**Severity:** Low (cosmetic)
**Fixed:** June 2026
**Status:** Resolved

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
Added the missing opaque-source guard at the top of `PropagateLightRGB`, mirroring the sunlight path — with an exemption for emissive opaque blocks (lamps/glowstone must radiate their own emission): `if (sourceProps.IsOpaque && !sourceProps.IsLightSource) return;`. The `ModifyVoxel` wake-up loop was left unchanged (the job-side guard is the robust fix since wake nodes can also originate from cross-chunk applies and serialized queues). Baseline scenario **B9** asserts the containment invariant (no blocklight anywhere below the floor's surface layer
across the whole grid); it deliberately did not run a full oracle compare because the same edit also tripped Bug 07's cross-border removal/re-spread loss — the full-field oracle compare was restored after the Bug 07 fix (June 2026).

---

### ~~11. Cross-chunk emissive sources produce a hard cut-off (or flicker) at the chunk border~~

**Severity:** High
**Fixed:** June 2026 (was Bug 07 in `LIGHTING_BUGS.md`)
**Status:** Resolved — confirmed in-game (the hard cut-off no longer reproduces in a new world; the flicker required the cut-off defect, so it is resolved with it). Guarded by validation suite baselines **B10/B11/B12** (promoted from known-bug repro scenarios K07a/K07b/K07c); tripwire baseline **B7** (the blocklight removal race that depended on the old force-clear) stayed green through the fix.
**Confidence:** Confirmed — the harness reproduced all three reported symptoms through the real job + the shared mod-apply logic, including light corruption stamped into opaque floor voxels during the ping-pong.
**Files:** `NeighborhoodLightingJob.cs` — `Execute` (BlocklightBfsQueue seeding), `PropagateDarknessRGB`, `PropagateDarkness`, `PropagateLightRGB`, `CheckEdgeVoxelRGB`; `Helpers/CrossChunkLightModApplier.cs` — `ComputeBlocklight`; `WorldJobManager.cs` — `ProcessLightingJobs` (blocklight mod application)

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
neighbor is unloaded), and the harness has no unload/save/load mirror.
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (defer/drain, per-channel dropped-mod handling), `ApplyCrossChunkLightMod`, `DrainDeferredCrossChunkMods`, `DegradeDeferredCrossChunkMods`; `Serialization/LightingStateManager.cs` — pending-blocklight store + `pending_blocklight.bin`; `World.cs` — `LoadOrGenerateChunk` replay; `Editor/Validation/Lighting/Framework/LightingTestWorld.cs` — defer/drain mirror

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

**Severity:** Medium
**Fixed:** June 2026 (was Bug 06 in `LIGHTING_BUGS.md`)
**Status:** Resolved — confirmed in-game in a freshly generated world (the red-debug-lamp-as-forest-surface setup: generated lamps now illuminate their surroundings immediately at generation time). Guarded by validation suite baseline **B14** (promoted from known-bug repro scenario K06).
⚠️ *Old-world caveat:* worlds saved BEFORE this fix already carry the stamped-but-unpropagated lamp voxels in their light data, so the fix's trigger (stored light below emission) never fires for them — those lamps stay dark until a nearby block update wakes them, exactly the pre-fix behavior. Only newly generated chunks are healed.
**Confidence:** Confirmed — reproduced in-game (June 2026) by setting the forest biome's surface block to the red debug lamp: no block lighting was generated at all (the masking by fluid-simulation light updates only applies to flowing lava).
**Files:** `NeighborhoodLightingJob.cs` — `SyncEmissionToLightArray`, `Execute` (queue seeding); `Chunk.cs` — `OnDataPopulated`; `World.cs` — initial lighting scheduling (`RecalculateSunLightLight`)

**Description:**
A chunk's initial lighting pass seeded only **sunlight**: `RecalculateSunLightLight()` enqueues all 256 columns into the sunlight recalc queue, and the `BlocklightBfsQueue` is empty for a freshly generated chunk. Inside the job, `SyncEmissionToLightArray` stamped each emissive block's RGB emission into its own `LightMap` cell — but **never enqueued those positions into the blocklight placement queue**, so the emission was not propagated to surrounding voxels. `Chunk.OnDataPopulated` only registers active voxels; it does not queue light updates either.

**Impact (user-confirmed in game):** A generated emissive block illuminated only its own voxel; surrounding air stayed dark until *some* block update near it woke the BFS (e.g. lava flow `ModifyVoxel` calls, or a player edit). Confirmed with the red debug lamp as a biome surface block; confined non-flowing lava pools and future emissive blocks in structures (glowstone etc.) hit the same gap.

**Fix (June 2026):** `SyncEmissionToLightArray` now takes the job-local blocklight placement queue and enqueues every position whose emission it stamps, so the stamped emission propagates within the same job (and reaches neighbors via the normal cross-chunk mods). The stamp condition (stored light below emission) is self-limiting: once propagated, the voxel holds at least its emission, so later job runs neither stamp nor enqueue — zero steady-state overhead for emissive-dense chunks (lava oceans). The index→position conversion runs only on the rare stamp
path, keeping the scan linear.

---

### ~~14. Cross-chunk edge check leaks light out of opaque border blocks~~

**Severity:** Low-Medium
**Fixed:** June 2026 (was Bug 10 in `LIGHTING_BUGS.md`)
**Status:** Resolved — confirmed via the validation suite (repros flipped red→green, all baselines green). Promoted to validation baselines **B43** (sunlight) and **B44** (blocklight) from known-bug repros K10a/K10b.
**Confidence:** Confirmed by the validation framework; not separately reproduced in-game, but **no regression observed in-game** after the fix. Likely in-game manifestation: the diagonal over-bright / light-decrease band seen along chunk borders **at world height** in the ChunkBorder debug VoxelVisualization mode (the opaque heightmap surface sits exactly on the borders there, so its surface light leaked one voxel across each border — producing the cross-border diagonal pattern).
**Found by:** the Bug-05 dense-canopy geometry fuzz (`LightingValidationSuite.Bug05Canopy.cs`), whose opaque under-canopy dividers sit on chunk borders and triggered the leak — the fuzz did not reproduce Bug 05 but surfaced this distinct defect.
**Files:** `NeighborhoodLightingJob.cs` — `CheckEdgeVoxel`, `CheckEdgeVoxelRGB` (+ call sites in `CheckEdges` and `PropagateDarkness`)

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

**Severity:** Medium-High
**Fixed:** June 2026 (was Bug 11 in `LIGHTING_BUGS.md`)
**Status:** Resolved — confirmed in-game (the world that stalled now loads quickly with no stuck-light logging) AND via the validation suite (repro flipped red→green, all baselines green). Promoted to validation baseline **B48** from known-bug repro K11a.
**Confidence:** Confirmed in-game and by the validation framework.
**Found by:** a user-reported `ForceCompleteDataJobsCoroutine exceeded max iterations` error on reloading a recently-played world; root-caused with the gated `[LightingDiag]` startup-convergence instrumentation added to `World.ForceCompleteDataJobsCoroutine` / `WorldJobManager.ProcessLightingJobs`.
**Files:** `CrossChunkLightModApplier.cs` (`ComputeSunlight`, new `InChunkSunlightSupport`), `WorldJobManager.cs` (`ApplyCrossChunkLightMod` apply site + diagnostics), `World.cs` (startup convergence diagnostics).

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

## Fluid

### ~~01. Cross-chunk fluid simulation stops at chunk borders~~

**Severity:** Bug  
**Files:** `Chunk.cs` — `Reset`, `World.cs` — `ApplyModifications`  
**Fixed:** March 2026

**Symptom:** Water would flow one block into a neighboring chunk and then stop, leaving a dead-end fluid tile that did not continue spreading.

**Root Cause:** During the initial world load (`StartWorld`), `ChunkData` for a chunk could be fully populated before the corresponding `Chunk` GameObject was retrieved from the pool. `Chunk.Reset()` did not call `OnDataPopulated()` when the data was already populated, so active
voxels (e.g., water source blocks) placed during generation were never registered in `_activeVoxels`, and fluid simulation never started for them.

**Fix:**

- Added a check in `Chunk.Reset()`: if `ChunkData.IsPopulated` is already true when the chunk is reset, `OnDataPopulated()` is called immediately.
- The noisy `[FLUID DEBUG]` warning in `World.ApplyModifications()` is now gated on `_isWorldLoaded` to suppress expected noise during the initial load phase.

---

### ~~02. Downward flow creates infinite source blocks (waterfalls don't drain)~~

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow`, `Active`; `FluidDataGenerator.cs`; `FluidMeshData.cs`  
**Fixed:** March 2026

**Symptom:** Placing a single water source block at height Y and then removing it left the entire waterfall column permanently, because every block in the column had become an independent infinite source. Additionally, even after an initial fix (FluidLevel=1), falling water
spread ~60 blocks on landing because level 1 still allowed 6 levels of horizontal spread.

**Root Cause:** `HandleFluidFlow` Rule 1 created downward flow without properly encoding the upstream level. In Minecraft Beta 1.3.2, falling fluid uses metadata `>= 8` (the "falling flag"), with the lower 3 bits carrying the effective upstream level. When a falling block lands,
horizontal spread starts from `effectiveLevel + 1`, not from level 1.

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

1. **Mathematical Symmetry:** Exploited `GetSmoothedCornerHeight` to pre-calculate the exact surface heights of shared neighbor edges.
   If a fluid block is adjacent to another, it now draws a "curtain" face extending *only* from the neighbor's sloped surface height up to our surface height, erasing geometry that would have plunged internally into the neighbor's volume.
2. **Submerged Culling:** Expanded the Burst neighbor lookup to include `above_N, above_S, etc.`. If both the current block and the neighbor are submerged (`neighborHasFluidAbove`),
   their top edges mathematically align at `1.0f`. There is zero gap to fill, so the face is completely culled.
3. **Waterfall Preservation:** Added an `isFullHeight` fallback. Waterfalls erupting from under solid stone ceilings (`hasFluidAbove == false` but `template == 1.0f`) now correctly recognize their full height,
   overriding the shallow puddle cull to draw a perfect gap-filler face down to the surface below.

---

### ~~05. 7x7 Horizontal Spreading Cube in Mid-Air~~

**Severity:** Gameplay / Physics bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** When a source block is placed on top of an elevated surface (like a tree), the fluid flows outwards and sometimes incorrectly spawns horizontal spreading blocks in mid-air (forming a floating 7x7 water grid) instead of accurately checking if those spread locations
have ground support below them.

**Root Cause:** The `isSupportedBelow` check during horizontal spreading incorrectly allowed fluid to spread if the block below was the *same fluid type*, regardless of whether it was falling or solid. Additionally, falling waterfall columns and pulsating decay loops contributed
to broken spread distances.

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
3. If `>= 2` native source blocks (level 0) are horizontally adjacent, and the block below is either solid ground or another level 0 source block,
   the block organically overrides its target decay to `0`, successfully generating an infinite source pool without extra overhead or allocations.

---

### ~~08. Missing Lava Viscosity Randomization~~

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`  
**Fixed:** March 2026

**Symptom:** Lava spread horizontally at the exact same deterministic rate as water, lacking its thick, random flow pattern.

**Root Cause:** Fluid flow evaluation always proceeded at 100% chance for all fluids.

**Fix:** Added a `spreadChance` configuration `float` (0.0 - 1.0) to `BlockType.cs` and exposed it in the `BlockEditorWindow`. Inside `HandleFluidSpread`, `UnityEngine.Random.value` is rolled against this chance; if it fails, the horizontal spread step is cleanly aborted for that
tick.

---

### ~~10. Missing Dynamic Flow Direction Texturing~~

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`  
**Fixed:** March 2026

**Symptom:** Water did not visually animate in the direction it was physically spreading, relying on a static world-space liquid shader.
**Fix / Implementation:** Implemented flow derivative math in `VoxelMeshHelper.cs` (`CalculateCornerFlow` and `ProjectFlowToSideFace`).
This calculates a 2D XZ flow vector based on surrounding fluid height differentials and maps it to the UV channels of the generated mesh.
The `UberLiquidShader` receives this vector (`localFlowVector`) and uses a dual-phase crossfading technique to scroll the noise and simulate continuous directional flow.
*(Note: As tracked in FLUID_BUGS.md #16, this is currently functionally operational but the math could be further refined in the future to eliminate surface stretching.)*

---

### ~~11. Missing Unique Textures for Falling Fluids (Waterfalls)~~

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`  
**Fixed:** March 2026

**Symptom:** Falling fluid blocks (waterfalls) used the exact same visual properties as resting horizontal fluids.
**Fix / Implementation:** In `VoxelMeshHelper.cs`, the mesh generation job now explicitly checks if the voxel has the falling flag (`fluidLevel >= 8`).
If true, instead of calculating complex horizontal flow math, it bypasses the evaluation and forces a strict, high-velocity downward flow vector (`new Vector2(0f, 1.5f)`).
The `UberLiquidShader` natively interprets this large V-axis vector, resulting in a fast-scrolling, distinct vertical waterfall effect that properly blends with horizontal pools at its base.

---

### ~~06. Missing Optimal Flow Direction Pathfinding~~

**Severity:** Missing Feature  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Water spread outward in a uniform diamond shape, oblivious to nearby holes or drops.
**Root Cause:** The simulation lacked Minecraft's recursive `calculateFlowCost` terrain scanner.
**Fix:** Injected a dot-net 2.1 zero-allocation Breadth-First-Search iterative pathfinder using Unity's `NativeQueue` and bitmasks to determine the optimal downhill path.

---

### ~~09. Severed waterfalls cause infinite decay loops~~

**Severity:** Bug  
**Files:** `BlockBehavior.Fluids.cs`  
**Fixed:** March 2026

**Symptom:** Breaking a source block with a waterfall beneath it left floating, non-decaying waterfall columns that indefinitely supplied water to adjacent blocks.
**Root Cause:** `CalculateExpectedFluidLevel` allowed orphaned waterfall blocks to act as level-0 support for each other, establishing a self-sustaining loop.
**Fix:** Added an `isFedFromAbove` check. Now, if a falling fluid block is cut off from the stream above, it immediately decays to air or regular decaying fluid, ending the loop.

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

**Severity:** Improvement (performance) & Bug
**Files:** `Chunk.cs` — `PlayChunkLoadAnimation`, `ChunkLoadAnimation.cs`
**Fixed:** March 2026

**Root Cause:** `GetComponent<ChunkLoadAnimation>()` was called on each pool activation. A primitive boolean flag was previously attempted, but it could become stale if the component were destroyed.
Furthermore, applying animations from the pool caused a 1-frame visual flash, and subsequent chunk modifications re-triggered the upward animation natively since it hooked into mesh generation.

**Fix:**

- Used a cached `_loadAnimation` reference inside `Chunk.cs` utilizing Unity's overriden `== null` safety check rather than a primitive boolean flag.
- Refactored `ChunkLoadAnimation` to take an absolute target position (`ResetToUnderground`) to prevent infinite vertical offsets.
- Added a `_hasPlayedLoadAnimation` flag to `Chunk.cs` to guarantee animations don't re-trigger when local voxel modifications request a mesh rebuild.

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

## World Generation & Data

### ~~02. `ProcessGenerationJobs` always uses `biomes[0]` for flora generation~~

**Severity:** Bug
**Files:** `WorldJobManager.cs` -> (Now handled by `IChunkGenerator.ExpandFlora`)
**Fixed:** April 2026

**Symptom:** In a multi-biome world, trees in non-default biomes received incorrect height distributions because the code hardcoded `_world.biomes[0].minHeight`.
**Root Cause:** During chunk generation, tree flora placement logic always evaluated the 0-th index biome definition rather than checking the local biome at the tree's physical coordinates.
**Fix:** Resolved during the Modular World Generation refactor. `WorldJobManager` now delegates this to `IChunkGenerator.ExpandFlora()`, which internally resolves the correctly mapped biome per local coordinate before handing off constraints to the `Structure` generator.

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

**Symptom:** Yielded incorrect chunk boundary voxel references for negative world coordinates.
**Root Cause:** The method used an `(int)` cast which truncates toward zero, producing wrong results for negative coordinates.
**Fix:** Both `x` and `z` components now use `Mathf.FloorToInt()`. The `y` component retains the `(int)` cast since Y is always non-negative.

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

**Symptom:** Race condition if block behavior is evaluated across multiple threads, leading to data corruption as `Behave()` overwrites the returned reusable metadata list.
**Root Cause:** The `Behave` method used a single shared static `List<VoxelMod>` (`s_mods`) that was cleared and reused on every call.
**Fix:** Replaced the single static list with a `[ThreadStatic]` lazily instantiated backing list mapped to a `Mods` property. This guarantees every thread hitting `Behave()` gets its own private, zero-allocation reusable list.

---

## Player & Input

### ~~01. Collision only checks at two height levels (feet and +1)~~

**Severity:** Bug  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`
**Fixed:** March 2026

**Root Cause:** The horizontal collision properties only checked at two Y levels.
**Fix:** Encapsulated player physics into `VoxelRigidbody` and implemented dynamic AABB sweeping across the entity's full `entityHeight`.

---

### ~~02. Collision checks don't account for player width in the cross-axis~~

**Severity:** Bug  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`
**Fixed:** March 2026

**Root Cause:** Each directional collision check sampled a single line, causing clipping on diagonal movement.
**Fix:** `VoxelRigidbody` now sweeps an axis-aligned bounding box face dynamically.

---

### ~~03. Raycast-based block placement can be incorrect on exact voxel edges~~

**Severity:** Bug (latent)  
**Files:** `PlayerInteraction.cs` — `RaycastForVoxel`
**Fixed:** March 2026

**Root Cause:** The block placement used modulo `% 1` which failed on negative coordinates.
**Fix:** Rewrote `RaycastForVoxel` to use a robust previous-step integer coordinate tracking method.

---

### ~~04. Block placement overlap check only covers 2 voxels of player height~~

**Severity:** Bug  
**Files:** `PlayerInteraction.cs` — `PlaceCursorBlocks`
**Fixed:** March 2026

**Root Cause:** The placement validity check only checked `y` and `y+1`, ignoring actual `playerHeight`.
**Fix:** Replaced hardcoded checks with a dynamic AABB intersection check against the placed voxel's bounds.

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
`ChunkSerializer.Deserialize` and never return (`finished=0`); the ThreadPool injects ~1 new
thread/second, each of which also wedges; CPU climbs until the system is unresponsive.

**Root cause (two stacked library defects, reproduced OUTSIDE Unity in a 3-line repro):**

1. **Asymmetric formats.** `LZ4Stream(CompressionMode.Compress)` writes raw **block** format
   (`[int32 blockSize][LZ4 block]`, e.g. `25 00 00 00 FF 04 ...`), but
   `LZ4Stream(CompressionMode.Decompress)` only parses the LZ4 **frame** format
   (magic `04 22 4D 18`). The library cannot round-trip its own stream output.
2. **Hang instead of error.** On non-frame input, `LZ4Stream.Read` spins forever at 100% CPU
   instead of throwing — so the engine's "corrupt chunk → log warning → regenerate" recovery
   path never triggers.

**Why loads used to work:** pre-existing saves were written by the previous LZ4 implementation in
frame format, which the decompressor DOES parse. The first re-save/migration through
NativeCompressions (e.g. the v7 migration of `Test Ocean`, 2026-06-11 18:05) converted payloads
to the unreadable block format, bricking the world.

**Evidence:**

- `Test Ocean_Backup_v7_20260611_180526/Region/r.0.2.bin` chunks: `04 22 4D 18 ...` (frame, valid —
  Python `lz4.frame.decompress` succeeds, 131,630 bytes, chunk version 4).
- Live `Test Ocean/Region/r.0.2.bin` chunks: `06 03 00 00 BF 07 ...` (block-size-prefixed, all 1015 chunks).
- Standalone repro: compress 1,900 bytes via `LZ4Stream` → 45 bytes starting `25 00 00 00`;
  reading it back with `LZ4Stream(Decompress)` spins a pwsh process at 100% CPU indefinitely.

**Regression origin:** the **0.6.0 → 0.6.1 upgrade** (done 2026-06-10 alongside the Android
runtime additions). Verified by standalone round-trip tests of both versions' DLLs outside Unity:

| Version | `LZ4Stream(Compress)` writes          | Round-trips own output?      |
|---------|---------------------------------------|------------------------------|
| 0.6.0   | LZ4 **frame** (`04 22 4D 18 ...`)     | ✅ Yes                        |
| 0.6.1   | raw **block** (`[int32 size][block]`) | ❌ Decompressor hangs forever |

**Fix (2026-06-11, confirmed by user):**

1. **Reverted all 6 NativeCompressions packages to 0.6.0** (`Assets/packages.config` +
   `Assets/Packages/` folders rebuilt with preserved `.meta` GUIDs/import settings).
2. **Fail-fast guard:** `CompressionFactory.ValidateLz4FrameMagic` now verifies the LZ4 frame
   magic before constructing the decompressor. Non-frame payloads throw `InvalidDataException`
   immediately, which `ChunkSerializer.Deserialize` already converts into the
   "corrupt chunk → warn → regenerate" recovery path. A hang of this class cannot recur silently.
3. **Save repair:** live `Test Ocean` (block-format, unreadable) moved aside to
   `Test Ocean_BROKEN_lz4block_20260611`; restored from the intact pre-migration backup
   `Test Ocean_Backup_v7_20260611_180526` (frame format verified). The v7 migration re-ran
   on next load using the fixed (0.6.0) compressor.

**Follow-ups (tracked in `LIBRARY_BUGS.md` — "NativeCompressions (LZ4) — Version pinned to 0.6.0"):**
do not upgrade past 0.6.0 until fixed upstream; consider the explicit `LZ4Encoder`/`LZ4Decoder`
frame API or switching to `K4os.Compression.LZ4`; any world saved while 0.6.1 was installed
(2026-06-10 → 2026-06-11) contains unreadable chunks that now regenerate instead of hanging.

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

