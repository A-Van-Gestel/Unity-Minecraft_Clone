using Data;
using Unity.Collections;
using Unity.Jobs;

namespace Jobs.Data
{
    public struct GenerationJobData
    {
        public JobHandle Handle;

        // --- Output data ---
        public NativeArray<uint> Map;
        public NativeArray<byte> HeightMap;
        public NativeQueue<VoxelMod> Mods;

        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            Map.Dispose();
            HeightMap.Dispose();
            Mods.Dispose();
        }
    }
}