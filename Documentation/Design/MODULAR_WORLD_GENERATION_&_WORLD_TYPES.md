# Design Document: Modular World Generation & World Types

**Version:** 1.0
**Date:** 2026-02-26  
**Status:** Approved for Implementation  
**Target:** Unity 6.3 (Mono Backend)  
**Context:** Decoupling legacy `Mathf.PerlinNoise` generation from a new `[BurstCompile]` `FastNoiseLite` generation pipeline via a modular "World Type" architecture.

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

* `JobDataManager` is now strictly for *world-type-agnostic* data (e.g., BlockTypes, Custom Meshes). Biomes and Lodes are removed from it.
* The `IChunkGenerator` must provide a fallback for synchronous main-thread voxel queries (used by `World.GetHighestVoxel` and `WorldEditor.cs`).

**File Location:** `Assets/Scripts/Jobs/Generators/IChunkGenerator.cs`

```csharp
using Data.JobData;
using Data.WorldTypes;
using Jobs.Data;
using UnityEngine;

namespace Jobs.Generators
{
    public interface IChunkGenerator
    {
        /// <summary>
        /// Injects explicit dependencies required for generation.
        /// Called once during WorldJobManager construction.
        /// </summary>
        /// <param name="seed">The deterministic world seed.</param>
        /// <param name="worldType">The ScriptableObject containing biome configuration.</param>
        /// <param name="globalJobData">World-type-agnostic data (Blocks, Meshes, etc.).</param>
        void Initialize(int seed, WorldTypeDefinition worldType, JobDataManager globalJobData);

        /// <summary>
        /// Schedules the generation job and returns a populated GenerationJobData struct.
        /// </summary>
        /// <param name="coord">The chunk coordinate to generate.</param>
        GenerationJobData ScheduleGeneration(ChunkCoord coord);

        /// <summary>
        /// Synchronous main-thread voxel query. Used by Editor tools and spawn-point logic.
        /// </summary>
        /// <param name="globalPos">The global voxel position to query.</param>
        /// <returns>The block ID at the given position.</returns>
        byte GetVoxel(Vector3Int globalPos);

        /// <summary>
        /// Disposes of any internal NativeArrays allocated during Initialize.
        /// </summary>
        void Dispose();
    }
}
```

### 2.3. Diverging the Jobs & Preserving Legacy Bugs

We will split the current `ChunkGenerationJob` into two distinct paths.

#### A. `LegacyWorldGen.cs` & `LegacyChunkGenerationJob.cs`

* **Refactor:** Rename `WorldGen.cs` to `LegacyWorldGen.cs` and move to `Scripts/Legacy/`.
* **Compiler:** Standard C# (No `[BurstCompile]`).
* **Preservation Note:** The current O(N²) biome noise evaluation (where `GetTerrainHeight` is recalculated for every Y step inside the column loop) **must be preserved exactly as-is**. Fixing it would alter the output and break legacy seed reproducibility. This will be
  explicitly commented in the legacy source.

#### B. `StandardChunkGenerator.cs` & `StandardChunkGenerationJob.cs`

* **Status:** Active / Default.
* **Compiler:** `[BurstCompile(FloatPrecision.Standard, FloatMode.Default)]` — `FloatMode.Default` ensures cross-platform math determinism for seeds, unlike `FloatMode.Fast`.
* **Behavior:** Highly optimized, branchless where possible, utilizing CPU vectorization.

---

## 3. Resolving the Lifecycle Timing & Disposal Conflict

Currently, `World.cs` initializes `JobDataManager` and `WorldJobManager` in `Awake()`, relying on biome data. We will split initialization and enforce strict, encapsulated disposal.

**Updates to `World.cs`:**

```csharp
[Header("World Configuration")]
public WorldTypeRegistry worldTypeRegistry;

// Set during StartWorld(). Read by any system that needs to know the active generation type.
public WorldTypeDefinition ActiveWorldType { get; private set; }

private void Awake()
{
    if (Instance is not null && Instance != this) Destroy(gameObject);
    else Instance = this;

    appSaveDataPath = Application.persistentDataPath;
    ChunkPool = new ChunkPoolManager(transform);

    // Parses BlockDatabase into NativeArrays (Custom Meshes, Textures, etc.)
    // DOES NOT parse Biomes anymore — that is the generator's responsibility.
    PrepareGlobalJobData();
}

private IEnumerator StartWorld()
{
    // ... Load Save Data & Settings ...

    // DETERMINE WORLD TYPE
    WorldTypeID typeToLoad = isNewGame
        ? WorldLaunchState.SelectedWorldType
        : loadedSaveData.worldType;

    // SAFE FALLBACK: Resolve any unsupported type IDs here, before the registry lookup.
    // This ensures the entire downstream pipeline (WorldTypeDefinition, IChunkGenerator.Initialize,
    // and all biome casts inside the generator) receives a fully valid, supported definition.
    // Doing this here rather than inside WorldJobManager prevents an InvalidCastException that
    // would occur if a StandardChunkGenerator tried to cast Amplified biomes to StandardBiomeAttributes.
    if (typeToLoad == WorldTypeID.Amplified)
    {
        Debug.LogWarning("[World] Amplified world type is not yet implemented. Falling back to Standard.");
        typeToLoad = WorldTypeID.Standard;
    }

    ActiveWorldType = worldTypeRegistry.GetWorldType(typeToLoad);

    // INITIALIZE JOB MANAGER & STRATEGY
    // Explicitly passes JobDataManager to avoid hidden order-of-operation contracts.
    JobManager = new WorldJobManager(this, ActiveWorldType, JobDataManager);

    // ... Proceed to LoadOrGenerateChunk ...
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
        public FastNoiseLite.FractalType FractalType;
        public int Octaves;
        public float Gain;
        public float Lacunarity;

        // Optional: only meaningful when NoiseType == Cellular
        public FastNoiseLite.CellularDistanceFunction CellularDistanceFunction;
        public FastNoiseLite.CellularReturnType CellularReturnType;
        public float CellularJitter;
    }
}
```

**File Location:** `Assets/Scripts/Jobs/Data/StandardBiomeAttributesJobData.cs`

```csharp
using Libraries;

namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of StandardBiomeAttributes.
    /// Constructed by StandardChunkGenerator.Initialize() from the ScriptableObject array.
    /// Lodes are flattened into a shared NativeArray&lt;LodeJobData&gt;, referenced by index range.
    /// </summary>
    public struct StandardBiomeAttributesJobData
    {
        public FastNoiseConfig TerrainNoiseConfig;
        public FastNoiseConfig BiomeWeightNoiseConfig;

        public float BaseTerrainHeight;
        public byte SurfaceBlockID;
        public byte SubSurfaceBlockID;

        public float MajorFloraPlacementThreshold;
        public byte MajorFloraIndex;

        // Index into the shared NativeArray<LodeJobData> owned by StandardChunkGenerator
        public int LodeStartIndex;
        public int LodeCount;
    }
}
```

**File Location:** `Assets/Scripts/Data/WorldTypes/StandardBiomeAttributes.cs`

```csharp
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

        public float BaseTerrainHeight;
        public byte SurfaceBlockID;
        public byte SubSurfaceBlockID;

        public float MajorFloraPlacementThreshold;
        public byte MajorFloraIndex;

        public Lode[] Lodes;
    }
}
```

### 4.2. Job Definition & Instantiation (Pass-by-Value & Vectorization)

`FastNoiseLite` is exactly **72 bytes** and entirely blittable (18 fields: 10 × `int`/`enum` + 8 × `float`). Lookup tables live in a pinned `SharedStatic<LookupPointers>` and are not part of the struct. We pass the noise state **by value** directly into the job — zero heap
allocation, maximum L1 cache locality.

`ChunkPosition` uses `int2` rather than `UnityEngine.Vector2Int` to keep the entire math pipeline within `Unity.Mathematics`, enabling Burst's SIMD auto-vectorization.

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
        [ReadOnly] public NativeArray<LodeJobData> AllLodes;

        // Passed by value (72 bytes). SharedStatic lookup tables are accessed via raw pointer,
        // shared across all worker threads without copying.
        public FastNoiseLite GlobalCaveNoise;

        #endregion

        #region Output Data

        [WriteOnly] [NativeDisableParallelForRestriction]
        public NativeArray<uint> OutputMap;

        [WriteOnly]
        public NativeArray<byte> OutputHeightMap;

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

### 4.3. Flora Placement in Burst (Deterministic Randoms)

`Mathf.PerlinNoise` cannot be called from Burst. We replace it with `Unity.Mathematics.Random`, seeded deterministically per-column.

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
    // VoxelMod accepts Vector3Int. This is a deliberate managed/unmanaged boundary point —
    // the VoxelMod struct predates this system and a broader refactor is out of scope here.
    Modifications.Enqueue(new VoxelMod(
        new Vector3Int(globalPos.x, globalPos.y, globalPos.z),
        biome.MajorFloraIndex
    ));
}
```

### 4.4. Standard Biome Blending Strategy

Unlike the legacy system which evaluated every biome's weight via Perlin noise per block, the `StandardChunkGenerationJob` will use **Voronoi/Cellular Noise** to assign discrete biome regions.

1. A `FastNoiseLite` instance configured for Cellular noise is evaluated at global X/Z to produce a cell value.
2. That value is mapped to a discrete biome index into the `Biomes` array.
3. **Note on Blending:** The initial implementation uses hard Voronoi cell boundaries with no cross-biome interpolation. `CellularJitter` controls how organic the cell boundaries appear but does **not** blend biome data — a column is assigned exactly one biome. Smooth gradient
   transitions are a future enhancement, requiring a separate biome-weight blending pass using cellular distance fields.

---

## 5. Generator Strategy Resolution & Encapsulation

`WorldJobManager` delegates scheduling to the active strategy, exposes a synchronous main-thread voxel query, and fully encapsulates all job disposal.

**File Location:** `Assets/Scripts/WorldJobManager.cs`

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
        // Unsupported types (Amplified) are expected to have been remapped by the caller (World.StartWorld).
        // Any WorldTypeID reaching this switch that has no concrete generator is a critical logic error.
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
    /// Used by WorldEditor and spawn-point logic.
    /// </summary>
    /// <param name="globalPos">The global voxel position to query.</param>
    /// <returns>The block ID at the given position.</returns>
    public byte GetVoxel(Vector3Int globalPos) => _chunkGenerator.GetVoxel(globalPos);

    /// <summary>
    /// Schedules a background job to generate voxel data for the given chunk coordinate.
    /// Returns immediately if a job is already running or if the chunk data is already populated.
    /// </summary>
    /// <param name="coord">The coordinate of the chunk to generate.</param>
    public void ScheduleGeneration(ChunkCoord coord)
    {
        // Guard 1: Don't schedule if a job is already running for this coord.
        if (generationJobs.ContainsKey(coord)) return;

        // Guard 2: Don't schedule if chunk data already exists and is populated (loaded from disk).
        Vector2Int chunkPos = new Vector2Int(coord.X * VoxelData.ChunkWidth, coord.Z * VoxelData.ChunkWidth);
        if (_world.worldData.Chunks.TryGetValue(chunkPos, out ChunkData data) && data.IsPopulated)
            return;

        GenerationJobData jobData = _chunkGenerator.ScheduleGeneration(coord);
        generationJobs.Add(coord, jobData);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Completes all active jobs, disposes their NativeArrays, and disposes
    /// the generator's internal NativeArrays.
    /// </summary>
    public void Dispose()
    {
        // Force-complete and dispose all tracked jobs
        foreach (var job in generationJobs.Values) { job.Handle.Complete(); job.Dispose(); }
        foreach (var (handle, meshData) in meshJobs.Values) { handle.Complete(); meshData.Dispose(); }
        foreach (var job in lightingJobs.Values) { job.Handle.Complete(); job.Dispose(); }

        generationJobs.Clear();
        meshJobs.Clear();
        lightingJobs.Clear();

        // Dispose the generator's internal NativeArrays (biomes, lodes)
        _chunkGenerator?.Dispose();
    }

    #endregion
}
```

---

## 6. Data & Serialization Integration

### 6.1. `WorldSaveData` Update

`WorldTypeID.Legacy = 0` means older JSON files that lack the `worldType` field will deserialize safely to `Legacy` by default — no migration logic is needed for this field specifically.

```csharp
[Serializable]
public class WorldSaveData
{
    public int version = 2; // Bumped to 2 via SaveSystem.CURRENT_VERSION
    public string worldName;
    public int seed;

    // Defaults to Legacy (0) when field is absent in old JSON files
    public WorldTypeID worldType;

    // ... existing fields unchanged ...
}
```

### 6.2. `WorldLaunchState` Update

```csharp
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

### 6.3. Migration Strategy (`v1 → v2`)

Replace the existing `MigrationV1ToV2Dummy` with the real world-type migration.

**File Location:** `Assets/Scripts/Serialization/Migration/Steps/MigrationV1ToV2WorldTypes.cs`

* **Action:** Parses the old `level.dat`, explicitly injects `"worldType": 0` (Legacy), and ensures the JSON is saved with version `2`.
* **Note:** `SaveSystem.CURRENT_VERSION` is updated from `1` to `2`.

---

## 7. Execution Plan & Migration Steps

### Phase 1: Preparation & Asset Protection (Non-Breaking)

1. Add `FastNoiseLite.cs` to `Assets/Scripts/Libraries/`.
   > **Gate (confirmed against source):** `FastNoiseLite` is **72 bytes**, fully blittable. All lookup tables live in a pinned `SharedStatic` — they are not struct fields. Pass-by-value is confirmed. Add `using Libraries;` to all consuming files.

2. **Remove No-Op Attribute:** Remove `[BurstCompile]` from the `FastNoiseLite` struct declaration. It does nothing on a plain struct and misleads readers.

3. **Protect Legacy Assets (CRITICAL):** Rename `BiomeAttributes` to `LegacyBiomeAttributes : BiomeBase`. **You MUST add the following attribute to the new class signature — without it, Unity loses script references on all existing `.asset` files and all biome data is silently
   nullified:**
   ```csharp
   [UnityEngine.Scripting.APIUpdating.MovedFrom(false, null, null, "BiomeAttributes")]
   public class LegacyBiomeAttributes : BiomeBase { ... }
   ```
   After renaming, run **Assets → Reimport All** to confirm all four biome assets upgrade cleanly.

4. Create a custom `Editor` script for `LegacyBiomeAttributes` that sets `GUI.enabled = false` in `OnInspectorGUI()`. This makes legacy biome assets visually read-only in the Inspector, preventing accidental modification.

5. Create the following files in `Data.WorldTypes`:
    - `BiomeBase.cs`
    - `WorldTypeDefinition.cs` (including `WorldTypeID` enum)
    - `WorldTypeRegistry.cs`

6. Create `IChunkGenerator.cs` in `Jobs.Generators`.

7. Add `WorldTypeID` to `WorldSaveData` and `WorldLaunchState` per Section 6.

### Phase 2: Refactoring & Safe Serialization (The Split)

1. Rename `WorldGen.cs` → `LegacyWorldGen.cs` and move to `Assets/Scripts/Legacy/`. Rename `ChunkGenerationJob` → `LegacyChunkGenerationJob`. Add a prominent comment block to both files documenting the intentional preservation of the `GetTerrainHeight` O(N²) evaluation bug.

2. Create `LegacyChunkGenerator : IChunkGenerator`. Update all call sites that previously used the `WorldGen` static class (including `World.GetHighestVoxel` and `WorldEditor.cs`) to call `JobManager.GetVoxel()` instead.

3. Split `PrepareJobData()` in `World.cs`:
    - `Awake()` calls a new `PrepareGlobalJobData()` that parses only BlockTypes and CustomMeshes.
    - `StartWorld()` resolves `ActiveWorldType` and constructs `WorldJobManager`.
    - Remove `BiomesJobData` and `AllLodesJobData` from `JobDataManager`.

4. Increment `SaveSystem.CURRENT_VERSION` from `1` to `2`.

5. Replace `MigrationV1ToV2Dummy` with `MigrationV1ToV2WorldTypes`.

6. **Verification Gate:** Confirm the game compiles, existing saves migrate gracefully to `Legacy`, and terrain generated from known seeds is bit-for-bit identical to pre-refactor output.

### Phase 3: The New Tech & UI Hookup

1. Create `FastNoiseConfig.cs` (`namespace Jobs.Data`) and `StandardBiomeAttributesJobData.cs` (`namespace Jobs.Data`). Create `StandardBiomeAttributes.cs` (`namespace Data.WorldTypes`). Ensure `using Libraries;` is present in all files referencing `FastNoiseLite`.

2. Create `StandardChunkGenerationJob` with:
    - `[BurstCompile(FloatPrecision.Standard, FloatMode.Default)]`
    - `int2 ChunkPosition` for SIMD vectorization
    - `NativeQueue<VoxelMod>.ParallelWriter Modifications` output field
    - `Unity.Mathematics.Random` flora logic per Section 4.3

3. Create `StandardChunkGenerator : IChunkGenerator`:
    - **Lookup warmup (CRITICAL):** Immediately after creating all `FastNoiseLite` instances, call `FastNoiseLite.Create(seed).GetNoise(0f, 0f)`. This forces the `Lookup` static constructor to fire and pin the gradient arrays via `GCHandle`. Without this, the `SharedStatic`
      pointers are null when the first worker thread executes `GradCoord`, resulting in a silent read from address 0 or a native crash with no Unity stack trace.
    - `Initialize()` allocates and owns both `NativeArray<StandardBiomeAttributesJobData>` and `NativeArray<LodeJobData>`. The lode arrays are flattened across all biomes (mirroring the legacy `PrepareJobData` pattern), with each biome's `LodeStartIndex` and `LodeCount` set
      accordingly.
    - `Dispose()` disposes both NativeArrays.
    - Wire all `FastNoiseConfig` cellular fields (`CellularDistanceFunction`, `CellularReturnType`, `CellularJitter`) to the corresponding `SetCellular*` calls when constructing `FastNoiseLite` instances.

4. Author new Standard Biome `ScriptableObjects`. Tune using `FastNoiseLite` APIs. Use `NoiseType.Cellular` for biome selection and configure `CellularJitter` to control boundary organicness.

5. **UI Update:** In `WorldSelectMenu.cs`, add `public TMP_Dropdown worldTypeDropdown;`. Inside `OnConfirmCreateClicked()`, map the dropdown's integer value to `WorldLaunchState.SelectedWorldType = (WorldTypeID)worldTypeDropdown.value;`.

---

## 8. Performance Expectations

| Metric            | Legacy (`Mathf.PerlinNoise`) | Standard (`FastNoiseLite` + Burst) | Expected Uplift    |
|:------------------|:-----------------------------|:-----------------------------------|:-------------------|
| **Compiler**      | Mono / IL2CPP (Managed)      | Burst (Native / SIMD)              | N/A                |
| **Noise Exec**    | Managed virtual call         | Inlined math intrinsics            | **~4x–8x faster**  |
| **Vectorization** | None                         | Auto-vectorized (AVX2/SSE4)        | High               |
| **GC Alloc**      | Minor per-frame overhead     | **Zero**                           | Absolute stability |
