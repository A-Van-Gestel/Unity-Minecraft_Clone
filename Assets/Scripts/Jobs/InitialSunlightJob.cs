using Data;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public struct InitialSunlightJob : IJob
    {
        public NativeArray<uint> Map;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        public NativeList<Vector3Int> SunlightPropagationQueue;

        // The index here represents a column in the X/Z plane of the chunk
        public void Execute()
        {
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    // Track if the sunlight has been blocked by an opaque block.
                    bool obstructed = false;

                    // Loop from top to bottom of chunk.
                    for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
                    {
                        int mapIndex = x + VoxelData.ChunkWidth * (y + VoxelData.ChunkHeight * z);
                        uint packedData = Map[mapIndex];
                        BlockTypeJobData props = BlockTypes[BurstVoxelDataBitMapping.GetId(packedData)];

                        // If light has been obstructed, all blocks below this point are dark (light level 0).
                        if (obstructed)
                        {
                            Map[mapIndex] = BurstVoxelDataBitMapping.SetSunLight(packedData, 0);
                        }
                        // Else if this block is opaque, it obstructs the light.
                        // It becomes dark, and everything below it will also be dark.
                        else if (props.opacity > 0)
                        {
                            Map[mapIndex] = BurstVoxelDataBitMapping.SetSunLight(packedData, 0);
                            obstructed = true;
                        }
                        // Else the block is transparent (like air or glass), so sunlight passes through.
                        // Set its light level to the maximum (15) and add it to the propagation queue.
                        else
                        {
                            Map[mapIndex] = BurstVoxelDataBitMapping.SetSunLight(packedData, 15);
                            SunlightPropagationQueue.Add(new Vector3Int(x, y, z));
                        }
                    }
                }
            }
        }
    }
}