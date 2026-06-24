using System;
using System.Runtime.InteropServices;
using Data;
using Helpers;
using Jobs.BurstData;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Jobs
{
    /// <summary>
    /// TG-4 Phase 3 — Burst port of the managed fluid behavior tick (<c>BlockBehavior.Fluids</c>) for
    /// <b>Tier-1 interior</b> fluid voxels (those whose entire ±4 X/Z neighbor footprint stays inside the chunk,
    /// so no cross-chunk read is ever needed). It is a <b>bit-for-bit</b> reimplementation of
    /// <c>HandleFluidFlow</c> + <c>IsFluidActive</c> over a native packed voxel snapshot — the same emission and
    /// activation decisions, just off the managed path so the per-voxel GC churn (the dam-break stutter spike)
    /// disappears.
    /// <para>
    /// <b>Inputs are read-only and pre-tick:</b> both the "Behave" (emit) and "Active" (drop) halves read the same
    /// <see cref="VoxelMap"/> snapshot, exactly as the managed tick does (mods are applied only after the whole
    /// pass). The job appends emitted <see cref="VoxelMod"/>s to <see cref="Mods"/> and the flat indices of voxels
    /// that became inactive to <see cref="NowInactive"/>; the main thread drains both afterward (the unchanged
    /// <c>ApplyModifications</c> path + bucket removal).
    /// </para>
    /// <para>
    /// <b>Tier-2 (border) fluids are NOT handled here</b> — they keep the managed hybrid path until Phase 4's
    /// neighbor view exists. The caller is responsible for passing only interior indices in
    /// <see cref="InteriorFluidIndices"/> (see the margin-4 classifier); this job assumes every in-chunk read is
    /// in-bounds and never reaches across a border.
    /// </para>
    /// </summary>
    [BurstCompile]
    public struct FluidTickJob : IJob
    {
        /// <summary>The chunk's section-contiguous packed voxel snapshot (the <see cref="ChunkMath.GetFlattenedIndexInChunk"/> layout).</summary>
        [ReadOnly]
        public NativeArray<uint> VoxelMap;

        /// <summary>Global block-type blob indexed by id (carries the fluid behavior props added in TG-4 Phase 3 C1).</summary>
        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        /// <summary>Flat chunk indices (<see cref="ChunkMath.GetFlattenedIndexInChunk"/>) of the interior fluid voxels to tick.</summary>
        [ReadOnly]
        public NativeArray<int> InteriorFluidIndices;

        /// <summary>The per-tick salt (<c>World.TickCounter</c>) feeding the viscosity RNG — passed in (seam S2).</summary>
        public int TickCounter;

        /// <summary>The chunk's world-space X/Z origin (<c>ChunkData.Position</c>) for building global mod positions + RNG seeds.</summary>
        public int2 ChunkOrigin;

        /// <summary>Output: the emitted voxel modifications (the Behave stream).</summary>
        public NativeList<VoxelMod> Mods;

        /// <summary>Output: flat indices of voxels that are no longer active and should be dropped from the bucket.</summary>
        public NativeList<int> NowInactive;

        /// <summary>
        /// Output: the number of mods emitted for each source, in <see cref="InteriorFluidIndices"/> order (one entry
        /// per source, including zeros). Lets the caller replay the job's mods in the <b>original bucket order</b> —
        /// interleaved with the managed border path — so the emitted stream is byte-identical to the legacy single
        /// loop (zero drift; BH-D1 same-target order holds).
        /// </summary>
        public NativeList<int> ModsPerSource;

        /// <summary>32-bit golden-ratio constant mixing the per-tick salt into the voxel-position hash (mirror of BlockBehavior).</summary>
        private const uint TICK_SALT_HASH_MULTIPLIER = 0x9E3779B1u;

        /// <inheritdoc />
        public void Execute()
        {
            // Allocate the BFS scratch once for the whole pass (locals, threaded into the flow chain); CalculateFlowCost
            // Clear()s it per call. Replaces the prior per-call Temp NativeQueue + NativeHashSet allocation
            // (≤4×/voxel/tick). Job fields can't hold this — Burst requires job containers constructed before schedule.
            NativeQueue<SearchNode> searchQueue = new NativeQueue<SearchNode>(Allocator.Temp);
            NativeHashSet<int3> searchVisited = new NativeHashSet<int3>(64, Allocator.Temp);

            foreach (int index in InteriorFluidIndices)
            {
                int modsBefore = Mods.Length;

                ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);

                uint packed = VoxelMap[index];
                ushort id = BurstVoxelDataBitMapping.GetId(packed);

                // Stale-bucket guard: an index whose voxel is no longer a fluid emits nothing (Behave) and is
                // inactive (Active) — drop it. Mirrors the managed Behave/Active early-outs for a non-fluid voxel.
                if (id >= BlockTypes.Length || BlockTypes[id].FluidType == FluidType.None)
                {
                    NowInactive.Add(index);
                    ModsPerSource.Add(0);
                    continue;
                }

                byte level = BurstVoxelDataBitMapping.GetFluidLevel(packed);
                BlockTypeJobData props = BlockTypes[id];

                // Behave: emit this voxel's mods for the tick.
                HandleFluidFlow(x, y, z, id, level, props, searchQueue, searchVisited);

                // Active: re-evaluate against the same pre-tick snapshot; drop if stable.
                if (!IsFluidActive(x, y, z, id, level, props))
                    NowInactive.Add(index);

                // Record this source's mod run length so the caller can replay it in bucket order.
                ModsPerSource.Add(Mods.Length - modsBefore);
            }

            searchQueue.Dispose();
            searchVisited.Dispose();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  Behave — flow logic (decay → gravity → horizontal spread), 1:1 with BlockBehavior.Fluids
        // ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Manages the flow logic for a single fluid voxel (Beta 1.3.2 order: Drain → Gravitate → Spread).</summary>
        private void HandleFluidFlow(int x, int y, int z, ushort currentId, byte currentLevel, BlockTypeJobData props,
            NativeQueue<SearchNode> searchQueue, NativeHashSet<int3> searchVisited)
        {
            LocalVoxel below = GetStateLocal(x, y - 1, z);
            bool belowIsSameFluid = below.Has && below.Id == currentId;
            bool canFlowDown = below.Has && !below.Props.IsSolid && !belowIsSameFluid;

            // Step 1: decay / drainage. Returns true if the block died or this voxel is done for the tick.
            if (HandleFluidDecay(x, y, z, currentId, ref currentLevel, props))
                return;

            byte effectiveLevel = GetEffectiveLevel(currentLevel);
            bool falling = IsFalling(currentLevel);

            // Step 2 & 3: gravity. Returns true if gravity acted (skip horizontal spread this tick).
            if (HandleFluidVertical(x, y, z, currentId, effectiveLevel, canFlowDown))
                return;

            // Step 4: horizontal spread.
            HandleFluidSpread(x, y, z, currentId, currentLevel, effectiveLevel, props, below, belowIsSameFluid, falling,
                searchQueue, searchVisited);
        }

        /// <summary>Drains/decays the fluid based on its support. Returns true if a terminal mod was emitted for this voxel.</summary>
        private bool HandleFluidDecay(int x, int y, int z, ushort currentId, ref byte currentLevel, BlockTypeJobData props)
        {
            if (currentLevel == 0) return false; // Source blocks never decay

            CalculateExpectedFluidLevel(x, y, z, currentId, props, out byte expectedEffectiveLevel, out bool isFedFromAbove);

            bool falling = IsFalling(currentLevel);
            bool isFallingAndCutOff = falling && !isFedFromAbove;

            bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
            byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

            if (expectedEffectiveLevel >= props.FlowLevels || isFallingAndCutOff)
            {
                Emit(x, y, z, 0, 0, true); // decay to air
                return true;
            }

            if (expectedFluidLevel != currentLevel)
            {
                Emit(x, y, z, currentId, BurstVoxelDataBitMapping.BuildMetaLegacy(0, expectedFluidLevel, true), true);
                currentLevel = expectedFluidLevel; // update by ref for the orchestrator
            }

            return false;
        }

        /// <summary>Handles fluid falling downward. Returns true if gravity acted (bypassing horizontal spread).</summary>
        private bool HandleFluidVertical(int x, int y, int z, ushort currentId, byte effectiveLevel, bool canFlowDown)
        {
            if (!canFlowDown) return false;

            Emit(x, y - 1, z, currentId, BurstVoxelDataBitMapping.BuildMetaLegacy(0, MakeFalling(effectiveLevel), true), false);
            return true;
        }

        /// <summary>Handles horizontal spreading across supported surfaces (Minecraft source/non-source gate + viscosity RNG).</summary>
        private void HandleFluidSpread(int x, int y, int z, ushort currentId, byte currentLevel, byte effectiveLevel,
            BlockTypeJobData props, LocalVoxel below, bool belowIsSameFluid, bool falling,
            NativeQueue<SearchNode> searchQueue, NativeHashSet<int3> searchVisited)
        {
            bool canSpreadHorizontally = effectiveLevel == 0 ||
                                         (below.Has && below.Props.IsSolid && !belowIsSameFluid);

            if (!canSpreadHorizontally) return;

            byte newLevel = falling && props.WaterfallsMaxSpread ? (byte)1 : (byte)(effectiveLevel + 1);
            if (newLevel >= props.FlowLevels) return;

            // Lava viscosity randomization: a fluid with spreadChance < 1 randomly skips spread ticks. Water (1.0)
            // never skips, so the RNG is guarded away for the dominant fluid (also keeps the hash off the hot path).
            if (props.SpreadChance < 1f)
            {
                Random rng = SeededVoxelRandom(new int3(x + ChunkOrigin.x, y, z + ChunkOrigin.y));
                if (rng.NextFloat() > props.SpreadChance) return;
            }

            byte optimalFlowMask = GetOptimalFlowDirections(x, y, z, currentId, searchQueue, searchVisited);

            for (int i = 0; i < 4; i++)
            {
                if ((optimalFlowMask & (1 << i)) == 0) continue;

                int3 off = HorizontalOffset(i);
                int nx = x + off.x, ny = y + off.y, nz = z + off.z;
                LocalVoxel nb = GetStateLocal(nx, ny, nz);
                if (!nb.Has) continue;

                bool neighborIsAir = nb.Id == BlockIDs.Air;
                bool neighborIsReplaceable = !nb.Props.IsSolid && (nb.Props.Tags & BlockTags.REPLACEABLE) != 0;
                bool neighborIsSameFluidAndWorse = nb.Id == currentId &&
                                                   !IsFalling(nb.Level) &&
                                                   GetEffectiveLevel(nb.Level) > newLevel;

                if (neighborIsAir || neighborIsReplaceable || neighborIsSameFluidAndWorse)
                    Emit(nx, ny, nz, currentId, BurstVoxelDataBitMapping.BuildMetaLegacy(0, newLevel, true), false);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  Active — stability test (drop-from-bucket decision), 1:1 with BlockBehavior.IsFluidActive
        // ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Returns true if the fluid block is unstable (can flow down, settle, spread, or drain) and must keep ticking.</summary>
        private bool IsFluidActive(int x, int y, int z, ushort id, byte currentLevel, BlockTypeJobData props)
        {
            byte effectiveLevel = GetEffectiveLevel(currentLevel);

            // Reason 1: can it flow down?
            LocalVoxel below = GetStateLocal(x, y - 1, z);
            bool belowIsSameFluid = below.Has && below.Id == id;
            if (below.Has && !below.Props.IsSolid && !belowIsSameFluid)
                return true;

            // Reason 3: can it flow horizontally?
            bool canSpreadHorizontally = effectiveLevel == 0 ||
                                         (below.Has && below.Props.IsSolid && !belowIsSameFluid);

            bool falling = IsFalling(currentLevel);
            byte expectedNewLevel = falling && props.WaterfallsMaxSpread ? (byte)1 : (byte)(effectiveLevel + 1);

            if (canSpreadHorizontally && expectedNewLevel < props.FlowLevels)
            {
                for (int i = 0; i < 4; i++)
                {
                    int3 off = HorizontalOffset(i);
                    LocalVoxel nb = GetStateLocal(x + off.x, y + off.y, z + off.z);
                    if (!nb.Has) continue;

                    bool neighborIsAir = nb.Id == BlockIDs.Air;
                    bool neighborIsReplaceable = !nb.Props.IsSolid && (nb.Props.Tags & BlockTags.REPLACEABLE) != 0;
                    bool neighborIsSameFluidAndWorse = nb.Id == id &&
                                                       !IsFalling(nb.Level) &&
                                                       GetEffectiveLevel(nb.Level) > expectedNewLevel;

                    if ((neighborIsAir || neighborIsReplaceable || neighborIsSameFluidAndWorse) && !nb.Props.IsSolid)
                        return true;
                }
            }

            // Reason 4: does its level match its expected supported level?
            if (currentLevel != 0)
            {
                CalculateExpectedFluidLevel(x, y, z, id, props, out byte expectedEffectiveLevel, out bool isFedFromAbove);

                bool isFallingAndCutOff = falling && !isFedFromAbove;
                bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
                byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

                if (isFallingAndCutOff) return true;
                if (expectedEffectiveLevel >= props.FlowLevels) return true;
                if (expectedFluidLevel != currentLevel) return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        //  Shared helpers
        // ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Calculates the expected effective fluid level + whether it is fed from above (mirror of the managed helper).</summary>
        private void CalculateExpectedFluidLevel(int x, int y, int z, ushort fluidId, BlockTypeJobData props,
            out byte expectedEffectiveLevel, out bool isFedFromAbove)
        {
            expectedEffectiveLevel = props.FlowLevels; // default: decays to air
            isFedFromAbove = false;

            // 1. Fed from above?
            LocalVoxel above = GetStateLocal(x, y + 1, z);
            if (above.Has && above.Id == fluidId)
            {
                isFedFromAbove = true;
                expectedEffectiveLevel = GetEffectiveLevel(above.Level);
                if (expectedEffectiveLevel == 0) expectedEffectiveLevel = 1; // a source feeds a falling column of level 1
            }

            if (!isFedFromAbove)
            {
                int adjacentSources = 0;

                for (int i = 0; i < 4; i++)
                {
                    int3 off = HorizontalOffset(i);
                    LocalVoxel nb = GetStateLocal(x + off.x, y + off.y, z + off.z);

                    if (nb.Has && nb.Id == fluidId)
                    {
                        byte neighborEffective;
                        if (IsFalling(nb.Level) && props.WaterfallsMaxSpread)
                        {
                            LocalVoxel nbAbove = GetStateLocal(x + off.x, y + off.y + 1, z + off.z);
                            bool isNeighborFed = nbAbove.Has && nbAbove.Id == fluidId;
                            neighborEffective = isNeighborFed ? (byte)0 : props.FlowLevels;
                        }
                        else
                        {
                            neighborEffective = GetEffectiveLevel(nb.Level);
                            if (neighborEffective == 0) adjacentSources++;
                        }

                        if (neighborEffective < expectedEffectiveLevel)
                            expectedEffectiveLevel = neighborEffective;
                    }
                }

                if (expectedEffectiveLevel < props.FlowLevels)
                    expectedEffectiveLevel++;

                // Infinite source regeneration (Beta 1.3.2): >= 2 adjacent true sources over solid/source.
                if (props.InfiniteSourceRegeneration && adjacentSources >= 2)
                {
                    LocalVoxel below = GetStateLocal(x, y - 1, z);
                    bool belowIsSolid = below.Has && below.Props.IsSolid;
                    bool belowIsSource = below.Has && below.Id == fluidId && below.Level == 0;
                    if (belowIsSolid || belowIsSource)
                        expectedEffectiveLevel = 0;
                }
            }
        }

        /// <summary>Returns a bitmask of optimal horizontal flow directions (toward the nearest drop within 4 blocks).</summary>
        private byte GetOptimalFlowDirections(int x, int y, int z, ushort fluidId,
            NativeQueue<SearchNode> searchQueue, NativeHashSet<int3> searchVisited)
        {
            Span<int> flowCost = stackalloc int[4];
            int minCost = 1000;
            byte validDirectionsMask = 0;

            for (int i = 0; i < 4; i++)
            {
                flowCost[i] = 1000;
                int3 off = HorizontalOffset(i);
                int nx = x + off.x, ny = y + off.y, nz = z + off.z;
                LocalVoxel nb = GetStateLocal(nx, ny, nz);

                if (!nb.Has) continue;

                bool isSolid = nb.Props.IsSolid && nb.Id != fluidId;
                bool isSourceFluid = nb.Id == fluidId && nb.Level == 0;

                if (!isSolid && !isSourceFluid)
                {
                    validDirectionsMask |= (byte)(1 << i);
                    LocalVoxel belowNeighbor = GetStateLocal(nx, ny - 1, nz);
                    bool belowIsSolid = belowNeighbor.Has && belowNeighbor.Props.IsSolid && belowNeighbor.Id != fluidId;

                    if (belowNeighbor.Has && !belowIsSolid)
                        flowCost[i] = 0; // immediate drop
                    else
                        flowCost[i] = CalculateFlowCost(nx, ny, nz, 1, i, fluidId, searchQueue, searchVisited);
                }

                if (flowCost[i] < minCost)
                    minCost = flowCost[i];
            }

            // No drop found in any direction (minCost beyond max BFS depth) → uniform spread over valid dirs.
            if (minCost > FluidTierClassifier.MaxFlowSearchDepth)
                return validDirectionsMask;

            byte optimalMask = 0;
            for (int i = 0; i < 4; i++)
            {
                if (flowCost[i] == minCost)
                    optimalMask |= (byte)(1 << i);
            }

            return optimalMask;
        }

        /// <summary>BFS to the nearest drop (max distance 4), in local coords. Mirror of the managed routine using <see cref="int3"/> keys.</summary>
        private int CalculateFlowCost(int startX, int startY, int startZ, int startCost, int incomingDir, ushort fluidId,
            NativeQueue<SearchNode> searchQueue, NativeHashSet<int3> searchVisited)
        {
            // Reuse the per-Execute scratch (cleared per call) instead of allocating Temp containers each call.
            searchQueue.Clear();
            searchVisited.Clear();

            int3 startPos = new int3(startX, startY, startZ);
            searchQueue.Enqueue(new SearchNode { Pos = startPos, Cost = startCost });
            searchVisited.Add(startPos);

            int minCost = 1000;

            while (searchQueue.TryDequeue(out SearchNode node))
            {
                for (int i = 0; i < 4; i++)
                {
                    // Skip going backwards immediately on the first step (opposites: 0/1, 2/3).
                    if (node.Cost == startCost)
                    {
                        if ((incomingDir == 0 && i == 1) || (incomingDir == 1 && i == 0) ||
                            (incomingDir == 2 && i == 3) || (incomingDir == 3 && i == 2))
                        {
                            continue;
                        }
                    }

                    int3 off = HorizontalOffset(i);
                    int3 neighborPos = node.Pos + off;

                    if (searchVisited.Contains(neighborPos)) continue;
                    searchVisited.Add(neighborPos);

                    LocalVoxel nb = GetStateLocal(neighborPos.x, neighborPos.y, neighborPos.z);
                    if (!nb.Has) continue;

                    bool isSolid = nb.Props.IsSolid && nb.Id != fluidId;
                    bool isSourceFluid = nb.Id == fluidId && nb.Level == 0;
                    if (isSolid || isSourceFluid) continue;

                    LocalVoxel belowNeighbor = GetStateLocal(neighborPos.x, neighborPos.y - 1, neighborPos.z);
                    bool belowIsSolid = belowNeighbor.Has && belowNeighbor.Props.IsSolid && belowNeighbor.Id != fluidId;

                    if (belowNeighbor.Has && !belowIsSolid)
                    {
                        minCost = node.Cost + 1;
                        return minCost;
                    }

                    if (node.Cost + 1 < FluidTierClassifier.MaxFlowSearchDepth)
                        searchQueue.Enqueue(new SearchNode { Pos = neighborPos, Cost = node.Cost + 1 });
                }
            }

            return minCost;
        }

        /// <summary>Reads the voxel at a local position from the snapshot. <see cref="LocalVoxel.Has"/> is false for out-of-chunk coords.</summary>
        private LocalVoxel GetStateLocal(int x, int y, int z)
        {
            if (x < 0 || x >= VoxelData.ChunkWidth ||
                z < 0 || z >= VoxelData.ChunkWidth ||
                y < 0 || y >= VoxelData.ChunkHeight)
            {
                return default; // Has == false
            }

            int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
            uint p = VoxelMap[idx];
            ushort id = BurstVoxelDataBitMapping.GetId(p);

            LocalVoxel v;
            v.Has = true;
            v.Id = id;
            v.Level = BurstVoxelDataBitMapping.GetFluidLevel(p);
            v.Props = id < BlockTypes.Length ? BlockTypes[id] : default;
            return v;
        }

        /// <summary>Appends a <see cref="VoxelMod"/> for a local position, translating to its global position.</summary>
        private void Emit(int localX, int localY, int localZ, ushort id, byte meta, bool immediate)
        {
            VoxelMod mod = new VoxelMod(new int3(localX + ChunkOrigin.x, localY, localZ + ChunkOrigin.y), id)
            {
                Meta = meta,
                ImmediateUpdate = immediate,
            };
            Mods.Add(mod);
        }

        /// <summary>Builds a per-voxel / per-tick <see cref="Random"/> (mirror of <c>BlockBehavior.SeededVoxelRandom</c>).</summary>
        private Random SeededVoxelRandom(int3 globalPos)
        {
            uint seed = math.max(1u, math.hash(globalPos) ^ (uint)(TickCounter * TICK_SALT_HASH_MULTIPLIER));
            return new Random(seed);
        }

        /// <summary>Local horizontal direction offset for index i (0=+z, 1=-z, 2=+x, 3=-x) — shared with the managed path via <see cref="FluidTierClassifier.HorizontalNeighborOffset"/>.</summary>
        private static int3 HorizontalOffset(int i) => FluidTierClassifier.HorizontalNeighborOffset(i);

        // Falling-flag encoding shared with the managed path (source of truth: BurstVoxelDataBitMapping).
        private static bool IsFalling(byte fluidLevel) => BurstVoxelDataBitMapping.IsFluidFalling(fluidLevel);
        private static byte GetEffectiveLevel(byte fluidLevel) => BurstVoxelDataBitMapping.GetEffectiveFluidLevel(fluidLevel);
        private static byte MakeFalling(byte effectiveLevel) => BurstVoxelDataBitMapping.MakeFluidFalling(effectiveLevel);

        /// <summary>A BFS search frontier node (local coords).</summary>
        private struct SearchNode
        {
            public int3 Pos;
            public int Cost;
        }

        /// <summary>A decoded in-chunk voxel read (the Burst analogue of the managed <c>VoxelState?</c>).</summary>
        private struct LocalVoxel
        {
            [MarshalAs(UnmanagedType.U1)]
            public bool Has;

            public ushort Id;
            public byte Level;
            public BlockTypeJobData Props;
        }
    }
}
