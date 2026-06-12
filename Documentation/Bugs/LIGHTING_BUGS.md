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

## Bug 08: Broken emissive blocks leave permanent "ghost" blocklight (cross-chunk removal loss)

**Severity:** Medium–High (ghost values get baked into saved region data — permanent world corruption until manually disturbed)
**Status:** Open / Path 2 (in-flight race) reproduced deterministically in the validation suite via the flight API (scenario **K08a**: sunlight uplift lost to a stale merge, receiving chunk stays 3–5 levels darker than the oracle, permanently). Baseline scenario **B7** documents that the *blocklight* removal race self-heals through the seeding force-clear (now per-channel after the Bug 07 fix, June 2026 — B7 stayed green through it, exactly as the analysis below predicted). Path 1 (unloaded-neighbor pending-store degradation) is not yet covered by the
suite.
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
   For **sunlight** the loss is total: the reverted voxel makes the seeded queue node a no-op (`currentLight == node.OldLightLevel` enqueues nothing, `NeighborhoodLightingJob.cs:153–156`). For **blocklight** the node survives via the force-clear path — kept (per-channel, on the block-change signature `cur == old > 0`) through the June 2026 Bug 07 fix precisely so this race keeps self-healing (guarded by baseline **B7**).

**Proposed fix direction:**

- Path 1: extend the pending-lighting store to persist full blocklight modifications (position + RGB + channel) instead of sun-only columns. ⚠️ Changes the `pending_lighting.bin` format — requires an AOT migration step (`MigratePendingLighting`) + version bump per the serialization rules.
- Path 2: don't apply cross-chunk mods directly to a chunk that has an in-flight lighting job; buffer them per-chunk and apply (data + queue node) after that job's `ApplyLightingJobResult` runs. Alternatively re-apply buffered mods on top of the merged result.
