# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–08, 10–15) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

## Bug 09: Cross-Chunk Blocklight Lost on Rapid Place/Break at Chunk Border

**Severity:** Medium-High
**Status:** Open

**Description:**
When rapidly breaking and re-placing a blocklight source (e.g., a torch or glowstone) at a chunk border — specifically in Chunk A adjacent to Chunk B — the lighting engine can fail to propagate the blocklight emission into Chunk B, or fail to emit light entirely in both chunks. Two distinct failure modes are observed:

1. **Partial propagation:** Chunk A receives the blocklight correctly, but Chunk B stays dark — the cross-chunk BFS propagation is silently skipped.
2. **Total emission loss:** Neither Chunk A nor Chunk B receives any blocklight, despite the emissive block being physically present in the world.

The issue is **not permanent** — forcing a lighting update on the affected chunk(s) (e.g., placing/breaking another block nearby) correctly re-triggers the BFS and restores proper lighting. This suggests the light data is not corrupted, but rather the emission seeding or cross-chunk mod delivery is being dropped during a specific race window.

**Reproduction Steps:**

1. Enter a world and navigate to a chunk border (ideally underwater in an ocean biome for easier reproduction).
2. Place a blocklight source (e.g., Jack O' Lantern) in Chunk A, directly adjacent to the Chunk B border.
3. Break the light source and immediately re-place it. Repeat rapidly.
4. Observe that after several cycles, Chunk B (or both chunks) may fail to update with the new blocklight.

**Aggravating factors:**

- **Fluid-heavy chunks significantly increase reproduction rate.** Testing underwater in ocean biomes shows noticeably slower cross-chunk light updates compared to non-fluid biomes. The additional voxel modifications from fluid flow (e.g., water flowing back into the broken block's position) likely create contention with the lighting job pipeline — either by flooding the deferred cross-chunk mod queue or by causing the chunk's lighting job to be scheduled/cancelled repeatedly before cross-chunk mods are delivered.
- **IL2CPP master build timing:** All testing was performed in a release IL2CPP build. Mono/Editor builds would be slower overall, potentially widening or narrowing the race window.

**Root Cause Suspected:**
A race condition in the cross-chunk blocklight mod delivery path. When a blocklight source is broken and re-placed in rapid succession while the chunk is simultaneously undergoing other voxel modifications (fluid re-flow), one of the following likely occurs:

- The removal pass's deferred cross-chunk mods for Chunk B are still in flight when the new placement triggers a fresh lighting job, causing the new emission's cross-chunk mods to be dropped or overwritten.
- The chunk's lighting job is cancelled and re-scheduled due to the concurrent voxel modification (fluid flow), and the re-scheduling loses the pending blocklight emission seed.
- The deferred cross-chunk mod queue for Chunk B is processed against stale snapshot data, causing the mods to be silently discarded as no-ops.

**Validation suite (June 2026):** Every production scheduling behavior modelable in the synchronous harness was exercised across five layers — direct-harness single/both-in-flight interleaving, frame-simulator `ContainsKey` in-flight guard / budget throttling / completion-order sensitivity, multi-frame held flights, fluid-flow contention (Air→Water opacity 0→2 injecting BFS nodes mid-flight), and seeded iteration-order randomness (Fisher-Yates shuffles, 50 seeds) — plus the combined ocean-biome stress test. All converged to the oracle across every tested
seed and ordering.

> **Consolidated 2026-06-14** (see [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md) §5): the deterministic single-instance permutations folded into two representatives — **B15** (direct-harness break+place, single- then both-in-flight) and **B16** (fluid break→water→place under a held flight + single-slot budget) — backed by **B22** (dual-chunk both-in-flight), **B26–B29** (50-seed shuffled sweeps: fluid contention, budget pressure, dual-chunk interleave, combined stress), and **B40
** (cross-chunk
> geometry fuzz). The retired numbers B17–B21 / B23–B25 are intentionally unused. Coverage of every behavior above is preserved by these survivors.

The Bug 07/08 cross-chunk mod delivery fixes were already present when Bug 09 was last observed — the bug is either a genuine async race condition (Burst job system timing, IL2CPP memory ordering) that synchronous `.Run()` cannot reproduce, or is no longer present in the current codebase. A faithful failing repro is still TODO before this bug's fix can be test-driven; the surviving baselines serve as regression guards.

**Plan update (2026-07-03 analysis — see [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)):** the environment this bug was observed in has since changed twice — MT-2 (`LightWorkScheduler` ready/waiting split, 2026-07-02) replaced the scheduler it raced against, and TG-4 fluid-Burst (June 2026) replaced the managed fluid tick that was its main aggravating factor. Three follow-ups are specced: **AS-2** (model the MT-2 park/promote layer in the frame simulator — a *missed-promotion stall* is exactly this bug's
symptom shape and is sync-testable), **AS-4** (real-`Schedule()` parallel-determinism gate covering pooled-buffer aliasing, the remaining plausible in-editor race), and **AS-5** (automated in-build stress rig — also the cheap way to **re-verify the bug still exists** before further investment).

**Testing environment:** IL2CPP master build, ocean biome (underwater), June 2026.

## Bug 16: Runaway Lighting Job Loop → OOM After Breaking a Blocklight Source Near a Chunk Border

**Severity:** High (editor/game/OS crash via out-of-memory)
**Status:** Open

**Files:**

- `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` — `PropagateDarknessRGB` (removal-node enqueue), `CheckEdgeVoxelRGB` (cross-seam pull-back during the darkness phase), `SetBlocklightRGB` (per-write cross-chunk mod emission, no dedup)
- `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` — `ComputeBlocklight` (removal zero-channels pass through unconditionally; no RGB independent-support veto)
- `Assets/Scripts/WorldJobManager.cs` — `ProcessLightingJobs` / `LightingCompletionPass` (unstable jobs re-arm `HasLightChangesToProcess` every pass)

**Description:**
Under certain conditions when breaking a blocklight source near a chunk border — most reliably while testing RGB blending (multiple different-colored lamps whose gradients overlap and cross the seam) — the lighting pipeline for the affected chunk(s) never stabilizes: lighting jobs re-schedule every frame indefinitely, the chunk's visual lighting freezes (mesh rebuilds only fire on `IsStable`), and memory usage grows monotonically until the editor, game, or OS crashes from OOM. Observed ~5 times over ~2 months (since the RGB lighting overhaul), always in
Mono editor play mode; IL2CPP builds are not interactively tested, so the absence of reports there is not evidence of absence — the suspected mechanism is backend-independent logic.

**Reproduction Steps (in-game, unreliable — the deterministic form is scenario K16a):**

1. Place two (or more) blocklight sources with *different* emission colors near a chunk border, such that their gradients overlap each other and cross the seam (enclosed spaces — caves, underwater — aggravate by building attenuation plateaus).
2. Break (and optionally re-place) one of the lamps at/near the border, repeatedly.
3. Occasionally the affected chunk's lighting freezes and memory climbs until OOM.

**Repro scenarios:** `K16a` (deterministic runaway repro, expected red — `LightingValidationSuite.KnownBugs.cs`) + `B86` (simple-form convergence guard / fix over-correction tripwire — `Baselines/LightingValidationSuite.Baseline.Bug16Runaway.cs`).

**Root Cause (CONFIRMED 2026-07-11 — an *infinite per-channel removal cycle inside a single job's blocklight darkness phase*, see the Update below for the captured specimen):**
Two compounding defects in the RGB removal path (the sky-light analogs were fixed in Bugs 11/13/14 but never mirrored):

1. **Removal-node channel contamination** (`PropagateDarknessRGB`): when a darkness wave zeroes *any* channel of a neighbor, the re-enqueued removal node carries the neighbor's **full pre-zero RGB** — including channels that were *not* removed because they belong to a different, still-live lamp's gradient (`neighborVal >= oldVal` re-spread channels, and channels where the incoming node's old value was 0). Downstream, those contaminated channel values act as removal thresholds, so breaking a red lamp launches a full-amplitude *green/blue* darkness wave
   through any overlapping live gradient. In-chunk this self-heals in the same pass (phase-2 re-spread from the live source), but at the seam it emits **removal mods that zero the neighbor chunk's live field**. (The BFS seed from `ModifyVoxel` has the same contamination in miniature: the captured old light of the broken block includes transit light from other sources.)
2. **No convergence guard on the RGB removal path**: `ComputeBlocklight` lets removal zero-channels through unconditionally — there is no RGB analog of the Bug 11/13 independent-support veto — and the RGB cross-seam pull-back (`CheckEdgeVoxelRGB` called from inside the darkness phase) is not claim-verified (`PullBackClaim` carries `WrittenSky` only, Bug 14's fix is sky-only). So chunk A's wave zeroes B's live field (wake nodes launch B's darkness wave with the genuine old values), B's pass re-heals from its live lamp and/or stale-snapshot pull-backs, and
   emits removal/placement mods back at A — with no veto to break the cycle. Each generation regenerates full-amplitude work: a period-2 live-lock, per channel.

**Memory-growth vector (confirmed):** the infinite removal cycle grows the job-internal `Allocator.Temp` BFS queues (and the placement queue alongside) without bound inside a single `Execute()` — in production a scheduled job that never completes (the chunk stays `AlreadyInFlight`, freezing its lighting) while worker-thread memory climbs to OOM. The originally-suspected *cross-pass* vectors (managed wake-node queues, undeduped RGB `CrossChunkLightMods`) are real churn surfaces but were NOT the OOM: the captured runaway jobs emitted only 1–6 mods.

**Why the validation suite hadn't caught it:** no scenario exercised *multi-colored overlapping gradients* across a seam, and none built the interrupted-reconciliation plateau state the cycle needs — existing RGB baselines used single-color or co-located sources on clean converged fields. Closed by K16a/B86 (this bug's scenarios).

**Cross-references:** fixed Bugs 11, 12, 13, 14 (`_FIXED_BUGS.md` Lighting #15–#18) — the identical mechanism family on the sky channel, each fixed with machinery (independent-support veto, cross-seam initiator, pull-back claim verification) that was never mirrored to the RGB path.

**Update (2026-07-11) — root cause CONFIRMED via harness reproduction + in-job node dumps.** The
symptom was reproduced synchronously (3/3 reliability) by the recipe now encoded as scenario
**K16a**: red + green lamps in the two shared x15|16 seam border columns, submerged in water
(opacity-2 attenuation builds mixed-channel plateaus), cycled through break → red-chunk removal pass
→ water re-flow → completing a held pre-edit green-chunk flight → under-budgeted waves → re-place.
Two editor OOM crashes during bisection (Editor.log: Burst-job `NullReferenceException`s at the
native allocator, `ALLOC_DEFAULT` 22.6 GB, repeated failed 1.7 GB `BlockDoublingLinearAllocator`
doublings, fatal) forced a temporary diagnostic into `NeighborhoodLightingJob`: a shared
`MAX_BFS_NODES_PER_PASS` work cap (200k) across the seed + phase loops with a loud abort
(`[LightingJob DIAG] Bug-16 BFS work cap exceeded`) and a near-cap node dump. **Do not remove the
cap before the fix** — K16a OOM-crashes the editor without it (each capped-but-churning job also
parks ~0.5–1 GB in the frame's Temp linear allocator, which only resets at end-of-frame, so many
runaway jobs in one editor frame still OOM'd at a 4M cap; 200k keeps a full suite run bounded).

The dump captured the loop verbatim: the blocklight **removal** queue cycling the *same* stacked
border cells with the *same* pre-zero values forever — e.g. `(15,65,15) rgb=(3,2,0) → (15,66,15)
rgb=(0,1,0) → (15,64,15) rgb=(2,3,0) → repeat` — at the x15|16 / z31|32 four-chunk corner, in jobs
with only 1–6 outgoing cross-chunk mods (pure in-chunk churn). Mechanism, matching the dumped
values exactly: a removal node zeroes ONE channel of a neighbor (`R: 2<3`), the re-enqueued node
carries the neighbor's full pre-zero RGB **including non-removed channels** (defect 1); the zeroed
border cell is immediately re-lit *mid-removal-phase* by `CheckEdgeVoxelRGB` (defect 2's pull-back
arm) from cross-seam halo cells that are never themselves zeroed (they keep hitting the respread
branch) — restoring the cell to the same constant; adjacent cells then re-zero it on the *other*
channel (`G: 2<3`), re-enqueueing the identical node. Restore-to-constant + per-channel alternation
defeats the BFS's strict-decrease termination guarantee: a true infinite loop inside one
`Execute()`, growing the Temp removal/placement queues until OOM. It is **not an async race** —
plain `.Run()` reproduces it; the interrupted-reconciliation cycling is needed only to build the
non-monotone mixed-channel plateau *values* that arm the cycle (a clean break's monotone gradients
cannot — guarded green as B86). This matches the in-game trigger profile: repeated lamp place/break
at chunk borders in enclosed spaces (cave walls/water = plateau-forming attenuation), Mono editor
merely the place it was observed, IL2CPP equally reachable.

**Testing environment:** Mono editor play mode, RGB blending tests near chunk borders, May–July 2026; harness repro 2026-07-11 (editor Mono, synchronous `.Run()`).

