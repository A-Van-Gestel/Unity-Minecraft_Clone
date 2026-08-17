# Modular World Generation & World Types

**Version:** 2.6  
**Date:** 2026-04-03  
**Status:** Implemented (2026-05-14)  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)  
**Context:** Decoupling legacy `Mathf.PerlinNoise` generation from a new `[BurstCompile]` `FastNoiseLite` generation pipeline via a modular "World Type" architecture.

---

## Changelog

### v2.6 (from v2.5) — Backlog migration

- **Moved** the forward-looking backlog out of this Architecture doc: former **Section 12** (world-generation
  enhancements), **Section 13** (editor tooling) and **Section 15** (assembly-definition boundary) now live in
  [`../../Design/WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](../../Design/WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md)
  as `TF-15`..`TF-19`. Every sketch was status-checked against code first, so only still-open items moved;
  §12 is now the **disposition mapping table** (what shipped, what an existing `TF-*` already owned, what
  migrated), retained so other docs' `§12.x`/`§13.x` citations keep resolving.
- **Renumbered** former Section 14 (Resolved Questions) → **Section 13**. Sections 1–11 and Appendix A are unchanged.
- **Fixed** §11.3's "adding a new flora type" instruction — structures are authored as pool entries and
  expanded through `ExpandStructure`, not added as `ExpandFlora` switch cases (see the §2.2 note).

### v2.5 (from v2.4) — Final Review

**Self-review fixes:**

- **Fixed** Section 3: `loadedSaveData.worldType` → `metadata.worldType` to match actual `StartWorld()` variable name.
- **Fixed** Section 5: Documented that `WorldJobManager`'s factory switch is the single intentional exception to the "zero legacy references" rule, with a note pointing to the Assembly Definition resolution in Section 15.
- **Fixed** Section 7 Phase 2 Step 3: Corrected cross-reference from "step 1 of Phase 1" to "step 3 of Phase 1."
- **Fixed** Section 10.2.A: Updated "Sky Skip" status from "Recommended for Phase 3" to "Addressed by Section 12.1.A" (Density Band pattern).
- **Fixed** Section 12.1.E.1: Removed dead code (unused first noise evaluation) from erosion pseudocode.
- **Expanded** Section 4.1 (`FastNoiseConfig`): Added missing fields (`RotationType3D`, `WeightedStrength`, `PingPongStrength`) and a note on Domain Warp requiring a separate config instance.
- **Clarified** Section 11.2 capability table: Distinguished "Phase 3 initial" from "future enhancement" for overhangs/caves.

**Colleague review merges:**

- **Resolved** Open Question #1: Confirmed `Vector3Int` is 100% blittable in Unity (three sequential `int` fields).
  Removed the requirement to create a `VoxelModNative` struct; `NativeQueue<VoxelMod>` works in Burst out of the box. Removed from Section 14, updated notes in Sections 4.2 and 4.3.
- **Added** `TerrainAmplitude` to `StandardBiomeAttributesJobData` and `StandardBiomeAttributes` (Section 4.1). `FastNoiseLite` returns normalized -1.0 to 1.0 values; an amplitude multiplier is required to define the physical height of hills/mountains per biome.
- **Refined** the `[MovedFrom]` attribute signature for `LegacyBiomeAttributes` and `LegacyLode` to include the assembly name `"Assembly-CSharp"` for safe namespace transition from the global namespace (Sections 2.3 and 7).
- **Clarified** SIMD expectations in Section 8 Performance table: Burst heavily optimizes scalar noise math, but true SIMD loop vectorization is limited by per-voxel branching/hashing in the noise algorithm.

### v2.4 (from v2.3)

- **Expanded** Section 12.1.A from a brief cave description to a comprehensive "Density Band" pattern covering caves, overhangs, cliff shelves, and arches — with per-biome band parameters (`CaveDepth`, `OverhangHeight`), noise-type-to-style mapping tables,
  and performance analysis showing the band approach is actually faster than legacy's full-column 2D evaluation.
- **Added** Section 12.1.E: Terrain Erosion & Weathering — two approaches:
    - E.1: Noise-based "fake" erosion (Ridged + Domain Warp, low effort, recommended first) with noise style table and concrete code sketch.
    - E.2: True hydraulic erosion simulation (future experimental) with job chaining pattern, cross-chunk boundary mitigations, performance estimates, and fallback strategy.

### v2.3 (from v2.2)

- **Added** Section 11: Extensibility Analysis — documents the three-layer flexibility model (noise primitives, composable configs, strategy pattern), what the new system unlocks vs. legacy limitations, and a concrete capability comparison table.
- **Added** Section 12: Future Enhancements — World Generation, covering terrain improvements (3D density caves, domain warp, continental landmasses, river carving), lode improvements (cellular veins, depth-weighted density),
  flora improvements (biome-aware placement, multi-structure types), and new world type ideas (Amplified, Far Lands, Flat/Creative).
- **Added** Section 13: Future Enhancements — Editor Tooling, covering noise preview inspectors, biome map visualizer, world type comparison tool, lode distribution preview, and seed browser.
- **Renumbered** former Section 11 (Open Questions) → Section 14, Section 12 (Assembly Definition) → Section 15.

### v2.2 (from v2.1)

- **Split** `Lode` / `LodeJobData` along the same boundary as biomes — legacy gets `LegacyLode` + `LegacyLodeJobData` (frozen), standard gets `StandardLode` + `StandardLodeJobData` (free to evolve with `FastNoiseConfig`, density curves, etc.). See Section 2.3 updated tables and
  Section 4.1 for `StandardLodeJobData`.
- **Established** guiding principle for shared vs. owned types: "Shared types describe the output contract, not the generation algorithm." Applied throughout Sections 2.3 and 4.1.
- **Updated** Section 7 Phase 2 steps to include the `Lode`/`LodeJobData` split and migration into Legacy.
- **Updated** `StandardBiomeAttributes` (Section 4.1) to reference `StandardLode` instead of the old shared `Lode`.

### v2.1 (from v2.0)

- **Adopted** "Sealed Legacy Module" architecture (Option A): all legacy code is fully self-contained in `Assets/Scripts/Legacy/`, with zero legacy type references in the main codebase. See Section 2.3 for full rationale and folder layout.
- **Added** `ExpandFlora()` to `IChunkGenerator` interface (Section 2.2) — severs the last cross-cutting dependency between `WorldJobManager` and legacy code (`Structure.cs` + `Noise.cs`).
- **Added** `LegacyNoise.cs` and `LegacyStructure.cs` to the legacy module — `Noise.cs` and `Structure.cs` are removed from the main codebase after migration.
- **Updated** Section 5 (`WorldJobManager`): `ProcessGenerationJobs()` now delegates flora expansion to `_chunkGenerator.ExpandFlora()` instead of calling `Structure.GenerateMajorFlora()` directly — also fixes the existing `_world.biomes[0]` hardcoded-biome bug.
- **Updated** Section 7 (Execution Plan) to reflect the new file movements and legacy isolation steps.
- **Resolved** Open Question #2 (Flora Height) — each generator owns its flora expansion logic, legacy uses `LegacyNoise`-based height, standard uses `Unity.Mathematics.Random`-based height.
- **Added** Assembly Definition boundary as future expansion option in Section 12.

### v2.0 (from v1.0)

- **Updated** target from Unity 6.3 to Unity 6.4 (build 60004.0f1).
- **Updated** all code samples to match actual current codebase (namespaces, signatures, field names).
- **Updated** `SaveSystem.CURRENT_VERSION` references: current version is `3` (not `1`), so migration becomes `v3 → v4`.
- **Updated** `WorldJobManager` constructor: current signature is `WorldJobManager(World world)`, not the redesigned version from v1.0 yet.
- **Updated** `JobDataManager` field names and constructor to match current code (`BiomesJobData`, `AllLodesJobData`, etc.).
- **Updated** `WorldSaveData`: current class lives in `namespace Serialization` and uses `version = 1` default.
- **Updated** `WorldLaunchState`: current class lives in `namespace Data`.
- **Updated** `BiomeAttributes` field names to match actual codebase (e.g., `surfaceBlock` not `SurfaceBlock`).
- **Updated** `ChunkGenerationJob` to reflect actual current fields (`Vector2Int ChunkPosition`, flora helpers `GetTerrainHeight`, `GetStrongestBiome`).
- **Clarified** that `VoxelMod` uses `Vector3Int` (not `Vector3`) — later confirmed as fully blittable and Burst-safe in v2.5.
- **Added** Section 9: FastNoiseLite Library Audit with findings and recommendations.
- **Added** Section 10: Cross-reference with `WORLD_GENERATION_PERFORMANCE_TODOS.md`.
- **Added** detailed notes on `Structure.cs` flora generation (uses `Noise.Get2DPerlin` internally for height randomization).
- **Removed** `MigrationV1ToV2Dummy` reference — this file exists only as a test fixture, the real v1→v2 migration (`MigrationV1ToV2RegionRepack`) and v2→v3 (`MigrationV2ToV3RestoreLighting`) are already implemented.

---

## 1. Executive Summary

To unlock the massive performance gains of the Burst compiler for terrain generation, we must eliminate managed code (`Mathf.PerlinNoise`) from the chunk generation pipeline. However, replacing the noise algorithm alters the deterministic output of seeds, breaking existing saves
and disrupting established testing environments.

This document outlines the **Modular World Generation Architecture**. By introducing a formal "World Type" system, we achieve three goals:

1. **Preserve Legacy Worlds:** Existing saves remain 100% intact using the original (unbursted) generation code and isolated, read-only biome configurations.
2. **Unlock Burst Compilation:** New worlds default to a high-performance, fully Burst-compatible generation pipeline utilizing a native `FastNoiseLite` implementation.
3. **Extensibility:** The architecture inherently supports future world types with unique logic, resolving lifecycle coupling issues to ensure high testability.

---

## 2. Core Architecture Changes

### 2.1. The `WorldType` Definition & Registry

Instead of the `World` singleton holding a hardcoded array of `BiomeAttributes`, World Types will be defined as `ScriptableObjects`. To avoid silent failures from missing inspector assignments, we introduce a strict `WorldTypeRegistry`.

To prevent semantic type collisions (mixing Perlin-noise parameters with FastNoise parameters), we introduce a `BiomeBase` abstract class extracted into its own file.

**File Location:** `Assets/Scripts/Data/WorldTypes/BiomeBase.cs`

```csharp
using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Abstract base for all biome configuration ScriptableObjects.
    /// Enforces type-safety on WorldTypeDefinition.Biomes without restricting
    /// the underlying implementation details of each world type.
    /// </summary>
    public abstract class BiomeBase : ScriptableObject { }
}
```

**File Location:** `Assets/Scripts/Data/WorldTypes/WorldTypeDefinition.cs`

```csharp
using UnityEngine;

namespace Data.WorldTypes
{
    public enum WorldTypeID : byte
    {
        Legacy   = 0, // Maps to the old Mathf.PerlinNoise generation. (0 is crucial for implicit JSON backwards compatibility)
        Standard = 1, // Maps to the new FastNoiseLite Burst generation.
        Amplified = 2 // Reserved for future expansion.
    }

    /// <summary>
    /// A ScriptableObject that defines the configuration for a single world generation type.
    /// </summary>
    [CreateAssetMenu(fileName = "New World Type", menuName = "Minecraft/World Type Definition")]
    public class WorldTypeDefinition : ScriptableObject
    {
        public WorldTypeID TypeID;
        public string DisplayName;

        [Tooltip("The specific biomes available to this world type.")]
        public BiomeBase[] Biomes;

        [Tooltip("Global terrain scaling parameters for this specific world type.")]
        public float BaseTerrainHeight = 42f;
    }
}
```

**File Location:** `Assets/Scripts/Data/WorldTypes/WorldTypeRegistry.cs`

```csharp
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// A ScriptableObject registry that maps WorldTypeIDs to their definitions.
    /// Throws on missing entries to prevent silent failures.
    /// </summary>
    [CreateAssetMenu(fileName = "WorldTypeRegistry", menuName = "Minecraft/World Type Registry")]
    public class WorldTypeRegistry : ScriptableObject
    {
        public WorldTypeDefinition[] Types;

        /// <summary>
        /// Looks up a WorldTypeDefinition by its ID. Throws if none is registered.
        /// </summary>
        public WorldTypeDefinition GetWorldType(WorldTypeID id)
        {
            var wt = Types.FirstOrDefault(t => t.TypeID == id);
            if (wt == null)
            {
                throw new KeyNotFoundException(
                    $"[WorldTypeRegistry] CRITICAL: No WorldTypeDefinition found for ID '{id}'. " +
                    $"Ensure it is assigned in the registry asset.");
            }
            return wt;
        }
    }
}
```

### 2.2. The Generation Strategy Pattern (`IChunkGenerator`)

We will abstract chunk generation using the Strategy Pattern.

**Crucial Fixes:**

* `JobDataManager` is currently responsible for *all* job data including biomes and lodes (see `Assets/Scripts/Data/JobData/JobDataManager.cs`).
  After this refactor, it will be strictly for *world-type-agnostic* data (BlockTypes, CustomMeshes). Biomes and Lodes will be owned by each `IChunkGenerator` implementation.
* The `IChunkGenerator` must provide a fallback for synchronous main-thread voxel queries — currently used by `World.GetHighestVoxel()` at `World.cs:2531` (which calls `WorldGen.GetVoxel()` at line 2578 as a fallback when chunk data isn't loaded).

**File Location:** `Assets/Scripts/Jobs/Generators/IChunkGenerator.cs`

```csharp
using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using UnityEngine;

namespace Jobs.Generators
{
    public interface IChunkGenerator
    {
        /// <summary>
        /// Controls which optional generation passes (caves, lodes, water) are executed.
        /// Defaults to GenerationFeatureFlags.Default (all passes enabled).
        /// Set before calling ScheduleGeneration to take effect.
        /// </summary>
        GenerationFeatureFlags FeatureFlags { get; set; }

        /// <summary>Injects explicit dependencies required for generation. Called once during WorldJobManager construction.</summary>
        void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData,
            bool isSingleBiomeMode = false, StandardBiomeAttributes selectedBiome = null);

        /// <summary>
        /// Schedules the generation job and returns a populated GenerationJobData struct.
        /// <paramref name="activeVoxelPool"/> (TG-6): when supplied, the per-chunk active-voxel list is
        /// rented from it and flagged so the caller returns it instead of disposing; null on the
        /// editor / preview / benchmark paths.
        /// </summary>
        GenerationJobData ScheduleGeneration(ChunkCoord coord,
            global::Helpers.ActiveVoxelListPool activeVoxelPool = null);

        /// <summary>Synchronous main-thread voxel query. Used by World.GetHighestVoxel and spawn-point logic.</summary>
        byte GetVoxel(Vector3Int globalPos);

        /// <summary>
        /// Expands a structure spawn marker (queued by the generation job) into a full set of VoxelMods
        /// (trunk + leaves, cactus body, etc.). Called on the main thread during
        /// WorldJobManager.ProcessGenerationJobs(). Each generator owns its own structure templates and
        /// random strategy.
        /// </summary>
        IEnumerable<VoxelMod> ExpandStructure(StructureSpawnMarker marker);

        /// <summary>Terrain generation diagnostics for one column. Main-thread only; used by DebugScreen.</summary>
        TerrainDebugInfo GetTerrainDebugInfo(int globalX, int globalZ);

        /// <summary>Evaluates a batch of pixels for the terrain debug minimap (RGBA32 into outputPixels).</summary>
        void EvaluateTerrainDebugPixels(int startIndex, int count, int textureSize,
            int originX, int originZ, int scale, TerrainDebugRenderMode mode,
            int biomeCount, int sliceY, byte[] outputPixels);

        /// <summary>Disposes of any internal NativeArrays allocated during Initialize.</summary>
        void Dispose();
    }
}
```

> **Structures superseded flora (`ExpandStructure`, not `ExpandFlora`).** The interface originally carried
> `ExpandFlora(VoxelMod rootMod)`, which encoded the flora type in the mod's `ID` field. It was replaced by
> the generic **structure-marker** path: the generation job queues a `StructureSpawnMarker`
> (`int3 Position` + `int PoolEntryIndex`, a flat index into the generator's flattened structure pool), and
> the main thread resolves that index to a `CompositeStructureTemplate`. Trees, cacti, and minor flora are
> all pool entries under this one mechanism, selected through the major/minor flora pools and flora-zone
> noises on `StandardBiomeAttributes`. Sections below that still speak of `ExpandFlora` and of adding "a case
> per flora type" describe the superseded shape — adding a structure today means authoring a template and
> adding a pool entry, not editing a `switch`.

### 2.3. Legacy Isolation Architecture ("Sealed Legacy Module")

All legacy world generation code will be fully self-contained in `Assets/Scripts/Legacy/`. The main codebase will contain **zero references** to any legacy type — the only bridge is the `IChunkGenerator` interface — with one intentional exception:
`WorldJobManager`'s factory switch must create `new LegacyChunkGenerator()` (see Section 5 for the caveat, and `TF-19` in [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](../../Design/WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) for how to eliminate it via the Assembly Definition pattern).
This prevents accidental breakage of legacy worlds when modifying the active (Standard) generation code.

#### Design Rationale

The legacy generation system has two cross-cutting concerns that would normally keep legacy types in shared code:

1. **`Noise.cs`** — Used by `WorldGen.GetVoxel()`, `ChunkGenerationJob` flora checks, and `Structure.cs` for trunk height.
2. **`Structure.cs`** — Called from `WorldJobManager.ProcessGenerationJobs()` (shared infrastructure) to expand flora root points into full tree/cactus structures.

By adding `ExpandFlora()` to `IChunkGenerator`, the second concern is resolved: `WorldJobManager` delegates flora expansion to the active generator instead of calling `Structure` directly. This also **fixes the existing bug** at `WorldJobManager.cs:347` where `_world.biomes[0]`
is hardcoded for all flora height bounds regardless of the actual biome.

`Noise.cs` and `Structure.cs` are then copied into `Legacy/` as `LegacyNoise.cs` and `LegacyStructure.cs`, and the originals are deleted from the main codebase. The duplication is ~155 lines of trivial utility code (29 + 81 + 23 + 22 = noise + structure + lode class + lode job
struct) — acceptable for complete isolation of a frozen, read-only code path.

#### Guiding Principle: Shared vs. Owned Types

> **Shared types describe the output contract, not the generation algorithm.**

Types that describe *what a chunk looks like when it's done* (`VoxelMod`, `GenerationJobData`, `BlockTypeJobData`) are safe to share — both generators produce the same output format and these will never need to diverge.

Types that describe *how terrain is generated* (`BiomeAttributesJobData`, `LodeJobData`, `Noise`) must be owned by the generator that uses them. Even when the fields currently overlap (as with `Lode`), the semantic meaning diverges across noise systems (e.g., `scale`/`threshold`/
`noiseOffset` are tuned for `Mathf.PerlinNoise` in legacy, but replaced by `FastNoiseConfig` in standard). Sharing these types forces the Standard path to repurpose legacy field names for different concepts — or forces legacy to accumulate dead-weight fields as Standard evolves.

#### Legacy Module Folder Structure

```
Assets/Scripts/Legacy/
├── LegacyChunkGenerator.cs            IChunkGenerator implementation (owns biome + lode NativeArrays + flora expansion)
├── LegacyChunkGenerationJob.cs        IJobFor struct (NO [BurstCompile] — uses managed Mathf.PerlinNoise)
├── LegacyWorldGen.cs                  Static GetVoxel() — renamed from WorldGen.cs
├── LegacyNoise.cs                     Mathf.PerlinNoise wrappers — copy of Noise.cs (29 lines)
├── LegacyStructure.cs                 Flora generation — copy of Structure.cs, uses LegacyNoise (81 lines)
├── LegacyBiomeAttributes.cs           ScriptableObject + LegacyLode class — renamed from BiomeAttributes.cs (with [MovedFrom])
├── LegacyBiomeAttributesJobData.cs    Blittable struct — extracted from Data/JobData.cs
├── LegacyLodeJobData.cs               Blittable struct — extracted from Data/JobData.cs (frozen, 6 fields)
└── Editor/
    └── LegacyBiomeAttributesEditor.cs Read-only Inspector to prevent accidental modification
```

#### Main Codebase (Legacy-Free)

```
Assets/Scripts/
├── World.cs                            No BiomeAttributes[] field, no WorldGen/Noise/Structure references
├── WorldJobManager.cs                  Only knows IChunkGenerator — delegates all generation + flora
├── Jobs/
│   ├── Generators/
│   │   ├── IChunkGenerator.cs          The sole interface bridging legacy and standard
│   │   └── StandardChunkGenerator.cs   IChunkGenerator for new worlds
│   ├── StandardChunkGenerationJob.cs   [BurstCompile] — FastNoiseLite + Unity.Mathematics
│   └── Data/
│       ├── GenerationJobData.cs        Shared output container (map, heightmap, mods queue)
│       ├── FastNoiseConfig.cs          Blittable noise config for Standard biomes & lodes
│       ├── StandardBiomeAttributesJobData.cs
│       └── StandardLodeJobData.cs      Blittable lode struct with FastNoiseConfig (free to evolve)
├── Data/
│   ├── WorldTypes/
│   │   ├── BiomeBase.cs                Abstract ScriptableObject base
│   │   ├── WorldTypeDefinition.cs      WorldTypeID enum + definition SO
│   │   ├── WorldTypeRegistry.cs        ID → Definition lookup
│   │   └── StandardBiomeAttributes.cs  Authoring SO for Standard biomes (includes StandardLode class)
│   ├── JobData.cs                      BlockTypeJobData, etc. — NO BiomeAttributesJobData, NO LodeJobData
│   ├── VoxelMod.cs                     Shared modification struct
│   └── JobData/
│       └── JobDataManager.cs           BlockTypes + CustomMeshes only — NO biomes/lodes
└── Libraries/
    └── FastNoiseLite.cs                Burst-compatible noise (Standard path only)
```

#### Files Removed from Main Codebase After Migration

| Original File                                   | Destination                                                     | Rationale                                                                                |
|-------------------------------------------------|-----------------------------------------------------------------|------------------------------------------------------------------------------------------|
| `WorldGen.cs`                                   | `Legacy/LegacyWorldGen.cs`                                      | Renamed + moved (uses `Mathf.PerlinNoise`)                                               |
| `ChunkGenerationJob.cs`                         | `Legacy/LegacyChunkGenerationJob.cs`                            | Renamed + moved (non-Burst)                                                              |
| `BiomeAttributes.cs` (includes `Lode` class)    | `Legacy/LegacyBiomeAttributes.cs` (includes `LegacyLode` class) | Renamed + moved (with `[MovedFrom]` attribute). `Lode` renamed to `LegacyLode`.          |
| `Noise.cs`                                      | `Legacy/LegacyNoise.cs`                                         | Copied then original deleted (managed noise)                                             |
| `Structure.cs`                                  | `Legacy/LegacyStructure.cs`                                     | Copied then original deleted (depends on Noise)                                          |
| `BiomeAttributesJobData` (in `Data/JobData.cs`) | `Legacy/LegacyBiomeAttributesJobData.cs`                        | Extracted to own file, removed from shared `JobData.cs`                                  |
| `LodeJobData` (in `Data/JobData.cs`)            | `Legacy/LegacyLodeJobData.cs`                                   | Extracted to own file, removed from shared `JobData.cs`. Constructor takes `LegacyLode`. |

> **Note on `LegacyLode` asset protection:** The `Lode` class is currently defined inside `BiomeAttributes.cs` and serialized as part of each biome `.asset` file. When renaming to `LegacyLode`, the same `[MovedFrom]` strategy is required:
> ```csharp
> [UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, "Assembly-CSharp", "Lode")]
> [Serializable]
> public class LegacyLode { /* exact current fields, frozen */ }
> ```

#### Shared Types (Used by Both Paths, Not Duplicated)

| Type                | File                             | Rationale                                                                        |
|---------------------|----------------------------------|----------------------------------------------------------------------------------|
| `VoxelMod`          | `Data/VoxelMod.cs`               | Output contract — both paths produce the same modification format                |
| `GenerationJobData` | `Jobs/Data/GenerationJobData.cs` | Both generators produce identical output shape (map + heightmap + mods)          |
| `VoxelData`         | `VoxelData.cs`                   | Constants (`ChunkWidth`, `ChunkHeight`, `SolidGroundHeight`, `SeaLevel`, `Seed`) |
| `BlockTypeJobData`  | `Data/JobData.cs`                | Block database is world-type-agnostic                                            |
| `ChunkCoord`        | `Helpers/ChunkCoord.cs`          | Coordinate helper                                                                |

> **Why `LodeJobData` is no longer shared:** Although the current `LodeJobData` fields (`BlockID`, `MinHeight`, `MaxHeight`, `Scale`, `Threshold`, `NoiseOffset`) superficially work for both paths, the fields `Scale`, `Threshold`, and `NoiseOffset` are semantically tied to
`Mathf.PerlinNoise`. The Standard path replaces these with a `FastNoiseConfig` struct offering `NoiseType`, `FractalType`, `Octaves`, `Lacunarity`, `Gain`, and cellular parameters. Future Standard lode enhancements (density curves, depth-weighted probability, multi-block veins)
> would either be blocked by the legacy struct shape, or would pollute the legacy path with unused fields. Splitting costs ~45 lines of duplication and removes this coupling permanently.

#### Preservation Requirements

* **Bug Preservation:** The current O(N²) biome noise evaluation (where `Noise.Get2DPerlin` is recalculated for *every Y step* inside the column loop via the `GetTerrainHeight` helper at `ChunkGenerationJob.cs:120-136` and duplicated in `WorldGen.GetVoxel` at `WorldGen.cs:25-52`)
  **must be preserved exactly as-is** in the legacy module. Fixing it would alter the output and break legacy seed reproducibility. This will be explicitly commented in `LegacyWorldGen.cs` and `LegacyChunkGenerationJob.cs`.
* **Flora Preservation:** The legacy flora pipeline — `LegacyNoise.Get2DPerlin` for zone/placement checks in `LegacyChunkGenerationJob` (current lines 91-108), and `LegacyNoise.Get2DPerlin` for trunk height in `LegacyStructure.MakeTree/MakeCacti` (current `Structure.cs:20,72`) —
  must remain identical to preserve tree/cactus placement and height for existing seeds.

#### B. Standard Path (`StandardChunkGenerator` + `StandardChunkGenerationJob`)

* **Status:** Active / Default for new worlds.
* **Compiler:** `[BurstCompile(FloatPrecision.Standard, FloatMode.Default)]` — `FloatMode.Default` ensures cross-platform math determinism for seeds, unlike `FloatMode.Fast`.
* **Behavior:** Highly optimized, branchless where possible, utilizing CPU vectorization.
* **Structures / flora:** `StandardChunkGenerator.ExpandStructure()` resolves the marker's `PoolEntryIndex` to a `CompositeStructureTemplate` and uses `Unity.Mathematics.Random` (seeded deterministically per-column) for placement variation, instead of `Noise.Get2DPerlin`. New structures are added here only — the legacy module is frozen.

---

## 3. Resolving the Lifecycle Timing & Disposal Conflict

Currently, `World.cs` initializes `JobManager` and `JobDataManager` in `Awake()` (lines 159-179), with biome data parsed in `PrepareJobData()` (line 1040). We will split initialization and enforce strict, encapsulated disposal.

**Current `Awake()` (to be modified):**

```csharp
// Current code at World.cs:159-179
private void Awake()
{
    if (Instance is not null && Instance != this) Destroy(gameObject);
    else
    {
        Instance = this;
        appSaveDataPath = Application.persistentDataPath;
        JobManager = new WorldJobManager(this);       // Current: no world-type awareness
        ChunkPool = new ChunkPoolManager(transform);
        PrepareJobData();                             // Current: parses biomes + blocks together
    }
}
```

**Updated `World.cs` (proposed changes):**

```csharp
[Header("World Configuration")]
[SerializeField] private WorldTypeRegistry worldTypeRegistry;

// Set during StartWorld(). Read by any system that needs to know the active generation type.
public WorldTypeDefinition ActiveWorldType { get; private set; }

private void Awake()
{
    if (Instance is not null && Instance != this) Destroy(gameObject);
    else
    {
        Instance = this;
        appSaveDataPath = Application.persistentDataPath;
        ChunkPool = new ChunkPoolManager(transform);

        // Parses BlockDatabase into NativeArrays (Custom Meshes, Textures, etc.)
        // DOES NOT parse Biomes anymore — that is the generator's responsibility.
        PrepareGlobalJobData();
    }
}

private IEnumerator StartWorld()
{
    // ... existing Load Save Data & Settings (lines 321-379 unchanged) ...

    // DETERMINE WORLD TYPE (new code, after line 379)
    // 'metadata' is the WorldSaveData loaded from level.dat at World.cs:371
    WorldTypeID typeToLoad = WorldLaunchState.IsNewGame
        ? WorldLaunchState.SelectedWorldType
        : metadata.worldType;

    // SAFE FALLBACK: Resolve any unsupported type IDs here, before the registry lookup.
    if (typeToLoad == WorldTypeID.Amplified)
    {
        Debug.LogWarning("[World] Amplified world type is not yet implemented. Falling back to Standard.");
        typeToLoad = WorldTypeID.Standard;
    }

    ActiveWorldType = worldTypeRegistry.GetWorldType(typeToLoad);

    // INITIALIZE JOB MANAGER & STRATEGY
    // Explicitly passes JobDataManager to avoid hidden order-of-operation contracts.
    JobManager = new WorldJobManager(this, ActiveWorldType, JobDataManager);

    // ... Proceed to LoadOrGenerateChunk (line 415+) ...
}

private void OnDestroy()
{
    // ENCAPSULATED DISPOSAL
    // World.cs no longer iterates job dictionaries directly. It trusts the Managers.
    JobManager?.Dispose();
    JobDataManager?.Dispose();
    FluidVertexTemplates?.Dispose();
    // ... other standard cleanup (ChunkPool, StorageManager, etc.) ...
}
```

**Key change:** `JobManager` construction moves from `Awake()` to `StartWorld()`, after the world type is resolved from save data or UI selection. The current `PrepareJobData()` at line 1040 is split: block/mesh data stays in `Awake()` (`PrepareGlobalJobData()`), biome/lode data
moves into each `IChunkGenerator.Initialize()`.

---

## 4. Burst-Compatible Noise & Biome Data

### 4.1. Struct Separation & Pure Config

To support all FastNoiseLite parameters — including the Cellular fields required by the biome selection strategy in Section 4.4 — we introduce explicit configuration structs and matching ScriptableObjects in their proper namespaces.

**File Location:** `Assets/Scripts/Jobs/Data/FastNoiseConfig.cs`

```csharp
using System;
using Libraries;

namespace Jobs.Data
{
    /// <summary>
    /// A fully serializable, blittable configuration struct for a FastNoiseLite instance.
    /// Factory construction of the actual FastNoiseLite object from this config
    /// must happen on the Main Thread inside StandardChunkGenerator.Initialize().
    /// </summary>
    [Serializable]
    public struct FastNoiseConfig
    {
        public int SeedOffset;
        public float Frequency;
        public FastNoiseLite.NoiseType NoiseType;
        public FastNoiseLite.RotationType3D RotationType3D; // ImproveXZPlanes recommended for terrain

        // Fractal parameters
        public FastNoiseLite.FractalType FractalType;
        public int Octaves;
        public float Gain;
        public float Lacunarity;
        public float WeightedStrength;  // FBm weighted strength (0 = standard FBm)
        public float PingPongStrength;  // Only meaningful when FractalType == PingPong

        // Cellular parameters — only meaningful when NoiseType == Cellular
        public FastNoiseLite.CellularDistanceFunction CellularDistanceFunction;
        public FastNoiseLite.CellularReturnType CellularReturnType;
        public float CellularJitter;
    }
}
```

> **Note on Domain Warp:** As of v2.3, `FastNoiseConfig` includes `DomainWarpType` and `DomainWarpAmp` fields. These default to `OpenSimplex2` and `0f` respectively, meaning existing configs are unaffected. Domain Warp is a coordinate transformation (`DomainWarp(ref x, ref y, ref z)`) applied *before* a noise evaluation — it requires its own `FastNoiseLite` instance, separate from the noise instance it distorts. Features that use Domain Warp (e.g., density warping, cave warping) define a separate `FastNoiseConfig` field (e.g., `densityWarpConfig`) whose
`domainWarpType` and `domainWarpAmp` values configure the warp instance.

**File Location:** `Assets/Scripts/Jobs/Data/StandardLodeJobData.cs`

```csharp
namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of a Standard lode (ore vein).
    /// Uses FastNoiseConfig instead of the legacy scale/threshold/noiseOffset triple,
    /// enabling full FastNoiseLite noise types (Cellular veins, fractal ridged, etc.).
    /// Free to evolve independently of the frozen LegacyLodeJobData.
    /// </summary>
    public struct StandardLodeJobData
    {
        public readonly byte BlockID;
        public readonly int MinHeight;
        public readonly int MaxHeight;
        public FastNoiseConfig NoiseConfig;

        public StandardLodeJobData(StandardLode lode)
        {
            BlockID = lode.blockID;
            MinHeight = lode.minHeight;
            MaxHeight = lode.maxHeight;
            NoiseConfig = lode.noiseConfig;
        }
    }
}
```

**File Location:** `Assets/Scripts/Jobs/Data/StandardBiomeAttributesJobData.cs`

```csharp
namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of StandardBiomeAttributes.
    /// Constructed by StandardChunkGenerator.Initialize() from the ScriptableObject array.
    /// Lodes are flattened into a shared NativeArray&lt;StandardLodeJobData&gt;, referenced by index range.
    /// </summary>
    public struct StandardBiomeAttributesJobData
    {
        public FastNoiseConfig TerrainNoiseConfig;
        public FastNoiseConfig BiomeWeightNoiseConfig;

        public float BaseTerrainHeight;
        public float TerrainAmplitude; // FastNoiseLite returns -1..1; multiply by this for physical height
        public byte SurfaceBlockID;
        public byte SubSurfaceBlockID;

        public float MajorFloraPlacementThreshold;
        public byte MajorFloraIndex;

        // Index into the shared NativeArray<StandardLodeJobData> owned by StandardChunkGenerator
        public int LodeStartIndex;
        public int LodeCount;
    }
}
```

**File Location:** `Assets/Scripts/Data/WorldTypes/StandardBiomeAttributes.cs`

```csharp
using System;
using UnityEngine;
using Jobs.Data;

namespace Data.WorldTypes
{
    /// <summary>
    /// Authoring ScriptableObject for a Standard (FastNoiseLite-based) biome.
    /// Fields map directly to StandardBiomeAttributesJobData for job consumption.
    /// </summary>
    [CreateAssetMenu(fileName = "New Standard Biome", menuName = "Minecraft/Standard Biome Attributes")]
    public class StandardBiomeAttributes : BiomeBase
    {
        public FastNoiseConfig TerrainNoiseConfig;
        public FastNoiseConfig BiomeWeightNoiseConfig;

        public float BaseTerrainHeight = 42f;

        [Tooltip("Vertical multiplier for terrain noise (e.g., 20 means hills reach BaseTerrainHeight ± 20). " +
                 "FastNoiseLite returns normalized -1.0 to 1.0; this gives it physical scale.")]
        public float TerrainAmplitude = 20f;

        public byte SurfaceBlockID;
        public byte SubSurfaceBlockID;

        public float MajorFloraPlacementThreshold;
        public byte MajorFloraIndex;

        public StandardLode[] Lodes;
    }

    /// <summary>
    /// Authoring class for a Standard lode (ore vein).
    /// Uses FastNoiseConfig for full FastNoiseLite noise control.
    /// </summary>
    [Serializable]
    public class StandardLode
    {
        [Tooltip("Name of the lode.")]
        public string nodeName;

        [Tooltip("ID of the block that will be generated.")]
        public byte blockID;

        [Tooltip("Blocks will not be generated below this height.")]
        public int minHeight;

        [Tooltip("Blocks will not be generated above this height.")]
        public int maxHeight;

        [Tooltip("FastNoiseLite noise configuration for this lode's generation pattern.")]
        public FastNoiseConfig noiseConfig;
    }
}
```

### 4.2. Job Definition & Instantiation (Pass-by-Value & Vectorization)

`FastNoiseLite` is exactly **72 bytes** and entirely blittable (18 fields: 10 × `int`/`enum` + 8 × `float`). Lookup tables live in a pinned `SharedStatic<LookupPointers>` and are not part of the struct (see `FastNoiseLite.cs:1870-1905`). We pass the noise state **by value**
directly into the job — zero heap allocation, maximum L1 cache locality.

`ChunkPosition` uses `int2` rather than the current `UnityEngine.Vector2Int` (see `ChunkGenerationJob.cs:22`) to keep the entire math pipeline within `Unity.Mathematics`, enabling Burst's SIMD auto-vectorization.

```csharp
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    [BurstCompile(FloatPrecision.Standard, FloatMode.Default)] // Strict cross-platform determinism
    public struct StandardChunkGenerationJob : IJobFor
    {
        #region Input Data

        [ReadOnly] public int BaseSeed;
        [ReadOnly] public int2 ChunkPosition; // int2, not Vector2Int, for Burst SIMD vectorization

        [ReadOnly] public NativeArray<BlockTypeJobData> BlockTypes;
        [ReadOnly] public NativeArray<StandardBiomeAttributesJobData> Biomes;
        [ReadOnly] public NativeArray<StandardLodeJobData> AllLodes;

        // Passed by value (72 bytes). SharedStatic lookup tables are accessed via raw pointer,
        // shared across all worker threads without copying.
        public FastNoiseLite GlobalCaveNoise;

        #endregion

        #region Output Data

        [WriteOnly] [NativeDisableParallelForRestriction]
        public NativeArray<uint> OutputMap;

        [WriteOnly]
        public NativeArray<ushort> OutputHeightMap;

        // ParallelWriter allows concurrent Enqueue from worker threads
        public NativeQueue<VoxelMod>.ParallelWriter Modifications;

        #endregion

        public void Execute(int index)
        {
            // ... implementation ...
        }
    }
}
```

> **Note on `VoxelMod`:** The current `VoxelMod` struct (used in `ChunkGenerationJob.cs:104`) uses `Vector3Int`. This is **100% safe** — `Vector3Int` is simply three sequential 32-bit integers (`x`, `y`, `z`) and is fully blittable in Unity. `NativeQueue<VoxelMod>` works
> correctly in Burst jobs without any wrapper or conversion. No `VoxelModNative` or `int3` replacement is needed.

### 4.3. Flora Placement in Burst (Deterministic Randoms)

`Mathf.PerlinNoise` cannot be called from Burst. The current flora check in `ChunkGenerationJob.cs:91-108` uses `Noise.Get2DPerlin` for both zone and placement detection. We replace this with `Unity.Mathematics.Random`, seeded deterministically per-column.

```csharp
using Unity.Mathematics;

// Inside StandardChunkGenerationJob.Execute(int index)
int3 globalPos = new int3(x + ChunkPosition.x, y, z + ChunkPosition.y);

// Create a deterministic random state for this column.
// math.max guards against Unity.Mathematics.Random throwing on seed value 0.
uint deterministicSeed = math.max(1u, math.hash(new int3(globalPos.x, globalPos.z, BaseSeed)));
var random = new Random(deterministicSeed);

if (random.NextFloat() > biome.MajorFloraPlacementThreshold)
{
    // Enqueue a flora root point for main-thread structure generation.
    // Vector3Int is fully blittable — safe in NativeQueue<VoxelMod> under Burst.
    Modifications.Enqueue(new VoxelMod(
        new Vector3Int(globalPos.x, globalPos.y, globalPos.z),
        biome.MajorFloraIndex
    ));
}
```

> **Structure Expansion:** Each `IChunkGenerator` owns its structure expansion via `ExpandStructure()`
(Section 2.2). The `StandardChunkGenerator` implementation resolves the marker's pool entry to a
`CompositeStructureTemplate` and uses `Unity.Mathematics.Random` (seeded from `math.hash(position, seed)`) for
placement variation instead of `Noise.Get2DPerlin`. The legacy path delegates to
`LegacyStructure.GenerateMajorFlora()`, which continues using `LegacyNoise.Get2DPerlin` unchanged. Neither
`Noise.cs` nor `Structure.cs` exist in the main codebase — see Section 2.3.

### 4.4. Standard Biome Blending Strategy

Unlike the legacy system which evaluates every biome's weight via `Noise.Get2DPerlin` per block (see `WorldGen.cs:25-45`), the `StandardChunkGenerationJob` will use **Voronoi/Cellular Noise** to assign discrete biome regions.

1. A `FastNoiseLite` instance configured for Cellular noise (`NoiseType.Cellular`) is evaluated at global X/Z to produce a cell value.
2. That value is mapped to a discrete biome index into the `Biomes` array.
3. **Note on Blending:** The initial implementation uses hard Voronoi cell boundaries with no cross-biome interpolation. `CellularJitter` controls how organic the cell boundaries appear but does **not** blend biome data — a column is assigned exactly one biome. Smooth gradient
   transitions are a future enhancement, requiring a separate biome-weight blending pass using cellular distance fields.

---

## 5. Generator Strategy Resolution & Encapsulation

`WorldJobManager` delegates scheduling to the active strategy, exposes a synchronous main-thread voxel query, and fully encapsulates all job disposal.

**Current `WorldJobManager` signature** (at `WorldJobManager.cs:33`):

```csharp
public WorldJobManager(World world) { _world = world; }
```

**Updated `WorldJobManager`:**

```csharp
using System;
using System.Collections.Generic;
using Data;
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using Jobs.Generators;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Manages the lifecycle of all background jobs (generation, meshing, lighting).
/// Owns the active IChunkGenerator strategy and delegates scheduling to it.
/// </summary>
public class WorldJobManager : IDisposable
{
    private readonly World _world;
    private readonly IChunkGenerator _chunkGenerator;

    #region Job Tracking Dictionaries

    public Dictionary<ChunkCoord, GenerationJobData> generationJobs { get; } = new();
    public Dictionary<ChunkCoord, (JobHandle handle, MeshDataJobOutput meshData)> meshJobs { get; } = new();
    public Dictionary<ChunkCoord, LightingJobData> lightingJobs { get; } = new();

    #endregion

    // --- Cached Collections for GC Optimization (carried from current code) ---
    private readonly List<ChunkCoord> _completedGenJobs = new();
    private readonly List<ChunkCoord> _completedMeshJobs = new();
    private readonly List<ChunkCoord> _completedLightJobs = new();
    private readonly HashSet<ChunkCoord> _chunksToRebuildMesh = new();
    private readonly Dictionary<ChunkCoord, HashSet<Vector2Int>> _droppedLightUpdates = new();

    #region Constructor

    /// <summary>
    /// Initializes the WorldJobManager and resolves the correct IChunkGenerator strategy.
    /// </summary>
    /// <param name="world">The main World instance that owns this manager.</param>
    /// <param name="activeWorldType">The resolved WorldTypeDefinition for the current session.
    /// All unsupported type IDs (e.g. Amplified) must be remapped to a supported type
    /// in World.StartWorld() before this constructor is called.</param>
    /// <param name="globalJobData">World-type-agnostic NativeArrays (BlockTypes, CustomMeshes, etc.).</param>
    public WorldJobManager(World world, WorldTypeDefinition activeWorldType, JobDataManager globalJobData)
    {
        _world = world;

        // Strategy Factory.
        // NOTE: This is the SINGLE intentional exception to the "zero legacy references"
        // rule from Section 2.3. The factory must create concrete generator instances,
        // which requires referencing the Legacy namespace. If the Assembly Definition
        // boundary (TF-19) is adopted later, this switch is replaced by a
        // registration pattern (GeneratorRegistry) that eliminates the direct reference.
        _chunkGenerator = activeWorldType.TypeID switch
        {
            WorldTypeID.Legacy   => new LegacyChunkGenerator(),
            WorldTypeID.Standard => new StandardChunkGenerator(),
            _ => throw new ArgumentException(
                $"[WorldJobManager] Unsupported WorldTypeID: {activeWorldType.TypeID}. " +
                $"Ensure all unimplemented types are remapped to a supported type before constructing WorldJobManager.")
        };

        _chunkGenerator.Initialize(VoxelData.Seed, activeWorldType, globalJobData);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Synchronous main-thread voxel query, delegated to the active generator strategy.
    /// Replaces the current direct call to WorldGen.GetVoxel() in World.GetHighestVoxel() (World.cs:2578).
    /// </summary>
    public byte GetVoxel(Vector3Int globalPos) => _chunkGenerator.GetVoxel(globalPos);

    /// <summary>
    /// Schedules a background job to generate voxel data for the given chunk coordinate.
    /// Replaces the current inline job creation in WorldJobManager.ScheduleGeneration() (WorldJobManager.cs:44-80).
    /// </summary>
    public void ScheduleGeneration(ChunkCoord coord)
    {
        if (generationJobs.ContainsKey(coord)) return;

        Vector2Int chunkVoxelPos = coord.ToVoxelOrigin();
        if (_world.worldData.Chunks.TryGetValue(chunkVoxelPos, out ChunkData data) && data.IsPopulated)
            return;

        GenerationJobData jobData = _chunkGenerator.ScheduleGeneration(coord);
        generationJobs.Add(coord, jobData);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        foreach (var job in generationJobs.Values) { job.Handle.Complete(); job.Dispose(); }
        foreach (var (handle, meshData) in meshJobs.Values) { handle.Complete(); meshData.Dispose(); }
        foreach (var job in lightingJobs.Values) { job.Handle.Complete(); job.Dispose(); }

        generationJobs.Clear();
        meshJobs.Clear();
        lightingJobs.Clear();

        _chunkGenerator?.Dispose();
    }

    #endregion
}
```

### 5.1. `ProcessGenerationJobs` Flora Delegation

The current `ProcessGenerationJobs()` at `WorldJobManager.cs:326` has a direct dependency on `Structure.GenerateMajorFlora()` and `_world.biomes[0]` for flora expansion. This must be refactored to delegate to the active generator:

**Current code (to be replaced):**

```csharp
// WorldJobManager.cs:347 — STAGE 2 inside ProcessGenerationJobs()
while (jobEntry.Value.Mods.TryDequeue(out VoxelMod mod))
{
    IEnumerable<VoxelMod> floraMods = Structure.GenerateMajorFlora(
        mod.ID, mod.GlobalPosition,
        _world.biomes[0].minHeight,   // BUG: hardcoded to first biome
        _world.biomes[0].maxHeight);  // BUG: ignores actual biome at position
    _world.EnqueueVoxelModifications(floraMods);
}
```

**Updated code (generator-agnostic):**

```csharp
// WorldJobManager.cs — STAGE 2 inside ProcessGenerationJobs()
while (jobEntry.Value.Mods.TryDequeue(out VoxelMod mod))
{
    // Delegate flora expansion to the active generator strategy.
    // Each generator resolves the correct biome at the mod's position and uses
    // its own noise/random strategy for trunk height determination.
    IEnumerable<VoxelMod> floraMods = _chunkGenerator.ExpandFlora(mod);
    _world.EnqueueVoxelModifications(floraMods);
}
```

This change:

- **Removes** the direct dependency on `Structure.cs` and `Noise.cs` from `WorldJobManager`.
- **Removes** the direct dependency on `World.biomes` (`BiomeAttributes[]`) from `WorldJobManager`.
- **Fixes** the existing bug where all flora used `biomes[0]` regardless of the actual biome at the position.

> **Migration Note:** The existing `ScheduleMeshing()`, `ScheduleLightingUpdate()`, `ProcessMeshJobs()`, and `ProcessLightingJobs()` methods in `WorldJobManager.cs` are world-type-agnostic (they operate on `GenerationJobData`, `MeshDataJobOutput`, `LightingJobData`) and do **not
** need to change. Only `ScheduleGeneration()` and the flora expansion call in `ProcessGenerationJobs()` are modified to delegate to the strategy.

---

## 6. Data & Serialization Integration

### 6.1. `WorldSaveData` Update

The current `WorldSaveData` is defined in `Assets/Scripts/Serialization/SaveDataTypes.cs` within `namespace Serialization`:

```csharp
// Current code (SaveDataTypes.cs:16-26)
[Serializable]
public class WorldSaveData
{
    public int version = 1;
    public string worldName;
    public int seed;
    public long creationDate;
    public long lastPlayed;
    public WorldStateData worldState = new WorldStateData();
    public PlayerSaveData player = new PlayerSaveData();
}
```

**Updated with WorldType field:**

```csharp
using Data.WorldTypes;

[Serializable]
public class WorldSaveData
{
    public int version = 1;
    public string worldName;
    public int seed;

    // Defaults to Legacy (0) when field is absent in old JSON files.
    // JSON deserialization of an int-backed enum to a byte-backed enum defaults to 0
    // when the field is missing — this is the desired behavior for backwards compatibility.
    public WorldTypeID worldType;

    public long creationDate;
    public long lastPlayed;
    public WorldStateData worldState = new WorldStateData();
    public PlayerSaveData player = new PlayerSaveData();
}
```

> **Important:** `WorldTypeID.Legacy = 0` means older JSON files that lack the `worldType` field will deserialize safely to `Legacy` by default. However, per the project's migration policy (lazy runtime migrations are discouraged), we still implement a formal migration step to
> explicitly inject the field.

### 6.2. `WorldLaunchState` Update

The current `WorldLaunchState` is at `Assets/Scripts/Data/WorldLaunchState.cs`:

```csharp
// Current code
namespace Data
{
    public static class WorldLaunchState
    {
        public static string WorldName = "New World";
        public static int Seed = 0;
        public static bool IsNewGame = true;
    }
}
```

**Updated:**

```csharp
using Data.WorldTypes;

namespace Data
{
    /// <summary>
    /// Static container for passing world configuration from the Main Menu to the Game Scene.
    /// </summary>
    public static class WorldLaunchState
    {
        public static string WorldName = "New World";
        public static int Seed = 0;
        public static bool IsNewGame = true;
        public static WorldTypeID SelectedWorldType = WorldTypeID.Standard; // New worlds default to the fast path
    }
}
```

### 6.3. Migration Strategy (`v3 → v4`)

The current save version is `3` (see `SaveSystem.cs:14`). The existing migration chain is:

- `v1 → v2`: `MigrationV1ToV2RegionRepack` (fixed region file layout)
- `v2 → v3`: `MigrationV2ToV3RestoreLighting` (restored lighting for empty sections)

We add a new step:

**File Location:** `Assets/Scripts/Serialization/Migration/Steps/Migration_v3_to_v4_WorldTypes.cs`

* **Action:** Parses the old `level.dat` JSON, explicitly injects `"worldType": 0` (Legacy), and ensures the JSON is saved with version `4`.
* **Note:** `SaveSystem.CURRENT_VERSION` is updated from `3` to `4`.

```csharp
namespace Serialization.Migration.Steps
{
    public class MigrationV3ToV4WorldTypes : WorldMigrationStep
    {
        public override int SourceWorldVersion => 3;
        public override int TargetWorldVersion => 4;
        public override string Description => "Adding World Type metadata";
        public override string ChangeSummary => "Assigns the Legacy world type to existing worlds.";

        public override string MigrateLevelDat(string oldJson)
        {
            // Parse, inject worldType: 0, bump version to 4, re-serialize.
            // Implementation uses Unity's JsonUtility or manual string injection.
            var data = UnityEngine.JsonUtility.FromJson<WorldSaveData>(oldJson);
            data.worldType = Data.WorldTypes.WorldTypeID.Legacy;
            data.version = TargetWorldVersion;
            return UnityEngine.JsonUtility.ToJson(data, true);
        }
    }
}
```

Register in `MigrationManager.cs` (line 23-28):

```csharp
private readonly List<WorldMigrationStep> _steps = new List<WorldMigrationStep>
{
    new MigrationV1ToV2RegionRepack(),
    new MigrationV2ToV3RestoreLighting(),
    new MigrationV3ToV4WorldTypes(),  // NEW
};
```

---

## 7. Execution Plan & Migration Steps

### Phase 1: Preparation & Asset Protection (Non-Breaking)

1. **FastNoiseLite is already ported** at `Assets/Scripts/Libraries/FastNoiseLite.cs` (namespace `Libraries`). See Section 9 for audit findings and recommended fixes.
   > **Gate (confirmed against source):** `FastNoiseLite` is **72 bytes**, fully blittable (18 fields × 4 bytes). All lookup tables live in a pinned `SharedStatic<LookupPointers>` via `GCHandle` — they are not struct fields. Pass-by-value is confirmed. Add `using Libraries;` to
   all consuming files.

2. **Remove No-Op Attribute:** Remove `[BurstCompile]` from the `FastNoiseLite` struct declaration (line 13). `[BurstCompile]` on a plain struct does nothing — it only has effect on `IJob*` structs and static methods. It misleads readers into thinking the struct itself is
   compiled by Burst.

3. **Protect Legacy Assets (CRITICAL):** Rename `BiomeAttributes` (at `Assets/Scripts/BiomeAttributes.cs`) to `LegacyBiomeAttributes : BiomeBase` and move to `Assets/Scripts/Legacy/LegacyBiomeAttributes.cs`. **You MUST add the following attribute to the new class signature —
   without it, Unity loses script references on all existing `.asset` files and all biome data is silently nullified:**
   ```csharp
   [UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, "Assembly-CSharp", "BiomeAttributes")]
   public class LegacyBiomeAttributes : BiomeBase { ... }
   ```
   After renaming, run **Assets → Reimport All** to confirm all biome assets upgrade cleanly.

4. Create a custom `Editor` script at `Assets/Scripts/Legacy/Editor/LegacyBiomeAttributesEditor.cs` that sets `GUI.enabled = false` in `OnInspectorGUI()`. This makes legacy biome assets visually read-only in the Inspector, preventing accidental modification.

5. Create the following files in `Data.WorldTypes`:
    - `BiomeBase.cs`
    - `WorldTypeDefinition.cs` (including `WorldTypeID` enum)
    - `WorldTypeRegistry.cs`

6. Create `IChunkGenerator.cs` in `Jobs.Generators` (including the `ExpandFlora()` method per Section 2.2).

7. Add `WorldTypeID` to `WorldSaveData` (in `Serialization/SaveDataTypes.cs`) and `WorldLaunchState` (in `Data/WorldLaunchState.cs`) per Section 6.

### Phase 2: Legacy Isolation & Safe Serialization (The Split)

1. **Move and rename generation code to `Legacy/`:**
    - `WorldGen.cs` → `Legacy/LegacyWorldGen.cs` (rename class to `LegacyWorldGen`)
    - `ChunkGenerationJob.cs` → `Legacy/LegacyChunkGenerationJob.cs` (rename struct to `LegacyChunkGenerationJob`)
    - Add a prominent comment block to both files documenting the intentional preservation of the biome evaluation loop.

2. **Copy utility code to `Legacy/` and delete originals from main codebase:**
    - `Noise.cs` → copy to `Legacy/LegacyNoise.cs` (rename class to `LegacyNoise`). Update all references within the legacy module (`LegacyWorldGen`, `LegacyChunkGenerationJob`, `LegacyStructure`) to use `LegacyNoise` instead of `Noise`. **Delete** `Assets/Scripts/Noise.cs`.
    - `Structure.cs` → copy to `Legacy/LegacyStructure.cs` (rename class to `LegacyStructure`). Update internal calls from `Noise.Get2DPerlin` to `LegacyNoise.Get2DPerlin`. **Delete** `Assets/Scripts/Structure.cs`.

3. **Extract legacy job-data structs from shared `Data/JobData.cs`:**
    - Move the `BiomeAttributesJobData` struct to `Legacy/LegacyBiomeAttributesJobData.cs` (rename to `LegacyBiomeAttributesJobData`). Update all references within the legacy module.
    - Move the `LodeJobData` struct to `Legacy/LegacyLodeJobData.cs` (rename to `LegacyLodeJobData`). Rename the constructor parameter type from `Lode` to `LegacyLode`. Update all references within the legacy module.
    - Remove both `BiomeAttributesJobData` and `LodeJobData` from `Data/JobData.cs`.
    - **Note:** The `Lode` class (currently in `BiomeAttributes.cs`) has already been moved to `Legacy/LegacyBiomeAttributes.cs` as `LegacyLode` in step 3 of Phase 1. Add `[MovedFrom(true, null, "Assembly-CSharp", "Lode")]` to preserve serialized `.asset` references.

4. **Create `LegacyChunkGenerator : IChunkGenerator`** at `Assets/Scripts/Legacy/LegacyChunkGenerator.cs`. This class:
    - Owns `NativeArray<LegacyBiomeAttributesJobData>` and `NativeArray<LegacyLodeJobData>` (both moved from `JobDataManager`).
    - Retains the `LegacyBiomeAttributes[]` ScriptableObject array reference for flora min/max height lookup.
    - `ScheduleGeneration()` contains the job creation logic currently at `WorldJobManager.cs:44-80`.
    - `GetVoxel()` delegates to `LegacyWorldGen.GetVoxel()`.
    - `ExpandFlora()` resolves the correct biome at the mod position using `LegacyNoise.Get2DPerlin` for biome selection, then delegates to `LegacyStructure.GenerateMajorFlora()` with the correct per-biome `minHeight`/`maxHeight`. This fixes the current `_world.biomes[0]`
      hardcoded bug.
    - `Dispose()` disposes both NativeArrays.

5. **Update `World.cs`:**
    - Remove the `public BiomeAttributes[] biomes` field (line 33).
    - Update `World.GetHighestVoxel()` (at line 2578) to call `JobManager.GetVoxel()` instead of `WorldGen.GetVoxel()` directly.
    - Remove any remaining references to `WorldGen`, `Noise`, or `Structure`.

6. **Update `WorldJobManager.cs`:**
    - Change constructor to accept `WorldTypeDefinition` and `JobDataManager` (see Section 5).
    - Refactor `ScheduleGeneration()` to delegate to `_chunkGenerator.ScheduleGeneration()`.
    - Refactor flora expansion in `ProcessGenerationJobs()` to call `_chunkGenerator.ExpandFlora()` (see Section 5.1).
    - Remove direct references to `Structure`, `Noise`, `BiomeAttributes`, and `World.biomes`.

7. **Split `PrepareJobData()` (at `World.cs:1040`):**
    - `Awake()` calls a new `PrepareGlobalJobData()` that parses only BlockTypes and CustomMeshes (lines 1071-1147 of current `PrepareJobData`).
    - `StartWorld()` resolves `ActiveWorldType` and constructs `WorldJobManager`.
    - Remove `BiomesJobData` and `AllLodesJobData` from `JobDataManager` constructor and fields (currently at `JobDataManager.cs:11-12`).

8. Increment `SaveSystem.CURRENT_VERSION` from `3` to `4` (at `SaveSystem.cs:14`).

9. Create `Migration_v3_to_v4_WorldTypes.cs` and register it in `MigrationManager._steps` (at `MigrationManager.cs:23-28`).

10. **Verification Gate:** Confirm the game compiles, existing saves migrate gracefully to `Legacy`, and terrain generated from known seeds is bit-for-bit identical to pre-refactor output. Verify that no file in `Assets/Scripts/` (excluding `Assets/Scripts/Legacy/`) references
    any legacy type: `LegacyWorldGen`, `LegacyNoise`, `LegacyStructure`, `LegacyBiomeAttributes`, `LegacyBiomeAttributesJobData`, `LegacyLode`, or `LegacyLodeJobData`.

### Phase 3: The New Tech & UI Hookup

1. Create the following new files. Ensure `using Libraries;` is present in all files referencing `FastNoiseLite`.
    - `FastNoiseConfig.cs` (`namespace Jobs.Data`) — shared noise configuration struct.
    - `StandardLodeJobData.cs` (`namespace Jobs.Data`) — blittable lode struct with `FastNoiseConfig`.
    - `StandardBiomeAttributesJobData.cs` (`namespace Jobs.Data`) — blittable biome struct referencing `StandardLodeJobData` via index range.
    - `StandardBiomeAttributes.cs` (`namespace Data.WorldTypes`) — authoring ScriptableObject including `StandardLode` class.

2. Create `StandardChunkGenerationJob` with:
    - `[BurstCompile(FloatPrecision.Standard, FloatMode.Default)]`
    - `int2 ChunkPosition` for SIMD vectorization (replacing current `Vector2Int` at `ChunkGenerationJob.cs:22`)
    - `NativeQueue<VoxelMod>.ParallelWriter Modifications` output field (`VoxelMod` with `Vector3Int` is fully blittable — see Note in Section 4.2)
    - `Unity.Mathematics.Random` flora root detection per Section 4.3
    - All `Unity.Mathematics` types (`float3`, `int3`, `float2`) instead of `Vector3`, `Vector3Int`, `Vector2`
    - `Unity.Mathematics.math` functions instead of `Mathf` (per project rules in `repomix-instructions.md` Section 6)

3. Create `StandardChunkGenerator : IChunkGenerator`:
    - **Lookup warmup (CRITICAL):** Immediately after creating all `FastNoiseLite` instances, call `FastNoiseLite.Create(seed).GetNoise(0f, 0f)`. This forces the `Lookup` static constructor (`FastNoiseLite.cs:1892`) to fire and pin the gradient arrays via `GCHandle`. Without
      this, the `SharedStatic` pointers are null when the first worker thread executes `GradCoord`, resulting in a silent read from address 0 or a native crash with no Unity stack trace.
    - `Initialize()` allocates and owns both `NativeArray<StandardBiomeAttributesJobData>` and `NativeArray<StandardLodeJobData>`. The lode arrays are flattened across all biomes (mirroring the current `PrepareJobData` pattern at `World.cs:1044-1069`), with each biome's
      `LodeStartIndex` and `LodeCount` set accordingly. Each `StandardLode` is converted to `StandardLodeJobData` via its constructor, and its `FastNoiseConfig` is used to construct a `FastNoiseLite` instance for ore evaluation.
    - `ExpandFlora()` uses `Unity.Mathematics.Random` (seeded deterministically from position + world seed) for trunk height calculation. Flora structure logic (leaf/trunk placement patterns) can be reimplemented inline or in a new `StandardStructure` helper class — it does **not
      ** reference `LegacyNoise` or `LegacyStructure`.
    - `Dispose()` disposes both NativeArrays.
    - Wire all `FastNoiseConfig` cellular fields (`CellularDistanceFunction`, `CellularReturnType`, `CellularJitter`) to the corresponding `SetCellular*` calls when constructing `FastNoiseLite` instances.

4. Author new Standard Biome `ScriptableObjects`. Tune using `FastNoiseLite` APIs. Use `NoiseType.Cellular` for biome selection and configure `CellularJitter` to control boundary organicness.

5. **UI Update:** In `WorldSelectMenu.cs` (at `Assets/Scripts/UI/WorldSelectMenu.cs`), add a `public TMP_Dropdown worldTypeDropdown;` field. Inside `OnConfirmCreateClicked()` (line 279), map the dropdown's integer value:
   ```csharp
   // After line 290 (WorldLaunchState.IsNewGame = true;)
   WorldLaunchState.SelectedWorldType = (WorldTypeID)worldTypeDropdown.value;
   ```
   The Create World panel (`createPanel` at line 21) needs to be updated in the Unity scene to include the dropdown UI element.

---

## 8. Performance Expectations

| Metric            | Legacy (`Mathf.PerlinNoise`) | Standard (`FastNoiseLite` + Burst) | Expected Uplift    |
|:------------------|:-----------------------------|:-----------------------------------|:-------------------|
| **Compiler**      | Mono (Managed)               | Burst (Native / SIMD)              | N/A                |
| **Noise Exec**    | Managed virtual call         | Inlined math intrinsics            | **~5x–8x faster**  |
| **Vectorization** | None                         | Burst-optimized scalar math*       | Moderate–High      |
| **GC Alloc**      | Minor per-frame overhead     | **Zero**                           | Absolute stability |

*\*Note on Vectorization:* Burst heavily optimizes `FastNoiseLite`'s scalar math (constant folding, branch elimination, register allocation),
but true SIMD loop vectorization is limited when evaluating single coordinates per iteration — the noise algorithm's internal hashing and branching prevents the compiler from batching 4+ voxels into a single SIMD instruction.
This is still ~5–8x faster than managed code. For perfect SIMD scaling, a dedicated `float4`-native noise library would be required in the future (see Section 10.4.A).

---

## 9. FastNoiseLite Library Audit

The Burst-compatible `FastNoiseLite` port at `Assets/Scripts/Libraries/FastNoiseLite.cs` (2069 lines, namespace `Libraries`) has been reviewed. Overall assessment: **production-ready for integration**, with minor recommendations below.

### 9.1. Confirmed Properties

| Property            | Status        | Detail                                                                                            |
|---------------------|---------------|---------------------------------------------------------------------------------------------------|
| Struct size         | **72 bytes**  | 18 fields × 4 bytes (10 int/enum + 8 float). No padding needed.                                   |
| Blittable           | **Yes**       | All fields are value types (int, float, int-backed enums).                                        |
| Managed references  | **None**      | No classes, strings, delegates, or generics.                                                      |
| Burst-compatible    | **Yes**       | All operations use value types and unsafe pointers.                                               |
| SharedStatic usage  | **Correct**   | `SharedStatic<LookupPointers>` holds 4 `float*` pointers to pinned gradient/random vector arrays. |
| GCHandle pinning    | **Correct**   | 4 arrays pinned via `GCHandle.Alloc(array, GCHandleType.Pinned)` in `Lookup` static constructor.  |
| Aggressive inlining | **Excellent** | 20+ hot methods marked `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.                      |
| Factory pattern     | **Clean**     | `FastNoiseLite.Create(int seed = 1337)` with sensible defaults.                                   |

### 9.2. Recommendations

#### A. Remove `[BurstCompile]` from Struct Declaration (Low Priority)

**Location:** `FastNoiseLite.cs:13`  
**Issue:** `[BurstCompile]` on a plain struct (not an `IJob*`) is a no-op. It only affects job structs and static methods. Its presence is misleading.  
**Action:** Remove the attribute. The struct is Burst-compatible by virtue of being blittable — the attribute is not what makes it so.

#### B. Extract Duplicate Constants (Low Priority, Readability)

**Issue:** Constants like `SQRT3`, `F2`, and `R3` are redefined locally within multiple methods (e.g., `TransformNoiseCoordinate`, `SingleOpenSimplex2`, domain warp methods).  
**Action:** Extract to private struct-level constants for clarity:

```csharp
private const float SQRT3 = 1.7320508075688772935274463415059f;
private const float F2 = 0.5f * (SQRT3 - 1);
private const float R3 = 2.0f / 3.0f;
```

**Risk:** None. Compiler already optimizes these identically. Pure readability improvement.

#### C. GCHandle Lifetime (No Action Needed)

**Issue:** The 4 `GCHandle` objects in `Lookup` (lines 1887-1890) are never freed with `.Free()`.  
**Assessment:** This is intentional and correct. The `Lookup` class is static and lives for the entire application lifetime. Freeing handles would cause Burst worker threads to access invalid memory. No action needed.

#### D. Seed Overflow in Fractal Loops (No Action Needed)

**Issue:** Fractal methods increment `seed++` per octave (e.g., line 624). If `mSeed` is `int.MaxValue`, this overflows.  
**Assessment:** Extremely unlikely in practice (max 16 octaves). C# `unchecked` integer arithmetic wraps safely. The original FastNoiseLite C++ library has the same behavior. No action needed.

#### E. Consider `readonly struct` (Low Priority, Future)

**Issue:** The struct is not marked `readonly`. Adding `readonly` would provide stronger guarantees about no-mutation and enable the compiler to avoid defensive copies when passed by `in` reference.  
**Consideration:** Many setter methods (`SetSeed`, `SetFrequency`, etc.) mutate the struct, so marking it `readonly` would require refactoring the API to use `Create()` with all parameters or a builder pattern. **Not recommended for this phase.** Revisit when/if the API
stabilizes.

### 9.3. Critical Integration Notes

1. **Lookup Table Warmup:** The `Lookup` static constructor fires on first access to any method that reads gradient/random data. The safest approach is to call `FastNoiseLite.Create(0).GetNoise(0f, 0f)` during `StandardChunkGenerator.Initialize()` on the main thread, before any
   jobs are scheduled. This guarantees the `SharedStatic<LookupPointers>` is populated.

2. **Thread Safety:** The struct is pass-by-value. Each job thread gets its own copy. The `SharedStatic` lookup tables are read-only after initialization. No synchronization is needed.

3. **Seed Strategy:** Use the factory method with a base seed, then configure via setters. For per-biome variation, use `FastNoiseConfig.SeedOffset` added to the base seed:
   ```csharp
   var noise = FastNoiseLite.Create(baseSeed + config.SeedOffset);
   noise.SetFrequency(config.Frequency);
   // ... etc
   ```

---

## 10. Cross-Reference: `WORLD_GENERATION_PERFORMANCE_TODOS.md`

This section maps each item from the Performance TODOs document to its status relative to this design.

### 10.1. Making `ChunkGenerationJob` Burst-Compatible

| TODO Item                                         | Status         | Notes                                                                                                                                                                                                                                                 |
|---------------------------------------------------|----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Step 1:** Choose Burst-compatible noise library | **Done**       | `FastNoiseLite` ported at `Assets/Scripts/Libraries/FastNoiseLite.cs`. See Section 9 for audit.                                                                                                                                                       |
| **Step 2:** Create `BurstNoise` abstraction layer | **Superseded** | This design passes `FastNoiseLite` by value directly into the job (72 bytes, L1 cache-friendly). A static wrapper adds an unnecessary indirection layer. The `FastNoiseConfig` struct (Section 4.1) serves as the authoring-side abstraction instead. |
| **Step 3:** Pass noise state via job data         | **Addressed**  | `StandardChunkGenerationJob` (Section 4.2) accepts `FastNoiseLite GlobalCaveNoise` by value. Per-biome noise instances are constructed from `FastNoiseConfig` during `Initialize()` and baked into `StandardBiomeAttributesJobData`.                  |
| **Step 4:** Refactor `WorldGen.GetVoxel`          | **Addressed**  | Legacy `WorldGen.GetVoxel` is preserved as-is (renamed to `LegacyWorldGen`). New generation logic lives inline in `StandardChunkGenerationJob.Execute()`, using `Unity.Mathematics` types throughout.                                                 |

### 10.2. Algorithmic Optimizations

| TODO Item                                | Status                          | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
|------------------------------------------|---------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **A. Heightmap Early Exit ("Sky Skip")** | **Addressed by Section 12.1.A** | The "Density Band" pattern (Section 12.1.A) subsumes this optimization. A cheap 2D terrain height is calculated first, then 3D noise is only evaluated in the band `[terrainHeight - CAVE_DEPTH .. terrainHeight + OVERHANG_HEIGHT]`. Blocks outside the band are filled without any noise evaluation (~75% of the column). This is strictly better than the original TODO's proposal because it also enables caves and overhangs, not just a sky skip. |
| **B. Pre-calculated Biome Map**          | **Addressed by Section 4.4**    | The Standard path uses Cellular noise for biome assignment, which is a single 2D evaluation per column — effectively the same as a pre-calculated biome map but without the separate job overhead. If biome blending is added later, a separate "Biome Job" pass becomes necessary.                                                                                                                                                                     |

### 10.3. Architectural Improvements

| TODO Item                                   | Status                  | Notes                                                                                                                                                                                                                                                                                                                                                                     |
|---------------------------------------------|-------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **A. Job Chaining (Internal Dependencies)** | **Deferred**            | The current `WorldJobManager` uses `Update()` polling (via `ProcessGenerationJobs()`, `ProcessMeshJobs()`, `ProcessLightingJobs()`). This pattern works and is not a bottleneck. Job chaining can be added later as an optimization to `StandardChunkGenerator.ScheduleGeneration()` if profiling shows main-thread polling overhead.                                     |
| **B. Deferred Structure Generation**        | **Partially Addressed** | Trees are already deferred to main thread via `NativeQueue<VoxelMod>.ParallelWriter` (current code at `ChunkGenerationJob.cs:47,104`). The TODO's "Decoration Pass" (waiting for neighbors before placing) remains a future enhancement. The current approach of queuing VoxelMods works correctly — it just causes main-thread spikes in `Structure.GenerateMajorFlora`. |

### 10.4. Micro-Optimizations

| TODO Item                   | Status                                   | Notes                                                                                                                                                                                                                                                                                                                                       |
|-----------------------------|------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **A. SIMD / Vectorization** | **Automatically handled**                | Burst auto-vectorizes loops using AVX2/SSE4 when `Unity.Mathematics` types are used. The `StandardChunkGenerationJob` uses `int2`, `int3`, `float2`, `float3` throughout, enabling Burst's optimizer. Manual `float4` batching is unnecessary unless profiling shows otherwise.                                                             |
| **B. Look Up Tables (LUT)** | **Already implemented in FastNoiseLite** | The `FastNoiseLite` library uses pre-computed gradient and random vector lookup tables pinned via `SharedStatic` (see `FastNoiseLite.cs:1870-1978`). No additional LUTs are needed for the noise calculations. The TODO's mention of `Mathf.Sin`/`cos` for biome blending is eliminated by the Cellular noise biome strategy (Section 4.4). |

---

## 11. Extensibility Analysis

This section documents why the new architecture is significantly more flexible than the legacy system, and where the extensibility surfaces are.

### 11.1. Three Layers of Flexibility

The new system's extensibility comes from three layers stacking on each other:

**Layer 1: FastNoiseLite Noise Primitives**

The library provides a combinatorial explosion of noise capabilities. The legacy system had exactly **one** primitive: `Mathf.PerlinNoise` (2D only, no fractals, no cellular, no domain warp).

| Dimension             | Legacy (`Mathf.PerlinNoise`)                               | Standard (`FastNoiseLite`)                                                             |
|-----------------------|------------------------------------------------------------|----------------------------------------------------------------------------------------|
| **Noise Types**       | Perlin only                                                | OpenSimplex2, OpenSimplex2S, Cellular, Perlin, ValueCubic, Value                       |
| **Fractal Modes**     | None (manual octave stacking possible but not implemented) | None, FBm, Ridged, PingPong, DomainWarpProgressive, DomainWarpIndependent              |
| **Cellular Distance** | N/A                                                        | Euclidean, EuclideanSq, Manhattan, Hybrid                                              |
| **Cellular Return**   | N/A                                                        | CellValue, Distance, Distance2, Distance2Add, Distance2Sub, Distance2Mul, Distance2Div |
| **Domain Warp**       | N/A                                                        | OpenSimplex2, OpenSimplex2Reduced, BasicGrid (2D and 3D)                               |
| **3D Noise**          | Faked via 6× 2D Perlin averages (`Noise.Get3DPerlin`)      | Native 3D with `GetNoise(x, y, z)` — true volumetric                                   |
| **3D Rotation**       | N/A                                                        | None, ImproveXYPlanes, ImproveXZPlanes                                                 |
| **Burst Compatible**  | No                                                         | Yes — fully inlined, SIMD auto-vectorized                                              |

**Layer 2: `FastNoiseConfig` as Composable Building Blocks**

Because `FastNoiseConfig` is a blittable struct, each feature can have its own independent noise configuration. The legacy system used a single `scale`/`threshold`/`offset` triple per feature (biome weight, terrain height, lode).
The Standard system can define separate noise configs for any aspect:

- **Per-biome terrain shape:** `StandardBiomeAttributesJobData.TerrainNoiseConfig`
- **Biome selection:** `StandardBiomeAttributesJobData.BiomeWeightNoiseConfig`
- **Per-lode ore pattern:** `StandardLodeJobData.NoiseConfig`
- **Cave carving:** `StandardChunkGenerationJob.GlobalCaveNoise` (already in the design)
- **Any future feature:** Just add another `FastNoiseConfig` field to the relevant job data struct

Each `FastNoiseConfig` can be independently tuned in the Inspector as a ScriptableObject field, without affecting other noise layers.

**Layer 3: `IChunkGenerator` Strategy Pattern**

New world types can be added without modifying existing ones. The generator owns its entire pipeline — biome data, lode data, noise instances, flora expansion, and synchronous voxel query. This means:

- An "Amplified" type could subclass or wrap `StandardChunkGenerator` with scaled terrain parameters.
- A "Far Lands" type could use a completely different terrain algorithm (e.g., extreme domain warp).
- A "Flat" type could skip noise entirely and return a fixed height.
- Each world type can define its own `BiomeBase` subclass with arbitrarily different fields.

### 11.2. Legacy vs. Standard Capability Comparison

| Terrain Feature            | Legacy                                                     | Standard (Phase 3 Initial)                                                                   | Standard (Post-Phase 3, Unlocked by Architecture)                  |
|----------------------------|------------------------------------------------------------|----------------------------------------------------------------------------------------------|--------------------------------------------------------------------|
| Heightmap terrain          | 2D Perlin weighted blend                                   | 2D Simplex/Perlin per biome                                                                  | Domain-warped continental heightmaps                               |
| Caves & Overhangs          | None (pure 2D heightmap)                                   | Density Band pattern (Section 12.1.A) — low effort, recommended for initial Phase 3 delivery | Ridged fractal caves, worm caves via domain warp, floating islands |
| Biome selection            | Weighted Perlin per biome (O(N) per column)                | Cellular Voronoi (O(1) per column)                                                           | Cellular distance blending, temperature/humidity maps              |
| Ore veins                  | 3D Perlin threshold (blob-shaped)                          | 3D FastNoiseLite threshold via `StandardLodeJobData.NoiseConfig`                             | Cellular vein patterns, depth-density curves                       |
| Rivers                     | None                                                       | Not in initial scope                                                                         | Domain warp path carving, Cellular distance channels               |
| Erosion                    | None                                                       | Not in initial scope (noise-based E.1 is low effort add-on)                                  | True hydraulic simulation (E.2)                                    |
| Surface decoration         | Perlin zone + placement checks                             | `Unity.Mathematics.Random` per column                                                        | Multi-pass decoration with separate noise layers                   |
| Cross-platform determinism | Not guaranteed (`Mathf.PerlinNoise` is platform-dependent) | `FloatMode.Default` ensures identical output                                                 | Guaranteed by Burst strict mode                                    |

### 11.3. Key Extensibility Points

These are the specific places where new features can be added with minimal architectural change:

1. **Adding a noise layer to terrain:** Add a `FastNoiseConfig` field to `StandardBiomeAttributesJobData` + `StandardBiomeAttributes`. Read it in `StandardChunkGenerationJob.Execute()`. No interface changes needed.

2. **Adding a new lode feature:** Add a field to `StandardLode` + `StandardLodeJobData`. Read it in the lode evaluation loop inside `StandardChunkGenerationJob.Execute()`. No interface changes needed.

3. **Adding a new world type:** Create a new `IChunkGenerator` implementation + job struct. Register in `WorldJobManager`'s factory switch. Add a new `WorldTypeID` enum value. Add biome `ScriptableObject` subclass if needed. No changes to existing generators.

4. **Adding a new structure / flora type:** author a `CompositeStructureTemplate` and add it as a pool entry on the biome — `StandardChunkGenerator.ExpandStructure()` resolves the marker's `PoolEntryIndex` against the flattened pool, so no `switch` arm is added per type. The detection logic in the job stays the same (it queues a `StructureSpawnMarker`). No changes to the `IChunkGenerator` interface or `WorldJobManager`. *(Pre-supersession this was "add a case to `ExpandFlora()`" — see the note in §2.2.)*

5. **Adding a new generation pass:** Add a new `NativeArray` output to `GenerationJobData` (or chain a second job in `ScheduleGeneration()`). `ProcessGenerationJobs()` reads the new output. The interface doesn't change — `GenerationJobData` is the output contract.

---

## 12. Enhancement Backlog — Migrated to `WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`

This document is an **Architecture** doc: it describes the world-generation architecture as built.
It used to also carry ~360 lines of forward-looking backlog here (§12 world-generation ideas, §13
editor-tooling ideas, §15 the assembly-definition boundary), which is Design-doc content. On
**2026-08-17** that backlog moved to
[`../../Design/WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](../../Design/WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md),
the live worldgen feature backlog, where it is tracked under `TF-*` IDs alongside the rest.

**Every sketch was status-checked against code before moving**, because a large share of it had
quietly shipped in the two years of worldgen work since it was written — importing that verbatim
into a live backlog would have re-opened finished features. The table below is the full disposition,
and it is deliberately kept here so that the `§12.x` / `§13.x` citations other documents already
make (for example this report's TF-5/TF-6, TF-7 and TF-10 entries) still resolve to something
meaningful.

| Old §                    | Sketch                                            | Disposition                                                                                                                                                            |
|--------------------------|---------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **12.1.A**               | 3D density field (caves, overhangs, arches)       | ✅ **Shipped** — as a full 3D volumetric density pipeline plus dedicated worm carvers, not the proposed narrow "density band". See [PROCEDURAL_TERRAIN_GENERATION.md](PROCEDURAL_TERRAIN_GENERATION.md) and [CAVE_GENERATION.md](CAVE_GENERATION.md). The per-biome `CaveDepth`/`OverhangHeight` band parameters were never adopted and do not exist. |
| **12.1.B**               | Domain-warped terrain                             | ✅ **Shipped** — per-biome density warp + cave warp noises (`StandardChunkGenerator._biomeDensityWarpNoises`, `_caveWarpNoises`).                                        |
| **12.1.C**               | Continental landmass scale                        | ✅ **Shipped** — as the Multi-Noise **continentalness** channel with per-biome `BurstSpline` curves, not a height multiplier.                                            |
| **12.1.D**               | River carving (cellular distance field)           | ⏸️ **Open — owned by `TF-7`** (rivers). That entry already cites this sketch.                                                                                           |
| **12.1.E.1**             | Noise-based "fake" erosion                        | ⛔ **Superseded** — shipped differently, as the Multi-Noise **erosion** channel with per-biome splines, rather than subtracting ridged noise from the heightmap.          |
| **12.1.E.2**             | True hydraulic erosion simulation                 | ⏸️ **Open — migrated as `TF-15`.**                                                                                                                                      |
| **12.2.A**               | Cellular vein patterns for lodes                  | ✅ **Available with no code change** — set a lode's noise config to `Cellular` + `Distance2Sub`/`Distance2Div`. Inspector tuning, so not a backlog item.                  |
| **12.2.B**               | Depth-weighted lode density                       | ⏸️ **Open — migrated as `TF-16`** (with 12.2.C).                                                                                                                        |
| **12.2.C**               | Multi-block veins                                 | ⏸️ **Open — migrated as `TF-16`.**                                                                                                                                      |
| **12.3.A**               | Biome-aware flora placement                       | ✅ **Shipped** — flora zones + weighted major/minor structure pools on `StandardBiomeAttributes`, flattened into `StructurePoolEntryJobData`.                             |
| **12.3.B**               | Multi-structure flora types                       | ✅ **Shipped** — superseded in shape: structures are authored as `CompositeStructureTemplate` pool entries and expanded via `IChunkGenerator.ExpandStructure`, so adding one is authoring work, not a new `switch` case (see §2.2). |
| **12.3.C**               | Neighbor-aware (deferred) decoration pass         | ⏸️ **Open — owned by `TF-10`** (multi-piece / large structures). That entry already cites this sketch.                                                                   |
| **12.4** Amplified       | Amplified world type                              | ⏸️ **Open — owned by `TF-5`.** `WorldTypeID.Amplified = 2` is reserved but unimplemented.                                                                                |
| **12.4** Far Lands       | Far Lands world type                              | ⏸️ **Open — owned by `TF-6`.**                                                                                                                                          |
| **12.4** Flat/Void/Noise | Flat/Creative, Void, Custom Noise Playground      | ⏸️ **Open — migrated as `TF-17`.**                                                                                                                                      |
| **13.1**                 | Noise preview inspector                           | ✅ **Shipped** — as the `WorldGenPreviewWindow` **Noise Channels** tab (+ `NoisePreviewJob`), i.e. a window tab rather than the proposed `PropertyDrawer`.                |
| **13.2**                 | Biome map visualizer                              | ✅ **Shipped** — `WorldGenPreviewWindow` **Biome Editor** / **World Blending** tabs, plus the runtime minimap's `TerrainDebugRenderMode` (`IChunkGenerator.EvaluateTerrainDebugPixels`). |
| **13.3**                 | World type comparison tool (split view)           | ⏸️ **Open — migrated as `TF-18`.** The existing `WorldType` tab *edits* one definition; it cannot render two side by side.                                               |
| **13.4**                 | Lode distribution preview                         | ✅ **Shipped** — `WorldGenPreviewWindow` **Cross Section** tab, which has a `Lodes` toggle.                                                                              |
| **13.5**                 | Seed browser                                      | ⏸️ **Open — migrated as `TF-18`** (with 13.3).                                                                                                                          |
| **15**                   | `Legacy.asmdef` boundary                          | ⏸️ **Open — migrated as `TF-19`.** Legacy isolation is still folder convention (§2.3), not compile-enforced.                                                             |

**What was dropped rather than moved.** The sketch prose for the ✅/⛔ rows above — including
§12.1.A's noise-type→cave/overhang style tables and §12.1.E.1's erosion pseudocode — was not
carried anywhere: it describes designs the engine either implemented differently or did not take,
and the Architecture docs named in each row now own the as-built behavior. Recover it from git
history (`git log -- "$THIS_FILE"`, versions ≤ v2.5) if a future item needs the reasoning.

---

## 13. Resolved Questions

1. **Biome Blending:** The initial Standard implementation uses hard Voronoi boundaries. Smooth blending is a separate, future enhancement that would be done as part of a full biome system overhaul (temperature/humidity maps,
   cellular distance field interpolation, cross-biome gradient transitions). Tracked in Section 12 as a future improvement — not a blocker for Phase 3.

2. **World Type UI:** The Create World panel in `WorldSelectMenu.cs` needs a `TMP_Dropdown` for world type selection (see Phase 3 Step 5 and Appendix A.2).
   The world type should also be displayed in the existing World Info screen (`WorldSelectMenu.OnInfoClicked()` at `WorldSelectMenu.cs:179`, which already shows world metadata via `WorldInfoUtility.FetchWorldInfoAsync()`).
   This requires reading `WorldSaveData.worldType` from `level.dat` and mapping the `WorldTypeID` to `WorldTypeDefinition.DisplayName` via the registry. Consider also showing it in `WorldListItem.cs` as a subtitle or badge next to the seed.

---
---

## Appendix A: Implementation Notes (Post-Review)

These notes were identified during the final review cycle. They do not change the architecture but address concrete implementation pitfalls to watch for during each phase.

### A.1. `VoxelMod.ImmediateUpdate` `bool` Blittability (Phase 1 — CRITICAL)

While `Vector3Int` is confirmed blittable (Section 4.2), the `VoxelMod` struct at `Data/VoxelMod.cs` also contains:

```csharp
public bool ImmediateUpdate;
```

Per the project's own `BURST_COMPILER_GUIDE.md` (Rule 2), standard C# `bool` has an undefined memory layout and is **not** inherently blittable in Burst. The legacy `ChunkGenerationJob` was never Burst-compiled, so `NativeQueue<VoxelMod>` was permitted.
Once `StandardChunkGenerationJob` uses `[BurstCompile]`, Burst will throw compiler error `BC1063` on the `NativeQueue<VoxelMod>.ParallelWriter` field.

**Fix (apply during Phase 1, before any Burst job references `VoxelMod`):**

```csharp
[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)]
public bool ImmediateUpdate;
```

This explicitly defines `bool` as a 1-byte unsigned integer in memory, making the struct fully blittable. The fix is backwards-compatible — it does not change serialization behavior or affect the legacy path.

> **Note:** The existing `VoxelMod` already uses `[MarshalAs]` on other fields in the codebase (see `BlockTypeJobData` at `Data/JobData.cs:139-140` for the pattern). This is a known project convention.

### A.2. `TMP_Dropdown` World Type Mapping Safety (Phase 3)

Section 7 Phase 3 Step 5 maps the UI dropdown to the enum via direct cast:

```csharp
WorldLaunchState.SelectedWorldType = (WorldTypeID)worldTypeDropdown.value;
```

`TMP_Dropdown.value` returns a 0-indexed `int` based on the **order of options in the Unity Inspector**. Because `WorldTypeID` explicitly assigns `Legacy = 0` and `Standard = 1`, this cast works correctly **only if the dropdown options are ordered identically** (Option 0 =
Legacy, Option 1 = Standard).

If a designer later reorders or alphabetizes the dropdown list, the cast silently maps to the wrong world type.

**Mitigations (pick one):**

- Add a comment on the UI prefab warning against reordering.
- Validate in `OnConfirmCreateClicked()` with an assertion: `Debug.Assert(worldTypeDropdown.options[0].text == "Legacy")`.
- Use a lookup array instead of a direct cast: `private static readonly WorldTypeID[] DropdownMapping = { WorldTypeID.Legacy, WorldTypeID.Standard };`
