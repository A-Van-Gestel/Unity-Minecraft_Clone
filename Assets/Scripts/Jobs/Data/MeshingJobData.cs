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
        /// The target <c>ChunkData.LifecycleEpoch</c> captured when this job was scheduled (MP-4), used by the
        /// merge to recognize a result whose chunk was recycled mid-flight — the mesh then describes terrain
        /// that no longer exists at that coord. Blittable rather than a <c>ChunkData</c> reference so this
        /// struct stays managed-field-free under <c>Assets/Scripts/Jobs/</c> (Burst rules).
        /// <para><b>Only half of the identity.</b> <c>LifecycleEpoch</c> is a <i>per-instance</i> counter, so
        /// this value is meaningful only when the live <c>ChunkData</c> is the same OBJECT it was captured from
        /// (a successor instance starts at epoch 0 and would compare equal to a captured 0). The reference half
        /// lives in <c>WorldJobManager._meshJobTargets</c> and the two are always checked together — the CP-3
        /// pool-ABA pairing. Never compare this epoch on its own.</para>
        /// </summary>
        public int TargetEpoch;

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
