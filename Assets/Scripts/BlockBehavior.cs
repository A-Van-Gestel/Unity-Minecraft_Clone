using System;
using System.Collections.Generic;
using Data;
using JetBrains.Annotations;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Contains the static logic for all special block behaviors in the world,
/// such as grass spreading and fluid simulation.
/// </summary>
public static partial class BlockBehavior
{
    [ThreadStatic]
    private static List<VoxelMod> _tMods;

    // A ThreadStatic reusable list to avoid allocating memory while ensuring thread-safety.
    // Lazy initialized because ThreadStatic inline initializers only run for the first thread.
    private static List<VoxelMod> Mods => _tMods ??= new List<VoxelMod>();

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
        if (id == BlockIDs.Grass)
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
            return IsFluidActive(chunkData, localPos, voxel, props, id);
        }

        // If no activation conditions are met, the block is stable and does not need to be ticked.
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
        Mods.Clear(); // Clear the reusable list before use.

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
        if (id == BlockIDs.Grass)
        {
            // Condition 1: If there is a solid block on top, grass turns to dirt.
            VoxelState? topNeighbour = chunkData.GetState(localPos + VoxelData.FaceChecks[2]);
            if (topNeighbour.HasValue && topNeighbour.Value.Properties.isSolid)
            {
                Vector3Int globalPos = new Vector3Int(localPos.x + chunkData.position.x, localPos.y, localPos.z + chunkData.position.y);
                VoxelMod voxelMod = new VoxelMod(globalPos, BlockIDs.Dirt);
                Mods.Add(voxelMod);
                return Mods;
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
                    Mods.Add(new VoxelMod(chosenCandidateGlobalPos, BlockIDs.Grass));
                }
            }
        }

        // --- GENERIC FLUID LOGIC ---
        if (props.fluidType != FluidType.None)
        {
            LogWaterDebug($"[WaterDebug BEHAVE] Behave called for {localPos} level={voxel.FluidLevel}");
            HandleFluidFlow(chunkData, localPos, voxel);
        }

        // IMPORTANT: Returns a reference to a shared ThreadStatic list. Callers must
        // consume the result immediately (e.g. by enqueueing it) and must not store
        // the reference, as it will be cleared on the next call to Behave() by this thread.
        return Mods.Count > 0 ? Mods : null;
    }

    #endregion
}
