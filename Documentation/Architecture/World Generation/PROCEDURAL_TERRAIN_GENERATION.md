# Procedural Terrain Generation

**Version:** 2.5 (Implemented — Cave Isolation Filter)  
**Date:** May 2026  
**Status:** Implemented  
**Target:** Unity 6.4 (Standard World Gen System / Burst Compiler)  
**Context:** Upgrading the 2D heightmap generation to a 3D volumetric density pipeline with Domain Warping and Multi-Noise (Continentalness, Erosion, Peaks & Valleys).

> [!NOTE]
> **Cave generation has its own authoritative doc.** This document covers terrain *shape*
> (multi-noise height, 3D density, domain warping, strata, lodes, the cave isolation filter,
> and the generation job structure). The cave *carving* system — worm carvers (trunk + local),
> zone attenuation, noise/mask seeking, Cheese/Noodle/Spaghetti modes — is described in
> [CAVE_GENERATION.md](CAVE_GENERATION.md). The cave-mode snippets in §1.4 and §4 below are a
> summary; CAVE_GENERATION.md is the source of truth for cave formulas and behavior.

---

## 1. Executive Summary

The legacy procedural terrain system uses a strict 2D heightmap ($y = f(x,z)$). This prevents the generation of complex overhangs, floating islands, and organic cave systems. By integrating modern techniques, we transition to a **Volumetric Density** model while maintaining strict Burst performance:

1. **Multi-Noise 2D Base (Minecraft C&C):** Terrain height is decoupled from a single noise map. Instead, we evaluate three independent noises (Continentalness, Erosion, Peaks & Valleys) mapped through **Data-Driven Splines** to determine a base terrain shape.
2. **3D Density Fields (GPU Gems 3):** The final surface is defined by a 3D density function: $Density = BaseHeight(x,z) - y + 3DNoise(x,y,z)$.
3. **Domain Warping (Iñigo Quílez):** The input coordinates of the 3D density noise (and optionally cave noises) are distorted using a secondary noise field ($p' = p + Warp(p)$), breaking up artificial grid-like patterns and simulating geological folding.
4. **Cave System:** Caves are carved into the volumetric terrain by a dedicated subsystem (Cheese, Spaghetti2D/3D, Noodle noise modes plus a two-tier trunk/local worm carver). That system has grown well beyond this document's scope and has its own authoritative doc — see **[CAVE_GENERATION.md](CAVE_GENERATION.md)**. This document covers only how the cave pass slots into the generation job (§4) and the cave isolation filter (§4.1).

---

## 2. Architectural Concepts

### 2.1. Multi-Noise Base Terrain (Continentalness, Erosion, P&V)

Relying on a single noise map creates predictable hills. We separate the macroscopic terrain shape into three 2D noise evaluations:

* **Continentalness:** Determines macro-scale landmasses vs. oceans. High values elevate the terrain globally.
* **Erosion:** Determines how weathered the terrain is. High erosion flattens terrain into plains or valleys; low erosion allows mountains to form.
* **Peaks & Valleys (PV):** Adds local high-frequency height variations.

*Burst Implementation:* We evaluate these three `FastNoiseLite` instances per column and map their outputs through a **`BurstSpline` struct** (baked from an `AnimationCurve` in the Editor at init time). This provides complete, code-free artist control over terrain shaping.

> [!NOTE]
> The legacy `terrainNoiseConfig` and `terrainAmplitude` fields have been **removed** from `StandardBiomeAttributes`. The legacy `_biomeTerrainNoises` array and the `[Obsolete]` `BiomeBlender` overload have also been removed. All biomes must configure their Multi-Noise fields. There is no runtime fallback to the legacy formula.

### 2.2. `BurstSpline` — Burst-Compatible Curve Evaluation

A fixed-size, blittable struct that bakes an `AnimationCurve` into a set of linear keyframes for fast evaluation inside Burst jobs.

```csharp
// Assets/Scripts/Jobs/Data/BurstSpline.cs [NEW]
/// <summary>
/// A Burst-compatible piecewise-linear curve baked from an AnimationCurve.
/// Fixed-size (max 16 keyframes) to remain a value type on the stack.
/// </summary>
public struct BurstSpline
{
    private const int MAX_KEYS = 16;

    /// <summary>Packed keyframe times (X) and values (Y).</summary>
    private unsafe fixed float _keys[MAX_KEYS * 2]; // [t0,v0, t1,v1, ...]
    private int _count;

    /// <summary>
    /// Bakes an AnimationCurve into this struct by sampling it at even intervals.
    /// Must be called on the main thread during initialization.
    /// </summary>
    public static BurstSpline FromAnimationCurve(AnimationCurve curve, int sampleCount = MAX_KEYS)
    {
        // Sample the curve at 'sampleCount' evenly-spaced points across its time range,
        // storing each (time, value) pair into the fixed buffer.
    }

    /// <summary>
    /// Evaluates the spline at input t using piecewise-linear interpolation.
    /// Burst-safe, zero allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Evaluate(float t) { /* binary search + lerp between adjacent keys */ }
}
```

### 2.3. 3D Density & The "Dynamic Density Band" Optimization

Evaluating 3D noise for all Y-levels of a chunk is too expensive. We use the **Dynamic Density Band** optimization:

1. Calculate the 2D `BaseHeight`.
2. Because the 3D noise output is bounded to `[-1, 1]` and scaled by `DensityAmplitude`, it cannot influence terrain outside `[BaseHeight - Amplitude, BaseHeight + Amplitude]`.
3. We compute explicit integer bounds using `math.ceil` to avoid off-by-one clipping:
   ```csharp
   int bandLow  = baseTerrainHeight - (int)math.ceil(biome.DensityAmplitude);
   int bandHigh = baseTerrainHeight + (int)math.ceil(biome.DensityAmplitude);
   ```
4. Voxels above the band are strictly Air; voxels below are strictly Solid.

> [!NOTE]
> **Biome Boundary Density Attenuation (`borderFade`):** The `BiomeBlender.CalculateBlendedTerrainHeight` method outputs a `borderFade` value (0.0 at a Voronoi cell boundary, 1.0 deep inside the biome). The generation job multiplies `DensityAmplitude * borderFade`, fading 3D density to zero near biome borders. This prevents cliff tearing at transitions where `DensityAmplitude` or noise configs differ. The fade uses the same `activeRadius`, per-biome `BlendCurve`, and `BlendWeight` as the height blending. Deep inside a biome, full 3D overhangs are
> preserved; at boundaries, terrain degrades to the smooth 2D blended heightmap.

### 2.4. Domain Warping

Using `FastNoiseLite.DomainWarp`, we offset the $(x, y, z)$ coordinates before feeding them into the 3D Density noise and (optionally) cave noises. This creates sweeping, overhanging cliffs and organic winding tunnels.

> [!NOTE]
> Domain warping requires a **dedicated `FastNoiseLite` instance** per warp source. The warp instance has its own `frequency`, `fractalType`, `octaves`, and `domainWarpAmp` — these are independent from the noise instance it distorts. In `StandardBiomeAttributes`, the `densityWarpConfig` is a separate `FastNoiseConfig` that fully configures the warp instance.

---

## 3. Architecture & Data Model Updates

### 3.1. Extending `FastNoiseConfig`

We expose Domain Warping parameters so that any `FastNoiseConfig` can optionally drive a `DomainWarp()` call.

```csharp
// Assets/Scripts/Jobs/Data/FastNoiseConfig.cs — additions after cellularJitter
        // --- NEW: Domain Warp Settings ---
        [Separator("Domain Warp Settings")]
        [Tooltip("Type of domain warp. Only relevant when this config drives a DomainWarp() call.")]
        public FastNoiseLite.DomainWarpType domainWarpType;

        [Tooltip("Strength/Amplitude of the coordinate distortion.")]
        public float domainWarpAmp;
```

### 3.2. Updating `FastNoiseFactory`

```csharp
// Assets/Scripts/Jobs/Generators/FastNoiseFactory.cs — additions before return
    noise.SetDomainWarpType(config.domainWarpType);
    noise.SetDomainWarpAmp(config.domainWarpAmp);
```

### 3.3. Updating Biome Attributes

```csharp
// Assets/Scripts/Data/WorldTypes/StandardBiomeAttributes.cs — new fields

        [Header("Terrain Shape (Multi-Noise)")]
        [Tooltip("Noise controlling macro landmass scale (Oceans vs Continents).")]
        public FastNoiseConfig continentalnessNoiseConfig;
        [Tooltip("Curve mapping Continentalness [-1, 1] to base height offset.")]
        public AnimationCurve continentalnessCurve;

        [Tooltip("Noise controlling weathering.")]
        public FastNoiseConfig erosionNoiseConfig;
        [Tooltip("Curve mapping Erosion [-1, 1] to height multiplier.")]
        public AnimationCurve erosionCurve;

        [Tooltip("Noise controlling localized hills and valleys.")]
        public FastNoiseConfig peaksAndValleysNoiseConfig;
        [Tooltip("Curve mapping P&V [-1, 1] to local amplitude.")]
        public AnimationCurve peaksAndValleysCurve;

        [Header("3D Density (Overhangs & Arches)")]
        public bool enable3DDensity;            // default false — opt-in per biome
        public FastNoiseConfig densityNoiseConfig;
        [Tooltip("Max height variation of 3D noise. Dynamically defines the Density Band.")]
        public float densityAmplitude = 15f;

        [Header("Domain Warping (Organic Distortion)")]
        public bool enableDensityWarp;          // default false — opt-in per biome
        public FastNoiseConfig densityWarpConfig;
```

> [!WARNING]
> **Multi-Noise `normalizeToZeroOne` Constraint:** The Continentalness, Erosion, and Peaks & Valleys noise configs must have `normalizeToZeroOne = false` (the default). Their outputs feed into `BurstSpline.Evaluate()` which expects the `[-1, 1]` input domain. If a designer accidentally enables normalization, the spline input becomes `[0, 1]` and terrain shapes will be wildly incorrect. `StandardChunkGenerator.Initialize()` should force this to `false` for these three configs before constructing the `FastNoiseLite` instances.

### 3.4. Updating `StandardBiomeAttributesJobData`

New fields mirroring the authoring class:

```csharp
// Assets/Scripts/Jobs/Data/StandardBiomeAttributesJobData.cs — new fields
        /// <summary>Whether to evaluate 3D density noise for volumetric terrain.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool Enable3DDensity;

        /// <summary>Max height variation of 3D noise. Defines the Dynamic Density Band bounds.</summary>
        public float DensityAmplitude;

        /// <summary>Whether to apply domain warping to density noise coordinates.</summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool EnableDensityWarp;
```

### 3.5. Cave Layer Data

Cave layer authoring (`StandardCaveLayer`, the `CaveMode` enum, per-layer domain warp, zone attenuation, depth/surface fades, and all worm-carver parameters) and its Burst `StandardCaveLayerJobData` mirror are documented in **[CAVE_GENERATION.md](CAVE_GENERATION.md)** (the `CaveMode` enum and `StandardCaveLayer` class both live in `Assets/Scripts/Data/WorldTypes/StandardBiomeAttributes.cs`). The only terrain-shape touch-point is that cave layers reuse the same domain-warp mechanism described in §2.4 / §3.1.

### 3.6. Updating `BiomeBlender`

The current `BiomeBlender.EvaluateHeight()` uses the legacy single-noise formula (`BaseTerrainHeight + noise * TerrainAmplitude`). It must be updated to use the Multi-Noise spline pipeline. To keep the parameter count manageable, we introduce a `MultiNoiseData` helper struct:

```csharp
// Assets/Scripts/Jobs/Data/MultiNoiseData.cs
/// <summary>
/// Groups the per-biome noise and spline arrays needed for Multi-Noise height evaluation.
/// Passed by ref to avoid copying six NativeArray headers through the blending pipeline.
/// </summary>
public struct MultiNoiseData
{
    [ReadOnly] public NativeArray<FastNoiseLite> ContinentalnessNoises;
    [ReadOnly] public NativeArray<FastNoiseLite> ErosionNoises;
    [ReadOnly] public NativeArray<FastNoiseLite> PeaksValleysNoises;
    [ReadOnly] public NativeArray<BurstSpline> ContinentalnessSplines;
    [ReadOnly] public NativeArray<BurstSpline> ErosionSplines;
    [ReadOnly] public NativeArray<BurstSpline> PeaksValleysSplines;
}

// Assets/Scripts/Jobs/Helpers/BiomeBlender.cs
/// <summary>
/// Calculates the blended terrain height at a global (x, z) column using Multi-Noise splines.
/// Returns float (not int) to preserve sub-block precision for the Dynamic Density Band.
/// Outputs borderFade (0.0 at Voronoi boundary, 1.0 deep inside biome) for density attenuation.
/// </summary>
public static float CalculateBlendedTerrainHeight(
    int globalX,
    int globalZ,
    ref FastNoiseLite selectionNoise,
    ref NativeArray<StandardBiomeAttributesJobData> biomes,
    ref MultiNoiseData multiNoise,
    bool isSingleBiomeMode,   // editor single-biome preview: skip Voronoi, force one biome
    int forceBiomeIndex,
    out float borderFade)
{
    // 9-cell Voronoi IDW blending with organic simplex wiggle on blend radius.
    // borderFade = ApplyCurve(saturate(edgeGap / activeRadius), primaryBiomeCurve) * saturate(primaryBlendWeight)
}

private static float EvaluateMultiNoiseHeight(
    int x, int z, int biomeIdx,
    ref NativeArray<StandardBiomeAttributesJobData> biomes,
    ref MultiNoiseData mn)
{
    StandardBiomeAttributesJobData b = biomes[biomeIdx];
    float cont = mn.ContinentalnessSplines[biomeIdx].Evaluate(mn.ContinentalnessNoises[biomeIdx].GetNoise(x, z));
    float erosion = mn.ErosionSplines[biomeIdx].Evaluate(mn.ErosionNoises[biomeIdx].GetNoise(x, z));
    float pv = mn.PeaksValleysSplines[biomeIdx].Evaluate(mn.PeaksValleysNoises[biomeIdx].GetNoise(x, z));
    return b.BaseTerrainHeight + cont + (pv * erosion);
}
```

> [!NOTE]
> **Return type & borderFade:** `CalculateBlendedTerrainHeight` returns `float` (not `int`) and outputs `borderFade` via an `out` parameter. The blend radius wiggle uses continuous simplex noise (`noise.snoise`) instead of Cellular CellValue to avoid step discontinuities at Voronoi grid boundaries. The legacy `int`-returning overload has been removed.

The `StandardChunkGenerationJob` constructs a `MultiNoiseData` from its input arrays and passes it to the blender. The `borderFade` output is used to attenuate `DensityAmplitude` near biome borders (see §4).

### 3.7. Generator Initialization & Memory Management

`StandardChunkGenerator.Initialize()` must allocate and populate the following **new** `Allocator.Persistent` arrays:

| Array                          | Size                  | Source                                                       |
|--------------------------------|-----------------------|--------------------------------------------------------------|
| `_biomeContinentalnessNoises`  | `biomeCount`          | `biome.continentalnessNoiseConfig`                           |
| `_biomeErosionNoises`          | `biomeCount`          | `biome.erosionNoiseConfig`                                   |
| `_biomePeaksValleysNoises`     | `biomeCount`          | `biome.peaksAndValleysNoiseConfig`                           |
| `_biomeDensityNoises`          | `biomeCount`          | `biome.densityNoiseConfig`                                   |
| `_biomeDensityWarpNoises`      | `biomeCount`          | `biome.densityWarpConfig`                                    |
| `_biomeContinentalnessSplines` | `biomeCount`          | `BurstSpline.FromAnimationCurve(biome.continentalnessCurve)` |
| `_biomeErosionSplines`         | `biomeCount`          | `BurstSpline.FromAnimationCurve(biome.erosionCurve)`         |
| `_biomePVSplines`              | `biomeCount`          | `BurstSpline.FromAnimationCurve(biome.peaksAndValleysCurve)` |
| `_caveWarpNoises`              | `totalCaveLayerCount` | `caveLayer.warpConfig` (see note below)                      |

All must have corresponding `.Dispose()` calls in `StandardChunkGenerator.Dispose()`.

> [!NOTE]
> **`_caveWarpNoises` Indexing:** This array is sized to `totalCaveLayerCount` and indexed by `caveIdx` (matching `_caveNoises`). Every cave layer gets an entry — including `Spaghetti` and `WormCarver` layers where warp is ignored. For layers with `enableWarp = false` or modes that don't support warping, populate the slot with a default-constructed `FastNoiseLite` instance (via `FastNoiseLite.Create(0)`). This avoids conditional indexing in the job and matches the existing pattern used by `_caveNoises` (which populates all slots regardless of mode).

> [!NOTE]
> **Cave noise tables:** `Initialize()` also allocates the cave subsystem's noise arrays — `_caveNoises` and `_caveSpaghetti3DNoises` (`totalCaveLayerCount`, the latter populated only for Spaghetti3D layers) and `_caveZoneNoises` (per-biome) — alongside `_caveWarpNoises`, all disposed in `Dispose()`. Their roles are documented in [CAVE_GENERATION.md](CAVE_GENERATION.md).

The legacy `_biomeTerrainNoises` array, the `terrainNoiseConfig` field, and the `terrainAmplitude` field have been removed. All biomes must have configured Multi-Noise fields.

---

## 4. Burst Job Pipeline (`StandardChunkGenerationJob`)

Core rewrite of the terrain generation loop. Key changes from v1.x:

- Multi-Noise spline-based height calculation
- 3D Density evaluation within a dynamic band
- `lastSurfaceY` tracking for correct subsurface strata under overhangs
- Lode pass runs **before** cave carving so `PreCaveBlockIDs` captures post-lode values
- `FluidType`-based cave carve guard
- Cave carving pass (noise modes + worm mask) gated by `FeatureFlags` — formulas/modes documented in [CAVE_GENERATION.md](CAVE_GENERATION.md)
- Cave isolation filter post-pass (`CaveIsolationFilterJob`) — volume-based flood fill removes small disconnected cave pockets

```csharp
// Assets/Scripts/Jobs/StandardChunkGenerationJob.cs
// ... existing biome dithering code ...

// --- 1. MULTI-NOISE BASE TERRAIN (via BiomeBlender) ---
// Height is evaluated through the existing 9-cell Voronoi IDW blending pipeline,
// now using Multi-Noise splines instead of the legacy single-noise formula.
// Returns float to preserve sub-block precision for the Density Band.
MultiNoiseData multiNoise = new MultiNoiseData
{
    ContinentalnessNoises = BiomeContinentalnessNoises,
    ErosionNoises = BiomeErosionNoises,
    PeaksValleysNoises = BiomePeaksValleysNoises,
    ContinentalnessSplines = BiomeContinentalnessSplines,
    ErosionSplines = BiomeErosionSplines,
    PeaksValleysSplines = BiomePVSplines,
};

float terrainHeightFloat = BiomeBlender.CalculateBlendedTerrainHeight(
    globalX, globalZ, ref BiomeSelectionNoise, ref Biomes, ref multiNoise,
    out float borderFade);
int baseTerrainHeight = (int)math.floor(terrainHeightFloat);

// Attenuate 3D density amplitude near biome borders to prevent cliff tearing.
float effectiveDensityAmplitude = biome.DensityAmplitude * borderFade;
int bandLow = baseTerrainHeight - (int)math.ceil(effectiveDensityAmplitude);
int bandHigh = baseTerrainHeight + (int)math.ceil(effectiveDensityAmplitude);

bool highestBlockFound = false;
float previousDensity = -1f;
int lastSurfaceY = baseTerrainHeight; // Anchor for subsurface strata depth

// --- 2. COLUMN ITERATION (Top-Down) ---
for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
{
    byte voxelValue = (byte)BlockIDs.Air;
    float density = (float)(baseTerrainHeight - y);

    // --- 3. DYNAMIC 3D DENSITY BAND & DOMAIN WARPING ---
    if (biome.Enable3DDensity && y >= bandLow && y <= bandHigh)
    {
        float dx = globalX, dy = y, dz = globalZ;

        if (biome.EnableDensityWarp)
        {
            BiomeDensityWarpNoises[biomeIndex].DomainWarp(ref dx, ref dy, ref dz);
        }

        density += BiomeDensityNoises[biomeIndex].GetNoise(dx, dy, dz) * effectiveDensityAmplitude;
    }

    // ----- IMMUTABLE PASS -----
    if (y == 0)
    {
        voxelValue = (byte)BlockIDs.Bedrock;
        density = 1f;
    }
    // ----- VOLUMETRIC TERRAIN PASS -----
    else if (density > 0f)
    {
        bool isExposedSurface = (previousDensity <= 0f);

        if (isExposedSurface)
        {
            lastSurfaceY = y; // Track for strata depth below
            voxelValue = y < SeaLevel - 1 ? surfaceBiome.UnderwaterSurfaceBlockID : surfaceBiome.SurfaceBlockID;

            // --- STRUCTURE PLACEMENT (topmost surface only) ---
            // The entire flora zone noise sampling + grid-election system from the legacy
            // y == terrainHeight guard is moved wholesale into this branch.
            // This includes: biome flora zone pre-sampling, per-entry override noise,
            // spacing/padding grid checks, and StructureSpawnMarker emission.
            if (!highestBlockFound && y >= SeaLevel && voxelValue != BlockIDs.Water)
            {
                // ... (Run the existing StructurePoolEntry grid-election loop here) ...
            }
        }
        else
        {
            // Subsurface strata — anchored to lastSurfaceY, NOT baseTerrainHeight
            voxelValue = (byte)BlockIDs.Stone;
            int depthCounter = 0;
            float depthJitter = StrataDepthNoises[surfaceBiomeIndex].GetNoise(globalX, globalZ);
            int jitterBlocks = (int)math.round(depthJitter * 2.5f);

            for (int i = 0; i < surfaceBiome.TerrainLayerCount; i++)
            {
                StandardTerrainLayerJobData layer = AllTerrainLayers[surfaceBiome.TerrainLayerStartIndex + i];
                int effectiveDepth = math.max(1, layer.Depth + jitterBlocks);

                if (y < lastSurfaceY - depthCounter && y >= lastSurfaceY - depthCounter - effectiveDepth)
                {
                    voxelValue = layer.BlockID;
                    break;
                }
                depthCounter += effectiveDepth;
            }
        }

        if (!highestBlockFound && BlockTypes[voxelValue].IsLightObstructing)
        {
            OutputHeightMap[x + VoxelData.ChunkWidth * z] = (ushort)y;
            highestBlockFound = true;
        }
    }
    else // density <= 0f
    {
        if (y < SeaLevel)
            voxelValue = (byte)BlockIDs.Water;
        else
            voxelValue = (byte)BlockIDs.Air;
    }

    previousDensity = density;

    // ----- LODE PASS (runs before cave carving) -----
    // Lodes run first so that PreCaveBlockIDs captures post-lode values.
    // ... existing lode code ...

    // ----- CAVE CARVING PASS -----
    // Guard: only carve solid, non-fluid, non-bedrock blocks. For each cave layer in the biome,
    // evaluate the layer's mode (Cheese / Spaghetti2D / Spaghetti3D / Noodle, or the pre-baked
    // WormMask from StandardWormCarverJob) against a depth/surface-fade + zone-attenuated
    // threshold. On a carve: write OutputCaveMask + OutputPreCaveBlockIDs (for the isolation
    // filter post-pass) and set voxelValue = Air.
    //
    // The per-mode formulas (incl. the smoothed Noodle/Spaghetti3D isobands), zone attenuation,
    // depth/surface fades, feature-flag gating, and the worm mask are the authoritative subject
    // of CAVE_GENERATION.md — see §4 there, not this summary.
    if (FeatureFlags.EnableCaves &&
        voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock &&
        BlockTypes[voxelValue].FluidType == FluidType.None)
    {
        // ... per-layer cave evaluation — see CAVE_GENERATION.md §4 ...
    }

    // ...[Pack Voxel & Write to OutputMap] ...
}
```

### 4.1. Cave Isolation Filter (`CaveIsolationFilterJob`)

A Burst-compiled `IJob` post-pass that removes isolated cave air pockets via connected-component flood fill. Scheduled after `StandardChunkGenerationJob` on the same `VoxelMap`.

**Temporary buffers** (allocated per-chunk during generation, disposed after the filter):

| Buffer            | Type                  | Size                    | Purpose                                                     |
|-------------------|-----------------------|-------------------------|-------------------------------------------------------------|
| `CaveMask`        | `NativeArray<byte>`   | 32,768 bytes (32KB)     | 1 byte per voxel — marks blocks carved by any cave mode     |
| `PreCaveBlockIDs` | `NativeArray<ushort>` | 32,768 × 2 bytes (64KB) | Original block ID before cave carving (post-lode, pre-cave) |

**Job chain:**

```
WormCarverJob (IJob) → StandardChunkGenerationJob (IJobFor, writes CaveMask + PreCaveBlockIDs)
                     → CaveIsolationFilterJob (IJob, flood-fills CaveMask, restores small pockets)
```

**Algorithm:** For each connected region of cave-carved air (identified via `CaveMask`), BFS flood fill computes the region volume. If the volume is below the per-biome `MinCavePocketSize` threshold, all voxels in that region are restored to their original pre-cave blocks from `PreCaveBlockIDs`. Restoration repacks the block ID with correct `LightEmission` and `FluidLevel` via `BlockTypes`.

**Design decisions:**

- **`NativeArray<byte>` over `NativeBitArray`:** Each `IJobFor` worker writes to a distinct byte index, avoiding the non-atomic read-modify-write race that `NativeBitArray.Set()` has on shared 64-bit words.
- **Per-biome field, global runtime threshold:** `MinCavePocketSize` lives on `StandardBiomeAttributes`. At runtime, `StandardChunkGenerator.Initialize()` takes the max across all biomes — conservative but avoids per-voxel biome tracking.
- **Out-of-chunk = boundary:** Neighbors outside 16×16×128 bounds are not traversed. A pocket at a chunk edge is evaluated independently (conservative — may filter a pocket that connects in a neighbor chunk).
- **Conditional scheduling:** Only scheduled when `_globalMinCavePocketSize > 0 && FeatureFlags.EnableCaves`. When disabled, neither `CaveMask` nor `PreCaveBlockIDs` are allocated.

**Editor preview:** The CrossSection and BiomeEditor tabs approximate this filter with a 2D flood fill (`ApplyCaveIsolationFilter2D`) on their vertical slices. The 3D Chunk Preview uses the real job pipeline.

---

## 5. `GetVoxel` Main-Thread Fallback

`StandardChunkGenerator.GetVoxel()` is a synchronous main-thread voxel lookup used exclusively by `World.GetHighestVoxel()` as a fallback when a chunk hasn't been generated yet (e.g., spawn point calculation). It is **not** used for structure collision checks — structures use `StructureSpawnMarker` + `ExpandStructure()` on the main thread, which operates on already-generated chunk data.

`GetVoxel` now uses the multi-noise `CalculateBlendedTerrainHeight` with `MultiNoiseData` for height evaluation, matching the job pipeline's 2D height formula. It does **not** evaluate 3D density, domain warping, or cave carving — it remains a 2D heightmap approximation sufficient for spawn-point lookups. A docstring comment in the method notes this intentional limitation.

---

## 6. Performance Impact Analysis

### 6.1. Computational Overhead

* **Domain Warping (2D/3D):** Low. `FastNoiseLite.DomainWarp` is optimized for Burst intrinsics.
* **3D Density Evaluation:** Low to Medium. The **Dynamic Density Band** skips ~95% of Y-levels.
* **Overall Estimate:** ~1.5ms – 2.5ms per chunk increase in Burst. Well within streaming budgets.

### 6.2. Subsurface Strata Evaluation Cost

The legacy system runs the terrain layer loop only for blocks where `y < terrainHeight` (a small subsurface region near the surface). The volumetric system runs it for every voxel where `density > 0` and `!isExposedSurface` — potentially dozens of Y-levels per column when `DensityAmplitude` is large. The strata loop early-exits via `break` when a matching layer is found, so this is not catastrophic. However, biomes with many terrain layers and high `DensityAmplitude` will see a measurable increase. If profiling reveals this as a bottleneck, precomputing
cumulative depth thresholds per-column (before the Y-loop) would eliminate the inner loop entirely.

### 6.3. Cache Locality & Memory

Zero new allocations inside the main execution job (`StandardChunkGenerationJob`). All data is pre-allocated `NativeArray<FastNoiseLite>` and `NativeArray<BurstSpline>` tables managed by `StandardChunkGenerator`. The `CaveIsolationFilterJob` post-pass allocates temporary `NativeArray` buffers (`CaveMask` 32KB, `PreCaveBlockIDs` 64KB) per chunk; these are disposed after the filter completes.

### 6.4. Visual Wins

1. **True Overhangs & Arches:** Bypassing the strict $y = f(x,z)$ mapping permits gravity-defying terrain features naturally.
2. **Organic River Valleys & Canyons:** Applying Domain Warp to Ridged noise generates winding, non-repetitive ravines that mimic natural hydraulic erosion.
3. **No Overdraw in Generation:** Because we track `previousDensity` natively in the top-down loop, we easily assign grass to floating islands and overhangs without running complex secondary pass algorithms.
