using Data;
using Helpers;
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
        public NativeArray<ushort> Heightmap;

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

        /// <summary>
        /// Executes the flood-fill lighting propagation algorithm within the central chunk, crossing boundaries to its 8 neighbors if necessary.
        /// </summary>
        public void Execute()
        {
            // Internal queues for the actual flood-fill algorithm. These are temporary for this job's execution.
            var sunlightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var sunlightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);
            var blocklightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            var blocklightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);

            // Write-through cache for cross-chunk modifications.
            // SetLight can't modify [ReadOnly] neighbor arrays, so subsequent GetPackedData calls
            // would return stale (pre-modification) values. This cache ensures that darkness removal
            // results are visible to the re-spreading phase within the same job execution.
            var neighborWriteCache = new NativeHashMap<long, uint>(64, Allocator.Temp);

            // --- PASS 0: SEEDING ---
            // Seed the queues with initial changes from the main thread.
            while (SunlightColumnRecalcQueue.TryDequeue(out Vector2Int column))
            {
                RecalculateSunlightForColumn(column.x, column.y, sunlightPlacementQueue, sunlightRemovalQueue, ref neighborWriteCache);
            }

            while (SunlightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position, ref neighborWriteCache);
                if (currentPacked == uint.MaxValue) continue;
                byte currentLight = BurstVoxelDataBitMapping.GetSunLight(currentPacked);
                if (currentLight < node.OldLightLevel)
                    sunlightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.Position, LightLevel = node.OldLightLevel });
                else if (currentLight > node.OldLightLevel)
                    sunlightPlacementQueue.Enqueue(node.Position);
            }

            while (BlocklightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position, ref neighborWriteCache);
                if (currentPacked == uint.MaxValue) continue;
                byte currentLight = BurstVoxelDataBitMapping.GetBlockLight(currentPacked);
                if (currentLight > node.OldLightLevel)
                    blocklightPlacementQueue.Enqueue(node.Position);
                else if (currentLight < node.OldLightLevel)
                    blocklightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.Position, LightLevel = node.OldLightLevel });
            }

            // --- LIGHTING PASSES ---
            // The propagation logic now seamlessly crosses chunk borders within the 3x3 grid.
            while (sunlightRemovalQueue.TryDequeue(out LightRemovalNode node))
                PropagateDarkness(node, LightChannel.Sun, sunlightPlacementQueue, sunlightRemovalQueue, ref neighborWriteCache);
            while (sunlightPlacementQueue.TryDequeue(out Vector3Int pos))
                PropagateLight(pos, LightChannel.Sun, sunlightPlacementQueue, ref neighborWriteCache);

            while (blocklightRemovalQueue.TryDequeue(out LightRemovalNode node))
                PropagateDarkness(node, LightChannel.Block, blocklightPlacementQueue, blocklightRemovalQueue, ref neighborWriteCache);
            while (blocklightPlacementQueue.TryDequeue(out Vector3Int pos))
                PropagateLight(pos, LightChannel.Block, blocklightPlacementQueue, ref neighborWriteCache);

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
            neighborWriteCache.Dispose();
        }

        /// <summary>
        /// Encodes a local position within the 3x3 grid into a unique long key.
        /// X/Z range: [-16, 31], Y range: [0, 255]. Offset X/Z by 16 to make them non-negative.
        /// </summary>
        private static long EncodeNeighborKey(int x, int y, int z)
        {
            // X+16: [0, 47], Z+16: [0, 47], Y: [0, 255]
            return (long)(x + 16) + (long)(z + 16) * 48L + (long)y * 48L * 48L;
        }

        #region Core Logic

        private void PropagateDarkness(LightRemovalNode node, LightChannel channel, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue, ref NativeHashMap<long, uint> cache)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = channel == LightChannel.Sun ? BurstVoxelDataBitMapping.GetSunLight(neighborPacked) : BurstVoxelDataBitMapping.GetBlockLight(neighborPacked);

                if (neighborLight > 0)
                {
                    if (neighborLight < node.LightLevel)
                    {
                        SetLight(neighborPos, 0, channel, ref cache);
                        rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborLight });
                    }
                    else
                    {
                        pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        private void PropagateLight(Vector3Int pos, LightChannel channel, NativeQueue<Vector3Int> pQueue, ref NativeHashMap<long, uint> cache)
        {
            uint sourcePacked = GetPackedData(pos, ref cache);
            if (sourcePacked == uint.MaxValue) return;

            byte sourceLight = channel == LightChannel.Sun ? BurstVoxelDataBitMapping.GetSunLight(sourcePacked) : BurstVoxelDataBitMapping.GetBlockLight(sourcePacked);
            BlockTypeJobData sourceProps = BlockTypes[BurstVoxelDataBitMapping.GetId(sourcePacked)];

            // An opaque block cannot propagate sunlight to its neighbors.
            // It might have sunlight level 15 from InitialSunlightJob, but it stops there.
            if (channel == LightChannel.Sun && sourceProps.IsOpaque) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
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
                        SetLight(neighborPos, lightToPropagate, channel, ref cache);
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
                        SetLight(neighborPos, lightToPropagate, channel, ref cache);
                        // Enqueue the transparent block to continue spreading the light.
                        pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        private void RecalculateSunlightForColumn(int x, int z, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue, ref NativeHashMap<long, uint> cache)
        {
            // Use the heightmap to find the Y-level of the highest block that has any opacity.
            int heightmapIndex = x + VoxelData.ChunkWidth * z;
            ushort highestBlockY = Heightmap[heightmapIndex];

            // --- PASS 1: Above the highest block ---
            // Everything above this point is transparent to the sky and should be fully sunlit.
            for (int y = VoxelData.ChunkHeight - 1; y > highestBlockY; y--)
            {
                var currentPos = new Vector3Int(x, y, z);
                uint currentPacked = GetPackedData(currentPos, ref cache);
                byte oldSunlight = BurstVoxelDataBitMapping.GetSunLight(currentPacked);

                // Update the current block in the column to be fully lit.
                if (oldSunlight != 15)
                {
                    SetLight(currentPos, 15, LightChannel.Sun, ref cache);
                    if (15 > oldSunlight)
                        pQueue.Enqueue(currentPos);
                    else
                        rQueue.Enqueue(new LightRemovalNode { Pos = currentPos, LightLevel = oldSunlight });
                }
            }

            // --- HORIZONTAL SHADOW CASTING CHECK (Still performed only once) ---
            // This remains a key optimization. We check for horizontal shadow casting at the highest point.
            Vector3Int shadowCasterPos = new Vector3Int(x, highestBlockY, z);
            uint shadowCasterPacked = GetPackedData(shadowCasterPos, ref cache);
            if (BlockTypes[BurstVoxelDataBitMapping.GetId(shadowCasterPacked)].IsOpaque)
            {
                // Check horizontal neighbors (N, E, S, W).
                for (int i = 0; i < 6; i++)
                {
                    if (VoxelData.FaceChecks[i].y != 0) continue; // Skip vertical neighbors
                    Vector3Int neighborPos = shadowCasterPos + VoxelData.FaceChecks[i];
                    uint neighborPacked = GetPackedData(neighborPos, ref cache);
                    if (neighborPacked == uint.MaxValue) continue;

                    byte neighborSunlight = BurstVoxelDataBitMapping.GetSunLight(neighborPacked);

                    // If the neighbor has sunlight BUT NOT FULL SUNLIGHT, it needs to be re-evaluated.
                    // A neighbor with level 15 has its own direct sky access and should be ignored.
                    if (neighborSunlight > 0 && neighborSunlight < 15)
                    {
                        // We MUST manually set this block's light to 0 before adding it to the removal queue.
                        // Otherwise, it acts as a permanent ghost light source during the darkness propagation pass!
                        SetLight(neighborPos, 0, LightChannel.Sun, ref cache);

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
                uint currentPacked = GetPackedData(currentPos, ref cache);
                byte oldSunlight = BurstVoxelDataBitMapping.GetSunLight(currentPacked);
                BlockTypeJobData props = BlockTypes[BurstVoxelDataBitMapping.GetId(currentPacked)];

                // Update the current block in the column based on the light from above.
                if (oldSunlight != lightFromSky)
                {
                    SetLight(currentPos, lightFromSky, LightChannel.Sun, ref cache);
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
        private uint GetPackedData(Vector3Int pos, ref NativeHashMap<long, uint> cache)
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

            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            uint data = targetMap[mapIndex];

            // Check write-through cache: if this neighbor position was modified by SetLight
            // during this job, return the cached (updated) value instead of the stale ReadOnly value.
            long cacheKey = EncodeNeighborKey(pos.x, pos.y, pos.z);
            if (cache.TryGetValue(cacheKey, out uint cachedData))
                return cachedData;

            return data;
        }

        /// SetLight writes to the central map directly, but adds modifications for neighbors to the `crossChunkLightMods` list.
        private void SetLight(Vector3Int localPos, byte lightLevel, LightChannel channel, ref NativeHashMap<long, uint> cache)
        {
            if (localPos.x is >= 0 and < VoxelData.ChunkWidth && localPos.z is >= 0 and < VoxelData.ChunkWidth)
            {
                // Voxel is in the central chunk, we can write to its map directly.
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);

                uint packedData = Map[mapIndex];
                uint newPackedData = channel == LightChannel.Sun
                    ? BurstVoxelDataBitMapping.SetSunLight(packedData, lightLevel)
                    : BurstVoxelDataBitMapping.SetBlockLight(packedData, lightLevel);
                Map[mapIndex] = newPackedData;
            }
            else
            {
                // Voxel is in a neighbor chunk, add a modification request to the output list.
                Vector3Int globalPos = new Vector3Int(localPos.x + ChunkPosition.x, localPos.y, localPos.z + ChunkPosition.y);
                CrossChunkLightMods.Add(new LightModification { GlobalPosition = globalPos, LightLevel = lightLevel, Channel = channel });

                // Update the write-through cache so subsequent GetPackedData calls within this
                // job see the modified value instead of the stale ReadOnly neighbor data.
                // This is critical for darkness removal: PropagateDarkness sets neighbor light to 0,
                // and PropagateLight must see that 0 to avoid re-spreading ghost light.
                long cacheKey = EncodeNeighborKey(localPos.x, localPos.y, localPos.z);
                uint currentPacked;
                if (cache.TryGetValue(cacheKey, out uint existing))
                    currentPacked = existing;
                else
                    currentPacked = GetPackedData(localPos, ref cache);
                uint updatedPacked = channel == LightChannel.Sun
                    ? BurstVoxelDataBitMapping.SetSunLight(currentPacked, lightLevel)
                    : BurstVoxelDataBitMapping.SetBlockLight(currentPacked, lightLevel);
                cache[cacheKey] = updatedPacked;
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
