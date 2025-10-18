using Data;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Jobs.Data
{
    public struct LightingJobData
    {
        public JobHandle handle;
        public NativeArray<uint> map; // The writable map for the center chunk
        public NativeQueue<LightQueueNode> sunLightQueue;
        public NativeQueue<LightQueueNode> blockLightQueue;
        public NativeQueue<Vector2Int> sunLightRecalcQueue;
        public NativeList<LightModification> mods;
        public NativeArray<bool> isStable;

        // A helper to dispose all the containers at once
        public void Dispose()
        {
            map.Dispose();
            sunLightQueue.Dispose();
            blockLightQueue.Dispose();
            sunLightRecalcQueue.Dispose();
            mods.Dispose();
            isStable.Dispose();
        }
    }
}