# Validation Suites — Inventory & Routing

Companion reference for the `validation-driven-bugfix` skill: every editor validation suite under
`Assets/Editor/Validation/`, what it guards, and its entry points. Use this to route a bugfix or
refactor to the right suite. Baseline counts and scenario IDs drift — read the suite's runner and
`.Baseline.cs` files for the current set; this inventory names only the stable structure.

Deep-dive walkthroughs exist for the two reference implementations:
[lighting-suite.md](lighting-suite.md) (bug-repro emphasis) and
[meshing-suite.md](meshing-suite.md) (perf-refactor regression-guard emphasis). The other suites
follow the same runner conventions (partial-method registration, `[PASS]`/`[FAIL]` console
output, baseline vs known-bug semantics).

## Suite overview

| Suite              | Location (`Assets/Editor/Validation/`) | Menu item (`Minecraft Clone/Dev/`)  | Guards                                                                              |
|--------------------|------------------------------------------|---------------------------------------|----------------------------------------------------------------------------------------|
| **Lighting**       | `Lighting/`                              | `Validate Lighting Engine`             | The lighting engine: BFS sky/block light, cross-chunk mods, in-flight staleness         |
| **Meshing**        | `Meshing/`                               | `Validate Meshing`                     | MR-* output-preserving mesh optimizations + `SectionRenderer` apply path                |
| **Behavior**       | `Behavior/`                              | `Validate Behavior`                    | TG-4/TG-5 block-behavior tick parity (fluid/grass `Behave`/`Active` path)               |
| **Placement**      | `Placement/`                             | `Validate Placement`                   | Player placement decision (`PlacementController`/`PlacementResolver` tag logic)         |
| **MeshQueue**      | `MeshQueue/`                             | `Validate Mesh Build Queue`            | MT-1 `MeshBuildQueue` contract (dedup, ordering, immediate-promotion)                   |
| **LightScheduler** | `LightScheduler/`                        | `Validate Light Work Scheduler`        | MT-2 `LightWorkScheduler` ready/waiting split, promotion events, `PromoteAll` fail-safe |

Additional standalone menu items: the Lighting suite has separate generation-fuzz runners
(`Validate Lighting Engine (Bug 05 Canopy Fuzz)` / `(Bug 09 Geometry Fuzz)`); the Behavior folder
has three `Validate Fluid Parallel Determinism*` gates (below).

## Per-suite notes

### Lighting (`Lighting/`)

Fixture `Framework/LightingTestWorld` (+`.Builder`) runs the real `NeighborhoodLightingJob` and
applies cross-chunk mods via the shared `CrossChunkLightModApplier`. Oracle
`Framework/LightingOracle` is a borderless global flood-fill (the spec). Assertions in
`Framework/LightingAssert` (`MatchesOracle`, `FieldsEqual`, `Converged`, …, all with bounded
diffs). Palette `Framework/TestBlockPalette` uses **test-local indices** (synthetic fixtures,
independent of the real database). Fidelity backlog:
`Documentation/Architecture/Testing Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md`; async-bug
roadmap: `Documentation/Design/LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md`.

### Meshing (`Meshing/`)

Fixture `Framework/MeshingTestWorld`; oracle `Framework/MeshOracle` (naive standard-cube mesher =
pre-optimization ground truth); assertions `Framework/MeshAssert` / `Framework/RendererAssert`;
`Framework/SectionRendererTestFixture` stubs the renderer apply path via reflection. Palette
`Framework/TestMeshBlockPalette` uses **test-local indices**. Fidelity backlog:
`Documentation/Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`.

### Behavior (`Behavior/`)

Parity guard for the tick path the TG-4/TG-5 optimizations re-architect. Three legs: golden-master
snapshots (shared `Validation/Framework/GoldenMaster`), behavioral invariants (determinism,
non-vacuity), and the BH-D1 old-vs-new differential. `Framework/BehaviorTestWorld` models the
production tick driver with selectable `TickDriver` (`Legacy` single-set vs `SplitFamily`
per-behavior buckets); `Framework/BehaviorSnapshot` records each tick;
`Framework/BehaviorDifferential` compares drivers under the TG-4 §4.3 canonicalization
(same-voxel writes order-sensitive, independent mods position-canonicalized).
**Palette caveat:** `Framework/TestBehaviorBlockPalette` is indexed by the **REAL `BlockIDs`
values** — behavior code hardcodes block identities, so slots must match production IDs (unlike
the lighting/meshing palettes). Fidelity doc:
`Documentation/Architecture/Testing Framework/BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md`.

`FluidParallelDeterminismValidation` (own menu items) gates the parallel fluid flags: serial
baselines vs concurrently-scheduled pooled tickers must be byte-identical and run-to-run stable,
for the interior path (Phase 4a), the full halo path (4b), and the Y-band gather variant.

### Placement (`Placement/`)

Drives the same `PlacementController` that `PlayerInteraction` uses in-game.
`Framework/PlacementTestWorld` returns a `PlacementOutcome` capturing each tag-driven decision
independently. Three scenario categories: **Baseline** (`.Baseline.cs`, controlled
`TestPlacementBlockPalette` — every block correctly configured, local ids, slot 0 must stay Air),
**Data-audit** (`.DataAudit.cs`, inspects the **real** `BlockDatabase.asset` for ray-tunneling
`placementCanReplaceTags`), and **Regression** (`.Regression.cs`).

### MeshQueue (`MeshQueue/`) and LightScheduler (`LightScheduler/`)

Both test a pure managed data structure in isolation (no Burst, jobs, or world state): the MT-1
`MeshBuildQueue` ("bit-identical to the old `List`+`HashSet`" contract) and the MT-2
`LightWorkScheduler` (parked chunks re-enter ready only via flag callback, 3×3 promotion event, or
`PromoteAll` backstop). All scenarios are baselines; the known-bug channel exists for parity but
is unused. **Prove-red convention:** each scenario's docstring names the one-line mutation that
should turn it red — break, run, confirm red, revert.

## Shared framework (`Validation/Framework/`)

- `GoldenMaster` — golden-master (characterization) comparison for suites whose oracle is "the
  output did not change"; centralizes CRLF normalization + capture-mode workflow.
- `ValidationReflection` — reflection helpers for driving private setters in edit mode (stubbing
  `World.Instance`, `World.ChunkPool`, tick counters) so each harness doesn't re-implement them.

## Not scenario suites

`Validation/` also holds standalone micro-tests that are not baseline/known-bug suites:
`ChunkRelativePositionTests`, `FastNoiseLiteTests` (+ `FastNoiseLiteGoldenValues.txt`),
`VoxelMetadataUtilityTests`.
