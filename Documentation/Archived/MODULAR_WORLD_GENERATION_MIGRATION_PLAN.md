# Modular World Generation — Migration Plan (2026-04/05)  `[ARCHIVED]`

> **Archived:** 2026-08-17  
> **Reason:** This is the delivery record of the world-type refactor — the execution phases, the
> proposed-vs-current code diffs, and the post-review pitfall notes that guided the work in
> 2026-04/05. It was carried inside
> [`../Architecture/World Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md`](../Architecture/World%20Generation/MODULAR_WORLD_GENERATION_&_WORLD_TYPES.md),
> which lives in `Architecture/` and therefore promises *current state*. An Architecture doc carries
> no phase structure, so the plan-shaped sections were moved here rather than kept as an appendix.
> Kept as a historical record of how the refactor was sequenced and why.

> [!WARNING]
> **This document is frozen and is NOT current state.** Every "current code" block below describes
> the codebase as it stood *before* the refactor; every "updated"/"proposed" block describes the
> intended end state as understood in 2026-04, which later work has moved on from. For how world
> generation behaves today, read the Architecture doc linked above — plus
> [`../Architecture/World Generation/PROCEDURAL_TERRAIN_GENERATION.md`](../Architecture/World%20Generation/PROCEDURAL_TERRAIN_GENERATION.md)
> and [`../Architecture/World Generation/CAVE_GENERATION.md`](../Architecture/World%20Generation/CAVE_GENERATION.md).
> Do not patch this file to track code drift.

Section numbers below are the ones these sections held in the source document at the time of
archival (v2.6), so that historical references to "Section 7" / "Appendix A" still land.

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

## 13. Resolved Questions

1. **Biome Blending:** The initial Standard implementation uses hard Voronoi boundaries. Smooth blending is a separate, future enhancement that would be done as part of a full biome system overhaul (temperature/humidity maps,
   cellular distance field interpolation, cross-biome gradient transitions). Tracked in Section 12 as a future improvement — not a blocker for Phase 3.

2. **World Type UI:** The Create World panel in `WorldSelectMenu.cs` needs a `TMP_Dropdown` for world type selection (see Phase 3 Step 5 and Appendix A.2).
   The world type should also be displayed in the existing World Info screen (`WorldSelectMenu.OnInfoClicked()` at `WorldSelectMenu.cs:179`, which already shows world metadata via `WorldInfoUtility.FetchWorldInfoAsync()`).
   This requires reading `WorldSaveData.worldType` from `level.dat` and mapping the `WorldTypeID` to `WorldTypeDefinition.DisplayName` via the registry. Consider also showing it in `WorldListItem.cs` as a subtitle or badge next to the seed.

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

---

## Source-document Changelog (v2.0 – v2.5)

The revision history of the migration plan itself, retained here because it records why individual
plan decisions changed between drafts. The live document keeps its v2.6-onward history.

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
