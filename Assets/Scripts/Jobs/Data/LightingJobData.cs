using System.Runtime.InteropServices;
using Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs.Data
{
    /// <summary>
    /// A container for the persistent NativeArray inputs required by a NeighborhoodLightingJob.
    /// This struct is responsible for disposing of all the data it contains.
    /// </summary>
    public struct LightingJobInputData
    {
        public NativeArray<ushort> Heightmap;
        public NeighborMapSet Neighbors;

        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            if (Heightmap.IsCreated) Heightmap.Dispose();
            Neighbors.Dispose();
        }
    }


    /// <summary>
    /// A comprehensive container for all data associated with a single NeighborhoodLightingJob.
    /// This struct manages the entire lifecycle of the job's native data, including the JobHandle for tracking,
    /// the persistent input arrays (neighbor maps), and the persistent output collections (modified map, cross-chunk mods).
    /// Its Dispose() method is responsible for cleaning up all associated native memory for both input and output.
    /// </summary>
    public struct LightingJobData
    {
        public JobHandle Handle;

        /// <summary>
        /// True when the full-volume maps (center + neighbor voxel/light maps) were rented from
        /// <c>ChunkJobArrayPool</c> and must be returned to it instead of disposed. False for
        /// TempJob-allocated startup jobs and non-pooled callers (editor pipeline, benchmarks),
        /// which clean up via <see cref="Dispose"/>. Not read by any Burst job.
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool UsesPooledBuffers;

        // --- Input data ---
        public LightingJobInputData Input;

        // P-2 Layer 1: the halo-padded volumes the job reads/writes. These are rented UNFILLED here; the
        // job's worker-thread gather (NeighborhoodLightingJob.Execute) populates them from the center +
        // 8 neighbor snapshot maps (Map/LightMap + Input.Neighbors), wired in via SetGatherSources. The
        // gather writes PaddedVoxels once (then read-only in-job); PaddedLight is the job's sole writable
        // light store. After the job completes, the center [2,18) region of PaddedLight is extracted back
        // into LightMap for ApplyJobLightMap. Pooled via ChunkJobArrayPool.RentPaddedVoxels/RentPaddedLight
        // when UsesPooledBuffers.
        public NativeArray<uint> PaddedVoxels;
        public NativeArray<ushort> PaddedLight;

        // --- Output data ---
        public NativeArray<uint> Map; // The center chunk voxel snapshot (gather source + ApplyJobLightMap reference)
        public NativeArray<ushort> LightMap; // The center chunk light buffer (gather source + readback target)
        public NativeQueue<LightQueueNode> SunLightQueue;
        public NativeQueue<LightQueueNode> BlockLightQueue;
        public NativeQueue<Vector2Int> SunLightRecalcQueue;
        public NativeList<LightModification> Mods;
        public NativeArray<bool> IsStable;

        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            // --- Input data ---
            Input.Dispose();

            // --- LI-1 padded volumes ---
            if (PaddedVoxels.IsCreated) PaddedVoxels.Dispose();
            if (PaddedLight.IsCreated) PaddedLight.Dispose();

            // --- Output data ---
            if (Map.IsCreated) Map.Dispose();
            if (LightMap.IsCreated) LightMap.Dispose();
            if (SunLightQueue.IsCreated) SunLightQueue.Dispose();
            if (BlockLightQueue.IsCreated) BlockLightQueue.Dispose();
            if (SunLightRecalcQueue.IsCreated) SunLightRecalcQueue.Dispose();
            if (Mods.IsCreated) Mods.Dispose();
            if (IsStable.IsCreated) IsStable.Dispose();
        }
    }
}
