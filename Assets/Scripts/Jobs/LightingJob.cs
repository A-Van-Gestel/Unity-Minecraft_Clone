using Data;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public struct LightingJob : IJob
    {
        // --- INPUT ---
        public NativeArray<ushort> map;
        public Vector2Int chunkPosition;
        public NativeQueue<LightQueueNode> lightBfsQueue;

        [ReadOnly]
        public NativeArray<ushort> neighborBack;

        [ReadOnly]
        public NativeArray<ushort> neighborFront;

        [ReadOnly]
        public NativeArray<ushort> neighborLeft;

        [ReadOnly]
        public NativeArray<ushort> neighborRight;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> blockTypes;

        // --- OUTPUT ---
        public NativeList<LightModification> crossChunkLightMods;

        public void Execute()
        {
            // We use two internal queues to manage the two-pass algorithm.
            var removalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var placementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);
            var visited = new NativeHashSet<Vector3Int>(1024, Allocator.Temp);
            var processedSunlightColumns = new NativeHashSet<Vector2Int>(128, Allocator.Temp);

            // --- PASS 0: SUNLIGHT & SEEDING PASS ---
            // Recalculate any potential sunlight propagation changes as needed,
            // and sort the initial requests from the main thread into our two queues.
            while (lightBfsQueue.TryDequeue(out LightQueueNode initialNode))
            {
                // --- PASS 0-1: SUNLIGHT PASS ---
                // We need to determine the opacity of the block that was *previously* here.
                // Since we don't have the old ID, we can check the new block's ID. If it's Air (ID 0),
                // we assume the old block was opaque. This is a robust simplification.
                ushort newPacked = GetPackedData(initialNode.position);
                if (newPacked == ushort.MaxValue) continue;

                byte newId = BurstVoxelDataBitMapping.GetId(newPacked);
                BlockTypeJobData newProps = blockTypes[newId];

                // Check if opacity *boundary* was crossed. We check the old light level to infer the old block.
                // If old light was less than 15, the block above was likely opaque.
                // A more direct check is if we replaced something with Air (0) or vice versa.
                bool oldBlockWasOpaque = newId == 0; // If new block is air, old was likely solid.
                bool newBlockIsOpaque = newProps.opacity > 0;

                if (oldBlockWasOpaque != newBlockIsOpaque)
                {
                    Vector2Int columnXZ = new Vector2Int(initialNode.position.x, initialNode.position.z);
                    if (processedSunlightColumns.Add(columnXZ))
                    {
                        // This column hasn't been processed yet. Run the sunlight calc.
                        RecalculateSunlightForColumn(columnXZ.x, columnXZ.y, placementQueue, removalQueue);
                    }
                }

                // --- PASS 0-2: SEEDING PASS ---
                // We re-read the packed data in case the sunlight pass changed it.
                newPacked = GetPackedData(initialNode.position);
                byte currentLightOnMap = BurstVoxelDataBitMapping.GetLight(newPacked);
                
                if (!visited.Add(initialNode.position)) continue;

                // A. If the light level on the map is now HIGHER than what it was,
                //    it must be a new light source. Add it to the placement queue.
                if (currentLightOnMap > initialNode.oldLightLevel)
                {
                    placementQueue.Enqueue(initialNode.position);
                }
                // B. If the light level on the map is now LOWER, it means light
                //    was removed. Add it to the removal queue.
                else if (currentLightOnMap < initialNode.oldLightLevel)
                {
                    removalQueue.Enqueue(new LightRemovalNode { pos = initialNode.position, lightLevel = initialNode.oldLightLevel });
                }
            }


            // --- PASS 1: DARKNESS PROPAGATION ---
            while (removalQueue.TryDequeue(out LightRemovalNode node))
            {
                // Check all 6 neighbors of the block being darkened.
                for (int i = 0; i < 6; i++)
                {
                    Vector3Int neighborPos = node.pos + BurstVoxelData.FaceChecks.Data[i];
                    ushort neighborPacked = GetPackedData(neighborPos);
                    if (neighborPacked == ushort.MaxValue) continue;

                    byte neighborLight = BurstVoxelDataBitMapping.GetLight(neighborPacked);

                    // If the neighbor's light is not 0...
                    if (neighborLight > 0)
                    {
                        // ...and it was dimmer than the light we just removed, it must have been lit by it.
                        // So, we darken it and add it to the queue to continue the darkness flood.
                        if (neighborLight < node.lightLevel)
                        {
                            // Set light will automatically handle neighbors that are in another chunk.
                            SetLight(neighborPos, 0);
                            // Only add to the INTERNAL queue if the neighbor is in THIS chunk.
                            if (IsVoxelInChunk(neighborPos))
                            {
                                removalQueue.Enqueue(new LightRemovalNode { pos = neighborPos, lightLevel = neighborLight });
                            }
                        }
                        // Otherwise, the neighbor is a light source itself (or lit by something brighter).
                        // We must queue it for re-propagation to fill the new darkness.
                        else
                        {
                            // If the bright neighbor is in THIS chunk, add it to our local placement queue.
                            if (IsVoxelInChunk(neighborPos))
                            {
                                placementQueue.Enqueue(neighborPos);
                            }
                            // If the bright neighbor is in ANOTHER chunk, we can't process it.
                            // Instead, create a modification request for the main thread.
                            // This tells the main thread to re-queue the neighbor in ITS OWN CHUNK's lighting job.
                            else
                            {
                                crossChunkLightMods.Add(new LightModification
                                {
                                    globalPosition = ToGlobal(neighborPos),
                                    lightLevel = neighborLight // Use its current light level
                                });
                            }
                        }
                    }
                }
            }

            // --- PASS 2: LIGHT PROPAGATION ---
            // This pass re-lights the darkened areas from the sources we identified.
            visited.Clear();
            while (placementQueue.TryDequeue(out Vector3Int pos))
            {
                if (!visited.Add(pos)) continue;

                ushort sourcePacked = GetPackedData(pos);
                if (sourcePacked == ushort.MaxValue) continue;

                byte sourceLight = BurstVoxelDataBitMapping.GetLight(sourcePacked);

                for (int i = 0; i < 6; i++)
                {
                    Vector3Int neighborPos = pos + BurstVoxelData.FaceChecks.Data[i];
                    ushort neighborPacked = GetPackedData(neighborPos);
                    if (neighborPacked == ushort.MaxValue) continue;

                    byte neighborLight = BurstVoxelDataBitMapping.GetLight(neighborPacked);
                    BlockTypeJobData neighborProps = blockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                    // Light reduces by at least 1 for air, or more for blocks with opacity.
                    byte lightAfterOpacity = (byte)Mathf.Max(0, sourceLight - Mathf.Max(1, neighborProps.opacity));

                    // Special handling for sunlight. If the source is full sunlight (15) and
                    // we are propagating straight down into a transparent block, do NOT decrease light level.
                    bool isVerticalSunlight = sourceLight == 15 &&
                                              BurstVoxelData.FaceChecks.Data[i].y == -1 &&
                                              neighborProps.opacity == 0;

                    if (isVerticalSunlight)
                    {
                        lightAfterOpacity = 15;
                    }

                    // If we can make the neighbor brighter, update it and add it to the queue.
                    if (lightAfterOpacity > neighborLight)
                    {
                        SetLight(neighborPos, lightAfterOpacity);
                        // Only add to the INTERNAL queue if the neighbor is in THIS chunk.
                        if (IsVoxelInChunk(neighborPos))
                        {
                            placementQueue.Enqueue(neighborPos);
                        }
                    }
                }
            }

            // Clean up temporary collections.
            removalQueue.Dispose();
            placementQueue.Dispose();
            visited.Dispose();
            processedSunlightColumns.Dispose();
        }

        #region Helper Methods

        private void RecalculateSunlightForColumn(int x, int z, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            bool obstructed = false;
            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                var pos = new Vector3Int(x, y, z);
                ushort packedData = map[GetIndex(pos)];
                byte oldLight = BurstVoxelDataBitMapping.GetLight(packedData);
                byte newLight;

                if (obstructed)
                {
                    newLight = 0;
                }
                else
                {
                    BlockTypeJobData props = blockTypes[BurstVoxelDataBitMapping.GetId(packedData)];
                    if (props.opacity > 0)
                    {
                        newLight = 0;
                        obstructed = true;
                    }
                    else
                    {
                        newLight = 15;
                    }
                }

                if (oldLight != newLight)
                {
                    map[GetIndex(pos)] = VoxelData.SetLight(packedData, newLight);
                    if (newLight > oldLight)
                    {
                        pQueue.Enqueue(pos); // This is a new light source
                    }
                    else
                    {
                        rQueue.Enqueue(new LightRemovalNode { pos = pos, lightLevel = oldLight }); // This source was removed
                    }
                }
            }
        }

        private void SetLight(Vector3Int pos, byte lightLevel)
        {
            ushort packedData = GetPackedData(pos);
            if (packedData == ushort.MaxValue) return;

            ushort newPackedData = VoxelData.SetLight(packedData, lightLevel);

            if (IsVoxelInChunk(pos))
            {
                map[GetIndex(pos)] = newPackedData;
            }
            else // It's in another chunk, so queue a modification request for the main thread.
            {
                crossChunkLightMods.Add(new LightModification
                {
                    globalPosition = ToGlobal(pos),
                    lightLevel = lightLevel
                });
            }
        }

        private bool IsVoxelInChunk(Vector3Int pos)
        {
            return pos.x >= 0 && pos.x < VoxelData.ChunkWidth &&
                   pos.y >= 0 && pos.y < VoxelData.ChunkHeight &&
                   pos.z >= 0 && pos.z < VoxelData.ChunkWidth;
        }

        private int GetIndex(Vector3Int pos) => pos.x + VoxelData.ChunkWidth * (pos.y + VoxelData.ChunkHeight * pos.z);

        private ushort GetPackedData(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= VoxelData.ChunkHeight) return ushort.MaxValue;

            if (IsVoxelInChunk(pos)) return map[GetIndex(pos)];

            // Neighbor lookups
            if (pos.x < 0) return neighborLeft.IsCreated ? neighborLeft[GetIndex(new Vector3Int(VoxelData.ChunkWidth - 1, pos.y, pos.z))] : ushort.MaxValue;
            if (pos.x >= VoxelData.ChunkWidth) return neighborRight.IsCreated ? neighborRight[GetIndex(new Vector3Int(0, pos.y, pos.z))] : ushort.MaxValue;
            if (pos.z < 0) return neighborBack.IsCreated ? neighborBack[GetIndex(new Vector3Int(pos.x, pos.y, VoxelData.ChunkWidth - 1))] : ushort.MaxValue;
            if (pos.z >= VoxelData.ChunkWidth) return neighborFront.IsCreated ? neighborFront[GetIndex(new Vector3Int(pos.x, pos.y, 0))] : ushort.MaxValue;

            return ushort.MaxValue;
        }

        private Vector3Int ToGlobal(Vector3Int localPos)
        {
            return new Vector3Int(localPos.x + chunkPosition.x, localPos.y, localPos.z + chunkPosition.y);
        }

        #endregion
    }
}

/// <summary>
/// A struct to hold data for the light removal pass.
/// </summary>
public struct LightRemovalNode
{
    public Vector3Int pos;
    public byte lightLevel;
}

/// <summary>
/// A struct to hold data for the cross-chunk light modifications.
/// </summary>
public struct LightModification
{
    public Vector3Int globalPosition;
    public byte lightLevel;
}