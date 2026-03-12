using Data;
using UnityEngine;

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
        bool isSupportedBelow = belowState.HasValue && (belowState.Value.Properties.isSolid || (belowIsSameFluid && !IsFalling(belowState.Value.FluidLevel)));

        // 2. Step 1: Calculate Expected Level (Decay / Drainage)
        // Returns true if the block died (drained completely) or changed its own level this tick.
        if (HandleFluidDecay(chunkData, localPos, globalPos, currentId, ref currentLevel, props.flowLevels))
        {
            return;
        }

        byte effectiveLevel = GetEffectiveLevel(currentLevel);
        bool falling = IsFalling(currentLevel);

        // 3. Step 2 & 3: Gravity and Settling
        // Returns true if gravity acted or settling occurred, meaning we shouldn't spread horizontally this tick
        if (HandleFluidVertical(globalPos, currentId, effectiveLevel, falling, canFlowDown, isSupportedBelow))
        {
            return;
        }

        // 4. Step 4: Horizontal Spreading
        HandleFluidSpread(chunkData, localPos, globalPos, currentId, currentLevel, effectiveLevel, props.flowLevels, belowState, isSupportedBelow);
    }

    /// <summary>
    /// Checks if the fluid needs to drain (decay) based on its neighbors.
    /// Returns true if a VoxelMod was added for this position, meaning further processing should stop.
    /// </summary>
    private static bool HandleFluidDecay(ChunkData chunkData, Vector3Int localPos, Vector3Int globalPos, ushort currentId, ref byte currentLevel, byte flowLevels)
    {
        if (currentLevel == 0) return false; // Source blocks never decay

        CalculateExpectedFluidLevel(chunkData, localPos, currentId, flowLevels, out byte expectedEffectiveLevel, out bool isFedFromAbove);

        bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
        byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

        if (expectedEffectiveLevel >= flowLevels)
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
    /// Handles fluid falling downward and settling when it hits a floor.
    /// Returns true if an action was taken, delaying horizontal spread to the next tick.
    /// </summary>
    private static bool HandleFluidVertical(Vector3Int globalPos, ushort currentId, byte effectiveLevel, bool falling, bool canFlowDown, bool isSupportedBelow)
    {
        // Gravity (Vertical Flow)
        if (canFlowDown)
        {
            Vector3Int globalBelowPos = new Vector3Int(globalPos.x, globalPos.y - 1, globalPos.z);
            Mods.Add(new VoxelMod(globalBelowPos, currentId)
            {
                FluidLevel = MakeFalling(effectiveLevel),
            });
            return true; // Skip horizontal spreading this tick if we pushed downwards
        }

        // Settle Falling Blocks
        if (falling)
        {
            if (isSupportedBelow)
            {
                // We've hit a floor, settle into horizontal form and prepare to spread next tick.
                Mods.Add(new VoxelMod(globalPos, currentId) { FluidLevel = effectiveLevel, ImmediateUpdate = true });
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles horizontal spreading of fluid across supported surfaces.
    /// </summary>
    private static void HandleFluidSpread(ChunkData chunkData, Vector3Int localPos, Vector3Int globalPos, ushort currentId, byte currentLevel, byte effectiveLevel, byte flowLevels, VoxelState? belowState, bool isSupportedBelow)
    {
        LogWaterDebug($"[WaterDebug FLOW] Step 4 REACHED: pos={globalPos} id={currentId} level={currentLevel} " +
                      $"below={(belowState.HasValue ? belowState.Value.id.ToString() : "none")}(solid? {(belowState.HasValue ? belowState.Value.Properties.isSolid.ToString() : "N/A")}, " +
                      $"falling? {(belowState.HasValue ? IsFalling(belowState.Value.FluidLevel).ToString() : "N/A")}, " +
                      $"level={(belowState.HasValue ? belowState.Value.FluidLevel.ToString() : "N/A")}) " +
                      $"isSupported: {isSupportedBelow}");

        if (!isSupportedBelow)
        {
            LogWaterDebug($"[WaterDebug FLOW] {globalPos} Not supported below. Returning.");
            return;
        }

        byte newLevel = (byte)(effectiveLevel + 1);
        if (newLevel >= flowLevels) return;

        for (int i = 0; i < 4; i++)
        {
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

        // Reason 2: Can it settle? (Falling block hitting a solid surface)
        if (IsFalling(currentLevel) && (!belowState.HasValue || belowState.Value.Properties.isSolid || belowIsSameFluid))
        {
            return true;
        }

        // Reason 3: Can it flow horizontally?
        if (effectiveLevel + 1 < props.flowLevels)
        {
            for (int i = 0; i < 4; i++) // 4 cardinal horizontal directions
            {
                Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                VoxelState? neighborState = chunkData.GetState(neighborPos);

                if (!neighborState.HasValue) continue;

                bool neighborIsAir = neighborState.Value.id == BlockIDs.Air;
                bool neighborIsSameFluidAndWorse = neighborState.Value.id == id &&
                                                   GetEffectiveLevel(neighborState.Value.FluidLevel) > effectiveLevel + 1;

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
            CalculateExpectedFluidLevel(chunkData, localPos, id, props.flowLevels,
                out byte expectedEffectiveLevel, out bool isFedFromAbove);

            bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
            byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

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
        ChunkData chunkData, Vector3Int localPos, ushort fluidId, byte flowLevels,
        out byte expectedEffectiveLevel, out bool isFedFromAbove)
    {
        expectedEffectiveLevel = flowLevels; // Default to max (decays to air)
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
            // 2. Check horizontal neighbors for the lowest effective level (closest to source)
            for (int i = 0; i < 4; i++)
            {
                Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                VoxelState? neighborState = chunkData.GetState(neighborPos);

                if (neighborState.HasValue && neighborState.Value.id == fluidId)
                {
                    byte neighborEffective = GetEffectiveLevel(neighborState.Value.FluidLevel);
                    if (neighborEffective < expectedEffectiveLevel)
                    {
                        expectedEffectiveLevel = neighborEffective;
                    }
                }
            }

            // Horizontal spread adds 1 to the effective level.
            if (expectedEffectiveLevel < flowLevels)
            {
                expectedEffectiveLevel++;
            }
        }
    }

    #endregion
}
