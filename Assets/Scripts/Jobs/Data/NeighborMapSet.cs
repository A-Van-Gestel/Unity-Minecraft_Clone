using Unity.Collections;

namespace Jobs.Data
{
    /// <summary>
    /// The full-volume voxel and light map snapshots of a chunk's 8 horizontal neighbors,
    /// as consumed by neighborhood-aware jobs (lighting, meshing). When changing the field set,
    /// also update the fill site (<c>WorldJobManager.AcquireNeighborMaps</c>) and the pool
    /// return overload (<c>Helpers.ChunkJobArrayPool.Return(in NeighborMapSet)</c>).
    /// <para>Buffers are either rented from <c>Helpers.ChunkJobArrayPool</c> (runtime path —
    /// return via the pool) or allocated per job (startup/editor/benchmark paths —
    /// release via <see cref="Dispose"/>).</para>
    /// </summary>
    public struct NeighborMapSet
    {
        public NativeArray<uint> NeighborN, NeighborE, NeighborS, NeighborW;
        public NativeArray<uint> NeighborNE, NeighborSE, NeighborSW, NeighborNW;
        public NativeArray<ushort> LightN, LightE, LightS, LightW;
        public NativeArray<ushort> LightNE, LightSE, LightSW, LightNW;

        /// <summary>
        /// Disposes every created buffer in the set (uncreated entries are skipped).
        /// Only for non-pooled buffers — pooled buffers must be returned via
        /// <c>Helpers.ChunkJobArrayPool.Return(in NeighborMapSet)</c> instead.
        /// </summary>
        public void Dispose()
        {
            if (NeighborN.IsCreated) NeighborN.Dispose();
            if (NeighborE.IsCreated) NeighborE.Dispose();
            if (NeighborS.IsCreated) NeighborS.Dispose();
            if (NeighborW.IsCreated) NeighborW.Dispose();
            if (NeighborNE.IsCreated) NeighborNE.Dispose();
            if (NeighborSE.IsCreated) NeighborSE.Dispose();
            if (NeighborSW.IsCreated) NeighborSW.Dispose();
            if (NeighborNW.IsCreated) NeighborNW.Dispose();
            if (LightN.IsCreated) LightN.Dispose();
            if (LightE.IsCreated) LightE.Dispose();
            if (LightS.IsCreated) LightS.Dispose();
            if (LightW.IsCreated) LightW.Dispose();
            if (LightNE.IsCreated) LightNE.Dispose();
            if (LightSE.IsCreated) LightSE.Dispose();
            if (LightSW.IsCreated) LightSW.Dispose();
            if (LightNW.IsCreated) LightNW.Dispose();
        }
    }
}
