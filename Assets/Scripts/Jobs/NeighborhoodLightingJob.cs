using System.Runtime.CompilerServices;
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

        // Parallel ushort light array for RGB light data (Phase 2).
        public NativeArray<ushort> LightMap;

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

        // Read-only light arrays for all 8 neighbors.
        [ReadOnly]
        public NativeArray<ushort> LightN;

        [ReadOnly]
        public NativeArray<ushort> LightE;

        [ReadOnly]
        public NativeArray<ushort> LightS;

        [ReadOnly]
        public NativeArray<ushort> LightW;

        [ReadOnly]
        public NativeArray<ushort> LightNE;

        [ReadOnly]
        public NativeArray<ushort> LightSE;

        [ReadOnly]
        public NativeArray<ushort> LightSW;

        [ReadOnly]
        public NativeArray<ushort> LightNW;

        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        // When true, the job performs an edge consistency check on the 4 horizontal chunk borders
        // before running the BFS. This detects and corrects stale light values at chunk boundaries.
        public bool PerformEdgeCheck;

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
            NativeQueue<LightRemovalNode> sunlightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            NativeQueue<Vector3Int> sunlightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);
            NativeQueue<LightRemovalNode> blocklightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            NativeQueue<Vector3Int> blocklightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);

            // Write-through cache for cross-chunk modifications.
            // SetLight can't modify [ReadOnly] neighbor arrays, so subsequent GetPackedData calls
            // would return stale (pre-modification) values. This cache ensures that darkness removal
            // results are visible to the re-spreading phase within the same job execution.
            // Packed as ulong: upper 32 bits = voxel uint, lower 16 bits = light ushort.
            NativeHashMap<long, ulong> neighborWriteCache = new NativeHashMap<long, ulong>(64, Allocator.Temp);

            // --- PASS -2: SYNC EMISSION TO LIGHT ARRAY ---
            // The uint packed data has emission baked in by generation/placement, but the ushort
            // light array may be uninitialized. Scan center chunk blocks and write emission RGB
            // so the BFS and edge checks can read from the ushort array consistently.
            SyncEmissionToLightArray();

            // --- PASS -1: EDGE CONSISTENCY CHECK (Starlight-inspired) ---
            // Validates light values on all 4 horizontal chunk borders against neighbor data.
            // If a border voxel's light is inconsistent with what its neighbor could supply,
            // it is queued for correction via the standard BFS passes.
            if (PerformEdgeCheck)
            {
                CheckEdges(sunlightPlacementQueue, blocklightPlacementQueue, ref neighborWriteCache);
            }

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

                // Sync the ushort light array with the block's actual emission state.
                // - Placement: the uint has emission baked in, but the ushort is uninitialized → write emission.
                // - Removal: the block is now air, but the ushort retains stale emission → clear to 0.
                // Wake-up neighbors (OldBlock = 0) are NOT cleared — they keep their propagated light
                // so the comparison correctly detects anyIncreased and enqueues them for re-spreading.
                ushort id = BurstVoxelDataBitMapping.GetId(currentPacked);
                BlockTypeJobData props = BlockTypes[id];
                if (props.EmissionR > 0 || props.EmissionG > 0 || props.EmissionB > 0)
                {
                    SetBlocklightRGB(node.Position, props.EmissionR, props.EmissionG, props.EmissionB, ref neighborWriteCache);
                }
                else if (node.OldBlockR > 0 || node.OldBlockG > 0 || node.OldBlockB > 0)
                {
                    SetBlocklightRGB(node.Position, 0, 0, 0, ref neighborWriteCache);
                }

                ushort currentLight = GetLightData(node.Position, ref neighborWriteCache);
                if (currentLight == ushort.MaxValue) continue;
                byte curR = LightBitMapping.GetBlocklightR(currentLight);
                byte curG = LightBitMapping.GetBlocklightG(currentLight);
                byte curB = LightBitMapping.GetBlocklightB(currentLight);
                bool anyIncreased = curR > node.OldBlockR || curG > node.OldBlockG || curB > node.OldBlockB;
                bool anyDecreased = curR < node.OldBlockR || curG < node.OldBlockG || curB < node.OldBlockB;
                if (anyIncreased)
                    blocklightPlacementQueue.Enqueue(node.Position);
                if (anyDecreased)
                    blocklightRemovalQueue.Enqueue(new LightRemovalNode
                    {
                        Pos = node.Position,
                        LightR = node.OldBlockR, LightG = node.OldBlockG, LightB = node.OldBlockB,
                    });
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
            return x + 16 + (z + 16) * 48L + y * 48L * 48L;
        }

        #region Core Logic

        /// <summary>
        /// Synchronizes the ushort light array with data from the uint packed map.
        /// Ensures sunlight and blocklight emission values (baked into the uint by generation/placement)
        /// are reflected in the ushort light array so BFS and edge checks read consistent data.
        /// </summary>
        private void SyncEmissionToLightArray()
        {
            for (int i = 0; i < Map.Length; i++)
            {
                uint packed = Map[i];
                ushort id = BurstVoxelDataBitMapping.GetId(packed);
                if (id == 0) continue;

                byte sun = BurstVoxelDataBitMapping.GetSunLight(packed);
                ushort currentLight = LightMap[i];
                byte currentSun = LightBitMapping.GetSunLight(currentLight);

                BlockTypeJobData props = BlockTypes[id];
                byte emR = props.EmissionR;
                byte emG = props.EmissionG;
                byte emB = props.EmissionB;

                // Only update if the ushort is stale relative to what the uint/emission says
                bool needsUpdate = sun != currentSun;
                if (emR > 0 || emG > 0 || emB > 0)
                {
                    byte curR = LightBitMapping.GetBlocklightR(currentLight);
                    byte curG = LightBitMapping.GetBlocklightG(currentLight);
                    byte curB = LightBitMapping.GetBlocklightB(currentLight);
                    if (curR < emR || curG < emG || curB < emB)
                        needsUpdate = true;
                    if (needsUpdate)
                    {
                        LightMap[i] = LightBitMapping.PackLightData(sun,
                            (byte)Mathf.Max(emR, curR),
                            (byte)Mathf.Max(emG, curG),
                            (byte)Mathf.Max(emB, curB));
                    }
                }
                else if (needsUpdate)
                {
                    LightMap[i] = LightBitMapping.SetSunLight(currentLight, sun);
                }
            }
        }

        /// <summary>
        /// Returns true if the position is within the central chunk's local coordinate space (0-15 for X and Z).
        /// BFS propagation must NOT continue into neighbor chunks — it creates light wrap-around artifacts
        /// where light exits the center chunk, travels through the neighbor's (possibly empty) data,
        /// and re-enters the center chunk underground. Neighbor lighting is handled by CrossChunkLightMods.
        /// </summary>
        private static bool IsInCenterChunk(Vector3Int pos)
        {
            return pos.x >= 0 && pos.x < VoxelData.ChunkWidth &&
                   pos.z >= 0 && pos.z < VoxelData.ChunkWidth;
        }

        private void PropagateDarkness(LightRemovalNode node, LightChannel channel, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue, ref NativeHashMap<long, ulong> cache)
        {
            if (channel == LightChannel.Block)
            {
                PropagateDarknessRGB(node, pQueue, rQueue, ref cache);
                return;
            }

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = BurstVoxelDataBitMapping.GetSunLight(neighborPacked);

                if (neighborLight > 0)
                {
                    if (neighborLight < node.LightLevel)
                    {
                        SetLight(neighborPos, 0, LightChannel.Sun, ref cache);
                        if (IsInCenterChunk(neighborPos))
                            rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborLight });
                    }
                    else
                    {
                        if (IsInCenterChunk(neighborPos))
                            pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        /// <summary>
        /// Per-channel RGB darkness removal for blocklight.
        /// Each channel is compared independently against the removal node's old values.
        /// </summary>
        private void PropagateDarknessRGB(LightRemovalNode node, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue, ref NativeHashMap<long, ulong> cache)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                ushort neighborLight = GetLightData(neighborPos, ref cache);
                if (neighborLight == ushort.MaxValue) continue;

                byte nR = LightBitMapping.GetBlocklightR(neighborLight);
                byte nG = LightBitMapping.GetBlocklightG(neighborLight);
                byte nB = LightBitMapping.GetBlocklightB(neighborLight);

                if (nR == 0 && nG == 0 && nB == 0) continue;

                byte newR = nR, newG = nG, newB = nB;
                bool anyRemoved = false;
                bool anyRespread = false;

                // Per-channel: if neighbor's value is less than the old value, it was dependent on the removed source
                if (nR > 0 && nR < node.LightR)
                {
                    newR = 0;
                    anyRemoved = true;
                }
                else if (nR >= node.LightR && node.LightR > 0)
                {
                    anyRespread = true;
                }

                if (nG > 0 && nG < node.LightG)
                {
                    newG = 0;
                    anyRemoved = true;
                }
                else if (nG >= node.LightG && node.LightG > 0)
                {
                    anyRespread = true;
                }

                if (nB > 0 && nB < node.LightB)
                {
                    newB = 0;
                    anyRemoved = true;
                }
                else if (nB >= node.LightB && node.LightB > 0)
                {
                    anyRespread = true;
                }

                if (anyRemoved)
                {
                    SetBlocklightRGB(neighborPos, newR, newG, newB, ref cache);
                    if (IsInCenterChunk(neighborPos))
                        rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightR = nR, LightG = nG, LightB = nB });
                }

                if (anyRespread && IsInCenterChunk(neighborPos))
                {
                    pQueue.Enqueue(neighborPos);
                }
            }
        }

        /// <summary>
        /// Calculates the attenuated light level after passing through a block.
        /// Uses the Starlight/Moonrise formula: attenuation = max(1, opacity).
        /// Air (opacity 0) costs 1 level; semi-transparent blocks cost their opacity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte AttenuateLight(int sourceLight, byte opacity)
        {
            return (byte)Mathf.Max(0, sourceLight - Mathf.Max(1, opacity));
        }

        private void PropagateLight(Vector3Int pos, LightChannel channel, NativeQueue<Vector3Int> pQueue, ref NativeHashMap<long, ulong> cache)
        {
            if (channel == LightChannel.Block)
            {
                PropagateLightRGB(pos, pQueue, ref cache);
                return;
            }

            uint sourcePacked = GetPackedData(pos, ref cache);
            if (sourcePacked == uint.MaxValue) return;

            byte sourceLight = BurstVoxelDataBitMapping.GetSunLight(sourcePacked);
            BlockTypeJobData sourceProps = BlockTypes[BurstVoxelDataBitMapping.GetId(sourcePacked)];

            // An opaque block cannot propagate sunlight to its neighbors.
            if (sourceProps.IsOpaque) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = BurstVoxelDataBitMapping.GetSunLight(neighborPacked);
                BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                bool isVerticalSunlight = sourceLight == 15 && sourceProps.IsFullyTransparentToLight && VoxelData.FaceChecks[i].y == -1 && neighborProps.IsFullyTransparentToLight;

                byte lightToPropagate;

                if (neighborProps.IsOpaque)
                {
                    lightToPropagate = (byte)Mathf.Max(0, sourceLight - 1);
                    if (lightToPropagate > neighborLight)
                    {
                        SetLight(neighborPos, lightToPropagate, LightChannel.Sun, ref cache);
                    }
                }
                else
                {
                    lightToPropagate = AttenuateLight(sourceLight, neighborProps.Opacity);

                    if (isVerticalSunlight)
                    {
                        lightToPropagate = 15;
                    }

                    if (lightToPropagate > neighborLight)
                    {
                        SetLight(neighborPos, lightToPropagate, LightChannel.Sun, ref cache);
                        if (IsInCenterChunk(neighborPos))
                            pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        /// <summary>
        /// Per-channel RGB blocklight propagation. Each channel attenuates independently.
        /// A neighbor is enqueued if any channel increased.
        /// </summary>
        private void PropagateLightRGB(Vector3Int pos, NativeQueue<Vector3Int> pQueue, ref NativeHashMap<long, ulong> cache)
        {
            uint sourcePacked = GetPackedData(pos, ref cache);
            if (sourcePacked == uint.MaxValue) return;

            ushort sourceLight = GetLightData(pos, ref cache);
            if (sourceLight == ushort.MaxValue) return;

            byte srcR = LightBitMapping.GetBlocklightR(sourceLight);
            byte srcG = LightBitMapping.GetBlocklightG(sourceLight);
            byte srcB = LightBitMapping.GetBlocklightB(sourceLight);

            if (srcR == 0 && srcG == 0 && srcB == 0) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;

                ushort neighborLight = GetLightData(neighborPos, ref cache);
                if (neighborLight == ushort.MaxValue) continue;

                byte nR = LightBitMapping.GetBlocklightR(neighborLight);
                byte nG = LightBitMapping.GetBlocklightG(neighborLight);
                byte nB = LightBitMapping.GetBlocklightB(neighborLight);

                BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                if (neighborProps.IsOpaque)
                {
                    // Opaque blocks receive surface light (source - 1) but do not propagate further
                    byte propR = (byte)Mathf.Max(0, srcR - 1);
                    byte propG = (byte)Mathf.Max(0, srcG - 1);
                    byte propB = (byte)Mathf.Max(0, srcB - 1);

                    byte finalR = (byte)Mathf.Max(nR, propR);
                    byte finalG = (byte)Mathf.Max(nG, propG);
                    byte finalB = (byte)Mathf.Max(nB, propB);

                    if (finalR != nR || finalG != nG || finalB != nB)
                    {
                        SetBlocklightRGB(neighborPos, finalR, finalG, finalB, ref cache);
                    }
                }
                else
                {
                    byte propR = AttenuateLight(srcR, neighborProps.Opacity);
                    byte propG = AttenuateLight(srcG, neighborProps.Opacity);
                    byte propB = AttenuateLight(srcB, neighborProps.Opacity);

                    byte finalR = (byte)Mathf.Max(nR, propR);
                    byte finalG = (byte)Mathf.Max(nG, propG);
                    byte finalB = (byte)Mathf.Max(nB, propB);

                    if (finalR != nR || finalG != nG || finalB != nB)
                    {
                        SetBlocklightRGB(neighborPos, finalR, finalG, finalB, ref cache);
                        if (IsInCenterChunk(neighborPos))
                            pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        private void RecalculateSunlightForColumn(int x, int z, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue, ref NativeHashMap<long, ulong> cache)
        {
            // Use the heightmap to find the Y-level of the highest block that has any opacity.
            int heightmapIndex = x + VoxelData.ChunkWidth * z;
            ushort highestBlockY = Heightmap[heightmapIndex];

            // --- PASS 1: Above the highest block ---
            // Everything above this point is transparent to the sky and should be fully sunlit.
            for (int y = VoxelData.ChunkHeight - 1; y > highestBlockY; y--)
            {
                Vector3Int currentPos = new Vector3Int(x, y, z);
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
                Vector3Int currentPos = new Vector3Int(x, y, z);
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
                lightFromSky = AttenuateLight(lightFromSky, props.Opacity);
            }
        }

        #endregion

        #region Edge Checking

        /// <summary>
        /// Starlight-inspired edge consistency check. Iterates all voxels on the 4 horizontal
        /// chunk borders and validates their light levels against what neighbors could supply.
        /// Inconsistencies are queued for correction via the standard BFS passes.
        /// </summary>
        private void CheckEdges(
            NativeQueue<Vector3Int> sunPlacement, NativeQueue<Vector3Int> blockPlacement,
            ref NativeHashMap<long, ulong> cache)
        {
            // Check all 4 horizontal borders:
            // South border (z=0, neighbor at z=-1), North border (z=15, neighbor at z=+1)
            // West border (x=0, neighbor at x=-1), East border (x=15, neighbor at x=+1)
            for (int border = 0; border < 4; border++)
            {
                for (int y = 0; y < VoxelData.ChunkHeight; y++)
                {
                    for (int along = 0; along < VoxelData.ChunkWidth; along++)
                    {
                        Vector3Int pos;
                        Vector3Int neighborPos;

                        switch (border)
                        {
                            case 0: // South (z=0)
                                pos = new Vector3Int(along, y, 0);
                                neighborPos = new Vector3Int(along, y, -1);
                                break;
                            case 1: // North (z=15)
                                pos = new Vector3Int(along, y, VoxelData.ChunkWidth - 1);
                                neighborPos = new Vector3Int(along, y, VoxelData.ChunkWidth);
                                break;
                            case 2: // West (x=0)
                                pos = new Vector3Int(0, y, along);
                                neighborPos = new Vector3Int(-1, y, along);
                                break;
                            default: // East (x=15)
                                pos = new Vector3Int(VoxelData.ChunkWidth - 1, y, along);
                                neighborPos = new Vector3Int(VoxelData.ChunkWidth, y, along);
                                break;
                        }

                        uint centerPacked = GetPackedData(pos, ref cache);
                        if (centerPacked == uint.MaxValue) continue;

                        uint neighborPacked = GetPackedData(neighborPos, ref cache);
                        if (neighborPacked == uint.MaxValue) continue;

                        CheckEdgeVoxel(pos, centerPacked, neighborPacked, LightChannel.Sun,
                            sunPlacement, ref cache);
                        CheckEdgeVoxelRGB(pos, centerPacked, neighborPos,
                            blockPlacement, ref cache);
                    }
                }
            }
        }

        /// <summary>
        /// Checks a single border voxel's sunlight against its cross-chunk neighbor.
        /// Detects missing light (black spots) where the neighbor has light that should propagate here.
        /// </summary>
        private void CheckEdgeVoxel(
            Vector3Int centerPos, uint centerPacked, uint neighborPacked, LightChannel channel,
            NativeQueue<Vector3Int> placementQueue, ref NativeHashMap<long, ulong> cache)
        {
            byte centerLight = BurstVoxelDataBitMapping.GetSunLight(centerPacked);
            byte neighborLight = BurstVoxelDataBitMapping.GetSunLight(neighborPacked);

            BlockTypeJobData centerProps = BlockTypes[BurstVoxelDataBitMapping.GetId(centerPacked)];
            if (centerProps.IsOpaque) return;

            byte expectedFromNeighbor = AttenuateLight(neighborLight, centerProps.Opacity);

            if (expectedFromNeighbor > centerLight)
            {
                SetLight(centerPos, expectedFromNeighbor, channel, ref cache);
                placementQueue.Enqueue(centerPos);
            }
        }

        /// <summary>
        /// Checks a single border voxel's blocklight RGB against its cross-chunk neighbor.
        /// Per-channel comparison detects missing light on any channel.
        /// </summary>
        private void CheckEdgeVoxelRGB(
            Vector3Int centerPos, uint centerPacked, Vector3Int neighborPos,
            NativeQueue<Vector3Int> placementQueue, ref NativeHashMap<long, ulong> cache)
        {
            BlockTypeJobData centerProps = BlockTypes[BurstVoxelDataBitMapping.GetId(centerPacked)];
            if (centerProps.IsOpaque) return;

            ushort centerLight = GetLightData(centerPos, ref cache);
            ushort neighborLight = GetLightData(neighborPos, ref cache);
            if (centerLight == ushort.MaxValue || neighborLight == ushort.MaxValue) return;

            byte cR = LightBitMapping.GetBlocklightR(centerLight);
            byte cG = LightBitMapping.GetBlocklightG(centerLight);
            byte cB = LightBitMapping.GetBlocklightB(centerLight);

            byte nR = LightBitMapping.GetBlocklightR(neighborLight);
            byte nG = LightBitMapping.GetBlocklightG(neighborLight);
            byte nB = LightBitMapping.GetBlocklightB(neighborLight);

            byte expR = AttenuateLight(nR, centerProps.Opacity);
            byte expG = AttenuateLight(nG, centerProps.Opacity);
            byte expB = AttenuateLight(nB, centerProps.Opacity);

            byte finalR = (byte)Mathf.Max(cR, expR);
            byte finalG = (byte)Mathf.Max(cG, expG);
            byte finalB = (byte)Mathf.Max(cB, expB);

            if (finalR != cR || finalG != cG || finalB != cB)
            {
                SetBlocklightRGB(centerPos, finalR, finalG, finalB, ref cache);
                placementQueue.Enqueue(centerPos);
            }
        }

        #endregion

        #region Helper Methods

        /// Get the packed data for the coordinates in the 3x3 grid.
        /// Examples:
        /// - A position like (-2, y, 5) is in the West neighbor.
        /// - A position like (20, y, 20) is in the North-East neighbor.
        private uint GetPackedData(Vector3Int pos, ref NativeHashMap<long, ulong> cache)
        {
            if (pos.y is < 0 or >= VoxelData.ChunkHeight) return uint.MaxValue;

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

            // Defensive validation: ensure remapped coordinates are within chunk bounds.
            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
                return uint.MaxValue;

            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            uint data = targetMap[mapIndex];

            // Check write-through cache: if this neighbor position was modified by SetLight
            // during this job, return the cached (updated) value instead of the stale ReadOnly value.
            // Cache packs (uint voxelData << 16) | ushort lightData into a ulong.
            long cacheKey = EncodeNeighborKey(pos.x, pos.y, pos.z);
            if (cache.TryGetValue(cacheKey, out ulong cachedPair))
                return (uint)(cachedPair >> 16);

            return data;
        }

        /// <summary>
        /// Gets the ushort light data for a position in the 3x3 grid.
        /// Returns ushort.MaxValue for out-of-bounds positions.
        /// </summary>
        private ushort GetLightData(Vector3Int pos, ref NativeHashMap<long, ulong> cache)
        {
            if (pos.y is < 0 or >= VoxelData.ChunkHeight) return ushort.MaxValue;

            // Check cache first for neighbor positions
            if (pos.x < 0 || pos.x >= VoxelData.ChunkWidth || pos.z < 0 || pos.z >= VoxelData.ChunkWidth)
            {
                long cacheKey = EncodeNeighborKey(pos.x, pos.y, pos.z);
                if (cache.TryGetValue(cacheKey, out ulong cachedPair))
                    return (ushort)(cachedPair & 0xFFFF);
            }

            NativeArray<ushort> targetLight;
            Vector3Int localPos = pos;

            if (pos.x < 0)
            {
                localPos.x += VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightSW;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightNW;
                }
                else
                {
                    targetLight = LightW;
                }
            }
            else if (pos.x >= VoxelData.ChunkWidth)
            {
                localPos.x -= VoxelData.ChunkWidth;
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightSE;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightNE;
                }
                else
                {
                    targetLight = LightE;
                }
            }
            else
            {
                if (pos.z < 0)
                {
                    localPos.z += VoxelData.ChunkWidth;
                    targetLight = LightS;
                }
                else if (pos.z >= VoxelData.ChunkWidth)
                {
                    localPos.z -= VoxelData.ChunkWidth;
                    targetLight = LightN;
                }
                else
                {
                    targetLight = LightMap;
                }
            }

            if (!targetLight.IsCreated || targetLight.Length == 0) return ushort.MaxValue;

            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
                return ushort.MaxValue;

            int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
            return targetLight[mapIndex];
        }

        /// <summary>
        /// Sets scalar sunlight level. Writes to both legacy uint bits and the ushort light array.
        /// For blocklight, use <see cref="SetBlocklightRGB"/> instead.
        /// </summary>
        private void SetLight(Vector3Int localPos, byte lightLevel, LightChannel channel, ref NativeHashMap<long, ulong> cache)
        {
            if (localPos.y is < 0 or >= VoxelData.ChunkHeight) return;

            if (localPos.x is >= 0 and < VoxelData.ChunkWidth && localPos.z is >= 0 and < VoxelData.ChunkWidth)
            {
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);

                uint packedData = Map[mapIndex];
                uint newPackedData = channel == LightChannel.Sun
                    ? BurstVoxelDataBitMapping.SetSunLight(packedData, lightLevel)
                    : BurstVoxelDataBitMapping.SetBlockLight(packedData, lightLevel);
                Map[mapIndex] = newPackedData;

                // Dual-write to ushort light array
                ushort lightData = LightMap[mapIndex];
                ushort newLightData = channel == LightChannel.Sun
                    ? LightBitMapping.SetSunLight(lightData, lightLevel)
                    : lightData; // Scalar blocklight SetLight should not be used for RGB — use SetBlocklightRGB
                if (channel == LightChannel.Block)
                    newLightData = LightBitMapping.SetBlocklightRGB(lightData, lightLevel, lightLevel, lightLevel);
                LightMap[mapIndex] = newLightData;
            }
            else
            {
                Vector3Int globalPos = new Vector3Int(localPos.x + ChunkPosition.x, localPos.y, localPos.z + ChunkPosition.y);
                CrossChunkLightMods.Add(new LightModification
                {
                    GlobalPosition = globalPos, LightLevel = lightLevel, Channel = channel,
                    BlockR = channel == LightChannel.Block ? lightLevel : (byte)0,
                    BlockG = channel == LightChannel.Block ? lightLevel : (byte)0,
                    BlockB = channel == LightChannel.Block ? lightLevel : (byte)0,
                });

                // Update the write-through cache (ulong: upper 32 = voxel, lower 16 = light)
                long cacheKey = EncodeNeighborKey(localPos.x, localPos.y, localPos.z);
                uint currentPacked;
                ushort currentLight;
                if (cache.TryGetValue(cacheKey, out ulong existing))
                {
                    currentPacked = (uint)(existing >> 16);
                    currentLight = (ushort)(existing & 0xFFFF);
                }
                else
                {
                    currentPacked = GetPackedData(localPos, ref cache);
                    currentLight = GetLightData(localPos, ref cache);
                }

                uint updatedPacked = channel == LightChannel.Sun
                    ? BurstVoxelDataBitMapping.SetSunLight(currentPacked, lightLevel)
                    : BurstVoxelDataBitMapping.SetBlockLight(currentPacked, lightLevel);
                ushort updatedLight = channel == LightChannel.Sun
                    ? LightBitMapping.SetSunLight(currentLight, lightLevel)
                    : LightBitMapping.SetBlocklightRGB(currentLight, lightLevel, lightLevel, lightLevel);

                cache[cacheKey] = ((ulong)updatedPacked << 16) | updatedLight;
            }
        }

        /// <summary>
        /// Sets per-channel RGB blocklight. Writes to both the ushort light array and the legacy uint bits (max(R,G,B)).
        /// </summary>
        private void SetBlocklightRGB(Vector3Int localPos, byte r, byte g, byte b, ref NativeHashMap<long, ulong> cache)
        {
            if (localPos.y is < 0 or >= VoxelData.ChunkHeight) return;

            byte legacyScalar = (byte)Mathf.Max(r, Mathf.Max(g, b));

            if (localPos.x is >= 0 and < VoxelData.ChunkWidth && localPos.z is >= 0 and < VoxelData.ChunkWidth)
            {
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);

                // Write RGB to ushort light array
                ushort lightData = LightMap[mapIndex];
                LightMap[mapIndex] = LightBitMapping.SetBlocklightRGB(lightData, r, g, b);

                // Dual-write max(R,G,B) to legacy uint blocklight bits
                uint packedData = Map[mapIndex];
                Map[mapIndex] = BurstVoxelDataBitMapping.SetBlockLight(packedData, legacyScalar);
            }
            else
            {
                Vector3Int globalPos = new Vector3Int(localPos.x + ChunkPosition.x, localPos.y, localPos.z + ChunkPosition.y);
                CrossChunkLightMods.Add(new LightModification
                {
                    GlobalPosition = globalPos, LightLevel = legacyScalar, Channel = LightChannel.Block,
                    BlockR = r, BlockG = g, BlockB = b,
                });

                long cacheKey = EncodeNeighborKey(localPos.x, localPos.y, localPos.z);
                uint currentPacked;
                ushort currentLight;
                if (cache.TryGetValue(cacheKey, out ulong existing))
                {
                    currentPacked = (uint)(existing >> 16);
                    currentLight = (ushort)(existing & 0xFFFF);
                }
                else
                {
                    currentPacked = GetPackedData(localPos, ref cache);
                    currentLight = GetLightData(localPos, ref cache);
                }

                uint updatedPacked = BurstVoxelDataBitMapping.SetBlockLight(currentPacked, legacyScalar);
                ushort updatedLight = LightBitMapping.SetBlocklightRGB(currentLight, r, g, b);
                cache[cacheKey] = ((ulong)updatedPacked << 16) | updatedLight;
            }
        }

        #endregion
    }

    // --- Supporting structs for the job ---
    public struct LightRemovalNode
    {
        public Vector3Int Pos;
        public byte LightLevel;
        public byte LightR;
        public byte LightG;
        public byte LightB;
    }

    public struct LightModification
    {
        public Vector3Int GlobalPosition;
        public byte LightLevel;
        public byte BlockR;
        public byte BlockG;
        public byte BlockB;
        public LightChannel Channel;
    }

    public enum LightChannel : byte
    {
        Sun,
        Block,
    }
}
