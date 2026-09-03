using Data;
using Jobs.Data;
using Unity.Collections;

namespace Helpers
{
    /// <summary>
    /// The buffer acquisition <see cref="NeighborMapAssembler"/> needs from its owner — the
    /// <see cref="IMeshDrainHost"/> / <see cref="IMeshCompletionHost"/> sibling. Implemented explicitly by
    /// <c>WorldJobManager</c> on <c>this</c> (cached, zero per-schedule allocation); a validation fake returns
    /// per-coordinate markers instead of real buffers.
    /// </summary>
    public interface INeighborMapSource
    {
        /// <summary>Acquires the filled voxel map for one neighbor chunk.</summary>
        /// <param name="coord">The neighbor's chunk coordinate.</param>
        /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
        /// <param name="allocator">The allocator for the non-pooled path.</param>
        /// <returns>A filled full-volume voxel map.</returns>
        NativeArray<uint> AcquireVoxelMap(ChunkCoord coord, bool pooled, Allocator allocator);

        /// <summary>Acquires the filled light map for one neighbor chunk.</summary>
        /// <param name="coord">The neighbor's chunk coordinate.</param>
        /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
        /// <param name="allocator">The allocator for the non-pooled path.</param>
        /// <returns>A filled full-volume light map.</returns>
        NativeArray<ushort> AcquireLightMap(ChunkCoord coord, bool pooled, Allocator allocator);
    }

    /// <summary>
    /// Builds a <see cref="NeighborMapSet"/> from a chunk's 8 horizontal neighbors. This type owns the
    /// engine's compass convention — <b>N = (0, +1), E = (+1, 0), S = (0, -1), W = (-1, 0)</b> — which
    /// <c>NeighborhoodLightingJob</c>, <c>FluidTickJob</c>, <c>FluidBurstTicker</c> and
    /// <c>ChunkMath.GatherPaddedFluidVoxelsBand</c> all key their own offsets against.
    /// <para>
    /// Extracted from <c>WorldJobManager.AcquireNeighborMaps</c> (MP-7 review round 2) for one reason: it is a
    /// hand-written direction→offset table feeding <b>both</b> the meshing and lighting schedules, and neither
    /// suite's harness executed it — both build their own <see cref="NeighborMapSet"/>. Behind
    /// <see cref="INeighborMapSource"/> it is world-free and therefore guardable (meshing baseline B39).
    /// </para>
    /// </summary>
    public static class NeighborMapAssembler
    {
        /// <summary>
        /// Acquires the filled neighbor map set (8 voxel + 8 light maps) for the given center chunk. This is
        /// the single authoritative direction→offset mapping — a transposition here sends every seam of the
        /// affected axis to the wrong chunk, in both the meshing and lighting pipelines.
        /// </summary>
        /// <param name="center">The chunk whose neighborhood is snapshotted.</param>
        /// <param name="source">Supplies the per-neighbor buffers.</param>
        /// <param name="pooled">Whether to rent from the pool instead of allocating.</param>
        /// <param name="allocator">The allocator for the non-pooled path.</param>
        /// <returns>A neighbor map set with every buffer filled.</returns>
        public static NeighborMapSet Build(ChunkCoord center, INeighborMapSource source, bool pooled, Allocator allocator)
        {
            return new NeighborMapSet
            {
                NeighborN = source.AcquireVoxelMap(center.Neighbor(0, 1), pooled, allocator),
                NeighborE = source.AcquireVoxelMap(center.Neighbor(1, 0), pooled, allocator),
                NeighborS = source.AcquireVoxelMap(center.Neighbor(0, -1), pooled, allocator),
                NeighborW = source.AcquireVoxelMap(center.Neighbor(-1, 0), pooled, allocator),
                NeighborNE = source.AcquireVoxelMap(center.Neighbor(1, 1), pooled, allocator),
                NeighborSE = source.AcquireVoxelMap(center.Neighbor(1, -1), pooled, allocator),
                NeighborSW = source.AcquireVoxelMap(center.Neighbor(-1, -1), pooled, allocator),
                NeighborNW = source.AcquireVoxelMap(center.Neighbor(-1, 1), pooled, allocator),
                LightN = source.AcquireLightMap(center.Neighbor(0, 1), pooled, allocator),
                LightE = source.AcquireLightMap(center.Neighbor(1, 0), pooled, allocator),
                LightS = source.AcquireLightMap(center.Neighbor(0, -1), pooled, allocator),
                LightW = source.AcquireLightMap(center.Neighbor(-1, 0), pooled, allocator),
                LightNE = source.AcquireLightMap(center.Neighbor(1, 1), pooled, allocator),
                LightSE = source.AcquireLightMap(center.Neighbor(1, -1), pooled, allocator),
                LightSW = source.AcquireLightMap(center.Neighbor(-1, -1), pooled, allocator),
                LightNW = source.AcquireLightMap(center.Neighbor(-1, 1), pooled, allocator),
            };
        }
    }
}
