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
- `CheckEdgeVoxel` can only **add** missing light — by design it never detects over-bright stale values. That direction is fine for shadow patches, but worth remembering when debugging the inverse artifact (light patches that refuse to darken until a manual update). See **Bug 12** (fixed June 2026; archived in `_FIXED_BUGS.md` as Lighting #16) for a concrete mechanism of that inverse artifact (an over-bright cross-seam sunlight loop that survived source removal).

**Validation suite (June 2026):** a minimal repro attempt — 5×5 grid, full slab with a single *diagonal* sky well, all chunks lit in one concurrent wave with stale snapshots plus the production 2 edge-check rounds — **converges to the oracle** and now guards as baseline scenario **B8**.

**Dense-canopy geometry fuzz (2026-06-14):** a procedural fuzz (`LightingValidationSuite.Bug05Canopy.cs`, menu `Minecraft Clone/Dev/Validate Lighting Engine (Bug 05 Canopy Fuzz)`) randomizes canopy height/thickness, sky-well placement, and opaque under-canopy dividers, then asserts the wave-parallel generation field matches the borderless oracle. It did **not** reproduce the Bug-05 shadow mechanism, but it **surfaced a distinct, deterministic defect — Bug 10** (over-bright leak of an opaque border block's surface light across the chunk boundary, the
inverse artifact noted above; fixed June 2026, see [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) Lighting entry 14). With Bug 10 fixed, **all fuzz seeds converge within the production 2 edge-check rounds** — corroborating that in-range 6-connected light paths reconcile within 2 rounds, so a faithful Bug-05 repro (if the bug is still real) is **not synchronously reproducible** (parallel to Bug 09: likely an async/Burst-timing artifact needing in-build instrumentation). The canopy fuzz now guards dense-canopy generation convergence as baseline **B42**; a faithful
failing repro remains TODO.

**Untried repro axis (2026-07-03 analysis):** the "not synchronously reproducible" verdict above covers the *geometry* axis only — every scenario so far lights all chunks in **one simultaneous wave**. Production lights a moving frontier (chunks join incrementally, readiness flips over time, edge-check rounds are consumed at staggered relative times), which is Bug 05's actual habitat and is fully sync-modelable. A staggered generation-wave fuzz is specced as **AS-3**
in [LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) (fidelity finding C8).

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

## Bug 13: Large Suspended Opaque Slab Never Settles (Oscillating Cross-Chunk Skylight)

**Severity:** Medium
**Status:** Fixed in code (July 2026) — awaiting in-game confirmation
**Files:** `Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` (`CrossChunkSunlightSupport`, the extended
Bug-11 veto), `Assets/Scripts/WorldJobManager.cs` (`ApplyCrossChunkLightMod` + emitter-carrying
`DeferredLightMod`), mirrored in `Assets/Editor/Validation/Lighting/Framework/LightingTestWorld.cs`

**Description:**
A large, flat **opaque** block layer (opacity 15) suspended in otherwise sky-lit air and **spanning a contiguous
multi-chunk region** never reaches a stable lighting state. The columns directly under the slab are shadowed while
the surrounding air stays full-bright, so light spills in from the slab's perimeter and forms a cross-chunk skylight
gradient beneath it. That gradient **oscillates / never converges**: the lighting jobs keep re-scheduling so
`WorldJobManager.HasActiveJobs` never returns to `false`, and in the scene view the slab's lit surfaces visibly
**flicker** (light values churn frame-to-frame) rather than settling.

This is distinct from **Bug 05** (dense-canopy shadow patches that converge to a *wrong but static* state until a
reload): there the system reaches a fixed point (an incorrect one); here it appears to reach **no fixed point at all**
within the production scheduling — a non-termination / live-oscillation symptom rather than a static artifact. It is
the same cross-chunk-convergence family, at the opposite extreme (one large hard shadow boundary tiled across many
chunk seams, instead of many small diagonal light pockets).

**Root Cause (confirmed 2026-07-04, via the K13c repro + oscillation probe + emit-neuter attribution):**
Not the edge-check rounds as originally suspected — a **mutual-removal machine between the Bug 12 cross-seam
removal initiator and the Bug 11 veto's in-chunk-only support model**, at the slab region's interior seams. The
under-slab gradient is *perimeter-fed*: a border voxel V in slab chunk A holds sky 14 supplied across a
*different* seam by the sky-lit ring chunk — support the Bug 11 veto (`InChunkSunlightSupport`) cannot see,
because it deliberately credits only in-chunk neighbors (V's in-chunk best is 13 < 14 → no veto). Meanwhile the
adjacent slab chunk B's darkness wave sees V at exactly the removed level, not sky-lit — the Bug 12
mutual-2-cycle signature — and emits a removal at it. The removal applies (no veto), V's chunk re-lights V via
the seam pull-back from the ring's live 15, the re-spread crosses back into B as uplift mods, B's next pass
emits the same removal again: a **period-2 live-lock** (the probe showed the light field hash-repeating with a
cycle length of 1–2 while work stayed pending, across all 8 ring chunks of the slab, y 11–99). Neutering the
Bug 12 emit converged the repro in 2 frames — the attribution test.

**Fix (July 2026):** the Bug 11 veto's support model was extended to match reality: independent support is now
the max of (a) in-chunk neighbors (unchanged) and (b) **live cross-chunk neighbors in chunks other than the
emitter** (`CrossChunkLightModApplier.CrossChunkSunlightSupport`). Live main-thread data is trustworthy —
staleness was only ever a property of the *emitting job's snapshot* — and excluding the emitting chunk preserves
Bug 12's collapse of genuine sourceless seam loops (the emitter is exactly the possibly-stale mutual-loop side,
and B53's loop pair has no third-party feed). Deferred cross-chunk mods now carry their emitter's origin
(`WorldJobManager.DeferredLightMod`) so the exclusion survives the defer/drain path. The perimeter-fed seam
voxel is now vetoed instead of cleared, the counter-wave never launches, and the machine winds down. An
emitter-side snapshot guard on the Bug 12 emit was tried first and rejected: it also suppressed load-bearing
initiators whose "supporter" was itself ghost light, worsening the stale-ghost residue (see Bug 14).

**Reproduction (deterministic, via the fluid stress harness):**
The full-world fluid stress pass (`Assets/Scripts/Benchmarks/FluidStressController.cs`, launched in
`RuntimeMode.FluidStress`) stamps a flat floor across a `REGION_CHUNKS × REGION_CHUNKS` region at a fixed high
altitude (y 100) above lower terrain/ocean, then waits for the pipeline to settle. With an **opaque** floor block
(e.g. `BlockIDs.Stone`, the pre-fix configuration) and `REGION_CHUNKS ≥ 3`, the substrate settle **never completes**
and the slab flickers. The current harness sidesteps this by using `BlockIDs.Facade` (solid but **opacity 0**) for the
floor — a light-transparent solid casts no shadow, so the gradient never forms and the region settles immediately.
That workaround is the empirical confirmation of the root cause (the defect is driven by the slab's *opacity*, not its
solidity, geometry, or the throttled stamp).

**Workaround:** for any large manufactured platform/ceiling that must span chunks in lit air, prefer a light-transparent
solid (opacity 0) over an opaque one. Naturally this also affects player-built large flat opaque roofs spanning chunk
borders.

**Validation suite:** **REPRODUCED SYNCHRONOUSLY** (2026-07-04, roadmap item **AS-1** of
[LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md](../Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md)) — known-bug scenarios
**K13a–K13d** in `Assets/Editor/Validation/Lighting/LightingValidationSuite.Bug13Slab.cs`, registered expected-red.
The **dynamic-stamp** variants — faithful to this repro (slab stamped through the player-edit path onto an
already-settled world) — are **red**:

- **K13c** (grid 5, slab = center 3×3 chunks inside a sky-lit 16-chunk ring): never settles under unlimited-budget
  (500 frames) *or* single-slot (1500 frames) scheduling, and the hash-based oscillation probe confirms the light
  field **repeats an exact cycle** (length 1–2) while work stays pending — a live-lock (no fixed point), not slow
  convergence. This is the first mechanical confirmation of the bug's "no fixed point" hypothesis.
- **K13d** (seeded completion-shuffle + budget/cadence sweep): fails on both geometries. Grid-5 inset seed 0
  live-locks like K13c; grid-3 full-grid seed 1 instead **settles on a massively wrong field** (~32.7k voxels
  over-bright vs the oracle, worst +14 sky) — the same stamp can also terminate into static ghost light, a
  Bug-05-shaped outcome. That terminating over-bright exit turned out to be a **distinct defect** (it persists
  with the live-lock fixed) and is now documented as **Bug 14** with its own scenario **K14a**; K13d asserts
  termination only.

The **generation-wave** variants (**K13a/K13b**, slab already present during initial lighting) are **green** — the
live-lock requires the dynamic-stamp path (player-edit removal seeds + opacity-change column recalcs against an
established bright field), not the initial wave.

**Post-fix state (2026-07-04):** K13a–K13d all **green** (K13c settles in a handful of frames under both budget
regimes; every sweep seed terminates) with all 47 baselines green — including B48/B50–B55, the Bug 11/12 family the
extended veto touches. Awaiting in-game confirmation on the fluid-stress opaque-floor config (`FluidStressController`
with `BlockIDs.Stone` instead of `BlockIDs.Facade`, `REGION_CHUNKS ≥ 3` — the substrate settle must complete and the
slab must not flicker), then promote the K13 scenarios to baselines **B56+** and archive via `archive-fixed-bug`.

---

## Bug 14: Stale-Snapshot Cross-Chunk Sunlight Ghost Light Survives Dynamic Multi-Chunk Darkening

**Severity:** Medium
**Status:** Open
**Files:** `Assets/Scripts/Jobs/NeighborhoodLightingJob.cs` (`PropagateDarkness` seam pull-back via
`CheckEdgeVoxel`; `SetSunlight` cross-chunk uplift mods), `Assets/Scripts/WorldJobManager.cs`
(`ApplyCrossChunkLightMod` uplift path — applied unconditionally when `> current`)

**Description:**
When a large multi-chunk region darkens dynamically (e.g. an opaque slab stamped across several chunks of
sky-lit air) while lighting jobs interleave under budgeted, out-of-order scheduling, chunks can settle into a
**stable but massively over-bright field**: stale "ghost" skylight survives under the slab (up to +14 vs the
borderless oracle) across tens of thousands of voxels. The pipeline terminates normally — no pending light work,
no flicker — so nothing ever re-examines the region; the ghost persists until a full relight (world reload) or
an unrelated nearby edit. This is the **terminating sibling of Bug 13**: the same AS-1 slab repro exposed both,
Bug 13 as the non-terminating exit (fixed July 2026), this defect as the over-bright terminating exit. It is the
over-bright counterpart of Bug 05's shadow patches, and mechanically adjacent to fixed Bug 12 (sourceless light
loops) — but here the light is not a mutual loop, it is simply **never re-visited**.

**Root Cause Suspected:**
A job that runs concurrently with its neighbors' darkening re-lights its side of a seam from its
**schedule-time snapshot** of the neighbor (the `PropagateDarkness` seam pull-back / `CheckEdgeVoxel`, plus
cross-chunk sunlight **uplift** mods, which `ApplyCrossChunkLightMod` applies unconditionally whenever they
exceed the target's current value). If the neighbor has darkened since the snapshot, the re-lit gradient is
sourceless — and unlike removals (which the Bug 11/13 veto adjudicates against live data), **uplifts have no
staleness guard**, and no mechanism ever initiates a removal at a voxel nobody touches again: `CheckEdgeVoxel`
is add-only (the Bug 05 note), the Bug 12 initiator only fires during an active darkness wave, and the ghost
chunk's own job ends stable. Instrumented evidence (2026-07-04 probe, grid-3 seed-1 case): the over-bright
volume shrinks monotonically to ~54k voxels as darkness propagates, then **grows back** over the final frames
(stale re-lights landing as the system quiesces) and freezes at ~57.6k; sequential convergence afterwards finds
zero pending work; two forced edge rounds reduce it (66k → 29k at the time of the probe) but cannot clear it.
Candidate fix direction: a symmetric **stale-uplift veto** at the apply site — verify against the *emitting*
chunk's live data that it still supplies the claimed level (the mirror of the Bug 13 fix, which verified
*removals* against live third-party data); the in-job seam pull-back needs an equivalent story.

**Reproduction (deterministic, editor validation suite):**
Known-bug scenario **K14a** (`Assets/Editor/Validation/Lighting/LightingValidationSuite.Bug14Ghost.cs`): settle
a slab-less grid-3 world, stamp a full-grid opaque slab at y100 chunk-by-chunk under the pinned seed-1 schedule
(per-frame budget 2, 1 frame between stamps, shuffled completion order), wait for settle (terminates — guarded
green by K13d), then compare to the borderless oracle: ~57.6k voxels over-bright, worst +14 sky. Expected red
until fixed. Found by the K13d sweep during the AS-1 session (2026-07-04); fix was **out of scope** for the
Bug 13 fix session that filed this.

**Workaround:** none needed for normal play observed so far — the repro requires large simultaneous multi-chunk
darkening under scheduling pressure. A world reload or any nearby light-triggering edit clears the residue.
