using Data;

namespace Helpers
{
    /// <summary>
    /// Carries a chunk's accumulated border light writes to the neighbors whose meshes read those borders, and answers
    /// which neighbors' meshes are stale. A neighbor is told at the writer's next lighting merge, which every
    /// lighting-enabled write guarantees (it enqueues BFS work on the written chunk).
    /// </summary>
    public static class MeshInputPropagation
    {
        /// <summary>Marks stale the mesh of every loaded neighbor whose facing border light changed since the last call.</summary>
        /// <param name="coord">The chunk that was written.</param>
        /// <param name="chunkData">Its data; its pending border writes are consumed.</param>
        /// <param name="lookup">Resolves the neighbors.</param>
        public static void Propagate(ChunkCoord coord, ChunkData chunkData, IChunkDataLookup lookup) =>
            MarkStale(coord, chunkData.ConsumePendingBorderWrites(), lookup);

        /// <summary>Marks stale the mesh of every loaded neighbor named in <paramref name="neighbors"/>.</summary>
        /// <param name="coord">The chunk whose neighbors are marked.</param>
        /// <param name="neighbors"><see cref="ChunkBorderMask"/> bits of the neighbors to mark.</param>
        /// <param name="lookup">Resolves the neighbors.</param>
        public static void MarkStale(ChunkCoord coord, byte neighbors, IChunkDataLookup lookup)
        {
            while (ChunkBorderMask.TryTakeNext(ref neighbors, out byte bit))
            {
                if (lookup.TryGetChunkData(ChunkBorderMask.Neighbor(coord, bit), out ChunkData neighbor) && neighbor != null)
                    neighbor.MarkMeshInputsStale();
            }
        }

        /// <summary>The neighbors of a chunk whose mesh no longer matches its inputs.</summary>
        /// <param name="coord">The chunk whose neighbors are checked.</param>
        /// <param name="lookup">Resolves the neighbors.</param>
        /// <returns><see cref="ChunkBorderMask"/> bits of the loaded neighbors with stale meshes.</returns>
        public static byte StaleNeighbors(ChunkCoord coord, IChunkDataLookup lookup)
        {
            int stale = 0;
            byte remaining = ChunkBorderMask.All;
            while (ChunkBorderMask.TryTakeNext(ref remaining, out byte bit))
            {
                if (lookup.TryGetChunkData(ChunkBorderMask.Neighbor(coord, bit), out ChunkData neighbor) && neighbor != null
                    && !neighbor.MeshInputsCurrent)
                    stale |= bit;
            }

            return (byte)stale;
        }
    }
}
