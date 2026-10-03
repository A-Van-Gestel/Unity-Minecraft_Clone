namespace Diagnostics
{
    /// <summary>
    /// Every timed region the performance monitor attributes frame time to; values index fixed per-slot arrays.
    /// The first ten are the <c>World.Update</c> phases and are fixed in value and name, because
    /// <c>Benchmarks.WorldFrameProfiler.Phase</c> is cast to them one-to-one; new slots are only ever appended.
    /// </summary>
    public enum PerfSlot : byte
    {
        /// <summary>The behavior tick (grass/fluid <c>Chunk.TickUpdate</c>).</summary>
        Tick = 0,

        /// <summary>The voxel-modification drain (<c>World.ApplyModifications</c>).</summary>
        Apply = 1,

        /// <summary>The unbudgeted lighting merge: completing lighting jobs and merging their light maps.</summary>
        LightMerge = 2,

        /// <summary>The unbudgeted drain of lighting flags staged by background deserialization.</summary>
        LightStagingDrain = 3,

        /// <summary>The unbudgeted ~1 Hz fail-safe walk of every resident chunk.</summary>
        LightFailSafeScan = 4,

        /// <summary>The budgeted lighting ready-set scan.</summary>
        LightSchedule = 5,

        /// <summary>The budgeted completed-mesh-job pass (buffer upload + load animation).</summary>
        MeshProcess = 6,

        /// <summary>The budgeted mesh-build-queue drain.</summary>
        MeshSchedule = 7,

        /// <summary>The budgeted completed-generation-job pass.</summary>
        GenerationProcess = 8,

        /// <summary>The skylight-queue pairing probe; compiled into instrumented builds only.</summary>
        LightQueueProbe = 9,

        /// <summary>Number of slots; not a slot.</summary>
        Count,
    }
}
