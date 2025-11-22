using Data;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs
{
    [BurstCompile]
    public struct NeighborhoodLightingJob : IJob
    {
        // --- INPUT Data ---

        #region Input Data

        // The map for the central chunk, which is the only one we can write to.
        public NativeArray<uint> Map;

        public Vector2Int ChunkPosition;

        // Queues of initial changes to process
        public NativeQueue<LightQueueNode> SunlightBfsQueue;
        public NativeQueue<LightQueueNode> BlocklightBfsQueue;
        public NativeQueue<Vector2Int> SunlightColumnRecalcQueue;

        // Read-only heightmap & maps for all 8 neighbors.
        [ReadOnly]
        public NativeArray<byte> Heightmap;

        [ReadOnly]
        public NativeArray<uint> NeighborN; // North (+Z)

        [ReadOnly]
        public NativeArray<uint> NeighborE; // East (+X)

        [ReadOnly]
        public NativeArray<uint> NeighborS; // South (-Z)

        [ReadOnly]
        public NativeArray<uint> NeighborW; // West (-X)

        [ReadOnly]
        public NativeArray<uint> NeighborNE; // North-East

        [ReadOnly]
        public NativeArray<uint> NeighborSE; // South-East

        [ReadOnly]
        public NativeArray<uint> NeighborSW; // South-West

        [ReadOnly]
        public NativeArray<uint> NeighborNW; // North-West

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        #endregion

        // --- OUTPUT Data  ---

        #region Output Data

        // A list of modifications for neighbor chunks. The job calculates these but can't apply them directly.
        public NativeList<LightModification> CrossChunkLightMods;

        // A flag to indicate if the lighting in the central chunk has stabilized.
        public NativeArray<bool> IsStable;

        #endregion

        public void Execute()
        {
            // Internal queues for the actual flood-fill algorithm. These are temporary for this job's execution.
            var sunlightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var sunlightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);
            var blocklightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var blocklightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);

            // --- PASS 0: SEEDING ---
            // Seed the queues with initial changes from the main thread.
            while (SunlightColumnRecalcQueue.TryDequeue(out Vector2Int column))
            {
                RecalculateSunlightForColumn(column.x, column.y, sunlightPlacementQueue, sunlightRemovalQueue);
            }

            while (SunlightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position);
                byte currentLight = BurstVoxelDataBitMapping.GetSunLight(currentPacked);
                if (currentLight < node.OldLightLevel)
                    sunlightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.Position, LightLevel = node.OldLightLevel });
                else if (currentLight > node.OldLightLevel)
                    sunlightPlacementQueue.Enqueue(node.Position);
            }

            while (BlocklightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position);
                byte currentLight = BurstVoxelDataBitMapping.GetBlockLight(currentPacked);
                if (currentLight > node.OldLightLevel)
                    blocklightPlacementQueue.Enqueue(node.Position);
                else if (currentLight < node.OldLightLevel)
                    blocklightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.Position, LightLevel = node.OldLightLevel });
            }

            // --- LIGHTING PASSES ---
            // The propagation logic now seamlessly crosses chunk borders within the 3x3 grid.
            while (sunlightRemovalQueue.TryDequeue(out LightRemovalNode node))
                PropagateDarkness(node, LightChannel.Sun, sunlightPlacementQueue, sunlightRemovalQueue);
            while (sunlightPlacementQueue.TryDequeue(out Vector3Int pos))
                PropagateLight(pos, LightChannel.Sun, sunlightPlacementQueue);

            while (blocklightRemovalQueue.TryDequeue(out LightRemovalNode node))
                PropagateDarkness(node, LightChannel.Block, blocklightPlacementQueue, blocklightRemovalQueue);
            while (blocklightPlacementQueue.TryDequeue(out Vector3Int pos))
                PropagateLight(pos, LightChannel.Block, blocklightPlacementQueue);

            // --- FINAL STEP ---
            // The lighting is stable if no more work was generated during this pass, AND no work was passed to neighbors.
            IsStable[0] = sunlightRemovalQueue.IsEmpty() && sunlightPlacementQueue.IsEmpty() &&
                          blocklightRemovalQueue.IsEmpty() && blocklightPlacementQueue.IsEmpty() &&
                          CrossChunkLightMods.Length == 0;

            // --- CLEANUP ---
            sunlightRemovalQueue.Dispose();
            sunlightPlacementQueue.Dispose();
            blocklightRemovalQueue.Dispose();
            blocklightPlacementQueue.Dispose();
        }

        #region Core Logic

        private void PropagateDarkness(LightRemovalNode node, LightChannel channel, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = channel == LightChannel.Sun ? BurstVoxelDataBitMapping.GetSunLight(neighborPacked) : BurstVoxelDataBitMapping.GetBlockLight(neighborPacked);

                if (neighborLight > 0)
                {
                    if (neighborLight < node.LightLevel)
                    {
                        SetLight(neighborPos, 0, channel);
                        rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborLight });
                    }
                    else
                    {
                        pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        private void PropagateLight(Vector3Int pos, LightChannel channel, NativeQueue<Vector3Int> pQueue)
        {
            uint sourcePacked = GetPackedData(pos);
            if (sourcePacked == uint.MaxValue) return;

            byte sourceLight = channel == LightChannel.Sun ? BurstVoxelDataBitMapping.GetSunLight(sourcePacked) : BurstVoxelDataBitMapping.GetBlockLight(sourcePacked);
            BlockTypeJobData sourceProps = BlockTypes[BurstVoxelDataBitMapping.GetId(sourcePacked)]; // <-- Line 165: Index out of range error here

            // An opaque block cannot propagate sunlight to its neighbors.
            // It might have sunlight level 15 from InitialSunlightJob, but it stops there.
            if (channel == LightChannel.Sun && sourceProps.IsOpaque) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = channel == LightChannel.Sun ? BurstVoxelDataBitMapping.GetSunLight(neighborPacked) : BurstVoxelDataBitMapping.GetBlockLight(neighborPacked);
                BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                // This special case allows sunlight to travel down columns of air without diminishing.
                bool isVerticalSunlight = channel == LightChannel.Sun && sourceLight == 15 && sourceProps.IsFullyTransparentToLight && VoxelData.FaceChecks[i].y == -1 && neighborProps.IsFullyTransparentToLight;

                byte lightToPropagate;

                // If the neighbor is opaque, it absorbs light but does not propagate it further.
                if (neighborProps.IsOpaque)
                {
                    // Light level on the surface of an opaque block is just one level lower than its source.
                    lightToPropagate = (byte)Mathf.Max(0, sourceLight - 1);
                    if (lightToPropagate > neighborLight)
                    {
                        SetLight(neighborPos, lightToPropagate, channel);
                        // IMPORTANT: Do not enqueue the opaque block for further propagation.
                    }
                }
                // If the neighbor is transparent, it both receives and propagates light.
                else
                {
                    // The light value is reduced by 1 for distance, plus the opacity of the block it's entering.
                    lightToPropagate = (byte)Mathf.Max(0, sourceLight - 1 - neighborProps.Opacity);

                    if (isVerticalSunlight)
                    {
                        lightToPropagate = 15;
                    }

                    if (lightToPropagate > neighborLight)
                    {
                        SetLight(neighborPos, lightToPropagate, channel);
                        // Enqueue the transparent block to continue spreading the light.
                        pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        private void RecalculateSunlightForColumn(int x, int z, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            // Use the heightmap to find the Y-level of the highest block that has any opacity.
            int heightmapIndex = x + VoxelData.ChunkWidth * z;
            byte highestBlockY = Heightmap[heightmapIndex];

            // --- PASS 1: Above the highest block ---
            // Everything above this point is transparent to the sky and should be fully sunlit.
            for (int y = VoxelData.ChunkHeight - 1; y > highestBlockY; y--)
            {
                var currentPos = new Vector3Int(x, y, z);
                uint currentPacked = GetPackedData(currentPos);
                byte oldSunlight = BurstVoxelDataBitMapping.GetSunLight(currentPacked);

                // Update the current block in the column to be fully lit.
                if (oldSunlight != 15)
                {
                    SetLight(currentPos, 15, LightChannel.Sun);
                    if (15 > oldSunlight)
                        pQueue.Enqueue(currentPos);
                    else
                        rQueue.Enqueue(new LightRemovalNode { Pos = currentPos, LightLevel = oldSunlight });
                }
            }

            // --- HORIZONTAL SHADOW CASTING CHECK (Still performed only once) ---
            // This remains a key optimization. We check for horizontal shadow casting at the highest point.
            Vector3Int shadowCasterPos = new Vector3Int(x, highestBlockY, z);
            uint shadowCasterPacked = GetPackedData(shadowCasterPos);
            if (BlockTypes[BurstVoxelDataBitMapping.GetId(shadowCasterPacked)].IsOpaque)
            {
                // Check horizontal neighbors (N, E, S, W).
                for (int i = 0; i < 6; i++)
                {
                    if (VoxelData.FaceChecks[i].y != 0) continue; // Skip vertical neighbors
                    Vector3Int neighborPos = shadowCasterPos + VoxelData.FaceChecks[i];
                    uint neighborPacked = GetPackedData(neighborPos);
                    if (neighborPacked == uint.MaxValue) continue;

                    byte neighborSunlight = BurstVoxelDataBitMapping.GetSunLight(neighborPacked);

                    // If the neighbor has sunlight BUT NOT FULL SUNLIGHT, it needs to be re-evaluated.
                    // A neighbor with level 15 has its own direct sky access and should be ignored.
                    if (neighborSunlight > 0 && neighborSunlight < 15)
                    {
                        rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborSunlight });
                    }
                }
            }

            // --- PASS 2: At and below the highest block (with correct attenuation) ---
            // Propagate light downwards, now correctly reducing light based on each block's opacity.
            byte lightFromSky = 15;
            for (int y = highestBlockY; y >= 0; y--)
            {
                var currentPos = new Vector3Int(x, y, z);
                uint currentPacked = GetPackedData(currentPos);
                byte oldSunlight = BurstVoxelDataBitMapping.GetSunLight(currentPacked);
                BlockTypeJobData props = BlockTypes[BurstVoxelDataBitMapping.GetId(currentPacked)];

                // Update the current block in the column based on the light from above.
                if (oldSunlight != lightFromSky)
                {
                    SetLight(currentPos, lightFromSky, LightChannel.Sun);
                    if (lightFromSky > oldSunlight)
                        pQueue.Enqueue(currentPos);
                    else
                        rQueue.Enqueue(new LightRemovalNode { Pos = currentPos, LightLevel = oldSunlight });
                }

                // If light is already 0, it can't get any lower.
                if (lightFromSky == 0) continue;

                // Attenuate light for the next block down in the column.
                lightFromSky = (byte)Mathf.Max(0, lightFromSky - props.Opacity);
            }
        }

        #endregion

        #region Helper Methods

        /// Get the packed data for the coordinates in the 3x3 grid.
        /// Examples:
        /// - A position like (-2, y, 5) is in the West neighbor.
        /// - A position like (20, y, 20) is in the North-East neighbor.
        private uint GetPackedData(Vector3Int pos)
        {
            if (pos.y < 0 || pos.y >= VoxelData.ChunkHeight) return uint.MaxValue;

            NativeArray<uint> targetMap;
            Vector3Int localPos = pos;

            if (pos.x < 0) // West side
            {
                localPos.x += VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborSW;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborNW;
                }
                else
                {
                    targetMap = NeighborW;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth) // East side
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborSE;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborNE;
                }
                else
                {
                    targetMap = NeighborE;
                }
            }
            else // Center column
            {
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetMap = NeighborS;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetMap = NeighborN;
                }
                else
                {
                    targetMap = Map;
                }
            }

            if (!targetMap.IsCreated || targetMap.Length == 0) return uint.MaxValue;
            return targetMap[localPos.x + VoxelData.ChunkWidth * (localPos.y + VoxelData.ChunkHeight * localPos.z)];
        }

        /// SetLight writes to the central map directly, but adds modifications for neighbors to the `crossChunkLightMods` list.
        private void SetLight(Vector3Int pos, byte lightLevel, LightChannel channel)
        {
            if (pos.x is >= 0 and < VoxelData.ChunkWidth && pos.z is >= 0 and < VoxelData.ChunkWidth)
            {
                // Voxel is in the central chunk, we can write to its map directly.
                int index = pos.x + VoxelData.ChunkWidth * (pos.y + VoxelData.ChunkHeight * pos.z);
                uint packedData = Map[index];
                uint newPackedData = channel == LightChannel.Sun
                    ? BurstVoxelDataBitMapping.SetSunLight(packedData, lightLevel)
                    : BurstVoxelDataBitMapping.SetBlockLight(packedData, lightLevel);
                Map[index] = newPackedData;
            }
            else
            {
                // Voxel is in a neighbor chunk, add a modification request to the output list.
                Vector3Int globalPos = new Vector3Int(pos.x + ChunkPosition.x, pos.y, pos.z + ChunkPosition.y);
                CrossChunkLightMods.Add(new LightModification { GlobalPosition = globalPos, LightLevel = lightLevel, Channel = channel });
            }
        }

        #endregion
    }

    // --- Supporting structs for the job ---
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
        Block,
    }
}