using Data;
using Data.WorldTypes;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
using Libraries;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
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

        [ReadOnly] public int BaseSeed;

        /// <summary>
        /// Chunk position in voxel-space (world origin). Uses <c>int2</c> instead of <c>Vector2Int</c>
        /// to keep the math pipeline within <c>Unity.Mathematics</c> for Burst SIMD vectorization.
        /// </summary>
        [ReadOnly] public int2 ChunkPosition;

        [ReadOnly] public NativeArray<BlockTypeJobData> BlockTypes;
        [ReadOnly] public NativeArray<StandardBiomeAttributesJobData> Biomes;
        [ReadOnly] public NativeArray<StandardLodeJobData> AllLodes;
        [ReadOnly] public NativeArray<StandardCaveLayerJobData> AllCaveLayers;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's terrain noise.
        /// Indexed by biome index. Passed by value (72 bytes each).
        /// </summary>
        [ReadOnly] public NativeArray<FastNoiseLite> BiomeTerrainNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each lode's noise.
        /// Indexed matching AllLodes array.
        /// </summary>
        [ReadOnly] public NativeArray<FastNoiseLite> LodeNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's cave layers.
        /// Indexed alongside AllCaveLayers.
        /// </summary>
        [ReadOnly] public NativeArray<FastNoiseLite> CaveNoises;

        /// <summary>
        /// Pre-constructed FastNoiseLite instances for each biome's flora zones.
        /// </summary>
        [ReadOnly] public NativeArray<FastNoiseLite> FloraZoneNoises;

        /// <summary>
        /// Global biome selection noise (Cellular/Voronoi).
        /// </summary>
        public FastNoiseLite BiomeSelectionNoise;

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
            // Map noise output (-1..1) to biome index (0..N-1)
            int biomeIndex = (int)math.floor((biomeNoise + 1f) * 0.5f * Biomes.Length);
            biomeIndex = math.clamp(biomeIndex, 0, Biomes.Length - 1);

            StandardBiomeAttributesJobData biome = Biomes[biomeIndex];

            // --- TERRAIN HEIGHT (2D noise) ---
            FastNoiseLite terrainNoise = BiomeTerrainNoises[biomeIndex];
            float heightNoise = terrainNoise.GetNoise(globalX, globalZ); // Returns -1..1
            int terrainHeight = (int)math.floor(biome.BaseTerrainHeight + heightNoise * biome.TerrainAmplitude);
            terrainHeight = math.clamp(terrainHeight, 1, VoxelData.ChunkHeight - 1);

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
                    voxelValue = biome.SurfaceBlockID;
                }
                else if (y < terrainHeight && y > terrainHeight - 4)
                {
                    voxelValue = biome.SubSurfaceBlockID;
                }
                else if (y > terrainHeight)
                {
                    if (y < VoxelData.SeaLevel)
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
                    voxelValue = (byte)BlockIDs.Stone;
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

                        if (caveLayer.Mode == CaveMode.Spaghetti)
                        {
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

                        // Apply depth fade: raise the effective threshold near depth bounds
                        // (depthFade=0 at edge → threshold becomes unreachable, depthFade=1 inside → normal threshold)
                        float effectiveThreshold = caveLayer.Threshold + (1f - depthFade) * (1f - caveLayer.Threshold);

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
                            // FastNoiseLite returns -1..1; use threshold at 0.5 (equivalent to legacy > threshold)
                            if (noiseVal > 0.5f)
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

                // --- Flora placement ---
                // We only place flora on non-Air, non-Fluid, solid surface blocks
                if (y == terrainHeight && voxelValue != BlockIDs.Air &&
                    voxelProps.FluidType == FluidType.None && biome.EnableMajorFlora)
                {
                    FastNoiseLite floraZoneNoise = FloraZoneNoises[biomeIndex];
                    float zoneNoiseVal = floraZoneNoise.GetNoise(globalX, globalZ);

                    // Tier 1: Are we inside a flora zone (e.g., a forest/grove)?
                    // FastNoiseLite.NormalizeToZeroOne config is recommended for flora zone noises to easily use 0..1 ranges.
                    if (zoneNoiseVal > (1f - biome.MajorFloraZoneCoverage))
                    {
                        // Tier 2: The Grid. Divide the world into Spacing x Spacing cells.
                        int spacing = math.max(1, biome.MajorFloraPlacementSpacing);
                        
                        // Use float division to gracefully handle negative coordinates mathematically
                        int cellX = (int)math.floor((float)globalX / spacing);
                        int cellZ = (int)math.floor((float)globalZ / spacing);

                        // Seed a random generator specifically for this grid cell
                        uint cellHash = math.hash(new int3(cellX, cellZ, BaseSeed));
                        var cellRandom = new Random(math.max(1u, cellHash));

                        // Find the exact mathematical center of this cell
                        int centerX = cellX * spacing + spacing / 2;
                        int centerZ = cellZ * spacing + spacing / 2;

                        // Calculate jitter range. -1 = Automatic safe distance (prevents edge overlapping).
                        int maxJitterPotential = math.max(0, (spacing / 2) - 1);
                        int jitter = biome.MajorFloraPlacementJitter < 0 
                            ? maxJitterPotential 
                            : biome.MajorFloraPlacementJitter;

                        // Pick exactly one target column within this cell
                        int targetX = centerX + cellRandom.NextInt(-jitter, jitter + 1);
                        int targetZ = centerZ + cellRandom.NextInt(-jitter, jitter + 1);

                        // Is THIS column the elected structural point for this local grid cell?
                        if (globalX == targetX && globalZ == targetZ)
                        {
                            // Tier 3: Chance. Does the elected cell actually spawn a tree?
                            if (cellRandom.NextFloat() <= biome.MajorFloraPlacementChance)
                            {
                                // Enqueue a flora root point for main-thread structure generation.
                                Modifications.Enqueue(new VoxelMod(
                                    new Vector3Int(globalX, y, globalZ),
                                    biome.MajorFloraIndex));
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
