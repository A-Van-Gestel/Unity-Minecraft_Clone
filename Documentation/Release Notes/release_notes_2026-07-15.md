WIP Release introducing an **Unbounded Infinite World** (both signs, no XZ limits), a per-world **World Border** with an animated border wall, **OM-1 Device Calibration** for auto-tuned per-device budgets, a **Shared Validation Framework** with a "Validate All" aggregate + headless CI entry point, the **LI-2 Banded Lighting Gather** (a confirmed in-game frame win), and a systematic **Lighting Bug Fix campaign** (Bugs 05, 13–18).

This release includes the following major new features and improvements:

- **Unbounded Infinite World (WS-1 → WS-3)**: The world is now unbounded on both horizontal axes — chunks generate, save, and load at any XZ coordinate, positive or negative, with only the Y axis still bounded:
    - WS-1: Replaced managed chunk-coordinate math with branch-free `ChunkMath` shift/mask helpers (correct floor-division for negative coordinates), migrated all managed call sites, `RegionAddressCodec.V2`, and `StandardWormCarverJob` onto them, guarded by an equivalence sweep.
    - WS-2: Relaxed the XZ upper bound to a `>= 0` floor only (unbounded +XZ), `WorldCentre` → `DefaultSpawnPosition`, and minimap floor/crosshair cosmetics. No save migration required.
    - WS-3: Dropped the XZ floor entirely — `IsVoxelInWorld`/`TryGetVoxel` are Y-only and `IsChunkInWorld` is always true. Fresh worlds now spawn at the origin `(0, 0)`. Verified in-game out to ≈(−10 000, −10 000); the existing V2 region codec and region filenames were already negative-correct, so the save format is unchanged.
    - VQ-1: Integer voxel-query fast path (`TryGetVoxel` + last-chunk cache with topology-version invalidation), with consumer migration off the float path.
- **TF-14: World Border**: New per-world gameplay fence, configurable at world creation:
    - Border radius persisted in `level.dat` (**format v11 → v12**, `0` = disabled); existing worlds default to disabled.
    - Player clamped inside the border in `VoxelRigidbody`, world-border input on the create-world menu, and a minimap border rect.
    - Animated `Minecraft/BorderWall` URP shader + `BorderWallRenderer` — 4 camera-following sliding quads with a depth offset to avoid Z-fighting against the voxel grid.
    - The generation/lighting/meshing pipeline stays border-blind by design (the border is a gameplay clamp, not a world-bounds check).
- **OM-1: Device Calibration**: First-launch device probing that auto-tunes per-device performance budgets:
    - `DeviceCalibration` + `StartupCalibrationProbe` over a shared `IsolatedJobProbe` (mesh), writing results into `settings.json`.
    - Device-scaled in-flight mesh cap + chunk-job pool retention, with a `RecalibrateDevice` action (pool retention applies on next world load; per-frame budgets and the in-flight cap apply live).
    - Baseline-capture probe mode (99-sample precision + per-leg spread) writing an on-disk report, with `REFERENCE_*_MS` anchors re-based on player medians (0.952 / 0.604).
    - Capture reports now prepend `BenchmarkEnvironment.DescribeSystem()` (backend + Editor-vs-Player), so a capture's comparability to the reference anchor is self-evident.
- **Player Placement Overhaul**: Extracted the placement decision out of `PlayerInteraction` into a dedicated `Placement` module (`PlacementController` + `PlacementProbe` + `PlacementResolver`) over the concrete `World`, with per-probe reach/check-increment tuning:
    - Split `BlockType.canReplaceTags` → `worldGenCanReplaceTags` + `placementCanReplaceTags`, routed by `VoxelModSource`, with a behavior-preserving back-fill migration.
    - Retuned the player placement mask to `REPLACEABLE|LIQUID` (dropping `PLANT` + world-gen structural tags) → resolves the world-gen tag leak (`PLAYER_BUGS §03`).
    - `REQUIRES_SUPPORT` blocks can no longer be placed without support (grass blades on water).
    - Retuned CanReplace/WorldGenReplace tags for grass blades and other missed blocks so they can be player-placed in liquids.
- **Block Database Decoupling**: `World.blockDatabase` is no longer a serialized field — it loads once via a shared `ResourceLoader.LoadBlockDatabase()` behind a read-only `World.BlockDatabase` property, with remaining ad-hoc loads routed onto single paths and a shared runtime `JobDataManagerFactory` de-duplicating `World.PrepareGlobalJobData` and `EditorJobDataManagerFactory`.
- **TESTING: Shared Validation Framework (VS-1 → VS-3)**: All engine validation suites now run on one runner, with an aggregate entry point and CI support:
    - VS-1: `ValidationSuiteRunner` + `ValidationRunResult`/`Scenario`/`KnownBugChannel`, with six existing suites + `ChunkRelativePosition` migrated onto it. Console UX gained a live progress bar, slowest-N + failed-baseline recap, a per-scenario counter, and h/min/s/ms timings for long runs.
    - VS-2: `Minecraft Clone/Dev/Validate All` aggregate runner over an explicit 8-suite registry, with a `World.Instance` isolation guard (snapshot → force-restore → mark-failed) so no suite can leak global state into the next. Added an NUnit3 XML writer (`IValidationResultWriter`) and a `ValidationSuiteCI` headless entry (`RunHeadless` via `-executeMethod`, `RunSelected` agent path, `-validationSuites` subset, exit(1) on baseline failure or ran-nothing).
    - VS-3: `StaleAssemblyGuard` — a warn-once stale-code preamble in the shared runner over three signals (isCompiling/isUpdating, source-vs-DLL timestamps, and domain-vs-disk), so a suite can never silently report on stale code.
    - Promoted the lighting work-cap fail-safe to a **runner-level invariant** (`FailSafeErrorScope`): any scenario logging a tagged engine error force-fails, across all 8 suites.
    - A `Validation Framework` self-test suite (18 baselines) proves the XML round-trip mapping, the isolation-guard trip path, the registry count-floor, and the stale-assembly guard.
    - **Validate All now runs 8 suites / 197 baselines green.**
- **TESTING: New Validation Suites**: Three new suites joined the registry:
    - **Placement** (13 baselines): CanReplace matrix, top-down outcome, and a real-database tag audit driven through the production `PlacementController` — guards the `PLAYER_BUGS §03` world-gen tag leak.
    - **Chunk Math** (26 baselines): coordinate shift/mask equivalence, including the WS-2/WS-3 unbounded + negative-coordinate bounds baselines and region-codec pins.
    - **Mesh Build Queue** (9 baselines) and **Light Work Scheduler** (9 baselines): guard the MT-1 queue promotion and the MT-2 ready/waiting split respectively, both prove-red confirmed.
- **TESTING: Lighting Suite Growth (55 → 86 baselines)**: Driven by the AS-* async-bug roadmap and the HF-* harness-hardening plan:
    - AS-1: Bug-13 synchronous repros K13a–K13d (dynamic-stamp live-lock proven via an oscillation probe).
    - AS-2: Scheduler-mode baselines B66–B70 — the frame simulator now drives the real `LightWorkScheduler` park/promote layer (cross-chunk parity, neighbor-ready promotion, 50-seed Bug-09 fuzz, and a prove-red where suppressed completion-promotion stalls a re-flagged chunk).
    - HF-1: Editor/dev-only `ChunkData` bounds asserts (69-site caller audit clean) replacing fail-soft accessors.
    - HF-2: Per-job fault isolation across all 3 job passes (Complete-fail retry + one-error release-and-continue + `LastFaultedLightJobs`), in-game injection verified: 1 error, no cascade.
    - HF-3: Border-heightmap fuzz (25-seed suite + 200-seed nightly) — which **found Bug 15** and produced the first synchronous **Bug 05** repro.
    - HF-4: Extracted the shared `LightingScanDecision.EvaluateReadyChunk` scan arm and the `LightingCompletionPass` completion skeleton, so production and the editor frame simulator drive one code path (byte-identical, in-game confirmed).
    - Post-Bug-16/17 fidelity audit closed C6/C10/C11/C12/C13 and B8, adding B89–B94 (RGB stale pull-back self-heal, interrupted-reconciliation seam fuzz, band differential, and a Bug-18 mixed-channel self-heal coverage baseline).
- **Lighting Bug Fixes** (Bugs 05, 13–18): Systematic fix campaign driven by the validation suite, all in-game confirmed and archived:
    - Bug 13: Slab live-lock → extended the Bug-11 veto with live third-party cross-chunk support, excluding the emitter (deferred mods carry the emitter). Repros K13a–K13d → baselines B56–B59.
    - Bug 14: Stale pull-back ghost light → `PullBackClaim` verify-at-merge against the live neighbor + a halo-node claim-contract fix. K14a → B61, with B60 as a contract guard.
    - Bug 15: Cross-chunk seam surface stamp wiped by a border-column edit → cross-seam re-derivation now stamps opaque centers (`CheckEdgeVoxel`/RGB + `PullBackClaimStillSupported` mirror + BFS re-spread of unchanged-but-lit nodes), plus a darkness-wave residual fix re-deriving opaque seam stamps from dimmer/zeroed cross-seam neighbors. K15b/K15c → B62/B63.
    - Bug 05: Border-shadow edge-round exhaustion → `ModifyVoxel` re-grants a bounded edge-check round on border-column opacity edits (self + cardinal neighbors, add-only). K15a → B64, 200-seed sweep green.
    - Bug 16: Runaway RGB removal loop (per-channel removal ↔ pull-back infinite cycle) → removal-node masking. K16a → B87. The 200k work cap is retained as a permanent fail-safe.
    - Bug 17: Sourceless RGB ghost island → per-channel cross-chunk removal veto (a sky Bug-11/13 mirror) in `ComputeBlocklight` + `In`/`CrossChunkBlocklightSupport`. K17a → B88.
    - Bug 18: Sourceless RGB seam loop → RGB cross-seam removal initiator (`EmitCrossChunkBlocklightRemoval`, Bug 12's mirror) in `PropagateDarknessRGB`, adjudicated by the Bug-17 veto. K18a → B90.
    - Additional fix: an LI-2 band top/bottom contradiction now fails closed via a shared `LightingBandDecision.ReconcileBand` — an inverted band resets to full height instead of mis-serving a 1-row band (baseline B93).
- **LI-2: Banded Lighting Gather**: Derive a Y-band for the lighting job's gather/scan/extract instead of always working the full column height, behind `EnableLightingBandGather` (default on, pooled path):
    - Three derivation rules (coverage + 16 headroom / column-recalc full-sky / cross-seam ±1) with virtual above-band reads, bit-identical to full-height (differential baselines B71–B78 incl. a 12-seed fuzz + headroom-strip prove-red).
    - Editor screening: −31…−75% on gather/scan-dominated jobs, never slower on the clean floor.
    - **GO FINAL** — IL2CPP in-game A/B confirmed a sustained frame win: settled-streaming **−26% frame / −27% Light**, min FPS **58 → 65**, floodlight −9%, and lighting removed as the worst-frame bottleneck.
- **LI-2b: Bottom Band**: Extended the Y-band downward using new per-section emissive-presence metadata (`ChunkSection.emissiveCount` via a palette-independent `EmissiveBlockLookup`) + an inert-dark coverage summary, with `DeriveBandMinY` over 9 chunks and heightmap-min headroom. Bit-identical (B79–B85). Editor A/B: −49…−59% on bottom-engaged deep-floor buried-lamp shapes vs. the shipped top band; IL2CPP in-game frame-neutral (worst-frame Light −8.6%, tails/GC better) → **GO FINAL** on the not-slower basis.
- **TG-4 Phase 4b: Fluid Y-Band Gather**: Refactored `ChunkMath` gather into a band-aware `GatherPaddedRange` core + a `GatherPaddedFluidVoxelsBand` wrapper (full-height callers byte-identical), then shipped the band behind `EnableFluidBandGather` (now default on). IL2CPP A/B: serial worst-tick tail −24…−46% on big floods + floor −1.6…−4.6%, frame-neutral in-game (Light-bound); managed → halo-full 2.16–2.89× confirms no regression. Behavior differential 15/15 green with new BH-4-SPLIT-Y/BAND-EDGE fixtures.
- **TG-6: Active-Voxel List Pooling**: `ActiveVoxelListPool` with a single terminal release, plus a fresh-vs-pooled leg on `ChunkGenerationBenchmark` to isolate the win.
- **MT-1: O(1) Mesh Queue**: New `MeshBuildQueue` pooled intrusive list replaces the linear queue, with immediate re-request promoting an already-queued chunk to the head (O(1) `MoveToHead`), guarded by baseline B9.
- **MT-2: Light Scheduler Ready/Waiting Split**: The light scan now visits only gate-ready chunks — blocked chunks park and are promoted on unblock events (generation/load/light-job completion), with a 1 s `PromoteAll` fail-safe backstop. In-game verified with zero fail-safe promotions.
- **MT-3: Zero-Alloc DebugScreen Refresh**: Shared `StringBuilderFormat` helpers eliminate the per-refresh allocations and de-duplicate `BenchmarkHUD`.
- **MT-4: O(1) Custom-Mesh Index Lookup**: Dictionary-backed lookup in `JobDataManagerFactory` replaces the linear scan.
- **MT-5: Zero-Alloc Startup Fills**: Startup `NativeArray` fills via a new `Helpers/NativeArrayHelper` (no `.ToArray()` round-trips).
- **Android Report Retrieval**: Lifted the Android public-Downloads (MediaStore) writer out of `DeviceCalibration` into `BenchmarkEnvironment.WriteReportToDisk`, so **all** benchmark reports are retrievable on Android — and the Results Screen "Open Folder" now opens the Android Downloads view (`ACTION_VIEW_DOWNLOADS`) instead of a no-op `file://` URL.
- **Bug Fixes**:
    - Unresolved-spawn sentinel `float.MinValue` overflowed the `PlayerBody` worldAABB during chunk load → moved `UNRESOLVED_HEIGHT` to −1 000 000 (far below terrain, still renderable) + an `IsHeightResolved()` helper replacing the magic `+1f` check.
    - Stale `SectionRenderer` materials when domain reload is off → reset the static material cache on play start (also clears UDR0001).
    - OM-1 calibration failure latched desktop defaults → stamp the version only on success + guard a null `BlockDatabase` (throw vs. NRE).
    - A corrupt/hand-edited `settings.json` could stall meshing or storm the pool → clamp device-calibrated in-flight + pool-retention budgets to ≥ 1.
    - A single frame could push its per-frame mesh budget past the RAM-calibrated in-flight ceiling → re-check the in-flight mesh cap each scheduling iteration (OM-1 overshoot on fast-CPU/low-RAM devices).
    - Cloud hull Z-fighting against the voxel grid → inflate the hull off the grid.
    - `CS0162 "Unreachable code detected"` false positive in `StartupCalibrationProbe`.
    - Broken/incorrect docstrings related to the `BlockType.canReplaceTags` split; leftover `FormerlySerializedAs` attributes removed.
- **Refactors**: Extracted `PlacementController`/`PlacementProbe`/`PlacementResolver`, `LightingScanDecision`, `LightingCompletionPass`, `LightingBandDecision`, `ChunkMath.GatherPaddedRange`, `JobDataManagerFactory`, `ResourceLoader`, `NativeArrayHelper`, `StringBuilderFormat`, `ActiveVoxelListPool`, and `BenchmarkEnvironment` shared helpers. Renamed `CompressionAlgorithm.GZip` → `Deflate` for accuracy (value 1 unchanged — saves stay compatible) [MT-6]. Fixed the remaining "neighbour" → "neighbor" rename casing and removed the dead commented-out
  `WorldEditor.cs` inspector.
- **Unity Upgrade**: Updated to Unity 6000.5.4f1 (from 6000.5.1f1) via 6000.5.2f1 → 6000.5.3f1 → 6000.5.4f1. IL2CPP managed code stripping raised to "low" (from "minimal").

This release also contains the changes & improvements of the previous releases:

- **Full RGB Smooth Lighting Engine** & **Lighting Bug Fixes** (Bugs 06–12)
- **Lighting, Meshing & Behavior-Tick Validation Suites**
- **TG-4 Full Fluid Burst Port** (Phases 0–4b) & **LI-1/P-2 Halo-Padded Lighting BFS**
- **MR-1…MR-9 Meshing Optimizations** (packed vertex format, pooling, off-main-thread post-process)
- **Persistent World Spawn Point** & initial **Android Support**
- **Extended Graphics & Display Settings** & **Data-Driven Settings UI** (Phases 1–4)
- **Multi-Noise Terrain Generation** & **Cave Generation Overhaul**
- **Pause Menu & UI Overhaul** with global Tooltip system
- **3D Chunk Preview & World Gen Preview Editor Tools**
- **Benchmark System**

## What's Changed
* Feat/modular world generation & world types by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/6
* Feat: Placement validation suite + Bugfixes by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/8
* Perf: MT-1. `List.Insert(0)` / `RemoveAt(i)` — O(n) mesh priority queue by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/9
* Perf: MT-2 - Light scheduler snapshots the full dirty set every frame by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/10
* feat/async-lighting-validation-suite by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/11


**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-06-25...2026-07-15
