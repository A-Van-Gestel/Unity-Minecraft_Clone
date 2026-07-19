using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// The routing seam for sunlight column recalculations: resolves which chunk's queue bucket a
    /// global voxel-space column belongs to, and the chunk-local column the lighting job's 16×16
    /// heightmap is indexed with. Shared by production (<c>WorldData.QueueSunlightRecalculation</c>,
    /// the <c>WorldJobManager</c> job-build drain, orphan-column persistence) and the lighting
    /// validation harness, so far-coordinate routing is exercised by the validation suite.
    /// </summary>
    public static class SunlightColumnRouting
    {
        /// <summary>
        /// Resolves the voxel-space origin of the chunk that owns the given global column — the
        /// key of the world-level sunlight recalculation queue bucket the column is routed to.
        /// Pure integer shift math: exact to the ±2³¹ edge (a float round-trip here mis-routes
        /// border columns past ±2²⁴ — Bug 19).
        /// </summary>
        /// <param name="globalColumn">The global voxel-space column (X, Z).</param>
        /// <returns>The owning chunk's voxel-space origin.</returns>
        public static Vector2Int RouteToChunkOrigin(Vector2Int globalColumn)
        {
            return new Vector2Int(
                ChunkMath.VoxelToChunk(globalColumn.x) * VoxelData.ChunkWidth,
                ChunkMath.VoxelToChunk(globalColumn.y) * VoxelData.ChunkWidth);
        }

        /// <summary>
        /// Converts a global column to the chunk-local column (expected range [0, ChunkWidth) on
        /// both axes) used to index the owning chunk's heightmap in the lighting job.
        /// </summary>
        /// <param name="globalColumn">The global voxel-space column (X, Z).</param>
        /// <param name="chunkVoxelOrigin">The owning chunk's voxel-space origin.</param>
        /// <returns>The chunk-local column.</returns>
        public static Vector2Int ToLocalColumn(Vector2Int globalColumn, Vector2Int chunkVoxelOrigin)
        {
            return new Vector2Int(globalColumn.x - chunkVoxelOrigin.x, globalColumn.y - chunkVoxelOrigin.y);
        }
    }
}
