using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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
            // light array may be uninitialized. Scan center chunk blocks, write emission RGB so the
            // BFS and edge checks can read from the ushort array consistently, and enqueue every
            // stamped position for placement BFS so generation-written emissives propagate (Bug 06).
            SyncEmissionToLightArray(blocklightPlacementQueue);

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
                ushort currentLightData = GetLightData(node.Position, ref neighborWriteCache);
                byte currentLight = LightBitMapping.GetSkyLight(currentLightData);
                if (currentLight < node.OldLightLevel)
                    sunlightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.Position, LightLevel = node.OldLightLevel });
                else if (currentLight > node.OldLightLevel)
                    sunlightPlacementQueue.Enqueue(node.Position);
            }

            while (BlocklightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position, ref neighborWriteCache);
                if (currentPacked == uint.MaxValue) continue;

                // No ushort.MaxValue sentinel check: the GetPackedData bounds check above proves the position is valid,
                // and a fully-lit voxel (sky 15 + RGB 15,15,15) packs to exactly 0xFFFF — the sentinel would silently skip it
                // (e.g. a white lamp on a sunlit surface would neither propagate on place nor clear on break).
                ushort currentLight = GetLightData(node.Position, ref neighborWriteCache);
                byte curR = LightBitMapping.GetBlocklightR(currentLight);
                byte curG = LightBitMapping.GetBlocklightG(currentLight);
                byte curB = LightBitMapping.GetBlocklightB(currentLight);

                // Sync the ushort light array with the block's actual state — PER CHANNEL:
                // - Force-clear: a channel still holding exactly its pre-change value (cur == old > 0 belongs to a block-change node
                //   (ModifyVoxel captures the old light but never rewrites the array, so stale emission/transit light survives there).
                //   Clear it so the darkness pass can launch with the old value.
                //   Cross-chunk applies write the new light value BEFORE enqueuing their wake node and report old != cur on every channel they touch,
                //   so they are never destructively re-interpreted as block removals (Bug 07 defect 1).
                // - Emission floor: an emissive block's own emission is stamped via per-channel max, preserving surface light contributed by other sources.
                //   Wake-up nodes (old = 0) are never cleared — they keep their propagated light so the comparison detects anyIncreased and enqueues them for re-spreading.
                ushort id = BurstVoxelDataBitMapping.GetId(currentPacked);
                BlockTypeJobData props = BlockTypes[id];
                byte newR = node.OldBlockR > 0 && curR == node.OldBlockR ? (byte)0 : curR;
                byte newG = node.OldBlockG > 0 && curG == node.OldBlockG ? (byte)0 : curG;
                byte newB = node.OldBlockB > 0 && curB == node.OldBlockB ? (byte)0 : curB;
                newR = (byte)math.max(newR, (int)props.EmissionR);
                newG = (byte)math.max(newG, (int)props.EmissionG);
                newB = (byte)math.max(newB, (int)props.EmissionB);

                if (newR != curR || newG != curG || newB != curB)
                {
                    bool isRemoval = newR < curR || newG < curG || newB < curB;
                    SetBlocklightRGB(node.Position, newR, newG, newB, isRemovalContext: isRemoval, ref neighborWriteCache);
                }

                bool anyIncreased = newR > node.OldBlockR || newG > node.OldBlockG || newB > node.OldBlockB;
                bool anyDecreased = newR < node.OldBlockR || newG < node.OldBlockG || newB < node.OldBlockB;
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
        /// Every position whose emission gets stamped is also enqueued into the blocklight placement
        /// queue so the emission actually PROPAGATES — generation-written emissives never pass through
        /// ModifyVoxel and would otherwise illuminate only their own voxel (Bug 06). The stamp
        /// condition (stored light below emission) is self-limiting: once propagated, the voxel holds
        /// at least its emission and later job runs neither stamp nor enqueue.
        /// </summary>
        /// <param name="blocklightPlacementQueue">The job-local blocklight placement BFS queue to seed.</param>
        private void SyncEmissionToLightArray(NativeQueue<Vector3Int> blocklightPlacementQueue)
        {
            for (int i = 0; i < Map.Length; i++)
            {
                uint packed = Map[i];
                ushort id = BurstVoxelDataBitMapping.GetId(packed);
                if (id == 0) continue;

                ushort currentLight = LightMap[i];
                byte sun = LightBitMapping.GetSkyLight(currentLight);

                BlockTypeJobData props = BlockTypes[id];
                byte emR = props.EmissionR;
                byte emG = props.EmissionG;
                byte emB = props.EmissionB;

                // Seed emission values into LightMap if the block emits light
                if (emR > 0 || emG > 0 || emB > 0)
                {
                    byte curR = LightBitMapping.GetBlocklightR(currentLight);
                    byte curG = LightBitMapping.GetBlocklightG(currentLight);
                    byte curB = LightBitMapping.GetBlocklightB(currentLight);
                    if (curR < emR || curG < emG || curB < emB)
                    {
                        LightMap[i] = LightBitMapping.PackLightData(sun,
                            (byte)math.max((int)emR, curR),
                            (byte)math.max((int)emG, curG),
                            (byte)math.max((int)emB, curB));

                        // Inverse of ChunkMath.GetFlattenedIndexInChunk
                        // (index = section*SECTION_VOLUME + z*(width*sectionSize) + localY*width + x)
                        // — computed only on this rare stamp path to keep the scan linear.
                        int sectionIdx = i / ChunkMath.SECTION_VOLUME;
                        int inSection = i % ChunkMath.SECTION_VOLUME;
                        int z = inSection / (ChunkMath.CHUNK_WIDTH * ChunkMath.SECTION_SIZE);
                        int rem = inSection % (ChunkMath.CHUNK_WIDTH * ChunkMath.SECTION_SIZE);
                        int y = sectionIdx * ChunkMath.SECTION_SIZE + rem / ChunkMath.CHUNK_WIDTH;
                        int x = rem % ChunkMath.CHUNK_WIDTH;

                        blocklightPlacementQueue.Enqueue(new Vector3Int(x, y, z));
                    }
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

                ushort neighborLightData = GetLightData(neighborPos, ref cache);
                byte neighborLight = LightBitMapping.GetSkyLight(neighborLightData);

                if (neighborLight > 0)
                {
                    if (neighborLight < node.LightLevel)
                    {
                        SetSunlight(neighborPos, 0, ref cache);
                        if (IsInCenterChunk(neighborPos))
                            rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightLevel = neighborLight });
                    }
                    else if (IsInCenterChunk(neighborPos))
                    {
                        pQueue.Enqueue(neighborPos);
                    }
                    else
                    {
                        // The independent sky light lives across the border. The BFS must not
                        // continue into the neighbor chunk, so pull the neighbor's attenuated
                        // contribution back into the just-darkened center voxel instead of
                        // silently dropping the re-spread seed (Bug 07 defect 2).
                        uint centerPacked = GetPackedData(node.Pos, ref cache);
                        if (centerPacked != uint.MaxValue)
                        {
                            CheckEdgeVoxel(node.Pos, centerPacked, GetLightData(node.Pos, ref cache),
                                neighborLightData, pQueue, ref cache);
                        }
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

                // Bounds via the packed-data sentinel, NOT the light sentinel: a fully-lit voxel
                // (sky 15 + RGB 15,15,15) packs to exactly 0xFFFF and would be skipped, leaving
                // its light permanently un-removable.
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;
                ushort neighborLight = GetLightData(neighborPos, ref cache);

                byte nR = LightBitMapping.GetBlocklightR(neighborLight);
                byte nG = LightBitMapping.GetBlocklightG(neighborLight);
                byte nB = LightBitMapping.GetBlocklightB(neighborLight);

                if (nR == 0 && nG == 0 && nB == 0) continue;

                byte newR = nR, newG = nG, newB = nB;
                bool anyRemoved = false;
                bool anyRespread = false;

                ProcessDarknessChannel(nR, node.LightR, ref newR, ref anyRemoved, ref anyRespread);
                ProcessDarknessChannel(nG, node.LightG, ref newG, ref anyRemoved, ref anyRespread);
                ProcessDarknessChannel(nB, node.LightB, ref newB, ref anyRemoved, ref anyRespread);

                if (anyRemoved)
                {
                    SetBlocklightRGB(neighborPos, newR, newG, newB, isRemovalContext: true, ref cache);
                    if (IsInCenterChunk(neighborPos))
                        rQueue.Enqueue(new LightRemovalNode { Pos = neighborPos, LightR = nR, LightG = nG, LightB = nB });
                }

                if (anyRespread)
                {
                    if (IsInCenterChunk(neighborPos))
                    {
                        pQueue.Enqueue(neighborPos);
                    }
                    else
                    {
                        // The independent source's light lives across the border. The BFS must not
                        // continue into the neighbor chunk, so pull the neighbor's attenuated
                        // contribution back into the just-darkened center voxel instead of
                        // silently dropping the re-spread seed (Bug 07 defect 2).
                        uint centerPacked = GetPackedData(node.Pos, ref cache);
                        if (centerPacked != uint.MaxValue)
                            CheckEdgeVoxelRGB(node.Pos, centerPacked, neighborPos, pQueue, ref cache);
                    }
                }
            }
        }

        /// <summary>
        /// Processes a single color channel during RGB darkness removal.
        /// If the neighbor's value is less than the old source value, the channel was dependent
        /// on the removed source and is cleared. Otherwise, it came from an independent source
        /// and is flagged for re-spreading.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ProcessDarknessChannel(byte neighborVal, byte oldVal, ref byte newVal,
            ref bool anyRemoved, ref bool anyRespread)
        {
            if (neighborVal > 0 && neighborVal < oldVal)
            {
                newVal = 0;
                anyRemoved = true;
            }
            else if (neighborVal >= oldVal && oldVal > 0)
            {
                anyRespread = true;
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

            byte sourceLight = LightBitMapping.GetSkyLight(GetLightData(pos, ref cache));
            BlockTypeJobData sourceProps = BlockTypes[BurstVoxelDataBitMapping.GetId(sourcePacked)];

            // An opaque block cannot propagate sunlight to its neighbors.
            if (sourceProps.IsOpaque) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = LightBitMapping.GetSkyLight(GetLightData(neighborPos, ref cache));
                BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                bool isVerticalSunlight = sourceLight == 15 && sourceProps.IsFullyTransparentToLight && VoxelData.FaceChecks[i].y == -1 && neighborProps.IsFullyTransparentToLight;

                byte lightToPropagate;

                if (neighborProps.IsOpaque)
                {
                    lightToPropagate = (byte)Mathf.Max(0, sourceLight - 1);
                    if (lightToPropagate > neighborLight)
                    {
                        SetSunlight(neighborPos, lightToPropagate, ref cache);
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
                        SetSunlight(neighborPos, lightToPropagate, ref cache);
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

            // NOTE: no ushort.MaxValue sentinel check on light reads here — the GetPackedData
            // bounds check above already proves the position is valid, and a legitimately
            // fully-lit voxel (sky 15 + RGB 15,15,15) packs to exactly 0xFFFF, colliding with
            // the sentinel and silently skipping it.
            ushort sourceLight = GetLightData(pos, ref cache);

            byte srcR = LightBitMapping.GetBlocklightR(sourceLight);
            byte srcG = LightBitMapping.GetBlocklightG(sourceLight);
            byte srcB = LightBitMapping.GetBlocklightB(sourceLight);

            // Opaque blocks do not transmit light: they may radiate their OWN emission, but never
            // re-propagate received surface light (source - 1 stamps from neighbors). Without this,
            // surface-lit opaque voxels woken by ModifyVoxel leak light into solid volumes
            // (fixed Bug 09), and an opaque lamp re-radiates light received from a brighter
            // adjacent source. Mirrors the IsOpaque source guard in the sunlight path.
            // Non-emissive opaque sources zero out entirely and exit via the all-zero return.
            BlockTypeJobData sourceProps = BlockTypes[BurstVoxelDataBitMapping.GetId(sourcePacked)];
            if (sourceProps.IsOpaque)
            {
                srcR = sourceProps.EmissionR;
                srcG = sourceProps.EmissionG;
                srcB = sourceProps.EmissionB;
            }

            if (srcR == 0 && srcG == 0 && srcB == 0) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos, ref cache);
                if (neighborPacked == uint.MaxValue) continue;

                // No sentinel check on the light read — bounds proven by the packed check above
                // (0xFFFF is a legitimate fully-lit value, see the source read above).
                ushort neighborLight = GetLightData(neighborPos, ref cache);

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
                        SetBlocklightRGB(neighborPos, finalR, finalG, finalB, isRemovalContext: false, ref cache);
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
                        SetBlocklightRGB(neighborPos, finalR, finalG, finalB, isRemovalContext: false, ref cache);
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
                byte oldSunlight = LightBitMapping.GetSkyLight(GetLightData(currentPos, ref cache));

                // Update the current block in the column to be fully lit.
                if (oldSunlight != 15)
                {
                    SetSunlight(currentPos, 15, ref cache);
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

                    byte neighborSunlight = LightBitMapping.GetSkyLight(GetLightData(neighborPos, ref cache));

                    // If the neighbor has sunlight BUT NOT FULL SUNLIGHT, it needs to be re-evaluated.
                    // A neighbor with level 15 has its own direct sky access and should be ignored.
                    if (neighborSunlight > 0 && neighborSunlight < 15)
                    {
                        // We MUST manually set this block's light to 0 before adding it to the removal queue.
                        // Otherwise, it acts as a permanent ghost light source during the darkness propagation pass!
                        SetSunlight(neighborPos, 0, ref cache);

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
                byte oldSunlight = LightBitMapping.GetSkyLight(GetLightData(currentPos, ref cache));
                BlockTypeJobData props = BlockTypes[BurstVoxelDataBitMapping.GetId(currentPacked)];

                // Update the current block in the column based on the light from above.
                if (oldSunlight != lightFromSky)
                {
                    SetSunlight(currentPos, lightFromSky, ref cache);
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

                        ushort centerLightData = GetLightData(pos, ref cache);
                        ushort neighborLightData = GetLightData(neighborPos, ref cache);

                        CheckEdgeVoxel(pos, centerPacked, centerLightData, neighborLightData,
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
            Vector3Int centerPos, uint centerPacked, ushort centerLightData, ushort neighborLightData,
            NativeQueue<Vector3Int> placementQueue, ref NativeHashMap<long, ulong> cache)
        {
            byte centerLight = LightBitMapping.GetSkyLight(centerLightData);
            byte neighborLight = LightBitMapping.GetSkyLight(neighborLightData);

            BlockTypeJobData centerProps = BlockTypes[BurstVoxelDataBitMapping.GetId(centerPacked)];
            if (centerProps.IsOpaque) return;

            byte expectedFromNeighbor = AttenuateLight(neighborLight, centerProps.Opacity);

            if (expectedFromNeighbor > centerLight)
            {
                SetSunlight(centerPos, expectedFromNeighbor, ref cache);
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

            // No light-sentinel check: every caller has already bounds-checked both positions via
            // GetPackedData (0xFFFF is a legitimate fully-lit value).
            ushort centerLight = GetLightData(centerPos, ref cache);
            ushort neighborLight = GetLightData(neighborPos, ref cache);

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
                SetBlocklightRGB(centerPos, finalR, finalG, finalB, isRemovalContext: false, ref cache);
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
        /// Sets sunlight level in the ushort light array.
        /// For blocklight, use <see cref="SetBlocklightRGB"/> instead.
        /// </summary>
        private void SetSunlight(Vector3Int localPos, byte lightLevel, ref NativeHashMap<long, ulong> cache)
        {
            if (localPos.y is < 0 or >= VoxelData.ChunkHeight) return;

            if (localPos.x is >= 0 and < VoxelData.ChunkWidth && localPos.z is >= 0 and < VoxelData.ChunkWidth)
            {
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
                LightMap[mapIndex] = LightBitMapping.SetSkyLight(LightMap[mapIndex], lightLevel);
            }
            else
            {
                Vector3Int globalPos = new Vector3Int(localPos.x + ChunkPosition.x, localPos.y, localPos.z + ChunkPosition.y);
                CrossChunkLightMods.Add(new LightModification
                {
                    GlobalPosition = globalPos, LightLevel = lightLevel, Channel = LightChannel.Sun,
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

                ushort updatedLight = LightBitMapping.SetSkyLight(currentLight, lightLevel);
                cache[cacheKey] = ((ulong)currentPacked << 16) | updatedLight;
            }
        }

        /// <summary>
        /// Sets per-channel RGB blocklight in the ushort light array.
        /// </summary>
        /// <param name="localPos">The position in the 3x3 grid's local space.</param>
        /// <param name="r">The red blocklight channel (0-15).</param>
        /// <param name="g">The green blocklight channel (0-15).</param>
        /// <param name="b">The blue blocklight channel (0-15).</param>
        /// <param name="isRemovalContext">True when called from a darkness/removal pass. Stamped
        /// into cross-chunk mods so the main-thread apply knows whether zero channels mean
        /// "remove" (removal context) or merely "no contribution" (placement context).</param>
        /// <param name="cache">The write-through cache for cross-chunk modifications.</param>
        private void SetBlocklightRGB(Vector3Int localPos, byte r, byte g, byte b, bool isRemovalContext, ref NativeHashMap<long, ulong> cache)
        {
            if (localPos.y is < 0 or >= VoxelData.ChunkHeight) return;

            if (localPos.x is >= 0 and < VoxelData.ChunkWidth && localPos.z is >= 0 and < VoxelData.ChunkWidth)
            {
                int mapIndex = ChunkMath.GetFlattenedIndexInChunk(localPos.x, localPos.y, localPos.z);
                LightMap[mapIndex] = LightBitMapping.SetBlocklightRGB(LightMap[mapIndex], r, g, b);
            }
            else
            {
                byte legacyScalar = (byte)math.max(r, math.max(g, (int)b));
                Vector3Int globalPos = new Vector3Int(localPos.x + ChunkPosition.x, localPos.y, localPos.z + ChunkPosition.y);
                CrossChunkLightMods.Add(new LightModification
                {
                    GlobalPosition = globalPos, LightLevel = legacyScalar, Channel = LightChannel.Block,
                    BlockR = r, BlockG = g, BlockB = b, IsRemoval = isRemovalContext,
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

                ushort updatedLight = LightBitMapping.SetBlocklightRGB(currentLight, r, g, b);
                cache[cacheKey] = ((ulong)currentPacked << 16) | updatedLight;
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

        /// <summary>
        /// True when this modification was emitted by a darkness/removal pass (blocklight only).
        /// Removal mods may legitimately zero channels; placement mods may only RAISE them —
        /// a zero channel in a placement mod means "the emitting job had no light to contribute
        /// there", never "remove" (a stale snapshot would otherwise erase light owned by an
        /// independent source the emitting job never saw — Bug 07 secondary contributor).
        /// Not part of the save format: LightModification only lives in a job-output NativeList.
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool IsRemoval;
    }

    public enum LightChannel : byte
    {
        Sun,
        Block,
    }
}
