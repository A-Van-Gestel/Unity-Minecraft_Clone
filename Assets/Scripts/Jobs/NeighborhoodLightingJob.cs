using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Data;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
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

        // LI-1: the 3x3 neighborhood's voxels and light, gathered into a single halo-padded linear
        // volume (20×128×20, see ChunkMath.PADDED_LIGHTING_VOLUME) instead of 9 separate maps. The
        // center chunk's [0,16) range lives at padded [2,18); a 2-voxel halo on X/Z carries the widest
        // cross-seam read the BFS performs. A missing neighbor is filled with the uint/ushort MaxValue
        // sentinel at gather time, reproducing the old per-neighbor !IsCreated||Length==0 guard.

        // P-2 Layer 1: the gather runs HERE (on the worker thread) at the top of Execute(), not on the
        // main thread before scheduling. The main thread only fills the 9 snapshot maps below and rents
        // the padded buffers (left unfilled); Execute() gathers center + 8 neighbors into the padded
        // volumes, then runs the existing BFS. Inputs are unchanged point-in-time snapshots, so the gather
        // moving threads is bit-identical by construction — only the gather's location changed.

        // Padded voxel volume. Written ONCE by the in-job gather, then read-only for the rest of Execute()
        // (the job never mutates voxels) — NOT [ReadOnly], because the gather writes it on the worker.
        public NativeArray<uint> PaddedVoxels;

        // Padded ushort light volume. Gathered first, then the ONLY writable light store. It also serves
        // as the in-job cross-chunk read-back store: a SetSunlight/SetBlocklightRGB into a halo cell writes
        // the padded volume directly (replacing the deleted NativeHashMap write-through cache), so a
        // subsequent Get*Data on that position observes the just-written value within the same job execution.
        public NativeArray<ushort> PaddedLight;

        // P-2 Layer 1: gather SOURCES — the center + 8 neighbor section-contiguous full-chunk snapshot maps
        // the worker-thread gather reads into the padded volumes above. Compass directions match
        // WorldJobManager.AcquireNeighborMaps / NeighborMapSet. Each is a point-in-time snapshot copied by
        // the main thread before scheduling (read-only here). A missing neighbor is passed as a created
        // zero-length array (NOT default — Unity job-safety requires every container be constructed), which
        // ChunkMath.GatherPadded* sentinel-fills (uint/ushort MaxValue) exactly as before.
        [ReadOnly]
        public NativeArray<uint> CenterVoxels;

        [ReadOnly]
        public NativeArray<uint> VoxelW;

        [ReadOnly]
        public NativeArray<uint> VoxelE;

        [ReadOnly]
        public NativeArray<uint> VoxelS;

        [ReadOnly]
        public NativeArray<uint> VoxelN;

        [ReadOnly]
        public NativeArray<uint> VoxelSW;

        [ReadOnly]
        public NativeArray<uint> VoxelNW;

        [ReadOnly]
        public NativeArray<uint> VoxelSE;

        [ReadOnly]
        public NativeArray<uint> VoxelNE;

        [ReadOnly]
        public NativeArray<ushort> CenterLight;

        [ReadOnly]
        public NativeArray<ushort> LightW;

        [ReadOnly]
        public NativeArray<ushort> LightE;

        [ReadOnly]
        public NativeArray<ushort> LightS;

        [ReadOnly]
        public NativeArray<ushort> LightN;

        [ReadOnly]
        public NativeArray<ushort> LightSW;

        [ReadOnly]
        public NativeArray<ushort> LightNW;

        [ReadOnly]
        public NativeArray<ushort> LightSE;

        [ReadOnly]
        public NativeArray<ushort> LightNE;

        public Vector2Int ChunkPosition;

        // Queues of initial changes to process
        public NativeQueue<LightQueueNode> SunlightBfsQueue;
        public NativeQueue<LightQueueNode> BlocklightBfsQueue;
        public NativeQueue<Vector2Int> SunlightColumnRecalcQueue;

        // Read-only heightmap.
        [ReadOnly]
        public NativeArray<ushort> Heightmap;

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
        /// Wires the P-2 Layer 1 gather sources (center + 8 neighbor snapshot maps) into this job from a
        /// <see cref="NeighborMapSet"/> plus the center voxel/light maps. This is the SINGLE site that maps
        /// each compass direction onto its <c>[ReadOnly]</c> source field, so the four schedulers
        /// (production <c>WorldJobManager</c>, the editor pipeline, the benchmark, and the validation
        /// harness) cannot drift or transpose a direction — a swap here would surface once, not in four
        /// hand-copied blocks. Called on the main thread at schedule time (not Burst code); must run before
        /// <see cref="IJob.Schedule"/> so <see cref="Execute"/>'s gather sees the populated fields.
        /// </summary>
        /// <param name="neighbors">The 8 neighbor voxel + light snapshot maps (compass-keyed).</param>
        /// <param name="centerVoxels">The center chunk's voxel snapshot (gather source for px/pz ∈ [2,18)).</param>
        /// <param name="centerLight">The center chunk's light snapshot (gather source for the center span).</param>
        public void SetGatherSources(in NeighborMapSet neighbors, NativeArray<uint> centerVoxels, NativeArray<ushort> centerLight)
        {
            CenterVoxels = centerVoxels;
            VoxelW = neighbors.NeighborW;
            VoxelE = neighbors.NeighborE;
            VoxelS = neighbors.NeighborS;
            VoxelN = neighbors.NeighborN;
            VoxelSW = neighbors.NeighborSW;
            VoxelNW = neighbors.NeighborNW;
            VoxelSE = neighbors.NeighborSE;
            VoxelNE = neighbors.NeighborNE;
            CenterLight = centerLight;
            LightW = neighbors.LightW;
            LightE = neighbors.LightE;
            LightS = neighbors.LightS;
            LightN = neighbors.LightN;
            LightSW = neighbors.LightSW;
            LightNW = neighbors.LightNW;
            LightSE = neighbors.LightSE;
            LightNE = neighbors.LightNE;
        }

        /// <summary>
        /// Executes the flood-fill lighting propagation algorithm within the central chunk, crossing boundaries to its 8 neighbors if necessary.
        /// </summary>
        public void Execute()
        {
            // --- P-2 LAYER 1: WORKER-THREAD GATHER ---
            // Gather the center + 8 neighbor snapshot maps into the halo-padded volumes BEFORE any BFS
            // work. This used to run on the main thread before scheduling (LI-1); moving it here lands the
            // ~305 µs gather floor on the worker thread instead, so the main thread pays only the snapshot
            // fill and the worker keeps the 2.4–3× branch-free BFS win. Bit-identical: the inputs are the
            // same unchanged snapshots and the gather logic is unchanged — only the call site moved.
            ChunkMath.GatherPaddedVoxels(PaddedVoxels, CenterVoxels,
                VoxelW, VoxelE, VoxelS, VoxelN, VoxelSW, VoxelNW, VoxelSE, VoxelNE);
            ChunkMath.GatherPaddedLight(PaddedLight, CenterLight,
                LightW, LightE, LightS, LightN, LightSW, LightNW, LightSE, LightNE);

            // Internal queues for the actual flood-fill algorithm. These are temporary for this job's execution.
            NativeQueue<LightRemovalNode> sunlightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            NativeQueue<Vector3Int> sunlightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);
            NativeQueue<LightRemovalNode> blocklightRemovalQueue = new NativeQueue<LightRemovalNode>(Allocator.Temp);
            NativeQueue<Vector3Int> blocklightPlacementQueue = new NativeQueue<Vector3Int>(Allocator.Temp);

            // LI-1: cross-chunk modifications no longer need a separate write-through cache. The halo
            // cells of the padded light volume ARE the cross-chunk read-back store: SetSunlight /
            // SetBlocklightRGB write the halo cell of PaddedLight in place, so subsequent Get*Data reads
            // see the just-written value — the property the old NativeHashMap<long, ulong> cache provided
            // (it existed only because [ReadOnly] neighbor arrays could not be written). Voxels are never
            // mutated, so darkness-removal results being visible to the re-spreading phase still holds.

            // Dedup set for Bug-12 cross-seam sunlight removal mods: a darkness wave can reach the same
            // cross-chunk neighbor from many removal nodes, and EmitCrossChunkSunlightRemoval (unlike
            // SetSunlight) writes neither the padded light volume nor anything else, so nothing else
            // suppresses a revisit. One removal mod per neighbor is sufficient (the main-thread apply is
            // idempotent), so we record emitted neighbor keys here and skip duplicates — keeping
            // CrossChunkLightMods from growing by O(wavefront) redundant entries (and the matching
            // redundant apply-side in-chunk-support scans). Keyed by EncodeNeighborKey.
            NativeHashMap<long, byte> emittedSunRemovals = new NativeHashMap<long, byte>(16, Allocator.Temp);

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
                CheckEdges(sunlightPlacementQueue, blocklightPlacementQueue);
            }

            // --- PASS 0: SEEDING ---
            // Seed the queues with initial changes from the main thread.
            while (SunlightColumnRecalcQueue.TryDequeue(out Vector2Int column))
            {
                RecalculateSunlightForColumn(column.x, column.y, sunlightPlacementQueue, sunlightRemovalQueue);
            }

            while (SunlightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position);
                if (currentPacked == uint.MaxValue) continue;
                ushort currentLightData = GetLightData(node.Position);
                byte currentLight = LightBitMapping.GetSkyLight(currentLightData);
                if (currentLight < node.OldLightLevel)
                    sunlightRemovalQueue.Enqueue(new LightRemovalNode { Pos = node.Position, LightLevel = node.OldLightLevel });
                else if (currentLight > node.OldLightLevel)
                    sunlightPlacementQueue.Enqueue(node.Position);
            }

            while (BlocklightBfsQueue.TryDequeue(out LightQueueNode node))
            {
                uint currentPacked = GetPackedData(node.Position);
                if (currentPacked == uint.MaxValue) continue;

                // No ushort.MaxValue sentinel check: the GetPackedData bounds check above proves the position is valid,
                // and a fully-lit voxel (sky 15 + RGB 15,15,15) packs to exactly 0xFFFF — the sentinel would silently skip it
                // (e.g. a white lamp on a sunlit surface would neither propagate on place nor clear on break).
                ushort currentLight = GetLightData(node.Position);
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
                    SetBlocklightRGB(node.Position, newR, newG, newB, isRemovalContext: isRemoval);
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
                PropagateDarkness(node, LightChannel.Sun, sunlightPlacementQueue, sunlightRemovalQueue, ref emittedSunRemovals);
            while (sunlightPlacementQueue.TryDequeue(out Vector3Int pos))
                PropagateLight(pos, LightChannel.Sun, sunlightPlacementQueue);

            while (blocklightRemovalQueue.TryDequeue(out LightRemovalNode node))
                PropagateDarkness(node, LightChannel.Block, blocklightPlacementQueue, blocklightRemovalQueue, ref emittedSunRemovals);
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
            emittedSunRemovals.Dispose();
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
            // Scan the CENTER chunk only — its [0,16)×[0,128)×[0,16) region within the padded volume.
            // (The old section-contiguous Map/LightMap scan covered exactly these voxels; iterating local
            // coordinates here lets us index the padded volume directly and enqueue the stamped position
            // without the inverse-flatten step the old linear scan needed.)
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        int idx = ChunkMath.GetPaddedLightingIndex(
                            x + ChunkMath.LIGHTING_HALO, y, z + ChunkMath.LIGHTING_HALO);

                        uint packed = PaddedVoxels[idx];
                        ushort id = BurstVoxelDataBitMapping.GetId(packed);
                        if (id == 0) continue;

                        ushort currentLight = PaddedLight[idx];
                        byte sun = LightBitMapping.GetSkyLight(currentLight);

                        BlockTypeJobData props = BlockTypes[id];
                        byte emR = props.EmissionR;
                        byte emG = props.EmissionG;
                        byte emB = props.EmissionB;

                        // Seed emission values into the light volume if the block emits light
                        if (emR > 0 || emG > 0 || emB > 0)
                        {
                            byte curR = LightBitMapping.GetBlocklightR(currentLight);
                            byte curG = LightBitMapping.GetBlocklightG(currentLight);
                            byte curB = LightBitMapping.GetBlocklightB(currentLight);
                            if (curR < emR || curG < emG || curB < emB)
                            {
                                PaddedLight[idx] = LightBitMapping.PackLightData(sun,
                                    (byte)math.max((int)emR, curR),
                                    (byte)math.max((int)emG, curG),
                                    (byte)math.max((int)emB, curB));

                                blocklightPlacementQueue.Enqueue(new Vector3Int(x, y, z));
                            }
                        }
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

        private void PropagateDarkness(LightRemovalNode node, LightChannel channel, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue, ref NativeHashMap<long, byte> emittedSunRemovals)
        {
            if (channel == LightChannel.Block)
            {
                PropagateDarknessRGB(node, pQueue, rQueue);
                return;
            }

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                ushort neighborLightData = GetLightData(neighborPos);
                byte neighborLight = LightBitMapping.GetSkyLight(neighborLightData);

                if (neighborLight > 0)
                {
                    if (neighborLight < node.LightLevel)
                    {
                        SetSunlight(neighborPos, 0);
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
                        uint centerPacked = GetPackedData(node.Pos);
                        if (centerPacked != uint.MaxValue)
                        {
                            CheckEdgeVoxel(node.Pos, centerPacked, GetLightData(node.Pos),
                                neighborPacked, neighborLightData, pQueue);
                        }

                        // Bug 12: a cross-seam neighbor sitting at EXACTLY the removed level, whose own
                        // column is NOT independently sky-lit, is the signature of a mutually-supporting
                        // 2-cycle straddling the boundary — voxel A (here) and voxel B (the neighbor) each
                        // lit "by" the other. Once the genuine external source is gone, the pull-back above
                        // re-lights this voxel from the neighbor's still-high (possibly stale) value, and the
                        // neighbor's own job does the same from this one, so the removal never initiates and
                        // the pair settles one level below the source forever (an over-bright stable-but-wrong
                        // field). Emit a cross-chunk sunlight removal mod so the neighbor re-evaluates: the
                        // Bug 11 veto (CrossChunkLightModApplier.InChunkSunlightSupport) KEEPS it when an
                        // in-chunk source still independently supports the value (e.g. a horizontal shaft) and
                        // CLEARS it when it was only the stale loop. The guards keep this surgical: a strictly
                        // brighter neighbor (> the removed level) is a genuine source, and a neighbor that is
                        // directly sky-exposed (receiving full vertical sunlight) is independently lit — both
                        // are left alone. A fully-opaque neighbor is also skipped: it cannot propagate
                        // sunlight (it only stores surface light), so it is never a participant in a
                        // light-propagation loop, and clearing its cross-seam surface value here would
                        // perturb a sky-exposed wall/floor. Without these guards this would spuriously clear
                        // ordinary sky-lit border voxels whenever a shadow's darkness wave reaches a seam.
                        // A neighbor that this heuristic wrongly suspects (it IS independently fed, e.g. by
                        // a sky-lit chunk on its far side) is protected on the apply side instead: the Bug 11
                        // veto also credits LIVE cross-chunk support from chunks other than this emitter
                        // (CrossChunkLightModApplier.CrossChunkSunlightSupport, the Bug 13 fix) — emitting
                        // here from the stale snapshot and adjudicating there against live data keeps the
                        // initiator aggressive without livelocking perimeter-fed seams.
                        if (neighborLight == node.LightLevel
                            && !BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)].IsOpaque
                            && !IsVerticallySkyLit(neighborPos, neighborPacked))
                            EmitCrossChunkSunlightRemoval(neighborPos, ref emittedSunRemovals);
                    }
                }
            }
        }

        /// <summary>
        /// Per-channel RGB darkness removal for blocklight.
        /// Each channel is compared independently against the removal node's old values.
        /// </summary>
        private void PropagateDarknessRGB(LightRemovalNode node, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[i];

                // Bounds via the packed-data sentinel, NOT the light sentinel: a fully-lit voxel
                // (sky 15 + RGB 15,15,15) packs to exactly 0xFFFF and would be skipped, leaving
                // its light permanently un-removable.
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;
                ushort neighborLight = GetLightData(neighborPos);

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
                    SetBlocklightRGB(neighborPos, newR, newG, newB, isRemovalContext: true);
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
                        uint centerPacked = GetPackedData(node.Pos);
                        if (centerPacked != uint.MaxValue)
                            CheckEdgeVoxelRGB(node.Pos, centerPacked, neighborPos, pQueue);
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
        /// Forwards to the shared <see cref="LightAttenuation.Attenuate"/> (the single definition of the
        /// Starlight/Moonrise formula <c>max(0, sourceLight - max(1, opacity))</c>) so the BFS, the
        /// validation oracle, and the cross-chunk veto can never disagree on attenuation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte AttenuateLight(int sourceLight, byte opacity)
        {
            return LightAttenuation.Attenuate(sourceLight, opacity);
        }

        private void PropagateLight(Vector3Int pos, LightChannel channel, NativeQueue<Vector3Int> pQueue)
        {
            if (channel == LightChannel.Block)
            {
                PropagateLightRGB(pos, pQueue);
                return;
            }

            uint sourcePacked = GetPackedData(pos);
            if (sourcePacked == uint.MaxValue) return;

            byte sourceLight = LightBitMapping.GetSkyLight(GetLightData(pos));
            BlockTypeJobData sourceProps = BlockTypes[BurstVoxelDataBitMapping.GetId(sourcePacked)];

            // An opaque block cannot propagate sunlight to its neighbors.
            if (sourceProps.IsOpaque) return;

            for (int i = 0; i < 6; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[i];
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                byte neighborLight = LightBitMapping.GetSkyLight(GetLightData(neighborPos));
                BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                bool isVerticalSunlight = sourceLight == 15 && sourceProps.IsFullyTransparentToLight && VoxelData.FaceChecks[i].y == -1 && neighborProps.IsFullyTransparentToLight;

                byte lightToPropagate;

                if (neighborProps.IsOpaque)
                {
                    lightToPropagate = (byte)math.max(0, sourceLight - 1);
                    if (lightToPropagate > neighborLight)
                    {
                        SetSunlight(neighborPos, lightToPropagate);
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
                        SetSunlight(neighborPos, lightToPropagate);
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
        private void PropagateLightRGB(Vector3Int pos, NativeQueue<Vector3Int> pQueue)
        {
            uint sourcePacked = GetPackedData(pos);
            if (sourcePacked == uint.MaxValue) return;

            // NOTE: no ushort.MaxValue sentinel check on light reads here — the GetPackedData
            // bounds check above already proves the position is valid, and a legitimately
            // fully-lit voxel (sky 15 + RGB 15,15,15) packs to exactly 0xFFFF, colliding with
            // the sentinel and silently skipping it.
            ushort sourceLight = GetLightData(pos);

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
                uint neighborPacked = GetPackedData(neighborPos);
                if (neighborPacked == uint.MaxValue) continue;

                // No sentinel check on the light read — bounds proven by the packed check above
                // (0xFFFF is a legitimate fully-lit value, see the source read above).
                ushort neighborLight = GetLightData(neighborPos);

                byte nR = LightBitMapping.GetBlocklightR(neighborLight);
                byte nG = LightBitMapping.GetBlocklightG(neighborLight);
                byte nB = LightBitMapping.GetBlocklightB(neighborLight);

                BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];

                if (neighborProps.IsOpaque)
                {
                    // Opaque blocks receive surface light (source - 1) but do not propagate further
                    byte propR = (byte)math.max(0, srcR - 1);
                    byte propG = (byte)math.max(0, srcG - 1);
                    byte propB = (byte)math.max(0, srcB - 1);

                    byte finalR = (byte)math.max(nR, (int)propR);
                    byte finalG = (byte)math.max(nG, (int)propG);
                    byte finalB = (byte)math.max(nB, (int)propB);

                    if (finalR != nR || finalG != nG || finalB != nB)
                    {
                        SetBlocklightRGB(neighborPos, finalR, finalG, finalB, isRemovalContext: false);
                    }
                }
                else
                {
                    byte propR = AttenuateLight(srcR, neighborProps.Opacity);
                    byte propG = AttenuateLight(srcG, neighborProps.Opacity);
                    byte propB = AttenuateLight(srcB, neighborProps.Opacity);

                    byte finalR = (byte)math.max(nR, (int)propR);
                    byte finalG = (byte)math.max(nG, (int)propG);
                    byte finalB = (byte)math.max(nB, (int)propB);

                    if (finalR != nR || finalG != nG || finalB != nB)
                    {
                        SetBlocklightRGB(neighborPos, finalR, finalG, finalB, isRemovalContext: false);
                        if (IsInCenterChunk(neighborPos))
                            pQueue.Enqueue(neighborPos);
                    }
                }
            }
        }

        private void RecalculateSunlightForColumn(int x, int z, NativeQueue<Vector3Int> pQueue, NativeQueue<LightRemovalNode> rQueue)
        {
            // Use the heightmap to find the Y-level of the highest block that has any opacity.
            int heightmapIndex = x + VoxelData.ChunkWidth * z;
            ushort highestBlockY = Heightmap[heightmapIndex];

            // --- PASS 1: Above the highest block ---
            // Everything above this point is transparent to the sky and should be fully sunlit.
            for (int y = VoxelData.ChunkHeight - 1; y > highestBlockY; y--)
            {
                Vector3Int currentPos = new Vector3Int(x, y, z);
                byte oldSunlight = LightBitMapping.GetSkyLight(GetLightData(currentPos));

                // Update the current block in the column to be fully lit.
                if (oldSunlight != 15)
                {
                    SetSunlight(currentPos, 15);
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

                    byte neighborSunlight = LightBitMapping.GetSkyLight(GetLightData(neighborPos));

                    // If the neighbor has sunlight BUT NOT FULL SUNLIGHT, it needs to be re-evaluated.
                    // A neighbor with level 15 has its own direct sky access and should be ignored.
                    if (neighborSunlight > 0 && neighborSunlight < 15)
                    {
                        // We MUST manually set this block's light to 0 before adding it to the removal queue.
                        // Otherwise, it acts as a permanent ghost light source during the darkness propagation pass!
                        SetSunlight(neighborPos, 0);

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
                uint currentPacked = GetPackedData(currentPos);
                byte oldSunlight = LightBitMapping.GetSkyLight(GetLightData(currentPos));
                BlockTypeJobData props = BlockTypes[BurstVoxelDataBitMapping.GetId(currentPacked)];

                // Update the current block in the column based on the light from above.
                if (oldSunlight != lightFromSky)
                {
                    SetSunlight(currentPos, lightFromSky);
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
            NativeQueue<Vector3Int> sunPlacement, NativeQueue<Vector3Int> blockPlacement)
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

                        uint centerPacked = GetPackedData(pos);
                        if (centerPacked == uint.MaxValue) continue;

                        uint neighborPacked = GetPackedData(neighborPos);
                        if (neighborPacked == uint.MaxValue) continue;

                        ushort centerLightData = GetLightData(pos);
                        ushort neighborLightData = GetLightData(neighborPos);

                        CheckEdgeVoxel(pos, centerPacked, centerLightData, neighborPacked, neighborLightData,
                            sunPlacement);
                        CheckEdgeVoxelRGB(pos, centerPacked, neighborPos,
                            blockPlacement);
                    }
                }
            }
        }

        /// <summary>
        /// Checks a single border voxel's sunlight against its cross-chunk neighbor.
        /// Detects missing light (black spots) where the neighbor has light that should propagate here.
        /// </summary>
        /// <param name="neighborPacked">The cross-chunk neighbor's packed voxel data, used to reject an
        /// opaque neighbor as a light source (its sky value is non-transmissible surface light).</param>
        private void CheckEdgeVoxel(
            Vector3Int centerPos, uint centerPacked, ushort centerLightData,
            uint neighborPacked, ushort neighborLightData,
            NativeQueue<Vector3Int> placementQueue)
        {
            byte centerLight = LightBitMapping.GetSkyLight(centerLightData);
            byte neighborLight = LightBitMapping.GetSkyLight(neighborLightData);

            BlockTypeJobData centerProps = BlockTypes[BurstVoxelDataBitMapping.GetId(centerPacked)];
            if (centerProps.IsOpaque) return;

            // An opaque neighbor cannot transmit sunlight across the border: its sky value is
            // non-propagable surface light (opaque blocks have no sky emission), so seeding from it would
            // leak light out of a wall into the adjacent chunk (Bug 10). Mirror of the IsOpaque source
            // guard in PropagateLight; the add-only edge check could never reconcile the surplus away.
            BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];
            if (neighborProps.IsOpaque) return;

            byte expectedFromNeighbor = AttenuateLight(neighborLight, centerProps.Opacity);

            if (expectedFromNeighbor > centerLight)
            {
                SetSunlight(centerPos, expectedFromNeighbor);
                placementQueue.Enqueue(centerPos);
            }
        }

        /// <summary>
        /// Checks a single border voxel's blocklight RGB against its cross-chunk neighbor.
        /// Per-channel comparison detects missing light on any channel.
        /// </summary>
        private void CheckEdgeVoxelRGB(
            Vector3Int centerPos, uint centerPacked, Vector3Int neighborPos,
            NativeQueue<Vector3Int> placementQueue)
        {
            BlockTypeJobData centerProps = BlockTypes[BurstVoxelDataBitMapping.GetId(centerPacked)];
            if (centerProps.IsOpaque) return;

            // No light-sentinel check: every caller has already bounds-checked both positions via
            // GetPackedData (0xFFFF is a legitimate fully-lit value).
            ushort centerLight = GetLightData(centerPos);
            ushort neighborLight = GetLightData(neighborPos);

            byte cR = LightBitMapping.GetBlocklightR(centerLight);
            byte cG = LightBitMapping.GetBlocklightG(centerLight);
            byte cB = LightBitMapping.GetBlocklightB(centerLight);

            byte nR = LightBitMapping.GetBlocklightR(neighborLight);
            byte nG = LightBitMapping.GetBlocklightG(neighborLight);
            byte nB = LightBitMapping.GetBlocklightB(neighborLight);

            // An opaque neighbor must not transmit its RECEIVED surface blocklight across the border (that
            // leaks a 1-deep stamp out of a wall — Bug 10); it may only seed its OWN emission, exactly as
            // PropagateLightRGB treats an opaque source. So an opaque lamp still illuminates across the
            // border while an opaque non-emissive block (emission 0) contributes nothing.
            uint neighborPacked = GetPackedData(neighborPos);
            if (neighborPacked == uint.MaxValue) return;
            BlockTypeJobData neighborProps = BlockTypes[BurstVoxelDataBitMapping.GetId(neighborPacked)];
            if (neighborProps.IsOpaque)
            {
                nR = neighborProps.EmissionR;
                nG = neighborProps.EmissionG;
                nB = neighborProps.EmissionB;
            }

            byte expR = AttenuateLight(nR, centerProps.Opacity);
            byte expG = AttenuateLight(nG, centerProps.Opacity);
            byte expB = AttenuateLight(nB, centerProps.Opacity);

            byte finalR = (byte)math.max(cR, (int)expR);
            byte finalG = (byte)math.max(cG, (int)expG);
            byte finalB = (byte)math.max(cB, (int)expB);

            if (finalR != cR || finalG != cG || finalB != cB)
            {
                SetBlocklightRGB(centerPos, finalR, finalG, finalB, isRemovalContext: false);
                placementQueue.Enqueue(centerPos);
            }
        }

        #endregion

        #region Helper Methods

        /// Get the packed voxel data for a coordinate in the 3x3 grid, read from the halo-padded volume.
        /// Examples:
        /// - A position like (-2, y, 5) is in the West neighbor (padded px = 0).
        /// - A position like (17, y, 17) is the +1 diagonal rim of the North-East neighbor (padded 19,19).
        /// Sentinel semantics are preserved exactly: out-of-Y → uint.MaxValue; horizontally beyond the
        /// 2-voxel halo (grid x/z outside [-2,17]) → uint.MaxValue; a missing neighbor → uint.MaxValue
        /// (the gather fill stamps MaxValue into that region). The center voxel volume is read-only.
        private uint GetPackedData(Vector3Int pos)
        {
            if (pos.y is < 0 or >= VoxelData.ChunkHeight) return uint.MaxValue;

            int px = pos.x + ChunkMath.LIGHTING_HALO;
            int pz = pos.z + ChunkMath.LIGHTING_HALO;
            if ((uint)px >= ChunkMath.PADDED_CHUNK_WIDTH || (uint)pz >= ChunkMath.PADDED_CHUNK_WIDTH)
                return uint.MaxValue;

            return PaddedVoxels[ChunkMath.GetPaddedLightingIndex(px, pos.y, pz)];
        }

        /// <summary>
        /// Gets the ushort light data for a position in the 3x3 grid, read from the halo-padded light
        /// volume. Returns ushort.MaxValue for out-of-bounds positions (out-of-Y, beyond the 2-voxel
        /// halo, or a missing neighbor — the latter stamped MaxValue into the volume by the gather fill).
        /// </summary>
        private ushort GetLightData(Vector3Int pos)
        {
            if (pos.y is < 0 or >= VoxelData.ChunkHeight) return ushort.MaxValue;

            int px = pos.x + ChunkMath.LIGHTING_HALO;
            int pz = pos.z + ChunkMath.LIGHTING_HALO;
            if ((uint)px >= ChunkMath.PADDED_CHUNK_WIDTH || (uint)pz >= ChunkMath.PADDED_CHUNK_WIDTH)
                return ushort.MaxValue;

            return PaddedLight[ChunkMath.GetPaddedLightingIndex(px, pos.y, pz)];
        }

        /// <summary>
        /// Maps a position in this job's 3x3-grid local space to its world-space voxel position. The chunk's
        /// horizontal origin <see cref="ChunkPosition"/> (a 2D X/Z offset) is added to X and Z; the voxel Y
        /// is already global and passes through unchanged. Single definition of the local→global mapping
        /// shared by every cross-chunk emitter (<see cref="SetSunlight"/>, <see cref="SetBlocklightRGB"/>,
        /// <see cref="EmitCrossChunkSunlightRemoval"/>).
        /// </summary>
        /// <param name="localPos">The position in the 3x3 grid's local space.</param>
        /// <returns>The world-space voxel position.</returns>
        private Vector3Int LocalToGlobal(Vector3Int localPos)
        {
            return new Vector3Int(localPos.x + ChunkPosition.x, localPos.y, localPos.z + ChunkPosition.y);
        }

        /// <summary>
        /// Returns true when the voxel at <paramref name="pos"/> receives full vertical sunlight — it is
        /// fully transparent and the voxel directly above it is fully transparent and holds full sky (15).
        /// Encodes the same vertical-sunlight rule that <see cref="PropagateLight"/>'s
        /// <c>isVerticalSunlight</c> relies on (a fully-transparent voxel directly below a fully-transparent
        /// sky-15 voxel is lit to 15 with no attenuation), but evaluated standalone for a single voxel
        /// rather than across a source→neighbor downward step — the two are independent code paths, so a
        /// change to the vertical-sunlight rule must be mirrored in both. Used by
        /// <see cref="PropagateDarkness"/> to recognize a genuinely sky-exposed cross-seam neighbor (which
        /// is independently lit and must NOT be sent a Bug-12 cross-seam removal mod) versus a roofed seam
        /// voxel (which can only be the stale mutual-support side of a 2-cycle).
        /// </summary>
        /// <param name="pos">The voxel position in the 3x3 grid's local space.</param>
        /// <param name="packed">The voxel's already-fetched packed data (avoids a redundant lookup).</param>
        /// <returns>True if the voxel is directly lit by vertical sunlight.</returns>
        private bool IsVerticallySkyLit(Vector3Int pos, uint packed)
        {
            if (!BlockTypes[BurstVoxelDataBitMapping.GetId(packed)].IsFullyTransparentToLight) return false;

            Vector3Int above = new Vector3Int(pos.x, pos.y + 1, pos.z);
            uint abovePacked = GetPackedData(above);
            if (abovePacked == uint.MaxValue) return false;
            if (!BlockTypes[BurstVoxelDataBitMapping.GetId(abovePacked)].IsFullyTransparentToLight) return false;

            return LightBitMapping.GetSkyLight(GetLightData(above)) == 15;
        }

        /// <summary>
        /// Emits a cross-chunk sunlight REMOVAL mod (level 0) for a neighbor-chunk voxel WITHOUT touching
        /// the padded light volume — the neighbor's halo value is left untouched in-job so the pull-back
        /// re-spread reads the unchanged snapshot, and the actual decision is deferred to the main-thread
        /// cross-chunk apply (<see cref="Helpers.CrossChunkLightModApplier.ComputeSunlight"/> +
        /// its in-chunk-support veto). Used by <see cref="PropagateDarkness"/> to break the Bug 12
        /// over-bright cross-seam loop: the neighbor re-evaluates and clears only if it was solely the stale
        /// mutual support. Unlike <see cref="SetSunlight"/>, this neither writes the padded volume nor seeds
        /// a local BFS node — it only appends the modification. A darkness wave can reach the same neighbor from
        /// many removal nodes, so <paramref name="emittedSunRemovals"/> dedups: only the first emission per
        /// neighbor is appended (the apply is idempotent), keeping the mod list from growing by O(wavefront).
        /// </summary>
        /// <param name="neighborPos">The neighbor-chunk voxel position in the 3x3 grid's local space.</param>
        /// <param name="emittedSunRemovals">Per-job set of neighbor keys already sent a removal mod.</param>
        private void EmitCrossChunkSunlightRemoval(Vector3Int neighborPos, ref NativeHashMap<long, byte> emittedSunRemovals)
        {
            // TryAdd returns false when the key is already present — one removal mod per neighbor suffices.
            if (!emittedSunRemovals.TryAdd(EncodeNeighborKey(neighborPos.x, neighborPos.y, neighborPos.z), 0))
                return;

            CrossChunkLightMods.Add(new LightModification
            {
                GlobalPosition = LocalToGlobal(neighborPos), LightLevel = 0, Channel = LightChannel.Sun,
            });
        }

        /// <summary>
        /// Sets sunlight level in the padded light volume (the single writable store for both center and
        /// halo cells). For blocklight, use <see cref="SetBlocklightRGB"/> instead.
        /// <para>The in-place RMW reads the live padded value exactly as the old write-through cache did,
        /// so out-of-center writes accumulate identically; a cross-chunk mod is still emitted ONLY for an
        /// out-of-center position and still carries the INPUT <paramref name="lightLevel"/> (not the RMW
        /// result) — identical to the pre-LI-1 behavior.</para>
        /// </summary>
        private void SetSunlight(Vector3Int localPos, byte lightLevel)
        {
            if (localPos.y is < 0 or >= VoxelData.ChunkHeight) return;

            int px = localPos.x + ChunkMath.LIGHTING_HALO;
            int pz = localPos.z + ChunkMath.LIGHTING_HALO;
            if ((uint)px >= ChunkMath.PADDED_CHUNK_WIDTH || (uint)pz >= ChunkMath.PADDED_CHUNK_WIDTH) return;

            int idx = ChunkMath.GetPaddedLightingIndex(px, localPos.y, pz);
            PaddedLight[idx] = LightBitMapping.SetSkyLight(PaddedLight[idx], lightLevel);

            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
            {
                CrossChunkLightMods.Add(new LightModification
                {
                    GlobalPosition = LocalToGlobal(localPos), LightLevel = lightLevel, Channel = LightChannel.Sun,
                });
            }
        }

        /// <summary>
        /// Sets per-channel RGB blocklight in the padded light volume (the single writable store for both
        /// center and halo cells).
        /// </summary>
        /// <param name="localPos">The position in the 3x3 grid's local space.</param>
        /// <param name="r">The red blocklight channel (0-15).</param>
        /// <param name="g">The green blocklight channel (0-15).</param>
        /// <param name="b">The blue blocklight channel (0-15).</param>
        /// <param name="isRemovalContext">True when called from a darkness/removal pass. Stamped
        /// into cross-chunk mods so the main-thread apply knows whether zero channels mean
        /// "remove" (removal context) or merely "no contribution" (placement context).</param>
        private void SetBlocklightRGB(Vector3Int localPos, byte r, byte g, byte b, bool isRemovalContext)
        {
            if (localPos.y is < 0 or >= VoxelData.ChunkHeight) return;

            int px = localPos.x + ChunkMath.LIGHTING_HALO;
            int pz = localPos.z + ChunkMath.LIGHTING_HALO;
            if ((uint)px >= ChunkMath.PADDED_CHUNK_WIDTH || (uint)pz >= ChunkMath.PADDED_CHUNK_WIDTH) return;

            int idx = ChunkMath.GetPaddedLightingIndex(px, localPos.y, pz);
            PaddedLight[idx] = LightBitMapping.SetBlocklightRGB(PaddedLight[idx], r, g, b);

            if (localPos.x < 0 || localPos.x >= VoxelData.ChunkWidth ||
                localPos.z < 0 || localPos.z >= VoxelData.ChunkWidth)
            {
                byte legacyScalar = (byte)math.max(r, math.max(g, (int)b));
                CrossChunkLightMods.Add(new LightModification
                {
                    GlobalPosition = LocalToGlobal(localPos), LightLevel = legacyScalar, Channel = LightChannel.Block,
                    BlockR = r, BlockG = g, BlockB = b, IsRemoval = isRemovalContext,
                });
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
