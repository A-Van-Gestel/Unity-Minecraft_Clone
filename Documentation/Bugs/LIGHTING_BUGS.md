# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–04, 06–08) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

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

**Validation suite (June 2026):** Fourteen repro attempts total across five harness layers:

- **Direct harness (B15, B16):** Single-cycle break+place with neighbor in-flight, and double-cycle with both chunks in-flight (wave-parallel). Both converge — guard the defer/drain mechanism.
- **Frame simulator, complete-all (B17, B18, B19):** A `LightingFrameSimulator` was built to model three production scheduling behaviors the direct harness cannot: the `ContainsKey` in-flight guard that rejects re-scheduling while a job runs (B17), budget-throttled single-slot convergence (B18), and reverse completion order exercising the `_completedLightJobs` ordering dependency (B19). All three converge to the oracle.
- **Frame simulator, multi-frame flight lifetimes (B20, B21, B22):** Completion predicates hold chunk A's removal job in-flight across 2–3 frames while chunk B snapshots stale pre-removal light. B20: stale neighbor snapshot (B schedules and completes while A is held). B21: B stabilizes before A's removal even completes. B22: both chunks in-flight simultaneously with interleaved completion maximizing deferred mod accumulation. All three converge to the oracle.
- **Frame simulator, fluid-flow contention (B23, B24, B25):** Water flows back into the broken lamp position (Air→Water, opacity 0→2) while the removal job is held in-flight, injecting BFS nodes and changing opacity mid-flight. B23: single fluid fill + re-place with stale neighbor snapshot. B24: fluid + re-place under single-slot budget pressure (maximum starvation). B25: two full break+fluid+place cycles with both chunks held and interleaved completion. All three converge to the oracle.
- **Frame simulator, seeded iteration-order randomness (B26, B27, B28):** Models production's non-deterministic `Dictionary`/`HashSet` iteration order via seeded Fisher-Yates shuffles of both completion and scheduling order. Each scenario runs 50 RNG seeds. B26: shuffled fluid contention ("kitchen sink" combining all production behaviors). B27: shuffled scheduling under single-slot budget pressure. B28: shuffled dual-chunk interleaved flights. All seeds converge to the oracle across all three scenarios.

Every production scheduling behavior modelable in the synchronous harness has been exhausted. The bug is likely either a genuine async race condition (Burst job system timing, IL2CPP memory ordering) that synchronous `.Run()` cannot reproduce, or has been fixed by the Bug 07/08 cross-chunk mod delivery fixes and is no longer reproducible in the current build. A faithful failing repro is still TODO before this bug's fix can be test-driven; the 28 baselines serve as comprehensive regression guards.

**Testing environment:** IL2CPP master build, ocean biome (underwater), June 2026.

