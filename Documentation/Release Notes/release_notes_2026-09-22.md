**Build**: `2026-09-22 - RC 93 Audio Engine + Swimming` · **Range**: `2026-08-17` → `2026-09-22` · **Commits**: 334 · **Unity**: 6000.6.2f1 · **level.dat**: v15

Release giving the engine a voice and a water line: a full **Sound Engine** (S0 → S11 — block one-shots, footsteps, biome ambience beds, fluid emitters and a weighted music scheduler), **Swimming & Fluid Physics** with buoyancy, drag, flow push and climb-out, **Underwater Rendering** (UW-0 → UW-6) that fogs the view from inside the liquid, and a **Banded UI Blur** compositor (UB-0 → UB-8) that moves the entire UI into the render graph. It also lands a **Toast Notification** system, the **Unity 6.6** upgrade, and grows the validation framework from 22 suites / 497 baselines to **30 suites / 753 baselines**.

## Highlights

- **Sound Engine (S0 → S11)**: The world is audible — block break/place/footstep one-shots, biome and cave ambience beds that pan toward their biome, looping water and lava emitters, swim strokes and entry splashes, and a weighted music scheduler with fades.
- **Swimming & Fluid Physics**: Entities float, drag, get pushed by flow, swim and climb out of liquids, with Water and Lava tuned separately.
- **Underwater Rendering (UW-0 → UW-6)**: Liquid backfaces render, and being submerged fogs the screen through a depth-bounded medium — water cool and clear, lava a dense hot orange.
- **UB-0 → UB-8: Banded UI Compositing**: UI is composited inside the render graph in sorting-layer bands, so blurred panels re-capture what is behind them and stack correctly without a single stencil mask.
- **Toast Notifications**: A pooled corner card stack with Info/Warning/Error variants, driving the "now playing" track card.
- **Audio & Sound Authoring Tools**: A Sound Editor window (families, ambience, emitters, loudness), an EBU R128 loudness auditor, and a WAV → OGG pack converter.
- **Validation Framework: 22 → 30 suites, 497 → 753 baselines**, with eight new suites covering serialization round-trips, the migration chain, the chunk pipeline, biome selection, sound, underwater, clouds and UI bands.

## Gameplay & Visuals

- **Sound Engine (S0 → S11)**: A complete audio stack built over a mixer with per-category volumes:
    - S0 — Data foundation: a `SoundMaterial` channel on every block, `BlockSoundGroup`/`BlockSoundDatabase`, the tag-preset seeding that fills it, an `AudioMixer` asset and an **Audio** settings tab.
    - S1 — One-shots: `SoundManager` with pooled 3D voices, and break, place and footstep triggers.
    - S4 — Footsteps read **two** cells: a non-solid occupant (a slab, a plant) layers over the block actually supporting the foot. Swim strokes and a fluid-entry splash followed, with `SoundMaterial.Lava` split off `Liquid`.
    - S2 — Ambience & music: `AmbienceDirector` runs a four-source bed roster weighted by the surrounding biomes, plus a cave layer, a rest cycle, a per-source underwater low-pass and a `MusicScheduler`. Beds are gated on depth below the surface and silenced entirely under a committed cave bed.
    - S5/S6 — Beds pan to their biome's bearing (derived from the cellular noise cell offset) and are drawn from per-biome, altitude-banded track pools.
    - S3 — Fluid emitters: a Burst `FluidEmitterScanJob` bins sounding fluid per section, and `FluidEmitterDirector` runs six looping voices for streams, waterfalls and lava. Lava still plays water's clips pending content.
    - S7/S8 — Per-track gain composed through `AmbienceResolution.BedSourceVolume`, and `MusicTrack` weighted music pools where biome tracks *add* to the global pool instead of replacing it, with a `/music` command to audition them.
    - Music plays by the light it belongs in — Daylight/Dark tracks and a dwell-filtered underground answer, all read from one `AudioContext`.
    - S10 — Music fades in, fades at its tail, and fades across every interruption; quitting a world fades the listener down before the scene is torn down.
    - S11 — Sprint, jump-start and jump-land footstep events with their own clips, falling back to the step clips when a family lacks them.
    - Content: Kenney CC0 impacts, NOX Sound CC0 footsteps, nature ambience beds and stream/waterfall loops, two Freesound lava loops, and 17 Cozy Tunes music tracks under Pizza Doggy's own (non-CC0) licence — every pack credited in `CreditsDatabase` and the markdown mirror.
    - `/sound` reports the live ambience mix, every duck applied and which one binds.
- **Swimming & Fluid Physics**: `VoxelRigidbody` gains buoyancy, drag, flow push, swimming and climb-out through a single `World.GatherFluidContact` gather, with Water and Lava values and the player's swim step, jump release and step smoothing tuned in the scene. Closes the physics half of FLUID_BUGS #02/#14 — the item-entity half stays blocked on #13.
- **UW-0 → UW-6: Underwater & Submersion Rendering**: Liquid backfaces are no longer culled, a shared sub-cell eye-submersion query (adopted by the audio layer) decides when you are under, and a screen-space medium fogs the view bounded per pixel by scene depth. UW-6 authored lava as a hot orange at density 1.5 and re-expressed water in sRGB with its confirmed look unchanged. UW-5's screen-space surface band was built and reverted — a sine band cannot meet a straight mesh edge — and stays paused with the mesh route open.
- **UB-0 → UB-8: Banded UI Compositing**: The UI moved into the render graph. Canvases converted to `Screen Space - Camera`, bands key on **sorting layer** (so a nested canvas routes a whole subtree with zero prefab overrides), and `UIBandCompositeRendererFeature` walks the bands at `AfterRenderingPostProcessing`, re-capturing the blur between UI layers. MainMenu joined the composite with four band roots and `UIBlurClear` so its backdrop blurs without darkening. Band occupancy reads the subtree's *content* rather than the band root being enabled, dropping an idle world from 4 blur captures to 1. All six stencil masks in the project were converted to `RectMask2D` — a stencil mask cannot clip in the band pass.
- **Toast Notifications**: A pooled, corner-anchored card stack on its own canvas, with Info, Warning and Error variants sharing the console's accent colors, font-checked glyph icons and per-variant tinted frosted backdrops. Drives the now-playing music card (toggleable in the Audio tab) and a `/toast` dev command.
- **FL-4/FL-4b: Cross-mesh flora variation**: Per-voxel offset, scale and mirror variation for cross-mesh flora, with the variation envelopes authored per block in the `BlockEditor` and a vertical-escape clamp so nothing lifts out of its cell.
- **Biome readout**: A managed biome-at-position query (`IChunkGenerator.TryGetBiomeAt` + `BiomeSample`) backs a `BiomeTracker` and a biome line on the debug screen.
- **Clouds through water**: Clouds are now visible through a water surface — a `VoxelCloud` `LightMode` pass drawn at `AfterRenderingSkybox` lands them in the opaque copy the liquid shader samples (CL-9). The bundled cloud pattern texture was removed; the texture-driven path stays as an optional art-direction hook, shipping unassigned.

## Engine & Performance

- **LP-1 → LP-7: Pipeline State Refactor**: The chunk pipeline's lighting flags and neighbor gates became one shared, testable surface:
    - LP-2: `World`'s three neighbor gates route through a shared `NeighborReadinessDecision`, collapsing `ReadyAndLit`'s duplicated loops; the lighting harness calls the same predicate.
    - LP-3: `IsAwaitingMainThreadProcess` removed.
    - LP-4: A `LightingWork` byte replaces the loose bools — every production lighting-flag write is now a transition method on `ChunkData`, and the edge-check cascade's effects live next to its decision.
    - LP-5: Both lighting schedulers derive the edge-check flag once via `ScheduledEdgeCheckDecision`; the startup coroutine's arms and its termination test route through `LightingScanDecision`.
    - LP-6: Lazy neighbor-gate evaluation — `INeighborGates` plus one shared generic core, applied at `World`'s four scan call sites, with dev-only gate-walk probes and a `LightingGateWalkBenchmark` micro A/B. Confirmed by a Master IL2CPP run.
    - LP-8 (production-scheduler harness driver) was filed with its measured null result and is not shipped.
- **Flag retirement**: The P-4 backpressure and P9-2 cascade rollback flags are gone — both paths are now unconditional, B97 folded into B98's budget sweep, and the P-4 harness retargeted to a frame-cap sweep. Four settings keys disappear with them.
- **Unity 6.6 upgrade**: 6000.5.8f1 → 6000.6.2f1, stepping through 6000.5.9f1, 6000.5.10f1, 6000.6.0f1 and 6000.6.1f1, with the 6.6 asset reserialization pass that came with it.
    - The shader target floor rose `3.5` → `4.5` now that 6.6 drops Android ES 3.0, and `SHADER_CONVENTIONS` was repriced by DX feature level.
    - The deprecated `DEVELOPMENT_BUILD` gate was replaced across 53 sites, split by meaning: `UNITY_ENABLE_CHECKS` for the 3 assertions, `UNITY_INCLUDE_INSTRUMENTATION` for the 50 telemetry sites. A `Windows - Profiler` build profile pins the Managed Code Variant to `Checked` so captures keep URP's per-pass samplers.
- **Project Auditor sweep (AU-1 → AU-3)**: Static Batching disabled for Standalone (URP0302 SRP-Batcher conflicts 7 → 0), and `[NoAutoStaticsCleanup]` with per-member reasons on 92 statics across 45 files plus a `SerializationBufferPool.DomainReset`, taking UAL0010/UAL0013 from 132 → 0 under `Assets`. Findings are tracked in the new `PROJECT_AUDITOR_FINDINGS_REPORT`.
- **Shared Burst fluid core**: The fluid corner-flow math moved to `Jobs.BurstData.BurstFluidFlowUtility`, shared by meshing and physics instead of living in two copies.
- **Shared biome selection**: Seven duplicated biome-selection copies folded into one Burst `BiomeSelection` helper (with `BiomeBlender`'s cell-hash mapping following it), gated by a golden terrain-height capture.
- **Shared audio gain**: `CategoryGain` + `AudioFade` + `EnumIndexedEntries` + `IAuthoredGain` — the mixer-group rule stopped existing in four copies.

## Tooling & Editor

- **Sound Editor window**: Audition and remap block sound clips by pack-grouped family, author ambience tracks, inspect live emitters per kind, and run a project-wide loudness audit — over a shared track drawer with play/stop auditioning and a roll preview.
- **Loudness auditing**: `AudioLoudnessAnalyzer` runs ffmpeg EBU R128 over the project, with per-role targets, effective-loudness bars and an authored-volume column; the Loudness tab writes Ambient trims back through an `AudioClipClaim` that single-sources writability. A −70 LUFS reading is treated as the meter's floor, not a measurement.
- **Audio pipeline tooling**: `convert_audio_pack.py` turns source WAV packs into engine-ready OGG families and honors a `.curated` sidecar so a re-import cannot resurrect an auditioned-out variant; automatic import profiles cover block audio, stereo/streaming ambience and mono emitters. A mixer setup tool and a sectioned `Dev/Audio` submenu round it out.
- **Block & Biome editors**: A sound-material section in the Block Editor and Tag Manager (with tag presets seeded to their blocks' material), cross-mesh variation envelopes, UW-0 and body-physics sliders, and a Biome Editor **Audio** sub-tab for ambience tracks.
- **Python tools**: `inspect_save_chunks.py` decodes region files and identifies which historical chunk layout a save uses; `rename_tokens.py` applies a dry-run-gated word-boundary rename map; `check_doc_links.py` and `check_doc_status.py` join the documentation checkers.

## Testing & Validation

- **Serialization Round-Trip** (`Minecraft Clone/Dev/Validate Serialization Round-Trip`, 16 baselines + 2 known-bug repros): round-trip identity, re-derived load state, byte-identical re-serialization, a fuzz sweep, a golden-byte format guard pinned to the on-disk chunk version, the compression round-trip/cross-load matrix, `RegionFile` sector mechanics, and pending-store round-trips.
- **Migration Chain** (`Minecraft Clone/Dev/Validate Migration Chain`, 25 baselines): the `level.dat` step chain and `MigrationManager` driven end-to-end, plus historical chunk-format coverage and the repro that caught SERIALIZATION_BUGS §10.
- **Chunk Pipeline** (`Minecraft Clone/Dev/Validate Chunk Pipeline`, 7 baselines): the real readiness gates driven through adversarial event orders, including LP-2's shared-predicate gate-term census. A harness fidelity doc records the measured prove-red map.
- **Biome Selection** (`Minecraft Clone/Dev/Validate Biome Selection`, 17 baselines): golden captures and managed-vs-Burst parity for the shared selection helper and the weighted neighborhood query.
- **Sound Engine** (`Minecraft Clone/Dev/Validate Sound Engine`, 87 baselines): the block sound resolution chain, the ambience gain chain and shipped-bed audibility census, 20 fluid-emitter baselines (count differential, palette parity through the real job, rolloff, slots, gain), the footfall tracker's edges, loudness parse/trim, and the Lava name split.
- **Underwater Render** (`Minecraft Clone/Dev/Validate Underwater Render`, 26 baselines): the fog arithmetic, the packing gate, the renderer-feature order, and the sRGB tint conversion pinned against spec literals.
- **Cloud Render** (`Minecraft Clone/Dev/Validate Cloud Render`, 6 baselines): the shader tag, the feature registration, the pass event and the opaque-copy toggle.
- **UI Band Layers** (`Minecraft Clone/Dev/Validate UI Band Layers`, 19 baselines): band-layer assignment, renderer-mask exclusion, occupancy driving the walk order and capture count, live pass-event reads, and the overlay-fallback restore.
- **Lighting Engine** grew to 113 baselines: the C14 mixed-channel mirrors closing the per-channel indexing gap (with asymmetric `TorchMixed`/`LampMixed` lamps added to the test palette), the LP-4 `LightingWork` transition census, and LP-6's lazy-gate arm rule and laziness.
- **Chunk Math** grew 56 → 72 baselines: NS-5's padded-volume, flattened-index, region-filename and legacy-V1-encoder pins, each with a per-cell or per-call oracle.
- **Physics Solver** grew to 44 baselines with B27–B44 over fluid contact, job data, jump application and step smoothing; **Meshing** grew to 61 with B64/B65 over the fluid flow UV channel, so a transposed flow vector is no longer invisible.
- **Harness hardening**: the round-trip suite's past-EOF runs now fail the disjointness walk and its pooled column sets are released; the chunk-pipeline non-vacuity floor stopped counting frontier parks; a baseline that compared two consts and could never fail now reads live pass events; and a failed guard no longer leaked a stub `World` into every later suite.
- **Validate All now runs 30 suites / 753 baselines green.**

## Bug Fixes

- **World & Storage**:
    - SERIALIZATION_BUGS #10: v1 worlds were repacked but never chunk-format-migrated → `MigrationManager`'s two region passes now run in sequence instead of exclusively. Regression dated to `752aea12`; #11–#13 were filed while reviewing the fix.
    - The restored sunlight column set was never returned to the pool → `World` releases it and pools its global copy, matching `WorldJobManager`'s ownership discipline.
    - `IsCreated` stays true after `Dispose`, so teardown guards on `JobDataManager` and `FluidVertexTemplatesNativeData` silently did nothing → both expose `IsDisposed` and `Dispose` is idempotent.
    - `FastNoiseFactory` leaked its precision setting across play sessions → the missing domain reset added (UDR0001).
- **Rendering**:
    - Clouds were invisible through a water surface → a `VoxelCloud` prepass at `AfterRenderingSkybox` (CL-9).
    - Static Batching was enabled for Standalone while URP's SRP Batcher was on → disabled in `PlayerSettings` *and* in the Profiler build profile's frozen copy (URP0302 7 → 0).
- **UI** (latent defects the band conversion exposed, all wrong at any canvas mode once the origin is not the screen):
    - `TooltipManager` and `DragAndDropHandler` assigned screen pixels straight into a world position → both map through the canvas rect, so placement survives a non-overlay canvas.
    - Two menu prefabs had scroll-area internals on layer `Default` → real UI sitting outside every renderer mask, now corrected.
- **Editor**:
    - The Block Editor's copy paths dropped 7 of 43 `BlockType` fields, so saving wrote initializers over authored fluid values → both paths use a reflective `BlockTypeCloner`.
    - `StaleAssemblyGuard` resolved DLL paths in a way 6.6 deprecates → reads them via `GetLoadedAssemblyPath` (3 UAC0007 warnings cleared).
    - `SafeArea` collided with ugui 2.6's new `UnityEngine.UI.SafeArea` → renamed to `SafeAreaFitter`.

## Refactors & Internals

- **Terminology & naming**: a Sun-to-Sky terminology sweep across code, shaders and docs; `blocklightData` → `lightData` in the flora flat-light path; American English spelling codified in `CODING_STYLE_GUIDE`.
- **Dead code removed**: `DiagonalNeighborOffsets`, `StandardBiomeAttributes.subSurfaceBlockID` (and its stale key in 6 biome assets), `ChunkLoadAnimation`'s never-assigned random-delay flag, the three UI band `GameObject` layers that sorting-layer banding made obsolete, the toast blur-suppression policy, the harness-only split schedule-clear, and 28 unreferenced audio clips.
- **New shared types**: `BurstFluidFlowUtility`, `BiomeSelection`, `NeighborReadinessDecision`, `ScheduledEdgeCheckDecision`, `INeighborGates`, `UIBlurChain`/`UIBandRegistry`/`UIBlurBand`, `FootfallTracker`, `CategoryGain`/`AudioFade`/`EnumIndexedEntries`/`IAuthoredGain`, `BlockTypeCloner`, `MusicTrack`/`AmbienceTrack`, `CrossMeshVariationSettings`.
- **Documentation lifecycle (DG-0 → DG-5)**: promoted design docs are now **deleted**, not archived, once an `OPEN_WORK_INDEX` exists to carry their open items — with an ID-index test that spares a closed arc, four checker scripts (`check_doc_refs`, `check_doc_links`, `check_doc_status`, `check_markdown_breaks`), a docs-sync closure sweep for prerequisite drift, and an `Assets/` sweep for XML docstrings citing deleted docs. `UNDERWATER_AND_SUBMERSION_RENDERING`, `TOAST_NOTIFICATION_SYSTEM` and `UI_BLUR_BANDED_COMPOSITING` were promoted to Architecture and their designs deleted.
- **Package hygiene**: `com.unity.project-auditor-rules` 1.0.3 → 2.0.0, the Rider support package to 3.1.0, and the `System.Diagnostics.DiagnosticSource` / `System.IO.Pipelines` / `System.Threading.Channels` NuGet packages to 10.0.12 (LZ4 stays pinned at 0.6.0).

## Compatibility

- **level.dat**: v15 — **unchanged** this release. The migration path itself was fixed: v1 worlds now get both the region repack *and* the chunk-format migration, where previously only one ran.
- **Chunk/region format**: v7 — **unchanged**. No new migration step was added.
- **Unity**: 6000.6.2f1 (from 6000.5.8f1), via 6000.5.9f1 → 6000.5.10f1 → 6000.6.0f1 → 6000.6.1f1 → 6000.6.2f1.
- **Settings**: eight new keys — `masterVolume` (`1.0`), `musicVolume` (`0.7`), `ambientVolume` (`1.0`), `blockVolume` (`1.0`), `fluidVolume` (`1.0`), `uiVolume` (`1.0`), `showNowPlayingToasts` (`true`) and `debugHudShowBiome` (`true`). Four keys were **removed** with the P-4/P9-2 flag retirement: `enablePipelineTimeBudgets`, `scaleBudgetCeilingsWithFpsCap`, `enableGenerationPanicGate` and `enableConvergentEdgeCheckCascade` — those paths are now unconditional. Existing settings files pick up the new keys at their defaults and ignore the removed ones.

## Previous Releases

This release also contains the changes & improvements of the previous three releases:

- **Sun Appearance** (SN-0/SN-1/SN-4) & **GS-4 Render Scale & Anti-Aliasing**
- **UI Blur Backdrop Overhaul** & **RUF-1…RUF-3 Runtime UI Factory**
- **Far-Coordinate Precision Fixes** & **Lean Production Build**
- **WS-4 Floating Origin** & **RF-1 Day/Night Cycle** & **RF-2 Procedural Skybox & Distance Fog** & **RF-3 HDR Emissive Bloom**
- **Cloud Rendering Overhaul** (CL-1 → CL-6) & **Foliage Wind Sway** (FL-1 → FL-2)
- **Voxel Occlusion Refactor** (VO-1 → VO-9) & **Silhouette Contact Shadows** (SS-1 → SS-3a)
- **P-4 Pipeline Backpressure** & **P-9 Edge-Check Cascade** & **CP-1 → CP-7 Chunk Pipeline Lifecycle Cleanup**
- **Command Console** (CMD-0 → CMD-5) & **Sub-Voxel Interaction** (VQ-2/VQ-3) & **PH-1/PH-2 Physics Solver Overhaul**
- **FP-0 → FP-7 Flight Profile Capture**
- **Unbounded Infinite World** (WS-1 → WS-3) & **World Border** (TF-14) & **OM-1 Device Calibration**
- **Shared Validation Framework** (VS-1 → VS-3) with Validate All & headless CI
- **LI-2 Banded Lighting Gather** & **Lighting Bug Fixes** (Bugs 05, 13–18)
- **Player Placement Overhaul** & **Block Database Decoupling**

## What's Changed

* feat/world-scaling by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/12
* feat/fluid-physics by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/14
* feat/code-clean-up-01 by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/15
* feat/ui-blur-rework by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/16
* chore/project-auditor-analysis-1 by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/17

**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-08-17...2026-09-22
