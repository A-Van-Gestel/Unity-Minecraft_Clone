# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–04, 06–08, 10–14) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

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

**Candidate mechanism found (2026-07-05):** the HF-3 border-heightmap fuzz reproduced a
deterministic, synchronous defect with exactly this bug's healing profile — **Bug 15** (cross-chunk
sunlight surface stamp on seam faces permanently wiped by a same-column border edit; dense-biome
decoration VoxelMods run through that same edit path). See the Bug 15 entry below; fixing it may
resolve or substantially shrink this bug.

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

---

## Bug 15: Cross-Chunk Sunlight Surface Stamp Permanently Lost After a Border-Column Edit

**Severity:** Medium
**Status:** Partially fixed in code (July 2026) — primary mechanism fixed (K15b/K15c flipped green; limited in-game spot-checks 2026-07-05 look correct, but natural terrain rarely generates chunk-border cliffs so full in-game confirmation is pending — a flat/dev world with a hand-built seam cliff is the practical test setup); an order-dependent residual remains OPEN (see below), still reproduced by K15a
**Files:** `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` (`RecalculateSunlightForColumn`, `CheckEdgeVoxel`, `CheckEdgeVoxelRGB`, BFS seeding, `PullBackCrossSeamContribution`), `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` (`PullBackClaimStillSupported`)

**Description:**
At a chunk-border height step (a cliff face sitting on the seam), the opaque face voxels carry the
sunlight **surface stamp** (`source − 1`, the receive-but-don't-propagate rule pinned by baseline
B39) fed by the *neighbor chunk's* lit border air. An opacity-changing edit **higher in the same
border column** (e.g. placing a block above the cliff top) triggers that column's sunlight
recalculation, which wipes those stamps — and nothing ever re-applies them. The field converges
(no pending work) with the seam face voxels at sky 0 where the borderless oracle — and the
engine's own initial generation wave — put 14. Stable-but-wrong and **permanent**: it survives
re-running both chunks' lighting jobs and only heals when some unrelated edit floods that seam again.

**Empirical facts (seed-0 diagnosis, 2026-07-05, grid-3 harness world):**

- Generation wave over the varied heightmap: **green** (matches oracle, stamps present).
- After 10 seeded border edits + convergence (17 frames, no pending work): **28 voxels under-bright**,
  all of them opaque seam-face voxels in the three edited border columns, engine sky 0 vs oracle 14,
  with the across-seam air neighbor correct at 15/15 (e.g. stamp voxel `(15,12,10)` stone, neighbor
  `(16,12,10)` air 15/15; edit was `place@(15,45,10)` — same column, 30+ voxels higher).
- **Re-running the edited chunk's own job + convergence does NOT restore the stamp. Re-running the
  neighbor's job + convergence does NOT restore it either** — so this is not a missing wake-up: the
  stamp is only ever produced while a BFS wave is *actively visiting* the neighbor's border air voxel
  (as during initial lighting, or a nearby edit's flood).
- A full-relight heal test (queue all 256 columns in every chunk, converge) makes it **worse** —
  202 under-bright seam-face voxels: every border column recalc wipes its column's cross-chunk-fed
  stamps chunk-wide, and only the in-chunk-fed ones recover.

**Root Cause Suspected:**
The sunlight column recalculation rewrites the column's sky values from the vertical rule alone;
lateral surface stamps on opaque voxels are not derivable from the vertical pass and get zeroed.
In-chunk stamps recover because the recalc's BFS wake/re-spread revisits the in-chunk lit air;
cross-chunk stamps cannot: the lit air lives in the neighbor chunk, the neighbor receives no
mod/wake (its own field is unchanged and correct), and `CheckEdgeVoxel`'s reconciliation evidently
does not re-stamp an opaque cross-border voxel on a settled chunk (whether it skips opaque targets
or simply is not armed on a settled world is still to be attributed). Note the job's padded halo
*contains* the neighbor's lit air — the recalc/wipe side has the data to re-derive the stamp locally.

**Relationship to Bug 05:** the healing profile is identical (full world reload = fresh initial
wave fixes it; forcing light updates nearby = BFS revisits the seam fixes it), and dense biomes
stamp tree/canopy decorations through this same edit path (opacity-changing VoxelMods at borders →
column recalcs). Bug 15 is therefore a strong candidate mechanism for at least part of Bug 05 —
but unlike Bug 05 it is deterministic, synchronous, and already reproduced in the harness.

**Repro scenario:** known-bug repro **K15a** (border-heightmap fuzz,
`Assets/Editor/Validation/Lighting/LightingValidationSuite.BorderHeightFuzz.cs`, expected red) reds
on exactly this defect at seed 0 (its first seed; the whole seed space likely hits it). After the
fix + in-game confirmation it promotes to baseline **B62** (flip its `BORDER_FUZZ_EXPECTED_RED`
switch). A distilled minimal deterministic repro (settled seam cliff → place one block atop the
border column → assert the seam-face stamp against the oracle) is the validation-driven-bugfix
next step for fast fix iteration.

**In-game verification suggestion:** stand at a chunk-border cliff, place a block on top of the
cliff's border column, and watch whether the cliff face below darkens permanently (also answers
whether the mesher reads the opaque voxel's own stamped light — the visual-severity question).

**Fix (2026-07-05, four parts — K15b/K15c red→green, all 53 baselines green):**

1. `CheckEdgeVoxel` no longer refuses an opaque center: it receives the surface stamp
   (`source − 1`, `PropagateLight`'s opaque-neighbor rule), written but never enqueued. This arms
   every cross-seam re-derivation path (edge rounds + darkness-wave pull-backs).
2. `CheckEdgeVoxelRGB` — the same change per channel (the K15c blocklight twin).
3. `CrossChunkLightModApplier.PullBackClaimStillSupported` mirrors the new write condition (a
   fully-opaque center's claim is supported by `liveNeighborSky − 1`), keeping the Bug-14 claim
   verification from clearing legitimate stamps.
4. The sun BFS seeding re-spreads an unchanged-but-lit edit node (an opacity-only change —
   e.g. breaking a stone-top block whose air keeps its old 15 — exposes faces that were never
   stamped; the in-chunk case of the same wipe).

**Attempted and REJECTED:** extending the darkness wave's cross-seam pull-back
(`PullBackCrossSeamContribution`) to also fire for a *dimmer*-but-lit neighbor. It did not fix
the residual seeds (verified fresh via a build marker — the arm simply never fired for them) and
it **regressed B59/B61** (stale pull-backs during the slab scenarios' stamp churn reintroduced
massive over-bright ghost light: 2497 voxels +12 sky at grid-3 seed 1). Reverted; the dimmer arm
remains the original unconditional `SetSunlight(neighbor, 0)` + in-center re-propagation enqueue.
The behavior-neutral `PullBackCrossSeamContribution` extraction itself is kept (used by the ≥ arm).

**Residual (OPEN):** the K15a fuzz still reds on 4 of 25 seeds with 1–8 under-bright voxels each
(pre-fix: 28 at seed 0) — an order-dependent interleaving survives all four repair paths. The
seed-12 single-voxel timeline (probe `(15,9,35)` stone, feed `(16,9,35)` air across the x=15|16
seam; sim budget 1, shuffled, seed 12): after the east chunk's cap edit merges, the feed correctly
drops 15→10 **and the stamp correctly follows 14→9** (the new pull-back/claim machinery working);
the west chunk is re-flagged, its job runs — and **its merge wipes the correct 9 back to 0** with
the feed still live at 10, no pending work anywhere, and nothing ever revisiting. The frame-10
wipe's exact path is not yet attributed (candidate: the west job's snapshot/wake-node interplay
during its own merge); it needs in-job tracing. Rebuild the diagnostic from the K15a fuzz body
(sample the probe voxel per frame) to reproduce the timeline exactly. Seeds 9/12/19 are the
1–2-voxel stone-stamp form; seed 14 also shows *transparent* border voxels 1–2 under (same
residual class, so not stamp-specific).
