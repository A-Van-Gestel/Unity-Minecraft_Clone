WIP Release introducing a **Floating Origin** system (WS-4, correct rendering to the ±2³¹ world border), a **Day/Night Cycle** with **Procedural Skybox** (sun, moon phases, star field & distance fog), **Cloud Rendering Overhaul** (wind drift, procedural noise, face shading & dual-layer support), **Foliage Wind Sway** (coherent traveling-wave animation for cross-mesh and leaf blocks), a **Voxel Occlusion Refactor** (per-face partial-block lighting, AO & contact shadows), **Pipeline Backpressure** (P-4, FPS 13→29 under fill load), a **Command Console**
with tab-autocomplete and relative `~` coordinates, **Sub-Voxel Interaction** (VQ-2/VQ-3 exact ray traversal & narrow-phase targeting), **HDR Emissive Bloom** (RF-3), a **Flight Profile Capture** instrument (FP-0→FP-7), and a **Physics Solver** rewrite (PH-1/PH-2 gather-once sweeps).

This release includes the following major new features and improvements:

- **WS-4: Floating Origin**: The world re-anchors its Unity-space origin to the player's chunk, eliminating floating-point jitter and rendering artifacts at large coordinates:
    - WS-4a: `WorldOrigin` conversion helpers + shader `_WorldOriginOffset` global, all Voxel↔Unity presentation and query sites routed through origin-aware paths, guarded by 6 non-zero-origin Chunk Math baselines.
    - WS-4b: Player position saves in voxel space, origin anchored at the spawn chokepoint, runtime origin-shift trigger + translate loop with latched bounded-position assertion (dev builds), `ChunkPosition` renamed `UnityPosition` with voxel-space readers de-spaced.
    - WS-4c: Player position stored chunk-relative (**level.dat v12 → v13**) so a far save resumes exactly.
    - SP-1: `SpawnResolution` policy unit routing `World.StartWorld`'s three spawn paths, with a 10-baseline Spawn validation suite.
    - Render-distance-scaled cloud coverage (max (2× viewDistance, 8) chunks) with pooled 64-block tiles and shared per-pattern-tile meshes.
    - Bug 19: Far-lands lighting crash → integer sunlight-column routing (`SunlightColumnRouting` + `WorldData`/`ChunkCoord` `Vector3Int` overloads auto-capturing 11 int call sites, latched ±2²⁴ float tripwire). In-game confirmed incl. ±2³¹ edge.
    - V2 noise rider: FNL `Precise64` double-coordinate pipeline + "Far Lands (Classic Noise)" world setting → generation artifact-free past ±2²⁴, classic float path preserved bit-identically (golden-proven), with a 5-baseline FNL validation suite.
    - Worm-carver far-coordinate precision → cell-local simulation frame (Precise64-gated, Classic32 bit-identical) → worm caves generate correctly to the ±2³¹ border, guarded by a 6-baseline Worm Carver validation suite.
- **RF-1: Day/Night Cycle**: World clock with MC-anchored `/time` command, subtractive sky-darkening model (render and gameplay agree), and an effective-light query layer for gameplay reads:
    - World time persisted in **level.dat v15** (`worldState.time`).
    - `/wind` command and wind persistence in a new level.dat environment section (**v14**).
    - 10-baseline World Clock validation suite.
- **RF-2: Procedural Skybox & Distance Fog**: Celestial body model driving sun arcs, 8-phase moon, and a rotating star field — with a curved distance fog that conceals the chunk boundary:
    - Horizon haze on celestial discs, richer sun/moon disc rendering, edit-mode sky preview renderer.
    - Sky Editor window for authoring sky colors against a live render, with moon-phase browsing and a rendered-pixel validation suite.
    - Distance Fog graphics setting (Off / Light / Full).
    - 15-baseline Sky & Celestial validation suite + 7-baseline Sky Render validation suite.
- **RF-3: HDR Emissive Bloom**: Per-vertex emissive strength in vertex-color alpha, with post-processing bloom gated by a Graphics setting. Lava glows via the liquid shader's emissive channel read.
- **Cloud Rendering Overhaul** (CL-1 → CL-6): Clouds transformed from static tiles into a dynamic, multi-layer atmospheric system:
    - CL-1: Wind drift on cloud-space tiles (drift-carrying root, pattern-period wrap, exact int anchor via `VoxelToUnity(Vector3Int)`).
    - CL-2: MC-style face shading (Fancy-only), `SkyLightColor` day/night tint, coverage-edge fade via the cloud shader.
    - CL-3: Procedural cloud pattern — seeded periodic FBM value noise (Burst `CloudPatternJob`, coverage-percentile threshold matching `clouds.png` density) with classic-texture fallback toggle.
    - CL-6: Second high-altitude cloud layer — `Clouds` generalized to a per-layer config array (height, drift multiplier+veer, opacity, style, noise knobs, seed salt). Defaults: main 100 + upper 170 (×1.5 drift veered 15°, 60% opacity).
- **Foliage Wind Sway** (FL-1 → FL-2): Cross-mesh and leaf-block vegetation now sways in the wind with a coherent traveling-wave animation:
    - FL-1: Mesh-side sway channels (uv.zw weight + voxel-space `VoxelHash01` phase, re-anchor-safe) + `ApplyFoliageSway` shader (transparent only) + `FoliageSway` component + Foliage Sway graphics toggle. Meshing suite B22.
    - FL-2: Per-block `swayStrength` (BlockEditor slider) written to cube uv.zw via a per-voxel post-pass + coherent traveling-wave phase (distance-along-wind through voxel-space XZ, gust wave, wave² vertical settle). Meshing suite B23.
- **Voxel Occlusion Refactor** (VO-1 → VO-9, SS-1 → SS-3a): Partial blocks now cast and receive light, AO, and contact shadows correctly:
    - VO-1: Shared rotation-to-AABB core between collision and a new Burst per-face occlusion utility.
    - VO-3: Light occlusion per-face for partial blocks + vertical sky-column rule per-face (Bug 20: daylight through a vertical slab → fixed, in-game confirmed).
    - VO-4: Cross-chunk sunlight/blocklight support and veto made directional for partial blocks (B106).
    - Bug 21: Sky-column heightmap test and recalculation trigger made shape-aware for partial blocks (B107).
    - VO-5: Ambient occlusion weighted by partial-block face coverage (B41–B43).
    - VO-6: Custom-mesh face light sampled from the cell the face faces, closing Bug M01.
    - VO-8: Per-corner AO occlusion, fixing recessed blocks shadowing their own faces (Bug M03).
    - Bug M02: Custom-mesh faces culled by the cell they face (B48).
    - VO-9a/b: AO occlusion queries generalized to arbitrary sample points + face subdivision for partial occluder reach (B49).
    - SS-1 → SS-3a: Face-silhouette primitive, silhouette distance field, localized AO corner seal, full-cube occluder contact shadows + directional binning — shipped as a Full Block Contact Shadows graphics setting (default off).
- **P-4: Pipeline Backpressure**: Rate-quota + ms-ceiling pass budgets for lighting/mesh scheduling, a generation panic gate with hysteresis, and FPS-cap-proportional ceilings:
    - Measured: fill-load FPS **13.3 → 29.1**, hitch frames **67% → 11%**.
    - IL2CPP A/B GO (final): legacy path never drains post-relocation (300 s timeout); ON path fills in 15.5 s / 2.2% hitches / 78 ms worst frame at 2209 chunks.
    - FPS-cap ceiling scaling: 30-cap fill ×1.82, 15-cap ×1.32, zero frame-health cost.
    - Performance-tab budget sliders (0.5 ms floors) + 2 rollback flags + `PipelinePassBudget` + `GenerationPanicGate`.
    - New Validate Pipeline Backpressure suite (22 baselines), prove-red confirmed.
- **P-9: Edge-Check Cascade Optimization**: Convergent edge-check cascade eliminates redundant light recomputation:
    - P9-0: Per-pass attribution instruments (`PipelineTelemetry`, `TraceStatistics`, `PipelineRegimeVerdict`).
    - P9-1: Five-run IL2CPP attribution capture confirming rate identity within 4%, mesh amplification exactly 1.00 (voiding Option B2's mesh premise), and 82% of latency as admission wait.
    - P9-2: `EdgeCheckCascadeDecision` propagates only when `ApplyJobLightMap`'s change signal says the pass moved light → amplification **6.12 → 1.86** per delivered chunk, Quota frames **94% → 8%**.
    - Lighting suite B97–B100.
- **Command Console** (CMD-0 → CMD-5): Full in-game command system with a three-layer engine/UI/command split:
    - CMD-3: 13 commands shipped (`/teleport`, `/give`, `/setblock`, `/time`, `/wind`, `/set-world-border`, `/setspawn`, etc.) with a 56-baseline Command Console validation suite.
    - CMD-4: Relative `~` coordinates (`~/~N/~-N` integer offsets) live on `/teleport` and `/setblock`.
    - CMD-5: Tab autocomplete + inline ghost text (`IArgumentCompleter` for give/setblock/set-world-border, Tab/RightArrow/End accept).
- **VQ-2: Exact Placement Ray March**: Amanatides-Woo DDA traversal (`VoxelRayDDA`) — no cell skipped, entered face is the stepped axis. `FaceNormal` heuristic + `checkIncrement` knob retired.
- **VQ-3: Sub-Voxel Narrow Phase**: A half-slab now stops the placement ray only where its volume is, and the reported face is the block's, not the cell's.
- **FP-0 → FP-7: Flight Profile Capture**: Full pipeline-profiling instrument — per-phase trace telemetry, statistics, regime verdict, settings snapshot, and integrity banners:
    - FP-4 capture: the 15+ m/s sluggish-chunk symptom is **ordering-bound** at every view distance (waste 22.9–61.2% in all 9 loading phases), closing the FP diagnostic arc.
    - FP-5 → FP-7: Run-boundary phase-leak fix, pipeline-settings snapshot (18 values), generation-process quota stop fix, trace-disposition + verdict-rule measurement defect corrections, and report integrity self-checks.
- **CP-1 → CP-7: Chunk Pipeline Lifecycle Cleanup**: Lifecycle observability probes, configurable debug screen, pool sizing with per-pool soft caps + 90 s linger, placeholder consolidation, constants unification (`ChunkMath.CHUNK_WIDTH/CHUNK_HEIGHT/SECTIONS_PER_CHUNK`), and region-codec V1/V2 pins (Chunk Math suite 26 → 47).
    - CP-4: `WorldData.GetOrCreatePlaceholder` single creation site (retire dead `LoadChunk`/`EnsureChunkExists`).
    - CP-5: `ChunkUnloadDecision` extraction + persist-and-unload the light-pending trail (`UnloadPersistLightPending`), with a 9-baseline Chunk Unload Decision validation suite.
    - CP-6: Save-on-unload durability (`ChunkSaveResult` contract + failed-save retry registry + reload guard + quit flush), with a 13-baseline Save Durability validation suite.
    - CP-7 F4: Pool prune decision with service-area hard caps → measured teleport churn **675 → 0**. New Validate Pool Prune Decision suite (B1–B5).
- **PH-1/PH-2: Physics Solver Overhaul**: Gather-once collision sweeps — `ResolveMovement` resolves its voxel neighborhood once per substep into a `PhysicsCellBuffer` and answers all nine sweeps from it:
    - PH-1: 0 mismatches over 142 sweeps (shadow-pass verified), step-height envelope guarded by B25.
    - PH-2: `CalculateVelocity` no longer writes the transform on each substep (5,846 substepped ticks at exact float equality, 0 mismatches), guarded by B26.
    - New Validate Physics Solver suite (B1–B26), with NS-4 multi-cell horizontal contacts (B24).
- **TESTING: Validation Framework Growth (8 → 21 suites / 197 → 477 baselines)**: Thirteen new suites joined the registry:
    - **Command Console** (56 baselines): command dispatch, argument parsing, relative `~` coordinates, tab autocomplete.
    - **Physics Solver** (26 baselines): collision bounds, gather-once sweeps, step-up, grounded verdict, velocity staging.
    - **Pipeline Backpressure** (22 baselines): pass budgets, panic gate, telemetry statistics, regime verdict, settings snapshot, cascade decision.
    - **Sky & Celestial** (15 baselines): celestial body model, gradient crossings, fog levels.
    - **Save Durability** (13 baselines): snapshot staging, retry registry, reload guard, flush on quit.
    - **Spawn** (10 baselines): spawn resolution policy and null-probe rejection.
    - **World Clock** (10 baselines): day wrap, monotone sky darkening, moonlight floor, `/time` mapping, effective light.
    - **Chunk Unload Decision** (9 baselines): precedence matrix, in-range strand narrowing, persist-light-pending trail drain.
    - **Deserialization Robustness** (9 baselines): corrupt LZ4 frame handling, truncated stream recovery, region header bounds.
    - **Sky Render** (7 baselines): color round-trip, moon disc star occlusion, zenith moon detail, gradient orientation, airlight sampling.
    - **Worm Carver** (6 baselines): far liveness, classic collapse, in-band parity, cross-chunk determinism, Classic32 golden.
    - **Voxel Occlusion** (6 baselines): face-silhouette primitive, contact-shadow distance field, orientation volume equivalence.
    - **Pool Prune Decision** (5 baselines): soft/hard caps, linger expiry, demand signal.
    - Standalone tests: **FNL / Noise** (5 baselines) in `FastNoiseLiteTests`.
    - **Lighting suite** grew 86 → 99 baselines (up to B107: far-anchored sunlight routing, edge-check cascade, partial-block directional occlusion, heightmap shape-awareness).
    - **Meshing suite** grew 21 → 57 baselines (up to B61: foliage sway, custom-mesh occlusion, AO coverage, sub-cell tessellation, silhouette contact shadows, HDR emissive).
    - **Chunk Math suite** grew 26 → 47 baselines (origin, region-codec V1/V2, constants coupling).
    - **Placement suite** grew 13 → 28 baselines (border edit gate, oblique DDA rays, sub-voxel narrow phase, floating origin).
    - **Validate All now runs 21 suites / 477 baselines green.**
- **Lighting Bug Fixes** (Bugs 19–21):
    - Bug 19: Far-lands lighting crash → integer sunlight-column routing.
    - Bug 20: Daylight through a vertical slab → per-face light occlusion for partial blocks (VO-3).
    - Bug 21: Heightmap test not shape-aware for partial blocks → shape-aware recalculation trigger.
- **Meshing Bug Fixes** (Bugs M01–M03):
    - Bug M01: Sub-block smooth-light sampling → custom-mesh face light sampled from the cell the face faces (VO-6).
    - Bug M02: Custom-mesh faces not culled by the cell they face → per-face cell culling.
    - Bug M03: Recessed blocks shadowing their own faces → per-corner AO occlusion (VO-8).
- **Bug Fixes**:
    - Clouds full-bright at night → brightness now via the shared `VoxelLightToShadow` curve at `sunLuminance=1` normalized to noon.
    - Console UI self-heals destroyed panel/children + stale-InUI recovery (UI_BUGS #04).
    - Border clamp bounds integer-first resolution → no large-float cancellation at extreme radii.
    - DebugScreen voxel readout via `UnityToVoxelCell` → float origin-add drifted past ±2²⁴.
    - `LevelDatCodec` in-memory level.dat normalization → pre-v13 menu reads fixed.
    - `FaceIndexOfDirection` rejects non-face steps instead of silently answering +X.
    - RF-3 emissive alpha seeded to 255, boosting every non-emitting block.
    - RF-9: Vertex AO crushes to black at night → occlusion baked after sky darkening.
    - Sky gradients keyed dawn on the celestial horizon crossing instead of the named sunrise tick.
    - Sky Editor preview re-rendered on every editor tick.
    - Play-mode teardown left the scene's ambient mode pinned to Flat.
    - Out-of-range Y in the effective-light query threw instead of reporting no light.
    - `DuplicateSelectedBlock` dropping `infiniteSourceRegeneration`/`spreadChance`.
    - Standard-cube faces gathered their shading neighborhood twice → gather once.
    - P-4 FPS-cap-proportional ceiling panic-gate 3-frame close debounce, `ComputeQuota` cap overflow clamp, `SanitizeBudgetMs` 0.5 ms floor.
    - FP-5: Run-boundary phase leak → `PipelineTelemetry.BeginRun()`.
    - FP-7b: Generation-process quota stop reported as `OutOfWork` → routes through shared `ClassifyStop`.
- **Refactors**: Retired TG-4 flag-gated fluid-tick fallbacks (parallel Y-band halo tick unconditional, 4 flags + serial/interior-hybrid paths dropped). Extracted `SpawnResolution`, `ChunkUnloadDecision`, `PipelinePassBudget`, `GenerationPanicGate`, `PipelineTelemetry`/`TraceStatistics`/`PipelineRegimeVerdict`/`PipelineSettingsSnapshot`, `PhysicsCollisionCells`/`PhysicsCellBuffer`, `BlockCollisionBoundsUtility`, `VoxelRayDDA`, `EdgeCheckCascadeDecision`, `PoolPruneDecision`, `LevelDatCodec`, `SunlightColumnRouting`, `WorldData.GetOrCreatePlaceholder`
  (retired dead `LoadChunk`/`EnsureChunkExists`), `ChunkMath.CHUNK_WIDTH/CHUNK_HEIGHT/SECTIONS_PER_CHUNK` constant aliases, and `FoliageSway`/`CloudPatternJob` helpers. Impure-struct-copy sweep (30 warnings → 0 codebase-wide). Dead `World.CompleteAndProcessMeshJobs` removed. Froze level.dat migration DTO. `WorldData.Chunks` encapsulated via `Set`/`Remove`/`ClearChunk`.
- **Git Hygiene**: `.gitattributes` expanded with C#/Markdown diff drivers, binary safety net, Unity YAML merge declarations, LF line-ending pinning, and Markdown blank-at-eol exemption. `check_markdown_breaks.py` validation tool.
- **Unity Upgrade**: Updated to Unity 6000.5.8f1 (from 6000.5.4f1) via 6000.5.5f1 → 6000.5.6f1 → 6000.5.7f1 → 6000.5.8f1.

This release also contains the changes & improvements of the previous releases:

- **Unbounded Infinite World** (WS-1 → WS-3) & **World Border** (TF-14) & **OM-1 Device Calibration**
- **Shared Validation Framework** (VS-1 → VS-3) with Validate All & headless CI
- **LI-2 Banded Lighting Gather** & **Lighting Bug Fixes** (Bugs 05, 13–18)
- **Player Placement Overhaul** & **Block Database Decoupling**
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

* feat/world-scaling by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/12

**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-07-15...2026-08-13
