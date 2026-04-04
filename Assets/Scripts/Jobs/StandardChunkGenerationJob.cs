using Data;
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
                    voxelValue = 8; // Bedrock
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
                        voxelValue = 19; // Water
                    }
                    else
                    {
                        voxelValue = 0; // Air
                    }
                }
                else
                {
                    voxelValue = 1; // Stone
                }

                // ----- SECOND PASS (Lodes) -----
                if (voxelValue == 1)
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

                // --- Pack voxel data ---
                BlockTypeJobData voxelProps = BlockTypes[voxelValue];
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
                if (y == terrainHeight && voxelValue != 0 && voxelValue != 19)
                {
                    // Deterministic random per column for flora placement
                    uint deterministicSeed = math.max(1u, math.hash(new int3(globalX, globalZ, BaseSeed)));
                    var random = new Random(deterministicSeed);

                    if (biome.EnableMajorFlora && random.NextFloat() > biome.MajorFloraPlacementThreshold)
                    {
                        // Enqueue a flora root point for main-thread structure generation.
                        Modifications.Enqueue(new VoxelMod(
                            new Vector3Int(globalX, y, globalZ),
                            biome.MajorFloraIndex));
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
