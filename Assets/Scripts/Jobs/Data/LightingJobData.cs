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
        public NativeArray<byte> Heightmap;
        public NativeArray<uint> NeighborN, NeighborE, NeighborS, NeighborW;
        public NativeArray<uint> NeighborNE, NeighborSE, NeighborSW, NeighborNW;

        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            if (Heightmap.IsCreated) Heightmap.Dispose();
            if (NeighborN.IsCreated) NeighborN.Dispose();
            if (NeighborE.IsCreated) NeighborE.Dispose();
            if (NeighborS.IsCreated) NeighborS.Dispose();
            if (NeighborW.IsCreated) NeighborW.Dispose();
            if (NeighborNE.IsCreated) NeighborNE.Dispose();
            if (NeighborSE.IsCreated) NeighborSE.Dispose();
            if (NeighborSW.IsCreated) NeighborSW.Dispose();
            if (NeighborNW.IsCreated) NeighborNW.Dispose();
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

        // --- Input data ---
        public LightingJobInputData Input;

        // --- Output data ---
        public NativeArray<uint> Map; // The writable map for the center chunk
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
            if (SunLightQueue.IsCreated) SunLightQueue.Dispose();
            if (BlockLightQueue.IsCreated) BlockLightQueue.Dispose();
            if (SunLightRecalcQueue.IsCreated) SunLightRecalcQueue.Dispose();
            if (Mods.IsCreated) Mods.Dispose();
            if (IsStable.IsCreated) IsStable.Dispose();
        }
    }
}