namespace Diagnostics
{
    /// <summary>
    /// Every per-frame counter the performance monitor records beside its slot times; values index fixed per-counter
    /// arrays, and new counters are only ever appended within their group.
    /// <para>
    /// Counters before <see cref="PerfStore.FirstPerFrameCount"/> are <b>gauges</b> — a level such as a queue depth,
    /// set with <see cref="PerfStore.SetGauge"/> and held until set again. The rest are <b>per-frame counts</b>,
    /// derived with <see cref="PerfStore.SampleTotal"/> from a running total and zeroed by every commit. Peaks are
    /// read from window statistics over a gauge, not kept as counters of their own.
    /// </para>
    /// </summary>
    public enum PerfCounter : byte
    {
        /// <summary>Generation requests waiting for admission.</summary>
        GenerationQueue,

        /// <summary>Generation jobs in flight.</summary>
        GenerationInFlight,

        /// <summary>Chunks in the lighting ready set.</summary>
        LightReady,

        /// <summary>Chunks parked in the lighting waiting set.</summary>
        LightWaiting,

        /// <summary>Lighting jobs in flight.</summary>
        LightInFlight,

        /// <summary>Chunks waiting in the mesh build queue.</summary>
        MeshQueue,

        /// <summary>Mesh jobs in flight.</summary>
        MeshInFlight,

        /// <summary>Voxel modifications waiting to be applied.</summary>
        ModificationQueue,

        /// <summary>Chunk data resident in the world (loaded, generating or placeholder).</summary>
        ResidentChunks,

        /// <summary>Chunks inside the view distance.</summary>
        ActiveChunks,

        /// <summary>Chunk sections handed out by the section pool.</summary>
        ActiveSections,

        /// <summary>Job buffers idle in the chunk job array pool, ready to rent (rented buffers are not tracked: some are disposed by their owner).</summary>
        JobArraysPooled,

        /// <summary>Mesh outputs idle in the mesh output pool, ready to rent.</summary>
        MeshOutputsPooled,

        /// <summary>Background chunk loads and saves submitted and not yet resumed on their caller.</summary>
        IoInFlight,

        /// <summary>Sections the section pool had to create this frame (a pool miss).</summary>
        SectionPoolMisses,

        /// <summary>Chunk data the data pool had to create this frame.</summary>
        DataPoolMisses,

        /// <summary>Job buffers the chunk job array pool had to allocate this frame.</summary>
        JobArrayMisses,

        /// <summary>Mesh outputs the mesh output pool had to allocate this frame.</summary>
        MeshOutputMisses,

        /// <summary>Serialization buffers the save-buffer pool had to allocate this frame.</summary>
        SaveBufferMisses,

        /// <summary>Timed generation jobs whose result was consumed this frame.</summary>
        GenerationCompleted,

        /// <summary>Summed latency of those generation jobs — schedule to result consumed — in microseconds.</summary>
        GenerationLatencyUs,

        /// <summary>Summed worker execute time of those generation jobs, in microseconds.</summary>
        GenerationBusyUs,

        /// <summary>Timed lighting jobs whose result was consumed this frame.</summary>
        LightCompleted,

        /// <summary>Summed latency of those lighting jobs, in microseconds.</summary>
        LightLatencyUs,

        /// <summary>Summed worker execute time of those lighting jobs, in microseconds.</summary>
        LightBusyUs,

        /// <summary>Timed mesh jobs whose result was consumed this frame.</summary>
        MeshCompleted,

        /// <summary>Summed latency of those mesh jobs, in microseconds.</summary>
        MeshLatencyUs,

        /// <summary>Summed worker execute time of those mesh jobs, in microseconds.</summary>
        MeshBusyUs,

        /// <summary>Jobs scheduled untimed this frame because every busy-time record was in use.</summary>
        UntimedJobs,

        /// <summary>Fluid tick jobs whose result was drained this frame.</summary>
        FluidCompleted,

        /// <summary>Summed latency of those fluid jobs — schedule to drained — in microseconds.</summary>
        FluidLatencyUs,

        /// <summary>Summed worker execute time of those fluid jobs, in microseconds.</summary>
        FluidBusyUs,

        /// <summary>Fluid sound scans whose result was read this frame.</summary>
        FluidSoundScanCompleted,

        /// <summary>Summed latency of those scans, in microseconds.</summary>
        FluidSoundScanLatencyUs,

        /// <summary>Summed worker execute time of those scans, in microseconds.</summary>
        FluidSoundScanBusyUs,

        /// <summary>Chunk loads that found the chunk on disk this frame.</summary>
        DiskLoadHits,

        /// <summary>Chunk loads that found nothing on disk this frame — the chunk is generated instead.</summary>
        DiskLoadMisses,

        /// <summary>Time spent reading region files for loads, hits and misses alike, in microseconds.</summary>
        DiskReadUs,

        /// <summary>Time spent decompressing and deserializing loaded chunks, in microseconds.</summary>
        DeserializeUs,

        /// <summary>Compressed payload bytes read this frame.</summary>
        DiskLoadBytes,

        /// <summary>Chunk payloads written to region files this frame, from every save path.</summary>
        DiskSaves,

        /// <summary>Time spent serializing and compressing chunks for saving, in microseconds.</summary>
        SerializeUs,

        /// <summary>Time spent writing payloads into region files, in microseconds.</summary>
        DiskWriteUs,

        /// <summary>Compressed payload bytes written this frame.</summary>
        DiskSaveBytes,

        /// <summary>The same payloads' bytes before compression; with <see cref="DiskSaveBytes"/>, the compression ratio.</summary>
        DiskSaveRawBytes,

        /// <summary>Time background loads and saves waited for a ThreadPool thread, in microseconds.</summary>
        IoQueueWaitUs,

        /// <summary>Background loads and saves that started on a ThreadPool thread this frame — the operations <see cref="IoQueueWaitUs"/> covers.</summary>
        IoBackgroundOps,

        /// <summary>Behavior-tick time spent building the active-chunk list, in microseconds.</summary>
        TickListUs,

        /// <summary>Behavior-tick time spent preparing fluid chunks — partitions, voxel-map copies, first-use allocation — in microseconds.</summary>
        TickPrepareUs,

        /// <summary>Behavior-tick time spent renting tickers and scheduling the fluid jobs, in microseconds.</summary>
        TickScheduleUs,

        /// <summary>Behavior-tick time spent waiting for the fluid jobs to complete, in microseconds.</summary>
        TickWaitUs,

        /// <summary>Behavior-tick time spent in the managed grass tick, in microseconds.</summary>
        TickGrassUs,

        /// <summary>Behavior-tick time spent replaying the fluid jobs' results, in microseconds.</summary>
        TickFluidReplayUs,

        /// <summary>Fluid chunks the behavior tick prepared a job for.</summary>
        TickFluidChunks,

        /// <summary>Kilobytes of voxel maps copied for those fluid jobs.</summary>
        TickSnapshotKb,

        /// <summary>Active grass voxels the behavior tick ticked.</summary>
        TickGrassVoxels,

        /// <summary>Fluid tickers the ticker pool had to create; each allocates its native scratch on its first prepare.</summary>
        FluidTickerPoolMisses,

        /// <summary>Number of counters; not a counter.</summary>
        Count,
    }
}
