# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–04) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

## Bug 05: Persistent Chunk-Border Shadow Patches in Dense Biomes

**Severity:** Medium
**Status:** Open / Partially mitigated

**Description:**
Persistent dark/shadow patches occur primarily in visually dense areas (e.g., repeating overlapping tree canopies in forest biomes), typically originating near the borders between freshly generated chunks.
While cave systems spanning chunk borders are now correctly lit with the iterative edge-checking rounds fix, dense forested areas still occasionally fail to converge and leave permanent shadow patches that only resolve upon a full world reload
or partially resolve upon forcing light updates (eg: Removing leaves blocks).

**Root Cause Suspected:**
During generation, if two chunks both stabilize their initial lighting passes with stale neighbor snapshot data (e.g., both border columns report 0 sunlight), the standard `CheckEdges` logic doesn't register a discrepancy to correct.
We implemented iterative `RemainingEdgeCheckRounds = 2` to combat this, but dense overhead coverage appears to require more passes or a different convergence triggering algorithm across multi-chunk bounds.

**Additional observations (June 2026 audit):**

- `CheckEdges` only validates the **4 cardinal borders**. Stale light originating in a *diagonal* neighbor can only reach the center chunk via two hops of cardinal edge checks, consuming both `RemainingEdgeCheckRounds` for a single corner correction. Dense canopies (small light pockets that depend on multi-chunk diagonal paths) are exactly the case where 2 rounds may not converge. Possible cheap mitigation: include the 4 corner voxel columns (which already have diagonal snapshot data available in the job) in `CheckEdges`.
- `CheckEdgeVoxel` can only **add** missing light — by design it never detects over-bright stale values. That direction is fine for shadow patches, but worth remembering when debugging the inverse artifact (light patches that refuse to darken until a manual update).

**Validation suite (June 2026):** a minimal repro attempt — 5×5 grid, full slab with a single *diagonal* sky well, all chunks lit in one concurrent wave with stale snapshots plus the production 2 edge-check rounds — **converges to the oracle** and now guards as baseline scenario **B8**. A faithful failing repro (denser multi-pocket canopies / different mod-loss timing) is still TODO before this bug's fix can be test-driven.

---

## Bug 06: Generated emissive blocks never seed the blocklight BFS (initial lighting)

**Severity:** Medium
**Status:** Open / Reproduced deterministically in the validation suite (scenario **K06**: a generation-written lamp's own voxel holds its emission but the neighbor voxel stays at (0,0,0) after initial lighting)
**Confidence:** High — the seeding gap is confirmed behaviorally in the editor harness; the in-game impact may still be masked by the fluid simulation re-triggering light updates on lava flow.
**Files:** `NeighborhoodLightingJob.cs` — `SyncEmissionToLightArray`, `Execute` (queue seeding); `Chunk.cs` — `OnDataPopulated`; `World.cs` — initial lighting scheduling (`RecalculateSunLightLight`)

**Description:**
A chunk's initial lighting pass seeds only **sunlight**: `RecalculateSunLightLight()` enqueues all 256 columns into the sunlight recalc queue, and the `BlocklightBfsQueue` is empty for a freshly generated chunk. Inside the job, `SyncEmissionToLightArray` stamps each emissive block's RGB emission into its own `LightMap` cell — but **never enqueues those positions into the blocklight placement queue**, so the emission is not propagated to surrounding voxels. `Chunk.OnDataPopulated` only registers active voxels; it does not queue light updates either.

**Expected impact:** A generated lava lake (or any future emissive block placed by world gen — glowstone in structures, etc.) initially illuminates only its own voxels; surrounding air stays dark until *some* block update near it wakes the BFS (e.g. lava flow `ModifyVoxel` calls, or a player edit). Confined, non-flowing lava pools — and especially future non-fluid emissive blocks in structures — would stay glow-less until touched.

**Proposed fix:** In `SyncEmissionToLightArray` (or in the seeding section of `Execute`), enqueue every position whose emission was stamped into the blocklight placement queue. This is cheap (the scan already runs) and makes initial lighting self-contained.

---

## Bug 07: Cross-chunk emissive sources produce a hard cut-off (or flicker) at the chunk border

**Severity:** High
**Status:** Open / Reproduced deterministically in the validation suite (scenarios **K07a** two-source border cut-off, **K07b** adjacent-lamp flicker — genuine non-convergence past the round budget, **K07c** cross-border re-spread loss after breaking a source)
**Confidence:** Confirmed — the harness reproduces all three reported symptoms through the real job + the shared mod-apply logic, including light corruption stamped into opaque floor voxels during the ping-pong.
**Files:** `NeighborhoodLightingJob.cs` — `Execute` (BlocklightBfsQueue seeding, ~lines 159–195), `PropagateDarknessRGB` (~line 351), `PropagateDarkness` (~line 309); `WorldJobManager.cs` — `ProcessLightingJobs` (blocklight mod application, ~lines 813–836), `ApplyLightingJobResult` (~lines 977–988)

**Symptoms (user-confirmed in game):**
Placing an emissive block in chunk A directly against the border of chunk B, while chunk B contains its own emissive source whose light bleeds into A, produces a hard cut-off between the two light fields exactly at the chunk border (each side shows only its own chunk's source). Depending on configuration the border can instead flicker indefinitely. After a world reload the two sources blend correctly — until **any** light update near the border re-triggers the artifact. Pre-dates the RGB upgrade; RGB colors just made it visible.

**Root Cause (two compounding defects):**

1. **Cross-chunk uplift mods are re-interpreted as block-removal events by the receiving chunk.**
   When `ProcessLightingJobs` applies a blocklight `LightModification` to a neighbor chunk, it enqueues the BFS wake-up node with the voxel's *real pre-apply* light values: `AddToBlockLightQueue(localVoxelPos, oldLightLevel, oldR, oldG, oldB)` (`WorldJobManager.cs:832`). But the job's seeding logic (`NeighborhoodLightingJob.cs:175–178`) treats **any** node at a non-emissive block with `OldBlock > 0` as "block was broken, ushort retains stale emission" and **force-clears the voxel to (0,0,0)**, then detects `anyDecreased` and launches a darkness wave with
   the old values. The wake-up convention (`OldBlock == 0` ⇒ don't clear) only holds for nodes created by `ChunkData.ModifyVoxel`; cross-chunk applies violate it whenever the target voxel already had light (exactly the two-sources-at-the-border case). Net effect: the uplift from chunk A is wiped before it can spread into B, **and** B's own legitimate light near the border is eaten by a spurious removal wave.

2. **Removal re-spread seeds across the border are dropped.**
   In `PropagateDarknessRGB`, when the darkness wave meets a voxel whose light comes from an *independent* source, that voxel is queued for re-spreading — but only `if (anyRespread && IsInCenterChunk(neighborPos))` (`NeighborhoodLightingJob.cs:351`). If the independent source's light lives across the chunk border, the re-spread seed is silently discarded, so light removed on this side is never restored from the neighbor's contribution. The sunlight path has the identical pattern (~line 309–313). `CheckEdgeVoxelRGB` could heal this, but edge checks only
   run during initial-generation convergence rounds (`RemainingEdgeCheckRounds`), never after player edits.

**Why it flickers:** B's spurious removal wave reaches the border and emits darkness mods back into A; A's next job re-places its own light and emits uplift mods back into B; each uplift is again destructively re-interpreted (defect 1) → mutual ping-pong. `IsStable` is false whenever `CrossChunkLightMods` is non-empty, so both chunks keep rescheduling lighting jobs and rebuilding meshes every round — visible as flicker. When the ping-pong happens to damp out, the residual state is the static cut-off.

**Why a reload looks correct:** the saved light data contains the blended result from initial generation (where edge-check rounds run and merge borders), and the BFS queues are empty on load. The first light update near the border re-enters the destructive cycle.

**Secondary contributor:** the per-channel mod-apply guard (`applyX = mod.BlockX == 0 ? 0 : max(oldX, mod.BlockX)`, `WorldJobManager.cs:824–826`) lets a *zero* channel from a stale-snapshot placement mod pass through as a darkness removal, clearing channels owned by an independent source the emitting job never saw.

**Proposed fix direction:**

- Distinguish node kinds. Add a discriminator to `LightQueueNode` (e.g. `BlockChanged` vs `CrossChunkApply`) so the seeding force-clear only fires for genuine block-change removals. ⚠️ `LightQueueNode` is serialized in chunk save data (`ChunkData.cs:384–385`) — this is a save-format change requiring an AOT migration step + version bump. A format-neutral alternative: enqueue cross-chunk *uplift* applications with `old = (0,0,0)` (wake-up semantics — the light value was already written, so `anyIncreased` still fires) and keep real old values only for mods
  that zeroed a channel.
- For defect 2: when `anyRespread` fires for an out-of-center `neighborPos`, perform a single-voxel pull at `node.Pos` (mirror `CheckEdgeVoxelRGB`: re-add the attenuated neighbor light and enqueue `node.Pos` in the placement queue). Apply the same to the sunlight path.

---

## Bug 08: Broken emissive blocks leave permanent "ghost" blocklight (cross-chunk removal loss)

**Severity:** Medium–High (ghost values get baked into saved region data — permanent world corruption until manually disturbed)
**Status:** Open / Path 2 (in-flight race) reproduced deterministically in the validation suite via the flight API (scenario **K08a**: sunlight uplift lost to a stale merge, receiving chunk stays 3–5 levels darker than the oracle, permanently). Baseline scenario **B7** documents that the *blocklight* removal race currently self-heals through Bug 07's force-clear — B7 must stay green when Bug 07 is fixed, exactly as the analysis below predicts. Path 1 (unloaded-neighbor pending-store degradation) is not yet covered by the suite.
**Confidence:** Confirmed for the in-flight-job race (now deterministic); High for the unloaded-neighbor path (code inspection).
**Files:** `WorldJobManager.cs` — `ProcessLightingJobs` (dropped-mod handling, ~lines 762–784; `ApplyLightingJobResult`, ~lines 977–988); `Serialization/LightingStateManager.cs`; `NeighborhoodLightingJob.cs` — `Execute` sunlight seeding (~lines 147–157)

**Symptoms (user-confirmed in game):**
Breaking an emissive block sometimes leaves its light behind permanently; no later update removes it. Suspected (not yet confirmed) to be cross-chunk related — the analysis below supports that: the ghost data lives in a *neighboring* chunk of the broken block.

**Root Cause (two independent loss paths for removal information):**

1. **Blocklight mods targeting unloaded/unpopulated chunks are degraded to sunlight-only column recalcs.**
   When a cross-chunk mod's target chunk is null or `!IsPopulated`, `ProcessLightingJobs` records only the affected *column* into `_droppedLightUpdates` → `LightingStateManager.AddPending` (`WorldJobManager.cs:762–784`, `:898–904`). That store (`pending_lighting.bin`) feeds `SunlightRecalculationQueue` → `RecalculateSunlightForColumn`, which touches **only the sky channel**. The RGB removal (and uplift) information is permanently discarded. Breaking a lamp whose glow crosses into a chunk that is unloaded — or loaded but not yet populated — leaves that
   chunk's saved light data glowing forever. Note the receiving radius matters: a lamp at a border illuminates up to ~14 voxels into the neighbor, so this triggers easily at render-distance edges and during chunk streaming.

2. **`ApplyLightingJobResult` full-LightMap overwrite races with mods applied during the job's flight.**
   The result merge overwrites the entire ushort light array with the job's output and the comment (`WorldJobManager.cs:979–983`) explicitly accepts that cross-chunk mods applied to live data during the job "may be temporarily lost", deferring to edge-check convergence. But (a) edge checks only run during initial generation (`NeedsEdgeCheck` is only set via `RemainingEdgeCheckRounds`), and (b) `CheckEdgeVoxel`/`CheckEdgeVoxelRGB` are **add-only** — they can never remove over-bright stale light (see Bug 05 notes). A lost *removal* is therefore permanent.
   For **sunlight** the loss is total: the reverted voxel makes the seeded queue node a no-op (`currentLight == node.OldLightLevel` enqueues nothing, `NeighborhoodLightingJob.cs:153–156`). For **blocklight** the node currently survives only because of the force-clear path — which is itself Bug 07's defect; fixing Bug 07 naively would surface this race.

**Proposed fix direction:**

- Path 1: extend the pending-lighting store to persist full blocklight modifications (position + RGB + channel) instead of sun-only columns. ⚠️ Changes the `pending_lighting.bin` format — requires an AOT migration step (`MigratePendingLighting`) + version bump per the serialization rules.
- Path 2: don't apply cross-chunk mods directly to a chunk that has an in-flight lighting job; buffer them per-chunk and apply (data + queue node) after that job's `ApplyLightingJobResult` runs. Alternatively re-apply buffered mods on top of the merged result.
