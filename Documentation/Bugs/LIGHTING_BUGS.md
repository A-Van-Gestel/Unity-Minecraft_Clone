# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–08, 10–17) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

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

## Bug 18: Sourceless RGB blocklight loop survives when equal-color seam sources are removed in the same wave

**Severity:** Medium (visual artifact: a stable, sourceless over-bright colored residue straddling a chunk seam after equal-color lamps are removed together; self-heals on any full relight, e.g. save/reload — the RGB mirror of Bug 12's sky loop)
**Status:** Open — reproduced deterministically in the lighting validation suite (repro scenario **K18a**, fidelity finding **C10**); never yet knowingly observed in-game (oracle/harness-found, per the Bug 12/17 colored-residue precedent).

**Description:**
When two **equal-color** blocklight sources on opposite sides of a chunk seam mutually light the two shared border columns, and both are removed in the same lighting wave (so each chunk's schedule-time snapshot still shows the *other* side lit), the seam does **not** darken. It drops by exactly one level and then **locks** as a stable-but-wrong over-bright residue: light whose genuine source is gone, sustained purely by the two seam voxels mutually supporting each other across the boundary. This is the RGB (blocklight) twin of **Bug 12** (the sky
sourceless cross-seam loop, fixed June 2026) and the same over-bright class as **Bug 17** (fixed July 2026), but with a distinct root cause — a missing removal *initiator*, not a missing *veto*.

**Reproduction (deterministic harness — repro K18a, `Assets/Editor/Validation/Lighting/LightingValidationSuite.C10RgbLoop.cs`):**
A sealed rock corridor straddles the x15|16 seam, lit only by two red lamps placed one step from each shared seam column (x14 in chunk (0,1), x17 in chunk (1,1)), so both seam voxels converge to the same red level. Breaking both lamps in the same wave-parallel round, then running to convergence:

- Pre-break: seam voxels x15/x16 = **R14 each** (mutually equal).
- Post-break: field **converges (stable)** but the corridor stays lit — x15/x16 = **R13**, fanning out R8→R13 across x10–x21; the borderless oracle is all-zero. 38 sourceless residue voxels in the corridor plane, worst **R13 at the seam (15,63,24)**.

**In-game reproduction (predicted, not yet confirmed):** enclosed space (cave/underwater) at a chunk border, two same-color lamps (e.g. two red glowstone-equivalents) on opposite sides of the seam each lighting the shared border columns; break both in rapid succession. Expected footprint: a faint colored tint near the seam that persists until the chunks are relit (reload).

**Root Cause (confirmed via harness repro + per-voxel attribution):**
The sky↔RGB removal-machinery parity gap after Bug 17. Sky light has a cross-seam removal **initiator** — when a darkness wave meets a cross-chunk neighbor at *exactly* the removed level (the 2-cycle signature of a sourceless loop) and that neighbor is not independently supported, `NeighborhoodLightingJob.EmitCrossChunkSunlightRemoval` emits a removal mod that starts collapse from the other side (the Bug 12 fix). RGB blocklight has **no counterpart** — `EmitCrossChunkSunlightRemoval` hard-codes `Channel = LightChannel.Sun`, and `PropagateDarknessRGB`
never emits a cross-seam removal initiator. So the symmetric mutually-equal seam has no node to begin removal from: each seam voxel's darkness wave stops one step short (it re-lights from the other side's stale snapshot), and the Bug 17 independent-support **veto** (`CrossChunkLightModApplier.ComputeBlocklight`), added to *protect* legitimately-supported channels, now actively **protects the stale mutual support** — the exact over-protection tension Bug 13 resolved on the sky side. The result is a stable 2-cycle: neither side ever drops below the level
the other supplies.

**Suspected fix (NOT YET APPLIED — awaiting approval):** mirror the Bug 12 cross-seam removal **initiator** to RGB per channel (an `EmitCrossChunkBlocklightRemoval` analog in `PropagateDarknessRGB`), adjudicated by the existing Bug 17 RGB veto so a genuinely independent colored source is still preserved. This must be designed together with fidelity finding **C12** (RGB darkness-phase pull-back claim verification — Bug 14's RGB mirror), since the initiator and claim-verification analogs share the removal-machinery fix surface. Constraints: preserve Bug
16's removal-node channel masking and the permanent `MAX_BFS_NODES_PER_PASS` fail-safe; keep the Bug-16 over-correction tripwires (B86–B88) and the Bug 12 sky baselines (B50–B53) green.

**Validation suite:** repro **K18a** (`LightingValidationSuite.C10RgbLoop.cs`, registered via `AddKnownBugScenarios`) asserts the correct behavior (corridor fully darkens) and currently reproduces the bug (expected ⚠️ until fixed). On fix it flips green with no test edits; promote to a baseline after in-game confirmation.

**Testing environment:** Mono editor, lighting validation harness (`NeighborhoodLightingJob` via `.Run()`), July 2026.
