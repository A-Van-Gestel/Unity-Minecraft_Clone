using System;
using Data;
using Unity.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

public static partial class BlockBehavior
{
    #region Fluid Behavior Methods

    // --- Falling Flag Encoding ---
    // Minecraft Beta 1.3.2 uses metadata >= 8 for falling fluid.
    // Lower 3 bits (0-7) carry the "effective level" of the upstream block.
    // FluidLevel 0 = source, 1-7 = horizontal flow, 8 = falling from source, 9-15 = falling from flow.
    private const byte FALLING_FLAG = 8;

    /// <summary>Returns true if the fluid level encodes a vertically falling block (level >= 8).</summary>
    private static bool IsFalling(byte fluidLevel) => fluidLevel >= FALLING_FLAG;

    /// <summary>Strips the falling flag, returning the horizontal level (0-7).</summary>
    private static byte GetEffectiveLevel(byte fluidLevel) => (byte)(fluidLevel & 0x7);

    /// <summary>Creates a falling metadata value from a horizontal level.</summary>
    private static byte MakeFalling(byte effectiveLevel) => (byte)(effectiveLevel | FALLING_FLAG);

    /// <summary>
    /// Manages the flow logic for a single fluid voxel.
    /// Follows Minecraft Beta 1.3.2 tick order: Update Level (Drain) → Gravitate → Spread Horizontal.
    /// </summary>
    private static void HandleFluidFlow(ChunkData chunkData, Vector3Int localPos, VoxelState fluidState)
    {
        // 1. Setup shared state
        ushort currentId = fluidState.id;
        byte currentLevel = fluidState.FluidLevel;
        BlockType props = fluidState.Properties;

        Vector3Int globalPos = new Vector3Int(localPos.x + chunkData.position.x, localPos.y, localPos.z + chunkData.position.y);

        VoxelState? belowState = chunkData.GetState(localPos + Vector3Int.down);
        bool belowIsSameFluid = belowState.HasValue && belowState.Value.id == currentId;
        bool canFlowDown = belowState.HasValue && !belowState.Value.Properties.isSolid && !belowIsSameFluid;

        // 2. Step 1: Calculate Expected Level (Decay / Drainage)
        // Returns true if the block died (drained completely) or changed its own level this tick.
        if (HandleFluidDecay(chunkData, localPos, globalPos, currentId, ref currentLevel, props))
        {
            return;
        }

        byte effectiveLevel = GetEffectiveLevel(currentLevel);
        bool falling = IsFalling(currentLevel);

        // 3. Step 2 & 3: Gravity and Settling
        // Returns true if gravity acted (skipping horizontal spread)
        if (HandleFluidVertical(globalPos, currentId, effectiveLevel, canFlowDown))
        {
            return;
        }

        // 4. Step 4: Horizontal Spreading
        HandleFluidSpread(chunkData, localPos, globalPos, currentId, currentLevel, effectiveLevel, props, belowState, belowIsSameFluid, falling);
    }

    /// <summary>
    /// Checks if the fluid needs to drain (decay) based on its neighbors.
    /// Returns true if a VoxelMod was added for this position, meaning further processing should stop.
    /// </summary>
    private static bool HandleFluidDecay(ChunkData chunkData, Vector3Int localPos, Vector3Int globalPos, ushort currentId, ref byte currentLevel, BlockType props)
    {
        if (currentLevel == 0) return false; // Source blocks never decay

        CalculateExpectedFluidLevel(chunkData, localPos, currentId, props, out byte expectedEffectiveLevel, out bool isFedFromAbove);

        bool falling = IsFalling(currentLevel);
        bool isFallingAndCutOff = falling && !isFedFromAbove;

        bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
        byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

        if (expectedEffectiveLevel >= props.flowLevels || isFallingAndCutOff)
        {
            Mods.Add(new VoxelMod(globalPos, 0) { ImmediateUpdate = true });
            return true;
        }

        if (expectedFluidLevel != currentLevel)
        {
            // Update our level to match our support
            Mods.Add(new VoxelMod(globalPos, currentId) { FluidLevel = expectedFluidLevel, ImmediateUpdate = true });
            currentLevel = expectedFluidLevel; // Update by ref for the orchestrator
            return false; // Still process gravity/spread this tick, but with updated level (caught next tick mostly)
        }

        return false;
    }

    /// <summary>
    /// Handles fluid falling downward.
    /// Returns true if gravity acted, bypassing horizontal spread to the next tick.
    /// </summary>
    private static bool HandleFluidVertical(Vector3Int globalPos, ushort currentId, byte effectiveLevel, bool canFlowDown)
    {
        // If we can't flow down, do nothing
        if (!canFlowDown) return false;

        // Gravity (Vertical Flow)
        Vector3Int globalBelowPos = new Vector3Int(globalPos.x, globalPos.y - 1, globalPos.z);
        Mods.Add(new VoxelMod(globalBelowPos, currentId)
        {
            FluidLevel = MakeFalling(effectiveLevel),
        });
        return true; // Skip horizontal spreading this tick if we pushed downwards
    }

    /// <summary>
    /// Handles horizontal spreading of fluid across supported surfaces.
    /// Minecraft rule: source blocks always spread;
    /// non-source blocks only spread if the block directly below blocks flow
    /// (is solid and NOT the same fluid type).
    /// </summary>
    private static void HandleFluidSpread(ChunkData chunkData, Vector3Int localPos, Vector3Int globalPos, ushort currentId, byte currentLevel, byte effectiveLevel, BlockType props, VoxelState? belowState, bool belowIsSameFluid, bool falling)
    {
        // Minecraft gate:
        // Source blocks (effectiveLevel == 0) always spread horizontally.
        // Non-source blocks only spread if the block below is solid AND not the same fluid.
        // This prevents mid-air horizontal grids when fluid overhangs an edge.
        bool canSpreadHorizontally = effectiveLevel == 0 ||
                                     (belowState.HasValue && belowState.Value.Properties.isSolid && !belowIsSameFluid);

        LogWaterDebug($"[WaterDebug FLOW] Step 4 REACHED: pos={globalPos} id={currentId} level={currentLevel} " +
                      $"canSpread={canSpreadHorizontally} effectiveLevel={effectiveLevel} " +
                      $"belowSolid={belowState.HasValue && belowState.Value.Properties.isSolid} " +
                      $"belowIsSameFluid={belowIsSameFluid}");

        if (!canSpreadHorizontally)
        {
            LogWaterDebug($"[WaterDebug FLOW] {globalPos} Cannot spread horizontally. Returning.");
            return;
        }

        // Minecraft waterfall spread reset: if a falling block hits the ground, it optionally resets to max spread (level 1)
        byte newLevel = falling && props.waterfallsMaxSpread ? (byte)1 : (byte)(effectiveLevel + 1);
        if (newLevel >= props.flowLevels) return;

        // Lava Viscosity Randomization (Bug 08)
        // If a fluid has a spread chance less than 1.0, it will randomly skip horizontal spreading ticks.
        // E.g., Lava at 0.25 only flows 25% of the time, resulting in thick, blob-like staggering.
        if (Random.value > props.spreadChance)
        {
            LogWaterDebug($"[WaterDebug FLOW] {globalPos} Random Viscosity Skip (Chance={props.spreadChance}).");
            return;
        }

        // Pathfind for the optimal flow direction (drops within 4 blocks)
        byte optimalFlowMask = GetOptimalFlowDirections(chunkData, localPos, currentId);

        for (int i = 0; i < 4; i++)
        {
            // If this direction is not in the optimal flow mask, skip it to prevent spreading away from drops
            if ((optimalFlowMask & (1 << i)) == 0) continue;

            Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
            VoxelState? neighborState = chunkData.GetState(neighborPos);

            if (!neighborState.HasValue) continue;

            // Flow into air or same fluid with worse level
            bool neighborIsAir = neighborState.Value.id == BlockIDs.Air;
            bool neighborIsSameFluidAndWorse = neighborState.Value.id == currentId &&
                                               GetEffectiveLevel(neighborState.Value.FluidLevel) > newLevel;

            if (neighborIsAir || neighborIsSameFluidAndWorse)
            {
                if (neighborState.Value.Properties.isSolid) continue;

                Vector3Int globalNeighborPos = new Vector3Int(
                    neighborPos.x + chunkData.position.x, neighborPos.y,
                    neighborPos.z + chunkData.position.y);

                LogWaterDebug($"[WaterDebug FLOW] {globalPos} SPREADING HORIZONTALLY to {globalNeighborPos} with level {newLevel}");

                Mods.Add(new VoxelMod(globalNeighborPos, currentId)
                {
                    FluidLevel = newLevel,
                });
            }
        }
    }

    // --- HELPER METHODS ---

    /// <summary>
    /// Evaluates whether a fluid block currently needs to be ticked by the chunk updater.
    /// Returns true if the block is unstable (can flow down, settle, spread, or drain).
    /// </summary>
    private static bool IsFluidActive(ChunkData chunkData, Vector3Int localPos, VoxelState voxel, BlockType props, ushort id)
    {
        byte currentLevel = voxel.FluidLevel;
        byte effectiveLevel = GetEffectiveLevel(currentLevel);

        LogWaterDebug($"[WaterDebug ACTIVE] Eval pos={localPos} level={currentLevel}");

        // Reason 1: Can it flow down? (Gravity)
        VoxelState? belowState = chunkData.GetState(localPos + Vector3Int.down);
        bool belowIsSameFluid = belowState.HasValue && belowState.Value.id == id;
        if (belowState.HasValue && !belowState.Value.Properties.isSolid && !belowIsSameFluid)
        {
            return true;
        }

        // Reason 2: Can it settle?
        // Removed: falling blocks hitting solid ground no longer settle, they stay falling and spread horizontally instead (matching Minecraft).

        // Reason 3: Can it flow horizontally?
        // Source blocks always, non-source only on solid non-fluid ground.
        bool canSpreadHorizontally = effectiveLevel == 0 ||
                                     (belowState.HasValue && belowState.Value.Properties.isSolid && !belowIsSameFluid);

        bool falling = IsFalling(currentLevel);
        byte expectedNewLevel = falling && props.waterfallsMaxSpread ? (byte)1 : (byte)(effectiveLevel + 1);

        if (canSpreadHorizontally && expectedNewLevel < props.flowLevels)
        {
            for (int i = 0; i < 4; i++) // 4 cardinal horizontal directions
            {
                Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                VoxelState? neighborState = chunkData.GetState(neighborPos);

                if (!neighborState.HasValue) continue;

                bool neighborIsAir = neighborState.Value.id == BlockIDs.Air;
                bool neighborIsSameFluidAndWorse = neighborState.Value.id == id &&
                                                   GetEffectiveLevel(neighborState.Value.FluidLevel) > expectedNewLevel;

                if ((neighborIsAir || neighborIsSameFluidAndWorse) && !neighborState.Value.Properties.isSolid)
                {
                    return true;
                }
            }
        }

        // Reason 4: Does its current level match its expected supported level? (Drainage / Source distance)
        // Source blocks (0) are conceptually stable internally and won't decay
        if (currentLevel != 0)
        {
            // If it's a falling block that is NO LONGER fed from above, it MUST decay and disappear (or become a declining horizontal flow)
            // It loses its "source-like" falling status.
            CalculateExpectedFluidLevel(chunkData, localPos, id, props,
                out byte expectedEffectiveLevel, out bool isFedFromAbove);

            bool isFallingAndCutOff = falling && !isFedFromAbove;

            bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
            byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

            if (isFallingAndCutOff) return true; // Needs to decay because the source above was broken
            if (expectedEffectiveLevel >= props.flowLevels) return true; // Needs to decay to air completely
            if (expectedFluidLevel != currentLevel) return true; // Needs to update its flowing level state
        }

        // If no activation conditions are met, the block is stable and does not need to be ticked this cycle.
        LogWaterDebug($"[WaterDebug ACTIVE] pos={localPos} Returning FALSE");
        return false;
    }

    /// <summary>
    /// Helper to conditionally log fluid diagnostics if the setting is enabled.
    /// </summary>
    private static void LogWaterDebug(string message)
    {
        if (World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs)
        {
            Debug.Log(message);
        }
    }

    /// <summary>
    /// Calculates the expected effective fluid level based on the environment.
    /// Used by both Active() and HandleFluidFlow() to determine if a block needs to drain or decay.
    /// </summary>
    private static void CalculateExpectedFluidLevel(
        ChunkData chunkData, Vector3Int localPos, ushort fluidId, BlockType props,
        out byte expectedEffectiveLevel, out bool isFedFromAbove)
    {
        expectedEffectiveLevel = props.flowLevels; // Default to max (decays to air)
        isFedFromAbove = false;

        // 1. Check if fed from above
        VoxelState? aboveState = chunkData.GetState(localPos + Vector3Int.up);
        if (aboveState.HasValue && aboveState.Value.id == fluidId)
        {
            isFedFromAbove = true;
            expectedEffectiveLevel = GetEffectiveLevel(aboveState.Value.FluidLevel);
            // Important: Falling fluid cannot be a source block (0). A source feeds a falling column of effective level 1.
            if (expectedEffectiveLevel == 0) expectedEffectiveLevel = 1;
        }

        if (!isFedFromAbove)
        {
            int adjacentSources = 0;

            // 2. Check horizontal neighbors for the lowest effective level (closest to source)
            for (int i = 0; i < 4; i++)
            {
                Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                VoxelState? neighborState = chunkData.GetState(neighborPos);

                if (neighborState.HasValue && neighborState.Value.id == fluidId)
                {
                    byte neighborEffective;
                    if (IsFalling(neighborState.Value.FluidLevel) && props.waterfallsMaxSpread)
                    {
                        // Minecraft rule: Falling blocks act like source blocks (level 0) for horizontal spreading.
                        // CRITICAL FIX: To prevent infinite decay loops when a waterfall is broken,
                        // a falling block can ONLY act as a horizontal source if it itself is currently fed from above.
                        VoxelState? neighborAbove = chunkData.GetState(neighborPos + Vector3Int.up);
                        bool isNeighborFed = neighborAbove.HasValue && neighborAbove.Value.id == fluidId;

                        // If it's a falling block but its source was severed, it provides no support.
                        neighborEffective = isNeighborFed ? (byte)0 : props.flowLevels;
                    }
                    else
                    {
                        // Conserve volume logic
                        neighborEffective = GetEffectiveLevel(neighborState.Value.FluidLevel);

                        // Infinite Water Mechanic: Count adjacent true source blocks (level 0, not falling).
                        if (neighborEffective == 0)
                        {
                            adjacentSources++;
                        }
                    }

                    if (neighborEffective < expectedEffectiveLevel)
                    {
                        expectedEffectiveLevel = neighborEffective;
                    }
                }
            }

            // Horizontal spread adds 1 to the effective level.
            if (expectedEffectiveLevel < props.flowLevels)
            {
                expectedEffectiveLevel++;
            }

            // 3. Infinite Source Regeneration (Minecraft Beta 1.3.2 rule)
            // If we have >= 2 adjacent true sources, and the block below us is solid or identical source fluid...
            if (props.infiniteSourceRegeneration && adjacentSources >= 2)
            {
                VoxelState? belowState = chunkData.GetState(localPos + Vector3Int.down);
                bool belowIsSolid = belowState.HasValue && belowState.Value.Properties.isSolid;
                bool belowIsSource = belowState.HasValue && belowState.Value.id == fluidId && belowState.Value.FluidLevel == 0;

                if (belowIsSolid || belowIsSource)
                {
                    expectedEffectiveLevel = 0;
                }
            }
        }
    }

    private struct SearchNode
    {
        public Vector3Int Pos;
        public int Cost;
    }

    /// <summary>
    /// Calculates the length of the path to the nearest drop. Max distance is 4.
    /// Uses NativeQueue/NativeHashSet from Unity.Collections to guarantee zero GC allocations on the main thread.
    /// </summary>
    private static int CalculateFlowCost(ChunkData chunkData, Vector3Int startPos, int startCost, int incomingDir, ushort fluidId)
    {
        // Use Temp allocator to avoid GC drops
        NativeQueue<SearchNode> queue = new NativeQueue<SearchNode>(Allocator.Temp);
        NativeHashSet<Vector3Int> visited = new NativeHashSet<Vector3Int>(64, Allocator.Temp);

        queue.Enqueue(new SearchNode { Pos = startPos, Cost = startCost });
        visited.Add(startPos);

        int minCost = 1000;

        while (queue.TryDequeue(out SearchNode node))
        {
            for (int i = 0; i < 4; i++)
            {
                // Skip going backwards immediately on the first step
                // Horizontal indices: 0=+z, 1=-z, 2=+x, 3=-x
                // Opposites: 0 and 1, 2 and 3
                if (node.Cost == startCost)
                {
                    if ((incomingDir == 0 && i == 1) || (incomingDir == 1 && i == 0) ||
                        (incomingDir == 2 && i == 3) || (incomingDir == 3 && i == 2))
                    {
                        continue;
                    }
                }

                Vector3Int neighborPos = node.Pos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];

                if (visited.Contains(neighborPos)) continue;
                visited.Add(neighborPos);

                VoxelState? neighborState = chunkData.GetState(neighborPos);
                if (!neighborState.HasValue) continue;

                // Stop if we hit a solid block or a source block of the same fluid
                bool isSolid = neighborState.Value.Properties.isSolid && neighborState.Value.id != fluidId;
                bool isSourceFluid = neighborState.Value.id == fluidId && neighborState.Value.FluidLevel == 0;

                if (isSolid || isSourceFluid) continue;

                // Check for a drop
                VoxelState? belowNeighbor = chunkData.GetState(neighborPos + Vector3Int.down);
                bool belowIsSolid = belowNeighbor.HasValue && belowNeighbor.Value.Properties.isSolid && belowNeighbor.Value.id != fluidId;

                if (belowNeighbor.HasValue && !belowIsSolid)
                {
                    // Found a drop! Since it's BFS, this is guaranteed to be the shortest path length.
                    minCost = node.Cost + 1;
                    queue.Dispose();
                    visited.Dispose();
                    return minCost;
                }

                // If no drop and we haven't reached max depth (4), explore further
                if (node.Cost + 1 < 4)
                {
                    queue.Enqueue(new SearchNode { Pos = neighborPos, Cost = node.Cost + 1 });
                }
            }
        }

        queue.Dispose();
        visited.Dispose();
        return minCost;
    }

    /// <summary>
    /// Returns a bitmask where each bit relates to an optimal flow direction (0: N, 1: E, 2: S, 3: W).
    /// If all directions are equally bad or equally good, returns 0b1111 (all directions).
    /// </summary>
    private static byte GetOptimalFlowDirections(ChunkData chunkData, Vector3Int centerPos, ushort fluidId)
    {
        int[] flowCost = new int[4];
        int minCost = 1000;
        byte validDirectionsMask = 0;

        for (int i = 0; i < 4; i++)
        {
            flowCost[i] = 1000;
            Vector3Int neighborPos = centerPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
            VoxelState? neighborState = chunkData.GetState(neighborPos);

            if (!neighborState.HasValue) continue;

            bool isSolid = neighborState.Value.Properties.isSolid && neighborState.Value.id != fluidId;
            bool isSourceFluid = neighborState.Value.id == fluidId && neighborState.Value.FluidLevel == 0;

            if (!isSolid && !isSourceFluid)
            {
                validDirectionsMask |= (byte)(1 << i);
                VoxelState? belowNeighbor = chunkData.GetState(neighborPos + Vector3Int.down);
                bool belowIsSolid = belowNeighbor.HasValue && belowNeighbor.Value.Properties.isSolid && belowNeighbor.Value.id != fluidId;

                // If the block below is not a solid boundary, it's an immediate drop
                if (belowNeighbor.HasValue && !belowIsSolid)
                {
                    flowCost[i] = 0;
                }
                else
                {
                    flowCost[i] = CalculateFlowCost(chunkData, neighborPos, 1, i, fluidId);
                }
            }

            if (flowCost[i] < minCost)
            {
                minCost = flowCost[i];
            }
        }

        // GetOptimalFlowDirections needs to accurately collect ALL minimum paths.
        // If minCost is > 4 (the max BFS depth), that means NO drops were found in ANY direction.
        // In that case, we MUST return all valid flowing directions minus solid walls, to create the spreading diamond.

        if (minCost > 4)
        {
            // No optimal path found, fallback to uniform spread.
            LogWaterDebug($"[WaterDebug PATHFINDING] {centerPos} NO OPTIMAL DROPS. Falling back to mask={Convert.ToString(validDirectionsMask, 2).PadLeft(4, '0')}");
            return validDirectionsMask;
        }

        byte optimalMask = 0;
        string debugStr = "";
        for (int i = 0; i < 4; i++)
        {
            if (flowCost[i] == minCost)
            {
                optimalMask |= (byte)(1 << i);
                debugStr += $"{i}:{flowCost[i]} ";
            }
            else
            {
                debugStr += $"({i}:{flowCost[i]}) ";
            }
        }

        LogWaterDebug($"[WaterDebug PATHFINDING] {centerPos} minCost={minCost} mask={Convert.ToString(optimalMask, 2).PadLeft(4, '0')} dirs={debugStr}");
        return optimalMask;
    }

    #endregion
}
