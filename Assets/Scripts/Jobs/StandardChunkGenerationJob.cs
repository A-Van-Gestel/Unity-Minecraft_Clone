using System.Runtime.InteropServices;
using Data;
using Data.WorldTypes;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
using Jobs.Helpers;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Jobs
{
    /// <summary>
    /// Burst-compiled chunk generation job using <see cref="FastNoiseLite"/>.
    /// Replaces the legacy <c>ChunkGenerationJob</c> for the Standard world type.
    /// Uses <c>Unity.Mathematics</c> types throughout for SIMD auto-vectorization.
    /// </summary>
    [BurstCompile]
    public struct StandardChunkGenerationJob : IJobFor
    {
        #region Input Data

        [ReadOnly]
        public int SeaLevel;

        [ReadOnly]
        public int BaseSeed;

        /// <summary>
        /// Chunk position in voxel-space (world origin). Uses <c>int2</c> instead of <c>Vector2Int</c>
        /// to keep the math pipeline within <c>Unity.Mathematics</c> for Burst SIMD vectorization.
        /// </summary>
        [ReadOnly]
        public int2 ChunkPosition;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        [ReadOnly]
        public NativeArray<StandardBiomeAttributesJobData> Biomes;

        [ReadOnly]
        public NativeArray<StandardTerrainLayerJobData> AllTerrainLayers;

        [ReadOnly]
        public NativeArray<StandardLodeJobData> AllLodes;

        [ReadOnly]
        public NativeArray<StandardCaveLayerJobData> AllCaveLayers;

        #region Multi-Noise Input Data

        /// <summary>Per-biome Continentalness noise instances for multi-noise terrain height.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> BiomeContinentalnessNoises;

        /// <summary>Per-biome Erosion noise instances for multi-noise terrain height.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> BiomeErosionNoises;

        /// <summary>Per-biome Peaks &amp; Valleys noise instances for multi-noise terrain height.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> BiomePeaksValleysNoises;

        /// <summary>Per-biome Continentalness splines baked from AnimationCurves.</summary>
        [ReadOnly]
        public NativeArray<BurstSpline> BiomeContinentalnessSplines;

        /// <summary>Per-biome Erosion splines baked from AnimationCurves.</summary>
        [ReadOnly]
        public NativeArray<BurstSpline> BiomeErosionSplines;

        /// <summary>Per-biome Peaks &amp; Valleys splines baked from AnimationCurves.</summary>
        [ReadOnly]
        public NativeArray<BurstSpline> BiomePVSplines;

        /// <summary>Per-biome 3D density noise instances.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> BiomeDensityNoises;

        /// <summary>Per-biome domain warp noise instances for density coordinate distortion.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> BiomeDensityWarpNoises;

        /// <summary>Per-cave-layer domain warp noise instances. Indexed by caveIdx.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveWarpNoises;

        /// <summary>Per-cave-layer secondary noise for Spaghetti3D mode. Indexed by caveIdx. Unused slots contain a default instance.</summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveSpaghetti3DNoises;

        #endregion

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's strata depth noise.
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> StrataDepthNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each lode's noise.
        /// Indexed matching AllLodes array.
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> LodeNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's cave layers.
        /// Indexed alongside AllCaveLayers.
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's cave zone gating.
        /// Indexed per biome. Evaluated as 2D noise to determine whether a column can have caves.
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> CaveZoneNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's flora zones.
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> FloraZoneNoises;

        /// <summary>
        /// Global biome selection noise (Cellular/Voronoi).
        /// </summary>
        public FastNoiseLite BiomeSelectionNoise;

        /// <summary>
        /// Pre-calculated bitmask of blocks that have been carved out by the Worm Carver scatter pass.
        /// 1 bit per voxel. If bit is 1, the block should be air.
        /// </summary>
        [ReadOnly]
        public NativeBitArray WormMask;

        /// <summary>
        /// Flattened array of all structure pool entries across all biomes.
        /// Each biome references a range via MajorFloraPoolStartIndex/Count and MinorFloraPoolStartIndex/Count.
        /// </summary>
        [ReadOnly]
        public NativeArray<StructurePoolEntryJobData> AllStructurePoolEntries;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for per-entry flora zone overrides.
        /// Indexed by <see cref="StructurePoolEntryJobData.FloraZoneNoiseIndex"/>.
        /// Entries without overrides use the biome's default <see cref="FloraZoneNoises"/> instead.
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> EntryFloraZoneNoises;

        [ReadOnly]
        [MarshalAs(UnmanagedType.U1)]
        public bool IsSingleBiomeMode;

        [ReadOnly]
        public int ForceBiomeIndex;

        /// <summary>Controls which optional generation passes (caves, lodes, water) are executed.</summary>
        [ReadOnly]
        public GenerationFeatureFlags FeatureFlags;

        #endregion

        #region Output Data

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<uint> OutputMap;

        [WriteOnly]
        public NativeArray<ushort> OutputHeightMap;

        /// <summary>Per-voxel byte flag marking blocks carved by cave generation. Consumed by <see cref="CaveIsolationFilterJob"/>.</summary>
        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<byte> OutputCaveMask;

        /// <summary>Original block IDs stored before cave carving. Only meaningful where OutputCaveMask is set.</summary>
        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<ushort> OutputPreCaveBlockIDs;

        /// <summary>
        /// ParallelWriter allows concurrent Enqueue from worker threads.
        /// <see cref="VoxelMod"/> with <c>Vector3Int</c> is fully blittable — safe in Burst.
        /// </summary>
        public NativeQueue<VoxelMod>.ParallelWriter Modifications;

        /// <summary>
        /// Structure spawn markers emitted by per-entry grid passes.
        /// Consumed on the main thread for structure expansion.
        /// </summary>
        public NativeQueue<StructureSpawnMarker>.ParallelWriter StructureSpawns;

        #endregion

        /// <summary>
        /// Generates the voxel data for a single vertical column (X/Z coordinate pair) in the chunk.
        /// </summary>
        /// <param name="index">The 1D flattened index representing the X and Z coordinates (0 to 255).</param>
        public void Execute(int index)
        {
            int x = index % VoxelData.ChunkWidth;
            int z = index / VoxelData.ChunkWidth;

            int globalX = x + ChunkPosition.x;
            int globalZ = z + ChunkPosition.y;

            // --- BIOME SELECTION (Voronoi / Cellular) ---
            int biomeIndex;
            if (IsSingleBiomeMode)
            {
                biomeIndex = ForceBiomeIndex;
            }
            else
            {
                // Single evaluation per column — O(1) regardless of biome count.
                float biomeNoise = BiomeSelectionNoise.GetNoise(globalX, globalZ);
                // Noise is enforced to [0,1] normalization internally
                biomeIndex = (int)math.floor(biomeNoise * Biomes.Length);
                biomeIndex = math.clamp(biomeIndex, 0, Biomes.Length - 1);
            }

            StandardBiomeAttributesJobData biome = Biomes[biomeIndex];

            // --- SURFACE BIOME DITHERING ---
            // Calculate a secondary biome index for surface/strata block types to organically dither boundaries
            // We use Simplex noise (snoise) with an irrational scale (0.23f) and distinct offsets
            // to avoid grid-aligned repeating artifacts commonly seen with Perlin (cnoise).
            float ditherNoiseX = noise.snoise(new float2(globalX * 0.23f + 1337f, globalZ * 0.23f + BaseSeed));
            float ditherNoiseZ = noise.snoise(new float2(globalX * 0.23f - 42f, globalZ * 0.23f - BaseSeed));
            float ditherX = globalX + ditherNoiseX * biome.SurfaceBlockDitheringWidth * 30f;
            float ditherZ = globalZ + ditherNoiseZ * biome.SurfaceBlockDitheringWidth * 30f;

            int surfaceBiomeIndex;
            if (IsSingleBiomeMode)
            {
                surfaceBiomeIndex = ForceBiomeIndex;
            }
            else
            {
                float ditheredBiomeNoise = BiomeSelectionNoise.GetNoise(ditherX, ditherZ);
                surfaceBiomeIndex = (int)math.floor(ditheredBiomeNoise * Biomes.Length);
                surfaceBiomeIndex = math.clamp(surfaceBiomeIndex, 0, Biomes.Length - 1);
            }

            StandardBiomeAttributesJobData surfaceBiome = Biomes[surfaceBiomeIndex];

            // --- TERRAIN HEIGHT (Multi-Noise spline blending) ---
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
                globalX, globalZ, ref BiomeSelectionNoise, ref Biomes, ref multiNoise, IsSingleBiomeMode, ForceBiomeIndex,
                out float borderFade);
            int terrainHeight = (int)math.floor(terrainHeightFloat);

            // --- Dynamic Density Band bounds ---
            // Attenuate 3D density amplitude near biome borders to prevent cliff tearing.
            // borderFade = 0.0 at the Voronoi boundary, 1.0 deep inside the primary biome.
            int baseTerrainHeight = terrainHeight;
            float effectiveDensityAmplitude = biome.DensityAmplitude * borderFade;
            int bandLow = baseTerrainHeight - (int)math.ceil(effectiveDensityAmplitude);
            int bandHigh = baseTerrainHeight + (int)math.ceil(effectiveDensityAmplitude);

            bool highestBlockFound = false;
            float previousDensity = -1f;
            int lastSurfaceY = baseTerrainHeight;

            // Pre-evaluate strata jitter once per column
            float strataDepthJitter = StrataDepthNoises[surfaceBiomeIndex].GetNoise(globalX, globalZ);
            int strataJitterBlocks = (int)math.round(strataDepthJitter * 2.5f);

            // Pre-evaluate cave zone noise once per column (per-layer attenuation applied inside the loop)
            float caveZoneNoise = CaveZoneNoises[biomeIndex].GetNoise(globalX, globalZ);

            // --- COLUMN ITERATION (top-down) ---
            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                // ReSharper disable once RedundantAssignment
                byte voxelValue = (byte)BlockIDs.Air;
                float density = baseTerrainHeight - y;

                // ----- 3D DENSITY BAND & DOMAIN WARPING -----
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
                        lastSurfaceY = y;
                        voxelValue = (y < SeaLevel - 1 && FeatureFlags.EnableWater) ? surfaceBiome.UnderwaterSurfaceBlockID : surfaceBiome.SurfaceBlockID;
                    }
                    else
                    {
                        // Subsurface strata — anchored to lastSurfaceY, NOT baseTerrainHeight
                        voxelValue = (byte)BlockIDs.Stone;
                        int depthCounter = 0;

                        for (int i = 0; i < surfaceBiome.TerrainLayerCount; i++)
                        {
                            StandardTerrainLayerJobData layer = AllTerrainLayers[surfaceBiome.TerrainLayerStartIndex + i];
                            int effectiveDepth = math.max(1, layer.Depth + strataJitterBlocks);

                            if (y < lastSurfaceY - depthCounter && y >= lastSurfaceY - depthCounter - effectiveDepth)
                            {
                                voxelValue = layer.BlockID;
                                break;
                            }

                            depthCounter += effectiveDepth;
                        }
                    }
                }
                else // density <= 0f
                {
                    voxelValue = (y < SeaLevel && FeatureFlags.EnableWater) ? (byte)BlockIDs.Water : (byte)BlockIDs.Air;
                }

                // Track whether this voxel is an exposed surface (air-to-solid transition from above)
                bool isExposedSurfaceForStructures = density > 0f && previousDensity <= 0f;
                previousDensity = density;

                // ----- LODE PASS (runs before cave carving so PreCaveBlockIDs captures post-lode values) -----
                if (FeatureFlags.EnableLodes && voxelValue == BlockIDs.Stone)
                {
                    for (int i = 0; i < biome.LodeCount; i++)
                    {
                        int lodeIdx = biome.LodeStartIndex + i;
                        StandardLodeJobData lode = AllLodes[lodeIdx];
                        if (y > lode.MinHeight && y < lode.MaxHeight)
                        {
                            FastNoiseLite lodeNoise = LodeNoises[lodeIdx];
                            float noiseVal = lodeNoise.GetNoise(globalX, y, globalZ);
                            if (noiseVal > lode.Threshold)
                            {
                                voxelValue = lode.BlockID;
                            }
                        }
                    }
                }

                // ----- CAVE CARVING PASS -----
                // Guard: only carve solid, non-fluid, non-bedrock blocks
                if (FeatureFlags.EnableCaves &&
                    voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock &&
                    BlockTypes[voxelValue].FluidType == FluidType.None)
                {
                    for (int i = 0; i < biome.CaveLayerCount; i++)
                    {
                        int caveIdx = biome.CaveLayerStartIndex + i;
                        StandardCaveLayerJobData caveLayer = AllCaveLayers[caveIdx];

                        if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight) continue;

                        float depthFade = StandardCaveLayerJobData.CalculateDepthFade(
                            y, caveLayer.MinHeight, caveLayer.MaxHeight,
                            caveLayer.DepthFadeMarginBottom, caveLayer.DepthFadeMarginTop);

                        float zoneBoost = caveLayer.ZoneAttenuation > 0f
                            ? (1f - caveZoneNoise) * 0.5f * caveLayer.ZoneAttenuation
                            : 0f;
                        float zoneBoostedThreshold = caveLayer.Threshold + zoneBoost;
                        float effectiveThreshold = zoneBoostedThreshold + (1f - depthFade) * (1f - zoneBoostedThreshold);

                        // --- WormCarver --- handled by pre-pass worm mask
                        if (caveLayer.Mode == CaveMode.WormCarver)
                        {
                            if (!FeatureFlags.EnableWormCarver) continue;

                            int flatIdx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                            if (WormMask.IsSet(flatIdx))
                            {
                                OutputPreCaveBlockIDs[flatIdx] = voxelValue;
                                OutputCaveMask[flatIdx] = 1;
                                voxelValue = (byte)BlockIDs.Air;
                                break;
                            }

                            continue;
                        }

                        FastNoiseLite caveNoise = CaveNoises[caveIdx];

                        // --- Cheese Caves (renamed from Blob) — large open caverns ---
                        if (caveLayer.Mode == CaveMode.Cheese)
                        {
                            if (!FeatureFlags.EnableCheese) continue;
                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp)
                                CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);

                            if (caveNoise.GetNoise(cx, cy, cz) > effectiveThreshold)
                            {
                                int flatIdx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                                OutputPreCaveBlockIDs[flatIdx] = voxelValue;
                                OutputCaveMask[flatIdx] = 1;
                                voxelValue = (byte)BlockIDs.Air;
                                break;
                            }
                        }
                        // --- Spaghetti2D — 6-way 2D axis-pair average ---
                        // Domain warp is NOT applied: 2D noise pairs would lose the Z-axis warp shift.
                        else if (caveLayer.Mode == CaveMode.Spaghetti2D)
                        {
                            if (!FeatureFlags.EnableSpaghetti) continue;

                            float bound = caveNoise.GetNoise(globalX * 0.25f, y * 0.25f, globalZ * 0.25f);
                            if (bound < effectiveThreshold - 0.2f) continue;

                            float noiseVal = (caveNoise.GetNoise(globalX, y) + caveNoise.GetNoise(y, globalZ) +
                                              caveNoise.GetNoise(globalX, globalZ) + caveNoise.GetNoise(y, globalX) +
                                              caveNoise.GetNoise(globalZ, y) + caveNoise.GetNoise(globalZ, globalX)) / 6f;

                            if (noiseVal > effectiveThreshold)
                            {
                                int flatIdx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                                OutputPreCaveBlockIDs[flatIdx] = voxelValue;
                                OutputCaveMask[flatIdx] = 1;
                                voxelValue = (byte)BlockIDs.Air;
                                break;
                            }
                        }
                        // --- Noodle (new) — winding tubular corridors via isoband ---
                        else if (caveLayer.Mode == CaveMode.Noodle)
                        {
                            if (!FeatureFlags.EnableNoodle) continue;

                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp)
                                CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);

                            float raw = caveNoise.GetNoise(cx, cy, cz);
                            float noiseVal = 1.0f - (math.sqrt(raw * raw + StandardCaveLayerJobData.NoodleSmoothRadiusSq) - StandardCaveLayerJobData.NoodleSmoothOffset);

                            if (noiseVal > effectiveThreshold)
                            {
                                int flatIdx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                                OutputPreCaveBlockIDs[flatIdx] = voxelValue;
                                OutputCaveMask[flatIdx] = 1;
                                voxelValue = (byte)BlockIDs.Air;
                                break;
                            }
                        }
                        // --- Spaghetti3D — dual zero-crossing intersection tunnels ---
                        else if (caveLayer.Mode == CaveMode.Spaghetti3D)
                        {
                            if (!FeatureFlags.EnableSpaghetti) continue;

                            float cx = globalX, cy = y, cz = globalZ;
                            if (caveLayer.EnableWarp)
                                CaveWarpNoises[caveIdx].DomainWarp(ref cx, ref cy, ref cz);

                            float rawA = caveNoise.GetNoise(cx, cy, cz);
                            float rawB = CaveSpaghetti3DNoises[caveIdx].GetNoise(cx, cy, cz);
                            float noiseVal = 1.0f - (math.sqrt(rawA * rawA + rawB * rawB
                                                                           + StandardCaveLayerJobData.Spaghetti3DSmoothRadiusSq)
                                                     - StandardCaveLayerJobData.Spaghetti3DSmoothOffset);

                            if (noiseVal > effectiveThreshold)
                            {
                                int flatIdx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                                OutputPreCaveBlockIDs[flatIdx] = voxelValue;
                                OutputCaveMask[flatIdx] = 1;
                                voxelValue = (byte)BlockIDs.Air;
                                break;
                            }
                        }
                    }
                }

                // --- Cache block properties (after all passes have finalized voxelValue) ---
                BlockTypeJobData voxelProps = BlockTypes[voxelValue];

                // --- Pack voxel data ---
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                byte packedMeta = BurstVoxelDataBitMapping.BuildMetaLegacy(
                    orientation: 1, fluidLevel: voxelProps.FluidLevel, isFluid: false);
                OutputMap[mapIndex] = BurstVoxelDataBitMapping.PackVoxelData(
                    voxelValue, 0, voxelProps.LightEmission, packedMeta);

                // --- Structure placement (per-entry independent grids) ---
                // Place structures on the topmost exposed solid surface above sea level.
                // Must run before heightmap tracking to ensure !highestBlockFound is still true.
                if (isExposedSurfaceForStructures && !highestBlockFound && y >= SeaLevel &&
                    voxelValue != BlockIDs.Air && voxelProps.FluidType == FluidType.None)
                {
                    // Pre-sample the biome's flora zone noise once for all entries that use it.
                    FastNoiseLite biomeFloraZoneNoise = FloraZoneNoises[biomeIndex];
                    float biomeZoneNoiseVal = biomeFloraZoneNoise.GetNoise(globalX, globalZ);
                    bool isInBiomeFloraZone = biomeZoneNoiseVal > 1f - biome.FloraZoneCoverage;

                    // Process major and minor flora pool entries
                    int totalPoolEntries = biome.MajorFloraPoolCount + biome.MinorFloraPoolCount;
                    for (int poolPass = 0; poolPass < totalPoolEntries; poolPass++)
                    {
                        bool isMajor = poolPass < biome.MajorFloraPoolCount;
                        if (isMajor && !FeatureFlags.EnableMajorFlora) continue;
                        if (!isMajor && !FeatureFlags.EnableMinorFlora) continue;

                        int entryIndex;
                        if (isMajor)
                            entryIndex = biome.MajorFloraPoolStartIndex + poolPass;
                        else
                            entryIndex = biome.MinorFloraPoolStartIndex + (poolPass - biome.MajorFloraPoolCount);

                        StructurePoolEntryJobData entry = AllStructurePoolEntries[entryIndex];

                        // Height bounds check
                        if (y < entry.MinPlacementHeight || y > entry.MaxPlacementHeight)
                            continue;

                        // Flora zone check (if this entry requires it)
                        if (entry.UseFloraZone)
                        {
                            if (entry.FloraZoneNoiseIndex >= 0)
                            {
                                // Per-entry override noise
                                float entryZoneNoiseVal = EntryFloraZoneNoises[entry.FloraZoneNoiseIndex]
                                    .GetNoise(globalX, globalZ);
                                if (entryZoneNoiseVal <= 1f - entry.FloraZoneCoverage)
                                    continue;
                            }
                            else
                            {
                                // Biome-level default
                                if (!isInBiomeFloraZone)
                                    continue;
                            }
                        }

                        // Grid-cell election with this entry's spacing
                        int spacing = math.max(1, entry.Spacing);
                        int cellX = (int)math.floor((float)globalX / spacing);
                        int cellZ = (int)math.floor((float)globalZ / spacing);

                        // Seed includes the entry index for independence between entries
                        uint cellHash = math.hash(new int4(cellX, cellZ, BaseSeed, entryIndex));
                        Random cellRandom = new Random(math.max(1u, cellHash));

                        // Calculate padding
                        int edgePadding;
                        if (entry.Padding < 0)
                        {
                            edgePadding = spacing >= 5 ? 1 : 0;
                        }
                        else
                        {
                            int maxPossiblePadding = (spacing - 1) / 2;
                            edgePadding = math.clamp(entry.Padding, 0, maxPossiblePadding);
                        }

                        // Define valid internal placement area
                        int innerMinX = cellX * spacing + edgePadding;
                        int innerMaxX = cellX * spacing + spacing - edgePadding;
                        int innerMinZ = cellZ * spacing + edgePadding;
                        int innerMaxZ = cellZ * spacing + spacing - edgePadding;

                        // Pick exactly one target column within this validated boundary range
                        int targetX = cellRandom.NextInt(innerMinX, innerMaxX);
                        int targetZ = cellRandom.NextInt(innerMinZ, innerMaxZ);

                        // Is THIS column the elected structural point for this entry's grid cell?
                        if (globalX == targetX && globalZ == targetZ)
                        {
                            // Chance roll
                            if (cellRandom.NextFloat() <= entry.Chance)
                            {
                                StructureSpawns.Enqueue(new StructureSpawnMarker
                                {
                                    Position = new int3(globalX, y, globalZ),
                                    PoolEntryIndex = entryIndex,
                                });
                            }
                        }
                    }
                }

                // --- Heightmap ---
                if (!highestBlockFound && voxelProps.IsLightObstructing)
                {
                    int heightmapIndex = x + VoxelData.ChunkWidth * z;
                    OutputHeightMap[heightmapIndex] = (ushort)y;
                    highestBlockFound = true;
                }
            }

            // If no opaque block was found in the column
            if (!highestBlockFound)
            {
                int heightmapIndex = x + VoxelData.ChunkWidth * z;
                OutputHeightMap[heightmapIndex] = 0;
            }
        }
    }
}
