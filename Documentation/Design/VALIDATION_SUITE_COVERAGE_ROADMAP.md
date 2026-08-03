# Validation Suite Coverage Roadmap — Uncovered Systems, Ranked

**Version:** 1.0
**Date:** 2026-07-02
**Status:** **Living backlog.** `NS-5` is ✅ complete (CP-2 close-out) and `NS-1` is partially seeded
(CP-3's robustness slice); `NS-2`, `NS-3`, `NS-4` and `NS-6` remain proposals. Existing-coverage counts
are re-verified against a real `Validate All` run each time they are touched.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> Which systems currently have **no validation suite** and deserve one, ranked most → least
> important by the severity of the failure class each suite would guard and by how many queued
> backlog items (`PERFORMANCE_IMPROVEMENTS_REPORT.md`) are blocked on an ad-hoc version of the same
> gate. Produced by the seventh-pass audit (2026-07-02), which found the six existing suites
> architecturally excellent (`VS-*` items are operational only) — the remaining risk is *coverage*,
> and it is concentrated in the systems below.
>
> Status: **Living backlog.** NS-1 is partially seeded (CP-3's robustness slice, 2026-07-22) and
> NS-5 is ✅ complete (CP-2 close-out, 2026-07-22) — see the per-item status lines; the rest are
> proposals.

**Existing coverage (for contrast, counts verified 2026-07-26 against a `Validate All` run — 350 baselines / 16 suites):** Lighting (88), Meshing (40, tip B40 — now including the **MP-\* orchestration** baselines B24–B27 and B31–B33, the meshing-side groundwork this roadmap's NS-3 convergence family names, B34–B36 guarding the chunk load-animation toggle, MP-7's neighbor-map permutation guards B37–B39 — one of which guards a direction→offset table feeding the **lighting** schedule too — and MH-13's B40, the same permutation guard for the eight neighbor
**light** maps), Behavior/fluid tick (12 + determinism gates), Placement (17), MeshBuildQueue (9), LightWorkScheduler (9), Chunk Math (46), Chunk Unload Decision (9), Pool Prune Decision (5), Pipeline Backpressure (7), Save Durability (13), Deserialization Robustness (7), Spawn (10), Command Console (54), Worm Carver (6), Validation Framework (18), plus the standalone `VoxelMetadataUtility` / `FastNoiseLite` / `ChunkRelativePosition` tests.

**Build protocol for every suite below:** the `validation-driven-bugfix` skill (deterministic repro first, prove-red before trusting green, promote repros to baselines). New suites should land on the shared `ValidationSuiteRunner` (`VS-1`, ✅ shipped 2026-07-08): register `Scenario`s and return its `ValidationRunResult` from a headless `Execute()`, with a thin `[MenuItem]` wrapper. All suites stay on the custom validation framework: migrating to the Unity Test Framework was evaluated 2026-07-02 and rejected (see the status header in
[`../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md`](../Archived/UNITY_TEST_FRAMEWORK_MIGRATION.md)); the CI/coverage/XML gaps close via the VS-2 extensions instead.



**Audited:** 2026-07-02 (seventh-pass audit), counts re-verified 2026-07-26 against a `Validate All`
run — **350 baselines / 16 suites**. The audit found the then-six suites architecturally sound, so the
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
    3. **Compression matrix** — None / LZ4 / Deflate round-trip, plus loading each algorithm's output regardless of the current setting.
    4. **`RegionFile` mechanics** — sector allocate/grow/shrink/reuse across mixed-size rewrites, offset-table integrity, corrupt/truncated-file robustness (returns null, never throws out).
    5. **Pending stores** — `LightingStateManager` pending columns + blocklight and
       `ModificationManager` pending mods survive a save → load cycle (Bug 08 history lives here).
    6. **Migration fixtures** — frozen mini-region fixtures per historical save version run through
       `MigrationManager`, asserting expected current-version state (enforces "never edit a shipped migration" mechanically).
    7. **Concurrency stress (the SL-4 gate)** — parallel load/save hammering one region file with integrity assertions. Trivially green under today's global lock; becomes *the* gate when SL-4 changes the locking.
- **Building blocks already available:** `ValidationReflection` (ChunkPool stubbing),
  `GoldenMaster`, temp-directory region files (the storage manager already supports a volatile path). Phase **CP-3** of
  [CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) seeds the robustness slice (truncated/garbage/wrong-version payloads → `Deserialize` returns null, no throw, no pooled-shell leak).
- **Effort:** 🟡 core (1–5) → 🔴 with migration fixtures (6); build 1–5 first.
- **Partial status (2026-07-22):** the CP-3 robustness slice shipped as
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

---

## NS-4. Physics / collision-solver suite — **Priority 4**

- **Failure class guarded:** player-facing movement regressions (fall-through, wall snag, broken step-up) — subtle, playtest-only today. `SUB_VOXEL_COLLISION_SYSTEM.md`'s own status line says **"Automated Tests Pending — automated regression tests remain outstanding."** This suite is that outstanding item.
- **Backlog items it gates:** `PH-1` (gather-once solver refactor), `VQ-1` (integer query path under the solver), collision-bounds authoring changes (Block Editor).
- **Scope sketch:** deterministic scenarios on fixture voxel fields, asserting final position/velocity/`IsGrounded` within tolerance: flat-ground grounding, wall slide, corner snag (the `COLLISION_EPSILON`/jitter-tolerance edges), step-up onto slab and full block, sub-voxel bounds (quarter slabs, rotated custom bounds), ceiling bump, and **substep consistency** (one large displacement vs N substeps → same endpoint). The scenario table in
  `SUB_VOXEL_COLLISION_SYSTEM.md` §2 is the ready-made baseline list.
- **Building blocks:** `PlacementTestWorld` proves the concrete-`World` stubbing pattern (`ValidationReflection`). The "or the stub world populated with real voxel data" option is **verified, not hypothetical**: VQ-3 (2026-08-03) drove `World.CheckPhysicsCollision` through an unmodified `PlacementTestWorld` seeded with the real `BlockDatabase` for a 1950-probe sweep, so **no dependency injection is required** to stand this suite up. ⚠️ One trap that sweep hit first: `CheckPhysicsCollision` reads the `WorldOrigin` **static**, which survives play sessions (it is reset only on play-mode entry), so a fixture must `WorldOrigin.ResetToIdentity()` and restore — otherwise every lookup lands far from the seeded blocks and the sweep silently returns **zero hits**, passing vacuously. Assert a non-zero hit count before trusting any such sweep.
- **Effort:** 🟡.

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

---

## NS-6. Pool reset-safety audit — **Priority 6**

- **Failure class guarded:** stale pooled state after recycle — the documented historical class (`RemainingEdgeCheckRounds` shipped without a reset and silently broke edge checks; the
  `pool-reset-safety` rules exist because of it). B17 guards exactly one pooled type (`MeshDataJobOutput`); the rest rely on review discipline.
- **Backlog items it gates:** `WG-1` (new generation-buffer pool), `DT-2` (retained visualizer containers), any future pooled type.
- **Scope sketch:** one generic, reflection-driven audit rather than per-field baselines: for each pooled type (`ChunkData`, `ChunkSection`, `Chunk`, `VisualizerChunkData`, pooled job outputs), write sentinel values into every transient field → `Reset()`/`Release()` → assert every field returned to its documented default. A newly added field with no reset **fails automatically** — the exact historical bug shape. Needs a per-type defaults map (or a `[PoolResetDefault]`
  attribute) — cheap, not free; fields legitimately exempt (persistent buffers) get an explicit exemption list so silence is never accidental.
- **Effort:** 🟢.

---

## Explicit non-goals

No suites proposed for: **UI/menus and input** (event-driven, low blast radius, visually verified), **clouds and debug tooling** (`DT-*` hygiene items suffice; debug tools are not correctness-critical), **OM-1 device calibration** (device-dependent by design, verified by its own startup probe), and **shaders/GPU output** (needs image-based comparison — a different kind of harness; revisit if GS-1/GS-3 visual refactors recur).

## Sequencing summary

`NS-1` (core, parts 1–5) and `NS-2` first — they guard the two irreversible failure classes (data loss, seed breaks) and unblock the most queued work (`SL-*`, `WG-3`, `ET-2`). `NS-5`/`NS-6` are 🟢-sized and should simply ride along with the work that triggers them (WS-1/VQ-1 and the next new pool, respectively). `NS-3` is the biggest investment — start it as repro fixtures for the three historical deadlocks and grow it scenario-wise, ideally before `P-4`/`OM-2` rework the scheduling invariants it guards. `NS-4` lands whenever `PH-1`/`VQ-1` get scheduled,
using the §2 scenario table as its baseline list.

---

## Document History

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.0** - Mandatory header completed (2026-07-26): `Version`/`Date`/`Status`/`Target`, an `Audited`
  line carrying the re-verified 350/16 counts, and a relationship list — including the distinction that
  keeps this document from overlapping the fidelity docs (**no suite at all** vs **blind spots in an
  existing suite**). No rankings or item content changed. First versioned edition.
* *(2026-07-26, `be22fefc` · `beac42b1`)* - Coverage census re-verified twice during the MP-* close-out;
  meshing tip advanced to **B40** as MH-13's neighbor light maps were closed.
* *(2026-07-25, `e788db9e` · `d3012337` · `ff3b14b6`)* - Census refreshed across the MP-* arc: the
  CrossChunk/Scheduling/Completion partials added, the `RunAll` → `Execute` registration corrected.
* *(2026-08-03)* - `NS-4`'s **Building blocks** upgraded from hypothesis to verified fact: VQ-3 drove
  `World.CheckPhysicsCollision` through an unmodified `PlacementTestWorld` (1950 probes), so the suite needs no
  dependency injection — and the `WorldOrigin` static trap that makes such a sweep pass vacuously is recorded.
* *(2026-07-22 – 2026-07-23, `51553999` · `72c8b9d9`)* - **`NS-5` completed** and **`NS-1` seeded** by the
  CP-2/CP-3 phases — the first two items to move off "proposal".
* *(2026-07-08, `8ca99ab7`)* - `VS-1`'s shared runner shipped, becoming the mandated landing pattern for
  every new suite listed here.
* *(2026-07-02, `ba637e9c`)* - Initial roadmap: `NS-1..6` ranked by the severity of the failure class
  each suite would guard and by how many backlog items are blocked on an ad-hoc equivalent.

---

**Last Updated:** 2026-07-26 (header completed; counts verified at 350 baselines / 16 suites)
**Next Review:** whenever a suite is added or a `Validate All` count changes — the existing-coverage
paragraph is the one part of this document that goes stale silently.
