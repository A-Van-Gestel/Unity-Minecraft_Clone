# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** June 2026 (full codebase audit)
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–08, 10–19) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

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

**Candidate synchronous repro lead (2026-07-12 — from the C11 interrupted-reconciliation fuzz) — RESOLVED 2026-07-12: harness-fidelity artifact, NOT a real defect.** the fuzz's diagonal 4-chunk-corner geometry (two equal-color lamps at a corner, e.g. `(31,64,31)`/`(32,64,32)`, water, ≥3 interrupted break/re-place cycles with a held neighbor flight + under-budgeted waves) leaves a stable, **under-bright** region *above* the water near the corner — cross-chunk blocklight the surviving lamp legitimately casts that is never delivered by the interrupted
schedule (a clean relight of the identical final voxel state matches the borderless oracle exactly, so it is a delivery/schedule gap, not a BFS/oracle defect). This is the Bug 09 shape (cross-chunk blocklight lost after rapid place/break at a border), reproduced **synchronously** in the harness — the first such lead. Not yet confirmed as Bug 09 vs a harness-fidelity artifact (the diagonal corner is not a face-adjacent pair, and the fuzz recipe hand-schedules only 2 of the 4 corner chunks, unlike production which wakes all neighbors). The C11 fuzz (
baseline **B91**) is deliberately scoped to face-adjacent seams and excludes this geometry.

> **Resolution (2026-07-12, classified via `Unity_RunCommand`):** hypothesis (b) — a **harness-fidelity artifact**, not a synchronous reproduction of Bug 09. The under-delivery exists only because the fuzz recipe settles with plain `LightingTestWorld.RunWaveToConvergence`, which deliberately does **not** drive the post-stabilization edge-check *re-add* rounds that production runs. Decisive classifier: replaying the identical interrupted schedule and then driving a **single** re-granted edge-check round (`RunReGrantedEdgeCheckRound` — exactly what
`LightingFrameSimulator.RunToConvergence` runs at grid quiescence, and the code path production's **Bug-05** border-column edge-check re-grant takes after the recipe's final `PlaceBlock(lampA, Water)`, an opacity edit at local `(15,·,15)`) heals the field **completely** — 41 → 0 divergent voxels, probe `(29,68,31)` G 2 → 4 = oracle. This is the §3.7 invariant in action (see [`LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) §3.6/§3.7): cross-chunk *placement* (**under-bright**) is always re-addable by an edge check; only the
> interrupted `RunWaveToConvergence` settle, which omits that pass, leaves it stranded. Since this class is pure under-bright and self-corrects in one edge round, it does **not** survive production's machinery. **Bug 09 stays open** — a genuine async race that survives the edge-check re-add still has no faithful synchronous repro; this lead was not it. (Harness gap noted, no B91 change: the fuzz's settle omits edge-check rounds by design and is seam-scoped, where they are unnecessary; a future diagonal-corner fuzz axis, if added under **AS-4**/**AS-5**,
> must settle through an edge-check-inclusive driver to avoid re-flagging this same artifact.)

**Testing environment:** IL2CPP master build, ocean biome (underwater), June 2026.

---

## Bug 20: Partial Blocks Are Uniformly Opaque — Slabs Block All Light and Max-Darken AO

**Severity:** Medium-High  
**Status:** Sky/blocklight propagation **fixed and confirmed in game** (August 2026). The cross-chunk half
(`VO-4`) is **fixed in code August 2026 — awaiting in-game confirmation**; **still open** for ambient
occlusion (`VO-5`). The lighting half is done
(`VO-3`, commit `f0d12ca2`): occlusion is now per-face, derived from the block's rotated
`BlockCollisionBounds` via `LightAttenuation.FaceBlocksLight` / `EntryOpacity` / `ExitBlocked`, with
propagation-source guards switched to `BlockTypeJobData.IsFullyOpaqueCell` so a partial block re-propagates
the light held in the open part of its cell. A first in-game pass found the column still decaying `15/14/13/…` below a vertical slab — the
`isVerticalSunlight` rule was likewise whole-block — fixed by `LightAttenuation.IsTransparentThroughFace`
and confirmed ("15 all the way down"). `K20a` was strengthened to a column-differential and **promoted to
permanent baseline `B104`**.

`VO-4` (August 2026) then made the cross-chunk half directional: the removal veto's support scan
(`CrossChunkLightModApplier`, now taking a `TargetEntryCost` and a block-data lookup instead of a
whole-block opacity and an `IsOpaque` predicate), the Bug 12/18 removal initiators, the dimmer-seam stamp
pull-back, `CheckEdgeVoxel`/`CheckEdgeVoxelRGB`, and `IsVerticallySkyLit` — the last being a site the plan
had not listed, and the one that let the Bug 12 initiator fire on a column the BFS holds at an undimmed 15.
Repro `K20b` (source-side credit, target-side entry cost, with solid-face and full-cube tripwires) flipped
green and was **promoted to permanent baseline `B106`** after in-game confirmation (no flicker at a slab
seam); baseline `B105` guards the settled seam field.

**One piece remains before this can be archived:** the ambient-occlusion half of the artifact — partial
blocks still darken AO at maximum — is `VO-5`. See also **Bug 21**, a separate defect found while
authoring `B105`.  
**Related:** [`MESHING_BUGS.md`](./MESHING_BUGS.md) Bug M01 (the mesher-side half of the same visual artifact — fixing M01 requires this entry fixed first)

**Description:**
The lighting model has one `opacity` value per block type and no concept of a block that occupies only part of its cell
(`LIGHTING_SYSTEM_OVERVIEW.md` §"Conditionally Opaque Blocks": *"We have no block types with directional transparency. …
If stairs, slabs, or other partial blocks are added in the future, this optimization would become relevant."*).

A partial block **has** since been added. `Stone Half Slab` (`BlockIDs.StoneHalfSlab`) is authored in
`BlockDatabase.asset` with `opacity = 15`, and `IsOpaque => opacity >= 15` (`Data/JobData.cs`, `Data/BlockType.cs`),
so a half slab is treated as a *full* light blocker despite filling half its cell. Two consequences:

1. **Sky light stops at the slab.** `LightAttenuation.Attenuate` charges the destination's opacity on entry
   (`max(0, source - max(1, opacity))`), so entering a slab cell costs the full 15 — the cell stores no propagatable
   value and everything below a slab goes dark, as if it were a solid cube.
2. **Ambient occlusion darkens at maximum.** `MeshGenerationJob.SampleNeighborLight` and `CalculateCornerLights` branch
   on the `IsOpaque` **boolean**: an opaque sample contributes `sun=0, r=g=b=0` and suppresses the corner's diagonal
   term. Every AO corner that touches a slab therefore receives the hardest possible darkening, regardless of the fact
   that light physically reaches that corner through the slab's empty half.

Effect 2 is what makes rotated slabs look wrong even where the surrounding cells are fully lit: a ring of slabs around a
sky-lit cell mutually max-darken each other's faces.

**Reproduction Steps:**

1. Dig a one-block-deep pit in flat, sky-lit terrain (the centre cell reads sky light 15).
2. Place a `Stone Half Slab` in each of the four cells around the pit, rotated so each slab's solid half faces the pit
   (`Facing6Roll2` metadata `0x03`, `0x0B`, `0x13`, `0x1B` — facing 3 = Bottom, rolls 0–3).
3. Observe with smooth lighting enabled: the slab faces are darkened far below what the neighbouring light levels justify.

**Root Cause:**
Confirmed by inspection, not yet by a failing scenario. `opacity = 15` on a block that does not fill its cell, combined
with a boolean `IsOpaque` gate in both the BFS and the AO sampler. The graded part of the model already exists
(`LightAttenuation` is a per-level cost, not a boolean), so the missing piece is a block-level notion of "does not fill
its cell" that keeps such a block out of the `IsOpaque` fast paths and gives it a traversal cost below 15.

**Scope note:** full *directional* (per-face) occlusion — the `hasDirectionalOpacity` design the architecture doc
sketches — is a strictly larger change and is **not** required to fix the artifact above. A non-directional partial-block
opacity is sufficient; per-face occlusion remains a follow-up.

**Repro scenario:** **`K20a`** (lighting suite, `LightingValidationSuite.PartialBlocks.cs`) — landed
2026-08-07 by VO-2 and **red for the documented reason**. A two-deep shaft in a superflat floor capped
by a half slab at metadata `0x03` (vertical): daylight must reach the voxel below the slab's open half,
and reads sky 0 today. Asserted as reach / no-reach rather than an exact level, so it does not restate
the cost formula. Shipped with three tripwire baselines that must stay green through the fix — **B101**
(an *unrotated* slab still blocks daylight below it — the guard against "fix this by making slabs
transparent"), **B102** (full opaque cube blocks), **B103** (an uncapped shaft is lit, so the other
three cannot pass vacuously).

**Fix phases:** that same plan — **VO-3** (directional occlusion in the BFS) and **VO-4** (the
directional cross-chunk support/veto that VO-3 is not shippable without), with **VO-7** owning the
world-version bump and relight. The plan's §4 D1 records why a new `VoxelShape` descriptor was
rejected in favour of deriving occlusion from the existing `BlockCollisionBounds` — do not
re-litigate that without reading it.

**Testing environment:** Editor, smooth lighting enabled, August 2026.

---

## Bug 21: A Sealed Partial-Block Light Shaft Leaves Its Sky Column Permanently Lit

**Severity:** Medium-High  
**Status:** Open  
**Found:** 2026-08-08, while authoring baseline B105 for `VO-4`.  
**Related:** Bug 20 (this is a consequence of the `VO-3` sky-column fix, not of the cross-chunk work `VO-4` covers)

**Description:**
Since `VO-3`, a vertical half slab admits an **undimmed** sky column through its open half — sky 15 all the
way down, confirmed in game and guarded by baseline `B104`. Sealing that shaft afterwards (rotating the slab
solid-side-down, or replacing it with any opaque block) must darken the column beneath it. It does not: the
column stays at **15 forever**, and every voxel it lights stays over-bright with it. Measured on a
single-chunk room: after sealing, the probe reads 15 where the borderless oracle says 11, with 550 voxels
divergent and the field stable (converged in 2 frames).

This is the **removal** counterpart to what `VO-3` fixed for placement. It is *not* a cross-chunk defect —
it reproduces with no chunk boundary anywhere in the world.

**Root Cause:**
Classified, not inferred, by differential controls:

1. `IsLightObstructing` is `Opacity > 0`, and a half slab is authored `opacity = 15`, so the slab **already
   sits in the heightmap** before it is sealed. Sealing it therefore does not move the heightmap, so
   `RecalculateSunlightForColumn` — the authority for sky-light removal — never re-runs for that column.
2. `PropagateDarkness` cannot finish the job either: it unwinds light by following exact
   `neighbor == old − cost` decrement chains, and a flat 15 column has none.

`VO-3` left `IsLightObstructing` whole-block deliberately, reasoning that "the BFS carries the undimmed
column down anyway, so the field is correct; the heightmap merely stays conservative". That reasoning holds
for placement and **fails for removal** — a conservative heightmap also means the column's removal authority
never fires.

**The controls are what pin this to partial blocks** (all three legs are in the repro scenario):

| Shaft block                 | Column before sealing | After sealing        | Verdict                    |
|-----------------------------|-----------------------|----------------------|----------------------------|
| Glass (full cube, opacity 0) | 15 (undimmed)        | 11 = oracle ✅        | Not light-obstructing, so the heightmap moves and the recalc runs |
| Water (opacity 2)           | 12 (gradient)         | 11 = oracle ✅        | Has a decrement chain for the darkness wave |
| **Vertical half slab**      | **15 (undimmed)**     | **15, oracle 11 ❌**  | Light-obstructing *and* flat — neither mechanism fires |

So it is neither "undimmed columns cannot be removed" (Glass disproves that) nor "opaque shafts cannot be
removed" — it is specifically a block that is light-obstructing by opacity while transmitting light by shape.

**Reproduction Steps:**

1. Roof a room with opaque blocks and leave one cell holding a `Stone Half Slab` rotated vertical
   (`Facing6Roll2` metadata `0x03`), so the column below it reads sky 15.
2. Rotate that slab solid-side-down, or replace it with any opaque block.
3. Observe the column below stays fully lit. Any other block update in the chunk does **not** clear it —
   the column recalculation is not triggered by an edit that does not move the heightmap.

**Repro scenario:** **`K21a`** (lighting suite, `LightingValidationSuite.PartialBlocksCrossChunk.cs`) —
single-chunk minimal form, red for the documented reason, shipped with the Glass and Water controls above so
a red cannot be mistaken for a broken fixture.

**Fix options (not yet chosen — needs a scope decision):**

- Make `IsLightObstructing` directional, so a vertical slab stops registering in the heightmap. This is the
  root fix, and it is exactly what `VO-3` scoped out: it touches `ChunkData` heightmap maintenance, terrain
  generation, and the LI-2 band derivation.
- Or trigger a column recalculation on any edit that changes a cell's *occlusion* even when the heightmap is
  unmoved — narrower, but it needs a notion of "occlusion changed" that the voxel-edit path does not have today.

**Testing environment:** Editor, lighting validation harness, August 2026.
