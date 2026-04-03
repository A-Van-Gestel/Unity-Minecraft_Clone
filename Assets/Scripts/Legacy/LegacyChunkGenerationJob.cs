using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Legacy
{
    /// <summary>
    /// Legacy chunk generation job using <c>Mathf.PerlinNoise</c>. Frozen — do not modify.
    /// Renamed from <c>ChunkGenerationJob.cs</c>. This job CANNOT be Burst-compiled
    /// because <c>LegacyWorldGen.GetVoxel</c> uses managed <c>Mathf.PerlinNoise</c>.
    /// </summary>
    /// <remarks>
    /// <b>INTENTIONAL BUG PRESERVATION:</b> The O(N²) biome noise evaluation
    /// (where <c>LegacyNoise.Get2DPerlin</c> is recalculated for every Y step inside the
    /// column loop via the <c>GetTerrainHeight</c> helper) is preserved exactly as-is.
    /// Fixing it would alter the deterministic output and break legacy seed reproducibility.
    /// </remarks>
    public struct LegacyChunkGenerationJob : IJobFor
    {
        #region Input Data

        [ReadOnly] public int Seed;
        [ReadOnly] public Vector2Int ChunkPosition;
        [ReadOnly] public NativeArray<BlockTypeJobData> BlockTypes;
        [ReadOnly] public NativeArray<LegacyBiomeAttributesJobData> Biomes;
        [ReadOnly] public NativeArray<LegacyLodeJobData> AllLodes;

        #endregion

        #region Output Data

        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<uint> OutputMap;

        [WriteOnly]
        public NativeArray<ushort> OutputHeightMap;

        [WriteOnly]
        public NativeQueue<VoxelMod>.ParallelWriter Modifications;

        #endregion

        /// <summary>
        /// Generates the voxel data for a single vertical column (X/Z coordinate pair) in the chunk.
        /// </summary>
        /// <param name="index">The 1D flattened index representing the X and Z coordinates of the column (0 to 255).</param>
        public void Execute(int index)
        {
            int x = index % VoxelData.ChunkWidth;
            int z = index / VoxelData.ChunkWidth;

            bool highestBlockFound = false;

            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                Vector3Int globalPos = new Vector3Int(x + ChunkPosition.x, y, z + ChunkPosition.y);

                byte voxelID = LegacyWorldGen.GetVoxel(globalPos, Seed, Biomes, AllLodes);
                BlockTypeJobData voxelProps = BlockTypes[voxelID];

                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                OutputMap[mapIndex] = BurstVoxelDataBitMapping.PackVoxelData(voxelID, 0, voxelProps.LightEmission, 1, voxelProps.FluidLevel);

                if (!highestBlockFound)
                {
                    if (BlockTypes[voxelID].IsLightObstructing)
                    {
                        int heightmapIndex = x + VoxelData.ChunkWidth * z;
                        OutputHeightMap[heightmapIndex] = (byte)y;
                        highestBlockFound = true;
                    }
                }

                // --- Major Flora Pass ---
                if (y == GetTerrainHeight(globalPos, Biomes))
                {
                    LegacyBiomeAttributesJobData biome = GetStrongestBiome(globalPos, Biomes);
                    if (biome.PlaceMajorFlora)
                    {
                        if (LegacyNoise.Get2DPerlin(new Vector2(globalPos.x, globalPos.z), 0, biome.MajorFloraZoneScale) > biome.MajorFloraZoneThreshold)
                        {
                            if (LegacyNoise.Get2DPerlin(new Vector2(globalPos.x, globalPos.z), 2500, biome.MajorFloraPlacementScale) > biome.MajorFloraPlacementThreshold)
                            {
                                Modifications.Enqueue(new VoxelMod(globalPos, blockId: (byte)biome.MajorFloraIndex));
                            }
                        }
                    }
                }
            }

            if (!highestBlockFound)
            {
                int heightmapIndex = x + VoxelData.ChunkWidth * z;
                OutputHeightMap[heightmapIndex] = 0;
            }
        }

        #region Helper Methods

        private static int GetTerrainHeight(Vector3 pos, NativeArray<LegacyBiomeAttributesJobData> biomeArray)
        {
            float sumOfHeights = 0f;
            int count = 0;
            for (int i = 0; i < biomeArray.Length; i++)
            {
                float weight = LegacyNoise.Get2DPerlin(new Vector2(pos.x, pos.z), biomeArray[i].Offset, biomeArray[i].Scale);
                float height = biomeArray[i].TerrainHeight *
                               LegacyNoise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomeArray[i].TerrainScale) * weight;
                if (height > 0)
                {
                    sumOfHeights += height;
                    count++;
                }
            }

            return Mathf.FloorToInt(sumOfHeights / count + VoxelData.SolidGroundHeight);
        }

        private static LegacyBiomeAttributesJobData GetStrongestBiome(Vector3 pos, NativeArray<LegacyBiomeAttributesJobData> biomeArray)
        {
            float strongestWeight = 0f;
            int strongestBiomeIndex = 0;
            for (int i = 0; i < biomeArray.Length; i++)
            {
                float weight = LegacyNoise.Get2DPerlin(new Vector2(pos.x, pos.z), biomeArray[i].Offset, biomeArray[i].Scale);
                if (weight > strongestWeight)
                {
                    strongestWeight = weight;
                    strongestBiomeIndex = i;
                }
            }

            return biomeArray[strongestBiomeIndex];
        }

        #endregion
    }
}
