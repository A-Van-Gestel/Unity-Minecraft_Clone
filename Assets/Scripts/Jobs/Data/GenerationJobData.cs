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

        /// <summary>
        /// True when <see cref="ActiveVoxels"/> was rented from <c>WorldJobManager</c>'s
        /// <see cref="Helpers.ActiveVoxelListPool"/> (the production generation path) rather than freshly
        /// allocated (editor / preview / benchmark paths, which pass no pool). Routes the release: a pooled
        /// list's lifecycle belongs to the pool — it is handed back via <c>ActiveVoxelListPool.Return</c> at
        /// the single terminal release point (<c>WorldJobManager.ReleaseGenerationJobData</c>) — so
        /// <see cref="Dispose"/> must NOT free it. A non-pooled list is freed by <see cref="Dispose"/> as usual.
        /// </summary>
        public bool ActiveVoxelsFromPool;

        /// <summary>
        /// Releases every native container owned by this job in one call. This is the <b>sole</b> release path
        /// for a <see cref="GenerationJobData"/>: any code that removes an entry from
        /// <c>WorldJobManager.GenerationJobs</c> — the completion drain, shutdown, or a future early eviction on
        /// view-distance change — MUST call this first (and after <c>Handle.Complete()</c>), or the per-chunk
        /// Persistent buffers (<see cref="Map"/>, <see cref="HeightMap"/>, <see cref="ActiveVoxels"/>, …) leak
        /// per abandoned chunk.
        /// <para><b>Exception:</b> a pool-owned <see cref="ActiveVoxels"/>
        /// (<see cref="ActiveVoxelsFromPool"/> == true) is NOT freed here — its lifecycle moved to
        /// <see cref="Helpers.ActiveVoxelListPool"/>, which is the sole release path for it. Callers that
        /// evict a job holding a pooled list must return it to the pool (not rely on this Dispose).</para>
        /// </summary>
        public void Dispose()
        {
            if (Map.IsCreated) Map.Dispose();
            if (HeightMap.IsCreated) HeightMap.Dispose();
            if (Mods.IsCreated) Mods.Dispose();
            if (StructureSpawns.IsCreated) StructureSpawns.Dispose();
            if (WormTelemetry.IsCreated) WormTelemetry.Dispose();
            if (ActiveVoxels.IsCreated && !ActiveVoxelsFromPool) ActiveVoxels.Dispose();
        }
    }
}