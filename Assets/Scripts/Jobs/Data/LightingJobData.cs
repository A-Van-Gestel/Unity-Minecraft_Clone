using Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs.Data
{
    public struct LightingJobData
    {
        public JobHandle Handle;

        // --- Output data ---
        public NativeArray<uint> Map; // The writable map for the center chunk
        public NativeQueue<LightQueueNode> SunLightQueue;
        public NativeQueue<LightQueueNode> BlockLightQueue;
        public NativeQueue<Vector2Int> SunLightRecalcQueue;
        public NativeList<LightModification> Mods;
        public NativeArray<bool> IsStable;

        // A helper to dispose all the containers at once
        public void Dispose()
        {
            Map.Dispose();
            SunLightQueue.Dispose();
            BlockLightQueue.Dispose();
            SunLightRecalcQueue.Dispose();
            Mods.Dispose();
            IsStable.Dispose();
        }
    }
}