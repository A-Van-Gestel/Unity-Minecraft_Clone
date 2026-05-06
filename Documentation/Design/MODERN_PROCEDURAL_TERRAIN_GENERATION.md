# Design Document: Modern Procedural Terrain Generation

**Version:** 2.2 (Production-Ready)  
**Date:** May 2026  
**Status:** Approved for Implementation  
**Target:** Unity 6.4 (Standard World Gen System / Burst Compiler)  
**Context:** Upgrading the 2D heightmap generation to a 3D volumetric density pipeline with Domain Warping and Multi-Noise (Continentalness, Erosion, Peaks & Valleys).

---

## 1. Executive Summary

The legacy procedural terrain system uses a strict 2D heightmap ($y = f(x,z)$). This prevents the generation of complex overhangs, floating islands, and organic cave systems. By integrating modern techniques, we transition to a **Volumetric Density** model while maintaining strict Burst performance:

1. **Multi-Noise 2D Base (Minecraft C&C):** Terrain height is decoupled from a single noise map. Instead, we evaluate three independent noises (Continentalness, Erosion, Peaks & Valleys) mapped through **Data-Driven Splines** to determine a base terrain shape.
2. **3D Density Fields (GPU Gems 3):** The final surface is defined by a 3D density function: $Density = BaseHeight(x,z) - y + 3DNoise(x,y,z)$.
3. **Domain Warping (Iñigo Quílez):** The input coordinates of the 3D density noise (and optionally cave noises) are distorted using a secondary noise field ($p' = p + Warp(p)$), breaking up artificial grid-like patterns and simulating geological folding.
4. **Cave System Modernization:** All existing cave modes are preserved. New volumetric cave modes are added:
    - *Cheese (renamed from Blob):* Large open caverns — `noise3D > threshold`
    - *Spaghetti (preserved):* Legacy 6-way 2D axis-pair average tunnel networks
    - *Noodle (new):* Winding tubular corridors — `1 - |noise3D| > threshold`
    - *WormCarver (preserved):* Organic recursive random-walk tunnels via pre-baked bitmask

---

## 2. Architectural Concepts

### 2.1. Multi-Noise Base Terrain (Continentalness, Erosion, P&V)

Relying on a single noise map creates predictable hills. We separate the macroscopic terrain shape into three 2D noise evaluations:

* **Continentalness:** Determines macro-scale landmasses vs. oceans. High values elevate the terrain globally.
* **Erosion:** Determines how weathered the terrain is. High erosion flattens terrain into plains or valleys; low erosion allows mountains to form.
* **Peaks & Valleys (PV):** Adds local high-frequency height variations.

*Burst Implementation:* We evaluate these three `FastNoiseLite` instances per column and map their outputs through a **`BurstSpline` struct** (baked from an `AnimationCurve` in the Editor at init time). This provides complete, code-free artist control over terrain shaping.

> [!IMPORTANT]
> The legacy `terrainNoiseConfig` field in `StandardBiomeAttributes` must be marked `[Obsolete]` and `[HideInInspector]`. The `BiomeTerrainNoises` array is replaced by the three Multi-Noise arrays. `BiomeBlender` must be updated to use the new spline-based height evaluation (see §3.6).

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

> [!WARNING]
> **Biome Boundary Tearing:** 3D Density evaluation *must* be blended at Voronoi biome boundaries using the Voronoi edge distance. Switching `biomeIndex` abruptly for 3D noise causes severe vertical cliffs/tearing on overhangs.

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
        public bool enable3DDensity = true;
        public FastNoiseConfig densityNoiseConfig;
        [Tooltip("Max height variation of 3D noise. Dynamically defines the Density Band.")]
        public float densityAmplitude = 15f;

        [Header("Domain Warping (Organic Distortion)")]
        public bool enableDensityWarp = true;
        public FastNoiseConfig densityWarpConfig;

        // Legacy field — replaced by Multi-Noise system above
        [Obsolete("Use continentalnessNoiseConfig/erosionNoiseConfig/peaksAndValleysNoiseConfig instead.")]
        [HideInInspector]
        public FastNoiseConfig terrainNoiseConfig;
```

> [!WARNING]
> **Multi-Noise `normalizeToZeroOne` Constraint:** The Continentalness, Erosion, and Peaks & Valleys noise configs must have `normalizeToZeroOne = false` (the default). Their outputs feed into `BurstSpline.Evaluate()` which expects the `[-1, 1]` input domain. If a designer accidentally enables normalization, the spline input becomes `[0, 1]` and terrain shapes will be wildly incorrect. `StandardChunkGenerator.Initialize()` should force this to `false` for these three configs before constructing the `FastNoiseLite` instances.

### 3.4. Updating `StandardBiomeAttributesJobData`

New fields mirroring the authoring class:

```csharp
// Assets/Scripts/Jobs/Data/StandardBiomeAttributesJobData.cs — new fields
        /// <summary>Whether to evaluate 3D density noise for volumetric terrain.</summary>
        public bool Enable3DDensity;

        /// <summary>Max height variation of 3D noise. Defines the Dynamic Density Band bounds.</summary>
        public float DensityAmplitude;

        /// <summary>Whether to apply domain warping to density noise coordinates.</summary>
        public bool EnableDensityWarp;
```

### 3.5. Updating Cave Layer Data (New `Noodle` Mode + Warp Support)

```csharp
// Assets/Scripts/Data/WorldTypes/StandardCaveLayer.cs — new fields
        [Tooltip("Apply domain warping to this cave layer's noise coordinates. Only affects Cheese and Noodle modes (3D evaluation). Ignored for Spaghetti (2D legacy).")]
        public bool enableWarp;
        public FastNoiseConfig warpConfig;

// Assets/Scripts/Data/WorldTypes/CaveMode.cs — add new enum value
public enum CaveMode
{
    Cheese,      // Renamed from Blob — large open caverns (noise3D > threshold)
    Spaghetti,   // Preserved — legacy 6-way 2D axis-pair average
    WormCarver,  // Preserved — pre-baked bitmask
    Noodle,      // NEW — winding tubular corridors (1 - |noise3D| > threshold)
}

// Assets/Scripts/Jobs/Data/StandardCaveLayerJobData.cs — add field
        public readonly bool EnableWarp;
```

> [!IMPORTANT]
> The `Blob` → `Cheese` rename requires `[FormerlySerializedAs("Blob")]` on the enum to preserve existing biome asset data. See the `unity-file-ops` skill for the serialization-safe rename protocol.

> [!NOTE]
> **Domain Warp and Spaghetti:** Cave domain warping (`EnableWarp`) only applies to `Cheese` and `Noodle` modes, which use full 3D noise evaluation. The legacy `Spaghetti` mode uses 2D noise pairs (`GetNoise(x, y)`, `GetNoise(y, z)`, etc.) — applying a 3D warp to coordinates consumed by 2D calls produces inconsistent distortion (the warped Z shift is lost in pairs that don't use Z). Spaghetti always evaluates with unwarped `globalX, y, globalZ` coordinates.

### 3.6. Updating `BiomeBlender`

The current `BiomeBlender.EvaluateHeight()` uses the legacy single-noise formula. It must be updated to use the Multi-Noise spline pipeline:

```csharp
// Assets/Scripts/Jobs/Helpers/BiomeBlender.cs
private static float EvaluateHeight(
    int x, int z, int biomeIdx,
    ref NativeArray<StandardBiomeAttributesJobData> biomes,
    ref NativeArray<FastNoiseLite> contNoises,
    ref NativeArray<FastNoiseLite> erosionNoises,
    ref NativeArray<FastNoiseLite> pvNoises,
    ref NativeArray<BurstSpline> contSplines,
    ref NativeArray<BurstSpline> erosionSplines,
    ref NativeArray<BurstSpline> pvSplines)
{
    StandardBiomeAttributesJobData b = biomes[biomeIdx];
    float cont = contSplines[biomeIdx].Evaluate(contNoises[biomeIdx].GetNoise(x, z));
    float erosion = erosionSplines[biomeIdx].Evaluate(erosionNoises[biomeIdx].GetNoise(x, z));
    float pv = pvSplines[biomeIdx].Evaluate(pvNoises[biomeIdx].GetNoise(x, z));
    return b.BaseTerrainHeight + cont + (pv * erosion);
}
```

The `CalculateBlendedTerrainHeight` signature must be updated to accept the new arrays.

### 3.7. Generator Initialization & Memory Management

`StandardChunkGenerator.Initialize()` must allocate and populate the following **new** `Allocator.Persistent` arrays:

| Array | Size | Source |
|---|---|---|
| `_biomeContinentalnessNoises` | `biomeCount` | `biome.continentalnessNoiseConfig` |
| `_biomeErosionNoises` | `biomeCount` | `biome.erosionNoiseConfig` |
| `_biomePeaksValleysNoises` | `biomeCount` | `biome.peaksAndValleysNoiseConfig` |
| `_biomeDensityNoises` | `biomeCount` | `biome.densityNoiseConfig` |
| `_biomeDensityWarpNoises` | `biomeCount` | `biome.densityWarpConfig` |
| `_biomeContinentalnessSplines` | `biomeCount` | `BurstSpline.FromAnimationCurve(biome.continentalnessCurve)` |
| `_biomeErosionSplines` | `biomeCount` | `BurstSpline.FromAnimationCurve(biome.erosionCurve)` |
| `_biomePVSplines` | `biomeCount` | `BurstSpline.FromAnimationCurve(biome.peaksAndValleysCurve)` |
| `_caveWarpNoises` | `totalCaveLayerCount` | `caveLayer.warpConfig` |

All must have corresponding `.Dispose()` calls in `StandardChunkGenerator.Dispose()`.

The legacy `_biomeTerrainNoises` array becomes unused and should be removed.

---

## 4. Burst Job Pipeline (`StandardChunkGenerationJob`)

Core rewrite of the terrain generation loop. Key changes from v1.x:
- Multi-Noise spline-based height calculation
- 3D Density evaluation within a dynamic band
- `lastSurfaceY` tracking for correct subsurface strata under overhangs
- `FluidType`-based cave carve guard
- Preserved legacy cave modes + new `Noodle` mode

```csharp
// Assets/Scripts/Jobs/StandardChunkGenerationJob.cs
// ... existing biome dithering code ...

// --- 1. MULTI-NOISE BASE TERRAIN ---
float continentalness = BiomeContinentalnessNoises[biomeIndex].GetNoise(globalX, globalZ);
float erosion = BiomeErosionNoises[biomeIndex].GetNoise(globalX, globalZ);
float pv = BiomePeaksValleysNoises[biomeIndex].GetNoise(globalX, globalZ);

float contOffset = BiomeContinentalnessSplines[biomeIndex].Evaluate(continentalness);
float erosionMultiplier = BiomeErosionSplines[biomeIndex].Evaluate(erosion);
float pvAmplitude = BiomePVSplines[biomeIndex].Evaluate(pv);

float terrainHeightFloat = biome.BaseTerrainHeight + contOffset + (pvAmplitude * erosionMultiplier);
int baseTerrainHeight = (int)math.floor(terrainHeightFloat);

// Dynamic Density Band bounds (use ceil to never clip valid voxels)
int bandLow = baseTerrainHeight - (int)math.ceil(biome.DensityAmplitude);
int bandHigh = baseTerrainHeight + (int)math.ceil(biome.DensityAmplitude);

bool highestBlockFound = false;
float previousDensity = -1f;
bool hasHitSurface = false;
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

        // TODO: Production code must blend this across biome boundaries
        density += BiomeDensityNoises[biomeIndex].GetNoise(dx, dy, dz) * biome.DensityAmplitude;
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
        hasHitSurface = true;
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
        if (y < SeaLevel && !hasHitSurface)
            voxelValue = (byte)BlockIDs.Water;
        else
            voxelValue = (byte)BlockIDs.Air;
    }

    previousDensity = density;

    // ----- CAVE CARVING PASS -----
    // Guard: only carve solid, non-fluid, non-bedrock blocks
    if (voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock &&
        BlockTypes[voxelValue].FluidType == FluidType.None)
    {
        for (int i = 0; i < biome.CaveLayerCount; i++)
        {
            int caveIdx = biome.CaveLayerStartIndex + i;
            StandardCaveLayerJobData caveLayer = AllCaveLayers[caveIdx];

            if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight) continue;

            float depthFade = 1f;
            if (caveLayer.DepthFadeMargin > 0)
            {
                int distFromMin = y - caveLayer.MinHeight;
                int distFromMax = caveLayer.MaxHeight - y;
                int distFromEdge = math.min(distFromMin, distFromMax);
                depthFade = math.saturate((float)distFromEdge / caveLayer.DepthFadeMargin);
            }

            float effectiveThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);

            // --- WormCarver (preserved) ---
            if (caveLayer.Mode == CaveMode.WormCarver)
            {
                if (WormMask.IsSet(ChunkMath.GetFlattenedIndexInChunk(x, y, z)))
                {
                    voxelValue = (byte)BlockIDs.Air;
                    break;
                }
                continue;
            }

            FastNoiseLite caveNoise = CaveNoises[caveIdx];

            // --- Cheese Caves (renamed from Blob) — large open caverns ---
            if (caveLayer.Mode == CaveMode.Cheese)
            {
                float cx = globalX, cy = y, cz = globalZ;
                if (caveLayer.EnableWarp)
                    CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);

                if (caveNoise.GetNoise(cx, cy, cz) > effectiveThreshold)
                {
                    voxelValue = (byte)BlockIDs.Air;
                    break;
                }
            }
            // --- Spaghetti (preserved) — legacy 6-way 2D axis-pair average ---
            // Domain warp is NOT applied: 2D noise pairs would lose the Z-axis warp shift.
            else if (caveLayer.Mode == CaveMode.Spaghetti)
            {
                // Bounding volume early-out: evaluate low-frequency 3D noise first.
                // If far below threshold, skip the expensive 6-way evaluation.
                float bound = caveNoise.GetNoise(globalX * 0.25f, y * 0.25f, globalZ * 0.25f);
                if (bound < effectiveThreshold - 0.2f) continue;

                float noiseVal = (caveNoise.GetNoise(globalX, y) + caveNoise.GetNoise(y, globalZ) +
                                  caveNoise.GetNoise(globalX, globalZ) + caveNoise.GetNoise(y, globalX) +
                                  caveNoise.GetNoise(globalZ, y) + caveNoise.GetNoise(globalZ, globalX)) / 6f;

                if (noiseVal > effectiveThreshold)
                {
                    voxelValue = (byte)BlockIDs.Air;
                    break;
                }
            }
            // --- Noodle (new) — winding tubular corridors via isoband ---
            else if (caveLayer.Mode == CaveMode.Noodle)
            {
                float cx = globalX, cy = y, cz = globalZ;
                if (caveLayer.EnableWarp)
                    CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);

                // Tunnels exist where |noise| is close to 0 (the 'core' of the noise wave).
                // Invert so center = 1.0, edges = 0.0.
                float noiseVal = 1.0f - math.abs(caveNoise.GetNoise(cx, cy, cz));

                if (noiseVal > effectiveThreshold)
                {
                    voxelValue = (byte)BlockIDs.Air;
                    break;
                }
            }
        }
    }

    // ----- LODE PASS -----
    // ... existing lode code ...

    // ...[Pack Voxel & Write to OutputMap] ...
}
```

---

## 5. `GetVoxel` Main-Thread Fallback

`StandardChunkGenerator.GetVoxel()` is a synchronous main-thread voxel lookup used for structure expansion collision checks. It must be updated in parallel with the Burst job to use the same volumetric density logic (Multi-Noise splines, 3D density band, `lastSurfaceY` strata anchoring, and the new cave modes). If left on the legacy 2D heightmap, structures will be collision-tested against incorrect terrain, causing floating trees and buried structures.

---

## 6. Performance Impact Analysis

### 6.1. Computational Overhead

* **Domain Warping (2D/3D):** Low. `FastNoiseLite.DomainWarp` is optimized for Burst intrinsics.
* **3D Density Evaluation:** Low to Medium. The **Dynamic Density Band** skips ~95% of Y-levels.
* **Overall Estimate:** ~1.5ms – 2.5ms per chunk increase in Burst. Well within streaming budgets.

### 6.2. Cache Locality & Memory

Zero new allocations inside the execution job. All data is pre-allocated `NativeArray<FastNoiseLite>` and `NativeArray<BurstSpline>` tables managed by `StandardChunkGenerator`.

### 6.3. Visual Wins

1. **True Overhangs & Arches:** Bypassing the strict $y = f(x,z)$ mapping permits gravity-defying terrain features naturally.
2. **Organic River Valleys & Canyons:** Applying Domain Warp to Ridged noise generates winding, non-repetitive ravines that mimic natural hydraulic erosion.
3. **No Overdraw in Generation:** Because we track `previousDensity` natively in the top-down loop, we easily assign grass to floating islands and overhangs without running complex secondary pass algorithms.
