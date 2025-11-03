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
    private static readonly List<VoxelMod> Mods = new List<VoxelMod>();


    // --- Public Methods ---

    #region Public Methods

    /// <summary>
    /// Checks if a voxel at a given position in a chunk should be "active" and processed on each tick.
    /// This method acts as a performance gatekeeper, ensuring that only blocks that can potentially
    /// change their state are added to the active update loop.
    /// </summary>
    /// <param name="chunkData">The data grid containing the voxel.</param>
    /// <param name="pos">The local position of the voxel within the chunk.</param>
    /// <returns>True if the voxel needs to be ticked; otherwise, false.</returns>
    public static bool Active(ChunkData chunkData, Vector3Int pos)
    {
        // Get the voxel's ID. If the position is invalid, this will throw an error,
        // which is intended as this should only be called for valid, active voxels.
        VoxelState voxel = chunkData.VoxelFromV3Int(pos);
        BlockType props = voxel.Properties;
        byte id = voxel.id;

        if (id == 2) // Grass Block
        {
            // A grass block is active if there is an adjacent dirt block that it can spread to.
            // We must check all possible spread locations.
            // Check adjacent (cardinal directions)
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[0])) return true; // Back
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[1])) return true; // Front
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[4])) return true; // Left
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[5])) return true; // Right

            // Check one block above adjacent
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[0] + VoxelData.FaceChecks[2])) return true; // Back, Up
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[1] + VoxelData.FaceChecks[2])) return true; // Front, Up
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[4] + VoxelData.FaceChecks[2])) return true; // Left, Up
            if (IsConvertibleDirt(chunkData, pos + VoxelData.FaceChecks[5] + VoxelData.FaceChecks[2])) return true; // Right, Up

            // Check one block below adjacent (for spreading "down" onto dirt next to an air block)
            if (IsDirtNextToAir(chunkData, pos + VoxelData.FaceChecks[0])) return true; // Back
            if (IsDirtNextToAir(chunkData, pos + VoxelData.FaceChecks[1])) return true; // Front
            if (IsDirtNextToAir(chunkData, pos + VoxelData.FaceChecks[4])) return true; // Left
            if (IsDirtNextToAir(chunkData, pos + VoxelData.FaceChecks[5])) return true; // Right
        }

        // --- Generic Fluid Activation Logic ---
        if (props.fluidType != FluidType.None)
        {
            // A fluid block is active if it has any potential to flow.

            // Reason 1: The block below is not solid or is a different fluid type.
            VoxelState? belowState = chunkData.GetState(pos + Vector3Int.down);
            if (!belowState.HasValue || !belowState.Value.Properties.isSolid || belowState.Value.Properties.fluidType != props.fluidType)
            {
                return true; // Must be active to fall or interact.
            }

            // Reason 2: It's a source block (level 0) and can flow out. Source blocks are always potentially active.
            if (voxel.FluidLevel == 0) return true;

            // Reason 3: It is a flowing fluid block that is not at its maximum flow distance.
            if (voxel.FluidLevel >= props.flowLevels - 1)
            {
                return false; // Max flow distance reached, it is stable.
            }

            // Reason 4: Check if any horizontal neighbor is a valid flow target.
            for (int i = 0; i < 4; i++)
            {
                Vector3Int neighborPos = pos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
                VoxelState? neighborState = chunkData.GetState(neighborPos);

                if (!neighborState.HasValue) continue; // Edge of loaded world, cannot flow.

                // If neighbor is air, we can flow into it.
                if (neighborState.Value.id == 0) return true;

                // If neighbor is the same fluid type and has a lower fluid level (higher level value), we can flow to level it out.
                if (neighborState.Value.Properties.fluidType == props.fluidType && neighborState.Value.FluidLevel > voxel.FluidLevel + 1) return true;
            }
        }

        // If no activation conditions are met, the block is stable and does not need to be ticked.
        return false;
    }

    /// <summary>
    /// Performs block behavior and returns a list of VoxelMods to be applied by the World.
    /// </summary>
    /// <returns>A list of VoxelMod structs, or null if no changes occurred.</returns>
    [CanBeNull]
    public static List<VoxelMod> Behave(ChunkData chunkData, Vector3Int localPos)
    {
        Mods.Clear(); // Clear the reusable list before use.
        VoxelState voxel = chunkData.VoxelFromV3Int(localPos);
        BlockType props = voxel.Properties;
        byte id = voxel.id;

        if (id == 2) // Grass Block
        {
            // Condition 1: If there is a solid block on top, grass turns to dirt.
            VoxelState? topNeighbour = chunkData.GetState(localPos + VoxelData.FaceChecks[2]);
            if (topNeighbour.HasValue && topNeighbour.Value.Properties.isSolid)
            {
                Vector3Int globalPos = new Vector3Int(localPos.x + chunkData.position.x, localPos.y, localPos.z + chunkData.position.y);
                VoxelMod voxelMod = new VoxelMod(globalPos, blockId: 3);
                Mods.Add(voxelMod);
                return Mods;
            }

            // Condition 2: Attempt to spread, using a GC-friendly method.
            int candidateCount = 0;
            Vector3Int chosenCandidateLocalPos = Vector3Int.zero; // A default value

            // Create an array of all possible relative locations to check.
            Vector3Int[] spreadVectors =
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

            // Check standard spread locations
            foreach (Vector3Int vec in spreadVectors)
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
            Vector3Int[] airCheckVectors =
            {
                VoxelData.FaceChecks[0],
                VoxelData.FaceChecks[1],
                VoxelData.FaceChecks[4],
                VoxelData.FaceChecks[5],
            };
            foreach (var vec in airCheckVectors)
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
                    Mods.Add(new VoxelMod(chosenCandidateGlobalPos, blockId: 2));
                }
            }
        }

        // --- GENERIC FLUID LOGIC ---
        if (props.fluidType != FluidType.None)
        {
            HandleFluidFlow(chunkData, localPos, voxel);
        }

        // Return the list of modifications.
        return Mods.Count > 0 ? Mods : null;
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

    /// <summary>
    /// Manages the flow logic for a single fluid voxel.
    /// </summary>
    private static void HandleFluidFlow(ChunkData chunkData, Vector3Int localPos, VoxelState fluidState)
    {
        BlockType props = fluidState.Properties;
        byte currentId = fluidState.id;
        byte currentLevel = fluidState.FluidLevel;
        Vector3Int globalPos = new Vector3Int(localPos.x + chunkData.position.x, localPos.y, localPos.z + chunkData.position.y);


        // --- Rule 1: Flow Downwards ---
        // Fluids always try to flow down into empty space first.
        Vector3Int belowPos = localPos + Vector3Int.down;
        VoxelState? belowState = chunkData.GetState(belowPos);

        if (belowState.HasValue && !belowState.Value.Properties.isSolid)
        {
            // Replace the block below with a new source block of this fluid.
            // This creates waterfalls and ensures fluid columns fill up from the bottom.
            Vector3Int globalBelowPos = new Vector3Int(globalPos.x, globalPos.y - 1, globalPos.z);
            Mods.Add(new VoxelMod(globalBelowPos, blockId:  currentId)); // Place a new source block below

            // If the current block was a flowing block (not a source), it has now flowed away
            // and should be replaced with air. Source blocks are infinite and remain.
            if (currentLevel > 0)
            {
                Mods.Add(new VoxelMod(globalPos, blockId: 0)); // Replace self with air
            }

            return; // Fluid has moved down, no further action this tick.
        }

        // --- Rule 2: Flow Horizontally ---
        // If it can't flow down, it tries to spread out.
        // A source block (level 0) is required to start a horizontal flow.
        if (currentLevel >= props.flowLevels - 1) return; // Max flow distance reached.

        // Check horizontal neighbors
        for (int i = 0; i < 4; i++)
        {
            Vector3Int neighborPos = localPos + VoxelData.FaceChecks[VoxelData.HorizontalFaceChecksIndices[i]];
            VoxelState? neighborState = chunkData.GetState(neighborPos);

            if (neighborState.HasValue && (!neighborState.Value.Properties.isSolid || neighborState.Value.Properties.fluidType != FluidType.None))
            {
                byte newLevel = (byte)(currentLevel + 1);
                
                // Flow into air or a fluid block with a lower level
                if (neighborState.Value.id == 0 || (neighborState.Value.id == currentId && neighborState.Value.FluidLevel > newLevel))
                {
                    Vector3Int globalNeighborPos = new Vector3Int(neighborPos.x + chunkData.position.x, neighborPos.y, neighborPos.z + chunkData.position.y);
                    VoxelMod mod = new VoxelMod(globalNeighborPos, blockId: currentId)
                    {
                        FluidLevel = newLevel,
                    };
                    mod.FluidLevel = newLevel; // Set fluid level
                    Mods.Add(mod);
                }
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