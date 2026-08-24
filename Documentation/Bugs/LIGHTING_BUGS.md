# Known Lighting related bugs

This document outlines **open** bugs related to the current lighting implementation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Validation suite:** the editor menu item `Minecraft Clone/Dev/Validate Lighting Engine`
> (`Assets/Editor/Validation/Lighting/`) runs baseline regression scenarios plus deterministic
> reproductions of the open bugs below (test-first: those scenarios assert the *correct* behavior
> and are expected to fail until the bug is fixed). Per-bug repro scenario IDs are listed in each entry.

---

> All previously listed lighting bugs (01–08, 10–21) have been fixed. See [`_FIXED_BUGS.md`](./_FIXED_BUGS.md) for details.

## Bug 22: Light-Work Fail-Safe Keeps Promoting Parked Chunks in a Fully Quiesced World

**Severity:** Low (diagnostic-visible only; no observed lighting defect)  
**Status:** Open — observed 2026-08-24, not yet root-caused

**Description:**
The ~1 s light-work fail-safe scan (`World.cs`, `_fullLightScanTimer` block) reports a **recurring non-zero
promotion count indefinitely** in a world that has fully converged and is no longer streaming. The code's own
comment states the intended reading of that signal:

> A recurring non-zero count means an unblock event lacks a promotion hook — work only progressed because of
> this backstop. Investigate before it reads as "slow lighting".

Observed: `[LIGHTING] Fail-safe promoted 4..18 parked chunk(s) to the ready set.` firing every cycle, still
firing minutes after the world went static.

**Why the reported count should be ~0.** The same block walks `worldData.ChunkValues` immediately before
`PromoteAll()` and calls `LightWorkScheduler.AddReady` for every populated chunk with
`HasAnyLightingWork`; `AddReady` removes the position from `_waiting`. `PromoteAll` then returns
`_waiting.Count`. So anything it reports is a parked entry the walk could **not** re-ready — i.e. a position
that is not a populated, flagged chunk at that instant.

**Measured state at the time (live editor, two samples 975 frames apart — byte-identical, so the world was
genuinely static, not mid-settle):**

| Quantity | Value |
|---|---|
| Chunks in map / populated / unpopulated | 787 / 787 / **0** |
| Populated chunks with lighting work | 125 |
| Scheduler ready / parked | 0 / **125** |
| Lighting / generation / mesh jobs | 0 / 0 / 0 |
| Fail-safe promotions per cycle | **4–18** |

Every parked position should therefore have been re-readied by the walk, leaving `PromoteAll` at 0. It was
not. `WorldData.ChunkValues` was confirmed to be the same collection as `WorldData.Chunks`, so a
collection mismatch is ruled out.

**Leading hypothesis (unverified): stale parked entries for unloaded positions.** The session that produced
it involved **high-speed flight followed by a reversal back over already-generated chunks** — heavy chunk
unload/reload churn. A position parked by the scan whose chunk is then unloaded is invisible to the
`ChunkValues` walk, so it stays in `_waiting` and is re-promoted every cycle. The scan's stale-entry arm
(`!worldData.TryGetChunk` → `Remove`) should drain such an entry the next time it is visited, so a
*persistent* population of them additionally implies the drain is not reaching them — a per-frame quota or
break in the ready-set loop is the obvious suspect. Both halves are unconfirmed.

**What is actually new here.** The first half of that hypothesis is not a guess — it was characterised
during LP-3's soak (2026-08-23), which established that unload cycling mints parked entries whose chunk is
gone or flag-less, and that **non-zero promotion counts *during* streaming or unload churn are therefore
expected** (3–26 observed then). The same work established the test that separates noise from signal:
**stand still, let the world quiesce, and confirm zero promotions.** LP-4's in-game session met it, and so
did the LP-2 flight before it.

This session is the **first time that quiet-window test has failed** — 4–18 promotions per cycle, minutes
after motion stopped, with two censuses 975 frames apart proving the world static. So the open question is
not "why do parked entries appear" (answered) but **"why do they not drain once churn stops"**, which is
what makes it worth a bug entry rather than a known-noise note.

**Reproduction Steps:**

1. Enable `settings.enableDiagnosticLogs` (the message is gated on it).
2. Load or generate a world and fly at **elevated speed** across un-generated terrain, then **turn back**
   over the chunks just generated, so chunks unload and reload.
3. Stop moving and let the world fully converge (0 jobs, 0 pending lighting work in render range).
4. Watch the console: `[LIGHTING] Fail-safe promoted N parked chunk(s)` keeps firing every ~1 s.

**Not caused by LP-5.** Present in an earlier session in the same editor log whose stack traces carry
pre-LP-5 line numbers (`World.cs:2436`) and a different load radius — 19 occurrences there, before any LP-5
play session. LP-5's own startup-only sessions produced none.

**Impact.** No lighting defect was observed alongside it: the render set was fully converged (441/441 chunks,
zero pending work of any kind, zero jobs in flight) and no exceptions, stalls or `exceeded max iterations`
occurred. The cost is a wasted full re-promotion each second plus the scan work it causes, and — more
importantly — a **permanently noisy diagnostic**, which erodes the signal the comment above depends on.

**Next diagnostic step.** The decisive measurement is enumerating `_waiting` and classifying each entry:
does its position still resolve via `TryGetChunk`, is that chunk populated, does it carry any lighting
work? The expected split tells the two candidate causes apart — unresolvable positions mean stale entries
for unloaded chunks, resolvable-and-flagged ones mean the ~1 s walk is somehow not reaching them.

`LightWorkScheduler` exposes only `ReadyCount`/`WaitingCount`, and `Unity_RunCommand` cannot read private
state (its analyzer blocks `System.Reflection`) — but **no production accessor is required**. Use the
`McpEval` harness (`Assets/Editor/Dev/McpEvalScratch.cs`), which is an ordinary editor script with full
namespace access including reflection: write the probe into `Run()`, then invoke
`Minecraft Clone/Dev/MCP Eval` and read the `[MCP-EVAL]` console output.

**Order matters, because this bug only exists in a dirtied live session.** Editing the scratch file
triggers a domain reload, which ends play mode — so write and compile the probe *first*, then enter play
mode, fly the repro above, stop moving, and only then run the menu item. Running it against a freshly
loaded world will show nothing.

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

**Candidate synchronous repro lead (2026-07-12 — from the C11 interrupted-reconciliation fuzz) — RESOLVED 2026-07-12: harness-fidelity artifact, NOT a real defect.** the fuzz's diagonal 4-chunk-corner geometry (two equal-color lamps at a corner, e.g. `(31,64,31)`/`(32,64,32)`, water, ≥3 interrupted break/re-place cycles with a held neighbor flight + under-budgeted waves) leaves a stable, **under-bright** region *above* the water near the corner — cross-chunk blocklight the surviving lamp legitimately casts that is never delivered by the interrupted
schedule (a clean relight of the identical final voxel state matches the borderless oracle exactly, so it is a delivery/schedule gap, not a BFS/oracle defect). This is the Bug 09 shape (cross-chunk blocklight lost after rapid place/break at a border), reproduced **synchronously** in the harness — the first such lead. Not yet confirmed as Bug 09 vs a harness-fidelity artifact (the diagonal corner is not a face-adjacent pair, and the fuzz recipe hand-schedules only 2 of the 4 corner chunks, unlike production which wakes all neighbors). The C11 fuzz (
baseline **B91**) is deliberately scoped to face-adjacent seams and excludes this geometry.

> **Resolution (2026-07-12, classified via `Unity_RunCommand`):** hypothesis (b) — a **harness-fidelity artifact**, not a synchronous reproduction of Bug 09. The under-delivery exists only because the fuzz recipe settles with plain `LightingTestWorld.RunWaveToConvergence`, which deliberately does **not** drive the post-stabilization edge-check *re-add* rounds that production runs. Decisive classifier: replaying the identical interrupted schedule and then driving a **single** re-granted edge-check round (`RunReGrantedEdgeCheckRound` — exactly what
`LightingFrameSimulator.RunToConvergence` runs at grid quiescence, and the code path production's **Bug-05** border-column edge-check re-grant takes after the recipe's final `PlaceBlock(lampA, Water)`, an opacity edit at local `(15,·,15)`) heals the field **completely** — 41 → 0 divergent voxels, probe `(29,68,31)` G 2 → 4 = oracle. This is the §3.7 invariant in action (see [`LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) §3.6/§3.7): cross-chunk *placement* (**under-bright**) is always re-addable by an edge check; only the
> interrupted `RunWaveToConvergence` settle, which omits that pass, leaves it stranded. Since this class is pure under-bright and self-corrects in one edge round, it does **not** survive production's machinery. **Bug 09 stays open** — a genuine async race that survives the edge-check re-add still has no faithful synchronous repro; this lead was not it. (Harness gap noted, no B91 change: the fuzz's settle omits edge-check rounds by design and is seam-scoped, where they are unnecessary; a future diagonal-corner fuzz axis, if added under **AS-4**/**AS-5**,
> must settle through an edge-check-inclusive driver to avoid re-flagging this same artifact.)

**Testing environment:** IL2CPP master build, ocean biome (underwater), June 2026.

