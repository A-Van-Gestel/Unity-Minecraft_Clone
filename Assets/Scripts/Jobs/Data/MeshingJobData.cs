using Data;
using Unity.Collections;
using Unity.Jobs;

namespace Jobs.Data
{
    /// <summary>
    /// A container for all data associated with a single scheduled MeshGenerationJob:
    /// the JobHandle for tracking, the full-volume input snapshots (center + 8 neighbors,
    /// voxel and light maps), the per-section metadata, and the mesh output.
    /// <para>In the runtime path the input buffers are rented from <c>ChunkJobArrayPool</c> and
    /// returned after the job completes; <see cref="Dispose"/> is the non-pooled fallback for
    /// shutdown paths and callers that do not use the pool.</para>
    /// </summary>
    public struct MeshingJobData
    {
        public JobHandle Handle;

        // --- Input data (full-volume snapshots; pooled buffers in the runtime path) ---
        public NativeArray<uint> Map;
        public NativeArray<ushort> LightMap;
        public NeighborMapSet Neighbors;

        // --- Input data (per-job allocations) ---
        public NativeArray<SectionJobData> SectionData;

        // --- Output data ---
        public MeshDataJobOutput Output;

        /// <summary>
        /// Disposes all input containers and the output. Only for non-pooled usage —
        /// the runtime path returns the input buffers to the pool instead.
        /// </summary>
        public void Dispose()
        {
            if (Map.IsCreated) Map.Dispose();
            if (LightMap.IsCreated) LightMap.Dispose();
            Neighbors.Dispose();
            if (SectionData.IsCreated) SectionData.Dispose();

            Output.Dispose();
        }
    }
}
