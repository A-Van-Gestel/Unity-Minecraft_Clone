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

        // --- Output data ---
        public NativeArray<uint> Map; // The writable map for the center chunk
        public NativeArray<ushort> LightMap; // The writable light map for the center chunk
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
