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

        /// <summary>
        /// Optional per-worm telemetry data. Only allocated when <see cref="Jobs.Generators.StandardChunkGenerator.EnableTelemetry"/> is true.
        /// </summary>
        public NativeList<WormTelemetryEntry> WormTelemetry;

        /// <summary>
        /// Flat chunk indices (<see cref="Helpers.ChunkMath.GetFlattenedIndexInChunk"/> convention) of
        /// voxels with active behavior, emitted by <see cref="Jobs.ActiveVoxelScanJob"/> for the
        /// generation path. Default (not created) for generators that do not run the scan pass
        /// (e.g. the legacy generator), in which case the caller falls back to the bitmask scan.
        /// </summary>
        public NativeList<int> ActiveVoxels;

        /// A helper to dispose all the containers at once
        public void Dispose()
        {
            if (Map.IsCreated) Map.Dispose();
            if (HeightMap.IsCreated) HeightMap.Dispose();
            if (Mods.IsCreated) Mods.Dispose();
            if (StructureSpawns.IsCreated) StructureSpawns.Dispose();
            if (WormTelemetry.IsCreated) WormTelemetry.Dispose();
            if (ActiveVoxels.IsCreated) ActiveVoxels.Dispose();
        }
    }
}