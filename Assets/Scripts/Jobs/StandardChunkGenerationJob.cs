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
    [BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
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

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's terrain noise.
        /// Indexed by biome index. Passed by value (72 bytes each).
        /// </summary>
        [ReadOnly]
        public NativeArray<FastNoiseLite> BiomeTerrainNoises;

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

        #endregion

        #region Output Data

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<uint> OutputMap;

        [WriteOnly]
        public NativeArray<ushort> OutputHeightMap;

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
            // Single evaluation per column — O(1) regardless of biome count.
            float biomeNoise = BiomeSelectionNoise.GetNoise(globalX, globalZ);
            // Noise is enforced to [0,1] normalization internally
            int biomeIndex = (int)math.floor(biomeNoise * Biomes.Length);
            biomeIndex = math.clamp(biomeIndex, 0, Biomes.Length - 1);

            StandardBiomeAttributesJobData biome = Biomes[biomeIndex];

            // --- SURFACE BIOME DITHERING ---
            // Calculate a secondary biome index for surface/strata block types to organically dither boundaries
            // We use Simplex noise (snoise) with an irrational scale (0.23f) and distinct offsets
            // to avoid grid-aligned repeating artifacts commonly seen with Perlin (cnoise).
            float ditherNoiseX = noise.snoise(new float2(globalX * 0.23f + 1337f, globalZ * 0.23f + BaseSeed));
            float ditherNoiseZ = noise.snoise(new float2(globalX * 0.23f - 42f, globalZ * 0.23f - BaseSeed));
            float ditherX = globalX + ditherNoiseX * biome.SurfaceBlockDitheringWidth * 30f;
            float ditherZ = globalZ + ditherNoiseZ * biome.SurfaceBlockDitheringWidth * 30f;

            float ditheredBiomeNoise = BiomeSelectionNoise.GetNoise(ditherX, ditherZ);
            int surfaceBiomeIndex = (int)math.floor(ditheredBiomeNoise * Biomes.Length);
            surfaceBiomeIndex = math.clamp(surfaceBiomeIndex, 0, Biomes.Length - 1);
            StandardBiomeAttributesJobData surfaceBiome = Biomes[surfaceBiomeIndex];

            // --- TERRAIN HEIGHT (2D noise blending) ---
            int terrainHeight = BiomeBlender.CalculateBlendedTerrainHeight(
                globalX, globalZ, ref BiomeSelectionNoise, ref Biomes, ref BiomeTerrainNoises);

            bool highestBlockFound = false;

            // --- COLUMN ITERATION (top-down) ---
            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                byte voxelValue;

                // ----- IMMUTABLE PASS -----
                if (y == 0)
                {
                    voxelValue = (byte)BlockIDs.Bedrock;
                }
                // ----- BASIC TERRAIN PASS -----
                else if (y == terrainHeight)
                {
                    voxelValue = y < SeaLevel - 1 ? surfaceBiome.UnderwaterSurfaceBlockID : surfaceBiome.SurfaceBlockID;
                }
                else if (y > terrainHeight)
                {
                    if (y < SeaLevel)
                    {
                        voxelValue = (byte)BlockIDs.Water;
                    }
                    else
                    {
                        voxelValue = (byte)BlockIDs.Air;
                    }
                }
                else
                {
                    // Default to stone
                    voxelValue = (byte)BlockIDs.Stone;

                    // Execute progressive dynamic subsurface strata layers top-down
                    int depthCounter = 0;

                    // Evaluate strata jitter noise (returns ~[-1, 1]) and scale by 2.5 blocks
                    float depthJitter = StrataDepthNoises[surfaceBiomeIndex].GetNoise(globalX, globalZ);
                    int jitterBlocks = (int)math.round(depthJitter * 2.5f);

                    for (int i = 0; i < surfaceBiome.TerrainLayerCount; i++)
                    {
                        StandardTerrainLayerJobData layer = AllTerrainLayers[surfaceBiome.TerrainLayerStartIndex + i];
                        int effectiveDepth = math.max(1, layer.Depth + jitterBlocks);

                        if (y < terrainHeight - depthCounter && y >= terrainHeight - depthCounter - effectiveDepth)
                        {
                            voxelValue = layer.BlockID;
                            break;
                        }

                        depthCounter += effectiveDepth;
                    }
                }

                // ----- SECOND PASS (Caves) -----
                // We do not carve Air, Fluids, or Bedrock
                if (voxelValue != BlockIDs.Air && voxelValue != BlockIDs.Bedrock &&
                    BlockTypes[voxelValue].FluidType == FluidType.None)
                {
                    for (int i = 0; i < biome.CaveLayerCount; i++)
                    {
                        int caveIdx = biome.CaveLayerStartIndex + i;
                        StandardCaveLayerJobData caveLayer = AllCaveLayers[caveIdx];

                        // --- Depth bounds check ---
                        if (y < caveLayer.MinHeight || y > caveLayer.MaxHeight)
                            continue;

                        // --- Depth fade (gradient attenuation near bounds) ---
                        float depthFade = 1f;
                        if (caveLayer.DepthFadeMargin > 0)
                        {
                            int distFromMin = y - caveLayer.MinHeight;
                            int distFromMax = caveLayer.MaxHeight - y;
                            int distFromEdge = math.min(distFromMin, distFromMax);
                            depthFade = math.saturate((float)distFromEdge / caveLayer.DepthFadeMargin);
                        }

                        // --- Noise evaluation (branched by CaveMode) ---
                        FastNoiseLite caveNoise = CaveNoises[caveIdx];
                        float noiseVal;

                        // Apply depth fade: raise the effective threshold near depth bounds
                        // (depthFade=0 at edge → threshold becomes unreachable, depthFade=1 inside → normal threshold)
                        float effectiveThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);

                        if (caveLayer.Mode == CaveMode.WormCarver)
                        {
                            // Worm carvers are pre-calculated in a scatter pass (StandardWormCarverJob).
                            // We just read the pre-calculated bitmask here.
                            if (WormMask.IsSet(ChunkMath.GetFlattenedIndexInChunk(x, y, z)))
                            {
                                voxelValue = (byte)BlockIDs.Air;
                                break;
                            }

                            continue; // Skip noise evaluation
                        }
                        else if (caveLayer.Mode == CaveMode.Spaghetti)
                        {
                            // Optimized Bounding Volume strategy: evaluate low-frequency 3D noise first.
                            // Scaling coordinates mimics evaluating a generalized broader volume.
                            float bound = caveNoise.GetNoise(globalX * 0.25f, y * 0.25f, globalZ * 0.25f);

                            // If the boundary check indicates highly dense solid rock, skip 6-way intersecting algorithm
                            if (bound < effectiveThreshold - 0.2f) continue;

                            // Legacy-style 6-way axis-pair 2D noise averaging.
                            // Creates intersecting ridges that form interconnected tunnel networks.
                            // Normalization to [0,1] is handled by FastNoiseLite.NormalizeToZeroOne via config.
                            float ab = caveNoise.GetNoise(globalX, y);
                            float bc = caveNoise.GetNoise(y, globalZ);
                            float ac = caveNoise.GetNoise(globalX, globalZ);
                            float ba = caveNoise.GetNoise(y, globalX);
                            float cb = caveNoise.GetNoise(globalZ, y);
                            float ca = caveNoise.GetNoise(globalZ, globalX);
                            noiseVal = (ab + bc + ac + ba + cb + ca) / 6f;
                        }
                        else // CaveMode.Blob
                        {
                            noiseVal = caveNoise.GetNoise(globalX, y, globalZ);
                        }

                        if (noiseVal > effectiveThreshold)
                        {
                            voxelValue = (byte)BlockIDs.Air;
                            break;
                        }
                    }
                }

                // ----- THIRD PASS (Lodes) -----
                if (voxelValue == BlockIDs.Stone)
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

                // --- Cache block properties (after all passes have finalized voxelValue) ---
                BlockTypeJobData voxelProps = BlockTypes[voxelValue];

                // --- Pack voxel data ---
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                OutputMap[mapIndex] = BurstVoxelDataBitMapping.PackVoxelData(
                    voxelValue, 0, voxelProps.LightEmission, 1, voxelProps.FluidLevel);

                // --- Heightmap ---
                if (!highestBlockFound && voxelProps.IsLightObstructing)
                {
                    int heightmapIndex = x + VoxelData.ChunkWidth * z;
                    OutputHeightMap[heightmapIndex] = (ushort)y;
                    highestBlockFound = true;
                }

                // --- Structure placement (per-entry independent grids) ---
                // We only place structures on non-Air, non-Fluid, solid surface blocks above sea level.
                if (y == terrainHeight && y >= SeaLevel &&
                    voxelValue != BlockIDs.Air && voxelProps.FluidType == FluidType.None)
                {
                    // Pre-sample the biome's flora zone noise once for all entries that use it.
                    FastNoiseLite biomeFloraZoneNoise = FloraZoneNoises[biomeIndex];
                    float biomeZoneNoiseVal = biomeFloraZoneNoise.GetNoise(globalX, globalZ);
                    bool isInBiomeFloraZone = biomeZoneNoiseVal > 1f - biome.FloraZoneCoverage;

                    // Process major flora pool entries
                    int totalPoolEntries = biome.MajorFloraPoolCount + biome.MinorFloraPoolCount;
                    for (int poolPass = 0; poolPass < totalPoolEntries; poolPass++)
                    {
                        int entryIndex;
                        if (poolPass < biome.MajorFloraPoolCount)
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
