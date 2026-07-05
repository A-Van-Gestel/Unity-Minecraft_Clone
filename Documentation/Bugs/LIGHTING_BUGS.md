# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–04, 06–08, 10–15) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

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
- `CheckEdgeVoxel` can only **add** missing light — by design it never detects over-bright stale values. That direction is fine for shadow patches, but worth remembering when debugging the inverse artifact (light patches that refuse to darken until a manual update). See **Bug 12** (fixed June 2026; archived in `_FIXED_BUGS.md` as Lighting #16) for a concrete mechanism of that inverse artifact (an over-bright cross-seam sunlight loop that survived source removal).

**Validation suite (June 2026):** a minimal repro attempt — 5×5 grid, full slab with a single *diagonal* sky well, all chunks lit in one concurrent wave with stale snapshots plus the production 2 edge-check rounds — **converges to the oracle** and now guards as baseline scenario **B8**.

**Dense-canopy geometry fuzz (2026-06-14):** a procedural fuzz (`LightingValidationSuite.Bug05Canopy.cs`, menu `Minecraft Clone/Dev/Validate Lighting Engine (Bug 05 Canopy Fuzz)`) randomizes canopy height/thickness, sky-well placement, and opaque under-canopy dividers, then asserts the wave-parallel generation field matches the borderless oracle. It did **not** reproduce the Bug-05 shadow mechanism, but it **surfaced a distinct, deterministic defect — Bug 10** (over-bright leak of an opaque border block's surface light across the chunk boundary, the
inverse artifact noted above; fixed June 2026, see [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) Lighting entry 14). With Bug 10 fixed, **all fuzz seeds converge within the production 2 edge-check rounds** — corroborating that in-range 6-connected light paths reconcile within 2 rounds, so a faithful Bug-05 repro (if the bug is still real) is **not synchronously reproducible** (parallel to Bug 09: likely an async/Burst-timing artifact needing in-build instrumentation). The canopy fuzz now guards dense-canopy generation convergence as baseline **B42**; a faithful
failing repro remains TODO.

**Untried repro axis (2026-07-03 analysis):** the "not synchronously reproducible" verdict above covers the *geometry* axis only — every scenario so far lights all chunks in **one simultaneous wave**. Production lights a moving frontier (chunks join incrementally, readiness flips over time, edge-check rounds are consumed at staggered relative times), which is Bug 05's actual habitat and is fully sync-modelable. A staggered generation-wave fuzz is specced as **AS-3**
in [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) (fidelity finding C8).

**Candidate mechanism found AND FIXED (2026-07-05):** the HF-3 border-heightmap fuzz reproduced a
deterministic, synchronous defect with exactly this bug's healing profile — **Bug 15** (cross-chunk
sunlight surface stamp on seam faces permanently wiped by a same-column border edit; dense-biome
decoration VoxelMods run through that same edit path). Bug 15 is fixed and archived
(`_FIXED_BUGS.md` Lighting **#19**, guarded by baselines B62/B63); whether it accounted for part of
this bug's dense-biome shadows remains to be observed in-game. Note the stamp corruption itself is
visually masked (the mesher shades faces from adjacent air, not the opaque voxel's stored value), so
Bug 05's *visible* patches more likely trace to the edge-round exhaustion below.

**FAITHFUL SYNCHRONOUS REPRO FOUND (2026-07-05) — edge-round exhaustion after border edits:** with
Bug 15's stamp mechanism fixed, the border-heightmap fuzz (**K15a**,
`Assets/Editor/Validation/Lighting/LightingValidationSuite.BorderHeightFuzz.cs`) still reds at its
seed 14: after the seeded border edits, four *transparent* border voxels one step from the z-seam
sit 1–2 sky levels under the oracle with **no pending work anywhere** — a converged, stable-but-dark
field, exactly this bug's symptom. Classifier proof: **exactly one forced edge-check round over the
grid heals the field to the oracle** (4 → 0 mismatches). Mechanism: both production
`RemainingEdgeCheckRounds` (= 2) are consumed during the generation wave; a *post-edit* cross-seam
under-report (the edit's darkness/re-spread interleaving across in-flight snapshots) then has no
edge-check round left to reconcile it, and edge checks are the only corrector for under-bright
border light (§3.6/§3.7 of LIGHTING_SYSTEM_OVERVIEW). This falsifies the "not synchronously
reproducible" verdict for the post-edit form (the initial-wave form does still converge in-harness).
K15a is registered as this bug's known-bug repro; it promotes to a baseline (flip
`BORDER_FUZZ_EXPECTED_RED`) once this mechanism is fixed. Candidate fix directions: re-arm a
bounded edge-check round when a border-column edit stabilizes, or extend the Bug-15 pull-back
machinery to transparent centers with claim verification (rejected once for spread risk — see the
rejected-approach note in `_FIXED_BUGS.md` Lighting #19 — but a *claim-verified* variant may be
viable).

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

**Validation suite (June 2026):** Every production scheduling behavior modelable in the synchronous harness was exercised across five layers — direct-harness single/both-in-flight interleaving, frame-simulator `ContainsKey` in-flight guard / budget throttling / completion-order sensitivity, multi-frame held flights, fluid-flow contention (Air→Water opacity 0→2 injecting BFS nodes mid-flight), and seeded iteration-order randomness (Fisher-Yates shuffles, 50 seeds) — plus the combined ocean-biome stress test. All converged to the oracle across every tested
seed and ordering.

> **Consolidated 2026-06-14** (see [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md) §5): the deterministic single-instance permutations folded into two representatives — **B15** (direct-harness break+place, single- then both-in-flight) and **B16** (fluid break→water→place under a held flight + single-slot budget) — backed by **B22** (dual-chunk both-in-flight), **B26–B29** (50-seed shuffled sweeps: fluid contention, budget pressure, dual-chunk interleave, combined stress), and **B40
** (cross-chunk
> geometry fuzz). The retired numbers B17–B21 / B23–B25 are intentionally unused. Coverage of every behavior above is preserved by these survivors.

The Bug 07/08 cross-chunk mod delivery fixes were already present when Bug 09 was last observed — the bug is either a genuine async race condition (Burst job system timing, IL2CPP memory ordering) that synchronous `.Run()` cannot reproduce, or is no longer present in the current codebase. A faithful failing repro is still TODO before this bug's fix can be test-driven; the surviving baselines serve as regression guards.

**Plan update (2026-07-03 analysis — see [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)):** the environment this bug was observed in has since changed twice — MT-2 (`LightWorkScheduler` ready/waiting split, 2026-07-02) replaced the scheduler it raced against, and TG-4 fluid-Burst (June 2026) replaced the managed fluid tick that was its main aggravating factor. Three follow-ups are specced: **AS-2** (model the MT-2 park/promote layer in the frame simulator — a *missed-promotion stall* is exactly this bug's
symptom shape and is sync-testable), **AS-4** (real-`Schedule()` parallel-determinism gate covering pooled-buffer aliasing, the remaining plausible in-editor race), and **AS-5** (automated in-build stress rig — also the cheap way to **re-verify the bug still exists** before further investment).

**Testing environment:** IL2CPP master build, ocean biome (underwater), June 2026.

