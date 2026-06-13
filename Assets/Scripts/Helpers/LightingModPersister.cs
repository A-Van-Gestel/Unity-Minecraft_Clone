using System.Runtime.CompilerServices;
using Data;
using Jobs;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Shared, side-effect-free math for persisting an undeliverable cross-chunk
    /// <see cref="LightModification"/> (one targeting a chunk that is in-world but not currently loaded).
    /// <para>
    /// Centralized so the production orchestrator (<c>WorldJobManager.PersistUndeliverableLightMod</c>) and
    /// the editor lighting validation harness compute a modification's LOCAL column inside its target chunk
    /// — and apply the same in-footprint bounds guard — through identical code, mirroring the existing
    /// <see cref="LightingJobProcessor"/> / <see cref="CrossChunkLightModApplier"/> seams. The off-by-one
    /// risk in the column math lives here once; callers own the channel dispatch (sunlight column recalc
    /// vs. pending blocklight store) and any logging of the rejected case.
    /// </para>
    /// </summary>
    public static class LightingModPersister
    {
        /// <summary>
        /// Computes the LOCAL column coordinate of a cross-chunk light modification within its target
        /// chunk and validates that the modification actually falls inside that chunk's horizontal
        /// footprint (0..<see cref="VoxelData.ChunkWidth"/>).
        /// </summary>
        /// <param name="targetChunkCoord">The chunk the modification is being delivered to.</param>
        /// <param name="mod">The cross-chunk modification (its <see cref="LightModification.GlobalPosition"/>
        /// is resolved against the target chunk's voxel origin).</param>
        /// <param name="localX">The resulting local X column (0..ChunkWidth-1 when valid).</param>
        /// <param name="localZ">The resulting local Z column (0..ChunkWidth-1 when valid).</param>
        /// <returns>True when the modification lies inside the target chunk's footprint; false otherwise
        /// (the caller should treat a false result as a logic error and skip the modification).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryComputeLocalColumn(ChunkCoord targetChunkCoord, in LightModification mod,
            out int localX, out int localZ)
        {
            Vector2Int chunkVoxelPos = targetChunkCoord.ToVoxelOrigin();
            localX = mod.GlobalPosition.x - chunkVoxelPos.x;
            localZ = mod.GlobalPosition.z - chunkVoxelPos.y;

            return localX >= 0 && localX < VoxelData.ChunkWidth &&
                   localZ >= 0 && localZ < VoxelData.ChunkWidth;
        }
    }
}
