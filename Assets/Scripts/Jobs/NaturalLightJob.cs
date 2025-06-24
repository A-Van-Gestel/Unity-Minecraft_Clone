using Data;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public struct NaturalLightJob : IJob
    {
        public NativeArray<ushort> map;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> blockTypes;

        public NativeList<Vector3Int> sunlitVoxels;

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
                        ushort packedData = map[mapIndex];
                        BlockTypeJobData props = blockTypes[BurstVoxelDataBitMapping.GetId(packedData)];

                        // If light has been obstructed, all blocks below this point are dark (light level 0).
                        if (obstructed)
                        {
                            map[mapIndex] = BurstVoxelDataBitMapping.SetLight(packedData, 0);
                        }
                        // Else if this block is opaque, it obstructs the light.
                        // It becomes dark, and everything below it will also be dark.
                        // TODO: Check if this is correct, seems to be wrong. Shouldn't blocks with opacity only get slightly darkened? 
                        else if (props.opacity > 0)
                        {
                            map[mapIndex] = BurstVoxelDataBitMapping.SetLight(packedData, 0);
                            obstructed = true;
                        }
                        // Else the block is transparent (like air or glass), so sunlight passes through.
                        // Set its light level to the maximum (15) and add it to the propagation queue.
                        else
                        {
                            map[mapIndex] = BurstVoxelDataBitMapping.SetLight(packedData, 15);
                            sunlitVoxels.Add(new Vector3Int(x, y, z));
                        }
                    }
                }
            }
        }
    }
}