using Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

// This job CANNOT be Burst compiled because WorldGen.GetVoxel uses Mathf.PerlinNoise,
// which is managed code. The performance gain comes from multithreading, not Burst.
// [BurstCompile] 
namespace Jobs
{
    public struct ChunkGenerationJob : IJob
    {
        [ReadOnly]
        public int seed;

        [ReadOnly]
        public Vector2Int chunkPosition;

        [ReadOnly]
        public NativeArray<BiomeAttributesJobData> biomes;

        [ReadOnly]
        public NativeArray<LodeJobData> allLodes;

        // This is the output of our job
        public NativeArray<ushort> outputMap;
        public NativeQueue<VoxelMod>.ParallelWriter modifications;

        public void Execute()
        {
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int x = 0; x < VoxelData.ChunkWidth; x++)
                {
                    for (int z = 0; z < VoxelData.ChunkWidth; z++)
                    {
                        Vector3Int globalPos = new Vector3Int(x + chunkPosition.x, y, z + chunkPosition.y);

                        byte voxelID = WorldGen.GetVoxel(globalPos, seed, biomes, allLodes);

                        int index = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                        outputMap[index] = VoxelData.PackVoxelData(voxelID, 0, 1);

                        // --- Major Flora Pass (Tree Generation) ---
                        // We can't call Structure.GenerateMajorFlora directly as it's not job-safe.
                        // Instead, we replicate the noise check here and queue the modification.
                        if (y == GetTerrainHeight(globalPos, biomes))
                        {
                            BiomeAttributesJobData biome = GetStrongestBiome(globalPos, biomes);
                            if (biome.placeMajorFlora)
                            {
                                if (Noise.Get2DPerlin(new Vector2(globalPos.x, globalPos.z), 0, biome.majorFloraZoneScale) > biome.majorFloraZoneThreshold)
                                {
                                    if (Noise.Get2DPerlin(new Vector2(globalPos.x, globalPos.z), 2500, biome.majorFloraPlacementScale) > biome.majorFloraPlacementThreshold)
                                    {
                                        // We can't generate the whole structure here, but we can queue a "request"
                                        // to generate it on the main thread later.
                                        // For simplicity here, we'll queue the *base* of the structure.
                                        // The main thread will then expand this into the full tree.
                                        modifications.Enqueue(new VoxelMod(globalPos, (byte)biome.majorFloraIndex));
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Helper to get terrain height for flora placement. Duplicated from GetVoxel to avoid re-calculating everything.
        private int GetTerrainHeight(Vector3 pos, NativeArray<BiomeAttributesJobData> biomeArray)
        {
            float sumOfHeights = 0f;
            int count = 0;
            for (int i = 0; i < biomeArray.Length; i++)
            {
                float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomeArray[i].offset, biomeArray[i].scale);
                float height = biomeArray[i].terrainHeight * Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomeArray[i].terrainScale) * weight;
                if (height > 0)
                {
                    sumOfHeights += height;
                    count++;
                }
            }

            return Mathf.FloorToInt((sumOfHeights / count) + VoxelData.SolidGroundHeight);
        }

        // Helper to get the strongest biome
        private BiomeAttributesJobData GetStrongestBiome(Vector3 pos, NativeArray<BiomeAttributesJobData> biomeArray)
        {
            float strongestWeight = 0f;
            int strongestBiomeIndex = 0;
            for (int i = 0; i < biomeArray.Length; i++)
            {
                float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomeArray[i].offset, biomeArray[i].scale);
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