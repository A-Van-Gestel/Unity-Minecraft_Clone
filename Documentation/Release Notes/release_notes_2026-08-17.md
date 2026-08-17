**Build**: `2026-08-17 - RC 90  World Scaling (RF-1 + RF-2 + RF-3 + GS-4 + Lean prod + Floating point precision fixes)` · **Range**: `2026-08-13` → `2026-08-17` · **Commits**: 125 · **Unity**: 6000.5.8f1 · **level.dat**: v15

WIP Release turning the sun into a body seen through air (**SN-0/SN-1/SN-4**: aureole, per-channel extinction and shader-side glare), adding **Render Scale + MSAA** graphics settings (**GS-4**), overhauling the **UI Blur Backdrop** so panels stack and tint correctly, and closing the last **far-coordinate precision** holes in foliage sway, liquid noise and voxel queries. It also lands a **Lean Production Build** configuration that halves IL2CPP build time at no runtime cost.

## Highlights

- **Sun Appearance (SN-0 / SN-1 / SN-4)**: The sun now has a forward-scattered aureole, reddens as it sets, and casts a shader-side glare — all produced in the skybox, never by post-process bloom.
- **GS-4: Render Scale & Anti-Aliasing**: Two new graphics settings — a 30–200% render scale and 2x/4x/8x MSAA — with main-light shadow variants stripped from the build.
- **UI Blur Backdrop Overhaul**: Blurred UI panels honor vertex color and clipping, stack on top of each other, and no longer fight the bloom pass for render targets.
- **RUF-1…RUF-3: Runtime UI Factory**: One shared factory for code-built UI, now backing both the benchmark HUD and the command console.
- **Far-Coordinate Precision Fixes**: Foliage sway, liquid surface noise, fluid wake routing and voxel state reads all stay correct out to the ±2³¹ world border.
- **Lean Production Build**: MethodOnly IL2CPP stack traces, Medium managed stripping and an 8.9 MB → 90 KB `Resources/` diet — **build time −49% (~15m00s → 7m42s)**, runtime neutral.

## Gameplay & Visuals

- **Sun Appearance (SN-0 → SN-4)**: The sun is rendered as a light source rather than a flat disc:
    - SN-0: Angular aureole — a forward-scattered glow blended over both the sky and the disc.
    - SN-1: Per-channel extinction, with the aureole tint derived from the transmitted sunlight, so the sun reddens through sunset.
    - SN-4: Shader-side glare as a third, tightest lobe on SN-0's falloff, plus an airmass falloff for the sun's own optical depth so a high sun stops reading orange.
    - The sun's extinction deliberately ignores the `Distance Fog` setting while the moon honors it; both behaviors are now pinned by a baseline.
    - SN-2 (HDR core for post-process bloom) and SN-3 (screen-space lens flare) were built, judged in game and **reverted in full** — URP's single global `Bloom` override cannot serve both the sun and RF-3's block emitters.
- **GS-4: Render Scale & Anti-Aliasing**: New `Render Scale` (30–200%, `renderScalePercent`) and `Anti-Aliasing (MSAA)` (`Off`/2x/4x/8x, `MsaaLevel`) graphics settings, wired through `GraphicsSettingsController` and the URP asset. Main-light shadow variants are stripped from the build, since the engine's lighting is fully voxel-driven. Centroid interpolation in `VoxelV2F` keeps MSAA from drawing wrong-block seams along silhouette edges.
- **UI Blur Backdrop Overhaul**: `MaskedUIBlur` now honors UI vertex color and rect clipping, so blurred panels tint correctly and stack on one another instead of rendering opaque (UI_BUGS #06). Documented end-to-end in the new `UI_BLUR_BACKDROP_SYSTEM` architecture doc.
- **Escape key precedence**: Escape now closes the creative inventory first — the pause menu takes a second press.
- **Default view distance raised 5 → 10**, with the generation panic gate's reference resident width following it to 27.
- **Credits**: New `Reference` and `Audio` credit categories, with the IQ domain-warping and GPU Gems 3 density-field articles credited in `CreditsDatabase` and the markdown mirror. Several existing credits corrected — the bark texture's chosen license (CC-BY-SA 3.0) and real source, `015-oak_log_top`'s true attribution, and broken asset paths for oak leaves, grass blades and Fira Code. An evaluated-but-unused Tree Bark credit was removed.

## Engine & Performance

- **Lean Production Build**: Three build-configuration changes shipped together, measured as a bundle:
    - `il2cppStacktraceInformation` `MethodFileLineNumber` → `MethodOnly` for Standalone and Android players.
    - Managed stripping raised `Low` → `Medium`, with new `link.xml` roots for the reflection-driven settings UI and `[Preserve]` on `ResolutionDropdownProvider`.
    - 38 atlas source tiles + `AtlasConfiguration.asset` moved out of `Assets/Resources/` (8.9 MB → 90 KB) so they stop shipping in the player.
    - IL2CPP Master A/B verdict: **GO on build time (−49%, ~15m00s → 7m42s) and build size, neutral on runtime** (~1% frame-time deltas with mixed signs, n = 1 per leg). Full capture in `BUILD_LEAN_PROD_IL2CPP_2026-08-15_BENCHMARK.md`.
- **Shader-side savings**: `sunTransmittance` is computed once and reused for the sun disc, and the liquid shore mask moved to a flat pass.
- **Shader target sweep**: Every project-owned shader now declares an explicit `#pragma target 3.5` — the project floor — codified in the new `SHADER_CONVENTIONS` guide.
- **Build provenance stamp**: `BuildStamp` + `BuildStampBaker` bake build identity into the player at build time, so benchmark reports state facts rather than editor-only guesses. The git provenance subprocess is bounded, the stamp is excluded from its own dirty check, and an unbaked stamp no longer reports its Burst flags as fact.
- **In-world micro-benchmarks are now gated**: A default-off `enableInWorldMicroBenchmarks` setting arms the chunk-generation, mesh-generation and lighting-job harnesses via `MicroBenchmarkGate`, so a production build ships them inert regardless of what the scene has serialized. Forced off under automated mode so captures cannot be corrupted mid-run.

## Testing & Validation

- **UI Blur Render** (5 baselines): A new suite (`Minecraft Clone/Dev/Validate All` registry entry "UI Blur Render") rendering real quads through `UIBlurQuadRenderer` — the blur sample reaching the panel unchanged, backdrop survival, vertex-color tinting and rect clipping.
- **Chunk Math** grew 47 → 56 baselines: foliage wave phase at far origins and over long sessions, liquid-noise period and origin handling, shift-mask coverage, and a far-coordinate wake-routing teeth baseline for Fluid Bug 17.
- **Sky Render** grew 7 → 11 baselines: B8 the sun aureole, B9 the sun's reddening plus an achromatic fixture and a high-sun neutrality check, B10 the glare falloff, B11 the sun/moon fog asymmetry. B4 was repaired — it had been passing a visible regression.
- **Behavior** grew 16 → 17 baselines (BH-B12) and **Placement** 28 → 29: paired near/far coordinate scenarios, prove-red on both, with in-fixture non-vacuity legs (grass inactive with the neighbor absent; an empty far cell still placeable) and far anchors derived from a chunk index so alignment cannot silently break.
- **Shared `ExactValue` helper**: The suites' bit-exact assertions now route through one helper instead of per-suite comparisons.
- **Validate All now runs 22 suites / 497 baselines green.**

## Bug Fixes

- **Far-coordinate precision**:
    - Foliage sway degraded with distance from the world center → wave phase reduced through the new `FoliagePhase` helper; `_WorldOriginOffset`/`_Time.y` retired from the sway shader.
    - Foliage sway degraded over session length → phase reduced mod 2π on the CPU, guarded by a long-session baseline and a hardened oracle.
    - FLUID #20: Liquid surface noise degraded with distance → `LiquidNoiseOrigin` exploits the fact that the Ashima simplex lattice is *already* periodic every 867 units, so the lattice is never wrapped. Evidence committed as `Tools/Python/verify_liquid_noise_period.py`.
    - FLUID #17: Fluid wake routing lost precision at far coordinates → `GetChunkFromVector3` and `GetVoxelPositionInChunkFromGlobalVector3` retyped to `Vector3Int` with no float overload retained. In-game confirmed at the 32-bit border.
    - BLOCK_BEHAVIOR #05: Residual float voxel reads → `ChunkData.GetState` routes cross-chunk through the integer `TryGetVoxel`; `IsCellOccupiedForPlacement` and `World.GetVoxelState` retyped to `Vector3Int`. In-game confirmed near the 32-bit world border.
    - Collision-bounds visualizer queried voxel space with a Unity-space position → the neighbor lookup now derives from the chunk's voxel origin. A WS-4 coordinate-space bug, wrong at any magnitude once the origin shifts.
- **Rendering**:
    - Bloom forced the post-processing stack on in Volume-less scenes → `ApplyBloom`'s camera write is gated on a Volume.
    - Liquid shaders declared `target 3.0` while `LiquidV2F` uses 11 interpolators → raised to 3.5.
- **UI**:
    - UI blur published a pooled render-graph texture that bloom then reclaimed → per-camera `UIBlurHistory` target.
    - UI_BUGS #06: `MaskedUIBlur` panels were opaque and could not stack → vertex color + clipping honored, pinned by the new UI Blur Render suite.
    - The benchmark HUD and results screen leaked blur material instances → both now own and destroy their instances.

## Refactors & Internals

- **RUF-1…RUF-3: `RuntimeUIFactory`** extracted from `BenchmarkUIBuilder` and reused by the command console — one implementation of the code-built UI primitives (canvas, panel, TMP text, button, scrollable text area) and one place that knows the blur-material contract. The palette stays per-screen by design.
- **Dead float voxel-query APIs removed**: `World.CheckForVoxel(Vector3)`, `WorldData.QueueLightUpdate(Vector3)` and `WorldData.GetVoxelState(Vector3)` are gone, making `TryGetVoxel` the sole voxel resolution entry point so the implicit `Vector3Int` → `Vector3` conversion cannot return.
- New helpers: `FoliagePhase`, `LiquidNoiseOrigin`, `MicroBenchmarkGate`, `BuildStamp`/`BuildStampBaker`, `UIBlurHistory`, `MsaaLevel`, and the validation framework's `ExactValue`.
- **Documentation sweep**: `DATA_STRUCTURES`, `SUB_CHUNK_MESHING_ARCHITECTURE` and `MODULAR_WORLD_GENERATION` rewritten against current code; new `UI_BLUR_BACKDROP_SYSTEM`, `RUNTIME_UI_FACTORY` and `SHADER_CONVENTIONS` docs; a bug-doc staleness audit archiving 6 already-fixed entries and correcting 4 open ones; and `Tools/Python/check_doc_refs.py` to keep documentation cross-references resolving.

## Compatibility

- **level.dat**: v15 — **unchanged** this release.
- **Chunk/region format**: **unchanged**. No migration step was added; nothing under `Assets/Scripts/Serialization/` changed.
- **Unity**: 6000.5.8f1 — **unchanged** from the previous release.
- **Settings**: three new keys — `renderScalePercent` (default `100`), `msaa` (default `Off`), `enableInWorldMicroBenchmarks` (default `false`). One default changed: `viewDistance` `5` → `10`. Existing settings files pick up the new keys at their defaults.

## Previous Releases

This release also contains the changes & improvements of the previous three releases:

- **WS-4 Floating Origin** & **RF-1 Day/Night Cycle** & **RF-2 Procedural Skybox & Distance Fog** & **RF-3 HDR Emissive Bloom**
- **Cloud Rendering Overhaul** (CL-1 → CL-6) & **Foliage Wind Sway** (FL-1 → FL-2)
- **Voxel Occlusion Refactor** (VO-1 → VO-9) & **Silhouette Contact Shadows** (SS-1 → SS-3a)
- **P-4 Pipeline Backpressure** & **P-9 Edge-Check Cascade** & **CP-1 → CP-7 Chunk Pipeline Lifecycle Cleanup**
- **Command Console** (CMD-0 → CMD-5) & **Sub-Voxel Interaction** (VQ-2/VQ-3) & **PH-1/PH-2 Physics Solver Overhaul**
- **FP-0 → FP-7 Flight Profile Capture** & the validation framework's growth to 21 suites / 477 baselines
- **Unbounded Infinite World** (WS-1 → WS-3) & **World Border** (TF-14) & **OM-1 Device Calibration**
- **Shared Validation Framework** (VS-1 → VS-3) with Validate All & headless CI
- **LI-2 Banded Lighting Gather** & **Lighting Bug Fixes** (Bugs 05, 13–18)
- **Full RGB Smooth Lighting Engine** & **Lighting Bug Fixes** (Bugs 06–12)
- **Lighting, Meshing & Behavior-Tick Validation Suites**
- **TG-4 Full Fluid Burst Port** (Phases 0–4b) & **Player Placement Overhaul** & **Block Database Decoupling**

## What's Changed

* docs/doc-sync + credit updates by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/13

**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-08-13...2026-08-17
