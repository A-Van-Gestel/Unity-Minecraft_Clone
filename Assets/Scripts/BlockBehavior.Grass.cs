using Data;
using UnityEngine;

public static partial class BlockBehavior
{
    #region Grass Behavior Methods

    /// <summary>
    /// Helper to check if a voxel at a position is a dirt block with air above it.
    /// </summary>
    private static bool IsConvertibleDirt(ChunkData chunkData, Vector3Int pos)
    {
        VoxelState? state = chunkData.GetState(pos);
        // It must be a dirt block (ID 3).
        if (!state.HasValue || state.Value.id != BlockIDs.Dirt)
            return false;

        // The block above it must be air (ID 0).
        VoxelState? stateAbove = chunkData.GetState(pos + VoxelData.FaceChecks[2]);
        return stateAbove.HasValue && stateAbove.Value.id == BlockIDs.Air;
    }

    /// <summary>
    /// Helper to check the special case of spreading downwards: is the target location Air,
    /// and is the block below *that* a convertible dirt block?
    /// </summary>
    private static bool IsDirtNextToAir(ChunkData chunkData, Vector3Int airPos)
    {
        VoxelState? state = chunkData.GetState(airPos);
        // The target adjacent block must be air.
        if (!state.HasValue || state.Value.id != BlockIDs.Air)
            return false;

        // The block below the air block must be a convertible dirt block.
        return IsConvertibleDirt(chunkData, airPos + VoxelData.FaceChecks[3]); // FaceChecks[3] is Down
    }

    /// Helper to reduce boilerplate code when checking a neighbour's neighbour.
    private static VoxelState? GetNeighboursNeighbour(ChunkData chunkData, Vector3Int initialPos, int neighbourFaceIndex, int finalFaceIndex)
    {
        VoxelState? initialNeighbour = chunkData.GetState(initialPos + VoxelData.FaceChecks[neighbourFaceIndex]);
        if (!initialNeighbour.HasValue) return null;

        return chunkData.GetState(initialPos + VoxelData.FaceChecks[neighbourFaceIndex] + VoxelData.FaceChecks[finalFaceIndex]);
    }

    #endregion
}
