WIP Release introducing a **Full RGB Smooth Lighting Engine**, a comprehensive **Lighting Validation Suite**, a **Meshing Validation Suite**, a **Behavior-Tick Validation Suite**, a complete **Fluid Burst Port (TG-4)** with parallel scheduling, and a sweeping set of **Performance Optimizations** across lighting, meshing, and tick systems.

This release includes the following major new features and improvements:

- **Full RGB Smooth Lighting Engine**: Complete overhaul of the lighting and smooth-lighting systems to support per-channel RGB color:
    - RGB-aware Lighting Engine with per-channel BFS propagation, replacing the legacy single-channel blocklight.
    - RGB-aware Smooth Lighting system for Standard, Custom, Fluid, and Cross meshes with sub-voxel precision.
    - Legacy light-bit removal (Phase B): migrated all light reads/writes from packed VoxelState bits to the dedicated `LightData` array, stripped the dual-write system and `PackVoxelData` API.
    - Section Flag Redesign (Chunk Format v7 + migration) and uniform-skylight memory optimization for empty sky & deep underground sections.
    - RGB lighting-aware fluids and per-block emission color support (15-level RGB controls + falloff preview widget in the Block Editor).
    - Tiered Smooth Lighting quality setting overhaul.
    - RGB block lighting persistence across save/load (Chunk Format v9).
- **Lighting Bug Fixes** (Bugs 06–12): Systematic fix campaign driven by the new validation suite:
    - Bug 06: Generated emissives never seeded blocklight BFS → `SyncEmissionToLightArray` enqueues stamped positions for placement BFS.
    - Bug 07: Cross-chunk emissive cut-off/flicker → per-channel seeding force-clear + wake-up uplift semantics in `CrossChunkLightModApplier` + cross-border darkness re-spread pull.
    - Bug 08: Ghost blocklight → deferred cross-chunk mods for in-flight targets (drained after merge) + full blocklight mods persisted to new `pending_blocklight.bin` for unloaded chunks.
    - Bug 09: Blocklight leak into opaque volumes → opaque-source guard in `PropagateLightRGB` (exempts emissive opaque lamps).
    - Bug 10: Cross-chunk opaque-border light leak → `CheckEdgeVoxel`/RGB now guards neighbor opacity.
    - Bug 11: Cross-seam sunlight removal/re-placement oscillation on load → in-chunk support veto in `ComputeSunlight` with opacity-aware attenuation.
    - Bug 12: Over-bright cross-seam sunlight loop survives source removal → cross-seam removal initiator emission (Bug-11 veto adjudicates).
    - Additional fixes: pending blocklight last-write-wins, `DegradeDeferredCrossChunkMods` bounds check, `BlocklightBfsQueue` seeding context, 0xFFFF light sentinel collision, and shared `LightAttenuation.Attenuate` dedup.
- **Persistent World Spawn Point**: New `ChunkRelativePosition` struct for a persistent `WorldSpawnPoint` surviving chunk load/unload cycles.
- **Android Support**: Initial Android support with basic touch controls, toolbar & inventory touch input (migrated to `IPointerDownHandler`/`IPointerUpHandler`/`IPointerClickHandler` interfaces), and AndroidManifest fix.
- **TESTING: Full Lighting Validation Suite**: Editor validation framework (`Minecraft Clone/Dev/Validate Lighting Engine`) exercising the real `NeighborhoodLightingJob` per chunk against a borderless `LightingOracle` reference solver:
    - 3 subsystems: LightingTestWorld harness, LightingFrameSimulator (frame-tick orchestration with budget throttling, completion-order control, and seeded iteration randomness), and SectionRendererTestFixture.
    - 55 baselines covering: core lighting (B1–B8), bug-specific guards (B9–B14, B15–B16 consolidated Bug-09 fleet, B17–B29 frame-simulator coverage), persist/replay round-trip (B30–B32), pool-recycle/flag-reset (B33–B34), oracle-independence probes (B35–B39), geometry fuzz (B40), neighbor-readiness deferral (B41), dense-canopy cross-chunk (B42–B44), multi-layer attenuation (B45), sunlight persist/replay (B46–B47), sunlight darkening race (B54–B55), and bug-specific baselines (B48–B53).
    - Nightly 2000-seed geometry fuzz sweep.
    - Harness fidelity closed: A1 (section compaction), A3 (heightmap maintenance), A4 (oracle independence), B1 (persist/replay), B2 (neighbor readiness), B4 (pool recycle), C1–C5 (geometry fuzz, sunlight, multi-layer attenuation).
- **TESTING: Full Meshing Validation Suite**: Editor validation framework (`Minecraft Clone/Dev/Validate Meshing`) with 21 baselines:
    - 6 subsystems: MeshingTestWorld, MeshOracle (UV + smooth-lighting value oracles), MeshAssert, SectionRendererTestFixture, FluidMeshData template helpers, and StructuralInvariants.
    - Baselines covering: standard cube output (B1–B4), rotation Euler hoist (MR-1), fluid meshing routing/determinism (B7–B8), SectionStats tiling (B9), MeshPostProcessJob chaining (B10), smooth-lighting uniform corners (B11), SectionRenderer material-combo/deactivation/bounds (B12–B14), material-combo cache + constant bounds (B15–B16), mesh-output pool stale-reuse (B17), and cross-chunk border-face culling (B18–B21).
- **TESTING: Behavior-Tick Validation Suite**: Editor validation framework (`Minecraft Clone/Dev/Validate Behavior Ticks`) with 13 baselines:
    - 2 subsystems: BehaviorTestWorld and a differential harness driver (BH-D1).
    - Baselines covering: water flow/gravity/waterfall (BH-B1–B4), lava viscosity staggering (BH-B5), grass spread/decay (BH-B6–B7), old-vs-new differential (BH-D1, 11 scenarios), and multi-chunk cross-chunk fluid fixtures.
- **LI-1: Halo-Padded Lighting BFS**: Folded the lighting job's 9 neighbor maps + write-through cache into one 20³ halo-padded volume with branch-free BFS, bulk-row `NativeArray.Copy` gather, and generic `GatherPadded<T>` unification. 2.4–3× isolated BFS speedup (benchmark-confirmed, gather-bound full-timing not a net win standalone — folded into P-2).
- **P-2: Worker-Thread Lighting Gather** (Layer 1): Moved the LI-1 halo-volume gather from the main-thread `PrepareJob` into `NeighborhoodLightingJob.Execute()` on the worker thread — benchmark net-positive, frees main-thread frame budget.
- **MR-1: Standard-Cube Rotation Euler Hoist**: Replaced per-vertex Euler rotation with a precomputed `float3x3` fast path. Output guarded bit-for-bit by meshing baselines B1/B4.
- **MR-2: Packed Vertex Format**: `half4` UV + `Color32` + SNorm8 normal → 32 B/vertex (−47%), upload −57% (benchmark-confirmed).
- **MR-3: SectionRenderer Material-Combo Cache**: Skip redundant material-array rebuilds when the submesh bitmask is unchanged. Guarded by meshing baseline B15.
- **MR-4: Constant Section Bounds**: Replace per-frame `Mesh.RecalculateBounds` with pre-computed constant cell bounds. Guarded by meshing baseline B16.
- **MR-5: Off-Main-Thread MeshPostProcessJob**: Chain the post-process job off the main thread via `JobHandle` dependency. Guarded by meshing baseline B10.
- **MR-6: Mesh-Output Pre-Size & Pooling**: `MeshOutputPool` rent/return reused buffers, eliminating per-chunk allocations. Stale-reuse guarded by meshing baseline B17.
- **MR-7: Fluid Neighbor Scratch Hoist**: Hoist per-fluid-voxel neighbor scratch to a single `Allocator.Temp`/Execute (−18% fluid meshing, benchmark-confirmed). Guarded by B7/B8.
- **MR-9: Clouds Mesh GC Reduction**: `SetVertices`/`SetTriangles`/`SetNormals` replace `.ToArray()` round-trips (no temp managed arrays per cloud tile).
- **TG-2: Jobified Active-Voxel Emission**: Active-voxel scan via Burst job + bitmask fallback. A/B benchmark editor tool included.
- **TG-3: Deterministic Tick RNG**: Replace `UnityEngine.Random` with local seeded `Unity.Mathematics.Random` in grass spread + lava viscosity (per-voxel+per-tick seed via `World._tickCounter`).
- **TG-4: Full Fluid Burst Port** (Phases 0–4b): Complete Burst migration of the fluid-tick system with cross-chunk halo reads:
    - Phase 0: BH-D1 old-vs-new differential harness (canonicalizer + 2-driver runner).
    - Phase 1: Per-family `NativeHashSet` active-voxel buckets on `ChunkData`.
    - Phase 2: Grass-Burst skipped (not cost-effective).
    - Phase 3: `FluidTickJob` Burst port (Tier-1 interior), zero-drift hybrid (interior Burst / border managed), shared falling-bit encoding and flow-search constants, byte-identical differential (BH-D1 12/12 green).
    - Phase 4a: Parallel interior-fluid scheduling (`IJobParallelFor`), determinism gate (8×6 concurrent byte-identical to serial), worker-count auto-guard, hardened try/finally reclaim.
    - Phase 4b: Full-height 4-cell halo gather (`GatherPaddedFluidVoxels`, `PADDED_FLUID_WIDTH=24`), all fluids Bursted (no managed border fallback), 1.70–2.15× faster than the managed-border hybrid (benchmark-confirmed), cross-chunk parallel-determinism gate (3×3 concurrent byte-identical over 6 rounds).
    - Fluid-tick benchmark suite with distribution stats, Cave-Fill-Cascade + Ocean-25ch scenarios, and full-world fluid stress pass (`FluidStressController` + per-frame tick/mesh/light attribution).
- **Chunk Job Buffer Pooling**: New `ChunkJobArrayPool` pools full-volume chunk job buffers. Neighbor maps refactored into `NeighborMapSet` and mesh job tuple into `MeshingJobData`.
- **Uniform Skylight Memory Optimization**: Sections with uniform skylight (empty sky & deep underground) skip per-voxel light storage.
- **WorldFrameProfiler**: Opt-in `Stopwatch` tick/apply/mesh/light timing in `World.Update`, zero-cost when off.
- **Fluid Stress Mode**: `RuntimeMode.FluidStress` + `WorldLaunchState.IsAutomatedMode` plumbing with main-menu launch button, throttled real-pipeline flood seed, and per-frame attribution report with shared results screen.
- **Benchmark Improvements**: `BenchmarkController` now records FPS (wall & CPU) and memory metrics. `LightingJobBenchmark` fully modernized with toggle to exclude `PrepareJob` from timed region. Mesh generation benchmark includes upload-phase timing.
- **Bug Fixes**:
    - Corrupt LZ4 chunk payloads hanging the loader forever → validate frame magic before decompression + pinned NativeCompressions.LZ4 to 0.6.0.
    - Empty air sections not storing any lighting data.
    - Light attenuation not being consistent across all light paths, causing overly strong shadows.
    - Diagonal shadow artifacts on smooth-lit legacy rotated blocks.
    - 3D Chunk Preview window having full-bright lighting after smooth lighting implementation.
    - Critical issues when the `enableLighting` setting is disabled (null lighting on initially empty sections, fully black editor Chunk Previews).
    - `Mathf` Burst violation in `NeighborhoodLightingJob`.
    - `ChunkPreview3DWindow` and `LightingJobBenchmark` blocklight apply ignoring `IsRemoval` → delegated to `CrossChunkLightModApplier.Compute`.
    - Failing IL2CPP builds due to "netstandard" code stripping.
    - Unity 6.5 UAC1001 warnings → `[NonSerialized]` on 9 runtime-only public fields.
    - Active-voxel scan bounds guard for out-of-range block IDs.
- **Refactors**: Extracted `CrossChunkLightModApplier`, `LightingScheduleDecision`, `LightingJobProcessor`, `LightingModPersister`, `LightingHelper`, `LightAttenuation`, `SeededVoxelRandom`, and `ValidationReflection` shared helpers. Renamed "neighbour" → "neighbor" codebase-wide.
- **Unity Upgrade**: Updated to Unity 6000.5.1f1 (from 6000.4.10f1) via 6000.4.11f1 → 6000.5.0f1 → 6000.5.1f1 + built-in package stripping and unused module pruning.

This release also contains the changes & improvements of the previous releases:

- **Extended Graphics & Display Settings** & **Data-Driven Settings UI** (Phases 1–4)
- **Multi-Noise Terrain Generation** & **Cave Generation Overhaul**
- **Pause Menu & UI Overhaul** with global Tooltip system
- **3D Chunk Preview & World Gen Preview Editor Tools**
- **Benchmark System**

## What's Changed

* feat/Modular-World-Generation-&-World-Types by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/6

**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-06-04...2026-06-25
