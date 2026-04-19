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
        public NativeArray<ushort> HeightMap;
        public NativeQueue<VoxelMod> Mods;

        /// <summary>
        /// Structure spawn markers emitted by the generation job's per-entry grid passes.
        /// Consumed on the main thread by <see cref="Jobs.Generators.IChunkGenerator.ExpandStructure"/>.
        /// </summary>
        public NativeQueue<StructureSpawnMarker> StructureSpawns;

        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            if (Map.IsCreated) Map.Dispose();
            if (HeightMap.IsCreated) HeightMap.Dispose();
            if (Mods.IsCreated) Mods.Dispose();
            if (StructureSpawns.IsCreated) StructureSpawns.Dispose();
        }
    }
}