using Data;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

// This job CANNOT be Burst compiled because WorldGen.GetVoxel uses Mathf.PerlinNoise,
// which is managed code. The performance gain comes from multithreading, not Burst.
// [BurstCompile] 
namespace Jobs
{
    public struct ChunkGenerationJob : IJobFor
    {
        // --- Input Data ---

        #region Input Data

        [ReadOnly]
        public int Seed;

        [ReadOnly]
        public Vector2Int ChunkPosition;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        [ReadOnly]
        public NativeArray<BiomeAttributesJobData> Biomes;

        [ReadOnly]
        public NativeArray<LodeJobData> AllLodes;

        #endregion

        // --- Output Data ---

        #region Output Data

        [WriteOnly] // We only write to this array, never read from it inside the job.
        [NativeDisableParallelForRestriction]
        public NativeArray<uint> OutputMap;

        [WriteOnly]
        public NativeArray<byte> OutputHeightMap;

        [WriteOnly]
        public NativeQueue<VoxelMod>.ParallelWriter Modifications;

        #endregion

        // The Execute method now takes an index, which will represent one X/Z column in the chunk.
        public void Execute(int index)
        {
            // The loop order is now column-major, processed in parallel.
            int x = index % VoxelData.ChunkWidth;
            int z = index / VoxelData.ChunkWidth;

            bool highestBlockFound = false;

            // Loop from the top of the chunk downwards for this specific column.
            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                Vector3Int globalPos = new Vector3Int(x + ChunkPosition.x, y, z + ChunkPosition.y);

                byte voxelID = WorldGen.GetVoxel(globalPos, Seed, Biomes, AllLodes);
                BlockTypeJobData voxelProps = BlockTypes[voxelID];
                // --- Populate the main voxel map ---

                int mapIndex = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                OutputMap[mapIndex] = BurstVoxelDataBitMapping.PackVoxelData(voxelID, 0, voxelProps.LightEmission, 1, voxelProps.FluidLevel);

                // --- Populate the heightmap ---
                // If we haven't found the highest block in this column yet, check if this one is light-obstructing.
                if (!highestBlockFound)
                {
                    // Check the opacity from the blockTypes array.
                    if (BlockTypes[voxelID].IsLightObstructing)
                    {
                        int heightmapIndex = x + VoxelData.ChunkWidth * z;
                        OutputHeightMap[heightmapIndex] = (byte)y;
                        highestBlockFound = true;
                    }
                }

                // --- Major Flora Pass (Tree Generation) ---
                // We can't call Structure.GenerateMajorFlora directly as it's not job-safe.
                // Instead, we replicate the noise check here and queue the modification.
                if (y == GetTerrainHeight(globalPos, Biomes))
                {
                    BiomeAttributesJobData biome = GetStrongestBiome(globalPos, Biomes);
                    if (biome.PlaceMajorFlora)
                    {
                        if (Noise.Get2DPerlin(new Vector2(globalPos.x, globalPos.z), 0, biome.MajorFloraZoneScale) > biome.MajorFloraZoneThreshold)
                        {
                            if (Noise.Get2DPerlin(new Vector2(globalPos.x, globalPos.z), 2500, biome.MajorFloraPlacementScale) > biome.MajorFloraPlacementThreshold)
                            {
                                // We can't generate the whole structure here, but we can queue a "request"
                                // to generate it on the main thread later.
                                // For simplicity here, we'll queue the *base* of the structure.
                                // The main thread will then expand this into the full tree.
                                Modifications.Enqueue(new VoxelMod(globalPos, blockId: (byte)biome.MajorFloraIndex));
                            }
                        }
                    }
                }
            }

            // If after checking the whole column, no opaque block was found, set height to 0.
            if (!highestBlockFound)
            {
                int heightmapIndex = x + VoxelData.ChunkWidth * z;
                OutputHeightMap[heightmapIndex] = 0;
            }
        }

        // Helper to get terrain height for flora placement. Duplicated from GetVoxel to avoid re-calculating everything.
        private static int GetTerrainHeight(Vector3 pos, NativeArray<BiomeAttributesJobData> biomeArray)
        {
            float sumOfHeights = 0f;
            int count = 0;
            for (int i = 0; i < biomeArray.Length; i++)
            {
                float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomeArray[i].Offset, biomeArray[i].Scale);
                float height = biomeArray[i].TerrainHeight * Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomeArray[i].TerrainScale) * weight;
                if (height > 0)
                {
                    sumOfHeights += height;
                    count++;
                }
            }

            return Mathf.FloorToInt(sumOfHeights / count + VoxelData.SolidGroundHeight);
        }

        // Helper to get the strongest biome
        private static BiomeAttributesJobData GetStrongestBiome(Vector3 pos, NativeArray<BiomeAttributesJobData> biomeArray)
        {
            float strongestWeight = 0f;
            int strongestBiomeIndex = 0;
            for (int i = 0; i < biomeArray.Length; i++)
            {
                float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomeArray[i].Offset, biomeArray[i].Scale);
                if (weight > strongestWeight)
                {
                    strongestWeight = weight;
                    strongestBiomeIndex = i;
                }
            }

            return biomeArray[strongestBiomeIndex];
        }
    }
}