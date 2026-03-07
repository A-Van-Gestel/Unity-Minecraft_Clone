using System.Collections.Generic;
using Data;
using JetBrains.Annotations;
using UnityEngine;

/// <summary>
/// Contains the static logic for all special block behaviors in the world,
/// such as grass spreading and fluid simulation.
/// </summary>
public static class BlockBehavior
{
    // A reusable list to avoid allocating new memory every time Behave is called.
    private static readonly List<VoxelMod> s_mods = new List<VoxelMod>();

    // OPTIMIZATION: Cached spread vectors to prevent array allocations every tick
    private static readonly Vector3Int[] s_grassSpreadVectors =
    {
        // Adjacent
        VoxelData.FaceChecks[0],
        VoxelData.FaceChecks[1],
        VoxelData.FaceChecks[4],
        VoxelData.FaceChecks[5],
        // Above Adjacent
        VoxelData.FaceChecks[0] + VoxelData.FaceChecks[2],
        VoxelData.FaceChecks[1] + VoxelData.FaceChecks[2],
        VoxelData.FaceChecks[4] + VoxelData.FaceChecks[2],
        VoxelData.FaceChecks[5] + VoxelData.FaceChecks[2],
    };

    private static readonly Vector3Int[] s_grassAirCheckVectors =
    {
        VoxelData.FaceChecks[0],
        VoxelData.FaceChecks[1],
        VoxelData.FaceChecks[4],
        VoxelData.FaceChecks[5],
    };

    // --- Public Methods ---

    #region Public Methods

    /// <summary>
    /// Checks if a voxel at a given position in a chunk should be "active" and processed on each tick.
    /// This method acts as a performance gatekeeper, ensuring that only blocks that can potentially
    /// change their state are added to the active update loop.
    /// </summary>
    /// <param name="chunkData">The data grid containing the voxel.</param>
    /// <param name="localPos">The local position of the voxel within the chunk.</param>
    /// <returns>True if the voxel needs to be ticked; otherwise, false.</returns>
    public static bool Active(ChunkData chunkData, Vector3Int localPos)
    {
        // Get the voxel's ID. If the position is invalid, this will throw an error,
        // which is intended as this should only be called for valid, active voxels.
        VoxelState? voxelNullable = chunkData.VoxelFromV3Int(localPos);

        // If the voxel is null (eg: outside the world), it is not active.
        if (!voxelNullable.HasValue)
        {
            return false;
        }

        // Convert voxel to non-nullable
        VoxelState voxel = voxelNullable.Value;

        // Get the voxel's properties & ID
        BlockType props = voxel.Properties;
        ushort id = voxel.id;

        // --- Grass Block ---
        if (id == 2) // Grass Block
        {
            // A grass block is active if there is an adjacent dirt block that it can spread to.
            // We must check all possible spread locations.
            // Check adjacent (cardinal directions)
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[0])) return true; // Back
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[1])) return true; // Front
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[4])) return true; // Left
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[5])) return true; // Right

            // Check one block above adjacent
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[0] + VoxelData.FaceChecks[2])) return true; // Back, Up
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[1] + VoxelData.FaceChecks[2])) return true; // Front, Up
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[4] + VoxelData.FaceChecks[2])) return true; // Left, Up
            if (IsConvertibleDirt(chunkData, localPos + VoxelData.FaceChecks[5] + VoxelData.FaceChecks[2])) return true; // Right, Up

            // Check one block below adjacent (for spreading "down" onto dirt next to an air block)
            if (IsDirtNextToAir(chunkData, localPos + VoxelData.FaceChecks[0])) return true; // Back
            if (IsDirtNextToAir(chunkData, localPos + VoxelData.FaceChecks[1])) return true; // Front
            if (IsDirtNextToAir(chunkData, localPos + VoxelData.FaceChecks[4])) return true; // Left
            if (IsDirtNextToAir(chunkData, localPos + VoxelData.FaceChecks[5])) return true; // Right
        }

        // --- Generic Fluid Activation Logic ---
        if (props.fluidType != FluidType.None)
        {
            byte currentLevel = voxel.FluidLevel;
            byte effectiveLevel = GetEffectiveLevel(currentLevel);
            
            // LOG ACTIVATION
            if (World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs)
                Debug.Log($"[WaterDebug ACTIVE] Eval pos={localPos} level={currentLevel}");

            // Reason 1: Can it flow down?
            VoxelState? belowState = chunkData.GetState(localPos + Vector3Int.down);
            bool belowIsSameFluid = belowState.HasValue && belowState.Value.id == id;
            if (belowState.HasValue && !belowState.Value.Properties.isSolid && !belowIsSameFluid)
            {
                return true; // Must be active to fall.
            }

            // Reason 2: Can it settle? (Falling block hitting solid)
            if (IsFalling(currentLevel) && (!belowState.HasValue || belowState.Value.Properties.isSolid || belowIsSameFluid))
            {
                return true; // Needs to settle.
            }

            // Reason 3: Can it flow horizontally?
            if (effectiveLevel + 1 < props.flowLevels)
            {
                for (int i = 0; i < 4; i++)
                {
                    Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                    VoxelState? neighborState = chunkData.GetState(neighborPos);

                    if (!neighborState.HasValue) continue;

                    bool neighborIsAir = neighborState.Value.id == 0;
                    bool neighborIsSameFluidAndWorse = neighborState.Value.id == id &&
                                                       GetEffectiveLevel(neighborState.Value.FluidLevel) > effectiveLevel + 1;

                    if ((neighborIsAir || neighborIsSameFluidAndWorse) && !neighborState.Value.Properties.isSolid)
                    {
                        return true;
                    }
                }
            }

            // Reason 4: Does its current level match its expected supported level? (Drain check / better source check)
            if (currentLevel != 0) // Source blocks are conceptually stable internally
            {
                byte expectedEffectiveLevel = props.flowLevels;
                bool isFedFromAbove = false;

                VoxelState? aboveState = chunkData.GetState(localPos + Vector3Int.up);
                if (aboveState.HasValue && aboveState.Value.id == id)
                {
                    isFedFromAbove = true;
                    expectedEffectiveLevel = GetEffectiveLevel(aboveState.Value.FluidLevel);
                    if (expectedEffectiveLevel == 0) expectedEffectiveLevel = 1; // Falling from source
                }
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                        VoxelState? neighborState = chunkData.GetState(neighborPos);

                        if (neighborState.HasValue && neighborState.Value.id == id)
                        {
                            byte neighborEffective = GetEffectiveLevel(neighborState.Value.FluidLevel);
                            if (neighborEffective < expectedEffectiveLevel)
                            {
                                expectedEffectiveLevel = neighborEffective;
                            }
                        }
                    }
                    if (expectedEffectiveLevel < props.flowLevels) expectedEffectiveLevel++;
                }

                bool expectedFalling = isFedFromAbove || IsFalling(currentLevel);
                byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;
                
                if (expectedEffectiveLevel >= props.flowLevels) return true; // Active: needs to decay to air
                if (expectedFluidLevel != currentLevel) return true;         // Active: needs to update level
            }
        }

        // If no activation conditions are met, the block is stable and does not need to be ticked.
        if (props.fluidType != FluidType.None && World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs) 
            Debug.Log($"[WaterDebug ACTIVE] pos={localPos} Returning FALSE");
        return false;
    }

    /// <summary>
    /// Performs block behavior and returns a list of VoxelMods to be applied by the World.
    /// </summary>
    /// <param name="chunkData">The data grid containing the voxel.</param>
    /// <param name="localPos">The local position of the voxel within the chunk.</param>
    /// <returns>A list of VoxelMod structs, or null if no changes occurred.</returns>
    [CanBeNull]
    public static List<VoxelMod> Behave(ChunkData chunkData, Vector3Int localPos)
    {
        s_mods.Clear(); // Clear the reusable list before use.

        // Get the voxel
        VoxelState? voxelNullable = chunkData.VoxelFromV3Int(localPos);

        // If the voxel is null (eg: outside the world), it can not behave.
        if (!voxelNullable.HasValue)
        {
            return null;
        }

        // Convert voxel to non-nullable
        VoxelState voxel = voxelNullable.Value;

        // Get the voxel's properties & ID
        BlockType props = voxel.Properties;
        ushort id = voxel.id;

        // --- Grass Block ---
        if (id == 2) // Grass Block
        {
            // Condition 1: If there is a solid block on top, grass turns to dirt.
            VoxelState? topNeighbour = chunkData.GetState(localPos + VoxelData.FaceChecks[2]);
            if (topNeighbour.HasValue && topNeighbour.Value.Properties.isSolid)
            {
                Vector3Int globalPos = new Vector3Int(localPos.x + chunkData.position.x, localPos.y, localPos.z + chunkData.position.y);
                VoxelMod voxelMod = new VoxelMod(globalPos, blockId: 3);
                s_mods.Add(voxelMod);
                return s_mods;
            }

            // Condition 2: Attempt to spread, using a GC-friendly method.
            int candidateCount = 0;
            Vector3Int chosenCandidateLocalPos = Vector3Int.zero; // A default value

            // Check standard spread locations
            foreach (Vector3Int vec in s_grassSpreadVectors)
            {
                Vector3Int checkPos = localPos + vec;
                if (IsConvertibleDirt(chunkData, checkPos))
                {
                    candidateCount++;
                    // Reservoir sampling: for the k-th item, replace choice with probability 1/k
                    if (Random.Range(0, candidateCount) == 0)
                    {
                        chosenCandidateLocalPos = checkPos;
                    }
                }
            }

            // Check "spread down" locations separately
            foreach (var vec in s_grassAirCheckVectors)
            {
                Vector3Int checkPos = localPos + vec;
                if (IsDirtNextToAir(chunkData, checkPos))
                {
                    candidateCount++;
                    if (Random.Range(0, candidateCount) == 0)
                    {
                        // The actual dirt block is below the air block
                        chosenCandidateLocalPos = checkPos + VoxelData.FaceChecks[3];
                    }
                }
            }

            // If we found at least one candidate...
            if (candidateCount > 0)
            {
                // Roll the dice to see if we spread this tick.
                if (Random.Range(0f, 1f) <= VoxelData.GrassSpreadChance)
                {
                    // Modify the single, randomly chosen candidate.
                    Vector3Int chosenCandidateGlobalPos = new Vector3Int(chosenCandidateLocalPos.x + chunkData.position.x, chosenCandidateLocalPos.y, chosenCandidateLocalPos.z + chunkData.position.y);
                    s_mods.Add(new VoxelMod(chosenCandidateGlobalPos, blockId: 2));
                }
            }
        }

        // --- GENERIC FLUID LOGIC ---
        if (props.fluidType != FluidType.None)
        {
            if (World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs)
                Debug.Log($"[WaterDebug BEHAVE] Behave called for {localPos} level={voxel.FluidLevel}");
            HandleFluidFlow(chunkData, localPos, voxel);
        }

        // IMPORTANT: Returns a reference to a shared static list. Callers must
        // consume the result immediately and must not store the reference, 
        // as it will be cleared on the next call to Behave().
        return s_mods.Count > 0 ? s_mods : null;
    }

    #endregion


    // --- Private Behavior Handlers ---

    #region Grass Behavior Methods

    /// <summary>
    /// Helper to check if a voxel at a position is a dirt block with air above it.
    /// </summary>
    private static bool IsConvertibleDirt(ChunkData chunkData, Vector3Int pos)
    {
        VoxelState? state = chunkData.GetState(pos);
        // It must be a dirt block (ID 3).
        if (!state.HasValue || state.Value.id != 3)
            return false;

        // The block above it must be air (ID 0).
        VoxelState? stateAbove = chunkData.GetState(pos + VoxelData.FaceChecks[2]);
        return stateAbove.HasValue && stateAbove.Value.id == 0;
    }

    /// <summary>
    /// Helper to check the special case of spreading downwards: is the target location Air,
    /// and is the block below *that* a convertible dirt block?
    /// </summary>
    private static bool IsDirtNextToAir(ChunkData chunkData, Vector3Int airPos)
    {
        VoxelState? state = chunkData.GetState(airPos);
        // The target adjacent block must be air.
        if (!state.HasValue || state.Value.id != 0)
            return false;

        // The block below the air block must be a convertible dirt block.
        return IsConvertibleDirt(chunkData, airPos + VoxelData.FaceChecks[3]); // FaceChecks[3] is Down
    }

    #endregion

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
        BlockType props = fluidState.Properties;
        ushort currentId = fluidState.id;
        byte currentLevel = fluidState.FluidLevel;
        Vector3Int globalPos = new Vector3Int(localPos.x + chunkData.position.x, localPos.y, localPos.z + chunkData.position.y);

        // Calculate gravity support state once at the top, since both Step 1 and Step 2 need it
        Vector3Int belowPos = localPos + Vector3Int.down;
        VoxelState? belowState = chunkData.GetState(belowPos);
        bool belowIsSameFluid = belowState.HasValue && belowState.Value.id == currentId;
        bool canFlowDown = belowState.HasValue && !belowState.Value.Properties.isSolid && !belowIsSameFluid;
        bool isSupportedBelow = belowState.HasValue && (belowState.Value.Properties.isSolid || (belowIsSameFluid && !IsFalling(belowState.Value.FluidLevel)));

        // --- Step 1: Calculate Expected Level (Decay / Drainage) ---
        // Source blocks (level 0) are infinite and never decay.
        if (currentLevel != 0)
        {
            byte expectedEffectiveLevel = props.flowLevels; // Default to max (decays to air)
            bool isFedFromAbove = false;

            // 1a: Check if fed from above
            VoxelState? aboveState = chunkData.GetState(localPos + Vector3Int.up);
            if (aboveState.HasValue && aboveState.Value.id == currentId)
            {
                isFedFromAbove = true;
                expectedEffectiveLevel = GetEffectiveLevel(aboveState.Value.FluidLevel);
                // Important: Falling fluid cannot be a source block (0).
                // A source (0) feeds a falling column of effective level 1.
                if (expectedEffectiveLevel == 0) expectedEffectiveLevel = 1;
            }

            if (isFedFromAbove)
            {
                // Naturally supported by the column above
            }
            else
            {
                // 1b: Check horizontal neighbors for the lowest effective level (closest to source)
                for (int i = 0; i < 4; i++)
                {
                    Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                    VoxelState? neighborState = chunkData.GetState(neighborPos);

                    if (neighborState.HasValue && neighborState.Value.id == currentId)
                    {
                        byte neighborEffective = GetEffectiveLevel(neighborState.Value.FluidLevel);
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
            }

            // 1c: Determine expected full fluidLevel state
            bool expectedFalling = isFedFromAbove || IsFalling(currentLevel); // Keep falling if we already are, unless we hit ground
            byte expectedFluidLevel = expectedFalling ? MakeFalling(expectedEffectiveLevel) : expectedEffectiveLevel;

            // If expected level is beyond max flow, or has changed, update it.
            if (expectedEffectiveLevel >= props.flowLevels)
            {
                // Drain to air if unsupported horizontally and vertically, or reached max flow
                s_mods.Add(new VoxelMod(globalPos, 0) { ImmediateUpdate = true });
                return; // Stop processing further
            }
            else if (expectedFluidLevel != currentLevel)
            {
                // Update our level to match our support
                s_mods.Add(new VoxelMod(globalPos, currentId) { FluidLevel = expectedFluidLevel, ImmediateUpdate = true });
                currentLevel = expectedFluidLevel; // Update local state for subsequent steps
            }
        }

        byte effectiveLevel = GetEffectiveLevel(currentLevel);
        bool falling = IsFalling(currentLevel);

        // --- Step 2: Gravity (Vertical Flow) ---
        if (canFlowDown)
        {
            Vector3Int globalBelowPos = new Vector3Int(globalPos.x, globalPos.y - 1, globalPos.z);
            s_mods.Add(new VoxelMod(globalBelowPos, currentId)
            {
                FluidLevel = MakeFalling(effectiveLevel),
            });
            return; // Skip horizontal spreading this tick if we pushed downwards
        }

        // --- Step 3: Settle Falling Blocks ---
        if (falling)
        {
            if (isSupportedBelow)
            {
                // We've hit a floor, settle into horizontal form and prepare to spread next tick.
                s_mods.Add(new VoxelMod(globalPos, currentId) { FluidLevel = effectiveLevel, ImmediateUpdate = true });
            }
            return;
        }

        // --- Step 4: Horizontal Spreading ---
        // Fluids ONLY spread horizontally if they are supported by a solid block or the same fluid (non-falling) below.
        
        if (World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs)
        {
            Debug.Log($"[WaterDebug FLOW] Step 4 REACHED: pos={globalPos} id={currentId} level={currentLevel} " + 
                      $"below={(belowState.HasValue ? belowState.Value.id.ToString() : "none")}(solid? {(belowState.HasValue ? belowState.Value.Properties.isSolid.ToString() : "N/A")}, " +
                      $"falling? {(belowState.HasValue ? IsFalling(belowState.Value.FluidLevel).ToString() : "N/A")}, " +
                      $"level={(belowState.HasValue ? belowState.Value.FluidLevel.ToString() : "N/A")}) " + 
                      $"isSupported: {isSupportedBelow}");
        }

        if (!isSupportedBelow) 
        {
            if (World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs)
                Debug.Log($"[WaterDebug FLOW] {globalPos} Not supported below. Returning.");
            return;
        }

        byte newLevel = (byte)(effectiveLevel + 1);
        if (newLevel >= props.flowLevels) return;

        for (int i = 0; i < 4; i++)
        {
            Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
            VoxelState? neighborState = chunkData.GetState(neighborPos);

            if (!neighborState.HasValue) continue;

            // Flow into air or same fluid with worse level
            bool neighborIsAir = neighborState.Value.id == 0;
            bool neighborIsSameFluidAndWorse = neighborState.Value.id == currentId &&
                                               GetEffectiveLevel(neighborState.Value.FluidLevel) > newLevel;

            if (neighborIsAir || neighborIsSameFluidAndWorse)
            {
                if (neighborState.Value.Properties.isSolid) continue;

                Vector3Int globalNeighborPos = new Vector3Int(
                    neighborPos.x + chunkData.position.x, neighborPos.y,
                    neighborPos.z + chunkData.position.y);
                
                if (World.Instance != null && World.Instance.settings.enableWaterDiagnosticLogs)
                    Debug.Log($"[WaterDebug FLOW] {globalPos} SPREADING HORIZONTALLY to {globalNeighborPos} with level {newLevel}");

                s_mods.Add(new VoxelMod(globalNeighborPos, currentId)
                {
                    FluidLevel = newLevel,
                });
            }
        }
    }

    #endregion

    // --- HELPER METHODS ---

    #region Helper Methods

    /// Helper to reduce boilerplate code when checking a neighbour's neighbour.
    private static VoxelState? GetNeighboursNeighbour(ChunkData chunkData, Vector3Int initialPos, int neighbourFaceIndex, int finalFaceIndex)
    {
        VoxelState? initialNeighbour = chunkData.GetState(initialPos + VoxelData.FaceChecks[neighbourFaceIndex]);
        if (!initialNeighbour.HasValue) return null;

        return chunkData.GetState(initialPos + VoxelData.FaceChecks[neighbourFaceIndex] + VoxelData.FaceChecks[finalFaceIndex]);
    }

    #endregion
}
