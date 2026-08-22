# Validation Suite Coverage Roadmap — Uncovered Systems, Ranked

**Version:** 1.5  
**Date:** 2026-07-02  
**Status:** **Living backlog.** `NS-4`, `NS-5`, `NS-7` and `NS-7b` are ✅ complete (NS-4 2026-08-03; NS-5 at the CP-2
close-out; NS-7 and NS-7b both 2026-08-20 — the migration chain is now covered end to end, and NS-7b's first
run found `SERIALIZATION_BUGS` §10, fixed 2026-08-21 and archived as `_FIXED_BUGS.md` Serialization 07, its `K10`
repro promoted to baseline `B25`). `NS-1` is **✅ COMPLETE (2026-08-21, parts 1–5)** as the standalone
`Validate Serialization Round-Trip` suite, on top of CP-3's robustness slice — the top-priority item of this
roadmap is closed, and the two open serialization bugs it found (`§04`, `§08`) are guarded by repros.
`NS-3` is 🟡 **slice 1 shipped (2026-08-22)** — 6 baselines, grown scenario-wise from here.
`NS-2`, `NS-6` and `NS-8`…`NS-11` remain proposals.
Existing-coverage counts are re-verified against a real `Validate All` run each time they are touched.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> Which systems currently have **no validation suite** and deserve one, ranked most → least
> important by the severity of the failure class each suite would guard and by how many queued
> backlog items (`PERFORMANCE_IMPROVEMENTS_REPORT.md`) are blocked on an ad-hoc version of the same
> gate. Produced by the seventh-pass audit (2026-07-02), which found the six existing suites
> architecturally excellent (`VS-*` items are operational only) — the remaining risk is *coverage*,
> and it is concentrated in the systems below.
>
> Status: **Living backlog.** NS-1 is partially seeded (CP-3's robustness slice, 2026-07-22); NS-5 ✅
> (CP-2 close-out, 2026-07-22), NS-4 ✅ (2026-08-03) and NS-7 + NS-7b ✅ (both 2026-08-20) are complete — see the per-item status lines; the rest are proposals.

**Existing coverage (for contrast, counts re-verified 2026-08-22 against a full `Validate All` run — 567 baselines / 25 suites, 0 failures, 0 isolation violations, 2 known-bug repros outstanding (`K04`/`K08`, `SERIALIZATION_BUGS` §04 and §08); 3 min 9 s wall clock, of which Lighting is the dominant share):** Lighting (106 — the last seven are the fidelity **C14** mixed-channel mirrors B108–B114), Meshing (57 — including the **MP-\* orchestration** baselines B24–B27 and B31–B33, the meshing-side groundwork this roadmap's NS-3 convergence family names, B34–B36 guarding the chunk load-animation toggle, MP-7's neighbor-map permutation guards B37–B39 — one of which guards a direction→offset table feeding the **lighting** schedule too — and MH-13's B40, the same permutation guard for the eight neighbor
**light** maps), Behavior/fluid tick (17, incl. determinism gates), Placement (29 — VQ-2's six ray-march guards and VQ-3's five sub-voxel guards landed here), **Physics Solver (26 — NS-4, incl. the retired `PLAYER_BUGS` §04's tripwires B18/B19, its promoted repro B20–B23, `PH-1`'s step-0 horizontal-aggregation guard B24 and its gather-envelope guard B25, and `PH-2`'s B26 pinning that `CalculateVelocity` never writes the transform)**, MeshBuildQueue (9), LightWorkScheduler (9), Chunk Math (72 — the NS-5 G1–G4 coverage extension added 16: 6 padded-volume, 4 flattened-index, 4 region-filename, 2 legacy-V1-encoder), Chunk Unload Decision (9), Pool Prune Decision (5), Pipeline Backpressure (22), **Chunk Pipeline (6 — NS-3 slice 1, the pipeline state machine: `B1` is the harness's own deadlock prove-red)**, Save Durability (13), Deserialization Robustness (9), **Serialization Round-Trip (16 — NS-1 parts 1–5, plus the `K04`/`K08` repros of `SERIALIZATION_BUGS` §04 and §08)**, **Migration Chain (25 — NS-7 + NS-7b, incl. `B25`, the promoted `K10` repro of the bug archived as `_FIXED_BUGS.md` Serialization 07)**, Spawn (10), Command Console (56), Voxel Occlusion (6), Sky & Celestial (15), Sky Render (11), World Clock (10), UI Blur Render (5), Worm Carver (6), Validation Framework (18), plus the standalone `VoxelMetadataUtility` / `FastNoiseLite` tests (the `ChunkRelativePosition` tests are no longer standalone — they are the Chunk Math suite's `.ChunkRelativePosition.cs` partial).

**Build protocol for every suite below:** the `validation-driven-bugfix` skill (deterministic repro first, prove-red before trusting green, promote repros to baselines). New suites should land on the shared `ValidationSuiteRunner` (`VS-1`, ✅ shipped 2026-07-08): register `Scenario`s and return its `ValidationRunResult` from a headless `Execute()`, with a thin `[MenuItem]` wrapper. All suites stay on the custom validation framework: migrating to the Unity Test Framework was evaluated 2026-07-02 and rejected (see the status header in
[`../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md`](../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md)); the CI/coverage/XML gaps close via the VS-2 extensions instead.



**Audited:** 2026-07-02 (seventh-pass audit), counts re-verified 2026-08-20 against a `Validate All`
run — **528 baselines / 23 suites, 0 failures, 1 known-bug repro outstanding** (`K10`, `SERIALIZATION_BUGS` §10).
That repro was fixed and promoted on 2026-08-21, and a full `Validate All` re-run the same day measured
**529 baselines / 23 suites, all green, 0 known-bug repros outstanding** (3 min 14 s). The 528 above counted
baselines only, so `K10` becoming `B25` moves it into that count; Migration Chain reads 25 baselines instead of
24 + 1.
The eighth-pass audit below recorded 497; the fidelity **C14** mixed-channel mirrors B108–B114 took it to 504,
NS-7's Migration Chain suite added 13, and NS-7b's chunk-payload scenarios added the last 11. The audit found the then-six suites architecturally sound, so the
`VS-*` items it produced are operational only; the residual risk it identified is *coverage*, which is
what this document ranks.

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the `VS-1..3` operational
  items (all shipped) came from the same audit pass; several backlog items there are blocked on an
  ad-hoc version of a gate one of these suites would provide.
- [`../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md`](../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md) — why
  every suite here stays on the project's own framework rather than the Unity Test Framework.
- The per-system fidelity docs under
  [`../Architecture/Testing Framework/`](../Architecture/Testing%20Framework/) — those track *blind
  spots in suites that exist*; this document tracks *systems with no suite at all*.

---

## NS-1. Serialization & save-format round-trip suite — **Priority 1**

- **Failure class guarded:** silent save corruption / permanent data loss — the worst class the engine has. The `serialization-safety` rules open with "changes to these files can silently corrupt every player's saved world"; `SERIALIZATION_BUGS.md` is an active bug category and
  `_FIXED_BUGS.md` carries a long serialization history. Nothing automated guards any of it.
- **Backlog items it gates:** `SL-1` (pooled buffers must keep bytes identical), `SL-3`
  (snapshot-at-dequeue), **`SL-4` (its report entry mandates a corruption stress test exist first)**, `P-5` (⚠️ Format), and every future ⚠️-Format item and migration step.
- **Scope sketch (baseline order):**
    1. **Round-trip identity** — build a `ChunkData`, `Serialize` → `Deserialize`, deep-compare:
       all four section flags (0x00–0x03), uniform-sky levels, light queues, heightmap, state flags; plus palette-randomized fuzz chunks.
    2. **Golden-byte format guard** — a fixed fixture chunk's serialized bytes hashed and pinned per
       `CURRENT_CHUNK_VERSION`: any layout change without a version bump turns red (`GoldenMaster` framework is ready for this).
    3. **Compression matrix** — None / LZ4 / Deflate round-trip, plus loading each algorithm's output regardless of the current setting. *(`GZip = 3` is commented out as reserved, so those three are the whole shipped surface.)*
    4. **`RegionFile` mechanics** — sector allocate/grow/shrink/reuse across mixed-size rewrites, offset-table integrity, corrupt/truncated-file robustness.
       ⚠️ **This bullet's "returns null, never throws out" is wrong and must not be built as written:** `RegionFile.LoadChunkData` deliberately **throws** on an unexpected I/O fault — only the explicit corrupt-shape branches return null. That is the CP-3 fault ≠ "not on disk" contract, already pinned by `Validate Deserialization Robustness` B6; asserting the bullet literally would encode the opposite of a deliberate decision.
    5. **Pending stores** — `LightingStateManager` pending columns + blocklight and
       `ModificationManager` pending mods survive a save → load cycle (Bug 08 history lives here).
    6. **Migration fixtures** — frozen mini-region fixtures per historical save version run through
       `MigrationManager`, asserting expected current-version state (enforces "never edit a shipped migration" mechanically).
       *(Superseded — `NS-7` took this over 2026-08-20 and shipped the `level.dat`/orchestration half as its own
       suite; the historical chunk-format fixtures this bullet describes are now `NS-7b`. Nothing to build here.)*
    7. **Concurrency stress (the SL-4 gate)** — parallel load/save hammering one region file with integrity assertions. Trivially green under today's global lock; becomes *the* gate when SL-4 changes the locking.
- **Building blocks already available:** `ValidationReflection` (ChunkPool stubbing),
  `GoldenMaster`, temp-directory region files (the storage manager already supports a volatile path). Phase **CP-3** of
  [CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) seeds the robustness slice (truncated/garbage/wrong-version payloads → `Deserialize` returns null, no throw, no pooled-shell leak).
- **Effort:** 🟡 core (1–5); part 6 moved to `NS-7`/`NS-7b` — build 1–5 here.
- **Status (2026-08-21): ✅ COMPLETE (parts 1–5).** Parts 4–5 landed the same day as parts 1–3, into the same
  suite: **`B9`–`B14`** cover the `RegionFile` sector allocator (a record's table entry shape, growth →
  relocation → the vacated run being reused, shrink → tail release without extending the file, a 6-round
  mixed-size rewrite storm across 12 slots asserting every payload intact **and every table run disjoint**,
  close/reopen durability including a post-reopen write, and the typed `ChunkTooLargeException` contract), and
  **`B15`–`B16`** cover the pending stores (`LightingStateManager`'s pending columns and blocklight mods —
  channels *and* the removal flag — plus `ModificationManager`'s pending voxel mods, each read back through a
  **second** manager instance, guarded by a "a fresh store holds nothing before Load" non-vacuity check).
  Suite total: **16 baselines + 2 known-bug repros, 202 ms**.
    - **The allocator is never its own oracle:** the structural assertions parse the 1024-entry offset table
      straight off disk.
    - **Flush discipline, learned the hard way:** `RegionFile` only flushes on `Dispose`, so a second handle
      opened mid-session reads **stale zeros**. The first draft of these scenarios inspected the table while
      the writer was still open — which made two of them fail for the wrong reason and, worse, made the
      disjointness and "no table entry" checks pass **vacuously** (all-zero reads satisfy both). Every
      on-disk inspection now happens after disposal, and `NoOverlappingRuns` fails an empty table outright.
    - **Prove-red:** three further mutations — not freeing a relocated record's old run → `{B10, B11}`;
      not writing the offset-table entry → `{B9, B10, B11, B12, B13, B14}`; dropping the blocklight file and
      the pending-mod metadata byte → `{B15, B16}`. The second batch reddened `B14`, which was **not**
      predicted: `B14` reopens the region file to check its neighbour, so it depends on table persistence too.
    - **`K08`** reproduces [`../Bugs/SERIALIZATION_BUGS.md`](../Bugs/SERIALIZATION_BUGS.md) **§08** and
      sharpens it: an out-of-range pending column is not merely stored, it is byte-truncated onto a
      **different, valid** column — `(259, 4)` becomes `(3, 4)`, passes the load-side bounds check, and queues
      a recalculation for a column the caller never named (while `(272, 5)` → `(16, 5)` is correctly rejected).
      Filed, not fixed.
- **Superseded status (2026-08-21): parts 1–3 COMPLETE.** Shipped as
  `Minecraft Clone/Dev/Validate Serialization Round-Trip` (`Assets/Editor/Validation/SerializationRoundTrip/`,
  8 baselines `B1`–`B8` plus the `K04` repro), registered as the 24th suite (`ExpectedSuiteCount` 23 → 24). No
  production code changed. Coverage: the fixture-integrity guard that the reference chunk exercises all four v7
  section flags and excludes data-less sections (`B1`), accessor-level round-trip identity over every persisted
  field (`B2`), re-derivation of the non-persisted state plus the compact-section contract (`B3`),
  byte-identical re-serialization and an unchanged flag map (`B4`), an 8-chunk fixed-seed fuzz sweep mixing all
  five section shapes (`B5`), the golden payload hash pinned to the **on-disk** version byte (`B6`), the
  three-arm compression matrix with a "the codec actually engaged" non-vacuity check (`B7`), and the 3×3
  cross-load matrix proving the algorithm comes from the stored record rather than `saveCompression` (`B8`).
    - **Deliberately a separate suite, not a `Validate Deserialization Robustness` partial** (which is what the
      2026-07-22 bullet below anticipated): that suite's charter is the CP-3 load-boundary **failure** paths;
      this one owns format **fidelity**. Same split reasoning as `NS-7`.
    - **The fixture uses test-local voxel ids, never `BlockIDs` constants** — the suite pins serialized bytes,
      so a `Generate Block IDs` run must not be able to move the golden hash.
    - **Prove-red is recorded, not assumed** — five mutations, each reddening exactly its predicted set; the map
      lives in the suite's class docstring. Three findings worth carrying: (1) `B4`'s byte-identity compare
      does **not** detect a reader that materializes compact sections, because the writer re-compacts them on
      the way out — `B3` is the sole guard there, and the cost is 8 KB of pooled `LightData` per section the
      format stores in 2 bytes; (2) a **self-consistent** layout change (R/B swapped in *both* the queue writer
      and reader) is invisible to every other scenario and caught only by `B6` — which is precisely the
      unbumped-version failure the `serialization-safety` rules exist to prevent; (3) under the `B6` and `B8`
      mutations, `Validate Deserialization Robustness` and `Validate Save Durability` both stayed fully green,
      so this suite is the only guard either contract has anywhere in the engine.
    - **It reproduced a documented bug on its first run.** `K04` reproduces
      [`../Bugs/SERIALIZATION_BUGS.md`](../Bugs/SERIALIZATION_BUGS.md) **§04** (the fixed 256 KB pooled buffer
      cannot hold a dense chunk plus large pending light queues) — with a control leg (same chunk, small
      queues) that passes, so the failure is attributable to the queue size. It also **corrected** that entry:
      post-CP-6 the throw no longer vanishes silently, it maps to `ChunkSaveResult.Failed` and the
      deterministic fault exhausts the retry loop into "this session's edits to that chunk are lost". Filed,
      not fixed — the fix is a format/buffer decision of its own.
- **Partial status (2026-07-22, superseded by the status block above):** the CP-3 robustness slice shipped as
  `Minecraft Clone/Dev/Validate Deserialization Robustness` (B1–B7): truncated / garbage / wrong-version / corrupt-tail payloads → null, no throw, no pooled-shell/section leak (pool active-count balance), fault ≠ "not-on-disk" contract at `LoadChunkAsync` (dev-only
  `InjectLoadFaults` seam), corrupt-on-disk → null through the full storage stack. Parts 1–5 above (round-trip identity, golden bytes, compression matrix, `RegionFile` mechanics, pending stores) remain open and should grow in this suite.

---

## NS-2. World-generation determinism suite — **Priority 2**

- **Failure class guarded:** seed-breaking — permanent, unfixable damage (new chunks stop matching a world's existing terrain; visible seams forever). Currently the engine's **largest unguarded ⚠️ surface**: the report demands fixed-seed differentials for `WG-3` and `ET-2`, and
  `WORLD_SCALING_ANALYSIS.md` §5 demands determinism gates for Tiers A/B — but every implementer must hand-build that gate today, and the TG-2 differential that once existed was throwaway.
- **Backlog items it gates:** `WG-2`, `WG-3`, `ET-2` (both seed-⚠️ items name this gate as mandatory), the `WS-1` generation-side audit, and any future generator/biome-pipeline change.
- **Scope sketch:**
    1. **Golden voxel-map hashes** for fixed seeds × representative fixture configurations (land, ocean, cave-dense, structure-dense) generated through `EditorChunkPipelineRunner` — which already drives the *production* generation jobs headlessly.
    2. **Golden structure mod-stream** — `ExpandStructure` output for fixed markers (directly the WG-3 acceptance gate).
    3. **Derived-data parity** — heightmap vs voxel-map consistency; `ActiveVoxelScanJob` vs managed scan (TG-2's differential, made permanent).
    4. **Cross-run determinism** — same seed twice → bit-identical (catches uninitialized memory and scheduling nondeterminism in the gen job chain).
- **Design constraint (important):** golden masters must bind to **frozen fixture
  `StandardBiomeAttributes`/`WorldTypeDefinition` copies**, never the live authoring assets — otherwise every intentional biome tweak turns the suite red. Intentional generator changes re-capture the goldens as an explicit, reviewed step.
- **Effort:** 🟡 — the runner and `GoldenMaster` do the heavy lifting.

---

## NS-3. Chunk lifecycle / pipeline state-machine suite — **Priority 3**

- **Failure class guarded:** pipeline deadlocks and stalls — **three historical incidents** (the reason the `chunk-lifecycle` skill exists). The flag-pairing, gate-ordering, and pool-recycle invariants are enforced only by rule-following today; the LightScheduler suite covers MT-2's scheduler slice, not the pipeline's state machine.
- **Backlog items it gates:** `P-4` (backpressure rewires scheduling), `OM-2` (emergency unload must respect the gates), `SL-2` (moves the load-apply staging steps), `SU-2`, and any unload pinning change.
- **Scope sketch:** a scripted multi-chunk harness driving the real gates (`AreNeighborsDataReady` / `AreNeighborsReadyAndLit`) through adversarial event orders:
  out-of-order generation completion, unload-during-lighting, pool recycle + replay, budget exhaustion mid-stage, neighbor stranding. Two assertion families: **convergence** (every chunk eventually reaches lit + meshed — the anti-deadlock property) and **flag-pairing** (after every step, no flag is set whose clear site is unreachable). Seed the scenario list with repro fixtures of the three historical deadlocks from `_FIXED_BUGS.md`.
- **Building blocks:** `LightingFrameSimulator` (already simulates frame-by-frame lighting progression) is the embryo of this harness; `BehaviorTestWorld`'s multi-chunk world shows the world-stubbing pattern scales. The LP-* plan ([LIGHTING_PIPELINE_STATE_REFACTOR.md](LIGHTING_PIPELINE_STATE_REFACTOR.md)) is deliberate groundwork: LP-1's invariant probes and LP-4's `ChunkData` flag-transition API are the first two concrete members of this suite's flag-pairing assertion family. The MP-* plan
  ([MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md)) is the meshing-side counterpart: MP-1's request/drop probes and MP-2's scheduling baselines are the first members of the *convergence* ("every chunk eventually reaches lit + meshed") family.
- **Effort:** 🔴 — the hardest harness on this list (World-level orchestration must be stubbed). Build scenario-by-scenario; even the first two scenarios (out-of-order completion, recycle replay) would have caught past incidents.

- **Status (2026-08-22): 🟡 SLICE 1 SHIPPED** — `Minecraft Clone/Dev/Validate Chunk Pipeline`
  (`Assets/Editor/Validation/ChunkPipeline/`, 6 baselines `B1`–`B6`), the 25th registered suite. The harness
  drives the **real** `AreNeighborsDataReady` / `AreNeighborsReadyAndLit` / `AreNeighborsMeshReady` plus the real
  `LightingScanDecision`, `MeshingScheduleDecision` and `ChunkUnloadDecision` over a stub `World` holding real
  `ChunkData`; only stage *execution* is modeled. Fidelity doc:
  [../Architecture/Testing Framework/CHUNK_PIPELINE_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/CHUNK_PIPELINE_VALIDATION_HARNESS_FIDELITY.md).
    - **Three corrections to the entry above, found while building it.** (1) The three historical deadlocks are
      **not in `_FIXED_BUGS.md`** — they are §9.1 / §9.3 / §9.6 of `CHUNK_LIFECYCLE_PIPELINE.md`, and §9.6's
      *decision* was already guarded by the Chunk Unload Decision suite (`B4`/`B5`/`B7`). (2) `LightingFrameSimulator`
      is not an "embryo": it is a 774-line simulator with scheduler mode, budget throttling, completion ordering
      and merge-fault injection. (3) The 🔴 rested on "World-level orchestration must be stubbed", but
      `StorageValidationFixture` already had the stub-world recipe and every decision helper was already extracted
      by CP-*/MP-*, so slice 1 needed **no** extraction refactor and no production change.
    - **`B1` is the harness's own prove-red, and it is the scenario to read first.** It neuters the §9.6 strand
      guard and asserts the pipeline **deadlocks**; a green-by-converging `B1` means the pump stopped modeling
      production and every other baseline is vacuous. Two false greens were caught and fixed during the build —
      the second is worth carrying: a chunk adjacent to an unloaded neighbour fails the mesh gate on the
      **missing neighbour alone**, so asserting "the stranded chunk never meshes" would have been red whether or
      not stranding was fixed. `B1`/`B5` assert the *flag* (`HasLightChangesToProcess`), which is the only signal
      that separates a guarded pipeline from an unguarded one.
    - **Deliberately not built yet:** pool recycle + replay, real `MeshDrainPolicy` composition, seeded-shuffle
      scan order, completion-pass fault injection, the edge-check round budget, and the load-from-disk arm —
      tracked as `CP-H1`…`CP-H6` in the fidelity doc. LP-* remains 0/7; slice 1 asserts flag pairing against
      today's raw bools, so it is the regression guard that makes LP-1/LP-4 safe to execute.
    - **Hardening pass (2026-08-23), from a code review of the slice-1 commits.** Two of the six baselines were
      making non-vacuity claims they could not support. `PipelineAssert`'s "was this scenario adversarial at
      all" floor read the **global** park counter, which frontier chunks satisfy unconditionally (their own
      neighbors were never seeded), so it could never fail; the counters are now scoped to an observed set
      (`ChunkPipelineSimulator.Observe`, auto-called by `RunUntilConverged`). Scoping then exposed the real
      defect underneath: **`B3` had zero target blocking** — a pre-seeded neighborhood passes
      `AreNeighborsDataReady` from frame 0, so no interior chunk is ever gated and the §9.3 wave-front it is
      named for never formed. `B3` now drives generation one admission per frame, and reports 24 observed
      parks where it previously reported none. Also fixed: `B6` never asserted that its run converged, and a
      dead `PipelineAssert.Deadlocked` had left two docs describing `B1` as a non-convergence assertion when
      it asserts the stranding flag (§4 of the fidelity doc had it right all along).
    - **Prove-red is now measured, not predicted.** Six mutations were applied in isolation and their red-sets
      recorded in the `.Baseline.cs` docstring; **all six baselines have been observed failing.** One recorded
      prediction was simply wrong and is kept as a warning: forcing `AreNeighborsDataReady` true does **not**
      red `B2` (its mesh declines still satisfy the floor and it converges either way) — `B2`'s real
      prove-red is the admission-cap mutation. Dropping the `WouldStrandInRangeNeighbor` arm reds `B5` alone
      with `B1` green, so the pair's intended asymmetry is now witnessed rather than asserted.

---

## NS-4. Physics / collision-solver suite — **Priority 4** — ✅ **COMPLETE (2026-08-03)**

- **Failure class guarded:** player-facing movement regressions (fall-through, wall snag, broken step-up) — subtle and playtest-only until this suite landed. It is the item `SUB_VOXEL_COLLISION_SYSTEM.md` carried as "Automated Tests Pending — automated regression tests remain outstanding" from Phase 6 until 2026-08-03; that status line now reads "Implemented", guarded by this suite.
- **Live bugs it would already have caught:** [`../Bugs/PLAYER_BUGS.md`](../Bugs/PLAYER_BUGS.md) **§04** (player embeds in a block after a fast landing and `IsGrounded` never recovers, so jumps are refused until flight/noclip rescues them — **High**, the player is stranded) and **§01** (collision sticks in tight spaces). §04 is the natural *first* scenario: its symptom is a single boolean (`IsGrounded`) on a solver whose grounded state is written in only four places, so it reduces to an assertable end-state rather than a feel judgement.
- **Backlog items it gates:** `PH-1` (gather-once solver refactor), `VQ-1` (integer query path under the solver), collision-bounds authoring changes (Block Editor).
- **Scope sketch:** deterministic scenarios on fixture voxel fields, asserting final position/velocity/`IsGrounded` within tolerance: flat-ground grounding, wall slide, corner snag (the `COLLISION_EPSILON`/jitter-tolerance edges), step-up onto slab and full block, sub-voxel bounds (quarter slabs, rotated custom bounds), ceiling bump, and **substep consistency** (one large displacement vs N substeps → same endpoint). The scenario table in
  `SUB_VOXEL_COLLISION_SYSTEM.md` §2 is the ready-made baseline list.
- **Building blocks:** `PlacementTestWorld` proves the concrete-`World` stubbing pattern (`ValidationReflection`). The "or the stub world populated with real voxel data" option is **verified, not hypothetical**: VQ-3 (2026-08-03) drove `World.CheckPhysicsCollision` through an unmodified `PlacementTestWorld` seeded with the real `BlockDatabase` for a 1950-probe sweep, so **no dependency injection is required** to stand this suite up. ⚠️ One trap that sweep hit first: `CheckPhysicsCollision` reads the `WorldOrigin` **static**, which survives play sessions (it is reset only on play-mode entry), so a fixture must `WorldOrigin.ResetToIdentity()` and restore — otherwise every lookup lands far from the seeded blocks and the sweep silently returns **zero hits**, passing vacuously. Assert a non-zero hit count before trusting any such sweep.
- **Effort:** 🟡.
- **Status (2026-08-03): ✅ COMPLETE** — shipped as `Minecraft Clone/Dev/Validate Physics Solver`
  (`Assets/Editor/Validation/PhysicsSolver/`, 17 baselines `B1`–`B17` at ship; **23** today after the retired
  `PLAYER_BUGS` §04's fix added `B18`–`B23`). The harness drives the **real**
  `VoxelRigidbody` (a live component with `_world` injected; `ResolveMovement` for an exact displacement,
  `CalculateVelocity` + translate for the substep chain) against the **real** `World.CheckPhysicsCollision`, over the
  `PlacementTestWorld` stub-world recipe as predicted above — no dependency injection was needed. Coverage:
  `SUB_VOXEL_COLLISION_SYSTEM.md` §5 Phase 6c's six regression tests (`B2`–`B9`) and Phase 6b's unit-test item, §2.2's
  failure table (`B10`–`B13`), substep invariance (`B15`), the corner/jitter edge (`B16`), fluid exclusion (`B14`),
  the WS-4 origin offset (`B17`), and the `WorldOrigin` vacuous-pass guard as `B1` (two-sided: a seeded AABB must hit
  **and** an open-air AABB must not).
    - **Prove-red is recorded, not assumed.** These baselines were authored against shipped code, so nine engine
      mutations were applied in isolation to observe each one red; the mutation → red-set map lives in the suite's
      `.Baseline.cs` docstring. Every baseline except `B1` (fixture-integrity, red by construction) has been observed
      failing. Two findings worth carrying: `B15` does **not** detect the absence of substepping (`B6` owns that — it
      guards the loop's composition instead), and the solver's `fluidType != None` filter has **no** coverage from
      real data (every shipping fluid is already non-solid), so the fixture carries a deliberately solid-flagged fluid
      to exercise it.
    - **Deliberately not pinned:** the `IsGrounded` verdict after a high-speed landing or a horizontal-only resolve.
      That is exactly [`../Bugs/PLAYER_BUGS.md`](../Bugs/PLAYER_BUGS.md) **§04**, which remains open — pinning today's
      answer would encode the bug as a baseline. §04's repro is the suite's next scenario.

---

## NS-5. Coordinate-math & voxel-query equivalence suite — **Priority 5** *(best value-per-effort)*

- **Failure class guarded:** silent chunk/region addressing corruption — today latent (all-positive world), fatal the moment Tier B lands. Ranked below NS-1..4 on *present* impact only; on value-per-effort it is first, and `WS-1`'s report entry already mandates exactly this sweep.
- **Backlog items it gates:** `WS-1` (shift/mask migration — equivalence sweep is its named gate),
  `VQ-1` (float-floor semantics must be preserved exactly), the region codec V3.
- **Scope sketch:** pure-function sweeps — old idioms (`FloorToInt`, truncating `/`/`%`) vs shift/mask across representative ranges *including negatives and the ±2²⁴ float boundary*;
  `GetVoxelState(Vector3)` vs the future integer path over fuzzed positions;
  `RegionAddressCodec.V2Codec` behavior **pinned as-is, bug included** (existing saves depend on it) alongside V3 correctness assertions.
- **Building blocks:** `ChunkRelativePositionTests` is the template for pure-math suites.
- **Effort:** 🟢 — build it together with WS-1/VQ-1. **Scheduled:** phase **CP-2** of
  [CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) executes WS-1 and builds this suite alongside it (positive-domain equivalence + negative/big-coordinate contract pins + region round-trips).
- **Partial status (2026-07-12):** the WS-1 shift/mask sweeps and the VQ-1 float↔int decomposition-parity sweep both shipped as scenarios in `ChunkRelativePositionTests` (the "Chunk Math" suite) — the WS-1 and VQ-1 gates above are satisfied. The V2/V3 region-codec pins remain outstanding as the standalone NS-5 suite. *(Superseded — see the next bullet: the pins shipped 2026-07-22.)*
- **Status (2026-07-22): ✅ COMPLETE** — the region-codec pins shipped with the CP-2 close-out as the `.RegionCodec.cs` partial of the "Chunk Math" suite (the standalone-suite framing was dropped: that suite is NS-5's de-facto home). Coverage: V2 encoder *expected-value* pins on both signs (round-trip identity alone is blind to a matched encoder/decoder bug pair — proven in the close-out's prove-red), ±2³¹-adjacent aligned-origin pins, a two-way inverse property (decoder∘encoder was previously unexercised), truncation teeth, the V1 decoder legacy pin with
  V1≠V2 divergence teeth, and the V1 encoder guard + `ForVersion` dispatch pins. "V3 correctness assertions" are moot: the recorded no-V3-bump verdict stands (V2 addressing is already negative-correct; no V3 codec exists). See
  [CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) §7 CP-2 Amended block.
- **Coverage extension (2026-08-22): ✅ G1–G4 shipped.** A blind-spot audit of the shipped NS-5 surface found three
  gaps — a fourth (`G4`) was added on review — all closed as `Chunk Math` partials (56 → 72 scenarios, **no production change**):
  **G1** `.PaddedVolume.cs` (6) — the padded lighting/fluid index helpers and all three neighborhood gathers had
  **zero** direct assertions; now pinned against an independent per-cell scatter oracle with position-encoding fills
  (full height, partial Y-band, missing-neighbor sentinel on each of the 8 sides, `ExtractCenterLight` round-trip, and
  the fluid wrapper's `bandCount`-vs-`bandHeight` parameter-semantics difference).  
  **Measured caveat on G1's value (2026-08-22):** the west-halo off-by-one prove-red was *also* run against the
  Lighting suite, which caught it decisively — **33 of 106 baselines red** (`B3` fails to converge, `B10` reports a
  hard border cut-off over 1431 voxels, fuzz baselines `B40`/`B68`/`B70` flag seeds); restoring returned 106/106.
  So the gather is **well covered indirectly** and the "an end-to-end oracle would miss this" hypothesis is
  **refuted** for this mutation class. G1's remaining value is therefore localization (it names the padded cell and
  the source slot, versus a downstream field diff), cost (34 ms versus 106 s), and the surface the Lighting suite
  genuinely never touches: `GatherPaddedFluidVoxelsBand`, the per-side missing-neighbor sentinel paths, and the
  band-semantics split. Weigh future "zero direct assertions" arguments against this result rather than assuming
  indirect coverage is thin.
  **G2** `.FlattenedIndex.cs` (4) — `GetFlattenedIndexInChunk` ↔ `GetLocalPositionFromFlattenedIndex` had no inverse
  pin: the same matched-pair blindness the region-codec pins above were written to defeat.
  **G3** `.RegionFileName.cs` (4) — the `r.{x}.{z}.bin` seam, unpinned for the negative region coordinates `WS-3` made
  reachable; the two production parsers' `>= 3` / `== 3` guards are pinned **as a divergence**, since they genuinely
  disagree on an over-segmented name.
  Each was prove-red verified by mutating the production code it guards (stride transposition, sign stripping,
  west-halo off-by-one, V1 modulo, removed V1 announcement). Notably the stride-transposition mutation left all 56 pre-existing Chunk Math scenarios
  green — direct evidence the G2 gap was real.
  **G4** `.RegionCodec.cs` (+2) — the V1 *legacy encoder*. Initially deferred because exercising it requires
  `allowLegacyEncoder: true`, whose deliberate `Debug.LogError` is exactly what the runner reads as red; built
  anyway because the path stays reachable for migration and future editor tooling. The announcement is
  **captured and asserted** (exactly one error-level log per encoding call, message-bound) rather than
  suppressed — turning the obstacle into the pin, since silent legacy encoding is the actual hazard. Also pins
  the formula, the `{0, 16}` slot set on aligned positive origins, and the encode∘decode identity that made the
  v1→v2 repack correct — plus its boundary: on a **negative** origin V1 pairs a floored region with a
  truncating modulo, producing an out-of-range slot that does not round-trip. Any future tool encoding V1 must
  stay non-negative.

---

## NS-6. Pool reset-safety audit — **Priority 6**

- **Failure class guarded:** stale pooled state after recycle — the documented historical class (`RemainingEdgeCheckRounds` shipped without a reset and silently broke edge checks; the
  `pool-reset-safety` rules exist because of it). B17 guards exactly one pooled type (`MeshDataJobOutput`); the rest rely on review discipline.
- **Backlog items it gates:** `WG-1` (new generation-buffer pool), `DT-2` (retained visualizer containers), any future pooled type.
- **Scope sketch:** one generic, reflection-driven audit rather than per-field baselines: for each pooled type (`ChunkData`, `ChunkSection`, `Chunk`, `VisualizerChunkData`, pooled job outputs), write sentinel values into every transient field → `Reset()`/`Release()` → assert every field returned to its documented default. A newly added field with no reset **fails automatically** — the exact historical bug shape. Needs a per-type defaults map (or a `[PoolResetDefault]`
  attribute) — cheap, not free; fields legitimately exempt (persistent buffers) get an explicit exemption list so silence is never accidental.
- **Effort:** 🟢.

---

## NS-7. Migration-chain suite — **Priority 1 (ties NS-1; sharpens its part 6)**

- **Failure class guarded:** the NS-1 class (permanent save corruption), but measured rather than projected. `Assets/Scripts/Serialization/Migration/Steps/` holds **15 files** — 14 numbered steps `v1_to_v2` … `v14_to_v15`, plus `LegacyLevelDat.cs`. **Exactly two of them are exercised** by any suite: `Validate Deserialization Robustness` B8 (v13→v14) and B9 (v14→v15). The twelve older steps — including the three highest-risk rewrites, `v5_to_v6_LegacyToSchemaBased`, `v7_to_v8_RGBLightQueues` and `v9_to_v10_StripLightBitsAndNewFlags` — have no coverage, and `MigrationManager` is never driven.
- **Why it is separate from NS-1 part 6:** part 6 asks for "frozen mini-region fixtures per historical save version". That is the right shape, but the item reads as future-proofing; the measurement above shows it is a **present** 12-step hole in a system whose own rule is "never edit a shipped migration". Any old world a player still has takes an untested code path today.
- **Scope sketch:**
    1. A frozen fixture per historical `level.dat`/region version, run through `MigrationManager`, asserting the expected current-version state — this is part 6, unchanged.
    2. **The chained upgrade** — one v1 fixture migrated all the way to the current version in a single run, asserting each step's postcondition en route. Per-step fixtures cannot catch a step that is individually correct but composes wrongly with its successor.
    3. **`Settings.Dev.simulateMigrationCorruption` as the negative control.** The fault seam already exists and no scenario uses it: a migration that silently no-ops must be *caught*, not merely survived.
- **Effort:** 🟡 — the fixtures are the cost; `MigrationManager` needs no new seam.
- **Status (2026-08-20): ✅ COMPLETE for the `level.dat` chain and the manager's orchestration** — shipped as
  `Minecraft Clone/Dev/Validate Migration Chain` (`Assets/Editor/Validation/MigrationChain/`, 13 baselines
  `B1`–`B13`), registered (`ExpectedSuiteCount` 22 → 23). No production code changed and no new seam was needed, as
  predicted. Coverage: the chain folded by `LevelDatCodec` over frozen v1 / v3 / v12 documents — every injected
  historical default (Legacy type, 128/16/100 dimensions, legacy-center spawn, border disabled, `-0.6` wind, noon
  clock) and every field that must survive fourteen steps (`B1`–`B4`), a gapless-path structural guard for all
  fourteen source versions (`B5`), and `MigrationManager` driven end-to-end over a real volatile-path world —
  stamp + content migration + payload survival + backup contents (`B6`), the documented chunkless-world skip
  (`B7`), the three fail-fast guards via a synthetic misbehaving step swapped into a local manager instance
  (`B8`–`B10`), abort-then-rollback (`B11`), rollback after success (`B12`), and the shipped
  `Dev.simulateMigrationCorruption` injector (`B13`).
    - **Prove-red is recorded, not assumed** — four engine mutations in two disjoint pairs; the map lives in the
      suite's class docstring. Three findings worth carrying:
        - **`ToChunkRelative` had no coverage at all** before this suite: the shipped `B8`/`B9` level.dat scenarios
          in `Validate Deserialization Robustness` both start at v13, *after* the re-type. `B2` is the first
          scenario to exercise it, and it does so with a **negative** absolute position — the case a truncating
          divide gets wrong and an all-positive world never reveals.
        - **The manager's post-chain version stamp is redundant.** Deleting it left `B6`/`B7`/`B12` green, because
          every shipped step also sets the version inside `MigrateLevelDat`. That is the
          [AOT_WORLD_MIGRATION_SYSTEM.md](../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md) §6 contradiction, now
          *measured* rather than argued. Deliberately not "fixed" — rewriting a shipped step's output is forbidden.
        - **`MigratePendingLighting` is overridden by no step**, so the manager's pending-lighting migration loop is
          dead code today. Recorded, not removed.
    - **The seam this item named is not the negative control it looked like.** `Dev.simulateMigrationCorruption`
      *throws*, editor-only; it exercises the per-chunk catch, not the "silently no-ops" case part 3 asks about.
      That case is caught by the manager's **version-byte fail-fast guard**, which `B8` now asserts directly with a
      synthetic no-op step. *(Amended 2026-08-20 by NS-7b: the seam's `Random.value < 0.01f` reads like a 1%
      sample, but `MigrateSingleRegion` runs on the ThreadPool where `Random.value` throws — so the throw lands in
      the per-chunk catch and an armed seam faults **every** chunk. No rate, and no seed matters. `B13` pins that
      and the accounting invariant, processed + corrupted == total.)*

### NS-7b. Historical chunk-format fixtures — the remainder of NS-7 — **Priority 1**

- **What is left:** the parts of NS-7 that need a *writer* for a historical on-disk layout, which is the whole
  fixture cost the parent item priced at 🟡. Still uncovered: the **five chunk-payload rewrites** (`v2→v3`,
  `v5→v6`, `v7→v8`, `v8→v9`, `v9→v10` — including all three of the high-risk rewrites the parent item names), the
  **two `pending_mods` steps** (`v4→v5`, `v5→v6`), and the **v1→v2 region-layout restructure**
  (`RequiresRegionLayoutMigration`, the only step that moves chunks between region files).
- **Why the parent suite could not reach them:** its on-disk fixture is a v12 world, chosen because no step between
  v12 and v15 touches the chunk payload or the region layout — so real current-format chunks written by
  `ChunkStorageManager` are a byte-faithful stand-in (v12 and v15 resolve the same V2 address codec). Any earlier
  source version needs bytes in a layout nothing in the engine can still write.
- **The one available oracle, and its limit:** each chunk-format step documents its input layout inline
  ("V2 READ DEFINITION / Historical Reference: ChunkSerializer.cs …") — by design, per §5 of the architecture doc,
  precisely so the format survives its writer. A fixture builder derived from those read definitions is therefore
  authoritative-by-construction for regressions and composition faults, but **cannot** detect a step that has always
  misread its input, since fixture and reader would share the error. Say so in the suite rather than implying more.
- **Scope sketch:** one frozen fixture builder per chunk era (v1/v2 → v3 → v4 → v5 → v6), assert the chained upgrade
  reaches chunk v7 with a hand-verified voxel and light value intact; a small `pending_mods` fixture per its two
  steps (16-byte entries); and a v1 region fixture with mis-scaled addresses for the repack.
- **Effort:** 🟡–🔴 — the builders are the cost; the harness, fixture and manager plumbing already exist in the
  parent suite.
- **Status (2026-08-20): ✅ COMPLETE** — shipped into the existing Migration Chain suite as `B14`–`B24` plus the
  known-bug repro `K10` (suite 13 → **24 baselines + 1 repro**; still 23 registered suites). All five chunk-payload
  rewrites, both `pending_mods` steps and the v1→v2 region repack now have byte-level coverage.
    - **The fixture cost was over-estimated 5×.** This item asked for "one frozen fixture builder per chunk era
      (v1/v2 → v3 → v4 → v5 → v6)". Only **one** builder is needed: the manager re-reads the version byte between
      steps, so a single authored chunk-format v1/v2 payload walks the whole chain (v2→v3 writes 3, v5→v6 writes 4,
      v7→v8 writes 5, v8→v9 writes 6, v9→v10 writes 7). Pinned by a SHA-256 golden hash rather than an embedded
      byte blob.
    - **The output-side circularity this item warned about is closed.** `ChunkSerializer.Deserialize` is public and
      accepts `CompressionAlgorithm.None`, so the migrated payload is validated by the **production reader**
      (`B16`), independent of anything authored in the fixture. The limit now applies to the *input* layout only —
      a step that has always misread its input is still undetectable, and the fixture says so.
    - **`pending_mods` record sizes, corrected:** the v4 record is **16 bytes** (3×int32 + ushort + orientation +
      fluid) and the v5 record is **15**. This item's "16-byte entries" was right for the v4 side.
    - **Prove-red:** five mutations in two disjoint batches, each reddening exactly its predicted set —
      `{B17, B19, B22}` and `{B16, B18, B20, B23}`. The map lives in the suite's class docstring.
    - **It found a real bug on its first run.** `K10` reproduces **[`../Bugs/SERIALIZATION_BUGS.md`](../Bugs/SERIALIZATION_BUGS.md) §10**:
      `RunAOTMigrationAsync` treats the region-layout branch and the per-chunk format branch as mutually
      exclusive, and v1 is the only world version whose path contains a layout step — so a **v1 world is repacked
      but never format-migrated**, leaving chunk-format v1/v2 payloads in a world stamped current. Every chunk
      then fails the version check and regenerates from seed. *(Fixed 2026-08-21 — the two passes now run in
      sequence; archived as `_FIXED_BUGS.md` Serialization 07 and `K10` promoted to `B25`. The rest of this item
      records what the first run found, and stands as written.)* Filed, not fixed: the fix touches shipped migration
      orchestration and needs an in-game load of a real old save.

---

## NS-8. Chunk-boundary continuity & the generation feature-flag matrix — **Priority 2 (rides NS-2)**

- **Failure class guarded:** seam-breaking terrain — NS-2's class, but a scenario shape NS-2's scope sketch does not name. NS-2 asks for golden voxel-map hashes and **cross-run** determinism (same seed twice → identical). Neither asserts the property that actually produces visible seams: **two adjacent chunks generated independently must agree on their shared column.** A generator that is perfectly deterministic and perfectly reproducible can still disagree across a boundary.
- **Why it ranks high for its size:** it needs no golden fixtures and no frozen biome assets — it is a self-comparison, so it cannot go stale when biomes are re-authored, which is the design constraint NS-2 carries. It is the cheapest generation scenario in the roadmap and the one most likely to fire.
- **Second half — the flag matrix.** `enableCaves`, `enableLodes`, `enableWater`, `enableMajorFloraPass` and `enableMinorFloraPass` are five independent generation toggles with **no coverage on either side**. A flag-off path that throws, or that shifts the heightmap relative to flag-on, is undetectable today. The continuity assertion above is the natural oracle to run each flag combination against.
- **Scope sketch:** generate chunk pairs (and a 2×2 block, for the diagonal case) independently via `EditorChunkPipelineRunner`, assert shared-column agreement; repeat across a seed sweep and across the flag matrix; add one flag-off smoke run per flag asserting no throw and a non-degenerate heightmap.
- **Effort:** 🟢 for the continuity half, 🟡 with the full flag matrix.

---

## NS-9. Cross-chunk-seam axis for the Physics and Placement suites — **Priority 3**

- **Failure class guarded:** player-facing failures that only exist at a chunk boundary — the classic production shape (fall through the world at a seam, place into a chunk that is not resident).
- **Measured gap:** every `Validate Physics Solver` scenario runs inside a **single** chunk (`ChunkCoord(100, -100)` is the only chunk coordinate the suite constructs). A collision query straddling a seam, and one against an *unloaded or placeholder* neighbor, are both unmodeled — even though `NS-4`'s own building-block note establishes that `PlacementTestWorld` can seed real voxel data across chunks. `Validate Placement` likewise has no scenario mentioning an unloaded or placeholder chunk; the Behavior suite is the only one that models placeholder neighbors (`BehaviorValidationSuite.Baseline.PlaceholderNeighbor.cs`), and that pattern is the template.
- **Also missing on the Placement side:** block **breaking** has no coverage at all — `PlayerInteraction`'s removal path, drop/tool rules, and toolbar/inventory selection are unvalidated, while placement is well covered.
- **Scope sketch:** re-run a representative slice of the existing Physics baselines with the fixture geometry translated onto a seam (landing, wall slide, step-up, the substep chain), then the same against a neighbor that is absent/placeholder, asserting graceful refusal rather than a fall-through. For Placement: place and break across a seam, and into a non-resident neighbor.
- **Effort:** 🟢 — both suites already have the world stubs; this is fixture placement, not new harness.

---

## NS-10. Feature-flag off-side matrix — **Priority 4 (cross-cutting, not a suite)**

- **Failure class guarded:** a shipped fallback path that no longer works. The engine's settings surface has grown faster than the suites, and the prevailing pattern is that only the **default** side of a toggle is ever exercised. `Validate Pipeline Backpressure` B7 is the counter-example done right — it asserts the *disabled* passthrough of `scaleBudgetCeilingsWithFpsCap` — and it should be the template, not the exception.
- **Measured one-sided flags** (all verified present in `Assets/Scripts/`):

  | Flag / enum | Suite that should own it | Off-side coverage |
  |-------------|--------------------------|-------------------|
  | `enablePipelineTimeBudgets`, `enableGenerationPanicGate` | Pipeline Backpressure | none |
  | `enableCaves` / `enableLodes` / `enableWater` / `enableMajorFloraPass` / `enableMinorFloraPass` | NS-8 | none, either side |
  | `SmoothLightingQuality` (Off/Low), `fullBlockContactShadows`, `FluidQuality` | Meshing | none — meshing always runs `High` |
  | `CompressionAlgorithm` arms (`saveCompression`) | NS-1 part 3 / Save Durability | none |
  | `keepChunksInMemory` | Chunk Unload Decision | none — it is a live bypass of unload |

- **Not in scope:** `CloudStyle` — clouds are an **explicit non-goal** below and this item does not re-open that.
- **Scope sketch:** no new suite. Each row is a scenario added to the suite that already owns the system, asserting the off-side path produces a *defined* result (not merely "does not throw"). Where a flag is a pure perf toggle, the correct assertion is a **differential**: on-side and off-side must produce identical output, which is the `LI-2`/`TG-4` pattern already proven in the Lighting and Behavior suites.
- **Effort:** 🟢 per row; the value is in doing them systematically rather than when a flag breaks.

---

## NS-11. CI-effective coverage — the two render suites contribute nothing headless — **Priority 5**

- **The situation (deliberate, but its aggregate consequence is undocumented):** `Validate Sky Render` (11 baselines) and `Validate UI Blur Render` (5) have no graphics device under `-nographics`, so every scenario reports **INCONCLUSIVE and passes** rather than failing the run. Both suites document this convention in their own class docstrings, and it is the right call — a headless agent must not red a run over a missing GPU. The undocumented part is the consequence: **16 baselines are green in CI regardless of what the code does**, and nothing pins the policy itself — `ValidationSuiteCI` has no self-test asserting that INCONCLUSIVE counts as pass, so a change to that rule (in either direction) is silent.
- **Scope sketch:** (1) a framework self-test pinning the INCONCLUSIVE→pass policy and the exit-code contract, so the rule is a decision rather than an emergent behavior; (2) have the aggregate runner **report** the inconclusive count per suite in its summary line, so a CI reader sees "16 not evaluated" rather than an unqualified green; (3) optionally, a CI lane with a software/offscreen device that actually runs them — decide explicitly, do not leave it implied.
- **Effort:** 🟢 for (1) and (2); (3) is an infrastructure call, not a suite.

---

## Deliberate: not every menu-item suite belongs in `Validate All`

`ValidationSuiteRegistry` carries **24** registered suites, pinned by `ExpectedSuiteCount` and guarded
by `ValidationFrameworkSelfTest.RegistryMeetsExpectedCount` (which reds if a suite is *dropped*) and by
the aggregate runner's and `ValidationSuiteCI`'s count check. Some validation entry points intentionally
sit **outside** that registry and therefore outside `Validate All` and CI:

- **`Minecraft Clone/Dev/Validate Fluid Parallel Determinism (Cross-Chunk Halo, Y-band)`**
  (`Assets/Editor/Validation/Behavior/FluidParallelDeterminismValidation.cs`) — a heavy, hand-rolled
  determinism sweep rather than a `Scenario` set. Its cheap invariants are already represented in the
  Behavior suite's `BH-D1` differentials; the standalone run is for deliberate investigation.
- The **nightly fuzz menu items** — `Validate Lighting Engine (Border Height Fuzz / Bug 05 Canopy Fuzz /
  Bug 09 Geometry Fuzz / Interrupted Reconciliation Fuzz)`. These are high-seed-count entry points into
  the *same* Lighting suite that `Validate All` already runs at suite-tier seed counts (the HF-3
  precedent). Running the nightly counts in every `Validate All` would dominate its runtime for a
  linear-in-seeds increase in coverage.
- **Standalone unit tests** with their own `MENU_PATH` — `FastNoiseLiteTests`, `VoxelMetadataUtilityTests`.

This is a **choice, not an oversight**, and it is recorded here because the registry cannot express it:
`ExpectedSuiteCount` is a hand-maintained constant, so a suite that *should* have been registered and
was not looks identical to one deliberately left out. The rule that keeps that honest: **a new suite is
registered by default; leaving it out requires a line in this section saying why.** If the exclusion
list grows past the three cases above, replace it with a reflection scan over
`[MenuItem("Minecraft Clone/Dev/Validate *")]` cross-checked against the registry, with this list as
the explicit allow-list.

---

## Explicit non-goals

No suites proposed for: **UI/menus and input** (event-driven, low blast radius, visually verified), **clouds and debug tooling** (`DT-*` hygiene items suffice; debug tools are not correctness-critical), **OM-1 device calibration** (device-dependent by design, verified by its own startup probe), and **shaders/GPU output** (needs image-based comparison — a different kind of harness; revisit if GS-1/GS-3 visual refactors recur). **2026-08-15:** this exclusion was load-bearing in `GS-3`'s deferral — the absence of any block-shader render gate, and the fact that building one exceeds the item itself, was one reason a guaranteed visual change was judged not worth an unmeasured gain. `SkyRenderValidationSuite` shows the harness *shape* that would be needed, but it is built on the skybox-specific `SkyPreviewRenderer`.

## Sequencing summary

~~`NS-1` (core, parts 1–5) and `NS-2` first~~ — `NS-1` **is complete as of 2026-08-21 (parts 1–5)**, so **`NS-2` is now the top unbuilt item**, with `NS-8`'s continuity half the cheaper way in (it needs none of `NS-2`'s frozen-fixture machinery). Of the two irreversible failure classes, data loss now has a gate; seed breaks still do not. `NS-1` unblocked `SL-1`/`SL-3`/`SL-4`'s named prerequisite; `WG-3` and `ET-2` still wait on `NS-2`. `NS-5`/`NS-6` are 🟢-sized and should simply ride along with the work that triggers them (WS-1/VQ-1 and the next new pool, respectively). `NS-3` is the biggest investment — start it as repro fixtures for the three historical deadlocks and grow it scenario-wise, ideally before `P-4`/`OM-2` rework the scheduling invariants it guards. ~~`NS-4` lands whenever `PH-1`/`VQ-1` get scheduled,
using the §2 scenario table as its baseline list.

**The 2026-08-19 additions slot in as follows.** ~~`NS-7` ties `NS-1` at the top — it is the same failure class,
but measured rather than projected (12 of 14 migration steps untested *today*), and it should be built as
`NS-1` part 6 rather than as a separate suite.~~ — `NS-7` ✅ **landed 2026-08-20** as its own suite rather than an
`NS-1` partial: its fixtures are whole migrated *worlds* on disk, which does not fit the load-boundary charter of
`Validate Deserialization Robustness`. `NS-7b` closed the chunk-payload remainder the same day, so `NS-1` parts 1–5 is now the top-priority
unbuilt item. `NS-8`'s continuity half is the cheapest item in this document
and needs none of `NS-2`'s frozen-fixture machinery, so it can land **before** `NS-2` and give the generator
its first gate of any kind. `NS-9` and `NS-10` are fixture-and-scenario work inside suites that already exist —
they ride along with whatever next touches those systems, exactly as `NS-5`/`NS-6` do. `NS-11` is
infrastructure hygiene: do (1) and (2) with the next framework change, and treat (3) as a separate call.~~ — `NS-4` ✅ **landed 2026-08-03**, deliberately built *before*
`PH-1` rather than alongside it, so `PH-1`'s "subtle physics-feel regression" risk has a gate it can fail against.

---

## Document History

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.5** *(2026-08-21)* - **`NS-1` COMPLETE — parts 4–5 landed the same day**, into the same suite:
  `B9`–`B14` for the `RegionFile` sector allocator and `B15`–`B16` for the pending stores, plus the `K08`
  repro of `SERIALIZATION_BUGS` §08 (suite: 8 → **16 baselines + 2 repros**, 202 ms). This closes the
  roadmap's top-priority item; **`NS-2` is now the top unbuilt entry**. **Census re-verified against a full
  `Validate All`: 537 → 545 baselines / 24 suites, 0 failures, 2 known-bug repros** (204.5 s); every other
  per-suite count re-verified unchanged, and the two repros are both this suite's. Prove-red: three mutations — not freeing a relocated
  record's vacated run → `{B10, B11}`; not writing the offset-table entry → `{B9, B10, B11, B12, B13, B14}`;
  dropping the blocklight file and the pending-mod metadata byte → `{B15, B16}`. The second batch reddened
  `B14` against prediction, because `B14` reopens the region file to check its neighbour. **A methodology
  correction worth keeping:** the first draft of the part-4 scenarios inspected the on-disk offset table while
  the writing `RegionFile` was still open — but `RegionFile` only flushes on `Dispose`, so those reads
  returned stale zeros. Two scenarios failed for the wrong reason, and two checks (run disjointness, "no
  table entry") were passing **vacuously**, since all-zero reads satisfy both. All on-disk inspection now
  happens post-dispose, and `NoOverlappingRuns` rejects an empty table outright. `K08` also sharpened §08:
  an invalid pending column is not just stored, it is truncated onto a *different valid* column — `(259, 4)`
  becomes `(3, 4)` and is silently queued.
* **v1.4** *(2026-08-21)* - **`NS-1` parts 1–3 COMPLETE**, shipped as the standalone
  `Minecraft Clone/Dev/Validate Serialization Round-Trip` (`Assets/Editor/Validation/SerializationRoundTrip/`,
  8 baselines + the `K04` repro), registered as the **24th** suite (`ExpectedSuiteCount` 23 → 24); no production
  code changed. **Census re-verified against a real full run: 529 / 23 → 537 baselines / 24 suites, 0 failures,
  1 known-bug repro** (188 s wall clock, of which Lighting alone is 182 s); every other per-suite count
  re-verified unchanged. Built as its own suite rather than the `Validate Deserialization Robustness` partial
  the 2026-07-22 seeding bullet anticipated — that suite owns the CP-3 load-boundary *failure* paths, this one
  owns format *fidelity* (the same charter split `NS-7` made). Three corrections to what this item claimed:
  (1) part 4's "returns null, **never throws out**" is **backwards** — `RegionFile.LoadChunkData` throws on an
  unexpected I/O fault by design (the CP-3 fault ≠ "not on disk" contract, pinned by `Validate Deserialization
  Robustness` B6), so building that bullet literally would assert the opposite of a deliberate decision; the
  bullet now carries a warning instead; (2) part 3's "None / LZ4 / **GZip**" — `GZip = 3` is commented out as
  reserved, so the shipped arms are None / Deflate / LZ4; (3) part 2's note that "`GoldenMaster` is ready for
  this" is true only via a hash adapter — the framework is text-only, so the payload is pinned as a SHA-256 hex
  string, keyed on the **on-disk** version byte because `CURRENT_CHUNK_VERSION` is private. Prove-red: five
  mutations, each reddening exactly its predicted set, with three findings recorded in the item's status block
  — most notably that a **self-consistent** on-disk layout change is invisible to every scenario except the
  golden-byte pin, and that the two neighbouring storage suites stay green under the mutations `B6`/`B8` catch.
  The suite **reproduced `SERIALIZATION_BUGS` §04 on its first run** (`K04`) and corrected that entry's
  mechanism: post-CP-6 the buffer overflow is no longer a silent drop but a `Failed` result whose deterministic
  fault exhausts the retry loop into "this session's edits to that chunk are lost". Filed, not fixed.
* **v1.3** *(2026-08-20)* - **`NS-7b` COMPLETE — and it found a real bug on its first run.** The chunk-payload
  remainder shipped into the existing Migration Chain suite as `B14`–`B24` plus the known-bug repro `K10`
  (13 → **24 baselines + 1 repro**; census **517 → 528**, still 23 suites). All five chunk-format rewrites, both
  `pending_mods` steps and the v1→v2 region repack now have byte-level coverage. **`K10` reproduces
  `SERIALIZATION_BUGS` §10**: `RunAOTMigrationAsync` treats the region-layout and per-chunk-format branches as
  mutually exclusive, and v1 is the only world version whose path contains a layout step — so a v1 world is
  repacked but never format-migrated, and every chunk fails the version check and regenerates from seed. Filed,
  not fixed. *(Fixed 2026-08-21 — archived as `_FIXED_BUGS.md` Serialization 07, `K10` promoted to `B25`, so the
  suite is now 25 baselines and 0 repros.)* Three corrections to what `NS-7b` claimed: (1) it asked for one fixture builder **per chunk era**,
  but one suffices — the manager re-reads the version byte between steps, so a single v1/v2 payload walks the
  whole chain; (2) the output-side circularity it warned about is **closed**, because `ChunkSerializer.Deserialize`
  is public and takes `CompressionAlgorithm.None`, making the production reader the oracle — the limit now applies
  to the input layout only; (3) its "16-byte entries" for `pending_mods` was **correct** for the v4 record (the v5
  record is 15). Also corrected from the NS-7 close-out: `Dev.simulateMigrationCorruption` does **not** sample at
  1% — its `Random.value` read throws on the worker thread `MigrateSingleRegion` runs on, so an armed seam faults
  *every* chunk; `B13` now pins that instead of a seeded RNG. Prove-red: five mutations in two disjoint batches,
  each reddening exactly `{B17, B19, B22}` and `{B16, B18, B20, B23}`.
* **v1.2** *(2026-08-20)* - **`NS-7` COMPLETE for the `level.dat` chain and the manager's orchestration**, shipped as
  `Minecraft Clone/Dev/Validate Migration Chain` (13 baselines, `Assets/Editor/Validation/MigrationChain/`), the
  **first coverage `MigrationManager` has had of any kind**. Registered as the 23rd suite (`ExpectedSuiteCount`
  22 → 23); no production code changed and no new seam was needed, as the item predicted. **Census refreshed:
  504 → 517 baselines across 23 suites, all green** (3 min 8 s wall clock); every other per-suite count
  re-verified unchanged. Three corrections to what this item claimed, all from measurement rather than
  re-reading: (1) the "12 untested steps" hole is **not uniform** — seven are `level.dat`-only and are now
  covered, five rewrite the chunk payload, two touch `pending_mods`, one restructures the region layout; (2)
  `Dev.simulateMigrationCorruption` is **not** the negative control part 3 assumed — it *throws*, exercising the
  per-chunk catch, whereas the silent-no-op case is caught by the manager's version-byte fail-fast guard (now
  `B8`); (3) the manager's post-chain version stamp is **redundant** — deleting it leaves the end-to-end
  scenarios green, because every shipped step also stamps inside `MigrateLevelDat`, which is exactly the open
  `AOT_WORLD_MIGRATION_SYSTEM.md` §6 contradiction, now measured. Also recorded: `ToChunkRelative` (the v13
  player-position re-type) had **zero** coverage before `B2`, since the shipped `B8`/`B9` level.dat scenarios
  both start at v13; and `MigratePendingLighting` is overridden by no step, making the manager's
  pending-lighting loop dead code. The residual chunk-format work is filed as **`NS-7b`** with its fixture-cost
  estimate and the limit of its only available oracle (fixtures derived from each step's own read definition
  cannot detect a step that has always misread its input).
* **v1.1** *(2026-08-19)* - **Eighth-pass coverage audit — five items added (`NS-7`…`NS-11`) plus a
  deliberate-exclusion section.** `NS-7` (migration chain) and `NS-8` (chunk-boundary continuity + generation
  flag matrix) sharpen `NS-1` part 6 and `NS-2` with measurements rather than projections: **12 of the 14
  numbered migration steps have no coverage**, and no suite anywhere asserts that two independently generated
  adjacent chunks agree on their shared column. `NS-9` records that every Physics Solver scenario runs inside a
  single chunk (`ChunkCoord(100, -100)` is the only coordinate the suite constructs) and that Placement has no
  unloaded/placeholder-neighbor case and no block-*breaking* coverage. `NS-10` tabulates the one-sided feature
  flags, with `Pipeline Backpressure` B7 named as the template and clouds explicitly left to the non-goals.
  `NS-11` records that `Sky Render` + `UI Blur Render` contribute 16 baselines that pass unconditionally under
  `-nographics` — a deliberate per-suite convention whose *aggregate* consequence was unrecorded and whose
  policy has no self-test. Separately, the three validation entry points that sit outside
  `ValidationSuiteRegistry` on purpose (the standalone fluid-determinism sweep, the nightly lighting fuzz menu
  items, and the two standalone unit-test files) are now written down as a **choice**, with the
  register-by-default rule that keeps that list honest. **Census re-verified against a real `Validate All` run:
  411 / 17 → 497 baselines / 22 suites, all green** (0 failures, 0 known-bug repros, 0 fix candidates; 192 s wall
  clock, of which Lighting is 189 s). The figure matches the `2026-08-17` release notes exactly, so nothing has
  landed since. Per-suite deltas since the 2026-08-04 census: Lighting 92 → 99, Meshing 40 → 57, Chunk Math
  47 → 56, Command Console 54 → 56, Behavior 16 → 17, Placement 28 → 29, Deserialization Robustness 7 → 9, and
  five suites that were absent from the census paragraph entirely are now listed — Voxel Occlusion (6),
  Sky &amp; Celestial (15), Sky Render (11), World Clock (10), UI Blur Render (5). The `ChunkRelativePosition`
  tests were removed from the "standalone" tail: they are the Chunk Math suite's `.ChunkRelativePosition.cs`
  partial and were being double-counted in prose.
* *(2026-08-03)* - **`NS-4`'s first bug fixed through it** — `PLAYER_BUGS` §04 (fast landing → `IsGrounded` stuck
  false → jumps refused) is fixed, confirmed in game, and archived. Its repro `K04a`–`K04d` was observed red against
  the unfixed solver and promoted to `B20`–`B23`, plus two tripwire baselines `B18`/`B19` that bound the fix from the
  other side: **census 402 → 408 baselines across the same 17 suites** (Physics Solver 17 → 23). Instrumentation refuted two premises the entry carried: the trigger is the residual gap at
  the landing tick's start rather than fall speed as such, and the body is never literally embedded — that symptom is
  now its own entry, `PLAYER_BUGS` §05 (largest-correction ejection of an embedded body), still open.
* *(2026-08-03)* - **`NS-4` COMPLETE** — the physics / collision-solver suite shipped (17 baselines,
  `Minecraft Clone/Dev/Validate Physics Solver`), built ahead of `PH-1` so that refactor has a gate. **Census
  refreshed: 385 → 402 baselines across 17 suites** (the +17 is entirely this suite; every other per-suite count
  re-verified unchanged). The item's status bullet records the nine-mutation prove-red map, the two coverage findings
  it surfaced (`B15` does not detect missing substepping; the `fluidType` filter has no real-data coverage), and the
  one thing the suite deliberately does **not** pin — the `IsGrounded` verdict owned by `PLAYER_BUGS` §04.
* *(2026-08-03)* - `NS-4` linked to the live bugs it would guard: `PLAYER_BUGS` **§04** (fast-landing embed →
  `IsGrounded` never recovers → jumps refused; filed the same day) and **§01**. §04 is named as the suite's
  natural first scenario because its symptom reduces to a single boolean rather than a feel judgement.
* *(2026-08-03)* - **Census refreshed: 350 → 385 baselines** across the same 16 suites. Lighting 88→92,
  Behavior 12→16, Placement 17→28, Chunk Math 46→47, Pipeline Backpressure 7→22; every other suite unchanged.
  Only the Placement delta is attributed here (VQ-2 + VQ-3 added those eleven); the rest accumulated across
  the P-*/lighting work since the last census and are recorded as counts, not provenance.
* *(2026-08-03)* - `NS-4`'s **Building blocks** upgraded from hypothesis to verified fact: VQ-3 drove
  `World.CheckPhysicsCollision` through an unmodified `PlacementTestWorld` (1950 probes), so the suite needs no
  dependency injection — and the `WorldOrigin` static trap that makes such a sweep pass vacuously is recorded.
* **v1.0** - Mandatory header completed (2026-07-26): `Version`/`Date`/`Status`/`Target`, an `Audited`
  line carrying the re-verified 350/16 counts, and a relationship list — including the distinction that
  keeps this document from overlapping the fidelity docs (**no suite at all** vs **blind spots in an
  existing suite**). No rankings or item content changed. First versioned edition.
* *(2026-07-26, `be22fefc` · `beac42b1`)* - Coverage census re-verified twice during the MP-* close-out;
  meshing tip advanced to **B40** as MH-13's neighbor light maps were closed.
* *(2026-07-25, `e788db9e` · `d3012337` · `ff3b14b6`)* - Census refreshed across the MP-* arc: the
  CrossChunk/Scheduling/Completion partials added, the `RunAll` → `Execute` registration corrected.
* *(2026-07-22 – 2026-07-23, `51553999` · `72c8b9d9`)* - **`NS-5` completed** and **`NS-1` seeded** by the
  CP-2/CP-3 phases — the first two items to move off "proposal".
* *(2026-07-08, `8ca99ab7`)* - `VS-1`'s shared runner shipped, becoming the mandated landing pattern for
  every new suite listed here.
* *(2026-07-02, `ba637e9c`)* - Initial roadmap: `NS-1..6` ranked by the severity of the failure class
  each suite would guard and by how many backlog items are blocked on an ad-hoc equivalent.

---

**Last Updated:** 2026-08-22 (**`NS-5` coverage extension `G1`–`G4`** — a blind-spot audit of the already-complete NS-5 surface found four unpinned areas, all closed as `Chunk Math` partials (56 → 72 scenarios, no production change): the padded-volume index helpers and all three neighborhood gathers (`G1`, previously **zero** direct assertions), the flattened-index inverse pair (`G2`), the `r.{x}.{z}.bin` seam on negative region coords (`G3`), and the gated legacy V1 encoder (`G4`, its deliberate `Debug.LogError` captured and asserted rather than suppressed). Each prove-red verified by mutating the production code it guards. Census re-verified against a full `Validate All` at **561 baselines / 24 suites, 0 failures, 0 isolation violations, 2 known-bug repros** (3 min 19.6 s). **`NS-2` remains the top unbuilt item.** Previously: 2026-08-21 (**`NS-1` COMPLETE — all five parts** shipped as `Validate Serialization Round-Trip`: 16 baselines plus the `K04`/`K08` repros of `SERIALIZATION_BUGS` §04 and §08, registered as the 24th suite. Census re-verified against a full `Validate All` at **545 baselines / 24 suites, 0 failures, 2 known-bug repros** (204.5 s). **`NS-2` is now the top unbuilt item.** Previously: 2026-08-21 (`NS-1` parts 1–3, census verified at 537 / 24 / 1 over 188 s). Previously: 2026-08-21 (`SERIALIZATION_BUGS` §10 fixed — `RunAOTMigrationAsync`'s region-layout and per-chunk passes now run in sequence instead of exclusively; the bug is archived as `_FIXED_BUGS.md` Serialization 07 and its `K10` repro promoted to baseline `B25`, taking Migration Chain to **25 baselines, 0 repros** and the census to **529 baselines / 23 suites, all green, 0 repros** — re-verified the same day against a full `Validate All` (3 min 14 s). A new `SERIALIZATION_BUGS` §11 was filed for the pre-`needsLight` v1 chunk layout the fix does not cover. Previously: 2026-08-20 (NS-7 **and** NS-7b shipped: `Validate Migration Chain`, 24 baselines + the `K10` repro of `SERIALIZATION_BUGS` §10, census re-verified at **528 baselines / 23 suites, all green**. Previously: 2026-08-19 eighth-pass audit: `NS-7`…`NS-11` added, plus the deliberate-exclusion section for entry points kept out of `ValidationSuiteRegistry`; census re-verified against a `Validate All` run at **497 baselines / 22 suites, all green** — matching the 2026-08-17 release notes; superseded later the same day by the C14 mirrors B108–B114, taking Lighting 99 → 106 and the total to **504**)))  
**Next Review:** whenever a suite is added or a `Validate All` count changes — the existing-coverage
paragraph is the one part of this document that goes stale silently.
