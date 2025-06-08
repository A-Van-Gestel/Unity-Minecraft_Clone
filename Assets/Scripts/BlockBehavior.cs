using Data;
using UnityEngine;

public static class BlockBehavior
{
    /// <summary>
    /// Checks if a voxel at a given position in a chunk should be "active" (e.g., grass that can spread).
    /// </summary>
    /// <param name="chunkData">The data grid containing the voxel.</param>
    /// <param name="pos">The local position of the voxel within the chunk.</param>
    /// <returns>True if the voxel should perform active behaviors.</returns>
    public static bool Active(ChunkData chunkData, Vector3Int pos)
    {
        // Get the voxel's ID. If the position is invalid, this will throw an error,
        // which is intended as this should only be called for valid, active voxels.
        byte id = chunkData.map[pos.x, pos.y, pos.z].id;

        switch (id)
        {
            case 2: // Grass Block
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

                break;
        }

        // If we get here, the block either isn't active or doesn't have a behavior.
        return false;
    }

    /// <summary>
    /// Performs the behavior of an active voxel (e.g., grass spreading or dying).
    /// This version is optimized to avoid List allocations and reduce garbage.
    /// </summary>
    public static void Behave(ChunkData chunkData, Vector3Int pos)
    {
        byte id = chunkData.map[pos.x, pos.y, pos.z].id;

        switch (id)
        {
            case 2: // Grass Block
                // Condition 1: If there is a solid block on top, grass turns to dirt.
                VoxelState? topNeighbour = chunkData.GetState(pos + VoxelData.FaceChecks[2]);
                if (topNeighbour.HasValue && topNeighbour.Value.Properties.isSolid)
                {
                    chunkData.ModifyVoxel(pos, 3, 0);
                    return;
                }

                // Condition 2: Attempt to spread, using a GC-friendly method.
                int candidateCount = 0;
                Vector3Int chosenCandidate = Vector3Int.zero; // A default value

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
                    VoxelData.FaceChecks[5] + VoxelData.FaceChecks[2]
                };

                // Check standard spread locations
                foreach (Vector3Int vec in spreadVectors)
                {
                    Vector3Int checkPos = pos + vec;
                    if (IsConvertibleDirt(chunkData, checkPos))
                    {
                        candidateCount++;
                        // Reservoir sampling: for the k-th item, replace choice with probability 1/k
                        if (Random.Range(0, candidateCount) == 0)
                        {
                            chosenCandidate = checkPos;
                        }
                    }
                }

                // Check "spread down" locations separately
                Vector3Int[] airCheckVectors =
                {
                    VoxelData.FaceChecks[0],
                    VoxelData.FaceChecks[1],
                    VoxelData.FaceChecks[4],
                    VoxelData.FaceChecks[5]
                };
                foreach (var vec in airCheckVectors)
                {
                    Vector3Int checkPos = pos + vec;
                    if (IsDirtNextToAir(chunkData, checkPos))
                    {
                        candidateCount++;
                        if (Random.Range(0, candidateCount) == 0)
                        {
                            // The actual dirt block is below the air block
                            chosenCandidate = checkPos + VoxelData.FaceChecks[3];
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
                        chunkData.ModifyVoxel(chosenCandidate, 2, 1);
                    }
                }

                break;
        }
    }

    /// Helper to reduce boilerplate code when checking a neighbour's neighbour.
    /// It's safe because it handles the case where the initial neighbour doesn't exist.
    private static VoxelState? GetNeighboursNeighbour(ChunkData chunkData, Vector3Int initialPos, int neighbourFaceIndex, int finalFaceIndex)
    {
        VoxelState? initialNeighbour = chunkData.GetState(initialPos + VoxelData.FaceChecks[neighbourFaceIndex]);
        if (!initialNeighbour.HasValue) return null;

        return chunkData.GetState(initialPos + VoxelData.FaceChecks[neighbourFaceIndex] + VoxelData.FaceChecks[finalFaceIndex]);
    }

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
}