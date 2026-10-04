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

        /// <summary>Number of counters; not a counter.</summary>
        Count,
    }
}
