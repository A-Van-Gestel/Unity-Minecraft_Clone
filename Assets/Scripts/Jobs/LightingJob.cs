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
        public NativeArray<uint> map;
        public Vector2Int chunkPosition;

        // Queues of changes to process
        public NativeQueue<LightQueueNode> sunlightBfsQueue;
        public NativeQueue<LightQueueNode> blocklightBfsQueue;
        public NativeQueue<Vector2Int> sunlightColumnRecalcQueue;

        [ReadOnly]
        public NativeArray<uint> neighborBack;

        [ReadOnly]
        public NativeArray<uint> neighborFront;

        [ReadOnly]
        public NativeArray<uint> neighborLeft;

        [ReadOnly]
        public NativeArray<uint> neighborRight;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> blockTypes;

        // --- OUTPUT ---
        public NativeList<LightModification> crossChunkLightMods;

        public void Execute()
        {
            // Internal queues for the actual flood-fill algorithm
            var sunlightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var sunlightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);

            var blocklightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var blocklightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);

            // --- PASS 0: SEEDING ---
            // First, process any full column rescans. This is the most reliable way to handle sunlight changes.
            while (sunlightColumnRecalcQueue.TryDequeue(out Vector2Int column))
            {
                RecalculateSunlightForColumn(column.x, column.y, sunlightPlacementQueue, sunlightRemovalQueue);
            }

            // Next, process discrete sunlight changes.
            while (sunlightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.position);
                byte currentLight = BurstVoxelDataBitMapping.GetSunlight(currentPacked);

                if (currentLight < node.oldLightLevel)
                {
                    // A sunlit block was removed. Queue for darkness propagation.
                    sunlightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.position, LightLevel = node.oldLightLevel });
                }
                else if (currentLight > node.oldLightLevel)
                {
                    // A new sunlit path was created. Queue for light propagation.
                    sunlightPlacementQueue.Enqueue(node.position);
                }
            }

            // Process blocklight changes.
            while (blocklightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.position);
                byte currentLight = BurstVoxelDataBitMapping.GetBlocklight(currentPacked);

                if (currentLight > node.oldLightLevel)
                {
                    blocklightPlacementQueue.Enqueue(node.position);
                }
                else if (currentLight < node.oldLightLevel)
                {
                    blocklightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.position, LightLevel = node.oldLightLevel });
                }
            }

            // --- PASS 1: SUNLIGHT - DARKNESS & RE-LIGHTING ---
            // As we remove sunlight, we find adjacent brighter blocks and queue them to re-flood the new darkness.
            while (sunlightRemovalQueue.TryDequeue(out LightRemovalNode node))
            {
                PropagateSunlightDarkness(node, sunlightPlacementQueue, sunlightRemovalQueue);
            }

            // --- PASS 2: SUNLIGHT - LIGHT PROPAGATION ---
            while (sunlightPlacementQueue.TryDequeue(out Vector3Int pos))
            {
                PropagateLight(pos, LightChannel.Sun, sunlightPlacementQueue);
            }

            // --- PASS 3 & 4: BLOCKLIGHT PROPAGATION (This logic can remain as is) ---
            while (blocklightRemovalQueue.TryDequeue(out LightRemovalNode node))
            {
                PropagateBlockLightDarkness(node, LightChannel.Block, blocklightPlacementQueue, blocklightRemovalQueue);
            }

            while (blocklightPlacementQueue.TryDequeue(out Vector3Int pos))
            {
                PropagateLight(pos, LightChannel.Block, blocklightPlacementQueue);
            }

            // --- CLEANUP ---
            sunlightRemovalQueue.Dispose();
            sunlightPlacementQueue.Dispose();
            blocklightRemovalQueue.Dispose();
            blocklightPlacementQueue.Dispose();
        }

        #region Core Logic

        private void PropagateSunlightDarkness(LightRemovalNode node, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborSunlight = BurstVoxelDataBitMapping.GetSunlight(neighborPacked);

                // If the neighbor was dimmer than the node we are removing, it must have been lit by it.
                // So, darken it and continue the darkness flood.
                if (neighborSunlight > 0 && neighborSunlight < node.LightLevel)
                {
                    SetLight(neighborPos, 0, LightChannel.Sun);
                    if (IsVoxelInChunk(neighborPos))
                    {
                        rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborSunlight });
                    }
                }
                // If the neighbor is brighter or equal, it's a light source (or lit by one).
                // Queue it for light placement to re-fill the void we just created.
                else if (neighborSunlight >= node.LightLevel)
                {
                    if (IsVoxelInChunk(neighborPos))
                    {
                        pQueue.Enqueue(neighborPos);
                    }
                    else
                    {
                        crossChunkLightMods.Add(new LightModification { GlobalPosition = ToGlobal(neighborPos), LightLevel = neighborSunlight, Channel = LightChannel.Sun });
                    }
                }
            }
        }

        private void PropagateBlockLightDarkness(LightRemovalNode node, LightChannel channel, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = (channel == LightChannel.Sun) ? BurstVoxelDataBitMapping.GetSunlight(neighborPacked) : BurstVoxelDataBitMapping.GetBlocklight(neighborPacked);

                if (neighborLight > 0)
                {
                    if (neighborLight < node.LightLevel)
                    {
                        SetLight(neighborPos, 0, channel);
                        if (IsVoxelInChunk(neighborPos))
                        {
                            rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborLight });
                        }
                    }
                    else
                    {
                        if (IsVoxelInChunk(neighborPos))
                        {
                            pQueue.Enqueue(neighborPos);
                        }
                        else
                        {
                            crossChunkLightMods.Add(new LightModification { GlobalPosition = ToGlobal(neighborPos), LightLevel = neighborLight, Channel = channel });
                        }
                    }
                }
            }
        }

        private void PropagateLight(Vector3Int pos, LightChannel channel, NativeQueue<Vector3Int> pQueue)
        {
            uint sourcePacked = GetPackedData(pos);
            if (sourcePacked == uint.MaxValue) return;
            byte sourceLight = (channel == LightChannel.Sun) ? BurstVoxelDataBitMapping.GetSunlight(sourcePacked) : BurstVoxelDataBitMapping.GetBlocklight(sourcePacked);

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = (channel == LightChannel.Sun) ? BurstVoxelDataBitMapping.GetSunlight(neighborPacked) : BurstVoxelDataBitMapping.GetBlocklight(neighborPacked);
                BlockTypeJobData neighborProps = blockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                byte lightAfterOpacity = (byte)Mathf.Max(0, sourceLight - neighborProps.opacity - 1);

                // Sunlight has a special rule: it doesn't decay when traveling straight down through transparent blocks.
                bool isVerticalSunlight = channel == LightChannel.Sun && sourceLight == 15 && VoxelData.FaceChecks[i].y == -1 && neighborProps.opacity == 0;
                if (isVerticalSunlight)
                {
                    lightAfterOpacity = 15;
                }

                if (lightAfterOpacity > neighborLight)
                {
                    SetLight(neighborPos, lightAfterOpacity, channel);
                    if (IsVoxelInChunk(neighborPos))
                    {
                        pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        // This implementation is too simple and causes issues with semi-transparent blocks.
        // It should be changed to correctly handle diminishing light levels.
        private void RecalculateSunlightForColumn(int x, int z, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            byte currentSunlight = 15; // Start with full sunlight from the sky.

            for (int y = VoxelData.ChunkHeight - 1; y >= 0; y--)
            {
                if (currentSunlight == 0)
                {
                    // Optimization: If light is zero, everything below is also zero.
                    // We still need to check for changes to queue removals correctly.
                    var pos = new Vector3Int(x, y, z);
                    uint packedData = map[GetIndex(pos)];
                    byte oldLight = BurstVoxelDataBitMapping.GetSunlight(packedData);
                    if (oldLight > 0)
                    {
                        SetLight(pos, 0, LightChannel.Sun);
                        rQueue.Enqueue(new LightRemovalNode { Pos = pos, LightLevel = oldLight });
                    }

                    continue;
                }

                var currentPos = new Vector3Int(x, y, z);
                uint currentPacked = map[GetIndex(currentPos)];
                byte oldSunlight = BurstVoxelDataBitMapping.GetSunlight(currentPacked);
                BlockTypeJobData props = blockTypes[BurstVoxelDataBitMapping.GetId(currentPacked)];

                // Update the block's light level
                if (oldSunlight != currentSunlight)
                {
                    SetLight(currentPos, currentSunlight, LightChannel.Sun);
                    // Queue updates based on the change
                    if (currentSunlight > oldSunlight)
                    {
                        pQueue.Enqueue(currentPos);
                    }
                    else
                    {
                        rQueue.Enqueue(new LightRemovalNode { Pos = currentPos, LightLevel = oldSunlight });
                    }
                }

                // Diminish the light for the next block down
                currentSunlight = (byte)Mathf.Max(0, currentSunlight - props.opacity);
            }
        }

        #endregion

        #region Helper Methods

        private void SetLight(Vector3Int pos, byte lightLevel, LightChannel channel)
        {
            uint packedData = GetPackedData(pos);
            if (packedData == uint.MaxValue) return;

            uint newPackedData = (channel == LightChannel.Sun)
                ? BurstVoxelDataBitMapping.SetSunLight(packedData, lightLevel)
                : BurstVoxelDataBitMapping.SetBlockLight(packedData, lightLevel);

            if (IsVoxelInChunk(pos)) map[GetIndex(pos)] = newPackedData;
            else crossChunkLightMods.Add(new LightModification { GlobalPosition = ToGlobal(pos), LightLevel = lightLevel, Channel = channel });
        }

        private bool IsVoxelInChunk(Vector3Int pos) => pos.x >= 0 && pos.x < VoxelData.ChunkWidth && pos.y >= 0 && pos.y < VoxelData.ChunkHeight && pos.z >= 0 && pos.z < VoxelData.ChunkWidth;
        private int GetIndex(Vector3Int pos) => pos.x + VoxelData.ChunkWidth * (pos.y + VoxelData.ChunkHeight * pos.z);
        private Vector3Int ToGlobal(Vector3Int localPos) => new Vector3Int(localPos.x + chunkPosition.x, localPos.y, localPos.z + chunkPosition.y);

        private uint GetPackedData(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= VoxelData.ChunkHeight) return uint.MaxValue; // Return "invalid"
            if (IsVoxelInChunk(pos)) return map[GetIndex(pos)];

            // Check neighbor coordinates
            if (pos.x < 0)
            {
                // Check if the neighbor array is created and has elements, main loop will skip "uint.MaxValue" if not.
                if (!neighborLeft.IsCreated || neighborLeft.Length == 0) return uint.MaxValue;
                return neighborLeft[GetIndex(new Vector3Int(VoxelData.ChunkWidth - 1, pos.y, pos.z))];
            }

            if (pos.x >= VoxelData.ChunkWidth)
            {
                // Check if the neighbor array is created and has elements, main loop will skip "uint.MaxValue" if not.
                if (!neighborRight.IsCreated || neighborRight.Length == 0) return uint.MaxValue;
                return neighborRight[GetIndex(new Vector3Int(0, pos.y, pos.z))];
            }

            if (pos.z < 0)
            {
                // Check if the neighbor array is created and has elements, main loop will skip "uint.MaxValue" if not.
                if (!neighborBack.IsCreated || neighborBack.Length == 0) return uint.MaxValue;
                return neighborBack[GetIndex(new Vector3Int(pos.x, pos.y, VoxelData.ChunkWidth - 1))];
            }

            if (pos.z >= VoxelData.ChunkWidth)
            {
                // Check if the neighbor array is created and has elements, main loop will skip "uint.MaxValue" if not.
                if (!neighborFront.IsCreated || neighborFront.Length == 0) return uint.MaxValue;
                return neighborFront[GetIndex(new Vector3Int(pos.x, pos.y, 0))];
            }

            return uint.MaxValue; // Should be unreachable, but good for safety
        }

        #endregion
    }

    // Supporting structs for the job
    public struct LightRemovalNode
    {
        public Vector3Int Pos;
        public byte LightLevel;
    }

    public struct LightModification
    {
        public Vector3Int GlobalPosition;
        public byte LightLevel;
        public LightChannel Channel;
    }

    public enum LightChannel : byte
    {
        Sun,
        Block
    }
}