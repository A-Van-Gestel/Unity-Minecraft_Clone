# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–08, 10–16) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

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

## Bug 17: Sourceless RGB Ghost-Light Island Survives Interrupted Cross-Seam Removal (No Over-Bright Corrector for Blocklight)

**Severity:** Low-Medium (visual artifact: a faint sourceless color tint — observed R ≤ 3/15 — near chunk seams after rapid lamp place/break; stable, but self-heals on any full relight of the chunk, e.g. save/reload)
**Status:** Open

**Files:**

- `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` — `PropagateDarknessRGB` (the `neighborVal >= oldVal` respread branch treats equal-value neighbors as independent support), `CheckEdgeVoxelRGB` when called from the darkness phase (snapshot-trusting re-light, not claim-verified — `PullBackClaim` carries `WrittenSky` only)
- `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` — `ComputeBlocklight` (no per-channel independent-support veto; no RGB analog of the Bug 12 cross-seam removal initiator exists anywhere)

**Repro scenario:** `K17a` (`LightingValidationSuite.KnownBugs.cs`) — the Bug 16 interrupted-cycling recipe (`_FIXED_BUGS.md` Lighting #21) with the full-field oracle assertion; deterministic red (~24 ghost voxels, R 1–3, straddling the z31|32 seam). The ghost needs ≥2 interrupted cycles (cycles 0–1 settle clean); baseline `B87` (promoted from Bug 16's repro K16a) runs the same recipe with a dated exemption for exactly this residue class — **restore B87's plain oracle compare when this bug is fixed**.

**Description:**
Found 2026-07-11 as the post-fix residue of Bug 16's repro. Interrupted reconciliation (lighting edits landing while stale-snapshot jobs are in flight, e.g. rapid lamp place/break under load) can leave a small **orphaned over-bright RGB island**: light whose source is gone, disconnected from any live gradient by zero-valued cells. Nothing ever corrects it, for the reasons documented in `LIGHTING_SYSTEM_OVERVIEW.md` §3.7 — but on the RGB channel *none* of the sky-side machinery exists:

1. A darkness wave cannot traverse the zero-valued gap around the island (removal only follows lit cells), and a wave that does reach it treats equal-value neighbors as independent support (`neighborVal >= oldVal` → respread) — the exact blind spot the **Bug 12** cross-seam initiator closed for sky light, never mirrored to RGB.
2. Edge checks only ADD light, never remove (§3.6 design constraint) — over-bright is invisible to them.
3. `ComputeBlocklight` has no independent-support veto (**Bug 11/13** analog) and RGB pull-backs are not claim-verified (**Bug 14** analog), so stale-snapshot re-lights during the churn window plant the island in the first place, and later passes have no path to question it.

**Root cause:** confirmed as a *class* (the orphaned island and its non-correction are deterministic in K17a); the exact planting write during cycle 2 of the recipe (stale green-flight full-LightMap merge re-instating pre-break red vs. a darkness-phase pull-back from a stale halo) has not been attributed — both paths are unverified-by-design today and both are closed by the same fix direction.

**Fix direction:** mirror the sky channel's removal machinery to RGB per channel — the Bug 12 exactly-equal cross-seam removal initiator, the Bug 11/13 independent-support veto in `ComputeBlocklight`, and Bug 14 claim verification for RGB pull-backs (extend `PullBackClaim` beyond `WrittenSky`; the struct is job-output only, not save format). Alternatively (or additionally) an authoritative RGB re-derivation pass for touched regions. Test-first against K17a with B86 + the Bug 07/09 family baselines as over-correction tripwires.

**Testing environment:** editor validation harness, 2026-07-11 (deterministic, synchronous). Not yet knowingly observed in-game — the in-game footprint would be a faint colored tint near a seam that disappears on reload.

