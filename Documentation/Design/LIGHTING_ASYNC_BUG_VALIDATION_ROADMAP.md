# Lighting Async-Bug Validation Roadmap (AS-1 … AS-5)

> **Status:** In progress — **AS-1 CLOSED 2026-07-04** (Bugs 13 + 14 both reproduced, fixed, confirmed
> in-game, and archived — see §3 outcome; suite at B61, 53 baselines). AS-2 … AS-5 remain proposals,
> plus the **§10 harness-hardening follow-ups HF-1 … HF-4** filed from the AS-1 session's lessons.
> **Created:** 2026-07-03 (async-testability analysis session, repo @ `a458173`)
> **Scope:** making the async-flavored open lighting bugs (**Bug 05 / Bug 09 / Bug 13**,
> [LIGHTING_BUGS.md](../Bugs/LIGHTING_BUGS.md)) testable, and closing the async surfaces the
> synchronous lighting validation suite does not model.
> **Companion docs:**
> [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
> (findings B3 / B6 / C8 reference this file),
> [VALIDATION_SUITE_COVERAGE_ROADMAP.md](VALIDATION_SUITE_COVERAGE_ROADMAP.md) (AS-2 is the
> lighting-slice embryo of NS-3).

---

## 1. The decomposition that makes this tractable

The core architectural fact (verified 2026-07-03 against `WorldJobManager.cs` and the
chunk-pipeline rules): **production lighting has no shared-memory concurrency by design.**

- Job inputs are snapshotted on the **main thread at schedule time**
  (`ChunkData.FillJobLightMap` / `FillJobVoxelMap`, LI-1 padded gathers).
- Results merge on the **main thread at completion**
  (`WorldJobManager.ApplyLightingJobResult` → `ChunkData.ApplyJobLightMap`,
  `ProcessLightingJobs` @ `WorldJobManager.cs:892`).
- Chunk state flags are **main-thread-only** (`.agents/rules/chunk-pipeline.md`: "Never mutate
  flags from inside a job").

"Async" therefore decomposes into exactly four classes, each with a different testability verdict:

| # | Async class                                                                                                                                         | Sync-testable?                                                                              | Covered today?                                     | Item |
|---|-----------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------|----------------------------------------------------|------|
| 1 | **Frame-boundary interleaving** (which frame a job completes; main-thread events in between)                                                        | ✅ yes — logical interleaving                                                                | ✅ exhausted (Bug-09 fleet, fidelity §5)            | —    |
| 2 | **MT-2 event-driven promotion** (parked chunks re-enter only via `Flag`/promote/fail-safe)                                                          | ✅ yes — logical interleaving                                                                | ❌ **not modeled at all** (fidelity B6)             | AS-2 |
| 3 | **Genuine cross-thread surfaces** (`ChunkJobArrayPool` buffer reuse across concurrent jobs; `LightWorkScheduler.Flag` from deserialization threads) | ⚠️ not with `.Run()` — but testable in-editor via real `Schedule()` + equivalence-to-serial | ❌ (fluid system has the pattern; lighting doesn't) | AS-4 |
| 4 | **IL2CPP / real-timing residue** (memory ordering in managed code, wall-clock job latency under load, full actor set at production frequencies)     | ❌ **structurally impossible in the editor**                                                 | ❌                                                  | AS-5 |

Two additional facts ground the per-bug plans:

- **MT-2 (`LightWorkScheduler`, shipped 2026-07-02) postdates the fidelity audit (2026-06-13/14)
  and the entire Bug-09 sync-repro campaign.** The frame simulator's scheduling phase
  (`LightingFrameSimulator.cs` Phase 2) still scans `AllChunkCoords()` every frame — the
  pre-MT-2 model. Production visits only the ready set; parked chunks return via promotion
  events (`World.cs:1553-1701` scan; park sites at `World.cs:1615/1626/1695`; completion
  promotion at `WorldJobManager.cs:1099`). The harness even nulls the flag callback
  (`LightingTestWorld.cs:97-98`), so the flag→staging→ready event path is structurally absent.
- **Bug 09's aggravating environment changed under it**: TG-4 fluid-Burst (Phases 3/4a/4b,
  June 2026) replaced the managed fluid tick that originally flooded the mod queues, and MT-2
  replaced the scheduler it raced against. Re-verify the bug still reproduces in a current
  IL2CPP build (AS-5 doubles as that verification) before deep investment.

### Suite bookkeeping (applies to every item)

- **Protocol:** the `validation-driven-bugfix` skill — deterministic repro first, prove-red
  before trusting green, promote repros to baselines after in-game confirmation.
- **Numbering:** the lighting suite is at **B59** (B56–B59 = the promoted AS-1 slab family). New
  baselines take **B60+**. The retired numbers B17–B21 / B23–B25 stay unused (fidelity §5).
- **Expected-red scenarios** register via `AddKnownBugScenarios` in
  `Assets/Editor/Validation/Lighting/LightingValidationSuite.KnownBugs.cs` (reported as
  warnings, not regressions).
- **Workflow gotchas:** newly created `.cs` files are invisible to `dotnet build` until Unity
  imports them; the menu suite can run stale code after `IsCompiling == false` — confirm
  red/green flips with a fresh `Unity_RunCommand` wave.

---

## 2. Items, ranked by expected value

| Item | One-liner                                                                     | Target                                                | Chance of a red        | Effort |
|------|-------------------------------------------------------------------------------|-------------------------------------------------------|------------------------|--------|
| AS-1 | Bug 13 termination scenario — sync repro believed feasible, never attempted   | Bug 13                                                | **High**               | 🟢     |
| AS-2 | Route the frame simulator through a real `LightWorkScheduler` (MT-2 layer)    | missed-promotion stalls (Bug-09-shaped symptom class) | Medium                 | 🟡     |
| AS-3 | Staggered generation-wave fuzz — Bug 05's untried axis                        | Bug 05                                                | Medium                 | 🟡     |
| AS-4 | Lighting parallel-determinism gate (real `Schedule()`, equivalence-to-serial) | pool aliasing / Burst races                           | Low (guard value high) | 🟡     |
| AS-5 | In-build `LightingStress` rig + instrumentation (the only path for class 4)   | Bug 09 residue                                        | —                      | 🟡–🔴  |

**Sequencing:** AS-1 first (cheapest, could red today). AS-2 second (new guard class + NS-3
embryo; also unlocks AS-3's scheduler-mode variant). AS-4 and AS-3 in either order. AS-5 whenever
a player-build session is available; its "re-verify Bug 09 exists" half is cheap and should run
early.

---

## 3. AS-1 — Bug 13 termination scenario (sync repro attempt)

**Target:** [Bug 13](../Bugs/LIGHTING_BUGS.md) — large suspended opaque slab never settles
(oscillating cross-chunk skylight; live-lock, not a wrong-but-static field).

**Why feasible synchronously.** The suspected mechanism is 100% main-thread orchestration
logic, all of it *shared* with the harness: unstable chunks re-flag via
`LightingJobProcessor.IsEffectivelyStable` (real cross-chunk mods ⇒ not stable ⇒
`HasLightChangesToProcess = true` ⇒ reschedule), applied mods enqueue BFS wake-ups that
re-flag targets, edge-check rounds decrement through the real `ChunkData` flags. Deterministic
non-termination is a logic property, not a race — and the in-game repro is *already
deterministic* (`FluidStressController` with an opaque floor and `REGION_CHUNKS ≥ 3`, see the
bug entry). The bug doc's assumption that this needs the async wave was written before this
analysis; nobody has actually attempted the sync scenario. Production even carries diagnostic
counters added to characterize this exact churn (`LastCrossChunkModsApplyRouted/Effective` +
per-op `LastEff*` breakdown, `WorldJobManager.cs:899-905` / `996-1002`).

**Implementation.**

- New file `Assets/Editor/Validation/Lighting/Baselines/LightingValidationSuite.Baseline.Bug13Slab.cs`
  (partial-class pattern of `LightingValidationSuite.Baseline.C3Darkening.cs`). Register first
  as an expected-red known-bug scenario in `KnownBugs.cs`.
- **Geometry:** `LightingTestWorld(gridSize: 3)` first, escalate to 5 (grid size is a
  constructor parameter; the in-game threshold was ≥ 3 chunks). `FillSuperflatFloor(10, Stone)`;
  full opaque slab spanning **all** chunks at y=100 (`FillBox((0,100,0) → (max,100,max), Stone)`,
  mirroring the stress harness's y-100 stamp); `RecalculateHeightmaps()`.
- **Two variants** (author both):
    1. *Generation-wave*: slab present from the start → `RunInitialLightingParallel()` → frame-sim
       convergence. Cheapest.
    2. *Dynamic-stamp* (faithful to the in-game repro, which stamps onto an already-settled
       world): converge a slab-less world first, then `PlaceBlock` the slab incrementally
       (chunk-by-chunk or row-by-row), running `sim.RunFrame(...)` ticks between stamps, then
       assert termination.
- **Assertions (termination is primary, correctness secondary):**
    - `sim.RunToConvergence(maxFrames: 500) != -1` — the anti-live-lock property. Run it under
      unlimited budget *and* `RunToConvergenceSingleSlot` (starvation pressure).
    - `LightingAssert.MatchesOracle(world, LightingOracle.Solve(world), …)` after convergence.
    - Sweep `CompletionOrder.Shuffled` over ~50 seeds via `FindFailingSeed` (oscillation may be
      order-sensitive).
- **Definition of done:** scenario authored + registered expected-red; outcome recorded in
  Bug 13's entry.

**Outcome branches — both are progress:**

- **RED** → first faithful Bug-13 repro; proceed with `validation-driven-bugfix` (fix until
  green with all baselines green, promote to baseline after in-game confirmation on the
  fluid-stress opaque-floor config).
- **GREEN** → a fidelity finding: enumerate the sim-vs-production deltas as the suspect list —
  mesh-rebuild interleaving, the world-level `SunlightRecalculationQueue` routing (fidelity A3
  remainder), fluid re-flow mods, real per-frame budgets, `PromoteAll` cadence (AS-2) — then
  instrument the in-game `FluidStressController` repro (opaque floor) with the existing
  `Last*` counters to identify which event the sim lacks. Update fidelity doc accordingly.

**Outcome (2026-07-04): RED — implemented as K13a–K13d in
`Assets/Editor/Validation/Lighting/LightingValidationSuite.Bug13Slab.cs`** (at the suite root per the
`Bug05Canopy`/`Bug09Fuzz` convention for bug-targeted files, not the `Baselines/` path drafted above —
that name applies after promotion). Implementation deltas from the spec, and results:

- **Geometry:** the harness grid boundary IS the world boundary (out-of-grid mods are dropped), so the
  drafted "slab spanning all chunks" has NO lit perimeter — but the in-game gradient is perimeter-fed
  (the 5×5-chunk stamp sits inside a larger loaded world). An **inset** config was added: grid 5, slab =
  centre 3×3 chunks inside a sky-lit 16-chunk ring. Both configs were authored.
- **K13a/K13b (generation-wave, both geometries): GREEN** — slab-from-the-start initial lighting
  terminates (4–5 waves) on the oracle. The live-lock needs the dynamic stamp, not the initial wave.
- **K13c (dynamic stamp onto a settled world, inset geometry): RED** — never settles under unlimited
  (500 frames) or single-slot (1500 frames) budgets; a hash-based oscillation probe confirms the field
  **repeats an exact cycle (length 1–2) while work stays pending** — live-lock proven, not slow
  convergence (budget-escalation and forced-edge-round classifiers are built into the scenarios).
- **K13d (seeded shuffle/budget sweep): RED on both geometries** — grid-5 inset seed 0 live-locks;
  grid-3 full-grid seed 1 instead SETTLES ~32.7k voxels over-bright (worst +14 sky): the same stamp can
  also exit into static ghost light, a Bug-05-shaped terminal state.
- All 47 baselines stayed green. Next step: `validation-driven-bugfix` on Bug 13 using K13c as the
  primary red, then promote to B56+ after in-game confirmation.

**Fix (same day, 2026-07-04):** root cause confirmed by instrumentation (period-2 field cycle across the
slab's 8 ring chunks; the Bug 12 cross-seam removal initiator vs. the Bug 11 veto's in-chunk-only support
model at perimeter-fed interior seams — attribution proven by an emit-neuter test converging the repro in
2 frames). Fixed by extending the veto with **live third-party cross-chunk support**
(`CrossChunkLightModApplier.CrossChunkSunlightSupport`, emitter excluded; deferred mods now carry their
emitter). K13a–K13d green, all 47 baselines green. The sweep also surfaced a **second, independent defect**
— terminating stale over-bright ghost light — filed as **Bug 13's sibling Bug 14** with scenario K14a
(expected red). Bug 13 was confirmed in-game the same day, promoted to baselines **B56–B59**, and archived
(`_FIXED_BUGS.md` Lighting #17). **Bug 14 was then also fixed, confirmed in-game, and archived the same
day** (`_FIXED_BUGS.md` Lighting #18 — pull-back-claim verification at merge, `PullBackClaim` /
`VerifyPullBackClaims`, plus a hotfix for the border shadow-caster halo-node claim contract found by the
first in-game test): K14a red→green and promoted to **B61**, the halo-node contract pinned as **B60**, and
the **B59** sweep upgraded to assert the borderless oracle across its full 75-seed space. Fidelity finding
**C9** (flat worlds never exercise border shadow-casters) filed and closed. Suite tip: **B61, 53 baselines**.

---

## 4. AS-2 — Model the MT-2 `LightWorkScheduler` layer in the frame simulator

**Target failure class:** *missed-promotion stalls* — a parked chunk whose unblock event lacks
a promotion hook silently never re-lights until the ~1 s `PromoteAll` fail-safe (or a player
edit) rescues it. This is exactly Bug 09's symptom shape ("light missing until another update
forces it") and is currently **invisible to every suite**: the LightScheduler suite tests the
class in isolation; the lighting suite's frame simulator predates MT-2 entirely. Production
treats a recurring non-zero fail-safe count as a bug (`World.cs:1588-1591`) — this item turns
that log line into a mechanical gate.

**Implementation.**

- **`LightingFrameSimulator` opt-in scheduler mode** (constructor flag; default off so every
  existing baseline stays byte-identical):
    - Hold a real `LightWorkScheduler` instance (plain main-thread class — editor-safe).
    - Replace Phase 2's `AllChunkCoords()` scan with: `DrainStaging()` → `SnapshotReady(buffer)`
      → iterate the snapshot only. Mirror the production scan's per-chunk outcomes **by reading
      `World.cs:1560-1701` and matching each arm**: no light work → `Remove(pos)`; job already
      in flight → `MarkWaiting(pos)`; neighbors not ready → `MarkWaiting(pos)`; otherwise
      schedule (budget-capped; starved chunks stay ready).
    - **Promotion hooks**, mirroring production sites: `CompleteLightingJob` →
      `PromoteNeighborhood(coord)` (production: `WorldJobManager.cs:1092-1100`);
      `MarkNeighborsReady` / `MarkChunkLoaded` → `PromoteNeighborhood` (production analog:
      generation / disk-load completion); flag transitions → wire
      `ChunkData.OnLightWorkFlagged` to `scheduler.Flag` for the harness lifetime instead of
      nulling it (`LightingTestWorld.cs:97-98`; the real `ChunkData` setters already fire the
      callback — fidelity B4 closed on that — so this Just Works; still restore the saved
      callback in `Dispose`).
    - **Fail-safe is a switch, default OFF in scenarios.** Expose `sim.FailSafePromoteAll()`
      (manual) or `promoteAllEveryNFrames`. The load-bearing assertion of the whole item:
      *scenarios must converge with the fail-safe off.* Any scenario that only converges with
      it on has found a missing promotion hook.
    - New `FrameResult` counters: `ChunksParked`, `ChunksPromoted`, `StagingDrained`.
- **Key-space note:** the scheduler keys on voxel-origin `Vector2Int` (`ChunkData.Position`),
  the harness on grid coords — reuse the harness's existing conversion (discovery step: confirm
  `TestChunk`'s `ChunkData.Position` is set to the voxel origin).
- **Migration:** re-run the frame-sim-based Bug-09 fleet (B15/B16/B22/B26–B29/B40/B41) in
  scheduler mode as a second pass (new baseline numbers or a mode toggle inside each — prefer a
  shared helper that runs a scenario body in both modes).
- **Prove-red:** temporarily disable one promotion hook (e.g. comment the
  completion-`PromoteNeighborhood`) → a border-lamp scenario must stall (converge only via
  fail-safe) → red under the fail-safe-off assertion. Restore → green.
- **Definition of done:** scheduler mode + fleet second pass green with fail-safe off, prove-red
  documented, fidelity finding **B6** flipped to CLOSED.

**Cross-reference:** this is the lighting slice of
[VALIDATION_SUITE_COVERAGE_ROADMAP.md](VALIDATION_SUITE_COVERAGE_ROADMAP.md) NS-3 (chunk
lifecycle suite), which already names `LightingFrameSimulator` as its embryo — build AS-2 with
NS-3's convergence/flag-pairing assertion families in mind.

---

## 5. AS-3 — Staggered generation-wave fuzz (Bug 05's untried axis)

**Target:** [Bug 05](../Bugs/LIGHTING_BUGS.md) — persistent chunk-border shadow patches in
dense biomes.

**Why this axis.** The geometry axis is exhausted (fidelity C2: canopy fuzz, all seeds
converge), but every existing scenario lights all chunks in **one simultaneous wave** with
`neighborsDataReady: true` throughout (C1's documented remainder). Production lights a **moving
frontier**: chunks join incrementally as generation completes, readiness flips per-chunk, and
each chunk's 2 `RemainingEdgeCheckRounds` are consumed at *different relative times* — dense
canopies at a frontier are exactly Bug 05's habitat. This is pure logical interleaving —
fully sync-modelable.

**Implementation.**

- New file `Assets/Editor/Validation/Lighting/LightingValidationSuite.Bug05Stagger.cs`
  (or extend `Bug05Canopy.cs`).
- **Driver-level first — zero harness changes:** all chunks exist in the grid with their final
  geometry (from `Bug05CanopyCase.FromSeed`); "not yet generated" is modeled at the scheduling
  level only. Maintain a joined-set in the scenario driver; before a chunk joins, don't flag
  its initial lighting; a chunk's `TestChunk.NeighborsReady` (via
  `MarkNeighborsNotReady`/`MarkNeighborsReady`) is derived from whether its neighbors have
  joined. Seed-derived join order and cadence (one join every k frames), interleaved with
  `sim.RunFrame(...)` under seeded shuffle + budget.
- **Modeling limit (document in the scenario docstring):** voxel data pre-exists — the stagger
  models "populated but not yet lit" (which is precisely the production initial-lighting
  environment per the `WorldJobManager.cs:1026-1035` comment), not "terrain absent" (that is
  B1/unload territory, already covered).
- **Tiering like C1/C2:** a small sweep (~25 seeds) as baseline **B56+** on every suite run; a
  nightly menu item `Minecraft Clone/Dev/Validate Lighting Engine (Bug 05 Staggered Wave Fuzz)`
  at 200+ seeds. On a failing seed, re-run with
  `RunInitialLightingParallelForcedEdgeRounds` (exists — C2's classifier) to distinguish
  round-budget shortfall from an unreachable pocket.
- **After AS-2 lands:** add a scheduler-mode variant where a join event is a promotion — the
  faithful MT-2 frontier.
- **Definition of done:** fuzz authored and tiered; outcome recorded in Bug 05's entry either
  way (RED → faithful repro, proceed validation-driven-bugfix; GREEN → the staggered axis is
  documented clean, fidelity C8 updated, and Bug 05's remaining hypothesis narrows to class 4).

---

## 6. AS-4 — Lighting parallel-determinism gate (real `Schedule()`)

**Target failure class:** the only plausible *real* data races left in the lighting path —
pooled-buffer aliasing (`ChunkJobArrayPool` double-hand-out / premature reuse while a job still
holds the buffer) and Burst-level nondeterminism in `NeighborhoodLightingJob`. Pattern
precedent: `Assets/Editor/Validation/Behavior/FluidParallelDeterminismValidation.cs`
(8 concurrent × 6 rounds, byte-compare vs serial baseline). The honest way to get async
coverage into an editor suite: **assert equivalence to serial, don't try to deterministically
reproduce a race.**

**Scope note (what this can and cannot catch):** Burst-compiled job code in the editor is the
same native codegen as the player — Burst-side races ARE in scope. Managed-side IL2CPP
differences are NOT (editor runs Mono; see §8).

**Implementation.**

- New file `Assets/Editor/Validation/Lighting/LightingParallelDeterminismValidation.cs`, menu
  `Minecraft Clone/Dev/Validate Lighting Parallel Determinism`.
- **Serial baseline:** fixed geometry (a canopy-fuzz seed + a lamps-at-all-borders layout);
  run each chunk's job through the existing flight path (`.Run()`), capture per-chunk
  `LightMap` bytes + sorted `Mods` + `IsStable`.
- **Parallel path:** add `LightingTestWorld.ScheduleLightingJobs(coords)` — build all N flights
  (input snapshots happen on the main thread exactly as production), then `.Schedule()` all N
  job structs holding the `JobHandle`s; complete all; then merge via `CompleteLightingJob` in a
  **fixed serial order** (merge order is the frame simulator's axis, already covered — this
  gate isolates job-*execution* concurrency). Note the harness runs the job inside
  `CompleteLightingJob` today (`BeginLightingJob` only snapshots — `LightingTestWorld.cs:28`),
  so the flight needs an optional pre-scheduled-handle path.
- **Pool fidelity upgrade (the actual point):** allocate `Map`/`LightMap`/`PaddedVoxels`/
  `PaddedLight` through a real `ChunkJobArrayPool` instance instead of the current fresh
  `Allocator.Persistent` arrays (`LightingTestWorld.cs:404-456`), returning buffers between
  rounds so cross-round reuse under overlapping job lifetimes is exercised (Get/Return stays
  main-thread, as in production).
- **Stress shape:** 8+ concurrent × 6 rounds; byte-compare against the serial baseline each
  round; report first divergence with chunk + voxel index.
- **Sibling micro-test (LightScheduler suite):** N background threads hammer
  `LightWorkScheduler.Flag(pos)` with unique positions while the main thread calls
  `DrainStaging()`; assert zero lost flags. Guards the `ConcurrentQueue` staging contract —
  the scheduler's only thread-safe member (`LightWorkScheduler.cs:19-21`), fed by real
  background deserialization threads in production.
- **Definition of done:** gate green across rounds; pool-backed buffers in use; divergence
  reporting proven by a deliberate sabotage run (e.g. return a buffer early → must red).

---

## 7. AS-5 — In-build `LightingStress` rig (the only path for class 4)

**Target:** the Bug 09 residue — and the *existence check* the docs already call for ("either a
genuine async race … or no longer present"). Precedent: `RuntimeMode.FluidStress` +
`Assets/Scripts/Benchmarks/FluidStressController.cs`.

**Implementation.**

- New `RuntimeMode.LightingStress` + `Assets/Scripts/Benchmarks/LightingStressController.cs`.
- **Scenario (automates the manual repro from Bug 09's entry):** fixed-seed ocean world; locate
  an underwater chunk-border column; loop: place an emissive block (via `BlockIDs` constants —
  the entry used a Jack O' Lantern) at the border voxel in chunk A → wait k frames → break →
  wait k frames; sweep k ∈ {0..5} across batches, hundreds of cycles per batch (fluid re-flow
  into the broken voxel is part of the scenario — that was the aggravating factor).
- **Self-check oracle:** every M cycles — **snapshot the 3×3 neighborhood light field FIRST**
  (forcing a relight erases the evidence), then force a full relight (discovery step: find the
  existing force-relight/debug facility, else set the initial-lighting flags on the
  neighborhood), wait for settle (`HasActiveJobs == false`, no pending light work), snapshot
  again, diff. **Nonzero diff = captured repro**: write cycle #, k, seed, the
  `LastCrossChunkMods*` counter history, and the diff voxel list to a report file under
  `persistentDataPath`, then continue (overnight batch runs).
- **Build matrix:** Mono development player first (cheap logging/iteration), then IL2CPP master
  for fidelity to the original observation.
- **Interpretation:** a long unattended run with zero diffs across both builds is strong
  evidence Bug 09 is no longer present (MT-2 + TG-4 both landed after the last observation) →
  annotate the bug entry accordingly. Any diff gives the instrumentation trail that fifteen
  synchronous attempts could not.
- **Definition of done:** rig runs unattended and writes a verdict report; Bug 09's entry
  updated with the result either way.

---

## 8. Hard limits — what CANNOT be implemented in the editor suites

Accept these; route them to AS-5 instead of burning effort on a synchronous layer (this is the
standing fidelity-B3 lesson):

- **IL2CPP memory ordering / codegen of managed orchestration code.** The editor runs Mono.
  No editor harness — sync or scheduled — can exhibit IL2CPP-specific behavior. Only an IL2CPP
  player build (AS-5) covers it.
- **Real Burst job timing.** The simulator models job *age* logically (`MinAge` predicates) and
  AS-4 catches outcome divergence, but wall-clock latency windows that require a job to
  physically overlap specific main-thread work at production load cannot be manufactured
  deterministically in-editor.
- **The full main-thread actor set at production frequencies** (fluid tick + meshing + chunk
  streaming + player input contending within real frame budgets). Only AS-5.
- **True runtime `Dictionary`/`HashSet` iteration order.** Cannot be forced; the seeded
  Fisher-Yates shuffles are the accepted equivalent (already in place).

---

## 9. Documentation upkeep on each outcome

- Scenario turns **red** → it is a faithful repro: move the bug's entry forward per
  `validation-driven-bugfix`; after the fix + in-game confirmation, promote to a baseline and
  archive the bug via `archive-fixed-bug`.
- Scenario stays **green** → record the negative result in the bug entry *and* the fidelity doc
  (the C1/C2 precedent: a green fuzz still closes a search axis and becomes regression
  coverage).
- AS-2 / AS-3 / AS-4 completions flip their fidelity findings (B6 / C8 / B3-amendment) and
  should update the suite counts in `VALIDATION_SUITE_COVERAGE_ROADMAP.md`'s "existing
  coverage" line.

---

## 10. Harness hardening — HF-1 … HF-4 (filed 2026-07-04, from the AS-1 / Bug 13+14 session)

**Origin.** The Bug 14 hotfix exposed a "not reproducible in harness" class that AS-1's outcome analysis
did not predict: baseline B60's prove-red **failed honestly** — with the fix's guards deliberately
removed, the scenario stayed green while identical code crashed real worlds. The root is NOT a
harness/production code divergence (both run the same `ChunkData`): it is that shared out-of-bounds
accesses are a **position lottery** (silent uniform-sky read / silent wrong-voxel alias / throw, depending
on coordinates), and the crash's visible form (per-frame `ObjectDisposedException` spam) additionally
required `ProcessLightingJobs`' **production-only pass bookkeeping** (release-inside-loop /
remove-after-loop). Fidelity findings: **A5** (fail-soft accessors) and **B7** (pass bookkeeping),
plus the already-closed **C9** (flat worlds never make border shadow-casters). "Fully matching"
production is asymptotic (the standing B3 lesson); the strategy below instead makes shared code
fail-fast, shrinks the production-only surface, and widens geometry sampling.

| Item | One-liner                                                                       | Closes                  | Effort                   |
|------|---------------------------------------------------------------------------------|-------------------------|--------------------------|
| HF-1 | Editor/dev-only bounds assertions in the `ChunkData` accessors                  | fidelity A5             | ✅ DONE 2026-07-05        |
| HF-2 | Per-job fault isolation in `ProcessLightingJobs` (eliminate the cascade class)  | fidelity B7 (near-term) | 🟢                       |
| HF-3 | Border heightmap fuzz baseline (B62+) — varied heights at seams + border edits  | C9 extension            | 🟡                       |
| HF-4 | Extract the lighting pass skeleton into a shared, harness-drivable orchestrator | fidelity B7 (full)      | 🔴 — fold into AS-2/NS-3 |

### HF-1 — Fail-fast `ChunkData` accessors (editor/development builds only)

- Add bounds assertions to `GetVoxel` / `GetLightData` / `SetLightData` / `SetVoxel`
  (`ChunkData.cs:853–900`): local x/z in `[0,16)`, y in `[0,128)`; throw with the offending coordinate
  and chunk position in the message. Must compile to **zero cost in IL2CPP master** — these are the
  hottest reads in the engine (`[Conditional]`-gated helper or `#if UNITY_EDITOR || DEVELOPMENT_BUILD`).
- **Prerequisite:** a caller audit (CodeGraph `codegraph_callers` + exhaustive Grep) confirming no caller
  legitimately relies on the leniency today. Any that do are themselves latent A5-class bugs — fix first.
- **Verification:** re-run B60's prove-red sabotage (remove the Bug-14 `IsInCenterChunk` guard + the
  verifier bounds-skip) — with HF-1 in place it must go RED at every position, retroactively giving B60
  the prove-red it could not have before. Then a full suite run (all baselines green) and an editor
  play-mode frame-cost sanity check (assertions are branch-only, but measure, don't assume).

> **Outcome (2026-07-05): DONE.** `ChunkData.AssertLocalPositionInChunk` (dual `[Conditional]`
> `UNITY_EDITOR`/`DEVELOPMENT_BUILD`, throws with coordinates + chunk position) called first in all four
> accessors — including before `GetLightData`'s uniform-sky early-return, collapsing the fully-silent
> compacted-section case. Caller audit (69 call sites, 17 files): **no caller relied on the leniency** —
> every site is loop-bounded, lookup-derived, explicitly guarded, or bounded by the Burst job's
> `GetPackedData` sentinel (which also bounds every emitted mod/claim y). B60 sabotage re-run: **RED**
> (`ArgumentOutOfRangeException: local position out of range: (-1, 49, 8)` in the harness claim
> verifier — the exact halo claim the position lottery used to swallow; 1 of 53 red, sabotage-only).
> Guards restored → **53/53 baselines green with the assertions live**. Fidelity **A5 CLOSED**; C9's
> "crash not scenario-provable" residual resolved. Play-mode frame-cost sanity check: PENDING user
> play session (expected noise-level — main-thread accessor traffic is not the per-voxel hot loop;
> jobs read `NativeArray` snapshots).

### HF-2 — Per-job fault isolation in `ProcessLightingJobs`

- Wrap the per-job block so an exception: logs one error (errors = regression signal, per suite
  convention), still **releases** that job's containers and adds it to `_completedLightJobs` (so the
  after-loop removal happens), clears `IsAwaitingMainThreadProcess` in a `finally` (flag-pairing
  invariant, `.agents/rules/chunk-pipeline.md`), and lets the pass continue. A new diagnostic counter
  (`LastFaultedLightJobs`, following the `Last*` family) makes recurrences observable.
- This intentionally *eliminates* the cascade class instead of modeling it in the harness — the spam hid
  the true thrower behind hundreds of repeats and cost a full diagnosis round.
- **Scope check while there:** audit `ProcessGenerationJobs` / `ProcessMeshJobs` for the same
  release-then-remove split; apply the same isolation if present.
- **Caution:** swallow-and-continue can mask corruption — the log must be an ERROR, and the chunk should
  be left in a re-schedulable state (`HasLightChangesToProcess = true`) rather than silently dropped.
  Update `CHUNK_LIFECYCLE_PIPELINE.md` in the same commit (chunk-pipeline rule).

### HF-3 — Border heightmap fuzz (baseline B62+)

- A seeded geometry fixture varying per-column heights across all seams (the terrain shape flat worlds
  never produce — C9's lesson), plus seeded border edits, swept over ~25 seeds per suite run with a
  nightly higher-seed tier (the C1/C2/B42 pattern). Assert termination + `MatchesOracle` per seed.
- With HF-1 in place this becomes a genuine crash-class detector: the fuzz samples many lottery
  positions, the assertions make every bad one loud. Prefer building it AFTER HF-1 so a found violation
  reds immediately instead of wrong-reading.

### HF-4 — Shared lighting-pass orchestrator (deferred; fold into AS-2 / NS-3)

- The only way the harness can truly replay the pass bookkeeping (B7's full closure): extract the
  iterate/complete/release/remove skeleton into a pure orchestrator both `WorldJobManager` and the frame
  simulator drive — the same extraction pattern as `LightingJobProcessor` (A2) and
  `LightingScheduleDecision` (B2). A meaningful refactor of `ProcessLightingJobs`; do it as part of
  **AS-2** (which already restructures the simulator's frame loop around a real `LightWorkScheduler`)
  or the NS-3 chunk-lifecycle suite — not standalone.

**Sequencing:** HF-1 first (small, unlocks HF-3's detector value and B60's prove-red), HF-2 second
(independent, small), HF-3 third, HF-4 whenever AS-2 lands. On completion, flip fidelity A5/B7 and record
the new baseline numbers here and in the fidelity backlog.
